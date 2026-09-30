using System.Windows.Controls;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.Views.Sections;

public partial class UIView : UserControl
{
    public UIView()
    {
        InitializeComponent();
        RefreshInfoTexts();
        // Раздел живёт столько же, сколько приложение — отписка не требуется.
        L.LanguageChanged += RefreshInfoTexts;
    }

    // Объединённая подсказка ⓘ шапочных операций возле кнопок:
    // каждая строка помечена именем кнопки, к которой относится описание.
    private void RefreshInfoTexts() =>
        HeaderInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_UiMenuDelay20", "S_UiMenuDelay400"], "I_UiMenuDelay"),
            new InfoTexts.Attributed(["S_UiRestartExplorer"], "I_UiRestartExplorer"),
            new InfoTexts.Attributed(["S_UiClearTaskbar"], "I_UiClearTaskbar"),
            new InfoTexts.Attributed(["S_UiRestoreTaskbar"], "I_UiRestoreTaskbar"));
}
