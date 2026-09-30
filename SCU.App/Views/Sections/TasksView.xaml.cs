using System.Windows.Controls;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.Views.Sections;

public partial class TasksView : UserControl
{
    public TasksView()
    {
        InitializeComponent();
        RefreshInfoTexts();
        // Раздел живёт столько же, сколько приложение — отписка не требуется.
        L.LanguageChanged += RefreshInfoTexts;
    }

    // Объединённая подсказка ⓘ операций «Бэкап», «Откатить» и «Отключить все» возле кнопок:
    // каждая строка помечена именем кнопки, к которой относится описание.
    private void RefreshInfoTexts() =>
        BackupInfoGlyph.InfoText = InfoTexts.JoinAttributed(
            new InfoTexts.Attributed(["S_Backup"], "I_TasksBackup"),
            new InfoTexts.Attributed(["S_Rollback"], "I_TasksRestore"),
            new InfoTexts.Attributed(["S_TasksDisableAll"], "I_TasksDisableAll"));
}
