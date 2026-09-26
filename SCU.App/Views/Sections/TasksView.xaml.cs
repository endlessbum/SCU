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

    // Объединённая подсказка ⓘ операций «Бэкап» и «Откатить» возле кнопок:
    // XAML не умеет конкатенировать DynamicResource, ключи читаются из текущего словаря строк.
    private void RefreshInfoTexts() =>
        BackupInfoGlyph.InfoText = InfoTexts.Join("I_TasksBackup", "I_TasksRestore");
}
