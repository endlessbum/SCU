using System.ComponentModel;
using System.Runtime.CompilerServices;
using SCU.Common;

namespace SCU.ViewModels.Sections;

// Строка-тумблер для разделов UI/Input. Ручной INPC: после операции состояние
// может совпасть со старым — ForceState обязан уведомить тумблер в любом случае.
//
// Подпись под заголовком — постоянное описание функции (назначение, влияние),
// не зависит от ON/OFF и не использует формулировки «Включить:» / «Отключить:».
public sealed class SwitchRow : INotifyPropertyChanged
{
    private bool _isOn;
    private bool _stateUnknown;

    public SwitchRow(string id, string title, string description, bool isOn, bool onMeansEnable = false)
    {
        Id = id;
        Title = title;
        Description = description;
        OnMeansEnable = onMeansEnable;
        _isOn = isOn;
        L.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(ActionDescription));
        OnPropertyChanged(nameof(LocalizedTitle));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    // Id закрепления карточки на «Главной» («input.<id>»); пусто — скрепка
    // не показывается (карточка не подлежит закреплению). Заполняет раздел.
    public string PinCardId { get; set; } = string.Empty;

    // Заголовок на языке интерфейса: Title хранит русский ключ для L.T.
    public string LocalizedTitle => L.T(Title);

    public string Title { get; }

    public string Description { get; }

    // Сохраняется для совместимости с вызывающим кодом (семантика тумблера:
    // true = ON означает «функция работает»). На текст подписи больше не влияет.
    public bool OnMeansEnable { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            _isOn = value;
            OnPropertyChanged(nameof(IsOn));
            // ActionDescription не зависит от IsOn — уведомление не требуется.
        }
    }

    public void ForceState(bool isOn)
    {
        _isOn = isOn;
        OnPropertyChanged(nameof(IsOn));
    }

    // Состояние не прочитано (сбой чтения): тумблер показывает последнее
    // известное значение, а бейдж «?» — что факт неизвестен. Успешное чтение
    // или переключение флаг снимает.
    public bool StateUnknown
    {
        get => _stateUnknown;
        set
        {
            _stateUnknown = value;
            OnPropertyChanged(nameof(StateUnknown));
        }
    }

    public string StateUnknownTooltip => L.T("Состояние не прочитано");

    /// <summary>
    /// Постоянное описание функции. Не «Включить:/Отключить:» — только суть настройки.
    /// </summary>
    public string ActionDescription => Description ?? string.Empty;

    public bool HasActionDescription => !string.IsNullOrEmpty(Description);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
