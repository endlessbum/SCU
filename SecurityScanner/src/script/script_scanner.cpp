#include "script/script_scanner.h"

#include <windows.h>

#include <cctype>
#include <cstring>
#include <vector>

namespace scan {

namespace {

constexpr size_t kMaxScriptBytes = 2ull * 1024 * 1024; // большие скрипты не читаем
constexpr size_t kMinBase64Run = 200;                  // минимальный base64-блоб для декодирования
constexpr size_t kMaxDecodedBytes = 1024 * 1024;

// Копия файла как ASCII-текст В ИСХОДНОМ РЕГИСТРЕ (п. SCAN-05 аудита:
// base64-прогон декодируется только из оригинального регистра, декодер
// различает 'A-Z' и 'a-z'; lowercase делается позже, отдельно для матчинга).
bool ReadText(const std::wstring& path, std::string& text)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0
        || static_cast<unsigned long long>(size.QuadPart) > kMaxScriptBytes) {
        CloseHandle(file);
        return false;
    }

    std::vector<char> buffer(static_cast<size_t>(size.QuadPart));
    DWORD read = 0;
    const bool ok = ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)
                    && read == buffer.size();
    CloseHandle(file);
    if (!ok) {
        return false;
    }

    text.assign(buffer.data(), buffer.size());
    return true;
}

std::string ToLowerAscii(const std::string& text)
{
    std::string lowered;
    lowered.reserve(text.size());
    for (const char c : text) {
        lowered += static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
    return lowered;
}

int DecodeBase64Char(char c)
{
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
}

// Декодирование base64-прогонов из текста В ОРИГИНАЛЬНОМ РЕГИСТРЕ (обфускация
// -EncodedCommand / FromBase64String). Результат — lowercase: он идёт только
// в общий поток паттерн-матчинга.
void AppendDecodedBase64(const std::string& text, std::string& decodedText)
{
    size_t runStart = std::string::npos;
    for (size_t i = 0; i <= text.size(); ++i) {
        const bool isBase64 = i < text.size()
                              && (std::isalnum(static_cast<unsigned char>(text[i]))
                                  || text[i] == '+' || text[i] == '/');
        if (isBase64 && runStart == std::string::npos) {
            runStart = i;
            continue;
        }
        if (!isBase64 && runStart != std::string::npos) {
            const size_t runLength = i - runStart;
            // Валидные длины base64-прогона без учёта '='-пэддинга: %4 == 0
            // (без пэддинга), 2 (один '='), 3 (два '='). Прежний допуск только
            // %4 == 0 отбрасывал все payload'ы с пэддингом (п. SCAN-05).
            if (runLength >= kMinBase64Run
                && decodedText.size() < kMaxDecodedBytes
                && runLength % 4 != 1) {
                int accumulator = 0;
                int bits = 0;
                for (size_t j = runStart; j < i; ++j) {
                    const int value = DecodeBase64Char(text[j]);
                    if (value < 0) {
                        break;
                    }
                    accumulator = (accumulator << 6) | value;
                    bits += 6;
                    if (bits >= 8) {
                        bits -= 8;
                        const char decoded = static_cast<char>((accumulator >> bits) & 0xFF);
                        // UTF-16LE-payload'ы (-EncodedCommand) декодируются в текст
                        // с перемежающимися '\0': без их выкидывания ни один
                        // паттерн в decodedText не сматчится (п. SCAN-05).
                        if (decoded != '\0') {
                            decodedText += static_cast<char>(std::tolower(static_cast<unsigned char>(decoded)));
                        }
                    }
                }
                decodedText += '\n';
            }
            runStart = std::string::npos;
        }
    }
}

bool Contains(const std::string& text, const char* pattern)
{
    return text.find(pattern) != std::string::npos;
}

// Комбинированный сигнал: оба паттерна встречаются (для cradle-детекции).
bool ContainsBoth(const std::string& text, const char* first, const char* second)
{
    return text.find(first) != std::string::npos && text.find(second) != std::string::npos;
}

void AddSignal(ScriptAnalysis& analysis, int weight, const wchar_t* signal)
{
    analysis.score += weight;
    analysis.signals.push_back(signal);
}

void AnalyzePowerShell(const std::string& text, ScriptAnalysis& analysis)
{
    // Download cradle: IEX + скачивание (п. 19: download+execute).
    if (ContainsBoth(text, "iex", "downloadstring")
        || ContainsBoth(text, "invoke-expression", "downloadstring")
        || ContainsBoth(text, "iex", "net.webclient")
        || ContainsBoth(text, "invoke-expression", "downloadstring")
        || ContainsBoth(text, "invoke-expression", "invoke-webrequest")
        || ContainsBoth(text, "invoke-expression", "invoke-restmethod")
        || ContainsBoth(text, "iex", "xmlhttp")
        || ContainsBoth(text, "iex", "msxml2")) {
        AddSignal(analysis, 3, L"ps:download-cradle");
    } else if (Contains(text, "downloadstring") || Contains(text, "net.webclient")) {
        AddSignal(analysis, 1, L"ps:web-download");
    }

    // Encoded payload + его декодированное содержимое.
    if (Contains(text, "-encodedcommand") || Contains(text, "-enc ")
        || Contains(text, "frombase64string")) {
        AddSignal(analysis, 2, L"ps:encoded-payload");
    }

    // Тамперинг с Defender (п. 1: сканер не трогает Defender — но скрипты,
    // которые это делают, это главный кандидат в suspicious).
    if ((Contains(text, "set-mppreference") || Contains(text, "add-mppreference")
         || Contains(text, "remove-mppreference"))
        && (Contains(text, "exclusion") || Contains(text, "disable"))) {
        AddSignal(analysis, 3, L"ps:defender-tamper");
    }

    // Persistence.
    if (Contains(text, "new-service") || Contains(text, "register-scheduledtask")
        || Contains(text, "new-scheduledtask") || Contains(text, "schtasks /create")
        || Contains(text, "register-wmievent") || Contains(text, "eventconsumer")
        || Contains(text, "currentversion\\run") || Contains(text, "runonce")) {
        AddSignal(analysis, 2, L"ps:persistence");
    }

    // Скрытность.
    if (Contains(text, "-window hidden") || Contains(text, "-w hidden")
        || Contains(text, "-nop -w hidden")) {
        AddSignal(analysis, 1, L"ps:hidden-window");
    }

    // Скачивание утилитами.
    if (Contains(text, "certutil") && (Contains(text, "-urlcache") || Contains(text, "-decode"))) {
        AddSignal(analysis, 2, L"ps:certutil");
    }
    if (Contains(text, "bitsadmin") && Contains(text, "/transfer")) {
        AddSignal(analysis, 2, L"ps:bitsadmin");
    }
}

void AnalyzeBatch(const std::string& text, ScriptAnalysis& analysis)
{
    if (Contains(text, "reg add") && (Contains(text, "\\run") || Contains(text, "runonce"))) {
        AddSignal(analysis, 3, L"bat:run-key");
    }
    if (Contains(text, "schtasks") && Contains(text, "/create")) {
        AddSignal(analysis, 2, L"bat:scheduled-task");
    }
    if (ContainsBoth(text, "powershell", "-enc") || ContainsBoth(text, "powershell", "-encodedcommand")) {
        AddSignal(analysis, 3, L"bat:encoded-powershell");
    }
    if (Contains(text, "certutil") && (Contains(text, "-urlcache") || Contains(text, "-decode"))) {
        AddSignal(analysis, 2, L"bat:certutil");
    }
    if (Contains(text, "bitsadmin") && Contains(text, "/transfer")) {
        AddSignal(analysis, 2, L"bat:bitsadmin");
    }
    if (Contains(text, "vssadmin") && Contains(text, "delete shadows")) {
        AddSignal(analysis, 3, L"bat:shadow-delete");
    }
    if (ContainsBoth(text, "sc config", "start= disabled")) {
        AddSignal(analysis, 1, L"bat:service-disable");
    }
}

void AnalyzeScriptHost(const std::string& text, ScriptAnalysis& analysis)
{
    // VBS/JS/HTA: WSH-исполнение, скачивание, дисковый ввод-вывод.
    if (Contains(text, "wscript.shell")) {
        AddSignal(analysis, 2, L"wsh:shell-object");
    }
    if (Contains(text, "adodb.stream")) {
        AddSignal(analysis, 2, L"wsh:adodb-stream");
    }
    if (Contains(text, "msxml2.xmlhttp") || Contains(text, "winhttp.winhttprequest")
        || Contains(text, "serverxmlhttp")) {
        AddSignal(analysis, 2, L"wsh:http-download");
    }
    if (Contains(text, ".run(") || Contains(text, ".exec(")) {
        AddSignal(analysis, 1, L"wsh:run-exec");
    }
    if (ContainsBoth(text, "msxml2.xmlhttp", "adodb.stream")) {
        AddSignal(analysis, 2, L"wsh:download-drop");
    }
}

} // namespace

ScriptAnalysis ScriptScanner::Analyze(const std::wstring& path)
{
    ScriptAnalysis analysis;
    // ReadText отдаёт оригинальный регистр: base64 декодируем из него,
    // а lowercase-копию строим только для паттерн-матчинга (п. SCAN-05).
    std::string text;
    if (!ReadText(path, text)) {
        return analysis;
    }

    std::string decoded;
    AppendDecodedBase64(text, decoded);

    std::string combined = ToLowerAscii(text);
    if (!decoded.empty()) {
        combined += '\n';
        combined += decoded; // уже lowercase (см. AppendDecodedBase64)
    }

    return AnalyzeText(combined);
}

ScriptAnalysis ScriptScanner::AnalyzeText(const std::string& combined)
{
    ScriptAnalysis analysis;

    // Семейство определяется по расширению пути, но сигнатуры PS/BAT безопасно
    // применять и по содержимому: PATHEXT-полиморфы редки, а allowlist
    // собственных скриптов сработал раньше.
    AnalyzePowerShell(combined, analysis);
    AnalyzeBatch(combined, analysis);
    AnalyzeScriptHost(combined, analysis);

    return analysis;
}

} // namespace scan
