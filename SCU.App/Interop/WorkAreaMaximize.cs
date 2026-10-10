using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SCU.Interop;

// Единое правило заголовка окна (п. 8): контроль минимального размера окна
// (WM_GETMINMAXINFO, п. 13 аудита) и подгонка разворачивания под рабочую область.
//
// О задгонке разворачивания: Windows 11 размещает максимизированное окно с
// выходом на толщину невидимой рамки (8 px) за края экрана, и этот rect
// контролируется системой — правки MINMAXINFO и SetWindowPos по факту
// игнорируются (проверено: SetWindowPos возвращает true, rect не меняется,
// в том числе из внешнего процесса). Видимая артефакт-полоса рамки сверху
// убирается перекраской DWM-границы окна в цвет заголовка — см.
// WindowEffects.ApplyCaptionColor (DWMWA_BORDER_COLOR).
internal static class WorkAreaMaximize
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int GWLP_WNDPROC = -4;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    // Делегаты держим живыми всё время существования окна.
    private static readonly Dictionary<IntPtr, WndProcDelegate> _delegates = [];

    public static void Attach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (_delegates.ContainsKey(hwnd))
        {
            return;
        }

        var original = GetWindowLongPtr(hwnd, GWLP_WNDPROC);
        if (original == IntPtr.Zero)
        {
            return;
        }

        WndProcDelegate proc = (h, msg, wParam, lParam) => WndProc(h, msg, wParam, lParam, original, window);
        _delegates[hwnd] = proc;
        SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
    }

    // Парный Attach: сабклассинг снимается при закрытии окна, иначе хук живёт
    // на мёртвом HWND, а делегат остаётся закреплённым нативным кодом.
    public static void Detach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !_delegates.Remove(hwnd, out var proc))
        {
            return;
        }

        // Восстановление оригинального proc: после этого делегат больше не
        // нужен нативному коду и может быть собран обычным порядком.
        SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, IntPtr original, Window window)
    {
        if (msg == WmGetMinMaxInfo)
        {
            // Сначала собственная обработка WPF (max-track-size и др.), затем
            // точечная правка min-track-size поверх её результата: замена proc'а
            // без вызова оригинала глушит обработку WPF целиком.
            _ = CallWindowProc(original, hwnd, msg, wParam, lParam);

            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            // Минимальный размер окна: без этого ptMinTrackSize остаётся нулевым и
            // отключает проверку MinWidth/MinHeight — окно сжимается почти в точку.
            // Значения XAML в DIU переводятся в физические пиксели по текущему DPI.
            var dpi = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice
                      ?? Matrix.Identity;
            info.ptMinTrackSize = new POINT
            {
                X = (int)Math.Ceiling(window.MinWidth * dpi.M11),
                Y = (int)Math.Ceiling(window.MinHeight * dpi.M22),
            };

            Marshal.StructureToPtr(info, lParam, true);
            return IntPtr.Zero;
        }

        return CallWindowProc(original, hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
