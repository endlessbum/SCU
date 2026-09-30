namespace SCU.Infrastructure.Browser;

// Решение политики навигации встроенного браузера.
public enum BrowserNavigationDecision
{
    Allow,
    Block,
    ExternalWithConfirmation,
}

// Политика навигации: web-контент недоверен. http/https разрешены, опасные и
// нативные схемы блокируются, внешние протоколы (mailto:, tel:, ms-settings:)
// запускаются ТОЛЬКО после явного подтверждения пользователя.
public static class BrowserNavigationPolicy
{
    private static readonly string[] BlockedSchemes =
    [
        "file", "javascript", "vbscript", "powershell", "cmd", "shell", "res", "ms-help",
        "view-source", "jar", "blob", "chrome", "edge", "ms-gamingoverlay", "data",
    ];

    // Схемы, открывающие внешнее приложение; молчаливый запуск запрещён.
    private static readonly string[] ExternalSchemes =
    [
        "mailto", "tel", "sms", "ms-settings", "microsoft-edge", "skype", "whatsapp",
    ];

    public static BrowserNavigationDecision Evaluate(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return BrowserNavigationDecision.Block;
        }

        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed))
        {
            return BrowserNavigationDecision.Block;
        }

        var scheme = parsed.Scheme.ToLowerInvariant();

        // about:blank — служебная заглушка WebView2 (пустые popup'ы и т.п.).
        // Прочие about: и любые data:-навигации блокируются: data:-страница —
        // полностью атакер-контент на «пустом» адресе (фишинг без индикатора
        // происхождения, обход URL-фильтров). Локальная стартовая страница
        // обслуживается virtual host'ом https://scu.example и в this не нуждается.
        if (scheme == "about")
        {
            return IsAboutBlank(parsed) ? BrowserNavigationDecision.Allow : BrowserNavigationDecision.Block;
        }

        if (scheme is "http" or "https")
        {
            return BrowserNavigationDecision.Allow;
        }

        if (BlockedSchemes.Contains(scheme))
        {
            return BrowserNavigationDecision.Block;
        }

        if (ExternalSchemes.Contains(scheme))
        {
            return BrowserNavigationDecision.ExternalWithConfirmation;
        }

        // Неизвестная схема — потенциально внешнее приложение: без подтверждения не запускаем.
        return BrowserNavigationDecision.ExternalWithConfirmation;
    }

    private static bool IsAboutBlank(Uri uri) =>
        string.Equals(uri.AbsoluteUri.TrimEnd('/'), "about:blank", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.AbsoluteUri, "about:blank/", StringComparison.OrdinalIgnoreCase);

    // Адресная строка: URL или поисковый запрос (Google — по умолчанию).
    public static string ResolveAddress(string input)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https")
        {
            return parsed.ToString();
        }

        // «example.com» без схемы — считаем адресом, а не поиском.
        if (!trimmed.Contains(' ')
            && trimmed.Contains('.')
            && Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var host)
            && host.HostNameType == UriHostNameType.Dns
            && host.Host.Contains('.'))
        {
            return host.ToString();
        }

        return BuildSearchUrl(trimmed);
    }

    public static string BuildSearchUrl(string query) =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString(query.Trim());
}
