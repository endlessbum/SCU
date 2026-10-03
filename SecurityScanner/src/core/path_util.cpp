#include "path_util.h"

namespace scan {

std::wstring GetModulePathDynamic()
{
    std::wstring result;
    DWORD size = MAX_PATH;
    for (;;) {
        result.resize(size);
        const DWORD written = GetModuleFileNameW(nullptr, result.data(), size);
        if (written == 0) {
            return L"";
        }
        // written == size - 1 неоднозначно (уместилось вплотную или обрезано):
        // надёжный признак усечения — повторный вызов с удвоенным буфером даёт
        // больший путь; лимит 32768 — практический потолок путей модулей.
        if (written < size - 1) {
            result.resize(written);
            return result;
        }
        if (size >= 32768) {
            return L"";
        }
        size *= 2;
    }
}

std::wstring GetProcessImagePathDynamic(HANDLE process)
{
    std::wstring result;
    DWORD size = 1024;
    for (;;) {
        result.resize(size);
        DWORD needed = size;
        if (QueryFullProcessImageNameW(process, 0, result.data(), &needed)) {
            result.resize(needed);
            return result;
        }
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || size >= 32768) {
            return L"";
        }
        // QueryFullProcessImageNameW сообщает требуемый размер.
        size = needed > size ? needed : size * 2;
    }
}

std::wstring ExpandEnvironmentDynamic(const std::wstring& value)
{
    std::wstring result;
    DWORD size = 512;
    for (;;) {
        result.resize(size);
        const DWORD written = ExpandEnvironmentStringsW(value.c_str(), result.data(), size);
        if (written == 0) {
            return L"";
        }
        if (written <= size) {
            result.resize(written - 1); // без завершающего нуля
            return result;
        }
        // Документированное поведение: превышение размера возвращает нужный размер.
        size = written;
    }
}

std::wstring GetEnvironmentValueDynamic(const wchar_t* name)
{
    std::wstring result;
    DWORD size = 256;
    for (;;) {
        result.resize(size);
        const DWORD written = GetEnvironmentVariableW(name, result.data(), size);
        if (written == 0) {
            return L""; // переменной нет или ошибка
        }
        if (written <= size) {
            result.resize(written);
            return result;
        }
        // Документированное поведение: превышение возвращает требуемый размер.
        size = written;
    }
}

std::wstring GetTempPathDynamic()
{
    std::wstring result;
    DWORD size = MAX_PATH;
    for (;;) {
        result.resize(size + 1);
        const DWORD written = GetTempPathW(size, result.data());
        if (written == 0) {
            return L"";
        }
        if (written < size) {
            result.resize(written);
            return result;
        }
        if (size >= 32768) {
            return L"";
        }
        size *= 2;
    }
}

std::wstring DirectoryOf(const std::wstring& path)
{
    const size_t slash = path.find_last_of(L'\\');
    if (slash == std::wstring::npos) {
        return path;
    }
    return path.substr(0, slash);
}

} // namespace scan
