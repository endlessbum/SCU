using SCU.Common;
using SCU.Models.Drivers;

namespace SCU.Infrastructure.Windows.Drivers;

// Обновления драйверов через Windows Update API (COM Microsoft.Update.Session) —
// тот же канал, которым Windows ставит драйверы сама: обновления подобраны по
// DeviceID оборудования и подписаны Microsoft. COM используется динамически
// (Type.GetTypeFromProgID, без COMReference в csproj).
// Поиск и установка — блокирующие COM-вызовы: выполняются в Task.Run, отменяются
// между стадиями (сам вызов COM отменить нельзя), поиск ограничен таймаутом.
public sealed class WindowsUpdateDriverService
{
    // Онлайн-поиск на серверах WU может занимать минуты; дольше — считаем зависанием.
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(10);

    // Результаты поиска живут до следующего поиска: установщик выбирает пакеты
    // по ключам UpdateID:Revision из последнего поиска.
    private readonly Dictionary<string, object> _foundUpdates = [];

    private readonly Logger _logger;

    public WindowsUpdateDriverService(Logger logger)
    {
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<DriverUpdateInfo>>> SearchAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            progress?.Report(L.T("Подключение к службе обновлений…"));
            var updates = await Task.Run(() => SearchCore(progress), ct).ConfigureAwait(false);
            return Result<IReadOnlyList<DriverUpdateInfo>>.Success(updates);
        }
        catch (OperationCanceledException)
        {
            return Result<IReadOnlyList<DriverUpdateInfo>>.Failure("Отменено", -1);
        }
        catch (TimeoutException)
        {
            _logger.Error("DRV | WU search timeout | " + SearchTimeout);
            return Result<IReadOnlyList<DriverUpdateInfo>>.Failure(
                L.T("Превышено время ожидания поиска обновлений ({0} мин) — серверы Windows Update недоступны.", (int)SearchTimeout.TotalMinutes));
        }
        catch (Exception exception)
        {
            _logger.Error("DRV | WU search failed | " + exception.Message);
            return Result<IReadOnlyList<DriverUpdateInfo>>.Failure(
                L.T("Не удалось выполнить поиск обновлений: {0}", DescribeWuError(exception)));
        }
    }

    private IReadOnlyList<DriverUpdateInfo> SearchCore(IProgress<string>? progress)
    {
        var session = CreateSession();
        dynamic searcher = session.CreateUpdateSearcher();
        // Online=true: принудительный онлайн-скан, иначе могли бы отдать кэш.
        searcher.Online = true;

        progress?.Report(L.T("Поиск обновлений драйверов на серверах Windows Update…"));
        var search = Task.Run(() => (object)searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Driver'"));
        var finished = Task.WhenAny(search, Task.Delay(SearchTimeout)).GetAwaiter().GetResult();
        if (finished != search)
        {
            throw new TimeoutException();
        }

        dynamic searchResult = search.GetAwaiter().GetResult();
        dynamic updates = searchResult.Updates;
        var list = new List<DriverUpdateInfo>();
        _foundUpdates.Clear();
        for (var i = 0; i < updates.Count; i++)
        {
            dynamic update = updates[i];
            var key = IdentityKey(update);
            _foundUpdates[key] = update;
            list.Add(new DriverUpdateInfo(
                key,
                (string)(update.Title ?? string.Empty),
                (string)(update.DriverProvider ?? string.Empty),
                (string)(update.DriverVerVersion ?? string.Empty),
                TryGetDate(update.DriverVerDate),
                (long)update.MaxDownloadSize));
        }

        _logger.Info($"DRV | WU search ok | {list.Count} driver updates");
        return list;
    }

    public async Task<Result<DriverInstallSummary>> InstallAsync(
        IReadOnlyList<string> keys,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            progress?.Report(L.T("Подготовка к установке…"));
            var summary = await Task.Run(() => InstallCore(keys, progress, ct), ct).ConfigureAwait(false);
            return Result<DriverInstallSummary>.Success(summary);
        }
        catch (OperationCanceledException)
        {
            return Result<DriverInstallSummary>.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            _logger.Error("DRV | WU install failed | " + exception.Message);
            return Result<DriverInstallSummary>.Failure(
                L.T("Не удалось установить драйвер: {0}", DescribeWuError(exception)));
        }
    }

    private DriverInstallSummary InstallCore(IReadOnlyList<string> keys, IProgress<string>? progress, CancellationToken ct)
    {
        var selected = keys
            .Select(key => _foundUpdates.TryGetValue(key, out var update) ? update : null)
            .Where(update => update is not null)
            .Cast<object>()
            .ToList();
        if (selected.Count == 0)
        {
            throw new InvalidOperationException(L.T("Обновления не найдены — выполните поиск заново."));
        }

        var session = CreateSession();

        // Загрузка: один вызов на весь набор; отмена — только между стадиями.
        ct.ThrowIfCancellationRequested();
        progress?.Report(L.T("Загрузка пакетов драйверов…"));
        dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        foreach (var update in selected)
        {
            collection.Add(update);
        }

        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = collection;
        var downloadResult = downloader.Download();
        var downloadCode = (int)downloadResult.ResultCode;
        _logger.Info($"DRV | WU download | rc={downloadCode} | hr=0x{downloadResult.HResult:X8}");
        if (downloadCode is 4 or 5) // orcFailed, orcAborted
        {
            throw new InvalidOperationException(L.T("Загрузка пакетов не завершилась (код {0}).", downloadCode));
        }

        // Установка.
        ct.ThrowIfCancellationRequested();
        progress?.Report(L.T("Установка драйверов…"));
        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = collection;
        var installResult = installer.Install();
        var installCode = (int)installResult.ResultCode;
        _logger.Info($"DRV | WU install | rc={installCode} | hr=0x{installResult.HResult:X8} | reboot={installResult.RebootRequired}");

        // Постраничный разбор: какие пакеты встали, какие нет.
        var succeeded = 0;
        var succeededKeys = new List<string>();
        var failures = new List<string>();
        for (var i = 0; i < collection.Count; i++)
        {
            dynamic updateResult = installResult.GetUpdateResult(i);
            var resultCode = (int)updateResult.ResultCode;
            dynamic update = collection[i];
            var title = (string)(update.Title ?? L.T("Без названия"));
            if (resultCode is 2 or 3) // orcSucceeded, orcSucceededWithErrors
            {
                succeeded++;
                succeededKeys.Add(IdentityKey(update));
            }
            else
            {
                var hr = (int)updateResult.HResult;
                failures.Add($"{title} (0x{hr:X8})");
            }
        }

        return new DriverInstallSummary(
            failures.Count,
            failures,
            succeededKeys,
            (bool)installResult.RebootRequired);
    }

    private static dynamic CreateSession()
    {
        var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new InvalidOperationException(
                L.T("COM-компонент Windows Update (Microsoft.Update.Session) недоступен на этой системе."));
        return Activator.CreateInstance(sessionType)!;
    }

    private static string IdentityKey(dynamic update) =>
        $"{update.Identity.UpdateID}:{update.Identity.RevisionNumber}";

    private static DateTime? TryGetDate(object? raw) =>
        raw is DateTime date ? date : null;

    // Типовые сбои WU API — в человеческий текст; остальное — код.
    private static string DescribeWuError(Exception exception)
    {
        var hr = exception.HResult;
        return hr switch
        {
            unchecked((int)0x80240020) => L.T("Установка требует прав администратора."),
            unchecked((int)0x80072EE2) => L.T("Серверы Windows Update недоступны (таймаут сети)."),
            unchecked((int)0x8024402C) => L.T("Не удалось связаться с серверами Windows Update — проверьте сеть или настройки прокси."),
            _ => exception.Message,
        };
    }
}
