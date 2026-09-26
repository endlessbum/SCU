namespace SCU.Interop;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

// П. 13 аудита: разворачивание строго в рабочую область монитора, на котором
// стоит окно, и минимальный размер окна (WndProc WM_GETMINMAXINFO),
// вынесено из MainWindow.
internal sealed class WindowMinMaxHandler
{
    private const int WmGetMinMaxInfo = 0x0024;

    private readonly Window _owner;

    public WindowMinMaxHandler(Window owner)
    {
        _owner = owner;
    }

    // Разворачивание строго в рабочую область монитора, на котором стоит окно
    // (не накрывает панель задач). SystemParameters.WorkArea — только первичный
    // монитор: на втором мониторе maximise уезжал бы в чужие координаты.
    internal IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var monitorInfo = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };
        var workArea = GetMonitorInfo(monitor, ref monitorInfo)
            ? monitorInfo.rcWork
            : new RECT
            {
                Left = (int)SystemParameters.WorkArea.Left,
                Top = (int)SystemParameters.WorkArea.Top,
                Right = (int)SystemParameters.WorkArea.Right,
                Bottom = (int)SystemParameters.WorkArea.Bottom
            };

        info.ptMaxPosition = new POINT { X = workArea.Left, Y = workArea.Top };
        info.ptMaxSize = new POINT { X = workArea.Right - workArea.Left, Y = workArea.Bottom - workArea.Top };

        // Минимальный размер окна: без этого ptMinTrackSize в обрабатываемой
        // структуре остаётся нулём и отключает проверку MinWidth/MinHeight —
        // окно сжимается почти в точку. Значения XAML в DIU переводятся в
        // физические пиксели по текущему DPI.
        var dpi = PresentationSource.FromVisual(_owner)?.CompositionTarget?.TransformToDevice
                  ?? Matrix.Identity;
        info.ptMinTrackSize = new POINT
        {
            X = (int)Math.Ceiling(_owner.MinWidth * dpi.M11),
            Y = (int)Math.Ceiling(_owner.MinHeight * dpi.M22)
        };

        Marshal.StructureToPtr(info, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
