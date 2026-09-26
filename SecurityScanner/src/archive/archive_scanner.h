#pragma once

// ArchiveScanner, срез v2 (документ п. 20): только ZIP, извлечение членов
// через miniz (public domain, third_party/miniz). Жёсткие лимиты:
// nesting, expanded size, entries, размер архива и члена, zip-bomb ratio.
// Зашифрованные ZIP — явный отказ (issue encrypted-archive), не молчаливый скип.
// Члены извлекаются во временные файлы по индексам (m0001...) — path traversal
// из имён членов невозможен; оригинальное имя живёт только в virtual path.

#include <atomic>
#include <functional>
#include <string>
#include <vector>

namespace scan {

struct ArchiveLimits {
    uint32_t maxNesting = 3;                                        // документ п. 4
    uint64_t maxExpandedSize = 512ull * 1024 * 1024;                // документ п. 4
    uint64_t maxArchiveSize = 256ull * 1024 * 1024;  // архив читается в память
    uint64_t maxMemberSize = 256ull * 1024 * 1024;
    uint32_t maxEntries = 50000;
    uint64_t maxCompressionRatio = 500;  // uncomp/comp при uncomp > 10 МБ
};

struct ArchiveIssue {
    std::wstring virtualPath;
    std::wstring code; // encrypted-archive | corrupt-archive | too-large | too-many-entries | expanded-limit | unsupported-member | member-too-large | zip-bomb | too-deep
    std::wstring member;
};

// Обработка извлечённого члена: virtualPath — для отчёта, realPath — временный
// файл на диске (удаляется после обработки). Возврат false прерывает обход.
using MemberCallback = std::function<bool(const std::wstring& virtualPath,
                                          const std::wstring& realPath,
                                          unsigned long long size)>;
using IssueCallback = std::function<void(const ArchiveIssue&)>;

class ArchiveScanner {
public:
    ArchiveScanner(const ArchiveLimits& limits,
                   const std::atomic<bool>& isCancelled,
                   MemberCallback onMember,
                   IssueCallback onIssue);

    // ZIP: обход членов с рекурсией во вложенные ZIP (depth от 0).
    // Возврат false: отмена или исчерпан лимит expanded size.
    bool ScanZip(const std::wstring& path, const std::wstring& virtualPath, int depth);

private:
    bool ScanMember(int index,
                    const std::wstring& displayName,
                    unsigned long long compSize,
                    unsigned long long uncompSize,
                    bool isSupported,
                    const std::wstring& virtualPath,
                    int depth,
                    void* zipHandle);

    const ArchiveLimits& limits_;
    const std::atomic<bool>& isCancelled_;
    MemberCallback onMember_;
    IssueCallback onIssue_;
    std::wstring tempDir_; // создаётся при первом извлечённом члене
    uint64_t totalExpanded_ = 0;
    unsigned long memberCounter_ = 0;

public:
    ~ArchiveScanner();

private:
};

} // namespace scan
