using System.Windows.Controls;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.Views.Cards;

// Карточка «TCP Global» раздела «Сеть». Вынесена для закрепления на главной:
// DataContext в разделе и на главной = NetworkViewModel. Объединённая подсказка
// ⓘ наполняется кодом (как раньше делал NetworkView).
public partial class NetworkTcpCard : UserControl
{
    public NetworkTcpCard()
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
        TcpInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_NetApplyAutoTuning"], "I_NetApplyAutoTuning"),
            new InfoTexts.Attributed(["S_NetApplyEcn"], "I_NetApplyEcn"));
    }
}
