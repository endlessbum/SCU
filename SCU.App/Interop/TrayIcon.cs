using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using SCU.Common;

namespace SCU.Interop;

// Действия и состояние окна-владельца, нужные иконке в области уведомлений.
// ShowContextMenu строит меню в MainWindow (WPF ContextMenu в стиле приложения).
internal sealed record TrayIconHost(
    Action ShowWindow,
    Action Exit,
    Action ShowContextMenu,
    Func<bool> IsTranslatorRunning);

// Иконка SCU в области уведомлений (аудит, п. 1). Нужна из-за фонового режима:
// при включённом переводе раскладки закрытие главного окна не завершает
// приложение, и без трея у пользователя не оставалось штатного «Выход» —
// процесс можно было снять только Диспетчером задач.
//
// Реализация нативная (Shell_NotifyIcon), без WinForms: сообщения оболочки
// приходят в HWND главного окна — тот же, что и у глобальной горячей клавиши.
// Меню по правой кнопке строит MainWindow (WPF, стиль приложения) — сюда
// приходит только запрос на его открытие.
internal sealed class TrayIcon : IDisposable
{
    private const int TrayIconId = 1;
    private const int CallbackMessage = 0x8000 + 0x42; // WM_APP + 66

    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;

    private const int SmCxSmIcon = 49;

    // IDI_APPLICATION как MAKEINTRESOURCE(32512).
    private static readonly IntPtr IdApplication = new(32512);

    private readonly HwndSource _source;
    private readonly Logger _logger;
    private readonly TrayIconHost _host;
    private readonly int _taskbarCreatedMessage;

    private IntPtr _icon;
    private bool _iconOwned;
    private bool _added;
    private bool _disposed;

    internal TrayIcon(HwndSource source, Logger logger, TrayIconHost host)
    {
        _source = source;
        _logger = logger;
        _host = host;
        _source.AddHook(OnMessage);

        // Explorer перезапустился — оболочка забыла все иконки: восстанавливаем
        // свою по широковещательному сообщению TaskbarCreated.
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    // Постановка иконки. Сбой (нет доступа к оболочке) не должен ломать запуск
    // приложения — в таком случае остаётся закрытие окна и глобальная горячая клавиша.
    internal void Show()
    {
        if (_disposed || _added)
        {
            return;
        }

        // Иконка могла остаться от предыдущей постановки (перезапуск Explorer):
        // освобождаем, чтобы хэндл не потерялся.
        ReleaseIcon();

        if (!TryLoadIcon(out _icon, out _iconOwned))
        {
            _logger.Warn("TRAY | icon handle unavailable");
            return;
        }

        var data = CreateData(_icon);
        if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            _logger.Warn("TRAY | Shell_NotifyIcon add failed");
            ReleaseIcon();
            return;
        }

        _added = true;
    }

    // Иконка выбрана другим цветом пресета (ThemeManager.IconAccent).
    internal void RefreshIcon()
    {
        if (_disposed || !_added)
        {
            return;
        }

        if (!TryLoadIcon(out var icon, out var owned))
        {
            return;
        }

        var previous = _icon;
        var previousOwned = _iconOwned;
        _icon = icon;
        _iconOwned = owned;

        var data = CreateData(_icon);
        if (!Shell_NotifyIcon(NimModify, ref data))
        {
            _logger.Warn("TRAY | Shell_NotifyIcon modify failed");
            _icon = previous;
            _iconOwned = previousOwned;
            if (owned)
            {
                DestroyIcon(icon);
            }

            return;
        }

        if (previousOwned)
        {
            DestroyIcon(previous);
        }
    }

    // Состояние перевода раскладки изменилось: обновляем подсказку иконки.
    internal void RefreshState()
    {
        if (_disposed || !_added)
        {
            return;
        }

        var data = CreateData(_icon);
        if (!Shell_NotifyIcon(NimModify, ref data))
        {
            // Подсказка — не критично: сама иконка и меню продолжают работать.
            _logger.Warn("TRAY | Shell_NotifyIcon tooltip modify failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(OnMessage);

        if (_added)
        {
            var data = CreateData(_icon);
            if (!Shell_NotifyIcon(NimDelete, ref data))
            {
                _logger.Warn("TRAY | Shell_NotifyIcon delete failed");
            }

            _added = false;
        }

        ReleaseIcon();
    }

    private void ReleaseIcon()
    {
        if (_iconOwned && _icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }

        _icon = IntPtr.Zero;
        _iconOwned = false;
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Иконка числится установленной, но оболочка её потеряла.
            _added = false;
            Show();
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage)
        {
            return IntPtr.Zero;
        }

        handled = true;
        switch (lParam.ToInt32())
        {
            case WmLButtonUp:
            case WmLButtonDblClk:
                SafeInvoke(_host.ShowWindow, "show window");
                break;
            case WmRButtonUp:
            case WmContextMenu:
                SafeInvoke(_host.ShowContextMenu, "show context menu");
                break;
        }

        return IntPtr.Zero;
    }

    // Команда приходит из сообщения оболочки: исключение здесь не должно
    // улетать в WndProc окна и валить UI-поток.
    private void SafeInvoke(Action action, string title)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _logger.Warn($"TRAY | {title} failed | {exception.Message}");
        }
    }

    private NotifyIconData CreateData(IntPtr icon)
    {
        return new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            Window = _source.Handle,
            Id = TrayIconId,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = CallbackMessage,
            Icon = icon,
            Tip = BuildTooltip(),
        };
    }

    // Подсказка показывает, что приложение продолжает работать в фоне именно
    // из-за включённого перевода раскладки.
    private string BuildTooltip() =>
        _host.IsTranslatorRunning() ? "SCU — " + L.T("Перевод раскладки включён.") : "SCU";

    // Иконка — из набора окрашенных по пресету .ico (Assets\Icons, тот же, что у
    // ярлыков). Резервы: иконка самого exe, затем системная — чтобы «Выход» в трее
    // существовал даже в dev-запуске без набора иконок.
    private bool TryLoadIcon(out IntPtr icon, out bool owned)
    {
        var size = GetSystemMetrics(SmCxSmIcon);
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Icons",
            DesktopIconService.GetIconFileName(ThemeManager.IconAccent));
        if (File.Exists(path))
        {
            var fromFile = LoadImage(IntPtr.Zero, path, ImageIcon, size, size, LrLoadFromFile);
            if (fromFile != IntPtr.Zero)
            {
                icon = fromFile;
                owned = true;
                return true;
            }
        }

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(executable)
            && ExtractIconEx(executable, 0, out _, out var small, 1) == 1
            && small != IntPtr.Zero)
        {
            icon = small;
            owned = true;
            return true;
        }

        // Системная иконка принадлежит оболочке: DestroyIcon для неё не вызываем.
        icon = LoadIcon(IntPtr.Zero, IdApplication);
        owned = false;
        return icon != IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public int State;
        public int StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public int TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public int InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);


    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);
}
