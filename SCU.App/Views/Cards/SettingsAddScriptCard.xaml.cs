using System.Windows.Controls;

namespace SCU.Views.Cards;

// Карточка «Добавить свой скрипт» раздела «Настройки». Вынесена для закрепления
// на главной: DataContext в разделе и на главной = MainViewModel (у «Настроек»
// он и есть MainViewModel).
public partial class SettingsAddScriptCard : UserControl
{
    public SettingsAddScriptCard()
    {
        InitializeComponent();
    }
}
