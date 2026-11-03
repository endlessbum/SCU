using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using SCU.Common;

namespace SCU.Infrastructure.Windows.Tweaks;

/// <summary>
/// Ручная конвертация выделенного текста или предыдущего слова через системный clipboard.
/// Содержимое буфера обмена не записывается в журнал и по возможности восстанавливается.
/// </summary>
internal static class ClipboardLayoutConversion
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint LlKHFInjectedTag = 0x53435553;
    private const uint VkControl = 0x11;
    private const uint VkShift = 0x10;
    private const uint VkMenu = 0x12;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;
    private const uint VkLeft = 0x25;
    private const uint VkRight = 0x27;
    private const uint VkC = 0x43;
    private const uint VkV = 0x56;

    internal static async Task<(bool Ok, string Status)> ConvertAsync(ManualConversionAction action)
    {
        if (Application.Current is { } app && !app.Dispatcher.CheckAccess())
        {
            return (false, L.T("Ручная конвертация должна запускаться на UI-потоке."));
        }

        var foregroundWindow = GetForegroundWindow();
        var focusWindow = KeyboardLayoutTranslator.GetFocusedWindow(foregroundWindow);
        if (KeyboardLayoutTranslator.IsPasswordField(focusWindow))
        {
            return (false, L.T("Ручная конвертация отключена в поле пароля."));
        }

        IDataObject? originalClipboard = null;
        var selectionWasCreated = action == ManualConversionAction.LastWord;
        var operationSucceeded = false;

        // Модификаторы, физически зажатые в момент вызова: при горячей клавише
        // Ctrl+Alt+P Alt ещё удерживается, и целевое приложение получало
        // Alt+Ctrl+C / Alt+Ctrl+V вместо Ctrl+C / Ctrl+V (аудит, п. 23).
        // Отпускаем их перед chords и возвращаем обратно в finally.
        var heldModifiers = ReleaseHeldModifiers();
        try
        {
            try { originalClipboard = Clipboard.GetDataObject(); }
            catch { originalClipboard = null; }

            Clipboard.Clear();
            if (selectionWasCreated && !SendChord(VkControl, VkShift, VkLeft))
            {
                return (false, L.T("Не удалось выделить последнее слово."));
            }

            if (!SendChord(VkControl, 0, VkC))
            {
                return (false, L.T("Не удалось скопировать текст для конвертации."));
            }

            // Пауза нужна, чтобы целевое приложение успело обработать Ctrl+C.
            // Thread.Sleep на UI-потоке блокировал интерфейс (аудит, п. 23) —
            // ждём асинхронно, clipboard остаётся доступен с UI-потока.
            await Task.Delay(90).ConfigureAwait(true);

            var source = ReadClipboardText();
            if (string.IsNullOrEmpty(source) && action == ManualConversionAction.SelectedText)
            {
                // Shift+Pause: выделенный фрагмент имеет приоритет; если его нет,
                // резервно пробуем предыдущее слово, чтобы сочетание оставалось полезным.
                Clipboard.Clear();
                if (SendChord(VkControl, VkShift, VkLeft))
                {
                    selectionWasCreated = true;
                    if (SendChord(VkControl, 0, VkC))
                    {
                        await Task.Delay(90).ConfigureAwait(true);
                        source = ReadClipboardText();
                    }
                }
            }

            if (string.IsNullOrEmpty(source))
            {
                return (false, L.T("Текст для конвертации не найден."));
            }

            var converted = ConvertText(source);
            if (converted is null || string.Equals(source, converted, StringComparison.Ordinal))
            {
                return (false, L.T("Текст смешанный, пустой или не требует смены раскладки."));
            }

            Clipboard.SetText(converted, TextDataFormat.UnicodeText);
            await Task.Delay(20).ConfigureAwait(true);
            if (!SendChord(VkControl, 0, VkV))
            {
                return (false, L.T("Не удалось вставить преобразованный текст."));
            }

            // Ctrl+V вставляется в очередь другого процесса асинхронно. Не восстанавливаем
            // старый clipboard до того, как целевое приложение успеет прочитать конвертированный текст.
            await Task.Delay(90).ConfigureAwait(true);

            operationSucceeded = true;
            return (true, L.T("Ручная конвертация выполнена."));
        }
        catch (Exception ex)
        {
            // Не включаем в диагностику исходный/преобразованный пользовательский текст.
            return (false, L.T("Ошибка ручной конвертации: {0}", ex.Message));
        }
        finally
        {
            // Если мы сами выделили слово, но не смогли преобразовать/вставить его,
            // не оставляем выделение в поле ввода.
            if (selectionWasCreated && !operationSucceeded)
            {
                try { SendKey(VkRight); } catch { }
            }

            try
            {
                if (originalClipboard is not null)
                    Clipboard.SetDataObject(originalClipboard, copy: true);
                else
                    Clipboard.Clear();
            }
            catch
            {
                // Внешнее приложение может блокировать clipboard — это не причина аварии SCU.
            }

            // Возвращаем модификаторы в исходное состояние: пользователь физически
            // держит их, и без этого следующая операция пришла бы с «залипшим» Alt.
            RestoreModifiers(heldModifiers);
        }
    }

    // Физически зажатые модификаторы: отпускаем перед chords, чтобы целевое
    // приложение получило чистые Ctrl+C / Ctrl+V (аудит, п. 23).
    private static uint[] ReleaseHeldModifiers()
    {
        var held = new List<uint>();
        foreach (var key in new[] { VkControl, VkShift, VkMenu, VkLWin, VkRWin })
        {
            if (IsKeyDown(key))
            {
                held.Add(key);
            }
        }

        if (held.Count == 0)
        {
            return [];
        }

        var releases = held.Select(key => MakeKey(key, true)).ToArray();
        _ = SendInput((uint)releases.Length, releases, Marshal.SizeOf<INPUT>());
        return held.ToArray();
    }

    private static void RestoreModifiers(uint[] held)
    {
        if (held.Length == 0)
        {
            return;
        }

        var downs = held.Select(key => MakeKey(key, false)).ToArray();
        _ = SendInput((uint)downs.Length, downs, Marshal.SizeOf<INPUT>());
    }

    private static bool IsKeyDown(uint key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static string? ConvertText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var leading = text.TakeWhile(char.IsWhiteSpace).ToArray();
        var trailing = text.Reverse().TakeWhile(char.IsWhiteSpace).Reverse().ToArray();
        var start = leading.Length;
        var end = text.Length - trailing.Length;
        if (end <= start) return null;
        var body = text[start..end];

        var hasLatin = false;
        var hasCyrillic = false;
        foreach (var c in body)
        {
            if (KeyboardLayoutTranslator.IsLatin(c)) hasLatin = true;
            else if (KeyboardLayoutTranslator.IsCyrillic(c)) hasCyrillic = true;
            else if (char.IsLetter(c)) return null;
        }

        if (hasLatin == hasCyrillic) return null;
        var converted = hasLatin
            ? KeyboardLayoutTranslator.TransliterateLatinToCyrillic(body)
            : KeyboardLayoutTranslator.TransliterateCyrillicToLatin(body);
        return new string(leading) + converted + new string(trailing);
    }

    private static string ReadClipboardText()
    {
        try
        {
            return Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? Clipboard.GetText(TextDataFormat.UnicodeText)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool SendKey(uint key)
    {
        var inputs = new[] { MakeKey(key, false), MakeKey(key, true) };
        var sent = (int)SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length) return true;

        // SendInput может отправить только key-down. Не оставляем клавишу зажатой.
        if (sent > 0)
        {
            var release = new[] { MakeKey(key, true) };
            _ = SendInput(1, release, Marshal.SizeOf<INPUT>());
        }
        return false;
    }

    private static bool SendChord(uint modifier1, uint modifier2, uint key)
    {
        var downKeys = new List<uint>();
        if (modifier1 != 0) downKeys.Add(modifier1);
        if (modifier2 != 0) downKeys.Add(modifier2);
        downKeys.Add(key);

        var upKeys = new List<uint> { key };
        if (modifier2 != 0) upKeys.Add(modifier2);
        if (modifier1 != 0) upKeys.Add(modifier1);

        var inputs = new List<INPUT>(downKeys.Count + upKeys.Count);
        foreach (var downKey in downKeys) inputs.Add(MakeKey(downKey, false));
        foreach (var upKey in upKeys) inputs.Add(MakeKey(upKey, true));

        var array = inputs.ToArray();
        var sent = (int)SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
        if (sent == array.Length) return true;

        // При частичной отправке снимаем все модификаторы/клавишу, чьи key-up
        // события не попали в отправленный префикс. Порядок отпускания — обратный.
        var held = new List<uint>();
        for (var downIndex = 0; downIndex < downKeys.Count; downIndex++)
        {
            var keyUpIndex = downKeys.Count + upKeys.IndexOf(downKeys[downIndex]);
            if (downIndex < sent && keyUpIndex >= sent)
                held.Add(downKeys[downIndex]);
        }

        if (held.Count > 0)
        {
            held.Reverse();
            var releases = held.Select(k => MakeKey(k, true)).ToArray();
            _ = SendInput((uint)releases.Length, releases, Marshal.SizeOf<INPUT>());
        }
        return false;
    }

    private static INPUT MakeKey(uint key, bool keyUp) => new()
    {
        type = InputKeyboard,
        u = new InputUnion
        {
            ki = new INPUT_KEYBOARD
            {
                wVk = (ushort)key,
                wScan = 0,
                dwFlags = keyUp ? KeyUp : 0,
                time = 0,
                dwExtraInfo = (UIntPtr)LlKHFInjectedTag,
            },
        },
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public INPUT_KEYBOARD ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_KEYBOARD
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
