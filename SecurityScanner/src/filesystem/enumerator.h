#pragma once

// Безопасный рекурсивный обход директорий (документ п. 6/22):
// - не следует за reparse points (junction/symlink — защита от циклов);
// - ограничение глубины;
// - недоступные директории не прерывают обход (только счётчик ошибок);
// - отмена через atomic-флаг между файлами, не посреди FindNext.

#include <atomic>
#include <functional>
#include <string>
#include <system_error>

namespace scan {

struct FileEntry {
    std::wstring path;
    unsigned long long size = 0;
};

class DirectoryEnumerator {
public:
    // onFile вызывается для каждого обычного файла; onDirectoryError — при
    // недоступной директории. isCancelled опрашивается перед каждым входом.
    void Enumerate(const std::wstring& root,
                   const std::function<void(const FileEntry&)>& onFile,
                   const std::function<void(const std::wstring& path, unsigned long code)>& onDirectoryError,
                   const std::atomic<bool>& isCancelled);
};

} // namespace scan
