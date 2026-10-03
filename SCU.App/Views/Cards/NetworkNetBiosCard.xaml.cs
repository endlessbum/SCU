using System.Windows.Controls;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.Views.Cards;

// Карточка «NetBIOS over TCP/IP» раздела «Сеть». Вынесена для закрепления на главной:
// DataContext в разделе и на главной = NetworkViewModel.
public partial class NetworkNetBiosCard : UserControl
{
    public NetworkNetBiosCard()
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
        NetBiosInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_NetNetBiosDisable", "S_NetNetBiosDhcp"], "I_NetNetBiosMode"),
            new InfoTexts.Attributed(["S_NetNetBiosFlush"], "I_NetNetBiosFlush"));
    }
}
