using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.ViewModels.Sections;
using SCU.Views.Controls;

namespace SCU.ViewModels;

// П. 13 аудита: зона ответственности «пользовательские скрипты и редактор меню».
public partial class MainViewModel
{
    // ===================== Пользовательские скрипты =====================

    private readonly UserScriptStore _userScriptStore;

    public ObservableCollection<UserScriptCard> UserScripts { get; } = [];

    // «Установленные» активны, пока есть хотя бы один добавленный скрипт.
    [ObservableProperty]
    private bool _hasInstalledScripts;

    // Поднимается при любом изменении набора скриптов (слушает полосы карточек).
    public event Action? UserScriptsChanged;

    [ObservableProperty]
    private string _selectedScriptPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddScriptCommand))]
    private bool _isValidatingScript;

    // Карточка скрипта в полосе раздела.
    public sealed partial class UserScriptCard : ObservableObject
    {
        public UserScriptCard(UserScriptData data)
        {
            Id = data.Id;
            Title = data.Title;
            Comment = data.Comment;
            Tooltip = data.Tooltip;
            SectionNumber = data.SectionNumber;
            Data = data;
        }

        public string Id { get; }

        public string Title { get; }

        public string Comment { get; }

        public string Tooltip { get; }

        public bool HasTooltip => !string.IsNullOrEmpty(Tooltip);

        public int SectionNumber { get; }

        public UserScriptData Data { get; }

        [ObservableProperty]
        private bool _isRunning;

        [ObservableProperty]
        private string? _resultText;
    }

    private void ReloadUserScripts()
    {
        UserScripts.Clear();
        foreach (var data in _userScriptStore.Load())
        {
            UserScripts.Add(new UserScriptCard(data));
        }

        HasInstalledScripts = UserScripts.Count > 0;
        UserScriptsChanged?.Invoke();
    }

    public bool AddUserScript(string sourcePath, int sectionNumber, string title,
        string comment, string tooltip)
    {
        try
        {
            var id = "us_" + Guid.NewGuid().ToString("N")[..12];
            var data = _userScriptStore.Import(sourcePath, sectionNumber, title, comment, tooltip, id);
            ReloadUserScripts();
            var sectionTitle = Sections.FirstOrDefault(section => section.Number == sectionNumber)?.Title ?? "";
            AppNotificationCenter.Instance.Push(
                L.T("Скрипт добавлен"),
                L.T("«{0}» размещён в разделе «{1}».", data.Title, sectionTitle),
                AppNotificationKind.Success);
            _logger.Info($"USCRIPT | added | {data.Id} | section={sectionNumber}");
            return true;
        }
        catch (Exception exception)
        {
            _logger.Error("USCRIPT | add failed | " + exception);
            AppNotificationCenter.Instance.Push(
                L.T("Скрипт не добавлен"),
                L.T("Ошибка: {0}", exception.Message),
                AppNotificationKind.Danger);
            return false;
        }
    }

    public void RemoveUserScript(string id)
    {
        var data = _userScriptStore.Load().FirstOrDefault(script => script.Id == id);
        if (data is null)
        {
            return;
        }

        _userScriptStore.Delete(data);
        var scripts = _userScriptStore.Load();
        _userScriptStore.Save(scripts.Where(script => script.Id != id).ToList());
        ReloadUserScripts();
        _logger.Info("USCRIPT | removed | " + id);
    }

    public async Task<string?> RunUserScriptAsync(UserScriptCard card)
    {
        // Весь путь под try, включая диалог подтверждения: метод вызывается из
        // async void (полоса скриптов), исключение из Ask не должно ронять приложение.
        try
        {
            // Скрипт выполняется с правами текущего сеанса SCU (в том числе
            // администратора): подтверждение — как у других рисковых действий.
            // При выключенном тумблере «UAC» Ask возвращает «подтверждено» автоматически.
            if (!_dialogs.Ask(
                    L.T("Запуск скрипта"),
                    L.T("Скрипт «{0}» выполнится с правами текущего сеанса SCU, включая администратора. Запустить?", card.Title),
                    L.T("Запустить")))
            {
                card.ResultText = L.T("Отменено.");
                _logger.Info("USCRIPT | run declined | " + card.Id);
                return card.ResultText;
            }

            card.IsRunning = true;
            card.ResultText = null;
            var (exitCode, output) = await _userScriptStore.RunAsync(card.Data).ConfigureAwait(true);
            card.ResultText = exitCode == 0
                ? (output.Length > 0 ? output : L.T("Готово."))
                : L.T("Код {0}: {1}", exitCode, output.Length > 0 ? output : L.T("без вывода"));
            _logger.Info($"USCRIPT | run | {card.Id} | rc={exitCode}");

            // Проверка целостности могла зафиксировать эталонный хэш legacy-карточки
            // (TOFU) — состояние скриптов сохраняется, чтобы проверка работала дальше.
            _userScriptStore.Save(UserScripts.Select(script => script.Data).ToList());
            return card.ResultText;
        }
        catch (Exception exception)
        {
            card.ResultText = L.T("Ошибка: {0}", exception.Message);
            _logger.Error("USCRIPT | run failed | " + card.Id + " | " + exception);
            return card.ResultText;
        }
        finally
        {
            card.IsRunning = false;
        }
    }

    // ===================== Редактирование меню =====================

    private readonly MenuCustomizationStore _menuStore;
    private MenuCustomization _menu = MenuCustomization.Empty;

    public MenuCustomization Menu => _menu;

    private const int CustomSectionFirstNumber = 100;

    private readonly Dictionary<int, CustomUtilitiesViewModel> _customSectionViewModels = [];

    public CustomUtilitiesViewModel GetCustomSectionViewModel(int sectionNumber) =>
        _customSectionViewModels.TryGetValue(sectionNumber, out var viewModel)
            ? viewModel
            : throw new InvalidOperationException("Неизвестная пользовательская вкладка " + sectionNumber);

    // Применение модели меню к Sections, поиску и пользовательским вкладкам.
    public void ApplyMenu()
    {
        _menu.Hidden = _menu.Hidden.Where(number => number < CustomSectionFirstNumber).Distinct().ToList();

        // 1. Встроенные разделы: переименования, группы.
        foreach (var section in Sections.Where(section => section.Number < CustomSectionFirstNumber))
        {
            section.TitleOverride = _menu.Titles.GetValueOrDefault(section.Number.ToString());
            section.GroupOverride = _menu.Groups.GetValueOrDefault(section.Number.ToString());
        }

        // 2. Пользовательские вкладки: пересоздаются с нуля (порядок = порядок в модели).
        foreach (var section in Sections.Where(section => section.Number >= CustomSectionFirstNumber).ToList())
        {
            Sections.Remove(section);
        }

        foreach (var custom in _menu.CustomSections)
        {
            Sections.Add(SectionItem.CreateCustom(custom.Id, custom.Title, custom.Group));
            var viewModel = _customSectionViewModels.TryGetValue(custom.Id, out var existing)
                ? existing
                : new CustomUtilitiesViewModel(custom.Id, _logger);
            viewModel.Rebuild(custom.Utils, id => Dashboard.GetUtility(id));
            _customSectionViewModels[custom.Id] = viewModel;
        }

        // 3. Скрытие встроенных разделов.
        foreach (var section in Sections
                     .Where(section => section.Number < CustomSectionFirstNumber
                         && _menu.Hidden.Contains(section.Number))
                     .ToList())
        {
            Sections.Remove(section);
        }

        // 4. Поиск: пользовательские вкладки для утилит (только не удалённые).
        foreach (var custom in _menu.CustomSections)
        {
            foreach (var utilityId in custom.Utils)
            {
                if (Dashboard.GetUtility(utilityId) is { } utility && utility.Section != custom.Id)
                {
                    Dashboard.SetUtilitySection(utilityId, custom.Id);
                }
            }
        }

        // 5. Выбор должен оставаться валидным (скрытая вкладка закрывается;
        //    сайдбар мог обнулить выделение при удалении элементов).
        if (CurrentSection is null || !Sections.Contains(CurrentSection))
        {
            CurrentSection = Sections.FirstOrDefault();
        }
    }

    // Поднимается после каждого применения меню: MainWindow сбрасывает кэш
    // view пользовательских вкладок (переиспользование id показывало старое).
    public event Action? MenuApplied;

    private void SaveMenu()
    {
        _menuStore.Save(_menu);
        ApplyMenu();
        MenuApplied?.Invoke();
    }

    public void SetSectionHidden(int number, bool hidden)
    {
        if (number >= CustomSectionFirstNumber)
        {
            return;
        }

        _menu.Hidden.Remove(number);
        if (hidden)
        {
            _menu.Hidden.Add(number);
        }

        SaveMenu();
    }

    public void SetSectionTitle(int number, string title)
    {
        var trimmed = title.Trim();
        if (number >= CustomSectionFirstNumber)
        {
            var custom = _menu.CustomSections.FirstOrDefault(section => section.Id == number);
            if (custom is not null)
            {
                custom.Title = trimmed;
                SaveMenu();
            }

            return;
        }

        if (trimmed.Length == 0)
        {
            _menu.Titles.Remove(number.ToString());
        }
        else
        {
            _menu.Titles[number.ToString()] = trimmed;
        }

        SaveMenu();
    }

    public void SetSectionGroup(int number, string group)
    {
        var raw = group.Trim();
        if (number >= CustomSectionFirstNumber)
        {
            var custom = _menu.CustomSections.FirstOrDefault(section => section.Id == number);
            if (custom is not null)
            {
                custom.Group = raw;
                SaveMenu();
            }

            return;
        }

        if (raw.Length == 0)
        {
            _menu.Groups.Remove(number.ToString());
        }
        else
        {
            _menu.Groups[number.ToString()] = raw;
        }

        SaveMenu();
    }

    public int AddCustomSection(string title, string group)
    {
        var id = _menu.CustomSections.Count == 0
            ? CustomSectionFirstNumber
            : Math.Max(CustomSectionFirstNumber, _menu.CustomSections.Max(section => section.Id)) + 1;
        _menu.CustomSections.Add(new CustomSectionData
        {
            Id = id,
            Title = title.Trim(),
            Group = group.Trim(),
            Utils = [],
        });
        SaveMenu();
        return id;
    }

    public void DeleteCustomSection(int number)
    {
        var custom = _menu.CustomSections.FirstOrDefault(section => section.Id == number);
        if (custom is null)
        {
            return;
        }

        // Утилиты возвращаются в родные разделы (OriginalSection в реестре).
        foreach (var utilityId in custom.Utils)
        {
            Dashboard.ResetUtilitySection(utilityId);
        }

        _menu.CustomSections.Remove(custom);
        _customSectionViewModels.Remove(number);
        SaveMenu();
    }

    public bool AddUtilityToCustomSection(int number, string utilityId)
    {
        var custom = _menu.CustomSections.FirstOrDefault(section => section.Id == number);
        if (custom is null || Dashboard.GetUtility(utilityId) is null
            || custom.Utils.Contains(utilityId))
        {
            return false;
        }

        custom.Utils.Add(utilityId);
        SaveMenu();
        return true;
    }

    public void RemoveUtilityFromCustomSection(int number, string utilityId)
    {
        var custom = _menu.CustomSections.FirstOrDefault(section => section.Id == number);
        if (custom is null)
        {
            return;
        }

        custom.Utils.Remove(utilityId);
        // Утилита возвращается в родной раздел, если не назначена в другую вкладку.
        if (!_menu.CustomSections.Any(section => section.Utils.Contains(utilityId)))
        {
            Dashboard.ResetUtilitySection(utilityId);
        }

        SaveMenu();
    }

    // Удаление встроенной утилиты (кроме списка главной страницы).
    public bool DeleteUtility(string utilityId)
    {
        if (_menu.DeletedUtils.Contains(utilityId))
        {
            return false;
        }

        _menu.DeletedUtils.Add(utilityId);
        Dashboard.DeleteUtility(utilityId);
        _menuStore.Save(_menu);
        ApplyMenu();
        // MenuApplied нужен и здесь: MainWindow сбрасывает кэш view пользовательских
        // вкладок, иначе переиспользованный view показывает удалённую утилиту.
        MenuApplied?.Invoke();
        return true;
    }

    // Полный сброс меню к заводскому виду: реестр утилит тоже возвращается
    // (удалённые восстанавливаются, перемещённые — в родные разделы) без перезапуска.
    public void ResetMenu()
    {
        _menu = new MenuCustomization();
        _menuStore.Save(_menu);
        _customSectionViewModels.Clear();
        Dashboard.ResetRegistry();
        ApplyMenu();
        MenuApplied?.Invoke();
        _logger.Info("MENU | reset to defaults");
    }

    // ===================== Добавление своего скрипта =====================

    public event Action? InstalledScriptsRequested;

    [RelayCommand]
    private void PickScript()
    {
        var path = _filePicker.PickOpenFile(
            L.T("Выбор скрипта"), L.T("Скрипты (*.ps1;*.bat)|*.ps1;*.bat|Все файлы (*.*)|*.*"));
        if (path is not null)
        {
            SelectedScriptPath = path;
        }
    }

    private bool CanAddScript() =>
        !IsValidatingScript
        && SelectedScriptPath.Length > 0
        && File.Exists(SelectedScriptPath)
        && UserScriptStore.IsSupportedExtension(SelectedScriptPath);

    [RelayCommand(CanExecute = nameof(CanAddScript))]
    private async Task AddScriptAsync()
    {
        // Кнопка «Добавить» на время проверки сменяется спиннером.
        IsValidatingScript = true;
        try
        {
            var validation = await _userScriptStore.ValidateAsync(SelectedScriptPath).ConfigureAwait(true);
            if (!validation.Ok)
            {
                AppNotificationCenter.Instance.Push(
                    L.T("Скрипт не рабочий"),
                    validation.Message,
                    AppNotificationKind.Danger);
                _logger.Warn("USCRIPT | validation failed | " + SelectedScriptPath + " | " + validation.Message);
                return;
            }

            // Скрипт рабочий: мастер размещения (раздел → имя → комментарий/информер).
            ScriptPlacementRequested?.Invoke(SelectedScriptPath);
        }
        finally
        {
            IsValidatingScript = false;
        }
    }

    // Обрабатывается SettingsView: открывает мастер размещения карточки.
    public event Action<string>? ScriptPlacementRequested;

    [RelayCommand]
    private void ShowInstalledScripts() => InstalledScriptsRequested?.Invoke();
}
