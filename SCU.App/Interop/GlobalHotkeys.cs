using System.Runtime.InteropServices;

namespace SCU.Interop;

// Глобальная горячая клавиша SCU: Ctrl+Alt+S — показать/скрыть окно приложения.
// Регистрируется на HWND главного окна через RegisterHotKey; работает даже когда
// SCU не в фокусе. Включается/выключается из «Настройки → Горячие клавиши».
public static class GlobalHotkeys
{
    private const uint ModControl = 0x2;
    private const uint ModAlt = 0x1;
    private const uint VkS = 0x53;
    private const int WmHotkey = 0x0312;

    public const int HotkeyId = 0x5353; // «SCU»

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static bool Register(IntPtr hwnd) =>
        RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt, VkS);

    public static void Unregister(IntPtr hwnd) => UnregisterHotKey(hwnd, HotkeyId);

    public static bool IsHotkeyMessage(int msg, int wParam) =>
        msg == WmHotkey && wParam == HotkeyId;
}
