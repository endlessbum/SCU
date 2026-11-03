using System.Windows;
using System.Windows.Controls;

namespace SCU.Common;

// Вспомогательное свойство ComboBox: подсказка в пустом редактируемом поле.
// Нужно редактору меню, где группа вводится вручную (дизайн, п. 5): без
// подсказки пустое поле неотличимо от «группы нет».
public static class ComboBoxAssist
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder",
        typeof(string),
        typeof(ComboBoxAssist),
        new PropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject element) =>
        (string)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string value) =>
        element.SetValue(PlaceholderProperty, value);
}
