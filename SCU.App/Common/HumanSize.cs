using System.Globalization;

namespace SCU.Common;

// Единый форматтер размеров файлов (аудит UX: раньше три частные реализации
// давали «2048 KB» / «2.0 MB» / «2,0 МБ» в разных разделах). Число — по культуре
// интерфейса, единицы локализуются словарём («КБ/МБ/ГБ» → KB/MB/GB).
public static class HumanSize
{
    private const long Kb = 1024;
    private const long Mb = Kb * 1024;
    private const long Gb = Mb * 1024;

    public static string Format(long bytes)
    {
        return bytes >= Gb
            ? (bytes / (double)Gb).ToString("F1", CultureInfo.CurrentCulture) + " " + L.T("ГБ")
            : bytes >= Mb
                ? (bytes / (double)Mb).ToString("F1", CultureInfo.CurrentCulture) + " " + L.T("МБ")
                : bytes >= Kb
                    ? (bytes / (double)Kb).ToString("F0", CultureInfo.CurrentCulture) + " " + L.T("КБ")
                    : bytes + " " + L.T("байт");
    }
}
