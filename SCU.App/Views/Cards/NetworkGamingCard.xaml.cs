using System.Windows.Controls;

namespace SCU.Views.Cards;

// Карточка «Профиль для игр» раздела «Сеть»: вынесена для переиспользования —
// та же карточка живёт в разделе и (при закреплении скрепкой) на главной,
// DataContext в обоих местах = NetworkViewModel.
public partial class NetworkGamingCard : UserControl
{
    public NetworkGamingCard()
    {
        InitializeComponent();
    }
}
