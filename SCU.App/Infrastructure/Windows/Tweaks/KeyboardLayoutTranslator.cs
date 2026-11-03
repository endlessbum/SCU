using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using SCU.Common;
using SCU.Infrastructure.Logging;

namespace SCU.Infrastructure.Windows.Tweaks;

// Автоисправление раскладки клавиатуры «в стиле Punto Switcher».
//
// Низкоуровневый хук клавиатуры (WH_KEYBOARD_LL) собирает вводимые символы,
// определяет текущую раскладку активного окна и, когда слово набрано в
// неверной раскладке (русский текст английскими буквами и наоборот), подменяет
// его на правильное по разделителю (пробел, Enter, знак препинания).
//
// Хук устанавливается на UI-потоке WPF, у которого уже есть насос сообщений. Коррекция не
// применяется в окнах с повышенными правами (UIPI блокирует SendInput туда) —
// это ожидаемое поведение, пароли и админ-ввод не перехватываются надежно.
//
// Безопасность:
//   * Введённые и исправленные слова никогда не записываются в журнал.
//   * Распознанные поля пароля не буферизуются и не редактируются.
//   * Перед первым включением показывается диалог подтверждения (см. InputViewModel).
public sealed class KeyboardLayoutTranslator : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const uint VkBack = 0x08;
    private const uint VkTab = 0x09;
    private const uint VkReturn = 0x0D;
    private const uint VkSpace = 0x20;
    private const uint VkEscape = 0x1B;
    private const uint VkPause = 0x13;
    private const uint VkLeft = 0x25;
    private const uint VkUp = 0x26;
    private const uint VkRight = 0x27;
    private const uint VkDown = 0x28;
    private const uint VkHome = 0x24;
    private const uint VkEnd = 0x23;
    private const uint VkDelete = 0x2E;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;
    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFUnicode = 0x0004;
    private const uint LlKHfInjected = 0x10;
    private const int GwlStyle = -16;
    private const uint EsPassword = 0x0020;

    // Уникальная метка для наших собственных инжект-событий (dwExtraInfo),
    // чтобы не обрабатывать подставленную замену повторно.
    private const UIntPtr HookExtraInfo = (UIntPtr)0x53435553; // "SCUS"

    // Первичные идентификаторы языков раскладки.
    private const ushort LangEnglish = 0x09;
    private const ushort LangRussian = 0x19;

    // Минимальная длина слова для рассмотрения коррекции. Окончательное решение
    // принимается языковой проверкой, а не только длиной.
    private const int MinWordLength = 2;
    private const int MaxBufferLength = 64;
    private const double PauseResetSeconds = 2.0;

    // Жизненный цикл хука.
    public enum HookState { Off, Starting, On, Stopping, Error }

    // Язык, распознанный по локальному словарю.
    public enum WordLanguage { None, English, Russian }

    private readonly Logger _logger;
    private readonly object _gate = new();

    // Диагностический счётчик входов в HookCallback (без пользовательского текста).
    private long _callbackCount;
    public long CallbackCount => System.Threading.Interlocked.Read(ref _callbackCount);



    private IntPtr _hHook;
    private IntPtr _hMouseHook;
    private LowLevelKeyboardProc? _hookProc;
    private LowLevelMouseProc? _mouseHookProc;
    private bool _disposed;
    private bool _skipSpecialToken;
    private PendingPeriodCorrection? _pendingPeriod;
    private bool _showCorrectionToast = true;
    private readonly HashSet<string> _customExceptions = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastToastUtc = DateTime.MinValue;
    private LastCorrection? _lastCorrection;

    private sealed record LastCorrection(
        string Original,
        string Corrected,
        char Separator,
        IntPtr ForegroundWindow,
        IntPtr FocusWindow,
        DateTime Timestamp);

    // Точка неоднозначна: это может быть конец предложения или середина домена/
    // имени файла. Решение откладывается до следующей клавиши.
    private sealed record PendingPeriodCorrection(
        string Original,
        IntPtr Layout,
        int LayoutId,
        IntPtr ForegroundWindow,
        IntPtr FocusWindow);

    private HookState _hookState = HookState.Off;

    public bool IsRunning => _hookState == HookState.On;
    public HookState State => _hookState;

    // ----- буфер текущего слова -----
    private readonly StringBuilder _buffer = new();
    private DateTime _lastKeyTime = DateTime.MinValue;
    private IntPtr _wordWindow;          // HWND сфокусированного контрола на начало слова
    private int _wordLayoutId;           // ID раскладки на начало слова

    public KeyboardLayoutTranslator(Logger logger) => _logger = logger;

    // ----- расширенные фильтры ложных срабатываний (P0) -----
    // URL/email/путь/аббревиатура/ALL_CAPS не должны корректироваться — это нетекстовые токены.
    private static readonly Regex UrlPrefixRegex = new(@"^(https?://|ftp://|www\.)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex PathLikeRegex = new(@"^([a-zA-Z]:\\|\\\\|/|[a-zA-Z]:/)", RegexOptions.Compiled);
    private static readonly Regex AbbreviationRegex = new(@"^[A-Z]{2,4}$", RegexOptions.Compiled);
    private static readonly Regex CodeIdentifierRegex = new(
        @"^(?:[a-z]+[A-Z][A-Za-z0-9]*|[A-Z][a-z]+(?:[A-Z][A-Za-z0-9]*)+|[A-Z]{3,}[A-Z][a-z]+(?:[A-Z][A-Za-z0-9]*)*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DomainRegex = new(
        @"^[a-z0-9-]+(?:\.[a-z0-9-]+)+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool IsExcludableToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var token = text.Trim();
        if (token.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (UrlPrefixRegex.IsMatch(token) || EmailRegex.IsMatch(token) || PathLikeRegex.IsMatch(token))
            return true;
        if (token.Contains("@", StringComparison.Ordinal) || token.Contains("://", StringComparison.Ordinal))
            return true;
        if (token.Contains('\\') || token.Contains('/')) return true;

        // Похожий на домен токен, например example.com или localhost.localdomain.
        if (DomainRegex.IsMatch(token))
            return true;

        if (AbbreviationRegex.IsMatch(token) || IsAllCapsAbbreviationLike(token)) return true;
        if (CodeIdentifierRegex.IsMatch(token)) return true; // camelCase/PascalCase identifiers
        return false;
    }

    private static bool IsAllCapsAbbreviationLike(string text)
    {
        // 2-4 заглавных латиницы подряд без строчных — аббревиатура, не слово с неверной раскладкой.
        if (text.Length < 2 || text.Length > 4) return false;
        foreach (var c in text)
            if (c < 'A' || c > 'Z') return false;
        return true;
    }

    // WH_KEYBOARD_LL устанавливается на WPF UI-потоке: именно этот поток
    // владеет действующим насосом сообщений. Нельзя оборачивать Start() в Task.Run.
    public Result Start()
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                return Result.Success("Перевод раскладки уже запущен.");
            }

            if (_disposed)
            {
                return Result.Failure("Перевод раскладки удалён, перезапустите приложение.");
            }

            if (Application.Current is { } app && !app.Dispatcher.CheckAccess())
            {
                return Result.Failure("Хук клавиатуры должен устанавливаться на UI-потоке SCU.");
            }

            _hookState = HookState.Starting;
            ClearBuffer();
            _skipSpecialToken = false;
            _lastCorrection = null;

            try
            {
                // Загружаем словари до установки хука, чтобы первое нажатие не
                // запускало файловый ввод-вывод внутри системного callback.
                _ = EnglishWords.Count;
                _ = RussianWords.Count;
                _ = EnglishBigramModel.Value;
                _ = RussianBigramModel.Value;
                _hookProc = HookCallback;
                var module = GetModuleHandle(null);
                _hHook = SetWindowsHookEx(WhKeyboardLl, _hookProc, module, 0);
                if (_hHook == IntPtr.Zero)
                {
                    var code = Marshal.GetLastWin32Error();
                    _hookProc = null;
                    _hookState = HookState.Error;
                    _logger.Error("LAYOUT | SetWindowsHookEx клавиатуры вернул NULL (rc=" + code + ")");
                    return Result.Failure("Не удалось установить хук клавиатуры.", code);
                }

                // Глобальный mouse-hook сбрасывает накопленное слово при клике в
                // другое место того же контрола: иначе буфер мог удалить не тот текст.
                _mouseHookProc = MouseHookCallback;
                _hMouseHook = SetWindowsHookEx(WhMouseLl, _mouseHookProc, module, 0);
                if (_hMouseHook == IntPtr.Zero)
                {
                    var code = Marshal.GetLastWin32Error();
                    var keyboardUnhooked = UnhookIfNeeded();
                    if (keyboardUnhooked) _hookProc = null;
                    _mouseHookProc = null;
                    _hookState = HookState.Error;
                    _logger.Error("LAYOUT | SetWindowsHookEx мыши вернул NULL (rc=" + code + ")");
                    return Result.Failure("Не удалось включить защиту от перемещения каретки мышью.", code);
                }

                _hookState = HookState.On;
                _logger.Info("LAYOUT | перехват раскладки запущен на UI-потоке");
                return Result.Success("Перевод раскладки включён.");
            }
            catch (Exception ex)
            {
                var keyboardUnhooked = UnhookIfNeeded();
                var mouseUnhooked = UnhookMouseIfNeeded();
                if (keyboardUnhooked) _hookProc = null;
                if (mouseUnhooked) _mouseHookProc = null;
                _hookState = HookState.Error;
                _logger.Error("LAYOUT | ошибка установки защитных хуков: " + ex.GetType().Name);
                return Result.Failure("Не удалось установить защитные хуки клавиатуры/мыши.");
            }
        }
    }

    public Result Stop()
    {
        lock (_gate)
        {
            if (_hHook == IntPtr.Zero && _hMouseHook == IntPtr.Zero)
            {
                _hookState = HookState.Off;
                return Result.Success("Перевод раскладки не запущен.");
            }

            _hookState = HookState.Stopping;
            var keyboardUnhooked = UnhookIfNeeded();
            var mouseUnhooked = UnhookMouseIfNeeded();
            ClearBuffer();
            _skipSpecialToken = false;
            _lastCorrection = null;

            if (!keyboardUnhooked || !mouseUnhooked)
            {
                // Сохраняем delegate и handle при ошибке снятия: система может
                // продолжить вызывать callback, и delegate не должен быть собран GC.
                _hookState = HookState.Error;
                return Result.Failure("Не удалось снять защитные хуки клавиатуры/мыши.", Marshal.GetLastWin32Error());
            }

            _hookProc = null;
            _mouseHookProc = null;
            _hookState = HookState.Off;
            _logger.Info("LAYOUT | перехват клавиатуры и защита каретки остановлены");
            return Result.Success("Перевод раскладки выключен.");
        }
    }

    // Снятие выполняется до освобождения delegate, иначе системный callback
    // мог бы обратиться к уже собранному делегату.
    private bool UnhookIfNeeded()
    {
        var h = _hHook;
        if (h == IntPtr.Zero) return true;

        try
        {
            if (UnhookWindowsHookEx(h))
            {
                Interlocked.CompareExchange(ref _hHook, IntPtr.Zero, h);
                return true;
            }

            int rc = Marshal.GetLastWin32Error();
            if (rc == 1404) // ERROR_INVALID_HOOK_HANDLE: уже снят системой.
            {
                Interlocked.CompareExchange(ref _hHook, IntPtr.Zero, h);
                return true;
            }

            _logger.Warn("LAYOUT | UnhookWindowsHookEx вернул FALSE (rc=" + rc + ")");
            return false;
        }
        catch (Exception exception)
        {
            _logger.Warn("LAYOUT | UnhookWindowsHookEx исключение: " + exception.Message);
            return false;
        }
    }

    private bool UnhookMouseIfNeeded()
    {
        var h = _hMouseHook;
        if (h == IntPtr.Zero) return true;

        try
        {
            if (UnhookWindowsHookEx(h))
            {
                Interlocked.CompareExchange(ref _hMouseHook, IntPtr.Zero, h);
                return true;
            }

            int rc = Marshal.GetLastWin32Error();
            if (rc == 1404) // ERROR_INVALID_HOOK_HANDLE: уже снят системой.
            {
                Interlocked.CompareExchange(ref _hMouseHook, IntPtr.Zero, h);
                return true;
            }

            _logger.Warn("LAYOUT | UnhookWindowsHookEx мыши вернул FALSE (rc=" + rc + ")");
            return false;
        }
        catch (Exception exception)
        {
            _logger.Warn("LAYOUT | ошибка снятия mouse-hook: " + exception.GetType().Name);
            return false;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WmLButtonDown || wParam == (IntPtr)WmRButtonDown
            || wParam == (IntPtr)WmMButtonDown || wParam == (IntPtr)WmXButtonDown))
        {
            // Клик мог перенести каретку в том же HWND. Не пытаемся восстановить
            // её позицию по геометрии символов: сброс безопаснее возможной порчи текста.
            ClearBuffer();
            _skipSpecialToken = false;
            _lastCorrection = null;
        }

        return CallNextHookEx(_hMouseHook, nCode, wParam, lParam);
    }

    public void Configure(IEnumerable<string>? customExceptions, bool showToast = true)
    {
        lock (_gate)
        {
            _customExceptions.Clear();
            if (customExceptions is not null)
            {
                foreach (var value in customExceptions)
                {
                    var normalized = NormalizeExceptionToken(value);
                    if (normalized.Length > 0 && normalized.Length <= MaxBufferLength)
                    {
                        _customExceptions.Add(normalized);
                    }
                    if (_customExceptions.Count >= 200) break;
                }
            }
            _showCorrectionToast = showToast;
        }
    }

    internal static string NormalizeExceptionToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Trim().Trim('.', ',', ';', ':', '!', '?', '"', '\'').ToLowerInvariant();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // После неудачного UnhookWindowsHookEx handle/delegate остаются удержанными,
        // но выключенный/ошибочный переводчик больше не вмешивается в ввод.
        if (_hookState != HookState.On)
        {
            return CallNextHookEx(_hHook, nCode, wParam, lParam);
        }

        if (nCode >= 0 && (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown))
        {
            var ks = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);



            // Счётчик входов в callback (без логирования и ввода пользователя).
            System.Threading.Interlocked.Increment(ref _callbackCount);

            // Игнорируем свои собственные инжект-события (SendInput), чтобы
            // не обрабатывать подставленную замену повторно.
            if ((ks.flags & LlKHfInjected) != 0 || ks.dwExtraInfo == HookExtraInfo)
            {
                return CallNextHookEx(_hHook, nCode, wParam, lParam);
            }

            bool suppress = false;
            try
            {
                suppress = ProcessKey(lParam);
            }
            catch (Exception exception)
            {
                _logger.Warn("LAYOUT | ошибка обработки нажатия: " + exception.Message);
            }

            if (suppress)
            {
                // Клавиша-разделитель, запустившая коррекцию, подавляется: сама
                // коррекция уже ввела слово и разделитель через SendInput.
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hHook, nCode, wParam, lParam);
    }

    // URL, email и пути нужно пропустить целиком, а не исправлять их фрагменты.
    // Проверяем маркеры в момент ввода, до того как пунктуация станет границей слова.
    private static bool IsSpecialTokenStart(string currentToken, char nextCharacter)
    {
        if (nextCharacter is '@' or '/' or '\\')
        {
            return true;
        }

        if (nextCharacter == ':')
        {
            return currentToken.Equals("http", StringComparison.OrdinalIgnoreCase)
                || currentToken.Equals("https", StringComparison.OrdinalIgnoreCase)
                || currentToken.Equals("ftp", StringComparison.OrdinalIgnoreCase)
                || (currentToken.Length == 1 && IsLatin(currentToken[0]));
        }

        if (nextCharacter == '.' && currentToken.Equals("www", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    // ----- обработка нажатия -----

    private bool ProcessKey(IntPtr lParam)
    {
        var ks = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        var now = DateTime.Now;
        // Сброс буфера при длинной паузе между словами.
        if (_lastKeyTime != DateTime.MinValue && (now - _lastKeyTime).TotalSeconds > PauseResetSeconds)
        {
            ClearBuffer();
        }
        _lastKeyTime = now;

        var foregroundWindow = GetForegroundWindow();

        // Сброс буфера при смене сфокусированного окна/контрола в середине слова.
        var focusWindow = GetFocusedWindow(foregroundWindow);
        if (_wordWindow != IntPtr.Zero && focusWindow != IntPtr.Zero && _wordWindow != focusWindow)
        {
            ClearBuffer();
        }
        if (focusWindow != IntPtr.Zero)
        {
            _wordWindow = focusWindow;
        }

        // Исключение полей пароля: не корректируем и не буферизуем ввод в них.
        if (IsPasswordField(focusWindow))
        {
            ClearBuffer();
            return false;
        }

        var layout = GetKeyboardLayout(GetWindowThreadProcessId(foregroundWindow, out _));
        var layoutId = GetLayoutId(layout);

        // Сброс буфера при смене раскладки в середине слова.
        if (_buffer.Length > 0 && _wordLayoutId != 0 && _wordLayoutId != layoutId)
        {
            ClearBuffer();
        }
        _wordLayoutId = layoutId;

        // Состояние модификаторов (Ctrl/Alt/Win) — горячие клавиши сбрасывают буфер.
        var keyState = new byte[256];
        GetKeyboardState(keyState);
        bool ctrlLeftDown = IsKeyDown(keyState, 0xA2);
        bool ctrlRightDown = IsKeyDown(keyState, 0xA3);
        bool ctrlGenericDown = IsKeyDown(keyState, VkControl) && !ctrlLeftDown && !ctrlRightDown;
        bool ctrlDown = IsKeyDown(keyState, VkControl) || ctrlLeftDown || ctrlRightDown;
        bool altDown = IsKeyDown(keyState, VkMenu) || IsKeyDown(keyState, 0xA4) || IsKeyDown(keyState, 0xA5);
        bool winDown = IsKeyDown(keyState, VkLWin) || IsKeyDown(keyState, VkRWin);
        bool shiftDown = IsKeyDown(keyState, 0xA0) || IsKeyDown(keyState, 0xA1);

        // Точку сначала пропускаем в целевое приложение. Если следующая клавиша
        // продолжает токен (домен/расширение), не исправляем его префикс; если
        // следует обычный разделитель, исправляем слово вместе с уже введённой точкой.
        var pendingPeriodCorrectionApplied = false;
        if (_pendingPeriod is { } pendingPeriod && !IsShiftKey(ks.vkCode))
        {
            _pendingPeriod = null;
            if (ctrlDown || altDown || winDown || IsNavigationOrEditKey(ks.vkCode)
                || IsModifierKey(ks.vkCode))
            {
                // Управляющее сочетание не продолжает домен и не должно оставлять
                // _skipSpecialToken включённым для следующих обычных символов.
                ClearBuffer();
            }
            else if (IsPotentialTokenContinuationKey(ks.vkCode))
            {
                _skipSpecialToken = true;
                ClearBuffer();
            }
            else if (pendingPeriod.LayoutId == layoutId
                && (pendingPeriod.FocusWindow == IntPtr.Zero || pendingPeriod.FocusWindow == focusWindow)
                && (pendingPeriod.ForegroundWindow == IntPtr.Zero || pendingPeriod.ForegroundWindow == foregroundWindow))
            {
                pendingPeriodCorrectionApplied = TryCorrectPendingPeriod(pendingPeriod);
            }
        }

        // Ctrl+Z отменяет только последнюю коррекцию, если фокус не ушёл из
        // исходного контрола и пользователь ещё не ввёл следующий символ.
        if (ctrlDown && !shiftDown && !altDown && !winDown && ks.vkCode == 0x5A
            && TryUndoLastCorrection(foregroundWindow, focusWindow, ctrlLeftDown, ctrlRightDown, ctrlGenericDown))
        {
            ClearBuffer();
            return true;
        }
        if (_lastCorrection is not null && !pendingPeriodCorrectionApplied
            && !ctrlDown && !altDown && !winDown && !IsModifierKey(ks.vkCode))
        {
            _lastCorrection = null;
        }

        // В URL/email/путях не корректируем отдельные фрагменты между знаками.
        if (_skipSpecialToken)
        {
            if (ks.vkCode == VkSpace || ks.vkCode == VkReturn || ks.vkCode == VkTab)
            {
                _skipSpecialToken = false;
            }
            ClearBuffer();
            return false;
        }

        // Управляющие клавиши и разделители — ПЕРЕД ToUnicodeEx.
        if (ks.vkCode == VkBack)
        {
            if (ctrlDown || altDown || winDown)
            {
                ClearBuffer();
            }
            else if (_buffer.Length > 0)
            {
                _buffer.Length--;
            }
            return false;
        }

        // Навигация и прочие границы слова — сбрасываем буфер, не корректируем.
        // Tab теперь — разделитель с попыткой коррекции (P0: ранее сбрасывал без TryCorrect).
        // Остальные навигационные клавиши — сброс без коррекции.
        if (ks.vkCode == VkTab)
        {
            // Tab как разделитель: пробуем скорректировать текущее слово перед очисткой.
            bool tabCorrected = false;
            if (!ctrlDown && !altDown && !winDown && !shiftDown && _buffer.Length >= MinWordLength)
            {
                // Получаем char для Tab как разделителя ('\t'); layout уже получен выше.
                // Модификаторные сочетания вроде Alt+Tab нельзя подавлять автозаменой.
                tabCorrected = TryCorrect(_buffer, layout, '\t');
            }
            ClearBuffer();
            return tabCorrected;
        }
        if (ks.vkCode == VkPause || ks.vkCode == VkEscape || ks.vkCode == VkLeft
            || ks.vkCode == VkRight || ks.vkCode == VkUp || ks.vkCode == VkDown
            || ks.vkCode == VkHome || ks.vkCode == VkEnd || ks.vkCode == VkDelete)
        {
            ClearBuffer();
            return false;
        }

        // Горячие клавиши (Ctrl/Alt/Win + anything) — не буферизуем, сбрасываем.
        if (ctrlDown || altDown || winDown)
        {
            ClearBuffer();
            return false;
        }

        if (IsModifierKey(ks.vkCode))
        {
            return false;
        }

        // Shift+Enter во многих редакторах означает перенос строки, а не обычный Enter.
        // Не подменяем его немодифицированным Enter ради коррекции.
        if (ks.vkCode == VkReturn && shiftDown)
        {
            ClearBuffer();
            return false;
        }

        // Space и обычный Enter обрабатываем до ToUnicodeEx: некоторые раскладки возвращают
        // для управляющих клавиш ноль символов, из-за чего коррекция иначе пропускается.
        if (ks.vkCode == VkSpace || ks.vkCode == VkReturn)
        {
            var delimiter = ks.vkCode == VkReturn ? '\r' : ' ';
            bool corrected = TryCorrect(_buffer, layout, delimiter);
            ClearBuffer();
            return corrected;
        }

        // ToUnicodeEx — только для клавиш, которым нужен символьный перевод.
        var chars = new StringBuilder(8);
        var result = ToUnicodeEx(
            ks.vkCode,
            ks.scanCode,
            keyState,
            chars,
            chars.Capacity,
            0,
            layout);

        if (result <= 0)
        {
            return false;
        }

        var ch = chars[0];

        if (IsSpecialTokenStart(_buffer.ToString(), ch))
        {
            _skipSpecialToken = true;
            ClearBuffer();
            return false;
        }

        if (ch == '.' && _buffer.Length >= MinWordLength)
        {
            var original = _buffer.ToString();
            var pending = new PendingPeriodCorrection(
                original, layout, layoutId, foregroundWindow, focusWindow);
            ClearBuffer();
            _pendingPeriod = pending;
            // Точка уже уйдёт в целевое приложение. Коррекция будет принята
            // только после того, как следующая клавиша прояснит границу токена.
            return false;
        }

        if (char.IsLetter(ch))
        {
            if (_buffer.Length < MaxBufferLength)
            {
                _buffer.Append(ch);
            }
            return false;
        }

        // Разделитель (пробел, Enter, знаки препинания, табуляция и т.п.)
        // запускает одну попытку коррекции, сам разделитель не дублируется —
        // оригинальная клавиша разделителя проходит через CallNextHookEx.
        if (IsSeparator(ch) || ks.vkCode == VkReturn || ks.vkCode == VkSpace)
        {
            bool corrected = TryCorrect(_buffer, layout, ch);
            ClearBuffer();
            return corrected;
        }

        // Неподдерживаемый символ внутри слова — сбрасываем буфер, чтобы не
        // получилась смешанная кириллица/латиница.
        ClearBuffer();
        return false;
    }

    private static bool IsKeyDown(byte[] keyState, uint vk) => (keyState[(int)vk] & 0x80) != 0;

    private static bool IsSeparator(char ch) =>
        ch is ' ' or '.' or ',' or '!' or '?' or ':' or ';' or '-' or '_' or '(' or ')'
            or '[' or ']' or '{' or '}' or '/' or '\\' or '|' or '"' or '\'' or '@';

    private static bool IsModifierKey(uint vk) =>
        vk == 0x10 || vk == 0xA0 || vk == 0xA1 || // VK_SHIFT, VK_LSHIFT, VK_RSHIFT
        vk == 0xA2 || vk == 0xA3 || // VK_LCONTROL, VK_RCONTROL
        vk == 0xA4 || vk == 0xA5 || // VK_LMENU, VK_RMENU
        vk == 0x5B || vk == 0x5C;   // VK_LWIN, VK_RWIN

    // ----- коррекция слова -----

    private bool TryCorrect(StringBuilder buffer, IntPtr layout, char separator)
    {
        if (buffer.Length < MinWordLength)
            return false;

        var text = buffer.ToString();
        // Маркеры URL/email/путей проверяем на границе до коррекции префикса.
        if (separator == '@' || separator == '/' || separator == '\\'
            || (separator == ':' && (text.Equals("http", StringComparison.OrdinalIgnoreCase)
                || text.Equals("https", StringComparison.OrdinalIgnoreCase)
                || text.Equals("ftp", StringComparison.OrdinalIgnoreCase)
                || (text.Length == 1 && IsLatin(text[0]))))
            || (separator == '.' && text.Equals("www", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!TryGetCorrectionCandidate(text, layout, out var candidate))
            return false;

        return ReplaceWord(text, candidate, separator, GetForegroundWindow(), _wordWindow);
    }

    private bool TryCorrectPendingPeriod(PendingPeriodCorrection pending)
    {
        if (!TryGetCorrectionCandidate(pending.Original, pending.Layout, out var candidate))
            return false;

        // Точка уже находится в документе, поэтому удаляем и восстанавливаем её
        // вместе со словом; последующий разделитель всё ещё пройдёт оригинальным.
        // Между точкой и принятым решением уже прошёл ещё один пользовательский
        // ввод. Старый undo-протокол хранит только один конечный разделитель, поэтому
        // для этого сценария не оставляем потенциально опасный Ctrl+Z-маркер.
        return ReplaceWord(pending.Original, candidate, '.', pending.ForegroundWindow,
            pending.FocusWindow, separatorAlreadyPresent: true, allowUndo: false);
    }

    private bool TryGetCorrectionCandidate(string text, IntPtr layout, out string candidate)
    {
        candidate = string.Empty;
        if (IsExcludableToken(text) || IsCustomException(text) || !IsCorrectableWord(text))
            return false;

        var isRussianLayout = IsRussianLayout(layout);
        var isEnglishLayout = IsEnglishLayout(layout);
        if (!isRussianLayout && !isEnglishLayout)
            return false;

        var layoutLanguage = isRussianLayout ? WordLanguage.Russian : WordLanguage.English;
        string? converted = null;

        if (isRussianLayout && IsAllCyrillic(text))
            converted = TransliterateCyrillicToLatin(text);
        else if (isEnglishLayout && IsAllLatin(text))
            converted = TransliterateLatinToCyrillic(text);

        if (string.IsNullOrEmpty(converted) || string.Equals(converted, text, StringComparison.Ordinal))
            return false;
        if (!IsCorrectionDesirable(text, converted, layoutLanguage))
            return false;

        candidate = converted;
        return true;
    }

    private static bool IsShiftKey(uint vk) => vk is 0x10 or 0xA0 or 0xA1;

    internal static bool IsPotentialTokenContinuationKey(uint vk) =>
        (vk >= 0x30 && vk <= 0x39)       // верхний ряд цифр
        || (vk >= 0x41 && vk <= 0x5A)    // A-Z
        || (vk >= 0x60 && vk <= 0x69)    // NumPad
        || vk is 0xBD or 0xBE or 0xBB;   // -, ., + (имена файлов/домены)

    private static bool IsNavigationOrEditKey(uint vk) => vk is VkBack or VkTab or VkEscape
        or VkLeft or VkRight or VkUp or VkDown or VkHome or VkEnd or VkDelete or VkPause;

    /// <summary>
    /// Только одноязычные буквенные слова без нетранслитерируемых букв (ё/Ё).
    /// </summary>
    internal static bool IsCorrectableWord(string text)
    {
        if (text is null || text.Length < MinWordLength)
        {
            return false;
        }

        var latin = 0;
        var cyrillic = 0;

        foreach (var c in text)
        {
            if (IsLatin(c))
            {
                latin++;
            }
            else if (IsCyrillic(c))
            {
                // ё/Ё нельзя надёжно отобразить в одну латинскую букву —
                // отказываемся от автоматической замены таких слов.
                if (c == '\u0451' || c == '\u0401')
                {
                    return false;
                }
                cyrillic++;
            }
            else
            {
                // Любой небуквенный символ (цифра, пунктуация, символ) — не корректируем.
                return false;
            }
        }

        // Только одноязычные слова: слово не может быть одновременно на двух раскладках.
        return (latin > 0 && cyrillic == 0) || (cyrillic > 0 && latin == 0);
    }

    internal static bool IsAllLatin(string text)
    {
        foreach (var c in text)
        {
            if (!IsLatin(c))
            {
                return false;
            }
        }
        return text.Length > 0;
    }

    internal static bool IsAllCyrillic(string text)
    {
        foreach (var c in text)
        {
            if (!IsCyrillic(c))
            {
                return false;
            }
        }
        return text.Length > 0;
    }

    /// <summary>
    /// Сначала применяется строгий словарь; для неизвестных слов допускается
    /// только явное преимущество символьной биграммной модели, обученной на словарях.
    /// </summary>
    internal static bool IsCorrectionDesirable(string original, string candidate, WordLanguage layoutLanguage)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(candidate)
            || layoutLanguage is not (WordLanguage.English or WordLanguage.Russian))
            return false;

        var targetLanguage = layoutLanguage == WordLanguage.Russian
            ? WordLanguage.English
            : WordLanguage.Russian;

        if (IsKnownWord(original, layoutLanguage)) return false;
        if (IsKnownWord(candidate, targetLanguage)) return true;

        // Биграммы не заменяют словарь безусловно: нужны минимум 4 символа,
        // достаточная целевая вероятность и заметный отрыв от исходной строки.
        if (original.Length < 4 || candidate.Length < 4) return false;
        if (targetLanguage == WordLanguage.Russian && !IsAllCyrillic(candidate)) return false;
        if (targetLanguage == WordLanguage.English && !IsAllLatin(candidate)) return false;

        var sourceScore = ScoreBigramWord(original, layoutLanguage);
        var targetScore = ScoreBigramWord(candidate, targetLanguage);
        return targetScore >= -3.6 && targetScore - sourceScore >= 0.75;
    }

    private sealed class BigramLanguageModel
    {
        private readonly Dictionary<(char Previous, char Next), int> _counts = [];
        private readonly Dictionary<char, int> _totals = [];
        private readonly int _alphabetSize;

        public BigramLanguageModel(IEnumerable<string> words, bool russian)
        {
            _alphabetSize = russian ? 33 : 26;
            foreach (var word in words)
            {
                if (word.Length == 0) continue;
                var lower = word.ToLowerInvariant();
                if (russian ? !IsAllCyrillic(lower) : !IsAllLatin(lower)) continue;
                var padded = "^" + lower + "$";
                for (var i = 1; i < padded.Length; i++)
                {
                    var pair = (padded[i - 1], padded[i]);
                    _counts[pair] = _counts.GetValueOrDefault(pair) + 1;
                    _totals[pair.Item1] = _totals.GetValueOrDefault(pair.Item1) + 1;
                }
            }
        }

        public double Score(string word)
        {
            var padded = "^" + word.ToLowerInvariant() + "$";
            var score = 0.0;
            var count = 0;
            const double smoothing = 0.15;
            for (var i = 1; i < padded.Length; i++)
            {
                var previous = padded[i - 1];
                var next = padded[i];
                var total = _totals.GetValueOrDefault(previous);
                var pairCount = _counts.GetValueOrDefault((previous, next));
                score += Math.Log((pairCount + smoothing) / (total + smoothing * _alphabetSize));
                count++;
            }
            return count == 0 ? double.NegativeInfinity : score / count;
        }
    }

    private static readonly Lazy<BigramLanguageModel> EnglishBigramModel = new(
        () => new BigramLanguageModel(EnglishWords, russian: false), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<BigramLanguageModel> RussianBigramModel = new(
        () => new BigramLanguageModel(RussianWords, russian: true), LazyThreadSafetyMode.ExecutionAndPublication);

    internal static double ScoreBigramWord(string word, WordLanguage language) => language switch
    {
        WordLanguage.English => EnglishBigramModel.Value.Score(word),
        WordLanguage.Russian => RussianBigramModel.Value.Score(word),
        _ => double.NegativeInfinity,
    };

    internal static WordLanguage DetectWordLanguage(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return WordLanguage.None;
        }

        var latin = 0;
        var cyrillic = 0;
        foreach (var c in text)
        {
            if (IsLatin(c)) latin++;
            else if (IsCyrillic(c)) cyrillic++;
        }

        if (latin > 0 && cyrillic == 0 && IsKnownWord(text, WordLanguage.English))
        {
            return WordLanguage.English;
        }
        if (cyrillic > 0 && latin == 0 && IsKnownWord(text, WordLanguage.Russian))
        {
            return WordLanguage.Russian;
        }

        return WordLanguage.None;
    }

    internal static bool IsKnownWord(string text, WordLanguage language)
    {
        if (language == WordLanguage.English)
        {
            return EnglishWords.Contains(text.ToLowerInvariant());
        }
        if (language == WordLanguage.Russian)
        {
            return RussianWords.Contains(text.ToLowerInvariant());
        }
        return false;
    }

    internal static int GetDictionaryWordCount(WordLanguage language) => language switch
    {
        WordLanguage.English => EnglishWords.Count,
        WordLanguage.Russian => RussianWords.Count,
        _ => 0,
    };

    // ----- офлайн-словари для языковой правдоподобия (только решения о коррекции) -----
    // Ленивая загрузка внешних словарей Assets/Dictionaries/{en,ru}.txt (P0).
    // Формат: по одному слову на строку (UTF-8, пустые/комментарии # пропускаются).
    // При отсутствии файла или ошибке — fallback на минимальный встроенный набор.
    private static readonly HashSet<string> FallbackEnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","be","to","of","and","a","in","that","have","i","it","for","not","on","with","he","as","you","do","at","this","but","his","by","from","they","we","say","her","she","or","an","will","my","hello","world","test"
    };
    private static readonly HashSet<string> FallbackRussianWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "привет","мир","тест","здравствуйте","спасибо","пожалуйста","да","нет","хорошо","плохо","дом","работа","время","человек","друг","жизнь","любовь","город","страна","школа","книга","вода","еда","день","ночь","утро","вечер","большой","маленький","новый","старый","красный","синий","белый","черный"
    };
    private static readonly Lazy<HashSet<string>> LazyEnglishWords = new(LoadEnglishWords, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<HashSet<string>> LazyRussianWords = new(LoadRussianWords, LazyThreadSafetyMode.ExecutionAndPublication);
    private static HashSet<string> EnglishWords => LazyEnglishWords.Value;
    private static HashSet<string> RussianWords => LazyRussianWords.Value;

    private static HashSet<string> LoadEnglishWords()
    {
        var loaded = TryLoadDictionaryFile("en.txt");
        if (loaded is not null && loaded.Count >= 50) return loaded;
        return new HashSet<string>(FallbackEnglishWords, StringComparer.OrdinalIgnoreCase);
    }
    private static HashSet<string> LoadRussianWords()
    {
        var loaded = TryLoadDictionaryFile("ru.txt");
        if (loaded is not null && loaded.Count >= 50) return loaded;
        return new HashSet<string>(FallbackRussianWords, StringComparer.OrdinalIgnoreCase);
    }
    private static HashSet<string>? TryLoadDictionaryFile(string fileName)
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "Dictionaries", fileName),
                Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
                Path.Combine(Environment.CurrentDirectory, "Assets", "Dictionaries", fileName),
            };
            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in lines)
                {
                    var w = raw.Trim();
                    if (w.Length == 0) continue;
                    if (w.StartsWith("#")) continue;
                    // Берём первое слово строки (защита от строк с пояснением)
                    var first = w.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (string.IsNullOrEmpty(first) || first.Length > MaxBufferLength) continue;
                    var isEnglishFile = string.Equals(fileName, "en.txt", StringComparison.OrdinalIgnoreCase);
                    var validAlphabet = isEnglishFile
                        ? first.All(IsLatin)
                        : first.All(IsCyrillic);
                    if (!validAlphabet) continue;
                    set.Add(first.ToLowerInvariant());
                }
                if (set.Count > 0) return set;
            }
        }
        catch { }
        return null;
    }

    private bool ReplaceWord(string originalText, string fixedText, char trailingSeparator, IntPtr foregroundWindow,
        IntPtr focusWindow, bool separatorAlreadyPresent = false, bool allowUndo = true)
    {
        // В отложенном сценарии точка уже введена и также должна быть удалена.
        var length = originalText.Length + (separatorAlreadyPresent ? 1 : 0);
        // Собираем всю замену в один массив INPUT и отправляем одним вызовом.
        var inputs = new List<INPUT>();

        AppendBackspaces(inputs, length);
        AppendUnicodeText(inputs, fixedText);

        // Восстанавливаем разделитель, который был подавлен в хуке, чтобы
        // после коррекции слово не слиплось со следующим.
        if (trailingSeparator != '\0')
        {
            inputs.Add(MakeSeparatorInput(trailingSeparator, keyUp: false));
            inputs.Add(MakeSeparatorInput(trailingSeparator, keyUp: true));
        }

        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());

        if (sent != array.Length)
        {
            // SendInput не атомарен. Пытаемся компенсировать уже отправленный префикс,
            // чтобы частичный Backspace/Unicode не оставил слово бесшумно повреждённым.
            var deletionEvents = length * 2;
            var replacementEvents = fixedText.Length * 2;
            var separatorEvents = trailingSeparator == '\0' ? 0 : 2;
            var recoverySucceeded = TryRecoverPartialReplacement(originalText, fixedText,
                trailingSeparator, separatorAlreadyPresent, (int)sent,
                deletionEvents, replacementEvents, separatorEvents);
            _logger.Warn($"LAYOUT | SendInput неполный: {sent}/{array.Length}; recovery={(recoverySucceeded ? "ok" : "failed")}; rc={Marshal.GetLastWin32Error()}");

            if (recoverySucceeded)
                return false; // исходный разделитель проходит обычным путём

            // Если down-событие разделителя успело уйти в целевой процесс, подавляем
            // оригинал, чтобы не дублировать пробел/знак. Иначе пропускаем оригинал.
            var separatorStart = deletionEvents + replacementEvents;
            var separatorMayHaveBeenSent = !separatorAlreadyPresent && separatorEvents > 0
                && sent > separatorStart;
            return separatorMayHaveBeenSent;
        }

        // Ctrl+Z undo не должен хранить контент пользовательского ввода в логах.
        // Tab/Enter могут переключить контрол или отправить форму — для них undo не сохраняем.
        if (allowUndo && trailingSeparator is not ('\t' or '\r' or '\n'))
        {
            _lastCorrection = new LastCorrection(originalText, fixedText, trailingSeparator,
                foregroundWindow, focusWindow, DateTime.UtcNow);
        }
        else
        {
            _lastCorrection = null;
        }
        _logger.Info($"LAYOUT | замена слова выполнена, событий={array.Length}");
        ShowCorrectionToastIfNeeded(allowUndo && trailingSeparator is not ('\t' or '\r' or '\n'));
        return true;
    }

    private bool TryRecoverPartialReplacement(string originalText, string fixedText, char trailingSeparator,
        bool separatorAlreadyPresent, int sent, int deletionEvents, int replacementEvents, int separatorEvents)
    {
        try
        {
            var releaseInputs = new List<INPUT>();
            var releaseInputCount = 0;

            // SendInput останавливается на префиксе списка. Если он закончился сразу
            // после KeyDown, освобождаем именно эту клавишу до восстановления текста.
            if (sent < deletionEvents && sent % 2 == 1)
            {
                releaseInputs.Add(MakeKeyInput(VkBack, true));
                releaseInputCount = 1;
            }
            else if (sent >= deletionEvents && sent < deletionEvents + replacementEvents
                && (sent - deletionEvents) % 2 == 1)
            {
                var charIndex = (sent - deletionEvents) / 2;
                if (charIndex >= 0 && charIndex < fixedText.Length)
                {
                    releaseInputs.Add(MakeUnicodeInput(fixedText[charIndex], true));
                    releaseInputCount = 1;
                }
            }
            else if (sent >= deletionEvents + replacementEvents && separatorEvents > 0
                && sent < deletionEvents + replacementEvents + separatorEvents
                && (sent - deletionEvents - replacementEvents) % 2 == 1)
            {
                releaseInputs.Add(MakeSeparatorInput(trailingSeparator, keyUp: true));
                releaseInputCount = 1;
            }

            if (releaseInputCount > 0 && SendInput((uint)releaseInputs.Count, releaseInputs.ToArray(), Marshal.SizeOf<INPUT>()) != releaseInputs.Count)
                return false;

            var recovery = new List<INPUT>();
            if (sent < deletionEvents)
            {
                // Каждая пара начинается с Backspace-down. Считаем down, включая
                // непарный последний down, и возвращаем только уже удалённый суффикс.
                var deletePresses = Math.Min(deletionEvents / 2, (sent + 1) / 2);
                var removedPeriod = separatorAlreadyPresent && deletePresses > 0;
                var removedWordChars = Math.Max(0, deletePresses - (separatorAlreadyPresent ? 1 : 0));
                if (removedWordChars > 0)
                {
                    var suffix = originalText.Substring(originalText.Length - removedWordChars);
                    AppendUnicodeText(recovery, suffix);
                }
                if (removedPeriod)
                    AppendUnicodeText(recovery, ".");
            }
            else
            {
                var insertedEventCount = Math.Max(0, Math.Min(replacementEvents, sent - deletionEvents));
                var insertedCharacters = Math.Min(fixedText.Length, (insertedEventCount + 1) / 2);
                var separatorWasStarted = separatorEvents > 0
                    && sent > deletionEvents + replacementEvents;
                var removeCount = insertedCharacters + (separatorWasStarted ? 1 : 0);
                AppendBackspaces(recovery, removeCount);
                AppendUnicodeText(recovery, originalText + (separatorAlreadyPresent ? "." : string.Empty));
            }

            if (recovery.Count == 0)
                return true;

            var recoveryArray = recovery.ToArray();
            return SendInput((uint)recoveryArray.Length, recoveryArray, Marshal.SizeOf<INPUT>()) == recoveryArray.Length;
        }
        catch (Exception ex)
        {
            _logger.Warn("LAYOUT | восстановление после частичного SendInput завершилось исключением: " + ex.GetType().Name);
            return false;
        }
    }

    private static void AppendBackspaces(List<INPUT> inputs, int count)
    {
        for (var i = 0; i < count; i++)
        {
            inputs.Add(MakeKeyInput(VkBack, false));
            inputs.Add(MakeKeyInput(VkBack, true));
        }
    }

    private static void AppendUnicodeText(List<INPUT> inputs, string text)
    {
        foreach (var c in text)
        {
            inputs.Add(MakeUnicodeInput(c, false));
            inputs.Add(MakeUnicodeInput(c, true));
        }
    }

    private static INPUT MakeSeparatorInput(char separator, bool keyUp)
    {
        if (separator is '\r' or '\n') return MakeKeyInput(VkReturn, keyUp);
        if (separator == '\t') return MakeKeyInput(VkTab, keyUp);
        return MakeUnicodeInput(separator, keyUp);
    }

    private bool IsCustomException(string text)
    {
        var normalized = NormalizeExceptionToken(text);
        lock (_gate)
        {
            return normalized.Length > 0 && _customExceptions.Contains(normalized);
        }
    }

    private void ShowCorrectionToastIfNeeded(bool undoAvailable)
    {
        if (!_showCorrectionToast) return;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastToastUtc).TotalSeconds < 2.5) return;
            _lastToastUtc = now;
        }

        try
        {
            var app = Application.Current;
            if (app is null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    AppNotificationCenter.Instance.Push(
                        L.T("Перевод раскладки"),
                        undoAvailable
                            ? L.T("Слово исправлено. Для отмены нажмите Ctrl+Z.")
                            : L.T("Слово исправлено."),
                        AppNotificationKind.Success);
                }
                catch { /* Toast не должен влиять на обработку клавиатуры. */ }
            }));
        }
        catch
        {
            // Уведомление необязательно и не должно влиять на ввод.
        }
    }

    private bool TryUndoLastCorrection(IntPtr foregroundWindow, IntPtr focusWindow, bool ctrlLeftDown, bool ctrlRightDown, bool ctrlGenericDown)
    {
        var correction = _lastCorrection;
        if (correction is null || correction.ForegroundWindow == IntPtr.Zero
            || foregroundWindow != correction.ForegroundWindow
            || (correction.FocusWindow != IntPtr.Zero && focusWindow != correction.FocusWindow)
            || (DateTime.UtcNow - correction.Timestamp).TotalSeconds > 10
            || correction.Separator is '\t' or '\r' or '\n')
        {
            return false;
        }

        var inputs = new List<INPUT>();
        // Ctrl физически ещё удерживается. Временно отпускаем активный Ctrl,
        // иначе Backspace может удалить слово целиком, а символы будут обработаны как shortcuts.
        if (ctrlLeftDown) inputs.Add(MakeKeyInput(0xA2, true));
        if (ctrlRightDown) inputs.Add(MakeKeyInput(0xA3, true));
        if (ctrlGenericDown) inputs.Add(MakeKeyInput(VkControl, true));
        // Убираем сохранённый разделитель, заменённое слово и возвращаем оригинал.
        inputs.Add(MakeKeyInput(VkBack, false)); inputs.Add(MakeKeyInput(VkBack, true));
        for (var i = 0; i < correction.Corrected.Length; i++)
        {
            inputs.Add(MakeKeyInput(VkBack, false)); inputs.Add(MakeKeyInput(VkBack, true));
        }
        foreach (var c in correction.Original)
        {
            inputs.Add(MakeUnicodeInput(c, false)); inputs.Add(MakeUnicodeInput(c, true));
        }
        if (correction.Separator != '\0')
        {
            inputs.Add(MakeUnicodeInput(correction.Separator, false));
            inputs.Add(MakeUnicodeInput(correction.Separator, true));
        }
        if (ctrlLeftDown) inputs.Add(MakeKeyInput(0xA2, false));
        if (ctrlRightDown) inputs.Add(MakeKeyInput(0xA3, false));
        if (ctrlGenericDown) inputs.Add(MakeKeyInput(VkControl, false));

        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
        if (sent != array.Length)
        {
            _logger.Warn($"LAYOUT | Ctrl+Z отмена не выполнена: SendInput {sent}/{array.Length}, rc={Marshal.GetLastWin32Error()}");
            return false;
        }

        _lastCorrection = null;
        _logger.Info("LAYOUT | последняя коррекция отменена");
        return true;
    }

    private static INPUT MakeKeyInput(uint vk, bool keyUp)
    {
        return new INPUT
        {
            type = InputKeyboard,
            u = new InputUnion
            {
                ki = new INPUT_KEYBOARD
                {
                    wVk = (ushort)vk,
                    wScan = 0,
                    dwFlags = keyUp ? KeyEventFKeyUp : 0u,
                    time = 0,
                    dwExtraInfo = HookExtraInfo
                }
            }
        };
    }

    private static INPUT MakeUnicodeInput(char c, bool keyUp)
    {
        return new INPUT
        {
            type = InputKeyboard,
            u = new InputUnion
            {
                ki = new INPUT_KEYBOARD
                {
                    wVk = 0,
                    wScan = (ushort)c,
                    dwFlags = KeyEventFUnicode | (keyUp ? KeyEventFKeyUp : 0u),
                    time = 0,
                    dwExtraInfo = HookExtraInfo
                }
            }
        };
    }

    private void ClearBuffer()
    {
        _buffer.Clear();
        _wordWindow = IntPtr.Zero;
        _wordLayoutId = 0;
        _pendingPeriod = null;
    }

    // ----- определение сфокусированного контрола и поля пароля -----

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal static IntPtr GetFocusedWindow(IntPtr foregroundWindow)
    {
        if (foregroundWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            // hwndFocus (сфокусированный контрол внутри очереди сообщений потока)
            // надёжнее верхнеуровневого HWND для поиска дочерних контролов.
            var threadId = GetWindowThreadProcessId(foregroundWindow, out _);
            if (threadId == 0)
            {
                return IntPtr.Zero;
            }

            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(threadId, out info))
            {
                return info.hwndFocus;
            }
        }
        catch
        {
        }

        return IntPtr.Zero;
    }

    internal static bool IsPasswordField(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var className = new StringBuilder(256);
            if (GetClassName(hwnd, className, className.Capacity) > 0
                && className.ToString() == "Edit")
            {
                var style = GetWindowLong(hwnd, GwlStyle);
                if ((style & EsPassword) != 0)
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        // UI Automation (аудит, п. 11): у современных приложений (Chromium,
        // Electron, UWP) нет класса «Edit» и стиля ES_PASSWORD, но поле помечено
        // как парольное через AutomationElement.IsPasswordProperty. Без этой
        // проверки переводчик правил бы пароль на лету.
        return IsPasswordByAutomation(hwnd);
    }

    // Кэш ответа UIA: запрос к чужому процессу стоит десятки миллисекунд,
    // а проверка пароля выполняется на каждом нажатии клавиши.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, (bool IsPassword, long Stamp)> PasswordFieldCache =
        new();

    private const long PasswordFieldCacheTtlMilliseconds = 1000;

    private static bool IsPasswordByAutomation(IntPtr hwnd)
    {
        var now = Environment.TickCount64;
        if (PasswordFieldCache.TryGetValue(hwnd, out var cached)
            && now - cached.Stamp < PasswordFieldCacheTtlMilliseconds)
        {
            return cached.IsPassword;
        }

        var isPassword = false;
        try
        {
            // FocusedElement — реально сфокусированный элемент: в Chromium/Electron
            // это само поле ввода, а не окно (FromHandle на окне вернул бы корень
            // документа, где IsPassword всегда false).
            var focused = AutomationElement.FocusedElement;
            if (focused is not null)
            {
                isPassword = focused.Current.IsPassword;
            }
            else
            {
                var element = AutomationElement.FromHandle(hwnd);
                isPassword = element?.Current.IsPassword == true;
            }
        }
        catch (Exception)
        {
            // Элемент мог исчезнуть или процесс не отвечает — считаем поле обычным.
            isPassword = false;
        }

        // Кэш не должен расти бесконечно: чистим протухшие записи.
        if (PasswordFieldCache.Count > 512)
        {
            foreach (var pair in PasswordFieldCache)
            {
                if (now - pair.Value.Stamp >= PasswordFieldCacheTtlMilliseconds)
                {
                    PasswordFieldCache.TryRemove(pair.Key, out _);
                }
            }
        }

        PasswordFieldCache[hwnd] = (isPassword, now);
        return isPassword;
    }

    // ----- транслитерация -----

    internal static string TransliterateLatinToCyrillic(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (LatinToCyrillic.TryGetValue(char.ToLowerInvariant(c), out var mapped))
            {
                sb.Append(char.IsUpper(c) ? char.ToUpperInvariant(mapped) : mapped);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    internal static string TransliterateCyrillicToLatin(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            // ё (строчная) не транслитерируется: пропускаем как есть, чтобы не ломать
            // обратную транслитерацию (ё часто заменяют на е; см. IsCorrectableWord).
            if (c == 'ё')
            {
                sb.Append(c);
                continue;
            }
            // Сначала точное совпадение: uppercase-символы русской раскладки
            // (Ё, Ж, Э, Х, Ъ, Б, Ю) соответствуют отдельным Shift-символам EN.
            if (CyrillicToLatin.TryGetValue(c, out var exact))
            {
                sb.Append(exact);
            }
            else if (CyrillicToLatin.TryGetValue(char.ToLowerInvariant(c), out var mapped))
            {
                sb.Append(char.IsUpper(c) ? char.ToUpperInvariant(mapped) : mapped);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    internal static bool IsLatin(char c) => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z';
    internal static bool IsCyrillic(char c) => (c >= '\u0430' && c <= '\u044f') || c == '\u0451'
        || (c >= '\u0410' && c <= '\u042f') || c == '\u0401';

    private static bool IsRussianLayout(IntPtr hkl)
    {
        var id = (uint)hkl.ToInt64() & 0xFFFF;
        return (ushort)(id & 0x3FF) == LangRussian;
    }

    private static bool IsEnglishLayout(IntPtr hkl)
    {
        var id = (uint)hkl.ToInt64() & 0xFFFF;
        return (ushort)(id & 0x3FF) == LangEnglish;
    }

    private static int GetLayoutId(IntPtr hkl) => (int)((uint)hkl.ToInt64() & 0xFFFF);

    // ----- карты транслитерации (русская раскладка ЙЦУКЕН <-> английская QWERTY) -----

    private static readonly IReadOnlyDictionary<char, char> LatinToCyrillic = new Dictionary<char, char>
    {
        ['!'] = '!', ['%'] = '%', ['*'] = '*', ['('] = '(', [')'] = ')', ['_'] = '_', ['+'] = '+', ['='] = '=', ['-'] = '-',
        ['a'] = 'ф', ['b'] = 'и', ['c'] = 'с', ['d'] = 'в', ['e'] = 'у',
        ['f'] = 'а', ['g'] = 'п', ['h'] = 'р', ['i'] = 'ш', ['j'] = 'о',
        ['k'] = 'л', ['l'] = 'д', ['m'] = 'ь', ['n'] = 'т', ['o'] = 'щ',
        ['p'] = 'з', ['q'] = 'й', ['r'] = 'к', ['s'] = 'ы', ['t'] = 'е',
        ['u'] = 'г', ['v'] = 'м', ['w'] = 'ц', ['x'] = 'ч', ['y'] = 'н',
        ['z'] = 'я',
        [';'] = 'ж', ['\''] = 'э', ['['] = 'х', [']'] = 'ъ',
        [','] = 'б', ['.'] = 'ю', ['/'] = '.',
        ['`'] = 'ё', ['~'] = 'Ё', ['@'] = '"', ['#'] = '№', ['$'] = ';',
        ['^'] = ':', ['&'] = '?', [':'] = 'Ж', ['"'] = 'Э', ['{'] = 'Х',
        ['}'] = 'Ъ', ['<'] = 'Б', ['>'] = 'Ю', ['?'] = ',', ['|'] = '/'
    };

    private static readonly IReadOnlyDictionary<char, char> CyrillicToLatin = new Dictionary<char, char>
    {
        ['!'] = '!', ['%'] = '%', ['*'] = '*', ['('] = '(', [')'] = ')', ['_'] = '_', ['+'] = '+', ['='] = '=', ['-'] = '-',
        ['ф'] = 'a', ['и'] = 'b', ['с'] = 'c', ['в'] = 'd', ['у'] = 'e',
        ['а'] = 'f', ['п'] = 'g', ['р'] = 'h', ['ш'] = 'i', ['о'] = 'j',
        ['л'] = 'k', ['д'] = 'l', ['ь'] = 'm', ['т'] = 'n', ['щ'] = 'o',
        ['з'] = 'p', ['й'] = 'q', ['к'] = 'r', ['ы'] = 's', ['е'] = 't',
        ['г'] = 'u', ['м'] = 'v', ['ц'] = 'w', ['ч'] = 'x', ['н'] = 'y',
        ['я'] = 'z',
        ['ж'] = ';', ['э'] = '\'', ['х'] = '[', ['ъ'] = ']',
        ['б'] = ',', ['ю'] = '.', ['.'] = '/',
        ['ё'] = '`', ['Ё'] = '~', ['"'] = '@', ['№'] = '#', [';'] = '$',
        [':'] = '^', ['?'] = '&', ['Ж'] = ':', ['Э'] = '"', ['Х'] = '{',
        ['Ъ'] = '}', ['Б'] = '<', ['Ю'] = '>', [','] = '?', ['/'] = '|'
    };

    // ----- P/Invoke -----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, out GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint wVirtKey,
        uint wScanCode,
        byte[] lpKeyState,
        [Out] StringBuilder pwszBuff,
        int cchBuff,
        uint wFlags,
        IntPtr dwhkl);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public INPUT_KEYBOARD ki;
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_KEYBOARD
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    // Алиас для совместимости с вызовами, ожидающими
    // KeyboardLayoutTranslator.DetectKeyboardLayoutTranslator.WordLanguage(...).
    public static class DetectKeyboardLayoutTranslator
    {
        public static WordLanguage WordLanguage(string text) => DetectWordLanguage(text);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { Stop(); }
        catch { /* Не падаем при закрытии приложения. */ }
    }
}
