using System.Windows.Controls;

namespace SCU.Views.Cards;

// Карточка «Проверка целостности» раздела «Поиск и целостность». Вынесена для
// закрепления на главной: DataContext в разделе и на главной = MaintenanceViewModel.
public partial class MaintenanceIntegrityCard : UserControl
{
    public MaintenanceIntegrityCard()
    {
        InitializeComponent();
    }
}
