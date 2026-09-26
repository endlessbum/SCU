using System.Windows;

namespace SCU.Views.Controls;

// Объединение нескольких подсказок ⓘ в один текст: каждая с новой строки,
// с префиксом «• ». XAML не умеет конкатенировать DynamicResource, поэтому
// ключи читаются из текущего словаря строк (Themes/Strings.*.xaml) — перевод
// берётся сам. При смене языка разделы пересобирают тексты повторным вызовом
// (подписка L.LanguageChanged в code-behind раздела).
public static class InfoTexts
{
    // При одиночном ключе маркер «• » не ставится — это не список.
    public static string Join(params string[] keys) => string.Join(
        "\n",
        keys.Select(key => (keys.Length > 1 ? "• " : string.Empty)
            + (Application.Current?.TryFindResource(key) as string ?? key)));
}
