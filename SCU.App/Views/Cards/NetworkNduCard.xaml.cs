using System.Windows.Controls;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.Views.Cards;

// Карточка «Служба NDU» раздела «Сеть». Вынесена для закрепления на главной:
// DataContext в разделе и на главной = NetworkViewModel.
public partial class NetworkNduCard : UserControl
{
    public NetworkNduCard()
    {
        InitializeComponent();
        RefreshInfoText();
        // Подписка только на время жизни в дереве: откреплённый дубликат с
        // «Главной» не должен оставлять мёртвый обработчик в статике L.
        // RefreshInfoText при Loaded — текст мог устареть, пока карточка была
        // вне дерева (смена языка на скрытой вкладке).
        Loaded += (_, _) => { RefreshInfoText(); L.LanguageChanged += RefreshInfoText; };
        Unloaded += (_, _) => L.LanguageChanged -= RefreshInfoText;
    }

    private void RefreshInfoText()
    {
        NduInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_NetNduDisable", "S_NetNduEnable"], "I_NetNduToggle"));
    }
}
