using System.Windows;

namespace SCU.Views.Controls;

// Сборка текстов ⓘ-информеров из строковых ресурсов. XAML не умеет
// конкатенировать DynamicResource, поэтому ключи читаются из текущего словаря
// строк (Themes/Strings.*.xaml) — перевод берётся сам. При смене языка разделы
// пересобирают тексты повторным вызовом (подписка L.LanguageChanged в code-behind).
public static class InfoTexts
{
    // Строка атрибуции: имена кнопок (ключи S_*) + описание действия (ключ I_*).
    // Одно описание может относиться к группе однотипных кнопок — несколько S_*.
    public readonly record struct Attributed(string[] ActionKeys, string InfoKey);

    // Объединённая подсказка с атрибуцией: каждая строка начинается с имени
    // кнопки в кавычках — при открытии видно, какой комментарий к какому
    // действию относится. Пример строки: «Бэкап» — Сохраняет текущий тип запуска…
    public static string JoinAttributed(params Attributed[] items) => string.Join(
        "\n",
        items.Select(item =>
            "«" + string.Join(" / ", item.ActionKeys.Select(Resolve)) + "» — "
            + Resolve(item.InfoKey)));

    private static string Resolve(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
