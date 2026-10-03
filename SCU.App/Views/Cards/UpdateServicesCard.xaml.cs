using System.Windows.Controls;

namespace SCU.Views.Cards;

// Карточка «Службы обновления» раздела «Обновления Windows». Вынесена для
// закрепления на главной: DataContext в разделе и на главной = UpdateViewModel.
public partial class UpdateServicesCard : UserControl
{
    public UpdateServicesCard()
    {
        InitializeComponent();
    }
}
