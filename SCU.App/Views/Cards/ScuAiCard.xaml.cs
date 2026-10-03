using System.Windows.Controls;

namespace SCU.Views.Cards;

// Карточка «AI-помощник SCU» раздела «AI». Вынесена для закрепления на главной:
// DataContext в разделе и на главной = DeepSeekViewModel (кнопка активна только
// при подключённом API).
public partial class ScuAiCard : UserControl
{
    public ScuAiCard()
    {
        InitializeComponent();
    }
}
