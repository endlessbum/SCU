using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SCU.Common;
using SCU.Models;
using SCU.Services.Dashboard;

namespace SCU.ViewModels.Sections;

// Заголовок дня в списке раздела «История» (группировка событий по дате).
public sealed class HistoryDayGroup
{
    public HistoryDayGroup(DateTime date)
    {
        DateText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public string DateText { get; }
}

// Раздел 18 «История»: полная история операций из HistoryStore. Только чтение:
// группировка по дню, фильтр по категории, отображение ограничено последними 300
// событиями (хранилище и так держит максимум 500 — TakeLast достаточно).
public partial class HistoryViewModel : ObservableObject
{
    private const int DisplayLimit = 300;

    private readonly Logger _logger;
    private readonly HistoryStore _history;

    // Полная история из хранилища: источник фильтра и отображения.
    private IReadOnlyList<HistoryEvent> _allEvents = [];

    public HistoryViewModel(Logger logger, HistoryStore history)
    {
        _logger = logger;
        _history = history;
        // Как SectionItem: подписка живёт столько же, сколько приложение.
        L.LanguageChanged += OnLanguageChanged;
    }

    // Строки списка: HistoryDayGroup (заголовок дня) и HistoryEventRow (событие).
    public ObservableCollection<object> Rows { get; } = [];

    // Пункты фильтра: «Все категории» + реально присутствующие в истории категории.
    public ObservableCollection<string> CategoryFilterOptions { get; } = [];

    // Выбранная категория фильтра; null/пункт «Все категории» — без фильтра.
    [ObservableProperty]
    private string? _categoryFilter;

    // Загрузка истории. Вызывается при активации раздела (MainViewModel).
    public async Task LoadAsync()
    {
        _logger.Info("HISTORY | opened");
        try
        {
            _allEvents = await _history.LoadAllAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Warn("HISTORY | load failed | " + exception.Message);
            _allEvents = [];
        }

        RebuildFilterOptions();
        RebuildRows();
    }

    partial void OnCategoryFilterChanged(string? value)
    {
        _logger.Info("HISTORY | filter=" + (IsAllFilter(value) ? "all" : value ?? "all"));
        RebuildRows();
    }

    private bool IsAllFilter(string? value) =>
        string.IsNullOrEmpty(value) || string.Equals(value, L.T("Все категории"), StringComparison.Ordinal);

    private void RebuildFilterOptions()
    {
        var selected = CategoryFilter;
        CategoryFilterOptions.Clear();
        CategoryFilterOptions.Add(L.T("Все категории"));
        foreach (var category in _allEvents
                     .Select(@event => @event.Category)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(category => category, StringComparer.Ordinal))
        {
            CategoryFilterOptions.Add(category);
        }

        // Выбранный фильтр сохраняется, если категория по-прежнему присутствует.
        CategoryFilter = selected is not null && CategoryFilterOptions.Contains(selected)
            ? selected
            : CategoryFilterOptions[0];
    }

    private void RebuildRows()
    {
        var allText = L.T("Все категории");
        var filtered = _allEvents
            .Where(@event => CategoryFilter is null
                || string.Equals(CategoryFilter, allText, StringComparison.Ordinal)
                || string.Equals(CategoryFilter, @event.Category, StringComparison.Ordinal))
            .OrderByDescending(@event => @event.Timestamp)
            .Take(DisplayLimit)
            .ToList();

        Rows.Clear();
        DateTime? currentDay = null;
        foreach (var @event in filtered)
        {
            if (currentDay != @event.Timestamp.Date)
            {
                currentDay = @event.Timestamp.Date;
                Rows.Add(new HistoryDayGroup(@event.Timestamp.Date));
            }

            Rows.Add(new HistoryEventRow(@event));
        }
    }

    private void OnLanguageChanged()
    {
        RebuildFilterOptions();
        RebuildRows();
    }
}
