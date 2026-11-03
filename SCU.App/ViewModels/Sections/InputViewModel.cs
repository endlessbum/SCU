using System.Collections.ObjectModel;
using Microsoft.Win32;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Infrastructure.Windows.Tweaks;
using SCU.Models;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Раздел 11 «Ввод, браузер и игры» — тумблеры поверх InputService
// (мышь, залипание клавиш, Edge, Game Bar, DVR, Game Mode) + пресет отключения Game Bar/DVR,
// плюс карточка «Перевод раскладки» (автоисправление ввода в неверной раскладке).
public partial class InputViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly InputService _inputService;
    private readonly KeyboardLayoutTranslator _translator;
    private readonly LayoutTranslatorHotkeyService _hotkeyService;
    private LayoutTranslatorProfileSettings _translatorSettings = new();
    private bool _translatorSettingsLoaded;
    private CancellationTokenSource? _operationCts;

    // Жизненный цикл переводчика: Off / Starting / On / Stopping / Error.
    public enum TranslatorLifecycleState
    {
        Off,
        Starting,
        On,
        Stopping,
        Error
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleSwitchCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableGameBarDvrCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetLayoutTranslatorCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isAdmin;

    // UI-отражаемое состояние переключателя. Обновляется только из фактического
    // результата Start/Stop — никогда из уже переключённого UI-состояния.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranslatorRunning))]
    [NotifyPropertyChangedFor(nameof(IsTranslatorInteractive))]
    private bool _layoutTranslatorOn;

    // Явное состояние жизненного цикла хука.
    [ObservableProperty]
    private TranslatorLifecycleState _translatorState = TranslatorLifecycleState.Off;

    [ObservableProperty]
    private string _statusText = L.T("Загрузка состояния…");

    [ObservableProperty]
    private string _manualHotKey = "Pause";

    [ObservableProperty]
    private string _customExceptionDraft = string.Empty;

    [ObservableProperty]
    private bool _showCorrectionToast = true;

    public ObservableCollection<string> TranslatorCustomExceptions { get; } = [];

    public InputViewModel(Logger logger, IConfirmDialogService dialogs)
    {
        _logger = logger;
        _dialogs = dialogs;
        IsAdmin = Elevation.IsAdmin();
        _inputService = new InputService(logger, new RegistryHelper(logger));
        _translator = new KeyboardLayoutTranslator(logger);
        _hotkeyService = new LayoutTranslatorHotkeyService(logger);
        _hotkeyService.HotkeyPressed += OnManualConversionHotkey;
        foreach (var option in _inputService.Switches)
        {
            // Звёздочка у параметров, включение которых выключает системное
            // поведение (см. сноску под списком во view).
            var title = option.Id is "mouse-acceleration" or "sticky-keys"
                ? option.Title + " *"
                : option.Title;
            // OnMeansEnable: тумблер включён = функция работает.
            Rows.Add(new SwitchRow(option.Id, title, option.Description, false, onMeansEnable: true));
        }

        // Закрепление на «Главной» (скрепка в SwitchRowCard): 5 тумблеров ввода
        // из ТЗ; «Ускорение запуска Edge» не подлежит.
        foreach (var row in Rows)
        {
            if (PinnableSwitchIds.Contains(row.Id))
            {
                row.PinCardId = "input." + row.Id;
            }
        }
    }

    private static readonly HashSet<string> PinnableSwitchIds = new(StringComparer.Ordinal)
    {
        "mouse-acceleration",
        "sticky-keys",
        "game-bar",
        "game-dvr",
        "game-mode",
    };

    public ObservableCollection<SwitchRow> Rows { get; } = [];

    public async Task InitializeAsync()
    {
        var states = await TaskRunner.RunBlocking(() =>
            _inputService.Switches
                .Select(option => (option.Id, IsOn: _inputService.IsSwitchOn(option)))
                .ToDictionary(x => x.Id, x => x.IsOn, StringComparer.OrdinalIgnoreCase))
            .ConfigureAwait(true);

        foreach (var row in Rows)
        {
            if (states.TryGetValue(row.Id, out var state))
            {
                ApplyReadState(row, state);
            }
        }

        StatusText = L.T("Состояние обновлено.");

        // Загружаем параметры переводчика из ProfileStateStore. Старый файл
        // layout-translator.json читается только для миграции предыдущих версий.
        try
        {
            _translatorSettings = await _inputService.LoadLayoutTranslatorSettingsAsync().ConfigureAwait(true);
            if (!_translatorSettings.IsConfigured)
            {
                _translatorSettings.AutoCorrectEnabled = LoadTranslatorPreference();
                _translatorSettings.IsConfigured = true;
                await _inputService.SaveLayoutTranslatorSettingsAsync(_translatorSettings).ConfigureAwait(true);
            }

            ManualHotKey = _translatorSettings.ManualHotKey;
            ShowCorrectionToast = _translatorSettings.ShowCorrectionToast;
            TranslatorCustomExceptions.Clear();
            foreach (var value in _translatorSettings.CustomExceptions)
            {
                TranslatorCustomExceptions.Add(value);
            }
            _translator.Configure(TranslatorCustomExceptions, _translatorSettings.ShowCorrectionToast);
            _translatorSettingsLoaded = true;

            if (_translatorSettings.AutoCorrectEnabled)
            {
                // Важно: Start ставит WH_KEYBOARD_LL на WPF UI-потоке, не в Task.Run.
                var result = _translator.Start();
                LayoutTranslatorOn = _translator.IsRunning;
                App.RunInBackgroundOnClose = _translator.IsRunning;
                if (result.IsSuccess)
                {
                    TranslatorState = TranslatorLifecycleState.On;
                    var hotkeysRegistered = _hotkeyService.SetEnabled(true, ManualHotKey);
                    StatusText = hotkeysRegistered
                        ? L.T("Перевод раскладки включён.")
                        : L.T("Перевод раскладки включён, но горячие клавиши заняты другим приложением.");
                }
                else
                {
                    TranslatorState = TranslatorLifecycleState.Error;
                    LayoutTranslatorOn = false;
                    StatusText = L.T("Ошибка перевода раскладки: {0} (код {1})", result.Message, result.Code);
                    _translatorSettings.AutoCorrectEnabled = false;
                    await _inputService.SaveLayoutTranslatorSettingsAsync(_translatorSettings).ConfigureAwait(true);
                    SaveTranslatorPreference(false);
                    SetScuAutorun(false);
                }
            }
            else
            {
                _hotkeyService.SetEnabled(false, ManualHotKey);
                // Если предыдущий запуск оставил нашу запись автозапуска, удаляем
                // только собственное неизменённое значение. Пользовательская запись
                // без маркера владения остаётся нетронутой.
                SetScuAutorun(false);
            }
        }
        catch (Exception ex)
        {
            // Не роняем инициализацию раздела из-за сбоя восстановления,
            // но фиксируем причину без пользовательского текста.
            _logger.Warn("INPUT | восстановление переводчика раскладки: " + ex.Message);
            _translatorSettingsLoaded = true;
        }
    }

    private const string TranslatorPreferenceFileName = "layout-translator.json";
    private static string GetTranslatorPreferencePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU",
        TranslatorPreferenceFileName);

    private static bool LoadTranslatorPreference()
    {
        try
        {
            var path = GetTranslatorPreferencePath();
            if (!File.Exists(path))
            {
                return false;
            }
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("enabled", out var el))
            {
                return el.ValueKind == JsonValueKind.True;
            }
        }
        catch
        {
        }
        return false;
    }

    private static void SaveTranslatorPreference(bool enabled)
    {
        try
        {
            var path = GetTranslatorPreferencePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new { enabled }));
        }
        catch
        {
        }
    }

    private static string GetTranslatorAutorunOwnershipPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU",
        "layout-translator-run-owner.txt");

    // Переводчик может предложить автозапуск самого SCU, но не имеет права
    // перезаписывать или удалять созданную пользователем запись Run. Удаляем
    // только точное значение, созданное нами и отмеченное ownership-файлом.
    private void SetScuAutorun(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "SCU";
        var ownershipPath = GetTranslatorAutorunOwnershipPath();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(runKey, writable: true);
            if (key is null)
            {
                _logger.Warn("INPUT | не удалось открыть раздел автозапуска SCU");
                return;
            }

            string? currentValue = key.GetValue(valueName) as string;
            string? ownedValue = null;
            try
            {
                if (File.Exists(ownershipPath))
                    ownedValue = File.ReadAllText(ownershipPath).Trim();
            }
            catch
            {
                // При невозможности проверить владение предпочитаем сохранить запись.
            }

            if (enabled)
            {
                // Если запись уже есть, она не наша по умолчанию. Если значение
                // изменено относительно нашей метки — отдаём владение пользователю.
                if (!string.IsNullOrWhiteSpace(currentValue))
                {
                    if (!string.IsNullOrWhiteSpace(ownedValue)
                        && !string.Equals(currentValue, ownedValue, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(ownershipPath); } catch { }
                    }
                    return;
                }

                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                    return;

                var command = "\"" + exe + "\"";
                // В режиме разработки приложение иногда запущено через dotnet.exe.
                // Сохраняем путь к entry assembly, иначе Run запустил бы только CLI.
                if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                    if (!string.IsNullOrWhiteSpace(entryAssembly) && File.Exists(entryAssembly))
                        command += " \"" + entryAssembly + "\"";
                }
                var directory = Path.GetDirectoryName(ownershipPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                // Метка записывается до реестра: при сбое SetValue она не приведёт
                // к удалению существующей записи, поскольку её ещё нет.
                File.WriteAllText(ownershipPath, command);
                key.SetValue(valueName, command, RegistryValueKind.String);
                return;
            }

            // Без маркера владения никогда не удаляем Run\SCU: это мог быть
            // пользовательский или установочный автозапуск из старой версии.
            if (string.IsNullOrWhiteSpace(ownedValue))
                return;

            if (!string.IsNullOrWhiteSpace(currentValue)
                && string.Equals(currentValue, ownedValue, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }

            try { File.Delete(ownershipPath); } catch { }
        }
        catch (Exception ex)
        {
            _logger.Warn("INPUT | управление автозапуском переводчика не выполнено: " + ex.GetType().Name);
        }
    }

    // Применение прочитанного состояния. null — сбой чтения: тумблер остаётся
    // на последнем известном значении, но помечается «?» — иначе сбой выглядел
    // бы как конкретное «включено/выключено».
    private static void ApplyReadState(SwitchRow row, bool? state)
    {
        if (state is { } isOn)
        {
            row.ForceState(isOn);
            row.StateUnknown = false;
        }
        else
        {
            row.StateUnknown = true;
        }
    }

    public bool IsInteractive => !IsBusy && IsAdmin;

    public string ReadOnlyHint =>
        IsAdmin ? string.Empty : L.T("Нужны права администратора — изменение параметров ввода и игр недоступно.");

    // Переводчик раскладки — пользовательская функция (хук на ввод текущего пользователя),
    // не требует прав администратора. Остальные переключатели раздела оставляем с требуемыми правами.
    public bool IsTranslatorInteractive => !IsBusy;

    public bool IsTranslatorRunning => _translator.IsRunning;

    private bool CanModify() => !IsBusy && IsAdmin;
    private bool CanRefresh() => !IsBusy;
    private bool CanCancel() => IsBusy;
    private bool CanToggleTranslator() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleSwitchAsync(SwitchRow? row)
    {
        if (row is null)
        {
            return;
        }

        var option = _inputService.Find(row.Id);
        if (option is null)
        {
            return;
        }

        var on = row.IsOn;
        await RunExclusiveAsync("переключение " + option.Id, async ct =>
        {
            StatusText = L.T(option.Title) + ": " + L.T(on ? "включение…" : "выключение…");
            try
            {
                var result = await TaskRunner.RunBlocking(() => _inputService.SetSwitch(option, on), ct).ConfigureAwait(true);
                var actual = await TaskRunner.RunBlocking(() => _inputService.IsSwitchOn(option), ct).ConfigureAwait(true);
                ApplyReadState(row, actual);
                StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
                _logger.Info($"INPUT | {option.Id} | {(actual is null ? "unknown" : actual == true ? "on" : "off")} | rc={result.Code}");
            }
            catch (OperationCanceledException)
            {
                // Ресинк затронутой строки на пути отмены: чтение без токена, best-effort.
                try
                {
                    ApplyReadState(row, await TaskRunner.RunBlocking(() => _inputService.IsSwitchOn(option), CancellationToken.None).ConfigureAwait(true));
                }
                catch
                {
                    // Не маскируем исходную отмену ошибкой чтения.
                }

                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task DisableGameBarDvrAsync()
    {
        if (!_dialogs.Ask(
                L.T("Game Bar и DVR"),
                L.T("Отключить Game Bar и фоновую запись (DVR)?\nGame Mode останется включённым — это полезно для игр."),
                L.T("Отключить")))
        {
            return;
        }

        await RunExclusiveAsync("отключение Game Bar / DVR", async ct =>
        {
            StatusText = L.T("Отключение Game Bar и DVR…");
            var result = await TaskRunner.RunBlocking(_inputService.DisableGameBarAndDvr, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            foreach (var row in Rows)
            {
                var option = _inputService.Find(row.Id);
                if (option is null)
                {
                    continue;
                }

                var state = await TaskRunner.RunBlocking(() => _inputService.IsSwitchOn(option), ct).ConfigureAwait(true);
                ApplyReadState(row, state);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление раздела ввода", async ct =>
        {
            StatusText = L.T("Чтение параметров ввода и игр…");
            foreach (var row in Rows)
            {
                var option = _inputService.Find(row.Id);
                if (option is null)
                {
                    continue;
                }

                var state = await TaskRunner.RunBlocking(() => _inputService.IsSwitchOn(option), ct).ConfigureAwait(true);
                ApplyReadState(row, state);
            }

            StatusText = L.T("Состояние обновлено.");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanToggleTranslator))]
    private async Task SetLayoutTranslatorAsync(bool? enable)
    {
        if (IsBusy)
        {
            return;
        }

        // Target state comes from CommandParameter (the ToggleButton's post-click IsChecked),
        // not from the already-bound LayoutTranslatorOn property.
        var target = enable ?? !LayoutTranslatorOn;
        if (target == IsTranslatorRunning)
        {
            return;
        }

        Result result;
        if (target)
        {
            // Перед первым запуском глобального хука запрашиваем у пользователя
            // подтверждение. При отказе не запускаем хук и оставляем тумблер OFF.
            if (!_dialogs.Ask(
                    L.T("Перевод раскладки"),
                    L.T("Включить автоматический перевод раскладки?\nПрограмма будет перехватывать нажатия клавиш в активном окне и исправлять слова, набранные в неверной раскладке. Корректный текст не меняется, распознанные поля пароля пропускаются."),
                    L.T("Включить")))
            {
                LayoutTranslatorOn = false;
                TranslatorState = TranslatorLifecycleState.Off;
                StatusText = L.T("Перевод раскладки не включён.");
                return;
            }

            TranslatorState = TranslatorLifecycleState.Starting;
            StatusText = L.T("Перевод раскладки: включение…");
            // Исключения и параметры уведомлений должны примениться до того,
            // как хук начнёт получать первый ввод.
            _translator.Configure(TranslatorCustomExceptions, _translatorSettings.ShowCorrectionToast);
            result = _translator.Start();
        }
        else
        {
            TranslatorState = TranslatorLifecycleState.Stopping;
            StatusText = L.T("Перевод раскладки: выключение…");
            result = _translator.Stop();
        }

        // Состояние UI выставляется только из фактического результата операции.
        LayoutTranslatorOn = _translator.IsRunning;

        if (result.IsSuccess)
        {
            TranslatorState = target ? TranslatorLifecycleState.On : TranslatorLifecycleState.Off;
            StatusText = target
                ? L.T("Перевод раскладки включён.")
                : L.T("Перевод раскладки выключен.");
            _logger.Info($"LAYOUT | toggle | target={target} | result=ok");
            // Фоновый режим (сворачивание в трей) переводчиком НЕ управляется:
            // это явный выбор пользователя в настройках (дизайн, п. 2) —
            // «Не включать фоновый режим скрытно вместе с переводчиком».

            // Сохраняем выбор пользователя и обеспечиваем автозапуск SCU
            // (переводчик работает только пока SCU запущена).
            _translatorSettings.AutoCorrectEnabled = target;
            _translatorSettings.IsConfigured = true;
            _translator.Configure(TranslatorCustomExceptions, _translatorSettings.ShowCorrectionToast);
            var hotkeysRegistered = _hotkeyService.SetEnabled(target, ManualHotKey);
            SaveTranslatorPreference(target);
            ApplyScuAutorunConsent(target);
            var persisted = await _inputService.SaveLayoutTranslatorSettingsAsync(_translatorSettings).ConfigureAwait(true);
            if (!persisted)
            {
                _logger.Warn("INPUT | настройки переводчика раскладки не сохранены");
            }
            if (target && !hotkeysRegistered)
            {
                StatusText = L.T("Перевод раскладки включён, но горячие клавиши заняты другим приложением.");
            }
        }
        else
        {
            // При ошибке оставляем UI в OFF, показываем сообщение и Win32-код.
            TranslatorState = TranslatorLifecycleState.Error;
            LayoutTranslatorOn = false;
            _hotkeyService.SetEnabled(false, ManualHotKey);
            // Фоновый режим не трогаем: он не связан с переводчиком, а задаётся
            // выбором пользователя в настройках (дизайн, п. 2).
            StatusText = L.T("Ошибка перевода раскладки: {0} (код {1})", result.Message, result.Code);
            _logger.Warn($"LAYOUT | toggle | target={target} | result=fail | code={result.Code} | msg={result.Message}");
        }
    }

    // Автозапуск SCU — отдельное решение пользователя (аудит, п. 12): переводчик
    // работает только пока SCU запущена, но создавать запись Run\SCU без
    // согласия нельзя. Вопрос задаётся один раз, ответ сохраняется в профиле.
    // Отказ не трогает ранее созданный автозапуск — он просто не создаётся.
    private void ApplyScuAutorunConsent(bool target)
    {
        if (!target)
        {
            SetScuAutorun(false);
            return;
        }

        if (_translatorSettings.AutorunConsent is null)
        {
            var allowed = _dialogs.Ask(
                L.T("Автозапуск SCU"),
                L.T("Перевод раскладки работает, пока запущена SCU. Добавить SCU в автозагрузку Windows, чтобы переводчик включался вместе с системой?"),
                L.T("Добавить"));
            _translatorSettings.AutorunConsent = allowed;
        }

        if (_translatorSettings.AutorunConsent == true)
        {
            SetScuAutorun(true);
        }
        else
        {
            _logger.Info("INPUT | autorun not enabled: no user consent");
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | input operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();

    partial void OnManualHotKeyChanged(string value)
    {
        if (!_translatorSettingsLoaded) return;
        _translatorSettings.ManualHotKey = value == "Ctrl+Alt+P" ? "Ctrl+Alt+P" : "Pause";
        var registered = _hotkeyService.SetEnabled(_translatorSettings.AutoCorrectEnabled, _translatorSettings.ManualHotKey);
        if (_translatorSettings.AutoCorrectEnabled)
        {
            StatusText = registered
                ? L.T("Перевод раскладки включён. Горячая клавиша ручной конвертации: {0}.", _translatorSettings.ManualHotKey)
                : L.T("Перевод раскладки включён, но горячие клавиши заняты другим приложением.");
        }
        _ = PersistTranslatorSettingsAsync();
    }

    partial void OnShowCorrectionToastChanged(bool value)
    {
        if (!_translatorSettingsLoaded) return;
        _translatorSettings.ShowCorrectionToast = value;
        _translator.Configure(TranslatorCustomExceptions, value);
        _ = PersistTranslatorSettingsAsync();
    }

    [RelayCommand]
    private async Task AddTranslatorExceptionAsync()
    {
        var value = CustomExceptionDraft.Trim();
        if (value.Length == 0) return;
        if (value.Length > 64 || value.Any(char.IsWhiteSpace))
        {
            StatusText = L.T("Исключение должно быть одним токеном длиной не более 64 символов.");
            return;
        }
        if (TranslatorCustomExceptions.Count >= 200)
        {
            StatusText = L.T("Можно добавить не более 200 исключений.");
            return;
        }
        if (TranslatorCustomExceptions.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
        {
            CustomExceptionDraft = string.Empty;
            return;
        }

        TranslatorCustomExceptions.Add(value);
        CustomExceptionDraft = string.Empty;
        _translatorSettings.CustomExceptions = TranslatorCustomExceptions.ToList();
        _translator.Configure(TranslatorCustomExceptions, _translatorSettings.ShowCorrectionToast);
        await PersistTranslatorSettingsAsync().ConfigureAwait(true);
        StatusText = L.T("Исключение добавлено.");
    }

    [RelayCommand]
    private async Task RemoveTranslatorExceptionAsync(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var found = TranslatorCustomExceptions.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        if (found is null) return;
        TranslatorCustomExceptions.Remove(found);
        _translatorSettings.CustomExceptions = TranslatorCustomExceptions.ToList();
        _translator.Configure(TranslatorCustomExceptions, _translatorSettings.ShowCorrectionToast);
        await PersistTranslatorSettingsAsync().ConfigureAwait(true);
        StatusText = L.T("Исключение удалено.");
    }

    private async Task PersistTranslatorSettingsAsync()
    {
        if (!_translatorSettingsLoaded) return;
        _translatorSettings.CustomExceptions = TranslatorCustomExceptions.ToList();
        _translatorSettings.ShowCorrectionToast = ShowCorrectionToast;
        var saved = await _inputService.SaveLayoutTranslatorSettingsAsync(_translatorSettings).ConfigureAwait(true);
        if (!saved) _logger.Warn("INPUT | настройки переводчика раскладки не сохранены");
    }

    // Ручная конвертация по горячей клавише: асинхронная (без Thread.Sleep
    // на UI-потоке — он блокировал интерфейс, аудит п. 23). Повторное
    // нажатие во время операции игнорируется.
    private async void OnManualConversionHotkey(ManualConversionAction action)
    {
        if (_manualConversionRunning)
        {
            return;
        }

        _manualConversionRunning = true;
        try
        {
            var (ok, status) = await ClipboardLayoutConversion.ConvertAsync(action).ConfigureAwait(true);
            StatusText = status;
        }
        catch (Exception ex)
        {
            _logger.Warn("INPUT | ручная конвертация: " + ex.Message);
            StatusText = L.T("Не удалось выполнить ручную конвертацию.");
        }
        finally
        {
            _manualConversionRunning = false;
        }
    }

    private bool _manualConversionRunning;

    public void Dispose()
    {
        _operationCts?.Cancel();
        _hotkeyService.HotkeyPressed -= OnManualConversionHotkey;
        _hotkeyService.Dispose();
        _translator.Dispose();
    }

    private Task RunExclusiveAsync(string title, Func<CancellationToken, Task> action) =>
        SectionOperations.RunExclusiveAsync(title, "INPUT", () => IsBusy, value => IsBusy = value, () => _operationCts, value => _operationCts = value, value => StatusText = value, _logger, action);}
