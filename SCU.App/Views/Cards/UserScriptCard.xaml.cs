using System.Windows;
using System.Windows.Controls;

namespace SCU.Views.Cards;

// Общая карточка пользовательского скрипта (см. комментарий в XAML):
// заголовок, информер ⓘ, скрепка закрепления, комментарий, результат,
// спиннер выполнения и кнопка запуска.
public partial class UserScriptCard : UserControl
{
    public UserScriptCard()
    {
        InitializeComponent();
    }

    // Запрос запуска скрипта: подписчик решает, что выполнять — полоса
    // раздела запускает свою копию карточки, дубликат на «Главной» —
    // оригинал через MainViewModel.RunUserScriptAsync.
    public event EventHandler? RunRequested;

    private void OnRunClick(object sender, RoutedEventArgs e) =>
        RunRequested?.Invoke(this, EventArgs.Empty);
}
