using System.Windows;
using System.Windows.Controls;
using SCU.Common;
using SCU.Models;
using SCU.ViewModels;

namespace SCU.Views;

// Мастер размещения карточки пользовательского скрипта после успешной проверки:
// шаг 1 — раздел меню, шаг 2 — имя карточки, шаг 3 — комментарий и информер ⓘ.
public partial class ScriptWizardWindow : Window
{
    private readonly MainViewModel _main;
    private readonly string _sourcePath;
    private int _step;

    public SectionItem? SelectedPlacement { get; set; }

    public ScriptWizardWindow(MainViewModel main, string sourcePath)
    {
        InitializeComponent();
        _main = main;
        _sourcePath = sourcePath;
        SectionCombo.ItemsSource = main.Sections;
        SectionCombo.SelectedIndex = 0;
        TitleBox.Text = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        _step = step;
        ((StackPanel)Root.Children[0]).Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        ((StackPanel)Root.Children[1]).Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        ((StackPanel)Root.Children[2]).Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = step > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = step < 2 ? L.T("Далее") : L.T("Готово");
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
        {
            ShowStep(_step - 1);
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 0 when SectionCombo.SelectedItem is not SectionItem:
                return;
            case 0:
                ShowStep(1);
                return;

            case 1 when TitleBox.Text.Trim().Length == 0:
                return;
            case 1:
                ShowStep(2);
                return;

            case 2:
                _main.AddUserScript(
                    _sourcePath,
                    (SectionCombo.SelectedItem as SectionItem)?.Number ?? 0,
                    TitleBox.Text.Trim(),
                    CommentBox.Text.Trim(),
                    TooltipBox.Text.Trim());
                Close();
                break;
        }
    }
}
