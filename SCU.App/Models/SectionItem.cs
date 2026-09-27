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
        : this(number, titleKey, descriptionKey, glyph, groupKey, isRaw: false)
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Number { get; }

    public string Glyph { get; }

    private readonly string _titleKey;
    private readonly string _descriptionKey;
    private readonly string? _groupKey;

    // Кастомизация меню («Редактирование меню» в «Настройках»): переопределение
    // заголовка/группы (пользовательские вкладки создаются через CreateCustom —
    // их ключи являются готовым текстом, а не ключами словаря).
    private string? _titleOverride;
    private string? _groupOverride;
    private bool _isRaw;

    public string? TitleOverride
    {
        get => _titleOverride;
        set
        {
            _titleOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            RefreshTexts();
        }
    }

    public string? GroupOverride
    {
        get => _groupOverride;
        set
        {
            _groupOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            RefreshTexts();
        }
    }

    public bool IsCustom => _isRaw;

    // Пользовательская вкладка: заголовок и группа — готовый текст.
    public static SectionItem CreateCustom(int number, string title, string group) =>
        new(number, title, string.Empty, "", group, isRaw: true);

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

    private SectionItem(int number, string titleKey, string descriptionKey, string glyph,
        string? groupKey, bool isRaw)
    {
        Number = number;
        Glyph = glyph;
        _titleKey = titleKey;
        _descriptionKey = descriptionKey;
        _groupKey = groupKey;
        _isRaw = isRaw;
        // Раздел живёт столько же, сколько приложение — отписка не требуется.
        L.LanguageChanged += RefreshTexts;
        // Язык применяется до создания окна (событие уже прошло) — читаем тексты сами.
        RefreshTexts();
    }

    private void RefreshTexts()
    {
        Title = _titleOverride ?? (_isRaw ? _titleKey : Resolve(_titleKey));
        Description = Resolve(_descriptionKey);
        GroupTitle = _groupOverride
            ?? (_groupKey is null
                ? string.Empty
                : _isRaw ? _groupKey : Resolve(_groupKey));
    }

    // Ключ лежит в Themes/Strings.*.xaml: словарь подменяется при смене языка,
    // поэтому читаем по ключу из актуального Application-словаря. Отсутствующий
    // ключ (например, раздел без описания) — пустая строка, а не имя ключа.
    private static string Resolve(string key) =>
        Application.Current?.TryFindResource(key) as string ?? string.Empty;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
