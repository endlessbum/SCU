using System.Windows.Controls;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.Views.Sections;

public partial class PowerView : UserControl
{
    public PowerView()
    {
        InitializeComponent();
        RefreshInfoTexts();
        // Раздел живёт столько же, сколько приложение — отписка не требуется.
        L.LanguageChanged += RefreshInfoTexts;
    }

    // Объединённая подсказка ⓘ шапочных операций (схемы электропитания):
    // каждая строка помечена именем кнопки плана — при открытии видно,
    // какой комментарий к какой схеме относится.
    private void RefreshInfoTexts() =>
        HeaderInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_PlanHigh"], "I_PlanHigh"),
            new InfoTexts.Attributed(["S_PlanUltimate"], "I_PlanUltimate"),
            new InfoTexts.Attributed(["S_PlanBalanced"], "I_PlanBalanced"),
            new InfoTexts.Attributed(["S_PlanPowerSaver"], "I_PlanPowerSaver"),
            new InfoTexts.Attributed(["S_PlanBitsum"], "I_PlanBitsum"));
}
