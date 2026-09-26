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
    // XAML не умеет конкатенировать DynamicResource, ключи читаются из текущего
    // словаря строк.
    private void RefreshInfoTexts() =>
        HeaderInfoGlyph.InfoText = InfoTexts.Join(
            "I_PlanHigh",
            "I_PlanUltimate",
            "I_PlanBalanced",
            "I_PlanPowerSaver",
            "I_PlanBitsum");
}
