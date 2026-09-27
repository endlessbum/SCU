using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SCU.Common;

namespace SCU.ViewModels.Sections;

// Утилита пакетного запуска (большой выключатель «Состояния ПК»): кнопка или
// тумблер одного из разделов. Id — стабильный ключ для сохранения включённости;
// TitleKey — ключ Strings.ru/en.xaml либо (IsRawTitle) русский текст для L.T.
// Run выполняет утилиту и возвращает статус раздела; null = пропущена
// (раздел занят, нет прав или утилита неприменима в текущем состоянии).
public sealed class BatchUtility
{
    public required string Id { get; init; }

    public required string TitleKey { get; init; }

    // Родной раздел утилиты (из реестра); проставляется при построении реестра
    // и не меняется — используется для возврата при удалении пользовательской
    // вкладки и сбросе меню.
    public int OriginalSection { get; internal set; }

    // Номер раздела-владельца: группировка списка настроек и заголовок группы.
    // Изменяется при перемещении утилиты в пользовательскую вкладку
    // («Редактирование меню») — глобальный поиск находит её там, где она теперь.
    public required int Section { get; set; }

    // true — TitleKey это ключ XAML-словаря; false — русский текст, перевод через L.T.
    public bool IsRawTitle { get; init; }

    // Целевое состояние для тумблерных утилит («вкл.»/«выкл.»); null — кнопка/пресет.
    public string? TargetText { get; init; }

    // Дополнительные слова поиска: стемы на русском и английском через пробел
    // (поиск разворачивает запрос и по словарю синонимов, и по этим словам).
    public string Keywords { get; init; } = string.Empty;

    // Проверка «можно ли применить прямо сейчас»: true — целевое состояние ещё
    // не достигнуто (показывается в «К применению»), false — уже применено,
    // null — для кнопок-операций проверить нельзя (очистки, DISM, бэкапы…).
    public Func<bool?>? NeedsApply { get; init; }

    public required Func<Task<string?>> Run { get; init; }

    public string ResolveTitle() =>
        IsRawTitle ? L.T(TitleKey) : Application.Current?.TryFindResource(TitleKey) as string ?? TitleKey;

    // Для биндингов DisplayMemberPath (редактор меню).
    public string Title => ResolveTitle();
}

// Строка списка настроек: включённость в пакет (сохраняется) и результат
// последнего запуска. Заголовок перечитывается при смене языка.
public sealed partial class BatchUtilityRow : ObservableObject
{
    public BatchUtilityRow(BatchUtility utility, bool isIncluded)
    {
        Utility = utility;
        _isIncluded = isIncluded;
        // Строки живут столько же, сколько приложение — отписка не требуется
        // (тот же подход, что у SectionItem в MainViewModel).
        L.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(TargetText));
        };
    }

    public BatchUtility Utility { get; }

    public string Title => Utility.ResolveTitle();

    // Целевое состояние тумблерной утилиты («вкл.»/«выкл.») или пустая строка у кнопок.
    public string TargetText => Utility.TargetText is null
        ? string.Empty
        : Application.Current?.TryFindResource(Utility.TargetText) as string ?? Utility.TargetText;

    [ObservableProperty]
    private bool _isIncluded;

    // Видимость строки при фильтре поиска.
    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private bool _hasResult;

    public void ResetResult()
    {
        ResultText = null;
        HasResult = false;
    }
}

// Группа утилит одного раздела в списке настроек.
public sealed class BatchGroup : INotifyPropertyChanged
{
    public BatchGroup(int sectionNumber)
    {
        SectionNumber = sectionNumber;
        // Подписка живёт столько же, сколько приложение.
        L.LanguageChanged += () => OnPropertyChanged(nameof(Title));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SectionNumber { get; }

    public string Title =>
        Application.Current?.TryFindResource($"S_Section{SectionNumber:00}_Title") as string
        ?? $"S_Section{SectionNumber:00}_Title";

    public ObservableCollection<BatchUtilityRow> Rows { get; } = [];

    // Видимость группы при фильтре поиска (есть хотя бы одна видимая строка).
    private bool _isVisible = true;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
