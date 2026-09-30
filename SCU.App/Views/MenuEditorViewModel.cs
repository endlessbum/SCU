using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Models;
using SCU.ViewModels;
using SCU.ViewModels.Sections;

namespace SCU.Views;

// Элемент дерева редактора меню: группа → раздел(ы).
// Настройки карточки (название, группа, видимость) редактируются локально и
// применяются только по кнопке «Применить»; без изменений кнопка неактивна.
// Операции со списком утилит остаются мгновенными (у каждой своя кнопка).
public sealed partial class MenuEditorSection : ObservableObject
{
    public MenuEditorSection(MainViewModel main, SectionItem section)
    {
        _main = main;
        Section = section;
        IsCustom = section.IsCustom;
        // Синхронизация при изменениях извне карточки (сброс меню, ApplyMenu).
        section.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SectionItem.Title) or nameof(SectionItem.GroupTitle))
            {
                RefreshSnapshot();
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Group));
                RefreshChangeState();
            }
        };
        RefreshSnapshot();
    }

    private readonly MainViewModel _main;

    public SectionItem Section { get; }

    public bool IsCustom { get; }

    public int Number => Section.Number;

    // ===== Редактируемые значения (применяются кнопкой) =====

    private string _title = string.Empty;

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value ?? string.Empty))
            {
                RefreshChangeState();
            }
        }
    }

    private string _group = string.Empty;

    public string Group
    {
        get => _group;
        set
        {
            if (SetProperty(ref _group, value ?? string.Empty))
            {
                RefreshChangeState();
            }
        }
    }

    private bool _isVisible;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                RefreshChangeState();
            }
        }
    }

    // ===== Снимок применённого состояния =====

    private string _originalTitle = string.Empty;
    private string _originalGroup = string.Empty;
    private bool _originalIsVisible;

    public bool HasChanges =>
        !string.Equals(_title, _originalTitle, StringComparison.Ordinal)
        || !string.Equals(_group, _originalGroup, StringComparison.Ordinal)
        || _isVisible != _originalIsVisible;

    private void RefreshSnapshot()
    {
        _originalTitle = _title = Section.Title;
        _originalGroup = _group = Section.GroupTitle;
        _originalIsVisible = _isVisible = _main.Menu.Hidden.All(number => number != Section.Number);
    }

    private void RefreshChangeState()
    {
        OnPropertyChanged(nameof(HasChanges));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    // Применение настроек карточки: только изменённые поля уходят в MainViewModel
    // (и сохраняются в menu.json), снимок обновляется по фактическому состоянию.
    private bool CanApply() => HasChanges;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!string.Equals(_title, _originalTitle, StringComparison.Ordinal))
        {
            _main.SetSectionTitle(Number, _title);
        }

        if (!string.Equals(_group, _originalGroup, StringComparison.Ordinal))
        {
            _main.SetSectionGroup(Number, _group);
        }

        if (_isVisible != _originalIsVisible)
        {
            _main.SetSectionHidden(Number, !_isVisible);
        }

        RefreshSnapshot();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Group));
        RefreshChangeState();
    }

    // Утилиты раздела: встроенные (удаление) или назначенные (удаление из вкладки).
    public ObservableCollection<MenuEditorUtility> Utilities { get; } = [];
}

// Строка утилиты в редакторе: встроенная (можно удалить) или назначенная
// в пользовательскую вкладку (можно убрать).
public sealed class MenuEditorUtility
{
    public MenuEditorUtility(string id, string title, bool canRemove, bool canDelete)
    {
        Id = id;
        Title = title;
        CanRemove = canRemove;
        CanDelete = canDelete;
    }

    public string Id { get; }

    public string Title { get; }

    public bool CanRemove { get; }

    public bool CanDelete { get; }
}

// Редактор меню: построен на живой MainViewModel. Операции со списком утилит
// применяются сразу; настройки карточки (название/группа/видимость) — по кнопке
// «Применить» и сохраняются в menu.json. Меню и поиск обновляются на ходу.
public partial class MenuEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Logger _logger;

    public MenuEditorViewModel(MainViewModel main, Logger logger)
    {
        _main = main;
        _logger = logger;
        RebuildTree();
    }

    public ObservableCollection<MenuEditorGroup> Groups { get; } = [];

    // Утилиты, доступные для добавления в выбранную пользовательскую вкладку.
    public ObservableCollection<BatchUtility> AvailableUtilities { get; } = [];

    [ObservableProperty]
    private MenuEditorSection? _selectedSection;

    [ObservableProperty]
    private string _newSectionTitle = string.Empty;

    [ObservableProperty]
    private string _newSectionGroup = string.Empty;

    private MenuEditorGroup? _selectedGroup;

    public MenuEditorGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                NewSectionGroup = value?.Title ?? string.Empty;
                RefreshAvailableUtilities();
            }
        }
    }

    [ObservableProperty]
    private MenuEditorUtility? _selectedAvailableUtility;

    public string ExistingGroupNames => string.Join(" · ",
        Groups.Select(group => group.Title).Where(title => title.Length > 0));

    private void RebuildTree()
    {
        // Сохраняем состояние блока «новая вкладка»: Groups.Clear() сбрасывает
        // выделение ComboBox и ввод.
        var keepGroup = NewSectionGroup;
        var selectedNumber = SelectedSection?.Number;

        Groups.Clear();
        // Дерево строится из ПОЛНОГО списка встроенных разделов (включая скрытые —
        // иначе вернуть скрытый раздел из редактора невозможно) + пользовательских.
        var all = _main.BuiltinSections.Concat(_main.Sections.Where(section => section.Number >= 100));
        foreach (var group in all.GroupBy(section => section.GroupTitle, StringComparer.Ordinal))
        {
            Groups.Add(new MenuEditorGroup(_main, group.Key, group));
        }

        OnPropertyChanged(nameof(ExistingGroupNames));
        OnPropertyChanged(nameof(GroupNames));

        SelectedSection = Groups
            .SelectMany(group => group.Sections)
            .FirstOrDefault(section => section.Number == selectedNumber)
            ?? Groups.SelectMany(group => group.Sections).FirstOrDefault();
        SelectedGroup = Groups.FirstOrDefault(group => group.Title == keepGroup)
            ?? Groups.FirstOrDefault();
        NewSectionGroup = keepGroup;
    }

    // Существующие группы для выпадающих списков ComboBox'ов
    // (безгрупповые разделы — «Главная» — в подсказки не попадают).
    public IReadOnlyList<string> GroupNames => Groups
        .Select(group => group.Title)
        .Where(title => title.Length > 0)
        .ToList();

    private void RebuildUtilities()
    {
        if (SelectedSection is not { } section)
        {
            return;
        }

        section.Utilities.Clear();
        if (section.IsCustom)
        {
            var custom = _main.Menu.CustomSections.FirstOrDefault(data => data.Id == section.Number);
            foreach (var utilityId in custom?.Utils ?? [])
            {
                if (_main.Dashboard.GetUtility(utilityId) is { } utility)
                {
                    section.Utilities.Add(new MenuEditorUtility(
                        utilityId, utility.ResolveTitle(), canRemove: true, canDelete: false));
                }
            }
        }
        else
        {
            foreach (var pair in _main.Dashboard.UtilitiesById
                         .Where(pair => pair.Value.Section == section.Number)
                         .OrderBy(pair => pair.Value.ResolveTitle()))
            {
                section.Utilities.Add(new MenuEditorUtility(
                    pair.Key, pair.Value.ResolveTitle(),
                    canRemove: false,
                    // Список главной страницы (раздел «Очистка») удалению не подлежит.
                    canDelete: !DashboardViewModel.IsProtectedUtility(pair.Value)));
            }
        }

        RefreshAvailableUtilities();
    }

    private void RefreshAvailableUtilities()
    {
        var assigned = new HashSet<string>(
            _main.Menu.CustomSections.SelectMany(data => data.Utils), StringComparer.Ordinal);
        AvailableUtilities.Clear();
        foreach (var pair in _main.Dashboard.UtilitiesById.OrderBy(pair => pair.Value.ResolveTitle()))
        {
            if (!assigned.Contains(pair.Key))
            {
                AvailableUtilities.Add(pair.Value);
            }
        }
    }

    partial void OnSelectedSectionChanged(MenuEditorSection? value)
    {
        if (value is not null)
        {
            RebuildUtilities();
        }
    }

    [RelayCommand]
    private void AddSection()
    {
        var title = NewSectionTitle.Trim();
        if (title.Length == 0)
        {
            return;
        }

        // Введённый текст группы имеет приоритет над выбранной из списка.
        var group = NewSectionGroup.Trim();
        _main.AddCustomSection(title, group.Length == 0 ? L.T("Мои утилиты") : group);
        NewSectionTitle = string.Empty;
        RebuildTree();
    }

    [RelayCommand]
    private void DeleteSection(MenuEditorSection? section)
    {
        if (section?.IsCustom == true)
        {
            _main.DeleteCustomSection(section.Number);
            RebuildTree();
        }
    }

    [RelayCommand]
    private void AddUtilityToSelected()
    {
        if (SelectedSection is { IsCustom: true } section && SelectedAvailableUtility is { } utility)
        {
            _main.AddUtilityToCustomSection(section.Number, utility.Id);
            RebuildUtilities();
            RebuildTree();
        }
    }

    [RelayCommand]
    private void RemoveUtility(MenuEditorUtility? utility)
    {
        if (SelectedSection is { IsCustom: true } section && utility is not null)
        {
            _main.RemoveUtilityFromCustomSection(section.Number, utility.Id);
            RebuildUtilities();
            RebuildTree();
        }
    }

    [RelayCommand]
    private void DeleteUtility(MenuEditorUtility? utility)
    {
        if (utility is { CanDelete: true })
        {
            _main.DeleteUtility(utility.Id);
            RebuildUtilities();
            RebuildTree();
        }
    }

    [RelayCommand]
    private void ResetMenu()
    {
        _main.ResetMenu();
        RebuildTree();
    }
}

public sealed class MenuEditorGroup
{
    public MenuEditorGroup(MainViewModel main, string title, IEnumerable<SectionItem> sections)
    {
        Title = title;
        foreach (var section in sections)
        {
            Sections.Add(new MenuEditorSection(main, section));
        }
    }

    public string Title { get; }

    public ObservableCollection<MenuEditorSection> Sections { get; } = [];
}
