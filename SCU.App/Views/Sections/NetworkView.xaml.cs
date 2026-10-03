using System.Windows.Controls;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.Views.Sections;

public partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();
        RefreshInfoTexts();
        // Раздел живёт столько же, сколько приложение — отписка не требуется.
        L.LanguageChanged += RefreshInfoTexts;
    }

    // Объединённая подсказка ⓘ шапки. Подсказки карточек TCP/MTU/QoS/NetBIOS
    // наполняются кодом самих карточек (Views/Cards) — они же на «Главной».
    private void RefreshInfoTexts()
    {
        HeaderInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_NetResetAll"], "I_NetResetAll"));
    }
}
