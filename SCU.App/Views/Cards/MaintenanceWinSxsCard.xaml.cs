using System.Windows.Controls;

namespace SCU.Views.Cards;

// Карточка «Хранилище компонентов (WinSxS)» раздела «Поиск и целостность».
// Вынесена для закрепления на главной: DataContext в разделе и на главной =
// MaintenanceViewModel.
public partial class MaintenanceWinSxsCard : UserControl
{
    public MaintenanceWinSxsCard()
    {
        InitializeComponent();
    }
}
