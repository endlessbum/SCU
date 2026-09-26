using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Models;
using SCU.Models.Benchmark;
using SCU.Services;
using SCU.Services.Dashboard;

namespace SCU.ViewModels.Sections;

// Раздел 20 «Бэнчмарк»: индекс состояния системы 0..100 по снимку SystemSnapshot,
// покрытие данными и подтверждённые изменения при повторной проверке (До → После).
// Расчёт — BenchmarkService (чистый), сырые снимки — SnapshotStore, журнал запусков —
// BenchmarkStore. Локализация подписей — здесь, по стабильным ключам метрик.
public partial class BenchmarkViewModel : ObservableObject, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly SystemStateService _systemState;
    private readonly SnapshotStore _snapshots;
    private readonly BenchmarkStore _store;
    private readonly HistoryStore _history;
    private readonly BenchmarkService _service = new();
    private CancellationTokenSource? _operationCts;

    // Метрики предыдущего запуска: источник статусов «Подтверждено / Без изменений».
    private IReadOnlyList<BenchmarkMetricResult> _previousMetrics = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _indexText = "—";

    [ObservableProperty]
    private string _potentialText = string.Empty;

    [ObservableProperty]
    private bool _hasPotential;

    [ObservableProperty]
    private string _coverageText = string.Empty;

    [ObservableProperty]
    private string _lastCheckText = string.Empty;

    [ObservableProperty]
    private bool _hasDelta;

    [ObservableProperty]
    private string _beforeIndexText = string.Empty;

    [ObservableProperty]
    private string _afterIndexText = string.Empty;

    [ObservableProperty]
    private string _deltaText = string.Empty;

    // «Индекс устарел»: система менялась после последней проверки.
    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private bool _hasUnreadAreas;

    [ObservableProperty]
    private string _unreadAreasText = string.Empty;

    // График тренда индекса по журналу запусков.
    private IReadOnlyList<DataPoint> _trend = [];

    public IReadOnlyList<DataPoint> Trend
    {
        get => _trend;
        private set
        {
            _trend = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTrend));
        }
    }

    public bool HasTrend => Trend.Count >= 2;

    // Индекс самой первой проверки (исходное состояние текущей серии запусков).
    [ObservableProperty]
    private bool _hasInitialIndex;

    [ObservableProperty]
    private string _initialIndexText = string.Empty;

    // Цвет дельты: рост — зелёный, падение — красный, ноль — нейтральный.
    [ObservableProperty]
    private string _deltaBrushKey = "SuccessBrush";

    partial void OnDeltaBrushKeyChanged(string value) =>
        DeltaBrush = Application.Current?.TryFindResource(value) as System.Windows.Media.Brush;

    [ObservableProperty]
    private System.Windows.Media.Brush? _deltaBrush = System.Windows.Media.Brushes.Gray;

    public bool CanRun => !IsRunning;

    public ObservableCollection<BenchmarkCategoryRow> Categories { get; } = [];

    public ObservableCollection<BenchmarkMetricRow> MetricRows { get; } = [];

    public ObservableCollection<BenchmarkPotentialRow> PotentialRows { get; } = [];

    public BenchmarkViewModel(
        Logger logger,
        SystemStateService systemState,
        SnapshotStore snapshots,
        BenchmarkStore store,
        HistoryStore history)
    {
        _logger = logger;
        _systemState = systemState;
        _snapshots = snapshots;
        _store = store;
        _history = history;
    }

    // Переход к разделу-владельцу (строки потенциала и таблицы) — подписан MainViewModel.
    public event Action<int>? NavigateToSectionRequested;

    // Активация вкладки: пометка «индекс устарел», если после последнего запуска
    // в истории появились операции (система менялась вне бэнчмарка).
    public async Task ActivateAsync()
    {
        try
        {
            var runs = await _store.LoadAllAsync().ConfigureAwait(true);
            if (runs.Count == 0)
            {
                return;
            }

            var lastRun = runs[^1].Timestamp;
            var events = await _history.LoadAllAsync().ConfigureAwait(true);
            if (events.Any(@event => @event.Timestamp > lastRun))
            {
                IsStale = true;
            }
        }
        catch (Exception exception)
        {
            _logger.Warn("BENCH | staleness check failed | " + exception.Message);
        }
    }

    // Загрузка сохранённых результатов без сканирования (старт приложения).
    public async Task InitializeAsync()
    {
        try
        {
            var runs = await _store.LoadAllAsync().ConfigureAwait(true);
            if (runs.Count == 0)
            {
                StatusText = L.T("Ещё не проверялось — запустите первую проверку.");
                return;
            }

            var latest = runs[^1];
            var previous = runs.Count > 1 ? runs[^2] : null;
            Render(latest, previous, runs.Take(runs.Count - 1).ToList());
        }
        catch (Exception exception)
        {
            _logger.Warn("BENCH | init failed | " + exception.Message);
        }
    }

    // Пометка «индекс устарел» (система менялась вне бэнчмарка).
    public void MarkStale() => IsStale = true;

    public void CancelOngoing() => _operationCts?.Cancel();

    // Переход к разделу, который может улучшить метрику (строка потенциала).
    [RelayCommand]
    private void OpenPotentialSection(BenchmarkPotentialRow row)
    {
        if (row.SectionNumber is { } section)
        {
            NavigateToSectionRequested?.Invoke(section);
        }
    }

    // Переход из строки детальной таблицы.
    [RelayCommand]
    private void OpenMetricSection(BenchmarkMetricRow row)
    {
        if (row.SectionNumber is { } section)
        {
            NavigateToSectionRequested?.Invoke(section);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunBenchmarkAsync()
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var ct = _operationCts.Token;
        IsRunning = true;
        try
        {
            StatusText = L.T("Проверка системы…");

            var snapshot = await _systemState.CollectFullSnapshotAsync(ct).ConfigureAwait(true);

            // Сырые данные снимка — в snapshots.json (бэнчмарк — надстройка над снимками).
            await _snapshots.SaveAsync(snapshot).ConfigureAwait(true);

            var result = _service.Evaluate(snapshot);

            // Точка сравнения «до» — предыдущий запуск из журнала.
            var runs = await _store.LoadAllAsync(ct).ConfigureAwait(true);
            var previous = runs.Count > 0 ? runs[^1] : null;
            var run = new BenchmarkRun(
                result.AlgorithmVersion,
                result.Timestamp,
                result.Index,
                result.Coverage,
                result.Potential,
                result.Metrics);
            await _store.AppendAsync(run, ct).ConfigureAwait(true);

            Render(run, previous, runs);

            StatusText = L.T("Проверка завершена.");
            _logger.Info($"BENCH | run | index={result.Index} coverage={result.Coverage} potential={result.Potential}");
        }
        catch (OperationCanceledException)
        {
            StatusText = L.T("Операция отменена.");
        }
        catch (Exception exception)
        {
            StatusText = L.T("Ошибка: {0}", exception.Message);
            _logger.Error("BENCH | run failed | " + exception);
        }
        finally
        {
            IsRunning = false;
        }
    }

    // ===================== Отображение =====================

    private void Render(BenchmarkRun run, BenchmarkRun? previous, IReadOnlyList<BenchmarkRun> earlierRuns)
    {
        HasResult = true;
        IndexText = string.Create(CultureInfo.CurrentCulture, $"{run.Index} / 100");

        var known = run.Metrics.Where(m => m.State == BenchmarkMetricState.Ok).ToList();
        var weightKnown = known.Sum(m => m.Weight);
        var weightAll = run.Metrics.Sum(m => m.Weight);
        CoverageText = L.T("Проверено: {0} из {1} · Покрытие: {2}%", known.Count, run.Metrics.Count, run.Coverage);
        LastCheckText = L.T("Последняя проверка: {0}", run.Timestamp.ToString("g", CultureInfo.CurrentCulture));

        // Потенциал: доступные баллы соответствия, не «проценты ускорения».
        var potential = known
            .Where(m => m.Conformity < 1.0)
            .Select(m => (Metric: m, Gain: (int)Math.Round(m.Weight * (1.0 - m.Conformity))))
            .Where(item => item.Gain > 0)
            .OrderByDescending(item => item.Gain)
            .ToList();
        PotentialRows.Clear();
        foreach (var (metric, gain) in potential)
        {
            PotentialRows.Add(new BenchmarkPotentialRow(
                MetricLabel(metric.Id),
                "+" + gain,
                MetricSection(metric.Id),
                MetricHint(metric.Id)));
        }

        HasPotential = PotentialRows.Count > 0;

        // Сравнение До → После: до = предыдущий запуск той же версии алгоритма.
        _previousMetrics = previous is not null && previous.AlgorithmVersion == run.AlgorithmVersion
            ? previous.Metrics
            : [];
        HasDelta = _previousMetrics.Count > 0;
        if (HasDelta)
        {
            BeforeIndexText = previous!.Index.ToString(CultureInfo.CurrentCulture);
            AfterIndexText = run.Index.ToString(CultureInfo.CurrentCulture);
            var delta = run.Index - previous.Index;
            DeltaText = (delta > 0 ? "+" : string.Empty) + delta.ToString(CultureInfo.CurrentCulture);
            DeltaBrushKey = delta > 0 ? "SuccessBrush" : delta < 0 ? "CaptionCloseHoverBrush" : "TertiaryTextBrush";
            DeltaBrush = Application.Current?.TryFindResource(DeltaBrushKey) as System.Windows.Media.Brush;
        }

        Categories.Clear();
        var categoryGroups = run.Metrics
            .GroupBy(m => m.Category, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in categoryGroups)
        {
            var groupKnown = group.Where(m => m.State == BenchmarkMetricState.Ok).ToList();
            var weight = groupKnown.Sum(m => m.Weight);
            var index = weight > 0
                ? (int)Math.Round(100.0 * groupKnown.Sum(m => m.Weight * m.Conformity) / weight)
                : -1;
            Categories.Add(new BenchmarkCategoryRow(
                CategoryTitle(group.Key),
                weight,
                group.Count(),
                groupKnown.Count,
                index));
        }

        MetricRows.Clear();
        foreach (var metric in run.Metrics)
        {
            MetricRows.Add(BuildMetricRow(metric));
        }

        // Тренд индекса: журнал + только что записанный запуск.
        Trend = BuildTrend(earlierRuns.Append(run).ToList());
        UpdateInitialIndex(earlierRuns, run);
        OnPropertyChanged(nameof(HasFullPotential));

        var unread = run.Metrics
            .Where(m => m.State == BenchmarkMetricState.Unknown)
            .Select(m => MetricLabel(m.Id))
            .ToList();
        HasUnreadAreas = unread.Count > 0;
        UnreadAreasText = HasUnreadAreas
            ? L.T("Не удалось прочитать: {0}", string.Join(", ", unread))
            : string.Empty;
    }

    // Все применимые цели универсального профиля достигнуты.
    public bool HasFullPotential => HasResult && !HasPotential;

    private static IReadOnlyList<DataPoint> BuildTrend(IReadOnlyList<BenchmarkRun> runs) =>
        runs.Select(item => new DataPoint(item.Timestamp, item.Index)).ToList();

    private void UpdateInitialIndex(IReadOnlyList<BenchmarkRun> earlierRuns, BenchmarkRun latest)
    {
        if (earlierRuns.Count > 0 && earlierRuns[0].AlgorithmVersion == latest.AlgorithmVersion)
        {
            HasInitialIndex = true;
            InitialIndexText = L.T("Исходный индекс серии: {0}", earlierRuns[0].Index);
        }
        else
        {
            HasInitialIndex = false;
            InitialIndexText = string.Empty;
        }
    }

    // Метрика -> раздел, который может на неё повлиять.
    private static int? MetricSection(string id) => id switch
    {
        "disk.free_system" => 3,          // Очистка
        "startup.count" => 7,             // Автозагрузка
        "services.readable" => 6,         // Службы
        "tasks.readable" => 15,           // Задачи
        "power.readable" => 8,            // Питание
        "security.uac_standard" => 13,    // Безопасность
        "privacy.applied" => 5,           // Приватность
        "updates.readable" => 16,         // Обновления
        "network.readable" => 9,          // Сеть
        "system.read" => 1,               // Информация о системе
        _ => null
    };

    private static string MetricHint(string id) => id switch
    {
        "disk.free_system" => L.T("Свободное место на диске с Windows. Очистка временных файлов и кэшей увеличивает показатель."),
        "startup.count" => L.T("Элементы, запускаемые вместе с Windows. Управляются в разделе «Автозагрузка»."),
        "services.readable" => L.T("Службы контрольного набора читаются без ошибок — их состояние можно менять и проверять."),
        "tasks.readable" => L.T("Задачи планировщика контрольного набора прочитаны."),
        "power.readable" => L.T("Активная схема питания определена; управляется в разделе «Питание»."),
        "security.uac_standard" => L.T("Контроль учётных записей на стандартном уровне. Ослабленный UAC — отклонение универсальной политики."),
        "privacy.applied" => L.T("Доля применённых категорий приватности. Управляется в разделе «Приватность»."),
        "updates.readable" => L.T("Состояние службы обновлений определено (блокировка/пауза — осознанные настройки)."),
        "network.readable" => L.T("Сетевые параметры прочитаны; управляются в разделе «Сеть»."),
        "system.read" => L.T("Базовые сведения о системе прочитаны."),
        _ => string.Empty
    };

    private BenchmarkMetricRow BuildMetricRow(BenchmarkMetricResult metric)
    {
        var status = BuildStatus(metric);
        return new BenchmarkMetricRow(
            MetricLabel(metric.Id),
            FormatValue(metric),
            metric.State == BenchmarkMetricState.Ok
                ? Math.Round(metric.Conformity * 100).ToString(CultureInfo.CurrentCulture) + "%"
                : "—",
            StateText(metric.State),
            status.Text,
            status.BrushKey,
            MetricSection(metric.Id),
            MetricHint(metric.Id));
    }

    // Статус против предыдущего запуска: подтверждено/не изменилось/не удалось проверить.
    // Успешное выполнение команды не равно подтверждённому изменению — сравниваются
    // значения, реально прочитанные системой повторно.
    private (string Text, string BrushKey) BuildStatus(BenchmarkMetricResult metric)
    {
        if (!HasDelta)
        {
            return (string.Empty, string.Empty);
        }

        var before = _previousMetrics.FirstOrDefault(m => m.Id == metric.Id);
        if (before is null)
        {
            return (L.T("Не удалось проверить"), "WarnBrush");
        }

        if (metric.State == BenchmarkMetricState.Ok && before.State == BenchmarkMetricState.Ok)
        {
            var valueChanged = !string.Equals(FormatValue(metric), FormatValue(before), StringComparison.Ordinal);
            if (metric.Conformity > before.Conformity || valueChanged)
            {
                return (L.T("Подтверждено"), "SuccessBrush");
            }

            return (L.T("Без изменений"), "TertiaryTextBrush");
        }

        if (metric.State == BenchmarkMetricState.Ok || before.State == BenchmarkMetricState.Ok)
        {
            // Одна сторона прочитана, другая нет.
            return metric.State == BenchmarkMetricState.Ok
                ? (L.T("Подтверждено"), "SuccessBrush")
                : (L.T("Не удалось проверить"), "WarnBrush");
        }

        return (L.T("Не удалось проверить"), "WarnBrush");
    }

    private static string StateText(BenchmarkMetricState state) => state switch
    {
        BenchmarkMetricState.Ok => string.Empty,
        BenchmarkMetricState.NotApplicable => L.T("Не применимо"),
        BenchmarkMetricState.RequiresRestart => L.T("Требует перезапуска"),
        _ => L.T("Не удалось проверить")
    };

    private static string FormatValue(BenchmarkMetricResult metric) => metric.Id switch
    {
        "disk.free_system" => metric.NumericValue is { } gb
            ? gb.ToString("0.0", CultureInfo.CurrentCulture) + " " + L.T("ГБ")
            : metric.TextValue ?? string.Empty,
        "services.readable" => metric.NumericValue is { } ok && metric.TextValue is { } total
            ? ok.ToString(CultureInfo.CurrentCulture) + " / " + total
            : string.Empty,
        "privacy.applied" => metric.NumericValue is { } applied && metric.TextValue is { } total2
            ? applied.ToString(CultureInfo.CurrentCulture) + " / " + total2
            : string.Empty,
        "security.uac_standard" => metric.TextValue switch
        {
            "standard" => L.T("стандартный"),
            "weakened" => L.T("ослабленный"),
            _ => metric.TextValue ?? string.Empty
        },
        "power.readable" or "updates.readable" or "network.readable" or "tasks.readable" =>
            metric.State == BenchmarkMetricState.Ok ? L.T("прочитано") : string.Empty,
        _ => metric.NumericValue is { } number
            ? number.ToString("0", CultureInfo.CurrentCulture)
            : metric.TextValue ?? string.Empty
    };

    private static string MetricLabel(string id) => id switch
    {
        "system.read" => L.T("Версия Windows определена"),
        "disk.free_system" => L.T("Свободно на системном диске"),
        "startup.count" => L.T("Элементов автозагрузки"),
        "services.readable" => L.T("Службы доступны для управления"),
        "tasks.readable" => L.T("Задачи доступны для управления"),
        "power.readable" => L.T("Схема питания прочитана"),
        "security.uac_standard" => L.T("UAC — стандартный уровень"),
        "privacy.applied" => L.T("Настройки приватности применены"),
        "updates.readable" => L.T("Состояние обновлений прочитано"),
        "network.readable" => L.T("Сетевые параметры прочитаны"),
        _ => id
    };

    private static string CategoryTitle(string category) => category switch
    {
        BenchmarkService.CategorySystem => L.T("Система"),
        BenchmarkService.CategoryDisk => L.T("Диск и очистка"),
        BenchmarkService.CategoryStartup => L.T("Автозагрузка"),
        BenchmarkService.CategoryServices => L.T("Службы"),
        BenchmarkService.CategoryTasks => L.T("Задачи"),
        BenchmarkService.CategoryPower => L.T("Питание"),
        BenchmarkService.CategorySecurity => L.T("Безопасность"),
        BenchmarkService.CategoryPrivacy => L.T("Приватность"),
        BenchmarkService.CategoryUpdates => L.T("Обновления"),
        BenchmarkService.CategoryNetwork => L.T("Сеть"),
        _ => category
    };
}

// Строка категории: заголовок, индекс, покрытие категории.
public sealed class BenchmarkCategoryRow
{
    public BenchmarkCategoryRow(string title, int weight, int metricsTotal, int metricsKnown, int index)
    {
        Title = title;
        Weight = weight;
        CoverageText = metricsKnown.ToString(CultureInfo.CurrentCulture) + " / " + metricsTotal.ToString(CultureInfo.CurrentCulture);
        // -1 = нечего оценивать (все метрики категории неизвестны).
        IndexText = index >= 0
            ? index.ToString(CultureInfo.CurrentCulture)
            : "—";
    }

    public string Title { get; }

    public int Weight { get; }

    public string CoverageText { get; }

    public string IndexText { get; }
}

// Строка детальной таблицы: с переходом в раздел-владелец и пояснением.
public sealed class BenchmarkMetricRow
{
    public BenchmarkMetricRow(string label, string value, string conformity, string state, string status, string statusBrushKey, int? sectionNumber, string hint)
    {
        Label = label;
        Value = value;
        Conformity = conformity;
        State = state;
        Status = status;
        StatusBrushKey = statusBrushKey;
        SectionNumber = sectionNumber;
        Hint = hint;
    }

    public string Label { get; }

    public string Value { get; }

    public string Conformity { get; }

    public string State { get; }

    public string Status { get; }

    public string StatusBrushKey { get; }

    public int? SectionNumber { get; }

    public bool HasNavigation => SectionNumber is not null;

    public string Hint { get; }
}

// Строка «Что может улучшить SCU»: метрика, доступные баллы и переход в раздел.
public sealed class BenchmarkPotentialRow
{
    public BenchmarkPotentialRow(string label, string gain, int? sectionNumber, string hint)
    {
        Label = label;
        Gain = gain;
        SectionNumber = sectionNumber;
        Hint = hint;
    }

    public string Label { get; }

    public string Gain { get; }

    public int? SectionNumber { get; }

    public bool HasNavigation => SectionNumber is not null;

    public string Hint { get; }
}
