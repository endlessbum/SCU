using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Models;
using SCU.Services.Dashboard;

namespace SCU.ViewModels.Sections;

// Раздел 0 «Состояние ПК»: один большой выключатель, применяющий выбранные
// утилиты по порядку. Выключатель — «Apply»: после выполнения списка сам
// возвращается в OFF; ручное выключение ничего не откатывает (откаты — в
// соответствующих разделах). Список утилит раскрывается шестерёнкой ниже:
// каждая утилита всех разделов (кнопки, пресеты и тумблеры; исключены
// зависящие от пользовательского ввода — текстовые поля, выбор дисков и точек
// восстановления) включается/выключается своим тумблером и сохраняется в
// state\batch.json. Разделы, где утилиты зависят от данных (Компоненты,
// Автозагрузка, удаление по одной строке), в список не входят.
public partial class DashboardViewModel : ObservableObject, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly HistoryStore _history;
    private readonly BatchStateStore _stateStore;
    private CancellationTokenSource? _batchCts;

    private readonly CleanupViewModel _cleanup;
    private readonly PrivacyViewModel _privacy;
    private readonly ServicesViewModel _services;
    private readonly TasksViewModel _tasks;
    private readonly PowerViewModel _power;
    private readonly NetworkViewModel _network;
    private readonly UIViewModel _ui;
    private readonly InputViewModel _input;
    private readonly MaintenanceViewModel _maintenance;
    private readonly SecurityViewModel _security;
    private readonly UpdateViewModel _update;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunBatch))]
    private bool _isRunning;

    // Состояние большого выключателя: ON запускает пакет, после завершения
    // возвращается в OFF программно; OFF руками — пустая операция.
    [ObservableProperty]
    private bool _batchSwitchOn;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _currentUtilityText = string.Empty;

    [ObservableProperty]
    private string _includedCountText = string.Empty;

    // Поиск по утилитам (запрос разворачивается по словарю синонимов).
    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplySearch();

    // Поиск активен, пока клавиатурный фокус в поле (ставит DashboardView):
    // тогда затемняется фон окна и раскрыты подсказки.
    [ObservableProperty]
    private bool _isSearchFocused;

    public bool IsSearchActive => IsSearchFocused;

    // Подсказки под полем поиска: совпавшие утилиты (то же сопоставление, что
    // и фильтр списка) с номером раздела для перехода по клику.
    public ObservableCollection<SearchSuggestion> Suggestions { get; } = [];

    [ObservableProperty]
    private bool _isSearchSuggestionsOpen;

    // Переход к разделу выбранной подсказки: подписан MainViewModel.
    // title — заголовок утилиты из подсказки: по нему окно находит строку в
    // открывшемся разделе и подсвечивает её (пусто — подсветка не нужна).
    public event Action<int, string?>? NavigateToSectionRequested;

    // Пакет завершён (успешно или отменён) — система менялась массово:
    // подписчик (MainViewModel) запускает один повторный бэнчмарк.
    public event Action? BatchCompleted;

    partial void OnIsSearchFocusedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSearchActive));
        IsSearchSuggestionsOpen = value && Suggestions.Count > 0;
    }

    // Клик по подсказке: перейти на раздел настройки, сбросить поиск — поле
    // закрывается (подсказки и затемнение уходят вместе с фокусом).
    [RelayCommand]
    private void OpenSuggestion(SearchSuggestion suggestion)
    {
        NavigateToSectionRequested?.Invoke(suggestion.SectionNumber, suggestion.Title);
        SearchText = string.Empty;
        IsSearchFocused = false;
    }

    // «К применению: N» — сколько выбранных утилит сейчас можно применить
    // (целевое состояние ещё не достигнуто). Пусто, если применять нечего.
    [ObservableProperty]
    private string _pendingText = string.Empty;

    public DashboardViewModel(
        Logger logger,
        HistoryStore history,
        BatchStateStore stateStore,
        CleanupViewModel cleanup,
        PrivacyViewModel privacy,
        ServicesViewModel services,
        TasksViewModel tasks,
        PowerViewModel power,
        NetworkViewModel network,
        UIViewModel ui,
        InputViewModel input,
        MaintenanceViewModel maintenance,
        SecurityViewModel security,
        UpdateViewModel update)
    {
        _logger = logger;
        _history = history;
        _stateStore = stateStore;
        _cleanup = cleanup;
        _privacy = privacy;
        _services = services;
        _tasks = tasks;
        _power = power;
        _network = network;
        _ui = ui;
        _input = input;
        _maintenance = maintenance;
        _security = security;
        _update = update;

        IsAdmin = Elevation.IsAdmin();

        // Строки живут столько же, сколько приложение — отписка не требуется.
        L.LanguageChanged += () =>
        {
            // Заголовки строк обновились — результаты фильтра могли измениться.
            ApplySearch();
            // Текст «К применению» на новом языке.
            RecomputePending();
        };

        BuildUtilities();
        RecountIncluded();
        ApplySearch();
    }

    public bool IsAdmin { get; }

    public bool CanRunBatch => !IsRunning;

    public string ReadOnlyHint =>
        IsAdmin ? string.Empty : L.T("Нужны права администратора — применение пакета недоступно.");

    public ObservableCollection<BatchGroup> Groups { get; } = [];

    // Группы, прошедшие поиск (те же экземпляры; строки/группы скрываются по IsVisible).
    public ObservableCollection<BatchGroup> FilteredGroups { get; } = [];

    public IReadOnlyList<BatchUtilityRow> Rows { get; private set; } = [];

    // Загрузка сохранённого списка включённых утилит (вызывается из InitializeAsync).
    // Пустой файл — «ещё не сохраняли»: остаётся дефолтный набор из реестра.
    // Плюс: последний снимок из state\snapshots.json — рекомендации, статистика,
    // тренды и diff видны сразу после запуска, без обязательной проверки.
    public async Task InitializeAsync()
    {
        var included = await TaskRunner.RunBlocking(_stateStore.Load, CancellationToken.None)
            .ConfigureAwait(true);
        if (included.Count > 0)
        {
            ApplyIncludedIds(included);
        }

        RecountIncluded();
    }

    partial void OnBatchSwitchOnChanged(bool value)
    {
        if (value)
        {
            TaskRunner.RunAndForget(RunBatchAsync(), _logger, "batch apply");
        }
        else if (IsRunning)
        {
            // Ручное выключение посреди пакета: отменяем оставшиеся операции.
            // Иначе тумблер уже в OFF, а утилиты продолжают применяться —
            // интерфейс врёт о состоянии.
            CancelOngoing();
        }
    }

    // Отмена пакета: пользователем (тумблер в OFF) или уходом с раздела
    // (MainViewModel.CancelOngoingFor). Гасит CTS пакета и операцию раздела,
    // чья утилита выполняется прямо сейчас.
    public void CancelOngoing()
    {
        if (!IsRunning)
        {
            return;
        }

        _batchCts?.Cancel();
        var currentSection = _currentRunningSection;
        if (currentSection.HasValue)
        {
            CancelSectionOperation(currentSection.Value);
        }
    }

    private int? _currentRunningSection;

    private void CancelSectionOperation(int section)
    {
        switch (section)
        {
            case 3: _cleanup.CancelOngoing(); break;
            case 5: _privacy.CancelOngoing(); break;
            case 6: _services.CancelOngoing(); break;
            case 8: _power.CancelOngoing(); break;
            case 9: _network.CancelOngoing(); break;
            case 10: _ui.CancelOngoing(); break;
            case 11: _input.CancelOngoing(); break;
            case 12: _maintenance.CancelOngoing(); break;
            case 13: _security.CancelOngoing(); break;
            case 15: _tasks.CancelOngoing(); break;
            case 16: _update.CancelOngoing(); break;
        }
    }

    private async Task RunBatchAsync()
    {
        // Ручное выключение ничего не делает: пакет запускается только включением.
        if (!BatchSwitchOn || IsRunning)
        {
            return;
        }

        if (!IsAdmin)
        {
            StatusText = ReadOnlyHint;
            BatchSwitchOn = false;
            return;
        }

        var included = Rows.Where(row => row.IsIncluded).ToList();
        if (included.Count == 0)
        {
            StatusText = L.T("Список пуст — включите утилиты в настройках ниже.");
            BatchSwitchOn = false;
            return;
        }

        _batchCts?.Dispose();
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;

        IsRunning = true;
        try
        {
            foreach (var row in Rows)
            {
                row.ResetResult();
            }

            var executed = 0;
            var cancelled = false;
            var index = 0;
            foreach (var row in included)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                index++;
                _currentRunningSection = row.Utility.Section;
                CurrentUtilityText = L.T("Применение: {0} ({1} из {2})…", row.Title, index, included.Count);
                string? result;
                try
                {
                    result = await row.Utility.Run().ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    result = L.T("Операция отменена.");
                    cancelled = true;
                }
                catch (Exception exception)
                {
                    result = L.T("Ошибка: {0}", exception.Message);
                    _logger.Error("BATCH | " + row.Utility.Id + " | " + exception);
                }

                _currentRunningSection = null;
                row.ResultText = result ?? L.T("Пропущено: раздел занят, нет прав или утилита неприменима.");
                row.HasResult = true;
                if (result is not null)
                {
                    executed++;
                }
            }

            CurrentUtilityText = string.Empty;
            StatusText = cancelled
                ? L.T("Пакет отменён: выполнено {0} из {1}.", executed, included.Count)
                : L.T("Готово: выполнено {0} из {1}.", executed, included.Count);
            RecomputePending();

            if (!cancelled)
            {
                await _history.RecordAsync(new HistoryEvent(
                    DateTime.Now,
                    L.T("Оптимизация"),
                    L.T("Пакетное применение ({0} утилит)", executed),
                    HistoryEvent.StatusOk,
                    L.T("Включено: {0}, выполнено: {1}.", included.Count, executed))).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            // Сбой вне построчного catch (RecomputePending, история): статус честный,
            // состояние сбрасывается в finally — тумблер не залипает.
            CurrentUtilityText = string.Empty;
            StatusText = L.T("Ошибка: {0}", exception.Message);
            _logger.Error("BATCH | " + exception);
        }
        finally
        {
            _currentRunningSection = null;
            // Порядок важен: сначала IsRunning=false, затем авто-возврат тумблера —
            // OnBatchSwitchOnChanged(false) при IsRunning=false ничего не отменяет.
            IsRunning = false;
            // Авто-возврат в OFF: выключатель — «Apply», а не постоянное состояние.
            BatchSwitchOn = false;
            // Один пересчёт бэнчмарка на весь пакет (не на каждую утилиту).
            BatchCompleted?.Invoke();
        }
    }

    // Команда тумблера строки списка: выполняется после смены IsIncluded биндингом —
    // здесь только пересчёт счётчика и сохранение набора (запись файла — вне UI-потока).
    [RelayCommand]
    private async Task ToggleUtilityAsync(BatchUtilityRow row)
    {
        RecountIncluded();
        RecomputePending();
        // Save глотает ошибки сам (Warn в лог); файл маленький, ожидание неощутимо.
        await TaskRunner.RunBlocking(
            () => _stateStore.Save(Rows.Where(r => r.IsIncluded).Select(r => r.Utility.Id).ToList()),
            CancellationToken.None).ConfigureAwait(true);
    }

    private void ApplyIncludedIds(List<string> ids)
    {
        foreach (var row in Rows)
        {
            row.IsIncluded = ids.Contains(row.Utility.Id);
        }
    }

    private void RecountIncluded() =>
        IncludedCountText = L.T("Выбрано: {0} из {1} утилит", Rows.Count(r => r.IsIncluded), Rows.Count);

    // Пересчёт «К применению»: сколько выбранных утилит ещё не в целевом
    // состоянии. Читаются только свойства VM (уже отражают систему после
    // стартового refresh) — обращений к системе нет.
    public void RecomputePending()
    {
        var pending = Rows.Count(r => r.IsIncluded && r.Utility.NeedsApply?.Invoke() == true);
        PendingText = pending > 0 ? L.T("К применению: {0}", pending) : string.Empty;
    }

    // ===================== Поиск утилит =====================

    // Словарь синонимов: слово пользователя → стемы для сопоставления с
    // заголовком и ключевыми словами утилиты. Русские и английские слова
    // работают при любом языке интерфейса.
    private static readonly Dictionary<string, string[]> SearchSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- очистка и мусор ---
        ["очист"] = ["очист", "clean", "чист", "temp", "кэш", "кеш", "cache", "корзин", "recycle", "мусор"],
        ["мусор"] = ["очист", "clean", "мусор", "bloat", "junk", "trash", "garbage"],
        ["чист"] = ["очист", "clean", "чист"],
        ["temp"] = ["temp", "временн", "очист"],
        ["кэш"] = ["кэш", "кеш", "cache", "очист"],
        ["cache"] = ["cache", "кэш", "кеш", "очист"],
        ["корзин"] = ["корзин", "recycle", "bin", "очист"],
        ["recycle"] = ["recycle", "корзин", "bin"],
        ["trash"] = ["trash", "мусор", "очист", "clean"],
        ["garbage"] = ["garbage", "мусор", "очист", "clean"],
        ["junk"] = ["junk", "мусор", "очист", "clean"],
        ["браузер"] = ["браузер", "browser", "chrome", "edge", "opera", "firefox", "яндекс", "кэш"],
        ["browser"] = ["browser", "браузер", "chrome", "edge", "opera", "firefox"],
        ["chrome"] = ["chrome", "браузер", "browser"],
        ["firefox"] = ["firefox", "браузер", "browser"],
        ["opera"] = ["opera", "браузер", "browser"],
        ["яндекс"] = ["яндекс", "браузер", "browser"],
        ["обновл"] = ["обновл", "update", "wu", "windows update", "softwaredistribution"],
        ["update"] = ["update", "обновл", "wu", "драйвер", "driver"],
        ["wu"] = ["wu", "update", "обновл"],
        ["драйвер"] = ["драйвер", "driver", "windows update", "обновл"],
        ["driver"] = ["driver", "драйвер", "windows update"],
        ["драв"] = ["драйвер", "driver"],
        ["пауза"] = ["пауза", "pause", "приостанов", "обновл"],
        ["pause"] = ["pause", "пауза", "приостанов"],
        ["приостанов"] = ["приостанов", "pause", "пауза"],
        ["блок"] = ["блок", "block", "запрет", "обновл"],
        ["block"] = ["block", "блок", "запрет"],
        ["запрет"] = ["запрет", "block", "блок"],

        // --- питание и железо ---
        ["гиберн"] = ["гиберн", "hibernat", "сон", "sleep", "hiberfil"],
        ["hibernat"] = ["hibernat", "гиберн", "hiberfil", "sleep"],
        ["сон"] = ["сон", "гиберн", "hibernat", "sleep"],
        ["sleep"] = ["sleep", "сон", "гиберн", "hibernat"],
        ["быстр"] = ["быстр", "fastboot", "fast startup", "запуск"],
        ["fastboot"] = ["fastboot", "быстр", "fast startup"],
        ["памят"] = ["памят", "memory", "озу", "ram", "compress", "сжат"],
        ["memory"] = ["memory", "памят", "ram", "compress"],
        ["озу"] = ["озу", "ram", "памят", "memory"],
        ["ram"] = ["ram", "озу", "памят", "memory"],
        ["сжат"] = ["сжат", "compress", "compact", "памят"],
        ["compress"] = ["compress", "сжат", "compact", "памят"],
        ["sysmain"] = ["sysmain", "superfetch"],
        ["superfetch"] = ["superfetch", "sysmain"],
        ["prefetch"] = ["prefetch", "prefetcher"],
        ["compact"] = ["compact", "сжат", "compactos"],
        ["compactos"] = ["compactos", "compact", "сжат"],
        ["коротк"] = ["коротк", "8.3", "short names"],
        ["доступ"] = ["доступ", "last access", "access"],
        ["last access"] = ["last access", "доступ"],
        ["план"] = ["план", "plan", "power", "питан", "производ"],
        ["power"] = ["power", "питан", "план", "plan"],
        ["питан"] = ["питан", "power", "план", "plan"],
        ["производ"] = ["производ", "performance", "план", "скорост"],
        ["performance"] = ["performance", "производ", "план"],
        ["скорост"] = ["скорост", "speed", "производ", "performance"],
        ["speed"] = ["speed", "скорост", "производ"],
        ["тормоз"] = ["тормоз", "производ", "performance", "скорост"],
        ["лаг"] = ["лаг", "fps", "производ", "игр", "game"],
        ["fps"] = ["fps", "лаг", "игр", "game"],
        ["подкач"] = ["подкач", "pagefile", "swap"],
        ["pagefile"] = ["pagefile", "подкач", "swap"],
        ["cpu"] = ["cpu", "процессор", "processor", "ядер", "numproc"],
        ["процессор"] = ["процессор", "cpu", "processor"],
        ["processor"] = ["processor", "cpu", "процессор"],
        ["numproc"] = ["numproc", "cpu", "bcd", "ограничен"],
        ["ограничен"] = ["ограничен", "numproc", "truncatememory", "cpu", "bcd"],

        // --- игры и ввод ---
        ["игр"] = ["игр", "game", "gaming", "fps", "dvr"],
        ["game"] = ["game", "игр", "gaming"],
        ["gaming"] = ["gaming", "game", "игр"],
        ["dvr"] = ["dvr", "запис", "record", "capture"],
        ["запис"] = ["запис", "dvr", "record", "capture"],
        ["record"] = ["record", "запис", "dvr"],
        ["capture"] = ["capture", "запис", "dvr"],
        ["мышь"] = ["мышь", "mouse", "указател", "курсор", "pointer", "accelerat", "ускорен"],
        ["mouse"] = ["mouse", "мышь", "pointer", "accelerat"],
        ["курсор"] = ["курсор", "pointer", "мышь", "mouse"],
        ["pointer"] = ["pointer", "указател", "mouse", "мышь"],
        ["указател"] = ["указател", "pointer", "мышь", "mouse"],
        ["ускорен"] = ["ускорен", "accelerat", "мышь", "mouse", "edge"],
        ["accelerat"] = ["accelerat", "ускорен", "mouse"],
        ["клавиатур"] = ["клавиатур", "keyboard", "залипан", "sticky"],
        ["keyboard"] = ["keyboard", "клавиатур", "sticky", "залипан"],
        ["залипан"] = ["залипан", "sticky", "клавиатур", "keyboard"],
        ["sticky"] = ["sticky", "залипан", "keys"],

        // --- сеть ---
        ["сеть"] = ["сеть", "net", "network", "интернет", "inet", "tcp", "адаптер"],
        ["net"] = ["net", "сеть", "network", "tcp"],
        ["network"] = ["network", "сеть", "net", "tcp"],
        ["интернет"] = ["интернет", "inet", "сеть", "network"],
        ["inet"] = ["inet", "интернет", "сеть"],
        ["ping"] = ["ping", "пинг", "сеть", "tcp"],
        ["пинг"] = ["пинг", "ping", "сеть"],
        ["tcp"] = ["tcp", "сеть", "net", "autotun"],
        ["mtu"] = ["mtu", "сеть", "интерфейс"],
        ["qos"] = ["qos", "полос", "override"],
        ["netbios"] = ["netbios", "smb", "сеть"],
        ["autotun"] = ["autotun", "auto-tuning", "автотюнинг", "tcp"],
        ["auto-tuning"] = ["auto-tuning", "autotun", "tcp"],
        ["автотюнинг"] = ["автотюнинг", "autotun", "auto-tuning"],
        ["ecn"] = ["ecn", "перегруз", "tcp"],
        ["адаптер"] = ["адаптер", "adapter", "nic", "карт", "сеть"],
        ["adapter"] = ["adapter", "адаптер", "nic", "network"],
        ["карт"] = ["карт", "адаптер", "adapter", "сеть"],

        // --- приватность ---
        ["телеметри"] = ["телеметри", "telemetry", "шпион", "spy", "слеж", "приватн", "privacy", "ceip", "diagtrack"],
        ["telemetry"] = ["telemetry", "телеметри", "spy", "privacy"],
        ["шпион"] = ["шпион", "spy", "телеметри", "telemetry", "слеж"],
        ["spy"] = ["spy", "шпион", "телеметри", "telemetry"],
        ["слеж"] = ["слеж", "шпион", "spy", "телеметри", "telemetry"],
        ["приватн"] = ["приватн", "privacy", "телеметри", "telemetry"],
        ["privacy"] = ["privacy", "приватн", "телеметри", "telemetry"],
        ["реклам"] = ["реклам", "ads", "совет", "tips", "уведомлен", "notification"],
        ["ads"] = ["ads", "реклам", "совет", "tips"],
        ["совет"] = ["совет", "tips", "реклам", "уведомлен"],
        ["tips"] = ["tips", "совет", "реклам"],
        ["уведомлен"] = ["уведомлен", "notification", "совет"],
        ["notification"] = ["notification", "уведомлен", "совет"],
        ["uwp"] = ["uwp", "фонов", "background"],
        ["фонов"] = ["фонов", "background", "uwp"],
        ["background"] = ["background", "фонов", "uwp"],
        ["copilot"] = ["copilot", "фильтр", "bing"],

        // --- интерфейс ---
        ["проводник"] = ["проводник", "explorer", "file explorer"],
        ["explorer"] = ["explorer", "проводник"],
        ["контекст"] = ["контекст", "context menu", "меню"],
        ["context menu"] = ["context menu", "контекст", "меню"],
        ["меню"] = ["меню", "menu", "пуск", "start", "контекст", "задержк"],
        ["пуск"] = ["пуск", "start", "меню", "menu"],
        ["задержк"] = ["задержк", "delay", "menushowdelay", "меню"],
        ["delay"] = ["delay", "задержк", "menu"],
        ["панел"] = ["панел", "taskbar", "задач"],
        ["taskbar"] = ["taskbar", "панел", "задач"],
        ["галере"] = ["галере", "gallery"],
        ["gallery"] = ["gallery", "галере"],
        ["скрыт"] = ["скрыт", "hidden"],
        ["hidden"] = ["hidden", "скрыт"],
        ["расширен"] = ["расширен", "extension"],
        ["extension"] = ["extension", "расширен"],
        ["onedrive"] = ["onedrive"],
        ["прозрачн"] = ["прозрачн", "transparen", "aero"],
        ["transparen"] = ["transparen", "прозрачн"],
        ["эскиз"] = ["эскиз", "thumbnail", "значк", "icons"],
        ["thumbnail"] = ["thumbnail", "эскиз"],
        ["значк"] = ["значк", "icons", "эскиз"],
        ["тени"] = ["тени", "shadow"],
        ["shadow"] = ["shadow", "тени"],
        ["анимац"] = ["анимац", "animation", "эффект", "effect", "visual"],
        ["animation"] = ["animation", "анимац", "effect", "эффект"],
        ["эффект"] = ["эффект", "effect", "animation", "анимац"],
        ["effect"] = ["effect", "эффект", "animation"],
        ["перезапуск"] = ["перезапуск", "restart", "explorer", "проводник"],
        ["restart"] = ["restart", "перезапуск", "explorer"],

        // --- обслуживание и прочее ---
        ["служб"] = ["служб", "service", "бэкап", "backup"],
        ["service"] = ["service", "служб", "backup"],
        ["бэкап"] = ["бэкап", "backup", "резерв", "restore", "откат"],
        ["backup"] = ["backup", "бэкап", "резерв", "restore"],
        ["резерв"] = ["резерв", "backup", "бэкап", "restore", "откат"],
        ["restore"] = ["restore", "откат", "восстанов", "резерв", "backup"],
        ["откат"] = ["откат", "restore", "восстанов", "резерв"],
        ["восстанов"] = ["восстанов", "restore", "точк", "резерв"],
        ["задач"] = ["задач", "task", "планировщ", "scheduler"],
        ["task"] = ["task", "задач", "планировщ", "scheduler"],
        ["планировщ"] = ["планировщ", "scheduler", "task", "задач"],
        ["scheduler"] = ["scheduler", "планировщ", "task"],
        ["точк"] = ["точк", "restore point", "восстанов"],
        ["dism"] = ["dism", "sfc", "целостн", "integrity"],
        ["sfc"] = ["sfc", "dism", "целостн"],
        ["целостн"] = ["целостн", "integrity", "dism", "sfc"],
        ["integrity"] = ["integrity", "целостн", "dism", "sfc"],
        ["winsxs"] = ["winsxs", "хранилищ", "component store", "resetbase"],
        ["хранилищ"] = ["хранилищ", "winsxs", "component store"],
        ["resetbase"] = ["resetbase", "winsxs", "хранилищ"],
        ["индексац"] = ["индексац", "index", "поиск", "search"],
        ["index"] = ["index", "индексац", "поиск"],
        ["поиск"] = ["поиск", "search", "индексац", "index"],
        ["search"] = ["search", "поиск", "index"],
        ["дамп"] = ["дамп", "dump", "wer", "ошибк"],
        ["dump"] = ["dump", "дамп", "wer"],
        ["wer"] = ["wer", "дамп", "dump"],
        ["uac"] = ["uac", "контроль", "учётных", "учетных", "защит", "admin"],
        ["контроль"] = ["контроль", "uac", "учётных", "учетных"],
        ["учётных"] = ["учётных", "uac", "контроль"],
        ["учетных"] = ["учетных", "uac", "контроль"],
        ["защит"] = ["защит", "uac", "secure", "контроль"],
        ["журнал"] = ["журнал", "log", "лог"],
        ["лог"] = ["лог", "log", "журнал"],
        ["edge"] = ["edge", "браузер", "browser", "ускорен"],
        ["версия"] = ["версия", "version", "about", "о приложении"],
        ["about"] = ["about", "о приложении", "версия"],
        ["о приложении"] = ["о приложении", "about", "версия"],
    };

    // Разбор запроса на слова.
    private static string[] SplitTokens(string query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Слово совпало, если оно есть в тексте утилиты либо через словарь синонимов.
    private static bool MatchesToken(string token, string searchText)
    {
        if (searchText.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var (key, stems) in SearchSynonyms)
        {
            var keyMatch = token.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                || (token.Length >= 3 && key.StartsWith(token, StringComparison.OrdinalIgnoreCase));
            if (!keyMatch)
            {
                continue;
            }

            foreach (var stem in stems)
            {
                if (searchText.Contains(stem, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Matches(BatchUtilityRow row, string[] tokens)
    {
        var searchText = row.Title + " " + row.Utility.Keywords;
        return tokens.Length == 0 || tokens.All(token => MatchesToken(token, searchText));
    }

    // Применение фильтра: строки и пустые группы скрываются (IsVisible),
    // совпавшие утилиты попадают в подсказки под полем поиска.
    private void ApplySearch()
    {
        var tokens = SplitTokens(SearchText);
        FilteredGroups.Clear();
        Suggestions.Clear();
        foreach (var group in Groups)
        {
            var any = false;
            foreach (var row in group.Rows)
            {
                var visible = Matches(row, tokens);
                row.IsVisible = visible;
                any |= visible;
            }

            group.IsVisible = any;
            if (any)
            {
                FilteredGroups.Add(group);
            }
        }

        if (tokens.Length > 0)
        {
            foreach (var row in Rows)
            {
                if (!Matches(row, tokens))
                {
                    continue;
                }

                Suggestions.Add(new SearchSuggestion(row.Title, row.TargetText, row.Utility.Section));
                if (Suggestions.Count >= MaxSuggestions)
                {
                    break;
                }
            }
        }

        IsSearchSuggestionsOpen = IsSearchFocused && Suggestions.Count > 0;
    }

    // Максимум подсказок в выпадающем списке (дальше — уточнить запрос).
    private const int MaxSuggestions = 8;

    // ===================== Реестр утилит =====================

    private void BuildUtilities()
    {
        var utilities = new List<BatchUtility>();

        utilities.AddRange(BuildCleanup());
        utilities.AddRange(BuildPrivacy());
        utilities.AddRange(BuildServices());
        utilities.AddRange(BuildTasks());
        utilities.AddRange(BuildPower());
        utilities.AddRange(BuildNetwork());
        utilities.AddRange(BuildUi());
        utilities.AddRange(BuildInput());
        utilities.AddRange(BuildMaintenance());
        utilities.AddRange(BuildSecurity());
        utilities.AddRange(BuildUpdate());

        // Дефолтный «безопасный» набор, пока пользователь не собрал свой.
        // Чтение batch.json из конструктора убрано: сохранённый набор применяется
        // асинхронно в InitializeAsync (до первого показа окна) — без IO на UI-потоке.
        string[] defaults =
        [
            "clean_temp",
            "clean_browsers",
            "clean_update_cache",
            "maint_do_cache",
            "ui_menu_delay_20",
            "row_game-bar",
            "row_game-dvr",
            "row_game-mode",
            "update_drivers_exclude"
        ];
        foreach (var sectionGroup in utilities.GroupBy(u => u.Section).OrderBy(g => g.Key))
        {
            var group = new BatchGroup(sectionGroup.Key);
            foreach (var utility in sectionGroup)
            {
                group.Rows.Add(new BatchUtilityRow(utility, defaults.Contains(utility.Id)));
            }

            Groups.Add(group);
        }

        Rows = Groups.SelectMany(g => g.Rows).ToList();
    }

    // Кнопка без параметров: CanExecute=false → пропуск (null).
    private static async Task<string?> RunAsync(IAsyncRelayCommand command, Func<string> status)
    {
        if (!command.CanExecute(null))
        {
            return null;
        }

        await command.ExecuteAsync(null).ConfigureAwait(true);
        return status();
    }

    // Тумблерная утилита: направленно привести к целевому состоянию и вернуть статус раздела.
    private static async Task<string?> RunSwitchAsync(
        IAsyncRelayCommand command,
        Action setTarget,
        Func<string> status)
    {
        if (!command.CanExecute(null))
        {
            return null;
        }

        setTarget();
        await command.ExecuteAsync(null).ConfigureAwait(true);
        return status();
    }

    private List<BatchUtility> BuildCleanup() =>
    [
        Utility(3, "clean_recyclebin", "S_CleanupRecycleBin",
            () => RunAsync(_cleanup.CleanRecycleBinCommand, () => _cleanup.StatusText),
            keywords: "очист корзин recycle bin clean"),
        Utility(3, "clean_temp", "S_CleanupTemp",
            () => RunAsync(_cleanup.CleanTempCommand, () => _cleanup.StatusText),
            keywords: "temp временн очист clean prefetch"),
        Utility(3, "clean_browsers", "S_CleanupBrowsers",
            () => RunAsync(_cleanup.CleanBrowsersCommand, () => _cleanup.StatusText),
            keywords: "браузер browser chrome edge opera firefox яндекс кэш"),
        Utility(3, "clean_update_cache", "S_CleanupUpdateCache",
            () => RunAsync(_cleanup.CleanUpdateCacheCommand, () => _cleanup.StatusText),
            keywords: "обновл update softwaredistribution кэш cache"),
    ];

    private List<BatchUtility> BuildPrivacy()
    {
        var list = new List<BatchUtility>();

        // Категории приватности — тумблеры: цель = защита включена (категория отключена).
        // «Все сразу» в список не входит: оно дублирует все категории разом.
        foreach (var row in _privacy.Rows)
        {
            var categoryRow = row;
            list.Add(new BatchUtility
            {
                Id = "privacy_" + categoryRow.Category.Id,
                TitleKey = categoryRow.Category.Title,
                IsRawTitle = true,
                TargetText = "S_BatchTargetOn",
                Section = 5,
                Keywords = "телеметри telemetry privacy приватн слеж шпион spy",
                NeedsApply = () => !categoryRow.IsEnabled,
                Run = async () =>
                {
                    if (!_privacy.ToggleCategoryCommand.CanExecute(categoryRow))
                    {
                        return null;
                    }

                    categoryRow.IsEnabled = true;
                    await _privacy.ToggleCategoryCommand.ExecuteAsync(categoryRow).ConfigureAwait(true);
                    return _privacy.StatusText;
                }
            });
        }

        return list;
    }

    private List<BatchUtility> BuildServices() =>
    [
        Utility(6, "svc_backup", "S_Backup",
            () => RunAsync(_services.BackupCommand, () => _services.StatusText),
            keywords: "служб service резерв бэкап backup"),
        Utility(6, "svc_restore", "S_Restore",
            () => RunAsync(_services.RestoreCommand, () => _services.StatusText),
            keywords: "служб service откат restore восстанов"),
    ];

    private List<BatchUtility> BuildTasks() =>
    [
        Utility(15, "tasks_backup", "S_Backup",
            () => RunAsync(_tasks.BackupCommand, () => _tasks.StatusText),
            keywords: "задач планировщ scheduler task резерв backup"),
        Utility(15, "tasks_restore", "S_Rollback",
            () => RunAsync(_tasks.RestoreCommand, () => _tasks.StatusText),
            keywords: "задач планировщ task откат restore"),
    ];

    private List<BatchUtility> BuildPower()
    {
        return
        [
            Utility(8, "power_hibernation_off", "S_BatchHibernationOff",
                () => RunSwitchAsync(_power.ToggleHibernationCommand,
                    () => _power.HibernationEnabled = false, () => _power.StatusText),
                needsApply: () => _power.HibernationEnabled),
            Utility(8, "power_fastboot_on", "S_BatchFastBootOn",
                () => RunSwitchAsync(_power.ToggleFastBootCommand,
                    () => _power.FastBootEnabled = true, () => _power.StatusText),
                needsApply: () => !_power.FastBootEnabled),
            Utility(8, "power_memcompression_on", "S_BatchMemCompressionOn",
                () => RunSwitchAsync(_power.ToggleMemoryCompressionCommand,
                    () => _power.MemoryCompressionEnabled = true, () => _power.StatusText),
                needsApply: () => !_power.MemoryCompressionEnabled),
            Utility(8, "power_sysmain_off", "S_BatchSysMainOff",
                () => RunSwitchAsync(_power.ToggleSysMainCommand,
                    () => _power.SysMainEnabled = false, () => _power.StatusText),
                needsApply: () => _power.SysMainEnabled),
            Utility(8, "power_shortnames_off", "S_BatchShortNamesOff",
                () => RunSwitchAsync(_power.ToggleShortNamesCommand,
                    () => _power.ShortNamesEnabled = false, () => _power.StatusText),
                needsApply: () => _power.ShortNamesEnabled),
            Utility(8, "power_lastaccess_off", "S_BatchLastAccessOff",
                () => RunSwitchAsync(_power.ToggleLastAccessCommand,
                    () => _power.LastAccessEnabled = false, () => _power.StatusText),
                needsApply: () => _power.LastAccessEnabled),
            Utility(8, "power_prefetcher_off", "S_BatchPrefetcherOff",
                () => RunSwitchAsync(_power.TogglePrefetcherCommand,
                    () => _power.PrefetcherEnabled = false, () => _power.StatusText),
                needsApply: () => _power.PrefetcherEnabled),
            Utility(8, "power_pagefile_system", "S_PowerPageFileSystemManaged",
                () => RunAsync(_power.SetSystemManagedPageFileCommand, () => _power.StatusText),
                needsApply: () => _power.ActivePageFileKey != "system"),
            Utility(8, "power_pagefile_6144", "S_PowerPageFile4",
                () => RunPageFile("6144", () => _power.StatusText),
                needsApply: () => _power.ActivePageFileKey != "6144"),
            Utility(8, "power_pagefile_8192", "S_PowerPageFile8",
                () => RunPageFile("8192", () => _power.StatusText),
                needsApply: () => _power.ActivePageFileKey != "8192"),
            Utility(8, "power_pagefile_4096", "S_PowerPageFile16",
                () => RunPageFile("4096", () => _power.StatusText),
                needsApply: () => _power.ActivePageFileKey != "4096"),
            Utility(8, "power_pagefile_2048", "S_PowerPageFile32",
                () => RunPageFile("2048", () => _power.StatusText),
                needsApply: () => _power.ActivePageFileKey != "2048"),
            Utility(8, "power_cpu_clear", "S_PowerCpuClear",
                () => RunAsync(_power.ClearCpuLimitsCommand, () => _power.StatusText)),
        ];
    }

    private async Task<string?> RunPageFile(string sizeMb, Func<string> status)
    {
        if (!_power.SetPageFileCommand.CanExecute(sizeMb))
        {
            return null;
        }

        await _power.SetPageFileCommand.ExecuteAsync(sizeMb).ConfigureAwait(true);
        return status();
    }

    private List<BatchUtility> BuildNetwork()
    {
        return
        [
            Utility(9, "net_reset_all", "S_NetResetAll",
                () => RunAsync(_network.ResetAllCommand, () => _network.StatusText)),
            Utility(9, "net_gaming_apply", "S_NetGamingProfile",
                () => RunAsync(_network.GamingProfileCommand, () => _network.StatusText),
                needsApply: () => !_network.IsGamingActive),
            Utility(9, "net_gaming_rollback", "S_Rollback",
                () => RunAsync(_network.RollbackGamingProfileCommand, () => _network.StatusText)),
            Utility(9, "net_adapter_apply", "S_NetAdapterProfileTitle",
                () => RunAsync(_network.ApplyUniversalAdapterProfileCommand, () => _network.StatusText)),
            Utility(9, "net_adapter_restore", "S_Rollback",
                () => RunAsync(_network.RestoreAdapterProfileCommand, () => _network.StatusText)),
            Utility(9, "net_autotuning_normal", "S_BatchAutoTuningNormal",
                async () =>
                {
                    if (!_network.ApplyAutoTuningCommand.CanExecute(null))
                    {
                        return null;
                    }

                    _network.SelectedAutoTuning = "normal";
                    await _network.ApplyAutoTuningCommand.ExecuteAsync(null).ConfigureAwait(true);
                    return _network.StatusText;
                },
                needsApply: () => _network.SelectedAutoTuning != "normal"),
            Utility(9, "net_ecn_default", "S_BatchEcnDefault",
                async () =>
                {
                    if (!_network.ApplyEcnCommand.CanExecute(null))
                    {
                        return null;
                    }

                    _network.SelectedEcn = "default";
                    await _network.ApplyEcnCommand.ExecuteAsync(null).ConfigureAwait(true);
                    return _network.StatusText;
                },
                needsApply: () => _network.SelectedEcn != "default"),
            Utility(9, "net_mtu_restore", "S_NetMtuRestore",
                () => RunAsync(_network.RestoreMtuCommand, () => _network.StatusText)),
            Utility(9, "net_qos_zero", "S_NetQosZero",
                () => RunAsync(_network.QosZeroCommand, () => _network.StatusText),
                needsApply: () => _network.ActiveQosKey != "0"),
            Utility(9, "net_qos_twenty", "S_NetQosTwenty",
                () => RunAsync(_network.QosTwentyCommand, () => _network.StatusText),
                needsApply: () => _network.ActiveQosKey != "20"),
            Utility(9, "net_qos_remove", "S_NetQosRemove",
                () => RunAsync(_network.QosRemoveCommand, () => _network.StatusText),
                needsApply: () => _network.ActiveQosKey != "none"),
            Utility(9, "net_qos_restore", "S_NetQosRestore",
                () => RunAsync(_network.QosRestoreCommand, () => _network.StatusText)),
            Utility(9, "net_netbios_disable", "S_NetNetBiosDisable",
                () => RunAsync(_network.NetBiosDisableCommand, () => _network.StatusText),
                needsApply: () => _network.ActiveNetBiosKey != "disable"),
            Utility(9, "net_netbios_dhcp", "S_NetNetBiosDhcp",
                () => RunAsync(_network.NetBiosDhcpCommand, () => _network.StatusText),
                needsApply: () => _network.ActiveNetBiosKey != "dhcp"),
            Utility(9, "net_netbios_flush", "S_NetNetBiosFlush",
                () => RunAsync(_network.FlushNetBiosCacheCommand, () => _network.StatusText)),
        ];
    }

    private List<BatchUtility> BuildUi()
    {
        var list = new List<BatchUtility>
        {
            Utility(10, "ui_menu_delay_20", "S_UiMenuDelay20",
                () => RunMenuDelay("20"),
                needsApply: () => _ui.ActiveMenuDelayKey != "20"),
            Utility(10, "ui_menu_delay_400", "S_UiMenuDelay400",
                () => RunMenuDelay("400"),
                needsApply: () => _ui.ActiveMenuDelayKey != "400"),
            Utility(10, "ui_restart_explorer", "S_UiRestartExplorer",
                () => RunAsync(_ui.RestartExplorerCommand, () => _ui.StatusText)),
            Utility(10, "ui_clear_taskbar", "S_UiClearTaskbar",
                () => RunAsync(_ui.ClearTaskbarCommand, () => _ui.StatusText)),
            Utility(10, "ui_restore_taskbar", "S_UiRestoreTaskbar",
                () => RunAsync(_ui.RestoreTaskbarCommand, () => _ui.StatusText)),
        };

        // Тумблеры проводника и визуальных эффектов: цель — из карты направлений.
        list.AddRange(UiSwitchRows(_ui.ExplorerRows));
        list.AddRange(UiSwitchRows(_ui.VisualFxRows));
        list.Add(Utility(10, "ui_recommended_off", "S_BatchRecommendedOff",
            async () =>
            {
                if (!_ui.ToggleRecommendedCommand.CanExecute(null))
                {
                    return null;
                }

                _ui.RecommendedRow.IsOn = false;
                await _ui.ToggleRecommendedCommand.ExecuteAsync(null).ConfigureAwait(true);
                return _ui.StatusText;
            }));

        return list;
    }

    // Целевое состояние тумблеров раздела «Интерфейс»: оптимизаторский набор —
    // функциональные удобства включаем, «шум» и визуальные эффекты отключаем.
    private static bool UiSwitchTarget(SwitchRow row) => row.Id switch
    {
        "open-to-this-pc" => true,
        "home-button" => true,
        "gallery-button" => false,
        "network-button" => false,
        "recycle-nav" => true,
        "recycle-desktop" => true,
        "compact-view" => true,
        "recent-files" => false,
        "classic-context-menu" => false,
        "show-file-extensions" => true,
        "show-hidden-files" => false,
        "full-path" => false,
        "item-checkboxes" => false,
        "onedrive-nav" => false,
        "animations" => false,
        "transparency" => false,
        "thumbnails" => false,
        "shadows" => false,
        _ => false
    };

    private IEnumerable<BatchUtility> UiSwitchRows(IEnumerable<SwitchRow> rows) =>
        SwitchRows(rows, 10, UiSwitchTarget);

    private async Task<string?> RunMenuDelay(string milliseconds)
    {
        if (!_ui.SetMenuDelayCommand.CanExecute(milliseconds))
        {
            return null;
        }

        await _ui.SetMenuDelayCommand.ExecuteAsync(milliseconds).ConfigureAwait(true);
        return _ui.StatusText;
    }

    private List<BatchUtility> BuildInput()
    {
        var list = new List<BatchUtility>();

        // Тумблеры ввода: ускорение мыши/залипание/Edge-фон/Game Bar/DVR — off, Game Mode — on.
        static bool InputTarget(SwitchRow row) => row.Id switch
        {
            "game-mode" => true,
            _ => false
        };
        list.AddRange(SwitchRows(_input.Rows, 11, InputTarget));

        list.Add(Utility(11, "input_gamebar_dvr_off", "S_BatchGameBarDvrOff",
            () => RunAsync(_input.DisableGameBarDvrCommand, () => _input.StatusText)));

        return list;
    }

    private List<BatchUtility> BuildMaintenance() =>
    [
        Utility(12, "maint_integrity", "S_MaintIntegrityRun",
            () => RunAsync(_maintenance.RunIntegrityCheckCommand, () => _maintenance.StatusText)),
        Utility(12, "maint_rp_create", "S_MaintRpCreate",
            () => RunAsync(_maintenance.CreateRestorePointCommand, () => _maintenance.StatusText)),
        Utility(12, "maint_winsxs_analyze", "S_MaintWinSxsAnalyze",
            () => RunAsync(_maintenance.AnalyzeComponentStoreCommand, () => _maintenance.StatusText)),
        Utility(12, "maint_winsxs_cleanup", "S_MaintWinSxsCleanup",
            () => RunAsync(_maintenance.StartComponentCleanupCommand, () => _maintenance.StatusText)),
        Utility(12, "maint_winsxs_resetbase", "S_MaintWinSxsResetBase",
            () => RunAsync(_maintenance.StartComponentCleanupResetBaseCommand, () => _maintenance.StatusText)),
        Utility(12, "maint_do_cache", "S_MaintDoCache",
            () => RunAsync(_maintenance.ClearDeliveryOptimizationCommand, () => _maintenance.StatusText)),
        Utility(12, "maint_dumps_wer", "S_MaintDumpsWer",
            () => RunAsync(_maintenance.ClearDumpsAndWerCommand, () => _maintenance.StatusText)),
        Utility(12, "maint_compactos_off", "S_BatchCompactOsOff",
            () => RunSwitchAsync(_maintenance.ToggleCompactOsCommand,
                () => _maintenance.CompactOsEnabled = false, () => _maintenance.StatusText),
            needsApply: () => _maintenance.CompactOsEnabled),
    ];

    private List<BatchUtility> BuildSecurity() =>
    [
        Utility(13, "security_uac_standard", "S_BatchUacStandardOn",
            () => RunSwitchAsync(_security.SetStandardCommand,
                () => _security.UacStandard = true, () => _security.StatusText),
            needsApply: () => !_security.UacStandard),
    ];

    private List<BatchUtility> BuildUpdate() =>
    [
        Utility(16, "update_block", "S_WuBlock",
            () => RunAsync(_update.BlockUpdatesCommand, () => _update.StatusText),
            needsApply: () => _update.BlockUpdatesCommand.CanExecute(null)),
        Utility(16, "update_unblock", "S_WuUnblock",
            () => RunAsync(_update.UnblockUpdatesCommand, () => _update.StatusText),
            needsApply: () => _update.UnblockUpdatesCommand.CanExecute(null)),
        Utility(16, "update_pause7", "S_WuPause7",
            () => RunAsync(_update.PauseUpdatesCommand, () => _update.StatusText),
            needsApply: () => _update.PauseUpdatesCommand.CanExecute(null)),
        Utility(16, "update_unpause", "S_WuUnpause",
            () => RunAsync(_update.UnpauseCommand, () => _update.StatusText),
            needsApply: () => _update.UnpauseCommand.CanExecute(null)),
        Utility(16, "update_drivers_exclude", "S_BatchDriversExclude",
            () => RunSwitchAsync(_update.ToggleDriverExclusionCommand,
                () => _update.DriverUpdatesExcluded = true, () => _update.StatusText),
            needsApply: () => !_update.DriverUpdatesExcluded),
    ];

    // Тумблерные строки UI/Input: одна утилита на строку, направление — из selector.
    private IEnumerable<BatchUtility> SwitchRows(
        IEnumerable<SwitchRow> rows,
        int section,
        Func<SwitchRow, bool> targetSelector)
    {
        foreach (var row in rows)
        {
            var switchRow = row;
            var target = targetSelector(row);
            var toggleCommand = section == 10 ? _ui.ToggleSwitchCommand : _input.ToggleSwitchCommand;
            var status = () => section == 10 ? _ui.StatusText : _input.StatusText;
            yield return new BatchUtility
            {
                Id = "row_" + switchRow.Id,
                TitleKey = switchRow.Title,
                IsRawTitle = true,
                TargetText = target ? "S_BatchTargetOn" : "S_BatchTargetOff",
                Section = section,
                NeedsApply = () => switchRow.IsOn != target,
                Run = async () =>
                {
                    if (!toggleCommand.CanExecute(switchRow))
                    {
                        return null;
                    }

                    switchRow.IsOn = target;
                    await toggleCommand.ExecuteAsync(switchRow).ConfigureAwait(true);
                    return status();
                }
            };
        }
    }

    private static BatchUtility Utility(
        int section,
        string id,
        string titleKey,
        Func<Task<string?>> run,
        string keywords = "",
        Func<bool?>? needsApply = null) =>
        new() { Id = id, TitleKey = titleKey, Section = section, Run = run, Keywords = keywords, NeedsApply = needsApply };
}

// Подсказка поиска: утилита с номером раздела-владельца. Заголовок раздела
// берётся из словаря на момент построения списка (ApplySearch перечитывает
// его при смене языка).
public sealed class SearchSuggestion
{
    public SearchSuggestion(string title, string target, int sectionNumber)
    {
        Title = title;
        Target = target;
        SectionNumber = sectionNumber;
        SectionTitle =
            Application.Current?.TryFindResource($"S_Section{sectionNumber:00}_Title") as string ?? string.Empty;
    }

    public string Title { get; }

    public string Target { get; }

    public int SectionNumber { get; }

    public string SectionTitle { get; }

    public bool HasTarget => !string.IsNullOrEmpty(Target);
}

// Строка чек-листа сканирования: область и текущее состояние.
