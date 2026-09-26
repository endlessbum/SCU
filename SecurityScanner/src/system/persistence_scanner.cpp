#include "system/persistence_scanner.h"

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

// Разрешение исполняемого файла из командной строки: первый токен (в кавычках
// или до пробела); префиксы \??\ и " " отбрасываются.
std::wstring ResolveTarget(const std::wstring& command)
{
    std::wstring value = Trim(command);
    if (value.rfind(L"\\??\\", 0) == 0) {
        value = value.substr(4);
    }
    if (value.rfind(L"\" ", 0) == 0) {
        value = Trim(value.substr(2));
    }

    std::wstring token;
    if (!value.empty() && value[0] == L'"') {
        const size_t closing = value.find(L'"', 1);
        token = value.substr(1, closing == std::wstring::npos ? std::wstring::npos : closing - 1);
    } else {
        const size_t space = value.find(L' ');
        token = value.substr(0, space == std::wstring::npos ? std::wstring::npos : space);
    }
    return token;
}

bool FileExists(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}

void ExpandEnvironment(std::wstring& value)
{
    wchar_t expanded[MAX_PATH * 2]{};
    const DWORD size = ExpandEnvironmentStringsW(value.c_str(), expanded, MAX_PATH * 2);
    if (size > 0 && size <= MAX_PATH * 2) {
        value = expanded;
    }
}

// ---------- Registry ----------

void EnumRegistryValues(HKEY hive, int hiveIndex, const wchar_t* subKey,
                        const std::function<void(const PersistenceEntry&)>& onEntry)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(hive, subKey, 0, KEY_READ, &key) != ERROR_SUCCESS) {
        return; // нет ключа/нет доступа — не ошибка сканирования
    }

    for (DWORD index = 0;; ++index) {
        wchar_t valueName[16384]{};
        DWORD nameLength = 16384;
        DWORD type = 0;
        BYTE data[4096]{};
        DWORD dataSize = sizeof(data);
        const LSTATUS status = RegEnumValueW(key, index, valueName, &nameLength,
                                             nullptr, &type, data, &dataSize);
        if (status != ERROR_SUCCESS) {
            break;
        }
        if (type != REG_SZ && type != REG_EXPAND_SZ) {
            continue;
        }

        PersistenceEntry entry;
        entry.location = std::wstring(kHiveNames[hiveIndex]) + L"\\" + subKey;
        entry.name = valueName;
        entry.command = (reinterpret_cast<const wchar_t*>(data))[dataSize / sizeof(wchar_t) - 1] == 0
            ? std::wstring(reinterpret_cast<const wchar_t*>(data))
            : std::wstring(reinterpret_cast<const wchar_t*>(data), dataSize / sizeof(wchar_t));
        entry.targetPath = ResolveTarget(entry.command);
        ExpandEnvironment(entry.targetPath);
        onEntry(entry);
    }

    RegCloseKey(key);
}

// Winlogon: Shell и Userinit — списки через запятую.
void EnumWinlogon(HKEY hive, int hiveIndex, const std::function<void(const PersistenceEntry&)>& onEntry)
{
    const wchar_t* subKey = L"Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon";
    HKEY key = nullptr;
    if (RegOpenKeyExW(hive, subKey, 0, KEY_READ, &key) != ERROR_SUCCESS) {
        return;
    }

    for (const wchar_t* valueName : { L"Shell", L"Userinit" }) {
        wchar_t data[4096]{};
        DWORD dataSize = sizeof(data);
        DWORD type = 0;
        if (RegQueryValueExW(key, valueName, nullptr, &type,
                             reinterpret_cast<BYTE*>(data), &dataSize) != ERROR_SUCCESS
            || (type != REG_SZ && type != REG_EXPAND_SZ)) {
            continue;
        }

        std::wstring value(reinterpret_cast<const wchar_t*>(data));
        size_t start = 0;
        while (start <= value.size()) {
            const size_t comma = value.find(L',', start);
            const std::wstring part = Trim(value.substr(
                start, comma == std::wstring::npos ? std::wstring::npos : comma - start));
            if (!part.empty()) {
                PersistenceEntry entry;
                entry.location = std::wstring(kHiveNames[hiveIndex]) + L"\\" + subKey;
                entry.name = valueName;
                entry.command = part;
                entry.targetPath = ResolveTarget(part);
                ExpandEnvironment(entry.targetPath);
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

        wchar_t imagePath[2048]{};
        DWORD dataSize = sizeof(imagePath);
        DWORD type = 0;
        if (RegQueryValueExW(serviceKey, L"ImagePath", nullptr, &type,
                             reinterpret_cast<BYTE*>(imagePath), &dataSize) == ERROR_SUCCESS
            && (type == REG_SZ || type == REG_EXPAND_SZ)) {
            PersistenceEntry entry;
            entry.location = L"HKLM\\" + std::wstring(servicesKey);
            entry.name = serviceName;
            entry.command = imagePath;
            entry.targetPath = ResolveTarget(imagePath);
            ExpandEnvironment(entry.targetPath);
            onEntry(entry);
        }

        RegCloseKey(serviceKey);
    }

    RegCloseKey(key);
}

// ---------- Scheduled Tasks (файлы XML) ----------

// Извлечение <Command>...</Command> из XML задачи (файлы в UTF-16 или UTF-8).
std::wstring ExtractTaskCommand(const std::wstring& path)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return {};
    }

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > 1024 * 1024) {
        CloseHandle(file);
        return {};
    }

    std::vector<char> buffer(static_cast<size_t>(size.QuadPart));
    DWORD read = 0;
    const bool ok = ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)
                    && read == buffer.size();
    CloseHandle(file);
    if (!ok) {
        return {};
    }

    // UTF-16 LE с BOM или без → wide; иначе — ANSI/UTF-8 байты в wide.
    std::wstring wide;
    if (buffer.size() >= 2 && static_cast<unsigned char>(buffer[0]) == 0xFF
        && static_cast<unsigned char>(buffer[1]) == 0xFE) {
        wide.assign(reinterpret_cast<const wchar_t*>(buffer.data() + 2),
                    (buffer.size() - 2) / sizeof(wchar_t));
    } else {
        const int wideLength = MultiByteToWideChar(CP_UTF8, 0, buffer.data(),
                                                   static_cast<int>(buffer.size()), nullptr, 0);
        if (wideLength == 0) {
            return {};
        }
        wide.resize(wideLength);
        MultiByteToWideChar(CP_UTF8, 0, buffer.data(), static_cast<int>(buffer.size()), wide.data(), wideLength);
    }

    std::wstring lower = wide;
    std::transform(lower.begin(), lower.end(), lower.begin(), ::towlower);
    const size_t openTag = lower.find(L"<command>");
    if (openTag == std::wstring::npos) {
        return {};
    }
    const size_t closeTag = lower.find(L"</command>", openTag);
    if (closeTag == std::wstring::npos) {
        return {};
    }
    return Trim(wide.substr(openTag + 9, closeTag - openTag - 9));
}

void EnumScheduledTasks(const std::function<void(const PersistenceEntry&)>& onEntry)
{
    wchar_t systemDir[MAX_PATH]{};
    if (GetSystemDirectoryW(systemDir, MAX_PATH) == 0) {
        return;
    }
    const std::wstring tasksRoot = std::wstring(systemDir) + L"\\Tasks";

    // Плоский обход верхних уровней: вложенные папки задач пропускаем в срезе
    // (FindFirstFileEx без рекурсии), главного источника автозапуска это покрывает.
    WIN32_FIND_DATAW findData{};
    HANDLE find = FindFirstFileExW((tasksRoot + L"\\*").c_str(), FindExInfoBasic, &findData,
                                   FindExSearchNameMatch, nullptr, 0);
    if (find == INVALID_HANDLE_VALUE) {
        return;
    }

    do {
        const std::wstring name = findData.cFileName;
        if (name == L"." || name == L"..") {
            continue;
        }
        if (findData.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) {
            continue;
        }

        const std::wstring command = ExtractTaskCommand(tasksRoot + L"\\" + name);
        if (command.empty()) {
            continue;
        }

        PersistenceEntry entry;
        entry.location = L"scheduled-tasks";
        entry.name = name;
        entry.command = command;
        entry.targetPath = ResolveTarget(command);
        ExpandEnvironment(entry.targetPath);
        onEntry(entry);
    } while (FindNextFileW(find, &findData));
    FindClose(find);
}

} // namespace

void PersistenceScanner::Enumerate(const std::function<void(const PersistenceEntry&)>& onEntry)
{
    for (int hive = 0; hive < 2; ++hive) {
        const HKEY hkey = hive == 0 ? HKEY_CURRENT_USER : HKEY_LOCAL_MACHINE;
        for (const wchar_t* runKey : kRunLocations) {
            EnumRegistryValues(hkey, hive, runKey, onEntry);
        }
        EnumWinlogon(hkey, hive, onEntry);
    }

    wchar_t profile[MAX_PATH]{};
    if (GetEnvironmentVariableW(L"APPDATA", profile, MAX_PATH) > 0) {
        EnumStartupFolder(std::wstring(profile)
                              + L"\\Microsoft\\Windows\\Start Menu\\Programs\\Startup",
                          onEntry);
    }
    wchar_t commonProfile[MAX_PATH]{};
    if (GetEnvironmentVariableW(L"ProgramData", commonProfile, MAX_PATH) > 0) {
        EnumStartupFolder(std::wstring(commonProfile)
                              + L"\\Microsoft\\Windows\\Start Menu\\Programs\\Startup",
                          onEntry);
    }

    EnumServices(onEntry);
    EnumScheduledTasks(onEntry);

    // Тестовый хук (SCU.Tests): дополнительный Run-подобный ключ в HKCU,
    // указывается переменной окружения. В production не задаётся.
    wchar_t testKey[256]{};
    if (GetEnvironmentVariableW(L"SCU_TEST_RUN_KEY", testKey, 256) > 0) {
        EnumRegistryValues(HKEY_CURRENT_USER, 0, testKey, onEntry);
    }
}

} // namespace scan
