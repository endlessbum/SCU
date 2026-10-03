#pragma once

// Главный pipeline сканирования (документ п. 5): hash → db lookup → allowlist
// → cache (только вердикты эвристик) → classification → signature → PE analysis
// → correlation-lite → verdict. Кэш никогда не перекрывает signed DB.

#include "core/database.h"
#include "core/allowlist.h"
#include "core/types.h"
#include "filesystem/enumerator.h"
#include "ipc/event_writer.h"

#include <atomic>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace scan {

class Scanner {
public:
    Scanner(EventWriter& events, ComponentAllowlist& allowlist, HashDatabase& database);

    // Заполняет stats/detections; возвращает false, если сканирование было
    // прервано отменой (частичный результат всё равно валиден — п. 26).
    bool Run(const ScanOptions& options,
             ScanStats& stats,
             std::vector<Detection>& detections);

    void RequestCancel() { isCancelled_.store(true, std::memory_order_relaxed); }
    bool IsCancelled() const { return isCancelled_.load(std::memory_order_relaxed); }

private:
    void ScanFile(const FileEntry& entry, ScanStats& stats, std::vector<Detection>& detections);

    // Обработка извлечённого члена архива (ScanFile/RunArchiveScan → ArchiveScanner).
    // Возвращает вердикт члена; отмена/предел — Clean, ArchiveScanner прерывает обход.
    Verdict ProcessMember(const std::wstring& virtualPath,
                          const std::wstring& realPath,
                          unsigned long long size,
                          const std::wstring& containerPath,
                          ScanStats& stats,
                          std::vector<Detection>& detections);

    // Полный archive-скан контейнера: извлечение членов с лимитами, скан каждого,
    // вложенные архивы разворачивает ArchiveScanner. Возвращает худший вердикт
    // члена (Clean, если контейнер чист или скан прерван отменой). Используется
    // и для файлов из обхода, и для persistence-целей-архивов.
    Verdict RunArchiveScan(const std::wstring& path,
                           const std::wstring& containerPath,
                           ScanStats& stats,
                           std::vector<Detection>& detections);

    // Общая часть для обычных файлов и членов архивов: hash → db → allowlist
    // → верификация/эвристики. Добавляет detection при вердикте != Clean.
    // Возвращает вердикт цели (для корреляции persistence, п. 18).
    // countInFileStats=false — анализ цели persistence: цель не из обхода,
    // счётчик файлов не растёт (семантика filesScanned — только обход).
    Verdict AnalyzeFile(const std::wstring& displayPath,
                        const std::wstring& realPath,
                        unsigned long long size,
                        const std::wstring& containerPath,
                        bool isVirtual,
                        ScanStats& stats,
                        std::vector<Detection>& detections,
                        bool& needsArchiveScan,
                        bool countInFileStats = true);

    // Persistence (п. 17/18): каждая запись коррелируется с анализом цели
    // (файл — через AnalyzeFile, командная строка — через ScriptScanner).
    void ScanPersistence(ScanStats& stats, std::vector<Detection>& detections);

    // Процессы (п. 18): в срезе детект только по hash-DB — одиночный признак
    // «запуск из AppData/Temp» не считается malware.
    void ScanProcesses(ScanStats& stats, std::vector<Detection>& detections);

    EventWriter& events_;
    ComponentAllowlist& allowlist_;
    HashDatabase& database_;
    unsigned long long maxFileSize_ = 0;
    bool scanArchives_ = true;
    bool scanScripts_ = true;
    bool scanPersistence_ = true;
    bool scanProcesses_ = true;
    bool useDiskCache_ = true;
    // Test-hook SCU_TEST_CANCEL_AFTER (интеграционные тесты отмены): 0 = выкл.
    unsigned long long testCancelAfter_ = 0;
    std::atomic<bool> isCancelled_{false};

    // Состояние, общее для воркеров: короткие критические секции под stateMutex_
    // (stats/detections), тяжёлый анализ — вне блокировок (аудит 2, п. 15).
    // ScanPersistence/ScanProcesses идут после join'а воркеров.
    std::mutex stateMutex_;

    // Кэш по SHA-256 (п. 25): детект или минимальная запись с Clean.
    // Персистится в %LOCALAPPDATA%\SCU\scan-cache.txt; инвалидация — по
    // engine/db версии И профилю скана в заголовке файла (п. 2 аудита).
    // Кэш не может перекрыть signed DB: lookup по базе всегда выполняется
    // до обращения к кэшу. Читается/пишется воркерами — под cacheMutex_;
    // порядок захвата всегда cacheMutex_ → stateMutex_.
    struct CachedVerdict {
        Verdict verdict;
        Detection detection; // валиден при verdict != Clean
    };
    std::mutex cacheMutex_;
    std::unordered_map<std::string, CachedVerdict> verdictCache_;
    void RememberVerdict(const std::string& sha256, Verdict verdict, Detection detection);
    std::string CacheProfileTag() const;
    void LoadDiskCache();
    void SaveDiskCache();

    std::wstring DiskCachePath() const;
};

} // namespace scan
