using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SCU.Common;

namespace SCU.Infrastructure.Windows.Power;

// Таймаут отключения диска в простое (SUB_DISK/DISKIDLE активной схемы электропитания).
// «Отключить диск» (тумблер вкл) = DISKIDLE 0 — диск не переводится в спящий режим;
// тумблер выкл = 20 минут (1200 с) — стандартное значение Windows из powercfg.cpl.
// Значение задаётся для AC и DC сразу (иначе состояние «смешанное»), читается AC;
// применение — /setactive SCHEME_CURRENT (пересборка активной схемы без её смены).
public sealed partial class PowerService
{
    public const int DiskIdleDisabledSeconds = 0;
    public const int DiskIdleDefaultSeconds = 1200; // 20 минут

    private const string SubDiskGuid = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    private const string DiskIdleGuid = "6738e2c4-e8a5-4a42-b16a-e040e769756e";

    // Только чтение: AC-таймаут диска активной схемы, в секундах. -1 — не распознано.
    public async Task<Result<int>> GetDiskIdleTimeoutAsync(CancellationToken ct = default)
    {
        // Аудит, п. 4: значение читается напрямую из активной схемы через
        // PowerReadACValueIndex. Прежний разбор powercfg брал первое hex-значение
        // после GUID параметра, а в реальном выводе перед Current AC идут
        // Minimum/Maximum/Increment — парсер возвращал 0 («отключение запрещено»),
        // и тумблер «вернуть стандартные 20 минут» не работал.
        if (TryReadDiskIdleAcIndex(out var seconds))
        {
            return Result<int>.Success(seconds);
        }

        // Резерв: разбор вывода powercfg. Подписи локализованы, но структура блока
        // стабильна — текущие значения AC и DC идут последними.
        _logger.Warn("POWER | PowerReadACValueIndex unavailable | fallback to powercfg output");

        var query = await _runner
            .RunAsync("powercfg", ["/q", "SCHEME_CURRENT", SubDiskGuid, DiskIdleGuid], null, ct)
            .ConfigureAwait(false);
        if (!query.IsSuccess)
        {
            return Result<int>.Failure(
                "Не удалось прочитать таймаут диска (powercfg): " + query.Message, query.Code);
        }

        seconds = ParseDiskIdleAcIndex(query.Value ?? string.Empty);
        return seconds >= 0
            ? Result<int>.Success(seconds)
            : Result<int>.Failure("Не удалось распознать значение DISKIDLE в выводе powercfg.");
    }

    // Блок параметра начинается со строки с GUID'ом DISKIDLE. В нём сначала идут
    // возможные значения (Minimum/Maximum/Приращение), затем — текущие AC и DC
    // (последние два hex-значения блока). Подписи строк локализованы, hex — нет,
    // поэтому опираемся на порядок, а не на текст («от сети»/«AC»).
    internal static int ParseDiskIdleAcIndex(string output)
    {
        var lines = output.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(DiskIdleGuid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = new List<int>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                // Следующий параметр схемы (строка с GUID) — конец текущего блока.
                if (GuidPattern.IsMatch(lines[j]))
                {
                    break;
                }

                foreach (Match match in Regex.Matches(lines[j], @"0x[0-9a-fA-F]{1,8}"))
                {
                    if (int.TryParse(
                            match.Value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    {
                        values.Add(value);
                    }
                }
            }

            // Меньше двух значений — текущие AC/DC не найдены: не угадываем.
            return values.Count >= 2 ? values[^2] : -1;
        }

        return -1;
    }

    // GUID параметра/подгруппы в тексте powercfg (в отличие от псевдонима DISKIDLE).
    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    // Чтение AC-индекса активной схемы через powrprof: не зависит от локализации
    // и от формата текстового вывода powercfg. false — API недоступно/ошибка.
    // DC-значение DISKIDLE активной схемы: показывается рядом с AC, чтобы
    // пользователь видел фактические оба значения (дизайн, п. 11).
    public Task<Result<int>> GetDiskIdleDcTimeoutAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(TryReadDiskIdleIndex(dc: true, out var seconds)
            ? Result<int>.Success(seconds)
            : Result<int>.Failure("Не удалось прочитать DC-таймаут диска."));
    }

    private static bool TryReadDiskIdleAcIndex(out int seconds) =>
        TryReadDiskIdleIndex(dc: false, out seconds);

    // Чтение индекса DISKIDLE активной схемы (AC или DC) через powrprof:
    // локализация и формат вывода powercfg не важны.
    private static bool TryReadDiskIdleIndex(bool dc, out int seconds)
    {
        seconds = -1;
        IntPtr schemePointer = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out schemePointer) != 0 || schemePointer == IntPtr.Zero)
            {
                return false;
            }

            var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
            var subGroup = new Guid(SubDiskGuid);
            var setting = new Guid(DiskIdleGuid);
            uint value;
            if (dc)
            {
                if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out value) != 0)
                {
                    return false;
                }
            }
            else
            {
                if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out value) != 0)
                {
                    return false;
                }
            }

            // Секунды хранятся в UINT; больше int.MaxValue для этого параметра не бывает.
            if (value > int.MaxValue)
            {
                return false;
            }

            seconds = (int)value;
            return true;
        }
        catch (Exception)
        {
            // powrprof недоступен — вызывающий уходит на резервный разбор powercfg.
            return false;
        }
        finally
        {
            if (schemePointer != IntPtr.Zero)
            {
                LocalFree(schemePointer);
            }
        }
    }

    public async Task<Result> SetDiskIdleDisabledAsync(bool disable, CancellationToken ct = default)
    {
        var seconds = disable ? DiskIdleDisabledSeconds : DiskIdleDefaultSeconds;
        var secondsText = seconds.ToString(CultureInfo.InvariantCulture);

        var ac = await _runner
            .RunAsync("powercfg", ["/setacvalueindex", "SCHEME_CURRENT", SubDiskGuid, DiskIdleGuid, secondsText], null, ct)
            .ConfigureAwait(false);
        if (!ac.IsSuccess)
        {
            return Result.Failure("Не удалось задать AC-таймаут диска: " + ac.Message, ac.Code);
        }

        var dc = await _runner
            .RunAsync("powercfg", ["/setdcvalueindex", "SCHEME_CURRENT", SubDiskGuid, DiskIdleGuid, secondsText], null, ct)
            .ConfigureAwait(false);
        if (!dc.IsSuccess)
        {
            return Result.Failure("Не удалось задать DC-таймаут диска: " + dc.Message, dc.Code);
        }

        var apply = await _runner
            .RunAsync("powercfg", ["/setactive", "SCHEME_CURRENT"], null, ct)
            .ConfigureAwait(false);
        if (!apply.IsSuccess)
        {
            return Result.Failure("Не удалось применить схему электропитания: " + apply.Message, apply.Code);
        }

        _logger.Info($"POWER | disk idle timeout={seconds}s | applied");

        // Подтверждение чтением: значение обязано совпасть с ожидаемым.
        var readback = await GetDiskIdleTimeoutAsync(ct).ConfigureAwait(false);
        if (!readback.IsSuccess || readback.Value != seconds)
        {
            return Result.Failure(
                "Таймаут задан, но повторное чтение не подтвердило значение: "
                + (readback.IsSuccess ? readback.Value.ToString(CultureInfo.InvariantCulture) : readback.Message));
        }

        return Result.Success(disable
            ? "Отключение диска в режиме простоя запрещено."
            : "Таймаут отключения диска: 20 минут.");
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupGuid,
        ref Guid powerSettingGuid,
        out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupGuid,
        ref Guid powerSettingGuid,
        out uint dcValueIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
