using System.Windows.Controls;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.Views.Cards;

// Карточка «MTU интерфейсов» раздела «Сеть». Вынесена для закрепления на главной:
// DataContext в разделе и на главной = NetworkViewModel.
public partial class NetworkMtuCard : UserControl
{
    public NetworkMtuCard()
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
        MtuInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_NetMtu1500", "S_NetMtu1472", "S_NetMtu1400"], "I_NetMtu"),
            new InfoTexts.Attributed(["S_NetMtuCustom"], "I_NetMtuCustom"),
            new InfoTexts.Attributed(["S_NetMtuRestore"], "I_NetMtuRestore"));
    }
}
