using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Раздел 9 «Сеть» — аналог :NetMenu из Utilities.bat: TCP Global (Auto-Tuning, ECN),
// MTU по интерфейсам, QoS override, NetBIOS over TCP/IP, игровой профиль и общий сброс.
// Изменяющие операции сервиса используют backup → change → verify, а профиль NIC выполняет change → restart → verify, сохраняя неподдерживаемые свойства без изменений.
public partial class NetworkViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private static readonly string[] AutoTuningOptions = ["normal", "disabled", "highlyrestricted", "restricted", "experimental"];
    private static readonly string[] EcnOptions = ["enabled", "disabled", "default"];

    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly NetworkService _networkService;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyAutoTuningCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyUniversalAdapterProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreAdapterProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyEcnCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetMtuCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreMtuCommand))]
    [NotifyCanExecuteChangedFor(nameof(QosRemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(QosZeroCommand))]
    [NotifyCanExecuteChangedFor(nameof(QosTwentyCommand))]
    [NotifyCanExecuteChangedFor(nameof(QosRestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(NetBiosDisableCommand))]
    [NotifyCanExecuteChangedFor(nameof(NetBiosDhcpCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlushNetBiosCacheCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlushDnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveMinCifraCertCommand))]
    [NotifyCanExecuteChangedFor(nameof(GamingProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RollbackGamingProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isAdmin;

    [ObservableProperty]
    private string _statusText = L.T("Загрузка состояния…");

    // П.5: выбор в списках выставляется по фактическим значениям при загрузке
    // (текстовые информеры удалены). null — состояние не прочитано: в списке
    // показывается плейсхолдер, а не дефолт «normal»/«default» как факт.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoTuningValues), nameof(EcnValues))]
    private string? _selectedAutoTuning = "normal";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoTuningValues), nameof(EcnValues))]
    private string? _selectedEcn = "default";

    [ObservableProperty]
    private InterfaceRow? _selectedInterface;

    // Поле ввода произвольного MTU (не информер): пусто — кнопка «MTU произвольный» берёт
    // значение отсюда; пресеты 1500/1472/1400 передают значение через CommandParameter.
    [ObservableProperty]
    private string _mtuText = string.Empty;

    // Сырое значение QoS override — источник и для ActiveQosKey, и для признака игрового профиля.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveQosKey))]
    private int? _qosOverrideValue;

    // Подсветка QoS-кнопок: "0"/"20" — совпал оверрайд, "none" — политики нет
    // (подсвечивается «Удалить override»), иное значение — ничего не подсвечено.
    public string? ActiveQosKey => QosOverrideValue switch
    {
        null => "none",
        0 => "0",
        20 => "20",
        _ => null
    };

    // Игровой профиль активен (Auto-Tuning=disabled, ECN=disabled, QoS=0) — видна «Откатить».
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RollbackGamingProfileCommand))]
    private bool _isGamingActive;

    // Подсветка NetBIOS-кнопок: все интерфейсы в одном режиме — он и подсвечен.
    [ObservableProperty]
    private string? _activeNetBiosKey;

    // В бэкапе профиля адаптера есть что откатывать — видна кнопка «Откатить».
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreAdapterProfileCommand))]
    private bool _isAdapterProfileRestoreAvailable;

    // Сертификат Минцифры найден в хранилищах сертификатов — кнопка «Удалить» активна.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveMinCifraCertCommand))]
    private bool _isMinCifraCertInstalled;

    public NetworkViewModel(Logger logger, IConfirmDialogService dialogs, HistoryStore history)
    {
        _logger = logger;
        _dialogs = dialogs;
        _history = history;
        IsAdmin = Elevation.IsAdmin();
        _networkService = new NetworkService(logger, new LongProcessRunner(logger));
    }

    // Плейсхолдер вместо несчитанного значения: пустой ComboBox выглядел бы
    // как «не задано», плейсхолдер честно говорит, что состояние не прочитано.
    private static string UnknownTcpOption => L.T("Не удалось прочитать");

    public IReadOnlyList<string> AutoTuningValues =>
        SelectedAutoTuning is null ? [UnknownTcpOption, .. AutoTuningOptions] : AutoTuningOptions;

    public IReadOnlyList<string> EcnValues =>
        SelectedEcn is null ? [UnknownTcpOption, .. EcnOptions] : EcnOptions;

    public ObservableCollection<InterfaceRow> Interfaces { get; } = [];

    public string ReadOnlyHint =>
        IsAdmin ? string.Empty : L.T("Нужны права администратора — изменение сетевых параметров недоступно.");

    private bool CanModify() => !IsBusy && IsAdmin;
    private bool CanRefresh() => !IsBusy;
    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление сетевых параметров", async ct =>
        {
            StatusText = L.T("Чтение TCP Global, MTU, QoS и NetBIOS…");
            var problems = new List<string>();
            AddRefreshProblem(problems, await RefreshTcpGlobalAsync(ct).ConfigureAwait(true));
            AddRefreshProblem(problems, await RefreshInterfacesAsync(ct).ConfigureAwait(true));
            await RefreshQosAsync(ct).ConfigureAwait(true);
            await RefreshNetBiosAsync(ct).ConfigureAwait(true);
            IsAdapterProfileRestoreAvailable = NetworkService.HasAdapterProfileBackup();
            IsMinCifraCertInstalled = TrustedCertificateService.IsRussianTrustedInstalled();
            StatusText = problems.Count > 0
                ? L.T("Состояние обновлено не полностью: {0}", string.Join("; ", problems))
                : L.T("Состояние обновлено.");
        }).ConfigureAwait(true);
    }

    private static void AddRefreshProblem(List<string> problems, string? problem)
    {
        if (!string.IsNullOrWhiteSpace(problem))
        {
            problems.Add(problem);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ApplyUniversalAdapterProfileAsync()
    {
        if (!_dialogs.Ask(
                L.T("Профиль сетевого адаптера"),
                L.T("Будет применён универсальный сбалансированный профиль к физическим сетевым адаптерам. Неподдерживаемые свойства будут пропущены.\nИзменения расширенных параметров могут кратковременно прервать сетевое соединение. Продолжить?"),
                L.T("Применить профиль")))
        {
            return;
        }

        await RunExclusiveAsync("универсальный профиль сетевых адаптеров", async ct =>
        {
            StatusText = L.T("Применение универсального профиля к сетевым адаптерам…");
            var progress = new Progress<string>(line =>
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                var parts = line.Split('|', 4);
                StatusText = parts.Length switch
                {
                    >= 4 when parts[0] == "SETTING" && parts[3] == "SET" => L.T("{0}: {1} — применено", LocalizeAdapterProfileLabel(parts[1]), LocalizeAdapterProfileSetting(parts[2])),
                    >= 4 when parts[0] == "SETTING" && parts[3] == "SKIP" => L.T("{0}: {1} — пропущено (нет свойства)", LocalizeAdapterProfileLabel(parts[1]), LocalizeAdapterProfileSetting(parts[2])),
                    >= 4 when parts[0] == "SETTING" && parts[3] == "FAIL" => L.T("{0}: {1} — ошибка", LocalizeAdapterProfileLabel(parts[1]), LocalizeAdapterProfileSetting(parts[2])),
                    2 when parts[0] == "ADAPTER" => L.T("Профиль: {0}", parts[1]),
                    2 when parts[0] == "RESTART" && parts[1] == "BEGIN" => L.T("Перезапуск изменённого сетевого адаптера…"),
                    _ => StatusText
                };
            });

            var result = await _networkService.ApplyUniversalAdapterProfileAsync(progress, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
            }

            StatusText = result.IsSuccess
                ? L.S(result.Message)
                : L.T("Профиль применён не полностью: {0}", L.S(result.Message));
            IsAdapterProfileRestoreAvailable = NetworkService.HasAdapterProfileBackup();
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRestoreAdapterProfile))]
    private async Task RestoreAdapterProfileAsync()
    {
        await RunExclusiveAsync("откат профиля сетевых адаптеров", async ct =>
        {
            StatusText = L.T("Восстановление свойств сетевых адаптеров из бэкапа…");
            var result = await _networkService.RestoreAdapterProfileAsync(ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            IsAdapterProfileRestoreAvailable = NetworkService.HasAdapterProfileBackup();
        }).ConfigureAwait(true);
    }

    private bool CanRestoreAdapterProfile() => CanModify() && IsAdapterProfileRestoreAvailable;

    private static string LocalizeAdapterProfileLabel(string value) => value switch
    {
        "Checksum Offload" => L.T("Разгрузка контрольных сумм"),
        "LSO v2" => L.T("LSO v2"),
        "Interrupt Moderation" => L.T("Модерация прерываний"),
        "Flow Control" => L.T("Управление потоком"),
        "Jumbo Packet" => L.T("Jumbo Packet"),
        "Energy Efficient Ethernet" => L.T("Energy Efficient Ethernet"),
        _ => value
    };

    private static string LocalizeAdapterProfileSetting(string value) => value switch
    {
        "*RSS" => "RSS",
        "*TCPUDPChecksumOffloadIPv4" => "IPv4",
        "*TCPUDPChecksumOffloadIPv6" => "IPv6",
        "*IPChecksumOffloadIPv4" => "IPv4",
        "*TCPChecksumOffloadIPv4" => "TCP IPv4",
        "*TCPChecksumOffloadIPv6" => "TCP IPv6",
        "*UDPChecksumOffloadIPv4" => "UDP IPv4",
        "*UDPChecksumOffloadIPv6" => "UDP IPv6",
        "*LsoV2IPv4" => "IPv4",
        "*LsoV2IPv6" => "IPv6",
        "*InterruptModeration" => "Interrupt Moderation",
        "*FlowControl" => "Flow Control",
        "*JumboPacket" => "Jumbo Packet",
        "*EEE" => "EEE",
        "Interrupt Moderation Rate" => L.T("Частота модерации прерываний"),
        _ => value
    };

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ApplyAutoTuningAsync()
    {
        if (SelectedAutoTuning is null)
        {
            StatusText = L.T("Сначала выберите значение — текущее состояние не прочитано.");
            return;
        }

        await SetTcpGlobalAsync("autotuninglevel", SelectedAutoTuning).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ApplyEcnAsync()
    {
        if (SelectedEcn is null)
        {
            StatusText = L.T("Сначала выберите значение — текущее состояние не прочитано.");
            return;
        }

        await SetTcpGlobalAsync("ecncapability", SelectedEcn).ConfigureAwait(true);
    }

    private async Task SetTcpGlobalAsync(string setting, string value)
    {
        await RunExclusiveAsync("TCP Global", async ct =>
        {
            StatusText = L.T("netsh: {0}={1}…", setting, value);
            var result = await _networkService.SetTcpGlobalAsync(setting, value, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
            if (result.IsSuccess)
            {
                await RefreshTcpGlobalAsync(ct).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task SetMtuAsync(string? preset)
    {
        var alias = SelectedInterface?.Alias;
        if (alias is null)
        {
            StatusText = L.T("Выберите интерфейс в списке.");
            return;
        }

        int mtu;
        if (preset is not null)
        {
            // Пресет приходит из XAML; на чужих данных не падаем, а сообщаем об ошибке.
            if (!int.TryParse(preset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromPreset))
            {
                StatusText = L.T("MTU не распознан — введите число 576..1500 или используйте пресеты.");
                return;
            }

            mtu = fromPreset;
        }
        else if (int.TryParse(MtuText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            mtu = parsed;
        }
        else
        {
            StatusText = L.T("MTU не распознан — введите число 576..1500 или используйте пресеты.");
            return;
        }

        if (mtu is < 576 or > 1500)
        {
            StatusText = L.T("MTU {0} вне диапазона 576..1500.", mtu);
            return;
        }

        await RunExclusiveAsync("MTU", async ct =>
        {
            StatusText = L.T("netsh: MTU {0} для «{1}»…", mtu, alias);
            var result = await _networkService.SetMtuAsync(alias, mtu, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
            if (result.IsSuccess)
            {
                await RefreshInterfacesAsync(ct).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RestoreMtuAsync()
    {
        await RunExclusiveAsync("восстановление MTU", async ct =>
        {
            StatusText = L.T("Восстановление сохранённых MTU…");
            var result = await _networkService.RestoreMtuAsync(ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            await RefreshInterfacesAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task QosRemoveAsync() => SetQosAsync(null);

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task QosZeroAsync() => SetQosAsync(0);

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task QosTwentyAsync() => SetQosAsync(20);

    private async Task SetQosAsync(int? value)
    {
        await RunExclusiveAsync("QoS override", async ct =>
        {
            StatusText = value is null ? L.T("Удаление QoS override…") : L.T("Установка QoS override = {0}%…", value);
            var result = await TaskRunner.RunBlocking(() => _networkService.SetQosOverride(value), ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            if (result.IsSuccess)
            {
                await RefreshQosAsync(ct).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task QosRestoreAsync()
    {
        await RunExclusiveAsync("восстановление QoS", async ct =>
        {
            StatusText = L.T("Восстановление исходного QoS override…");
            var result = await TaskRunner.RunBlocking(_networkService.RestoreQosOverride, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            await RefreshQosAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task NetBiosDisableAsync() => SetNetBiosAsync(2, L.T("Отключить NetBIOS over TCP/IP на всех активных IPv4-интерфейсах?"));

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task NetBiosDhcpAsync() => SetNetBiosAsync(0, L.T("Вернуть NetBIOS over TCP/IP к значению по DHCP (0) на всех активных IPv4-интерфейсах?"));

    private async Task SetNetBiosAsync(int mode, string confirmText)
    {
        if (!_dialogs.Ask(
                L.T("NetBIOS over TCP/IP"),
                L.T(confirmText) + "\n" + L.T("Исходные режимы сохраняются в бэкап."),
                L.T("Применить")))
        {
            return;
        }

        await RunExclusiveAsync("NetBIOS", async ct =>
        {
            StatusText = L.T("WMI SetTcpipNetbios mode={0}…", mode);
            var result = await _networkService.SetNetBiosAsync(mode, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
            if (result.IsSuccess)
            {
                await RefreshNetBiosAsync(ct).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task FlushNetBiosCacheAsync()
    {
        await RunExclusiveAsync("сброс кэша NetBIOS", async ct =>
        {
            StatusText = "nbtstat -R / -RR…";
            var result = await _networkService.FlushNetBiosCacheAsync(ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
        }).ConfigureAwait(true);
    }

    // Очистка кэша DNS-резолвера: ipconfig /flushdns стирает локальную историю запросов к доменам.
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task FlushDnsAsync()
    {
        await RunExclusiveAsync("очистка кэша DNS", async ct =>
        {
            StatusText = "ipconfig /flushdns…";
            var result = await _networkService.FlushDnsCacheAsync(ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
        }).ConfigureAwait(true);
    }

    private bool CanRemoveMinCifraCert() => CanModify() && IsMinCifraCertInstalled;

    [RelayCommand(CanExecute = nameof(CanRemoveMinCifraCert))]
    private async Task RemoveMinCifraCertAsync()
    {
        if (!_dialogs.Ask(
                L.T("Сертификат Минцифры"),
                L.T("Удалить сертификаты «Russian Trusted Root CA» из доверенных корневых и промежуточных центров сертификации?"),
                L.T("Удалить")))
        {
            return;
        }

        await RunExclusiveAsync("удаление сертификата Минцифры", async ct =>
        {
            StatusText = L.T("Удаление сертификата Минцифры…");
            // Хранилища сертификатов — IO: не на UI-потоке.
            var result = await TaskRunner.RunBlocking(new TrustedCertificateService().Remove, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            IsMinCifraCertInstalled = await TaskRunner.RunBlocking(
                TrustedCertificateService.IsRussianTrustedInstalled, ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task GamingProfileAsync()
    {
        if (!_dialogs.Ask(
                L.T("Профиль для игр"),
                L.T("Будут применены: Auto-Tuning=disabled, ECN=disabled, QoS override=0%.\n"
                    + "Профиль не гарантирует снижение пинга; исходные значения сохраняются в бэкап.\nПродолжить?"),
                L.T("Применить профиль")))
        {
            return;
        }

        await RunExclusiveAsync("игровой профиль", async ct =>
        {
            StatusText = L.T("Применение игрового профиля…");
            var result = await _networkService.ApplyGamingProfileAsync(ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            if (result.IsSuccess)
            {
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Сеть"),
                    L.T("Игровой профиль применён"),
                    HistoryEvent.StatusOk));
            }

            await RefreshTcpGlobalAsync(ct).ConfigureAwait(true);
            await RefreshQosAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    // Откат игрового профиля: TCP Global и QoS возвращаются из бэкапов последовательно.
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RollbackGamingProfileAsync()
    {
        await RunExclusiveAsync("откат игрового профиля", async ct =>
        {
            StatusText = L.T("Откат игрового профиля из бэкапа…");
            var tcp = await _networkService.RestoreTcpGlobalAsync(ct).ConfigureAwait(true);
            var qos = await TaskRunner.RunBlocking(_networkService.RestoreQosOverride, ct).ConfigureAwait(true);
            var messages = string.Join(" ", new[] { tcp, qos }.Select(r => L.S(r.Message)));
            StatusText = tcp.IsSuccess && qos.IsSuccess
                ? L.T("Игровой профиль отключён: {0}", messages)
                : L.T("Ошибка: {0}", messages);
            if (tcp.IsSuccess && qos.IsSuccess)
            {
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Сеть"),
                    L.T("Откат игрового профиля"),
                    HistoryEvent.StatusOk));
            }
            await RefreshTcpGlobalAsync(ct).ConfigureAwait(true);
            await RefreshQosAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ResetAllAsync()
    {
        if (!_dialogs.Ask(
                L.T("Сброс сетевых параметров"),
                L.T("Восстановить TCP Global, QoS override, MTU и NetBIOS из сохранённых бэкапов?"),
                L.T("Восстановить всё")))
        {
            return;
        }

        await RunExclusiveAsync("сброс сетевых параметров", async ct =>
        {
            StatusText = L.T("Восстановление всех параметров из бэкапов…");
            var result = await _networkService.ResetAllAsync(ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Сброс завершён не полностью: {0}", result.Message);
            _logger.Info("NET | reset all | " + (result.IsSuccess ? "ok" : "partial: " + result.Message));
            if (result.IsSuccess)
            {
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Сеть"),
                    L.T("Сброс сетевых параметров"),
                    HistoryEvent.StatusOk));
            }
            await RefreshTcpGlobalAsync(ct).ConfigureAwait(true);
            await RefreshInterfacesAsync(ct).ConfigureAwait(true);
            await RefreshQosAsync(ct).ConfigureAwait(true);
            await RefreshNetBiosAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | network operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Возвращает описание проблемы чтения (null — прочитано), чтобы RefreshAsync
    // показал её в статусе: сбой чтения не должен выглядеть как «Состояние обновлено».
    private async Task<string?> RefreshTcpGlobalAsync(CancellationToken ct)
    {
        var state = await _networkService.GetTcpGlobalAsync(ct).ConfigureAwait(true);
        _tcpGlobalState = state.IsSuccess ? state.Value : null;
        // П.5: выбор в ComboBox выставляется по факту (регистронезависимо);
        // несчитанное/неизвестное значение — плейсхолдер, а не прошлое значение.
        SelectedAutoTuning = state.IsSuccess ? MatchOption(AutoTuningOptions, state.Value?.AutoTuning) : null;
        SelectedEcn = state.IsSuccess ? MatchOption(EcnOptions, state.Value?.Ecn) : null;
        UpdateIsGamingActive();
        return state.IsSuccess ? null : state.Message;
    }

    private static string? MatchOption(string[] options, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return null;
        }

        return options.FirstOrDefault(o => string.Equals(o, actual.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> RefreshInterfacesAsync(CancellationToken ct)
    {
        var list = await _networkService.GetInterfacesAsync(ct).ConfigureAwait(true);
        Interfaces.Clear();
        if (list.IsSuccess)
        {
            foreach (var item in list.Value ?? [])
            {
                Interfaces.Add(new InterfaceRow(item.Alias, item.Mtu));
            }

            return null;
        }

        // Пустой список при сбое неотличим от «интерфейсов нет» — проблема в статус.
        _logger.Warn("NET | interfaces | " + list.Message);
        return L.T("интерфейсы: {0}", list.Message);
    }

    private async Task RefreshQosAsync(CancellationToken ct)
    {
        QosOverrideValue = await TaskRunner.RunBlocking(_networkService.GetQosOverride, ct).ConfigureAwait(true);
        UpdateIsGamingActive();
    }

    private async Task RefreshNetBiosAsync(CancellationToken ct)
    {
        var modes = await TaskRunner.RunBlocking(NetworkService.GetNetBiosModes, ct).ConfigureAwait(true);
        // П.13: строка с актуальными режимами удалена — состояние видно по подсветке кнопок.
        // Подсветка: все интерфейсы в одном режиме — он и активен; смешанное/пустое — ничего.
        ActiveNetBiosKey = modes.Count > 0 && modes.All(m => m.Options == 2)
            ? "disable"
            : modes.Count > 0 && modes.All(m => m.Options == 0) ? "dhcp" : null;
    }

    // Последнее прочитанное TCP Global — для вычисления признака игрового профиля.
    private NetworkService.TcpGlobalState? _tcpGlobalState;

    // Игровой профиль активен: Auto-Tuning и ECN выключены, QoS override = 0.
    // Сравнение без регистра — netsh может вернуть значения в любом регистре.
    private void UpdateIsGamingActive() => IsGamingActive =
        string.Equals(_tcpGlobalState?.AutoTuning, "disabled", StringComparison.OrdinalIgnoreCase)
        && string.Equals(_tcpGlobalState?.Ecn, "disabled", StringComparison.OrdinalIgnoreCase)
        && QosOverrideValue == 0;

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
        _operationCts?.Cancel();
    }

    private async Task RunExclusiveAsync(string title, Func<CancellationToken, Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await action(_operationCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = L.T("Операция отменена.");
            _logger.Warn("CANCEL | " + title);
        }
        catch (Exception exception)
        {
            StatusText = L.T("Ошибка: {0}", exception.Message);
            _logger.Error("NET | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public sealed record InterfaceRow(string Alias, int Mtu)
    {
        public string Display => $"{Alias} — MTU: {Mtu}";
    }
}
