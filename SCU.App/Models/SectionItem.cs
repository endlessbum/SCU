using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using SCU.Common;

namespace SCU.Models;

// Элемент навигации. Title/Description хранят ключи Strings.ru/en.xaml
// (S_SectionNN_Title / S_SectionNN_Desc); тексты читаются из актуального словаря
// и перечитываются при смене языка интерфейса — как DynamicResource, но из
// ViewModel (уведомление через L.LanguageChanged).
public sealed class SectionItem : INotifyPropertyChanged
{
    public SectionItem(int number, string titleKey, string descriptionKey, string glyph,
        string? groupKey = null)
    {
        Number = number;
        Glyph = glyph;
        _titleKey = titleKey;
        _descriptionKey = descriptionKey;
        _groupKey = groupKey;
        // Раздел живёт столько же, сколько приложение — отписка не требуется.
        L.LanguageChanged += RefreshTexts;
        // Язык применяется до создания окна (событие уже прошло) — читаем тексты сами.
        RefreshTexts();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Number { get; }

    public string Glyph { get; }

    private readonly string _titleKey;
    private readonly string _descriptionKey;
    private readonly string? _groupKey;

    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _groupTitle = string.Empty;

    public string Title
    {
        get => _title;
        private set
        {
            _title = value;
            OnPropertyChanged(nameof(Title));
        }
    }

    public string Description
    {
        get => _description;
        private set
        {
            _description = value;
            OnPropertyChanged(nameof(Description));
        }
    }

    // П. 22 аудита: группа раздела в сайдбаре (пустая — без группировки).
    public string GroupTitle
    {
        get => _groupTitle;
        private set
        {
            _groupTitle = value;
            OnPropertyChanged(nameof(GroupTitle));
        }
    }

    private void RefreshTexts()
    {
        Title = Resolve(_titleKey);
        Description = Resolve(_descriptionKey);
        GroupTitle = _groupKey is null ? string.Empty : Resolve(_groupKey);
    }

    // Ключ лежит в Themes/Strings.*.xaml: словарь подменяется при смене языка,
    // поэтому читаем по ключу из актуального Application-словаря. Отсутствующий
    // ключ (например, раздел без описания) — пустая строка, а не имя ключа.
    private static string Resolve(string key) =>
        Application.Current?.TryFindResource(key) as string ?? string.Empty;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
