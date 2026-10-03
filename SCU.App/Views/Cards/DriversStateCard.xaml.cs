using System.Windows.Controls;

namespace SCU.Views.Cards;

// Карточка «Состояние драйверов» раздела «Драйверы». Вынесена для закрепления
// на главной: DataContext в разделе и на главной = DriversViewModel.
public partial class DriversStateCard : UserControl
{
    public DriversStateCard()
    {
        InitializeComponent();
    }
}
