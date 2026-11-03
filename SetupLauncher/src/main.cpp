// SCU_Install — двухфазный загрузчик установщика.
//
// Формат обёртки (build-release.ps1, шаг 6a):
//   [PE-загрузчик][overlay: payload][трейлер 8 байт LE]
// После подписи Authenticode (аудит, п. 19) в конец файла добавляется таблица
// сертификатов, и трейлер оказывается перед ней — загрузчик ищет его по
// каталогу данных PE, а не «последние 8 байт».
//
// Загрузчик читает собственный файл, извлекает размер payload из трейлера
// (int64 LE), сохраняет payload во временный файл и запускает его.
// После завершения payload временный файл удаляется.
//
// APP_SEMVER передаётся через CMake (-DAPP_SEMVER=...) и используется
// только для диагностики в заголовке — сам payload содержит полную версию.

#include <windows.h>
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#ifndef APP_SEMVER
#define APP_SEMVER "0.0.0"
#endif

namespace {

std::wstring GetOwnPath()
{
    wchar_t path[MAX_PATH];
    DWORD len = GetModuleFileNameW(nullptr, path, MAX_PATH);
    if (len == 0 || len == MAX_PATH)
    {
        return L"";
    }
    return std::wstring(path, len);
}

std::wstring GetTempDirectory()
{
    wchar_t path[MAX_PATH];
    DWORD len = GetTempPathW(MAX_PATH, path);
    if (len == 0 || len == MAX_PATH)
    {
        return L"";
    }
    return std::wstring(path, len);
}

std::wstring MakeTempFileName(const std::wstring& dir)
{
    wchar_t name[MAX_PATH];
    UINT unique = GetTempFileNameW(dir.c_str(), L"SCU", 0, name);
    if (unique == 0)
    {
        return L"";
    }
    return std::wstring(name, wcslen(name));
}

std::vector<uint8_t> ReadFileBytes(HANDLE file, uint64_t offset, uint64_t size)
{
    std::vector<uint8_t> buffer(static_cast<size_t>(size));
    if (size == 0)
    {
        return buffer;
    }

    LARGE_INTEGER liOffset{};
    liOffset.QuadPart = static_cast<LONGLONG>(offset);
    if (!SetFilePointerEx(file, liOffset, nullptr, FILE_BEGIN))
    {
        return {};
    }

    DWORD bytesRead = 0;
    DWORD totalRead = 0;
    while (totalRead < size)
    {
        DWORD chunk = static_cast<DWORD>(std::min<uint64_t>(size - totalRead, 16 * 1024 * 1024));
        if (!ReadFile(file, buffer.data() + totalRead, chunk, &bytesRead, nullptr))
        {
            return {};
        }
        if (bytesRead == 0)
        {
            return {};
        }
        totalRead += bytesRead;
    }

    return buffer;
}

bool WriteFileBytes(HANDLE file, const std::vector<uint8_t>& data)
{
    DWORD written = 0;
    DWORD totalWritten = 0;
    while (totalWritten < data.size())
    {
        DWORD chunk = static_cast<DWORD>(std::min<size_t>(data.size() - totalWritten, 16 * 1024 * 1024));
        if (!WriteFile(file, data.data() + totalWritten, chunk, &written, nullptr))
        {
            return false;
        }
        totalWritten += written;
    }
    return true;
}

// Смещение трейлера (8 байт с размером payload) в собственном файле.
//
// Обычно это последние 8 байт. Но после подписи Authenticode в конец файла
// добавляется таблица сертификатов, и «последние 8 байт» оказываются внутри
// неё (аудит, п. 19: финальная обёртка SCU_Install_*.exe подписывается).
// Поэтому трейлер ищем перед таблицей сертификатов — каталог данных PE №4.
uint64_t FindTrailerOffset(HANDLE file, uint64_t fileSize)
{
    const uint64_t fallback = fileSize >= 8 ? fileSize - 8 : 0;

    std::vector<uint8_t> dos = ReadFileBytes(file, 0, 64);
    if (dos.size() < 64)
    {
        return fallback;
    }

    uint32_t peOffset = 0;
    memcpy(&peOffset, dos.data() + 0x3C, sizeof(peOffset));
    // 256 байт оптического заголовка хватает для каталога данных PE32+:
    // каталоги начинаются со смещения 112, а нужная запись (№4 — таблица
    // сертификатов) лежит на 112 + 32.
    const uint64_t optionalSize = 256;
    if (peOffset == 0 || static_cast<uint64_t>(peOffset) + 24 + optionalSize > fileSize)
    {
        return fallback;
    }

    std::vector<uint8_t> optional = ReadFileBytes(file, peOffset + 24, optionalSize);
    if (optional.size() < optionalSize)
    {
        return fallback;
    }

    uint16_t magic = 0;
    memcpy(&magic, optional.data(), sizeof(magic));

    size_t dataDirectoryOffset = 0;
    if (magic == 0x10B)      // PE32
    {
        dataDirectoryOffset = 96;
    }
    else if (magic == 0x20B) // PE32+
    {
        dataDirectoryOffset = 112;
    }
    else
    {
        return fallback;
    }

    const size_t certificateEntry = dataDirectoryOffset + 4 * sizeof(uint32_t) * 2;
    if (certificateEntry + 8 > optional.size())
    {
        return fallback;
    }

    uint32_t certificateOffset = 0;
    uint32_t certificateSize = 0;
    memcpy(&certificateOffset, optional.data() + certificateEntry, sizeof(certificateOffset));
    memcpy(&certificateSize, optional.data() + certificateEntry + 4, sizeof(certificateSize));

    if (certificateOffset == 0 || certificateSize == 0)
    {
        return fallback;
    }

    if (certificateOffset < 8 || static_cast<uint64_t>(certificateOffset) > fileSize)
    {
        return fallback;
    }

    return static_cast<uint64_t>(certificateOffset) - 8;
}

// Экранирование одного аргумента по правилам CommandLineToArgvW (SCU-04).
// BuildCommandLine собирает новую командную строку из уже разобранного argv:
// без обратного экранирования аргумент с пробелами («/DIR=C:\Program Files\SCU»),
// кавычками или завершающими backslash'ами при повторном разборе терял бы
// границы. Алгоритм — канонические правила обратного квотирования
// (удвоение backslash'ов перед кавычкой, кавычка как \", удвоение
// завершающих backslash'ов перед закрывающей кавычкой).
std::wstring QuoteWindowsArg(const std::wstring& argument)
{
    if (!argument.empty()
        && argument.find_first_of(L" \t\n\v\"") == std::wstring::npos)
    {
        return argument; // спецсимволов нет — кавычки не нужны
    }

    std::wstring quoted;
    quoted.reserve(argument.size() + 2);
    quoted.push_back(L'"');
    const wchar_t* it = argument.c_str();
    while (true)
    {
        unsigned long long backslashes = 0;
        while (*it == L'\\')
        {
            ++it;
            ++backslashes;
        }
        if (*it == L'\0')
        {
            // Завершающие backslash'ы удваиваются перед закрывающей кавычкой.
            quoted.append(backslashes * 2, L'\\');
            break;
        }
        if (*it == L'"')
        {
            // Backslash'ы перед кавычкой удваиваются, кавычка экранируется.
            quoted.append(backslashes * 2 + 1, L'\\');
            quoted.push_back(*it);
        }
        else
        {
            // Вне кавычек backslash'ы особого смысла не имеют.
            quoted.append(backslashes, L'\\');
            quoted.push_back(*it);
        }
        ++it;
    }
    quoted.push_back(L'"');
    return quoted;
}

std::wstring BuildCommandLine(const std::wstring& payloadPath)
{
    std::wstring cmd = L"\"";
    cmd += payloadPath;
    cmd += L"\"";

    // Передаём оригинальные аргументы загрузчика (после имени программы),
    // восстанавливая их границы для повторного разбора (SCU-04).
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv != nullptr)
    {
        for (int i = 1; i < argc; ++i)
        {
            cmd += L" ";
            cmd += QuoteWindowsArg(argv[i]);
        }
        LocalFree(argv);
    }

    return cmd;
}

int ShowError(const wchar_t* message)
{
    MessageBoxW(nullptr, message, L"SCU Install", MB_OK | MB_ICONERROR);
    return 1;
}

} // namespace

int main()
{
    // 1. Путь к самому себе
    const std::wstring selfPath = GetOwnPath();
    if (selfPath.empty())
    {
        return ShowError(L"Не удалось определить путь к загрузчику.");
    }

    // 2. Открываем себя для чтения
    HANDLE selfFile = CreateFileW(
        selfPath.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);

    if (selfFile == INVALID_HANDLE_VALUE)
    {
        return ShowError(L"Не удалось открыть файл загрузчика.");
    }

    // 3. Размер файла
    LARGE_INTEGER fileSize{};
    if (!GetFileSizeEx(selfFile, &fileSize))
    {
        CloseHandle(selfFile);
        return ShowError(L"Не удалось получить размер файла загрузчика.");
    }

    // 4. Трейлер: 8 байт с размером payload (int64 LE) — перед таблицей
    //    сертификатов, если файл подписан, иначе в самом конце.
    if (fileSize.QuadPart < 8)
    {
        CloseHandle(selfFile);
        return ShowError(L"Файл загрузчика слишком мал.");
    }

    const uint64_t trailerOffset = FindTrailerOffset(selfFile, static_cast<uint64_t>(fileSize.QuadPart));
    std::vector<uint8_t> trailer = ReadFileBytes(selfFile, trailerOffset, 8);
    if (trailer.size() != 8)
    {
        CloseHandle(selfFile);
        return ShowError(L"Не удалось прочитать трейлер загрузчика.");
    }

    const int64_t payloadSize = *reinterpret_cast<const int64_t*>(trailer.data());
    if (payloadSize <= 0 || static_cast<uint64_t>(payloadSize) > trailerOffset)
    {
        CloseHandle(selfFile);
        return ShowError(L"Некорректный размер payload в трейлере.");
    }

    // 5. Извлекаем payload
    const uint64_t payloadOffset = trailerOffset - static_cast<uint64_t>(payloadSize);
    std::vector<uint8_t> payload = ReadFileBytes(selfFile, payloadOffset, static_cast<uint64_t>(payloadSize));
    if (payload.size() != static_cast<size_t>(payloadSize))
    {
        CloseHandle(selfFile);
        return ShowError(L"Не удалось прочитать payload из загрузчика.");
    }

    CloseHandle(selfFile);

    // 6. Сохраняем payload во временный файл
    const std::wstring tempDir = GetTempDirectory();
    if (tempDir.empty())
    {
        return ShowError(L"Не удалось определить временный каталог.");
    }

    const std::wstring tempFile = MakeTempFileName(tempDir);
    if (tempFile.empty())
    {
        return ShowError(L"Не удалось создать временный файл.");
    }

    // Запись payload: FILE_SHARE_READ, а не 0 — CreateProcessW сам открывает
    // образ на чтение, а для всех прочих процессов доступ на запись/удаление/
    // переименование остаётся закрыт, пока наш хэндл не закрыт (аудит
    // SetupLauncher: окно подмены между записью и запуском).
    HANDLE tempHandle = CreateFileW(
        tempFile.c_str(),
        GENERIC_WRITE,
        FILE_SHARE_READ,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);

    if (tempHandle == INVALID_HANDLE_VALUE)
    {
        return ShowError(L"Не удалось открыть временный файл для записи.");
    }

    if (!WriteFileBytes(tempHandle, payload))
    {
        CloseHandle(tempHandle);
        DeleteFileW(tempFile.c_str());
        return ShowError(L"Не удалось записать payload во временный файл.");
    }
    FlushFileBuffers(tempHandle);

    // 7. Запускаем payload
    const std::wstring cmdLine = BuildCommandLine(tempFile);

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};

    std::vector<wchar_t> cmdBuffer(cmdLine.begin(), cmdLine.end());
    cmdBuffer.push_back(L'\0');

    BOOL started = CreateProcessW(
        nullptr,
        cmdBuffer.data(),
        nullptr,
        nullptr,
        FALSE,
        0,
        nullptr,
        tempDir.c_str(),
        &si,
        &pi);

    // Хэндл закрываем только после CreateProcessW: с этого момента файл
    // защищён секцией образа запущенного процесса (запись/удаление в неё
    // запрещены), окно подмены закрыто полностью.
    CloseHandle(tempHandle);

    if (!started)
    {
        DeleteFileW(tempFile.c_str());
        return ShowError(L"Не удалось запустить установщик.");
    }

    // 8. Ждём завершения — ограниченно (аудит SetupLauncher): зависший
    // установщик больше не держит загрузчик вечность; temp-файл убирается
    // на всех путях выхода.
    constexpr DWORD kInstallWaitMs = 30 * 60 * 1000;
    const DWORD waitResult = WaitForSingleObject(pi.hProcess, kInstallWaitMs);
    if (waitResult != WAIT_OBJECT_0)
    {
        if (waitResult == WAIT_TIMEOUT)
        {
            TerminateProcess(pi.hProcess, static_cast<UINT>(-1));
            WaitForSingleObject(pi.hProcess, 15000);
        }
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        DeleteFileW(tempFile.c_str());
        return ShowError(waitResult == WAIT_TIMEOUT
                             ? L"Установщик не завершился за отведённое время."
                             : L"Не удалось дождаться завершения установщика.");
    }

    DWORD exitCode = 0;
    if (!GetExitCodeProcess(pi.hProcess, &exitCode))
    {
        // SCU-04: без проверки exitCode оставался 0 — оболочка возвращала ложный
        // успех при сбое чтения кода завершения.
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        DeleteFileW(tempFile.c_str());
        return ShowError(L"Не удалось получить код завершения установщика.");
    }

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    // 9. Удаляем временный файл
    DeleteFileW(tempFile.c_str());

    return static_cast<int>(exitCode);
}
