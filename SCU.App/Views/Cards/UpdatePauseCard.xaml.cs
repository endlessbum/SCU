using System.Windows.Controls;

namespace SCU.Views.Cards;

// Карточка «Пауза обновлений» раздела «Обновления Windows». Вынесена для
// закрепления на главной: DataContext в разделе и на главной = UpdateViewModel.
public partial class UpdatePauseCard : UserControl
{
    public UpdatePauseCard()
    {
        InitializeComponent();
    }
}
