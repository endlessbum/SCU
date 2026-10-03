#include "filesystem/enumerator.h"

#include <windows.h>

namespace scan {

namespace {

// Разумный предел глубины: защита от аномально глубоких деревьев.
constexpr int kMaxDepth = 48;

void WalkDirectory(const std::wstring& directory,
                   int depth,
                   const std::function<void(const FileEntry&)>& onFile,
                   const std::function<void(const std::wstring&, unsigned long)>& onDirectoryError,
                   const std::function<void(const std::wstring&, const wchar_t*)>& onSkipped,
                   const std::atomic<bool>& isCancelled)
{
    if (depth > kMaxDepth) {
        // Предел глубины — не «пустой каталог», а непроверенное поддерево
        // (аудит 2, п. 4): обязательно фиксируем потерю покрытия.
        onSkipped(directory, L"max-depth");
        return;
    }

    const std::wstring pattern = directory + L"\\*";
    WIN32_FIND_DATAW findData{};
    HANDLE find = FindFirstFileExW(pattern.c_str(), FindExInfoBasic, &findData,
                                   FindExSearchNameMatch, nullptr, FIND_FIRST_EX_LARGE_FETCH);
    if (find == INVALID_HANDLE_VALUE) {
        const unsigned long code = GetLastError();
        if (code != ERROR_FILE_NOT_FOUND) {
            onDirectoryError(directory, code);
        }
        return;
    }

    do {
        if (isCancelled.load(std::memory_order_relaxed)) {
            break;
        }

        const std::wstring name = findData.cFileName;
        if (name == L"." || name == L"..") {
            continue;
        }

        const std::wstring fullPath = directory + L"\\" + name;

        // Reparse points (junction/symlink/OneDrive placeholders) не проходим:
        // циклы и выход за пределы корня сканирования. Но сам факт пропуска
        // попадает в coverage (аудит 2, п. 3).
        if (findData.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) {
            onSkipped(fullPath, L"reparse");
            continue;
        }

        if (findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            WalkDirectory(fullPath, depth + 1, onFile, onDirectoryError, onSkipped, isCancelled);
            continue;
        }

        if (findData.dwFileAttributes & (FILE_ATTRIBUTE_DEVICE | FILE_ATTRIBUTE_OFFLINE)) {
            onSkipped(fullPath,
                      (findData.dwFileAttributes & FILE_ATTRIBUTE_OFFLINE) ? L"offline" : L"device");
            continue;
        }

        FileEntry entry;
        entry.path = fullPath;
        entry.size = (static_cast<unsigned long long>(findData.nFileSizeHigh) << 32)
                     | findData.nFileSizeLow;
        onFile(entry);
    } while (FindNextFileW(find, &findData));

    const unsigned long nextError = GetLastError();
    FindClose(find);
    if (nextError != ERROR_NO_MORE_FILES && nextError != ERROR_SUCCESS
        && !isCancelled.load(std::memory_order_relaxed)) {
        onDirectoryError(directory, nextError);
    }
}

} // namespace

void DirectoryEnumerator::Enumerate(const std::wstring& root,
                                    const std::function<void(const FileEntry&)>& onFile,
                                    const std::function<void(const std::wstring&, unsigned long)>& onDirectoryError,
                                    const std::function<void(const std::wstring&, const wchar_t*)>& onSkipped,
                                    const std::atomic<bool>& isCancelled)
{
    DWORD attributes = GetFileAttributesW(root.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        onDirectoryError(root, GetLastError());
        return;
    }

    if (attributes & FILE_ATTRIBUTE_REPARSE_POINT) {
        // Корень-репарс не обходится намеренно; это skip, а не пустой результат
        // (аудит 2, п. 3.1): иначе единственный target-репарс дал бы ложный Clean.
        onSkipped(root, L"reparse-root");
        return;
    }

    if (!(attributes & FILE_ATTRIBUTE_DIRECTORY)) {
        WIN32_FIND_DATAW findData{};
        HANDLE find = FindFirstFileExW(root.c_str(), FindExInfoBasic, &findData,
                                       FindExSearchNameMatch, nullptr, 0);
        if (find != INVALID_HANDLE_VALUE) {
            FileEntry entry;
            entry.path = root;
            entry.size = (static_cast<unsigned long long>(findData.nFileSizeHigh) << 32)
                         | findData.nFileSizeLow;
            FindClose(find);
            onFile(entry);
        } else {
            onDirectoryError(root, GetLastError());
        }
        return;
    }

    WalkDirectory(root, 0, onFile, onDirectoryError, onSkipped, isCancelled);
}

} // namespace scan
