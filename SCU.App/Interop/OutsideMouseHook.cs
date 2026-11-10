using System.Runtime.InteropServices;

namespace SCU.Interop;

// Низкоуровневый хук мыши на время жизни меню трея. ContextMenu с
// StaysOpen=false закрывается по клику мимо внутри приложения, но меню
// показывается из прозрачного окна-носителя, пока главное окно скрыто:
// нажатия по рабочему столу и чужим окнам до WPF не доходят, и меню
// оставалось висеть. Хук ловит нажатия вне окна попапа и сообщает о них;
// само нажатие не глотается — клик доходит и до окна под курсором.
//
// Ограничения: пока UI-поток заблокирован надолго (например, идёт «Очистить
// всё»), ОС может выкинуть LL-хук по таймауту LowLevelHooksTimeout — закрытие
// по клику мимо не сработает до следующего открытия меню. Если в menu.Opened
// не достался HWND попапа, хук не ставится — остаётся штатное поведение
// StaysOpen=false (клики внутри приложения меню закрывают).
internal sealed class OutsideMouseHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int GaRoot = 2;

    private const int WmLButtonDown = 0x0201;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmRButtonDown = 0x0204;
    private const int WmNcRButtonDown = 0x00A4;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;

    private readonly IntPtr _popupWindow;
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook;

    // Процедура хука выполняется в UI-потоке (он ставит хук и качает сообщения):
    // тяжёлой работы здесь быть не должно, закрытие меню уходит в диспетчер.
    internal event Action? OutsideMouseDown;

    // Хук установлен: false при отказе SetWindowsHookEx (политика, нехватка
    // ресурсов) — вызывающий код логирует деградацию закрытия по клику мимо.
    internal bool Installed => _hook != IntPtr.Zero;

    internal OutsideMouseHook(IntPtr popupWindow)
    {
        _popupWindow = popupWindow;
        // Делегат обязан жить в поле: нативный хук держит только указатель на
        // thunk, и если ссылку на делегат не сохранить, GC соберёт его, а хук
        // молча перестанет вызываться.
        _proc = OnMouse;
        _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && IsOutsidePress(wParam, lParam))
        {
            OutsideMouseDown?.Invoke();
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool IsOutsidePress(IntPtr wParam, IntPtr lParam)
    {
        var message = wParam.ToInt32();
        if (message is not (WmLButtonDown or WmNcLButtonDown or WmRButtonDown
            or WmNcRButtonDown or WmMButtonDown or WmXButtonDown))
        {
            return false;
        }

        var info = Marshal.PtrToStructure<MouseLlHookStruct>(lParam);
        var window = WindowFromPoint(info.Pt);
        return GetAncestor(window, GaRoot) != _popupWindow;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLlHookStruct
    {
        public Point Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc hook, IntPtr module, uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
