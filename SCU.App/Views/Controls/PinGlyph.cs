using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SCU.Views.Controls;

// Присоединённое свойство: CardId карточки-контейнера для шаблонных карточек
// (ContentControl со стилем SwitchRowCard). Шаблон стиля передаёт его в глиф:
// <controls:PinGlyph CardId="{Binding Path=(controls:PinCard.CardId),
// RelativeSource={RelativeSource TemplatedParent}}" />. Отдельный класс — имя
// «CardId» у PinGlyph занято его собственным DP.
public static class PinCard
{
    public static readonly DependencyProperty CardIdProperty = DependencyProperty.RegisterAttached(
        "CardId", typeof(string), typeof(PinCard), new PropertyMetadata(string.Empty));

    public static void SetCardId(DependencyObject element, string value) =>
        element.SetValue(CardIdProperty, value);

    public static string GetCardId(DependencyObject element) =>
        (string)element.GetValue(CardIdProperty);
}

// «Скрепка» закрепления карточки на главной (п. ТЗ «Закрепление карточек»):
// ToggleButton-глиф по образцу InfoGlyph. Обычное состояние — приглушённый цвет
// неактивного информера (TertiaryTextBrush), закреплённое — акцентный цвет из
// настроек (AccentFillBrush, обновляется через DynamicResource при смене акцента).
// Состояние живёт в PinState по CardId, поэтому все скрепки одной карточки
// (в разделе и дубликат на главной) синхронизированы автоматически.
public sealed class PinGlyph : ToggleButton
{
    public static readonly DependencyProperty CardIdProperty = DependencyProperty.Register(
        nameof(CardId), typeof(string), typeof(PinGlyph),
        new PropertyMetadata(string.Empty, OnCardIdChanged));

    public string CardId
    {
        get => (string)GetValue(CardIdProperty);
        set => SetValue(CardIdProperty, value);
    }

    private bool _subscribed;

    public PinGlyph()
    {
        IsThreeState = false;
        Focusable = true;
        ToolTip = L.T("Закрепить карточку на главной");
        Click += OnClick;
        // Подписки на статику — только на время жизни в дереве: откреплённый
        // дубликат с «Главной» не должен удерживаться статическими событиями
        // вместе с карточкой. Тултип и имя автоматизации при возврате в дерево
        // обновляет SyncChecked.
        Loaded += (_, _) =>
        {
            L.LanguageChanged += OnLanguageChanged;
            Subscribe();
            SyncChecked();
        };
        Unloaded += (_, _) =>
        {
            L.LanguageChanged -= OnLanguageChanged;
            Unsubscribe();
        };
    }

    private static void OnCardIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((PinGlyph)d).SyncChecked();
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        // Глиф без CardId (шаблонная карточка, которой закрепление не положено)
        // ничего не делает: возвращаем визуал в состояние из PinState.
        if (string.IsNullOrEmpty(CardId))
        {
            SyncChecked();
            return;
        }

        PinState.Instance.Set(CardId, IsChecked == true);
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        PinState.Instance.CardPinChanged += OnPinChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        PinState.Instance.CardPinChanged -= OnPinChanged;
        _subscribed = false;
    }

    private void OnPinChanged(string cardId)
    {
        if (string.Equals(cardId, CardId, StringComparison.OrdinalIgnoreCase))
        {
            SyncChecked();
        }
    }

    private void OnLanguageChanged() => SyncChecked();

    // Источник истины — PinState: синхронизируем визуал без ThreeState-скачков.
    private void SyncChecked()
    {
        var pinned = !string.IsNullOrEmpty(CardId) && PinState.Instance[CardId];
        if (IsChecked != pinned)
        {
            IsChecked = pinned;
        }

        var tooltip = L.T(pinned ? "Открепить карточку" : "Закрепить карточку на главной");
        ToolTip = tooltip;
        // Имя для скринридеров — синхронно с тултипом (как у InfoGlyph).
        SetValue(AutomationProperties.NameProperty, tooltip);
    }
}
