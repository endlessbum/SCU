using System.Windows.Controls;

namespace SCU.Views.Cards;

// Карточка «DNS» раздела «Сеть». Вынесена для закрепления на главной:
// DataContext в разделе и на главной = NetworkViewModel.
public partial class NetworkDnsCard : UserControl
{
    public NetworkDnsCard()
    {
        InitializeComponent();
    }
}
