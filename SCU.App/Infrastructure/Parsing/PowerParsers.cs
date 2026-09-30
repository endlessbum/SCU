namespace SCU.Infrastructure.Parsing;

// П. 13 аудита: чистые парсеры вывода powercfg/bcdedit/fsutil — без I/O,
// легко тестируются. Вынесены из PowerService.
internal static class PowerParsers
{
    // Сырой вывод ("ACPI\ThermalZone\TZ00_0: 27,9 C") → «27,9°» первой зоны.
    // null — датчики не отвечают (не подставляем 0 °C).
    internal static string? FormatThermalZoneTemperature(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('('))
        {
            return null;
        }

        var first = raw.Split('\n')[0].Trim();
        var colon = first.LastIndexOf(':');
        var value = (colon >= 0 ? first[(colon + 1)..] : first).Trim();
        if (value.EndsWith(" C", StringComparison.Ordinal))
        {
            value = value[..^2].TrimEnd();
        }

        // Строка без двоеточия: берём последний токен, содержащий цифру.
        if (colon < 0)
        {
            var last = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(t => t.Any(char.IsDigit));
            value = last ?? value;
        }

        return value + "°";
    }

    internal static string? ExtractBcdValue(string output, string valueName)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(valueName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[^1] : null;
        }

        return null;
    }

    internal static bool ContainsBcdValue(string output, string valueName)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(valueName + " ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // fsutil 8dot3name query: "The volume state is: 0 (enabled)" / "1 (disabled)"
    // или локализованный аналог. null — не удалось распознать.
    internal static bool? TryParseVolumeStateEnabled(string text)
    {
        var markers = new[] { "volume state is:", "состояние тома:" };
        foreach (var marker in markers)
        {
            var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var tail = text[(idx + marker.Length)..];
            var digit = tail.SkipWhile(c => !char.IsDigit(c)).FirstOrDefault();
            if (digit == '0') return true;
            if (digit == '1') return false;
        }

        if (text.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.Contains("enabled", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return null;
    }

    // fsutil behavior query disablelastaccess: строка вида "DisableLastAccess = 1".
    internal static int? TryParseFsutilValue(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var rhs = line[(eq + 1)..].Trim();
            if (int.TryParse(rhs, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                return n;
            }
        }

        return null;
    }
}
