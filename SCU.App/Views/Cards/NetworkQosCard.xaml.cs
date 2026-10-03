using System.Windows.Controls;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.Views.Cards;

// Карточка «QoS-политика» раздела «Сеть». Вынесена для закрепления на главной:
// DataContext в разделе и на главной = NetworkViewModel.
public partial class NetworkQosCard : UserControl
{
    public NetworkQosCard()
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
        QosInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_NetQosZero", "S_NetQosTwenty"], "I_NetQosSet"),
            new InfoTexts.Attributed(["S_NetQosRemove"], "I_NetQosRemove"),
            new InfoTexts.Attributed(["S_NetQosRestore"], "I_NetQosRestore"));
    }
}
