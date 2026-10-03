#include "system/persistence_scanner.h"

#include "core/path_util.h"

#include <windows.h>

#include <algorithm>
#include <cctype>

namespace scan {

namespace {

const wchar_t* kRunLocations[] = {
    L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",
    L"Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce",
};

const wchar_t* kHiveNames[] = { L"HKCU", L"HKLM" };

std::wstring Trim(const std::wstring& value)
{
    size_t begin = 0;
    size_t end = value.size();
    while (begin < end && iswspace(value[begin])) ++begin;
    while (end > begin && iswspace(value[end - 1])) --end;
    return value.substr(begin, end - begin);
}

// Разрешение исполняемого файла из командной строки (аудит 2, п. 8):
// 1) кавыченный токен — до закрывающей кавычки;
// 2) некавыченный путь с пробелами — самый длинный СУЩЕСТВУЮЩИЙ префикс,
//    обрезаемый по пробелам ("C:\Program Files\App\App.exe -arg" → exe),
//    иначе первый токен (прежнее поведение как fallback).
bool FileExists(const std::wstring& path);
void ExpandEnvironment(std::wstring& value);

std::wstring ResolveTarget(const std::wstring& command)
{
    std::wstring value = Trim(command);
    if (value.rfind(L"\\??\\", 0) == 0) {
        value = value.substr(4);
    }
    if (value.rfind(L"\\\\?\\", 0) == 0) {
        value = value.substr(4);
    }
    if (value.rfind(L"\" ", 0) == 0) {
        value = Trim(value.substr(2));
    }

    if (value.empty()) {
        return {};
    }

    if (value[0] == L'"') {
        const size_t closing = value.find(L'"', 1);
        return closing == std::wstring::npos ? value.substr(1) : value.substr(1, closing - 1);
    }

    const size_t firstSpace = value.find(L' ');
    if (firstSpace == std::wstring::npos) {
        return value;
    }

    std::wstring best = value.substr(0, firstSpace);
    size_t space = firstSpace;
    while (space != std::wstring::npos) {
        const std::wstring candidate = value.substr(0, space);
        if (FileExists(candidate)) {
            best = candidate;
        }
        space = value.find(L' ', space + 1);
    }
    return best;
}

// Цель записи автозапуска: окружение раскрывается ДО разбора — иначе пути с
// %ProgramFiles% (x86) не находятся как существующие префиксы.
std::wstring ResolveEntryTarget(const std::wstring& command)
{
    std::wstring expanded = ExpandEnvironmentDynamic(command);
    std::wstring target = ResolveTarget(expanded.empty() ? command : expanded);
    ExpandEnvironment(target);
    return target;
}

bool FileExists(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}

void ExpandEnvironment(std::wstring& value)
{
    // Динамический буфер (аудит п. 9): длинное значение раскрылось молча
    // обрезанным либо не раскрывалось вовсе; теперь размер берём документированный.
    const std::wstring expanded = ExpandEnvironmentDynamic(value);
    if (!expanded.empty()) {
        value = expanded;
    }
}

// ---------- Registry ----------

void EnumRegistryValues(HKEY hive, int hiveIndex, const wchar_t* subKey,
                        const std::function<void(const PersistenceEntry&)>& onEntry,
                        const PersistenceScanner::SkipCallback& onSkipped)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(hive, subKey, 0, KEY_READ, &key) != ERROR_SUCCESS) {
        return; // нет ключа/нет доступа — не ошибка сканирования
    }

    const std::wstring location = std::wstring(kHiveNames[hiveIndex]) + L"\\" + subKey;

    for (DWORD index = 0;; ++index) {
        wchar_t valueName[16384]{};
        DWORD nameLength = 16384;
        DWORD type = 0;
        DWORD dataSize = 0;
        // Размер значения запрашивается первым (аудит 2, п. 12): значение
        // больше 4 КБ больше не обрывает перечисление ключа.
        LSTATUS status = RegEnumValueW(key, index, valueName, &nameLength,
                                       nullptr, &type, nullptr, &dataSize);
        if (status != ERROR_SUCCESS) {
            break;
        }
        if (type != REG_SZ && type != REG_EXPAND_SZ) {
            continue;
        }
        if (dataSize > 65536) {
            // Чрезмерно длинное значение не читаем, но перечисление ключа
            // продолжаем — остальные значения остаются в coverage.
            if (onSkipped) {
                onSkipped(location + L"\\" + valueName, L"registry-value-too-large");
            }
            continue;
        }

        std::wstring data;
        if (dataSize > 0) {
            data.resize(dataSize / sizeof(wchar_t) + 1, L'\0');
            nameLength = 16384; // первая фаза вернула фактическую длину имени — вернуть размер буфера
            DWORD got = static_cast<DWORD>(data.size() * sizeof(wchar_t));
            status = RegEnumValueW(key, index, valueName, &nameLength,
                                   nullptr, &type, reinterpret_cast<BYTE*>(data.data()), &got);
            if (status != ERROR_SUCCESS) {
                // Значение могло измениться между двумя вызовами — пропускаем
                // его, но не весь ключ.
                if (onSkipped) {
                    onSkipped(location + L"\\" + valueName, L"registry-value-unreadable");
                }
                continue;
            }
            while (!data.empty() && data.back() == L'\0') {
                data.pop_back();
            }
        }

        PersistenceEntry entry;
        entry.location = location;
        entry.name = valueName;
        entry.command = data;
        entry.targetPath = ResolveEntryTarget(entry.command);
        onEntry(entry);
    }

    RegCloseKey(key);
}

// Winlogon: Shell и Userinit — списки через запятую.
void EnumWinlogon(HKEY hive, int hiveIndex, const std::function<void(const PersistenceEntry&)>& onEntry,
                  const PersistenceScanner::SkipCallback& onSkipped)
{
    const wchar_t* subKey = L"Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon";
    HKEY key = nullptr;
    if (RegOpenKeyExW(hive, subKey, 0, KEY_READ, &key) != ERROR_SUCCESS) {
        return;
    }

    for (const wchar_t* valueName : { L"Shell", L"Userinit" }) {
        // Двухфазное чтение (аудит 2, п. 12): ERROR_MORE_DATA больше не дропает
        // значение молча.
        DWORD dataSize = 0;
        DWORD type = 0;
        if (RegQueryValueExW(key, valueName, nullptr, &type, nullptr, &dataSize) != ERROR_SUCCESS
            || (type != REG_SZ && type != REG_EXPAND_SZ)
            || dataSize == 0 || dataSize > 65536) {
            if (dataSize > 65536 && onSkipped) {
                onSkipped(std::wstring(kHiveNames[hiveIndex]) + L"\\" + subKey + L"\\" + valueName,
                          L"registry-value-too-large");
            }
            continue;
        }

        std::wstring data(dataSize / sizeof(wchar_t) + 1, L'\0');
        DWORD got = static_cast<DWORD>(data.size() * sizeof(wchar_t));
        if (RegQueryValueExW(key, valueName, nullptr, &type,
                             reinterpret_cast<BYTE*>(data.data()), &got) != ERROR_SUCCESS) {
            continue;
        }
        while (!data.empty() && data.back() == L'\0') {
            data.pop_back();
        }

        size_t start = 0;
        while (start <= data.size()) {
            const size_t comma = data.find(L',', start);
            const std::wstring part = Trim(data.substr(
                start, comma == std::wstring::npos ? std::wstring::npos : comma - start));
            if (!part.empty()) {
                PersistenceEntry entry;
                entry.location = std::wstring(kHiveNames[hiveIndex]) + L"\\" + subKey;
                entry.name = valueName;
                entry.command = part;
                entry.targetPath = ResolveEntryTarget(part);
                onEntry(entry);
            }
            if (comma == std::wstring::npos) {
                break;
            }
            start = comma + 1;
        }
    }

    RegCloseKey(key);
}

// ---------- Startup-папки ----------

void EnumStartupFolder(const std::wstring& folder,
                       const std::function<void(const PersistenceEntry&)>& onEntry)
{
    WIN32_FIND_DATAW findData{};
    HANDLE find = FindFirstFileExW((folder + L"\\*").c_str(), FindExInfoBasic, &findData,
                                   FindExSearchNameMatch, nullptr, 0);
    if (find == INVALID_HANDLE_VALUE) {
        return;
    }

    do {
        const std::wstring name = findData.cFileName;
        if (name == L"." || name == L".." || (findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
            continue;
        }
        PersistenceEntry entry;
        entry.location = L"startup-folder";
        entry.name = name;
        entry.command = folder + L"\\" + name;
        entry.targetPath = entry.command;
        onEntry(entry);
    } while (FindNextFileW(find, &findData));
    FindClose(find);
}

// ---------- Службы (ImagePath из реестра) ----------

void EnumServices(const std::function<void(const PersistenceEntry&)>& onEntry)
{
    const wchar_t* servicesKey = L"SYSTEM\\CurrentControlSet\\Services";
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, servicesKey, 0, KEY_READ, &key) != ERROR_SUCCESS) {
        return;
    }

    for (DWORD index = 0;; ++index) {
        wchar_t serviceName[256]{};
        DWORD nameLength = 256;
        if (RegEnumKeyExW(key, index, serviceName, &nameLength,
                          nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS) {
            break;
        }

        HKEY serviceKey = nullptr;
        if (RegOpenKeyExW(key, serviceName, 0, KEY_READ, &serviceKey) != ERROR_SUCCESS) {
            continue;
        }

        // Размер ImagePath запрашивается первым (аудит п. 9): фиксированный
        // буфер 2048 молча резал длинные пути сервисов.
        DWORD dataSize = 0;
        DWORD type = 0;
        if (RegQueryValueExW(serviceKey, L"ImagePath", nullptr, &type, nullptr, &dataSize) == ERROR_SUCCESS
            && (type == REG_SZ || type == REG_EXPAND_SZ)
            && dataSize > 0 && dataSize <= 65536) {
            std::wstring imagePath(dataSize / sizeof(wchar_t) + 1, L'\0');
            dataSize = static_cast<DWORD>(imagePath.size() * sizeof(wchar_t));
            if (RegQueryValueExW(serviceKey, L"ImagePath", nullptr, &type,
                                 reinterpret_cast<BYTE*>(imagePath.data()), &dataSize) == ERROR_SUCCESS) {
                while (!imagePath.empty() && imagePath.back() == L'\0') {
                    imagePath.pop_back();
                }
                PersistenceEntry entry;
                entry.location = L"HKLM\\" + std::wstring(servicesKey);
                entry.name = serviceName;
                entry.command = imagePath;
                entry.targetPath = ResolveEntryTarget(imagePath);
                onEntry(entry);
            }
        }

        RegCloseKey(serviceKey);
    }

    RegCloseKey(key);
}

// ---------- Scheduled Tasks (файлы XML) ----------

// Декодирование XML-сущностей в тексте элемента (аудит 2, п. 10): путь
// C:\x&amp;y больше не приходит в анализ как C:\x&y-мусор или обрыв.
std::wstring DecodeXmlEntities(const std::wstring& text)
{
    if (text.find(L'&') == std::wstring::npos) {
        return text;
    }

    std::wstring result;
    result.reserve(text.size());
    for (size_t i = 0; i < text.size();) {
        if (text[i] != L'&') {
            result.push_back(text[i++]);
            continue;
        }
        const size_t semicolon = text.find(L';', i + 1);
        if (semicolon == std::wstring::npos || semicolon - i > 10) {
            result.push_back(text[i++]);
            continue;
        }
        const std::wstring entity = text.substr(i + 1, semicolon - i - 1);
        if (entity == L"amp") result.push_back(L'&');
        else if (entity == L"lt") result.push_back(L'<');
        else if (entity == L"gt") result.push_back(L'>');
        else if (entity == L"quot") result.push_back(L'"');
        else if (entity == L"apos") result.push_back(L'\'');
        else if (!entity.empty() && entity[0] == L'#') {
            unsigned long code = 0;
            bool ok = true;
            if (entity.size() > 2 && (entity[1] == L'x' || entity[1] == L'X')) {
                const std::wstring hex = entity.substr(2);
                const wchar_t* end = nullptr;
                code = wcstoul(hex.c_str(), const_cast<wchar_t**>(&end), 16);
                ok = end != nullptr && *end == L'\0' && code > 0 && code <= 0x10FFFF;
            } else {
                const wchar_t* end = nullptr;
                code = wcstoul(entity.c_str() + 1, const_cast<wchar_t**>(&end), 10);
                ok = end != nullptr && *end == L'\0' && code > 0 && code <= 0x10FFFF;
            }
            if (ok) {
                result.push_back(static_cast<wchar_t>(code));
            } else {
                result.append(text.substr(i, semicolon - i + 1)); // не сущность — как есть
            }
        } else {
            result.append(text.substr(i, semicolon - i + 1)); // неизвестная — как есть
        }
        i = semicolon + 1;
    }
    return result;
}

static wchar_t ToLowerW(wchar_t c) { return static_cast<wchar_t>(towlower(c)); }

// Регистронезависимый поиск открывающего/закрывающего тега по ЛОКАЛЬНОМУ имени
// (namespace-префикс <ns:Command> и атрибуты <Command attr="x"> не ломают поиск;
// комментарии <!-- --> и CDATA пропускаются). Возвращает текст элемента.
std::wstring ExtractXmlElementText(const std::wstring& xml, const wchar_t* localName)
{
    const std::wstring name = localName;
    const size_t nameLength = name.size();

    size_t searchFrom = 0;
    for (;;) {
        // Поиск "<" + возможный префикс ":" + name + (пробел | '/' | '>').
        size_t openTag = std::wstring::npos;
        size_t contentBegin = std::wstring::npos;
        for (size_t pos = searchFrom;;) {
            pos = xml.find(L'<', pos);
            if (pos == std::wstring::npos) {
                return {};
            }
            size_t tag = pos + 1;
            if (tag < xml.size() && xml[tag] == L'?') { // <?xml …?> — не элемент
                pos = tag;
                continue;
            }
            if (tag + 3 < xml.size() && xml.compare(tag, 3, L"!--") == 0) { // комментарий
                const size_t commentEnd = xml.find(L"-->", tag + 3);
                if (commentEnd == std::wstring::npos) {
                    return {};
                }
                pos = commentEnd + 3;
                continue;
            }
            if (tag < xml.size() && xml[tag] == L'/') {
                pos = tag; // закрывающий тег — не наш открывающий
                continue;
            }
            if (tag < xml.size() && xml[tag] == L':') {
                ++tag;
            }
            if (xml.size() - tag < nameLength
                || _wcsnicmp(xml.c_str() + tag, name.c_str(), nameLength) != 0) {
                pos = tag;
                continue;
            }
            const wchar_t after = tag + nameLength < xml.size() ? xml[tag + nameLength] : L'\0';
            if (after != L' ' && after != L'\t' && after != L'\r' && after != L'\n' && after != L'>'
                && after != L'/') {
                pos = tag;
                continue;
            }
            // Наш открывающий тег; тело — после '>'.
            size_t bodyBegin = xml.find(L'>', tag + nameLength);
            if (bodyBegin == std::wstring::npos) {
                return {};
            }
            ++bodyBegin;
            // Самозакрытый элемент — текст пуст.
            if (bodyBegin >= 2 && xml[bodyBegin - 2] == L'/') {
                return {};
            }
            openTag = pos;
            contentBegin = bodyBegin;
            break;
        }
        if (openTag == std::wstring::npos) {
            return {};
        }

        // Закрывающий тег: "</" [префикс:]localName [пробелы] '>'.
        size_t pos = contentBegin;
        for (;;) {
            pos = xml.find(L'<', pos);
            if (pos == std::wstring::npos) {
                return {};
            }
            if (pos + 1 < xml.size() && xml[pos + 1] != L'/') {
                // Вложенный элемент/CDATA/PI — пропускаем его открывающий тег.
                pos += 1;
                continue;
            }
            size_t tag = pos + 2;
            if (tag < xml.size() && xml[tag] == L':') {
                ++tag;
            }
            if (xml.size() - tag >= nameLength
                && _wcsnicmp(xml.c_str() + tag, name.c_str(), nameLength) == 0) {
                break;
            }
            pos = tag;
        }

        std::wstring content = xml.substr(contentBegin, pos - contentBegin);
        // CDATA: берём содержимое без экранирования.
        const size_t cdata = content.find(L"<![CDATA[");
        if (cdata != std::wstring::npos) {
            const size_t cdataEnd = content.find(L"]]>", cdata);
            if (cdataEnd != std::wstring::npos) {
                return content.substr(cdata + 9, cdataEnd - cdata - 9);
            }
        }
        return Trim(DecodeXmlEntities(content));
    }
}

// Command + Arguments задачи одним командным блоком (аудит 2, п. 9):
// <Command>powershell.exe</Command><Arguments>-enc …</Arguments> раньше
// терял аргументы — вредоносное содержимое не доходило до ScriptScanner.
std::wstring ExtractTaskCommandLine(const std::wstring& path,
                                    const PersistenceScanner::SkipCallback& onSkipped)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        if (onSkipped) {
            onSkipped(path, L"task-unreadable");
        }
        return {};
    }

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > 1024 * 1024) {
        CloseHandle(file);
        if (onSkipped) {
            onSkipped(path, L"task-unreadable");
        }
        return {};
    }

    std::vector<char> buffer(static_cast<size_t>(size.QuadPart));
    DWORD read = 0;
    const bool ok = ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)
                    && read == buffer.size();
    CloseHandle(file);
    if (!ok) {
        if (onSkipped) {
            onSkipped(path, L"task-unreadable");
        }
        return {};
    }

    // UTF-16 LE с BOM → wide; UTF-16 LE без BOM распознаём по нулевым старшим
    // байтам первых символов (XML задач начинается с '<'?xml…'), иначе ANSI/UTF-8.
    // П. PAR-01: прежде без-BOM UTF-16 уходил в UTF-8-ветку и терялся.
    std::wstring wide;
    const bool utf16Bom = buffer.size() >= 2 && static_cast<unsigned char>(buffer[0]) == 0xFF
                          && static_cast<unsigned char>(buffer[1]) == 0xFE;
    const bool utf16NoBom = !utf16Bom && buffer.size() >= 2 && buffer.size() % 2 == 0
                            && buffer[1] == 0 && buffer[0] != 0;
    if (utf16Bom) {
        wide.assign(reinterpret_cast<const wchar_t*>(buffer.data() + 2),
                    (buffer.size() - 2) / sizeof(wchar_t));
    } else if (utf16NoBom) {
        wide.assign(reinterpret_cast<const wchar_t*>(buffer.data()),
                    buffer.size() / sizeof(wchar_t));
    } else {
        const int wideLength = MultiByteToWideChar(CP_UTF8, 0, buffer.data(),
                                                   static_cast<int>(buffer.size()), nullptr, 0);
        if (wideLength == 0) {
            if (onSkipped) {
                onSkipped(path, L"task-parse-failed");
            }
            return {};
        }
        wide.resize(wideLength);
        MultiByteToWideChar(CP_UTF8, 0, buffer.data(), static_cast<int>(buffer.size()), wide.data(), wideLength);
    }

    const std::wstring command = ExtractXmlElementText(wide, L"Command");
    if (command.empty()) {
        return {};
    }
    const std::wstring arguments = ExtractXmlElementText(wide, L"Arguments");
    return arguments.empty() ? command : command + L" " + arguments;
}

// Рекурсивный обход задач (п. SCAN-04): вложенные папки Tasks — полноценный
// источник автозапуска. Глубина ограничена, reparse-точки не разворачиваются.
void WalkTasksFolder(const std::wstring& tasksRoot, const std::wstring& folder, int depth,
                     const std::function<void(const PersistenceEntry&)>& onEntry,
                     const PersistenceScanner::SkipCallback& onSkipped)
{
    if (depth > 8) {
        // Предел глубины — непроверенное поддерево задач (аудит 2, п. 11).
        if (onSkipped) {
            onSkipped(folder, L"max-depth");
        }
        return;
    }

    WIN32_FIND_DATAW findData{};
    HANDLE find = FindFirstFileExW((folder + L"\\*").c_str(), FindExInfoBasic, &findData,
                                   FindExSearchNameMatch, nullptr, 0);
    if (find == INVALID_HANDLE_VALUE) {
        if (onSkipped) {
            onSkipped(folder, L"access-denied");
        }
        return;
    }

    do {
        const std::wstring name = findData.cFileName;
        if (name == L"." || name == L"..") {
            continue;
        }
        // Junction/symlink в дереве задач не разворачиваем: типичный источник
        // зацикливания и обхода path-safety; непроверяемое пропускаем.
        if (findData.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) {
            if (onSkipped) {
                onSkipped(folder + L"\\" + name, L"reparse");
            }
            continue;
        }

        const std::wstring path = folder + L"\\" + name;
        if (findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            WalkTasksFolder(tasksRoot, path, depth + 1, onEntry, onSkipped);
            continue;
        }

        const std::wstring command = ExtractTaskCommandLine(path, onSkipped);
        if (command.empty()) {
            continue;
        }

        PersistenceEntry entry;
        entry.location = L"scheduled-tasks";
        entry.name = path.substr(tasksRoot.size() + 1);
        entry.command = command;
        entry.targetPath = ResolveEntryTarget(command);
        onEntry(entry);
    } while (FindNextFileW(find, &findData));
    FindClose(find);
}

void EnumScheduledTasks(const std::function<void(const PersistenceEntry&)>& onEntry,
                        const PersistenceScanner::SkipCallback& onSkipped)
{
    // Динамический путь (аудит 2, п. 6/2.6): GetSystemDirectoryW сообщает
    // требуемый размер, усечение исключено.
    const UINT needed = GetSystemDirectoryW(nullptr, 0);
    if (needed == 0) {
        return;
    }
    std::wstring systemDir(needed + 1, L'\0');
    GetSystemDirectoryW(systemDir.data(), needed + 1);
    systemDir.resize(wcslen(systemDir.c_str()));

    const std::wstring tasksRoot = systemDir + L"\\Tasks";
    WalkTasksFolder(tasksRoot, tasksRoot, 0, onEntry, onSkipped);
}

} // namespace

void PersistenceScanner::Enumerate(const std::function<void(const PersistenceEntry&)>& onEntry,
                                   const SkipCallback& onSkipped)
{
    for (int hive = 0; hive < 2; ++hive) {
        const HKEY hkey = hive == 0 ? HKEY_CURRENT_USER : HKEY_LOCAL_MACHINE;
        for (const wchar_t* runKey : kRunLocations) {
            EnumRegistryValues(hkey, hive, runKey, onEntry, onSkipped);
        }
        EnumWinlogon(hkey, hive, onEntry, onSkipped);
    }

    const std::wstring profile = GetEnvironmentValueDynamic(L"APPDATA");
    if (!profile.empty()) {
        EnumStartupFolder(profile
                              + L"\\Microsoft\\Windows\\Start Menu\\Programs\\Startup",
                          onEntry);
    }
    const std::wstring commonProfile = GetEnvironmentValueDynamic(L"ProgramData");
    if (!commonProfile.empty()) {
        EnumStartupFolder(commonProfile
                              + L"\\Microsoft\\Windows\\Start Menu\\Programs\\Startup",
                          onEntry);
    }

    EnumServices(onEntry);
    EnumScheduledTasks(onEntry, onSkipped);

    // Тестовый хук (SCU.Tests): дополнительный Run-подобный ключ в HKCU,
    // указывается переменной окружения. В production не задаётся.
    wchar_t testKey[256]{};
    if (GetEnvironmentVariableW(L"SCU_TEST_RUN_KEY", testKey, 256) > 0) {
        EnumRegistryValues(HKEY_CURRENT_USER, 0, testKey, onEntry, onSkipped);
    }
}

} // namespace scan
