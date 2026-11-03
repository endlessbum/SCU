#include "core/file_type.h"

#include <windows.h>

#include <array>
#include <cctype>

namespace scan {

namespace {

bool HasExtension(const std::wstring& path, std::initializer_list<const wchar_t*> extensions)
{
    const size_t dot = path.find_last_of(L'.');
    if (dot == std::wstring::npos) {
        return false;
    }
    std::wstring lower = path.substr(dot);
    for (wchar_t& c : lower) {
        c = static_cast<wchar_t>(towlower(c));
    }
    for (const wchar_t* extension : extensions) {
        if (lower == extension) {
            return true;
        }
    }
    return false;
}

} // namespace

FileType ClassifyFile(const std::wstring& path, HANDLE predefinedFile)
{
    // Предопределённый хэндл (аудит TOCTOU) принадлежит вызывающему:
    // закрывается только собственный хэндл.
    const bool ownsFile = predefinedFile == INVALID_HANDLE_VALUE;
    HANDLE file = ownsFile
        ? CreateFileW(path.c_str(), GENERIC_READ,
                      FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                      nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr)
        : predefinedFile;
    if (file == INVALID_HANDLE_VALUE) {
        return FileType::Unknown;
    }

    // Magic читается с начала: предопределённый хэндл мог быть уже прочитан
    // (хэш) — позиция файла не гарантируется.
    LARGE_INTEGER begin{};
    SetFilePointerEx(file, begin, nullptr, FILE_BEGIN);

    std::array<unsigned char, 8> magic{};
    DWORD read = 0;
    const BOOL readOk = ReadFile(file, magic.data(), static_cast<DWORD>(magic.size()), &read, nullptr);
    if (ownsFile) {
        CloseHandle(file);
    }
    if (!readOk || read < 2) {
        return FileType::Unknown;
    }

    if (magic[0] == 'M' && magic[1] == 'Z') {
        // Реальный PE-контент важнее расширения: MZ-файл, как бы он ни
        // назывался, исполняется как PE.
        return FileType::PE;
    }

    // Скрипты определяются по расширению (документ п. 19: собственный парсер
    // скриптов — в следующей итерации, здесь только классификация).
    const bool scriptExtension = HasExtension(
        path, {L".ps1", L".bat", L".cmd", L".vbs", L".js", L".jse", L".vbe", L".hta"});

    if (read >= 4 && magic[0] == 'P' && magic[1] == 'K' && magic[2] == 3 && magic[3] == 4) {
        // Аудит: PK-магия возвращалась раньше проверки расширения — .bat/.ps1
        // с PK-заголовком уходили в ArchiveZip, скрипт-эвристика не
        // запускалась; .hta с PK-магией и шифрованным архивом вообще не
        // анализировалась. Полиглот «ZIP+скрипт» исполняется интерпретатором
        // как текст — исполняемый вектор анализируем как скрипт. Осознанный
        // trade-off: члены такого файла как архива не извлекаются, зато
        // исполняемый вектор под эвристиками.
        if (scriptExtension) {
            return FileType::Script;
        }
        return FileType::ArchiveZip;
    }

    if (scriptExtension) {
        return FileType::Script;
    }

    return FileType::Other;
}

} // namespace scan
