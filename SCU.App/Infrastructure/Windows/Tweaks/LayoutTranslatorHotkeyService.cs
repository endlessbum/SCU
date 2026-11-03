using System.Runtime.InteropServices;
using System.Windows.Interop;
using SCU.Common;

namespace SCU.Infrastructure.Windows.Tweaks;

public enum ManualConversionAction
{
    LastWord,
    SelectedText,
}

/// <summary>
/// Глобальные горячие клавиши ручной конвертации. HwndSource создаётся на WPF UI-потоке,
/// поэтому WM_HOTKEY обрабатывается тем же потоком, на котором доступен WPF Clipboard.
/// </summary>
public sealed class LayoutTranslatorHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkPause = 0x13;
    private const uint VkP = 0x50;
    private const int HotkeyLastWordId = 0x4C54;
    private const int HotkeySelectionId = 0x4C55;

    private readonly Logger _logger;
    private HwndSource? _source;
    private bool _registeredLastWord;
    private bool _registeredSelection;
    private bool _disposed;

    public event Action<ManualConversionAction>? HotkeyPressed;

    public LayoutTranslatorHotkeyService(Logger logger) => _logger = logger;

    public bool SetEnabled(bool enabled, string? hotkey)
    {
        if (_disposed) return false;
        if (!EnsureUiThread())
        {
            _logger.Warn("LAYOUT | горячие клавиши нужно менять на UI-потоке");
            return false;
        }

        Unregister();
        if (!enabled) return true;

        try
        {
            if (_source is null)
            {
                var parameters = new HwndSourceParameters("SCU.LayoutTranslator.Hotkeys")
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = 0,
                    UsesPerPixelOpacity = false,
                };
                _source = new HwndSource(parameters);
                _source.AddHook(WndProc);
            }

            var normalized = hotkey == "Ctrl+Alt+P" ? "Ctrl+Alt+P" : "Pause";
            var key = normalized == "Ctrl+Alt+P" ? VkP : VkPause;
            var modifiers = normalized == "Ctrl+Alt+P" ? ModControl | ModAlt : 0u;
            var baseRegistered = RegisterHotKey(_source.Handle, HotkeyLastWordId, modifiers | ModNoRepeat, key);
            var selectionRegistered = baseRegistered && RegisterHotKey(
                _source.Handle, HotkeySelectionId, modifiers | ModShift | ModNoRepeat, key);

            _registeredLastWord = baseRegistered;
            _registeredSelection = selectionRegistered;
            if (!baseRegistered || !selectionRegistered)
            {
                var error = Marshal.GetLastWin32Error();
                Unregister();
                _logger.Warn("LAYOUT | не удалось зарегистрировать горячие клавиши ручной конвертации (rc=" + error + ")");
                return false;
            }

            _logger.Info("LAYOUT | горячие клавиши ручной конвертации зарегистрированы: " + normalized + ", Shift+" + normalized);
            return true;
        }
        catch (Exception ex)
        {
            Unregister();
            _logger.Warn("LAYOUT | ошибка регистрации горячих клавиш: " + ex.Message);
            return false;
        }
    }

    private bool EnsureUiThread() => System.Windows.Application.Current?.Dispatcher.CheckAccess() ?? true;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        var id = unchecked((int)wParam);
        if (id == HotkeyLastWordId)
        {
            handled = true;
            HotkeyPressed?.Invoke(ManualConversionAction.LastWord);
        }
        else if (id == HotkeySelectionId)
        {
            handled = true;
            HotkeyPressed?.Invoke(ManualConversionAction.SelectedText);
        }

        return IntPtr.Zero;
    }

    private void Unregister()
    {
        if (_source is null) return;
        if (_registeredLastWord) UnregisterHotKey(_source.Handle, HotkeyLastWordId);
        if (_registeredSelection) UnregisterHotKey(_source.Handle, HotkeySelectionId);
        _registeredLastWord = false;
        _registeredSelection = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
