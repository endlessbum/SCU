using System.Windows.Controls;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.Views.Sections;

public partial class ServicesView : UserControl
{
    public ServicesView()
    {
        InitializeComponent();
        RefreshInfoTexts();
        // Раздел живёт столько же, сколько приложение — отписка не требуется.
        L.LanguageChanged += RefreshInfoTexts;
    }

    // Объединённая подсказка ⓘ операций «Бэкап» и «Откатить» возле кнопок:
    // каждая строка помечена именем кнопки, к которой относится описание.
    private void RefreshInfoTexts() =>
        BackupInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_Backup"], "I_SvcBackup"),
            new InfoTexts.Attributed(["S_Restore"], "I_SvcRestore"));
}
