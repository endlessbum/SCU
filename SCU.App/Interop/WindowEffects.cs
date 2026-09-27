using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SCU.Interop;

// DWM-эффекты системного окна Windows 11: бэкдроп Mica, скруглённые углы (8 px)
// и тёмная строка заголовка. Тень и скругление целиком от DWM — никаких
// DropShadowEffect и CornerRadius у корневого контейнера.
// При отсутствии поддержки (RDP, старые сборки, отключённые эффекты)
// вызывающий код откатывается на непрозрачный фон — окно остаётся рабочим.
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 3819;

    private const int DwmwcpRound = 2;

    private const int DwmsbtNone = 1;
    private const int DwmsbtMainwindow = 2;
    private const int DwmsbtTabbedwindow = 4;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    // Цвет строки заголовка = цвету фона приложения (SolidRootBrush текущей
    // темы): заголовок перестаёт выделяться на фоне содержимого. Атрибут
    // DWMWA_CAPTION_COLOR поддерживается Windows 11; на старых системах
    // вызов молча пропускается — заголовок остаётся системным.
    // COLORREF кодируется как 0x00BBGGRR.
    public static void ApplyCaptionColor(Window window, bool isDarkTheme)
    {
        UpdateDarkMode(window, isDarkTheme);
        try
        {
            if (Application.Current?.TryFindResource("SolidRootBrush") is not SolidColorBrush brush)
            {
                return;
            }

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var colorref = brush.Color.R | (brush.Color.G << 8) | (brush.Color.B << 16);
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorref, sizeof(int));
        }
        catch
        {
            // Нет dwmapi/старая система — заголовок системного цвета, не критично.
        }
    }

    // Включает Mica (DWMSBT_TABBEDWINDOW), скругление и тёмный режим DWM.
    // Возвращает false, если системный бэкдроп недоступен — тогда окно
    // красится непрозрачным SolidRootBrush вместо прозрачного фона.
    public static bool TryApplyMica(Window window, bool isDarkTheme)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();

            SetDarkMode(hwnd, isDarkTheme);

            // Стекло растягивается на всю клиентскую область: прозрачные места
            // WPF-поверхности показывают бэкдроп (без этого останется чёрный фон).
            var sheet = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref sheet);

            var preference = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));

            var backdrop = DwmsbtTabbedwindow;
            if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0)
            {
                return true;
            }

            backdrop = DwmsbtMainwindow;
            if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0)
            {
                return true;
            }

            backdrop = DwmsbtNone;
            DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            return false;
        }
        catch
        {
            return false;
        }
    }

    // Синхронизирует тёмный режим DWM (цвет глифов строки заголовка) с темой приложения.
    public static void UpdateDarkMode(Window window, bool isDarkTheme)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            SetDarkMode(hwnd, isDarkTheme);
        }
        catch
        {
            // Окно ещё не имеет дескриптора или DWM недоступен — не критично.
        }
    }

    private static void SetDarkMode(IntPtr hwnd, bool isDarkTheme)
    {
        var dark = isDarkTheme ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
        }
    }
}
