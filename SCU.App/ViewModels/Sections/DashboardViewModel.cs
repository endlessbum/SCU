using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Models;

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

    // Клавиатурный фокус в поле поиска (ставит DashboardView): пока он там,
    // раскрыты подсказки (затемнение фона окна убрано).
    [ObservableProperty]
    private bool _isSearchFocused;

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

    // Признак «раздел уже инициализировался и читал систему». Заполняется из
    // MainViewModel (_initializedSections); до установки — true, чтобы счётчик
    // и другие вызовы вели себя как раньше (например, в тестах).
    internal Func<int, bool> SectionLoaded { get; set; } = _ => true;

    public bool CanRunBatch => !IsRunning && Rows.Any(row => row.IsIncluded);

    public string ReadOnlyHint =>
        IsAdmin ? string.Empty : L.T("Нужны права администратора — применение пакета недоступно.");

    public ObservableCollection<BatchGroup> Groups { get; } = [];

    // Скрытые группы поиском остаются в Groups: ItemsControl привязан к нему и
    // не пересоздаёт карточки на каждый символ запроса — фильтр переключает
    // только IsVisible у строк и групп.
    public bool HasVisibleGroups => Groups.Any(group => group.IsVisible);

    public IReadOnlyList<BatchUtilityRow> Rows { get; private set; } = [];

    // ===================== Реестр утилит для «Редактирования меню» =====================

    // Полный реестр (id → утилита), БЕЗ удалённых; секция отражает перемещения.
    // Кэш пересобирается в RebuildSearchRows (и при DeleteUtility).
    public IReadOnlyDictionary<string, BatchUtility> UtilitiesById => _utilitiesById;

    private List<BatchUtility> _allUtilities = [];
    private HashSet<string> _deletedUtilIds = [];
    private Dictionary<string, BatchUtility> _utilitiesById = new(StringComparer.Ordinal);

    // Полный реестр утилит (единственная истина о функциях SCU) — наружу
    // отдаётся read-only: его использует AI-слой (capabilities) вместо
    // создания собственного реестра (п. 5 ТЗ: нет двух источников истины).
    public IReadOnlyList<BatchUtility> Utilities => _allUtilities;

    // Утилиты главной страницы («Настройка списка») удалению не подлежат.
    public static bool IsProtectedUtility(BatchUtility utility) => utility.Section == 3;

    public BatchUtility? GetUtility(string id) =>
        UtilitiesById.GetValueOrDefault(id);

    // Удаление встроенной утилиты: исчезает из поиска и из палитр выбора.
    public bool DeleteUtility(string id)
    {
        var utility = _allUtilities.FirstOrDefault(u => u.Id == id);
        if (utility is null || IsProtectedUtility(utility) || !_deletedUtilIds.Add(id))
        {
            return false;
        }

        RebuildSearchRows();
        return true;
    }

    // Перемещение утилиты в пользовательскую вкладку: поиск находит её там.
    public void SetUtilitySection(string id, int section)
    {
        if (_allUtilities.FirstOrDefault(u => u.Id == id) is { } utility)
        {
            utility.Section = section;
            RebuildSearchRows();
        }
    }

    // Возврат утилиты в родной раздел (сброс пользовательской вкладки/меню).
    public void ResetUtilitySection(string id)
    {
        if (_allUtilities.FirstOrDefault(u => u.Id == id) is { } utility
            && utility.Section != utility.OriginalSection)
        {
            utility.Section = utility.OriginalSection;
            RebuildSearchRows();
        }
    }

    // Полный сброс реестра к заводскому состоянию (сброс меню): вернуть все
    // перемещённые утилиты и восстановить удалённые.
    public void ResetRegistry()
    {
        _deletedUtilIds.Clear();
        foreach (var utility in _allUtilities)
        {
            utility.Section = utility.OriginalSection;
        }

        RebuildSearchRows();
    }

    private void RebuildSearchRows()
    {
        // Строки поиска переиспользуются по id: строки подписаны на статическое
        // L.LanguageChanged без отписки — пересоздание на каждую правку меню
        // накапливало бы мёртвые обработчики.
        _utilitiesById = _allUtilities
            .Where(u => !_deletedUtilIds.Contains(u.Id))
            .ToDictionary(u => u.Id, u => u, StringComparer.Ordinal);

        var actualIds = new HashSet<string>(_utilitiesById.Keys, StringComparer.Ordinal);
        var staleIds = _searchRowMap.Keys.Where(id => !actualIds.Contains(id)).ToList();
        foreach (var staleId in staleIds)
        {
            _searchRowMap.Remove(staleId);
        }

        foreach (var utility in _utilitiesById.Values)
        {
            if (!_searchRowMap.ContainsKey(utility.Id))
            {
                _searchRowMap[utility.Id] = new BatchUtilityRow(utility, isIncluded: false);
            }
        }

        _searchRows = _utilitiesById.Values
            .Select(utility => _searchRowMap[utility.Id])
            .ToList();
        ApplySearch();
    }

    private readonly Dictionary<string, BatchUtilityRow> _searchRowMap = new(StringComparer.Ordinal);

    // Заголовок раздела для подсказок поиска (в т.ч. пользовательских вкладок);
    // назначается MainViewModel после построения Sections.
    public Func<int, string>? SectionTitleResolver { get; set; }

    // Загрузка сохранённого списка включённых утилит (вызывается из InitializeAsync).
    // Пустой файл — «ещё не сохраняли»: остаётся дефолтный набор из реестра.
    // Плюс: последний снимок из state\snapshots.json — рекомендации, статистика,
    // тренды и diff видны сразу после запуска, без обязательной проверки.
    public async Task InitializeAsync()
    {
        var included = await TaskRunner.RunBlocking(_stateStore.Load, CancellationToken.None)
            .ConfigureAwait(true);
        if (included is not null)
        {
            // Сохранённый набор: применяем пересечение с текущим списком
            // (только очистка); выпавшие идентификаторы фиксируются в журнале,
            // а сам batch.json не перезаписывается до действия пользователя.
            ApplyIncludedIds(included);
            var dropped = included.Where(id => Rows.All(row => row.Utility.Id != id)).ToList();
            if (dropped.Count > 0)
            {
                _logger.Warn("BATCH | saved utilities not in quick-cleanup list: "
                             + string.Join(", ", dropped));
            }
        }

        RecountIncluded();
    }

    partial void OnBatchSwitchOnChanged(bool value)
    {
        if (value)
        {
            if (IsRunning)
            {
                // П. №8 аудита: ранее запрос ON во время завершения предыдущего
                // пакета молча отбрасывался finally (BatchSwitchOn = false).
                // Запоминаем и запускаем пакет после завершения текущего.
                _rerunRequested = true;
                return;
            }

            TaskRunner.RunAndForget(RunBatchAsync(), _logger, "batch apply");
        }
        else if (IsRunning)
        {
            // Ручное выключение посреди пакета: отменяем оставшиеся операции
            // и отложенный запуск. Иначе тумблер уже в OFF, а утилиты
            // продолжают применяться — интерфейс врёт о состоянии.
            _rerunRequested = false;
            CancelOngoing();
        }
    }

    private bool _rerunRequested;

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
                _currentRunningSection = row.Utility.OriginalSection;
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

            // Отменённый пакет тоже менял систему — пишем в историю с честным статусом.
            await _history.RecordAsync(new HistoryEvent(
                DateTime.Now,
                L.T("Оптимизация"),
                L.T("Пакетное применение ({0} утилит)", executed),
                cancelled ? HistoryEvent.StatusFail : HistoryEvent.StatusOk,
                L.T("Включено: {0}, выполнено: {1}.", included.Count, executed))).ConfigureAwait(true);
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
            var rerun = _rerunRequested;
            _rerunRequested = false;
            if (rerun)
            {
                // П. №8: отложенный запрос ON — повторное включение тумблера
                // запускает новый пакет штатным путём через OnBatchSwitchOnChanged.
                BatchSwitchOn = true;
            }
            else
            {
                // Авто-возврат в OFF: выключатель — «Apply», а не постоянное состояние.
                BatchSwitchOn = false;
            }

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
    // стартового refresh) — обращений к системе нет. Неинициализированные
    // разделы не считаются: их состояния — конструкторские дефолты.
    public void RecomputePending()
    {
        var pending = PendingCounter.Count(
            Rows.Select(r => (r.IsIncluded, r.Utility.Section, r.Utility.NeedsApply)),
            SectionLoaded);
        PendingText = pending > 0 ? L.T("К применению: {0}", pending) : string.Empty;
    }

    // ===================== Поиск утилит =====================




    // Применение фильтра: строки и пустые группы скрываются (IsVisible),
    // совпавшие утилиты попадают в подсказки под полем поиска. Контейнеры списка
    // не пересоздаются (Clear+Add групп заставлял ItemsControl на каждый символ
    // заново генерировать все ~100 карточек утилит) — только переключение видимости.
    private void ApplySearch()
    {
        var tokens = DashboardSearchService.SplitTokens(SearchText);
        Suggestions.Clear();
        foreach (var group in Groups)
        {
            var any = false;
            foreach (var row in group.Rows)
            {
                // Шапочные кнопки («Бэкап», «Откатить», «Сбросить») поиском не
                // находятся: при пустом запросе видны, при активном — скрыты.
                var visible = tokens.Length == 0
                    ? true
                    : row.ShowInSearch && DashboardSearchService.MatchesText(row.SearchText, tokens);
                row.IsVisible = visible;
                any |= visible;
            }

            group.IsVisible = any;
        }

        OnPropertyChanged(nameof(HasVisibleGroups));

        if (tokens.Length > 0)
        {
            // Встроенные утилиты и строки пользовательских скриптов в одном
            // потоке подсказок: иначе при заполненном лимите скрипты бы
            // никогда не показывались.
            foreach (var row in _searchRows.Concat(_scriptRows))
            {
                if (!row.ShowInSearch || !DashboardSearchService.MatchesText(row.SearchText, tokens))
                {
                    continue;
                }

                Suggestions.Add(new SearchSuggestion(row.Title, row.TargetText, row.Utility.Section, SectionTitleResolver));
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

        // Родной раздел каждой утилиты — для возврата при сбросе меню
        // и удалении пользовательских вкладок.
        foreach (var utility in utilities)
        {
            utility.OriginalSection = utility.Section;
        }

        // Список на «Главной» (переключатель быстрой очистки): только утилиты,
        // со временем накапливающие мусор — раздел «Очистка» (Корзина, Temp,
        // кэши браузеров и обновлений Windows).
        var listUtilities = utilities.Where(u => u.Section == 3).ToList();

        // Дефолтный набор, пока пользователь не собрал свой.
        // Чтение batch.json из конструктора убрано: сохранённый набор применяется
        // асинхронно в InitializeAsync (до первого показа окна) — без IO на UI-потоке.
        string[] defaults =
        [
            "clean_temp",
            "clean_browsers",
            "clean_update_cache"
        ];
        foreach (var sectionGroup in listUtilities.GroupBy(u => u.Section).OrderBy(g => g.Key))
        {
            var group = new BatchGroup(sectionGroup.Key);
            foreach (var utility in sectionGroup)
            {
                group.Rows.Add(new BatchUtilityRow(utility, defaults.Contains(utility.Id)));
            }

            Groups.Add(group);
        }

        Rows = Groups.SelectMany(g => g.Rows).ToList();
        foreach (var row in Rows)
        {
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BatchUtilityRow.IsIncluded))
                {
                    OnPropertyChanged(nameof(CanRunBatch));
                }
            };
        }

        // Глобальный поиск «Главной» находит утилиты всех разделов — отдельные
        // строки для подсказок, не зависящие от урезанного списка переключателя.
        _allUtilities = utilities;
        RebuildSearchRows();
    }

    private List<BatchUtilityRow> _searchRows = [];

    // ===================== Поиск по пользовательским скриптам =====================

    private readonly Dictionary<string, BatchUtilityRow> _scriptRowMap = new(StringComparer.Ordinal);
    private List<BatchUtilityRow> _scriptRows = [];

    // Строки скриптов — ТОЛЬКО для подсказок поиска (MainViewModel.RefreshScriptSearchRows
    // при загрузке/добавлении/удалении). В реестр утилит они не попадают: меню-редактор
    // и список большого выключателя скрипты не видят, в подсказках скрипт — один,
    // по месту в своём разделе (закреплённый дубликат не участвует). Экземпляры
    // BatchUtility мутируются на месте, строки переиспользуются по Id —
    // BatchUtilityRow подписан на статический L.LanguageChanged без отписки.
    public void SetScriptSearchRows(IEnumerable<BatchUtility> scripts)
    {
        var actualIds = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<BatchUtilityRow>();
        foreach (var utility in scripts)
        {
            actualIds.Add(utility.Id);
            if (!_scriptRowMap.TryGetValue(utility.Id, out var row))
            {
                row = new BatchUtilityRow(utility, isIncluded: false);
                _scriptRowMap[utility.Id] = row;
            }
            else
            {
                // Скрипт переименован/изменён: свежий экземпляр подставляется
                // в существующую строку (см. комментарий к BatchUtilityRow.Utility).
                row.Utility = utility;
            }

            rows.Add(row);
        }

        foreach (var staleId in _scriptRowMap.Keys.Where(id => !actualIds.Contains(id)).ToList())
        {
            _scriptRowMap.Remove(staleId);
        }

        _scriptRows = rows;
        ApplySearch();
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
        // «Включить все» в список не входит: оно дублирует все категории разом.
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
        // Бэкап/откат — шапочные кнопки раздела «Службы», не карточки: поиск их не находит.
        Utility(6, "svc_backup", "S_Backup",
            () => RunAsync(_services.BackupCommand, () => _services.StatusText),
            keywords: "служб service бэкап backup", showInSearch: false),
        Utility(6, "svc_restore", "S_Restore",
            () => RunAsync(_services.RestoreCommand, () => _services.StatusText),
            keywords: "служб service откат restore восстанов", showInSearch: false),
    ];

    private List<BatchUtility> BuildTasks() =>
    [
        // Шапочные кнопки «Бэкап»/«Откатить» раздела «Задачи» — вне поиска.
        Utility(15, "tasks_backup", "S_Backup",
            () => RunAsync(_tasks.BackupCommand, () => _tasks.StatusText),
            keywords: "задач планировщ scheduler task бэкап backup", showInSearch: false),
        Utility(15, "tasks_restore", "S_Rollback",
            () => RunAsync(_tasks.RestoreCommand, () => _tasks.StatusText),
            keywords: "задач планировщ task бэкап restore", showInSearch: false),
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
            // «Сбросить» — шапочная кнопка раздела «Сеть», не карточка: вне поиска.
            Utility(9, "net_reset_all", "S_NetResetAll",
                () => RunAsync(_network.ResetAllCommand, () => _network.StatusText), showInSearch: false),
            Utility(9, "net_gaming_apply", "S_NetGamingProfile",
                () => RunAsync(_network.GamingProfileCommand, () => _network.StatusText),
                needsApply: () => !_network.IsGamingActive),
            // «Откатить» у карточки профиля — кнопка, не отдельная карточка: вне поиска.
            Utility(9, "net_gaming_rollback", "S_Rollback",
                () => RunAsync(_network.RollbackGamingProfileCommand, () => _network.StatusText), showInSearch: false),
            Utility(9, "net_adapter_apply", "S_NetAdapterProfileTitle",
                () => RunAsync(_network.ApplyUniversalAdapterProfileCommand, () => _network.StatusText)),
            Utility(9, "net_adapter_restore", "S_Rollback",
                () => RunAsync(_network.RestoreAdapterProfileCommand, () => _network.StatusText), showInSearch: false),
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
        Func<bool?>? needsApply = null,
        bool showInSearch = true) =>
        new()
        {
            Id = id,
            TitleKey = titleKey,
            Section = section,
            Run = run,
            Keywords = keywords,
            NeedsApply = needsApply,
            ShowInSearch = showInSearch
        };
}

// Подсказка поиска: утилита с номером раздела-владельца. Заголовок раздела
// берётся из словаря на момент построения списка (ApplySearch перечитывает
// его при смене языка).
public sealed class SearchSuggestion
{
    public SearchSuggestion(string title, string target, int sectionNumber,
        Func<int, string>? sectionTitleResolver = null)
    {
        Title = title;
        Target = target;
        SectionNumber = sectionNumber;
        // Пользовательские вкладки (номера ≥ 100) не имеют ключа в словаре —
        // заголовок берётся из resolver'а MainViewModel.
        SectionTitle =
            Application.Current?.TryFindResource($"S_Section{sectionNumber:00}_Title") as string
            ?? sectionTitleResolver?.Invoke(sectionNumber)
            ?? string.Empty;
    }

    public string Title { get; }

    public string Target { get; }

    public int SectionNumber { get; }

    public string SectionTitle { get; }

    public bool HasTarget => !string.IsNullOrEmpty(Target);
}

// Строка чек-листа сканирования: область и текущее состояние.
