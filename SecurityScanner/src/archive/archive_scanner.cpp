#include "archive/archive_scanner.h"

#include "core/path_util.h"

#include <windows.h>

#include "miniz.h"

#include <cstdio>
#include <cstring>

namespace scan {

namespace {

std::wstring TempRoot()
{
    const std::wstring temp = GetTempPathDynamic();
    return temp.empty() ? std::wstring(L".") : temp;
}

std::wstring ToWideName(const char* name, mz_uint bitFlag)
{
    // ZIP: имена в UTF-8 при установленном бите 11 общего флага, иначе —
    // кодировка изготовителя (обычно CP437/ANSI). Строгая проверка UTF-8,
    // при неудаче — системная ANSI-страница. Fallback последовательно меняет
    // code page во ВСЕХ вызовах (аудит 2, п. 14): раньше длина считалась по
    // CP_ACP, а конвертация выполнялась исходной CP_UTF8 — имя терялось.
    UINT codePage = (bitFlag & 0x800) != 0 ? CP_UTF8 : CP_ACP;
    const int sourceLength = static_cast<int>(strlen(name));
    int wideLength = MultiByteToWideChar(codePage, MB_ERR_INVALID_CHARS,
                                         name, sourceLength, nullptr, 0);
    if (wideLength == 0) {
        codePage = CP_ACP;
        wideLength = MultiByteToWideChar(codePage, 0, name, sourceLength, nullptr, 0);
        if (wideLength == 0) {
            return L"member";
        }
    }
    std::wstring wide(static_cast<size_t>(wideLength), L'\0');
    MultiByteToWideChar(codePage, (codePage == CP_UTF8 ? MB_ERR_INVALID_CHARS : 0),
                        name, sourceLength, wide.data(), wideLength);
    return wide;
}

struct WriteContext {
    HANDLE file;
    const std::atomic<bool>* isCancelled;
    bool failed = false;
};

size_t WriteChunk(void* opaque, mz_uint64 /*fileOfs*/, const void* buffer, size_t size)
{
    auto* context = static_cast<WriteContext*>(opaque);
    if (context->isCancelled->load(std::memory_order_relaxed)) {
        context->failed = true;
        return 0; // отмена: прерывает извлечение
    }
    DWORD written = 0;
    if (!WriteFile(context->file, buffer, static_cast<DWORD>(size), &written, nullptr)
        || written != size) {
        context->failed = true;
        return 0;
    }
    return size;
}

} // namespace

ArchiveScanner::ArchiveScanner(const ArchiveLimits& limits,
                               const std::atomic<bool>& isCancelled,
                               MemberCallback onMember,
                               IssueCallback onIssue)
    : limits_(limits)
    , isCancelled_(isCancelled)
    , onMember_(std::move(onMember))
    , onIssue_(std::move(onIssue))
{
}

bool ArchiveScanner::ScanZip(const std::wstring& path, const std::wstring& virtualPath, int depth)
{
    if (isCancelled_.load(std::memory_order_relaxed)) {
        return false;
    }

    if (depth > static_cast<int>(limits_.maxNesting)) {
        onIssue_({virtualPath, L"too-deep", virtualPath});
        return true;
    }

    LARGE_INTEGER fileSize{};
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        onIssue_({virtualPath, L"corrupt-archive", path});
        return true;
    }
    if (!GetFileSizeEx(file, &fileSize)) {
        CloseHandle(file);
        onIssue_({virtualPath, L"corrupt-archive", path});
        return true;
    }
    if (static_cast<unsigned long long>(fileSize.QuadPart) > limits_.maxArchiveSize) {
        CloseHandle(file);
        onIssue_({virtualPath, L"too-large", path});
        return true;
    }

    // Архив читается в память: miniz открывает файлы через ANSI-пути, а нам
    // нужны произвольные Unicode-пути (кириллица). Лимит maxArchiveSize
    // ограничивает потребление памяти.
    std::vector<unsigned char> buffer(static_cast<size_t>(fileSize.QuadPart));
    DWORD read = 0;
    const bool readOk = ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)
                        && read == buffer.size();
    CloseHandle(file);
    if (!readOk) {
        onIssue_({virtualPath, L"corrupt-archive", path});
        return true;
    }

    mz_zip_archive zip{};
    if (!mz_zip_reader_init_mem(&zip, buffer.data(), buffer.size(), 0)) {
        onIssue_({virtualPath, L"corrupt-archive", path});
        return true;
    }

    const mz_uint entryCount = mz_zip_reader_get_num_files(&zip);
    if (entryCount > limits_.maxEntries) {
        mz_zip_reader_end(&zip);
        onIssue_({virtualPath, L"too-many-entries", path});
        return true;
    }

    bool encrypted = false;
    bool cancelled = false;
    bool limitExceeded = false;

    for (mz_uint index = 0; index < entryCount; ++index) {
        if (isCancelled_.load(std::memory_order_relaxed)) {
            cancelled = true;
            break;
        }

        mz_zip_archive_file_stat stat{};
        if (!mz_zip_reader_file_stat(&zip, index, &stat)) {
            // Метаданные члена не прочитаны — член не анализируется вовсе
            // (аудит 2, п. 13): обязательный issue, иначе coverage теряется.
            onIssue_({virtualPath, L"member-stat-failed", L"", true});
            continue;
        }

        // Зашифрованный архив — явный отказ для всего архива (документ п. 20).
        if ((stat.m_bit_flag & 0x01) != 0) {
            encrypted = true;
            break;
        }

        if (!stat.m_is_supported) {
            onIssue_({virtualPath, L"unsupported-member", ToWideName(stat.m_filename, stat.m_bit_flag)});
            continue;
        }

        if (stat.m_uncomp_size > limits_.maxMemberSize) {
            onIssue_({virtualPath, L"member-too-large", ToWideName(stat.m_filename, stat.m_bit_flag)});
            continue;
        }

        // Zip-bomb guard: аномальный коэффициент сжатия на крупном члене.
        if (stat.m_comp_size > 0
            && stat.m_uncomp_size > 10ull * 1024 * 1024
            && stat.m_uncomp_size / stat.m_comp_size > limits_.maxCompressionRatio) {
            onIssue_({virtualPath, L"zip-bomb", ToWideName(stat.m_filename, stat.m_bit_flag)});
            continue;
        }

        if (totalExpanded_ + stat.m_uncomp_size > limits_.maxExpandedSize) {
            onIssue_({virtualPath, L"expanded-limit", path});
            limitExceeded = true;
            break;
        }

        if (stat.m_uncomp_size > 0) {
            const bool memberOk = ScanMember(static_cast<int>(index),
                                             ToWideName(stat.m_filename, stat.m_bit_flag),
                                             stat.m_comp_size, stat.m_uncomp_size,
                                             true, virtualPath, depth, &zip);
            if (!memberOk) {
                cancelled = isCancelled_.load(std::memory_order_relaxed);
                limitExceeded = !cancelled;
                break;
            }
        }
    }

    if (encrypted) {
        onIssue_({virtualPath, L"encrypted-archive", path});
    }

    mz_zip_reader_end(&zip);
    return !cancelled && !limitExceeded;
}

bool ArchiveScanner::ScanMember(int index,
                                const std::wstring& displayName,
                                unsigned long long /*compSize*/,
                                unsigned long long uncompSize,
                                bool /*isSupported*/,
                                const std::wstring& virtualPath,
                                int depth,
                                void* zipHandle)
{
    // Temp-каталог — один на экземпляр сканера (создаётся при первом члене);
    // члены извлекаются по индексам (без имён из архива — защита от zip slip).
    // Имя каталога уникально для экземпляра: параллельные воркеры сканируют
    // свои архивы одновременно (п. 26), pid+tick могут совпасть.
    if (tempDir_.empty()) {
        static std::atomic<unsigned long> instanceCounter{0};
        wchar_t id[64];
        swprintf_s(id, L"ScannerCore\\%lu_%lu_%lu", GetCurrentProcessId(),
                   GetTickCount(), instanceCounter.fetch_add(1));
        tempDir_ = TempRoot() + id;
        CreateDirectoryW((TempRoot() + L"ScannerCore").c_str(), nullptr);
        CreateDirectoryW(tempDir_.c_str(), nullptr);
    }

    wchar_t name[32];
    swprintf_s(name, L"m%06lu", ++memberCounter_);
    const std::wstring tempPath = tempDir_ + L"\\" + name;

    HANDLE outFile = CreateFileW(tempPath.c_str(), GENERIC_WRITE, 0,
                                 nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY,
                                 nullptr);
    if (outFile == INVALID_HANDLE_VALUE) {
        onIssue_({virtualPath, L"corrupt-archive", tempPath, true});
        return true;
    }

    WriteContext context{outFile, &isCancelled_, false};
    const mz_bool extracted = mz_zip_reader_extract_to_callback(
        static_cast<mz_zip_archive*>(zipHandle), static_cast<mz_uint>(index), WriteChunk, &context, 0);
    CloseHandle(outFile);

    if (!extracted) {
        DeleteFileW(tempPath.c_str());
        // Ошибка записи на диск — техническая ошибка, не свойство архива.
        onIssue_({virtualPath, L"unsupported-member", displayName, context.failed});
        return !context.failed; // ошибка записи — прерываем, отмена/лимит не было
    }

    totalExpanded_ += uncompSize;

    const std::wstring memberVirtual = virtualPath + L"\\" + displayName;
    const bool proceed = onMember_(memberVirtual, tempPath, uncompSize);

    // Вложенный ZIP: тот же лимит nesting; временный файл ещё жив — рекурсия.
    if (proceed && !isCancelled_.load(std::memory_order_relaxed) && uncompSize >= 4) {
        char magic[4] = {};
        HANDLE nested = CreateFileW(tempPath.c_str(), GENERIC_READ,
                                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                    nullptr, OPEN_EXISTING, 0, nullptr);
        DWORD read = 0;
        if (nested != INVALID_HANDLE_VALUE) {
            ReadFile(nested, magic, sizeof(magic), &read, nullptr);
            CloseHandle(nested);
        }
        if (read == 4 && magic[0] == 'P' && magic[1] == 'K' && magic[2] == 3 && magic[3] == 4) {
            ScanZip(tempPath, memberVirtual, depth + 1);
        }
    }

    DeleteFileW(tempPath.c_str());
    return proceed && !isCancelled_.load(std::memory_order_relaxed);
}

ArchiveScanner::~ArchiveScanner()
{
    // Остатки temp-каталога (после отмены): удалить файлы и сам каталог.
    if (tempDir_.empty()) {
        return;
    }

    WIN32_FIND_DATAW findData{};
    HANDLE find = FindFirstFileW((tempDir_ + L"\\*").c_str(), &findData);
    if (find != INVALID_HANDLE_VALUE) {
        do {
            const std::wstring name = findData.cFileName;
            if (name != L"." && name != L"..") {
                DeleteFileW((tempDir_ + L"\\" + name).c_str());
            }
        } while (FindNextFileW(find, &findData));
        FindClose(find);
    }
    RemoveDirectoryW(tempDir_.c_str());
    RemoveDirectoryW((TempRoot() + L"ScannerCore").c_str());
}

} // namespace scan
