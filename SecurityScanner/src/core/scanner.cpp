#include "core/scanner.h"

#include "core/path_util.h"

#include <windows.h>

#include <algorithm>
#include <cctype>
#include <fstream>
#include <sstream>
#include <thread>
#include <unordered_map>

#include "archive/archive_scanner.h"
#include "core/file_type.h"
#include "hash/sha256.h"
#include "pe/pe_analyzer.h"
#include "pe/signature.h"
#include "script/script_scanner.h"
#include "system/persistence_scanner.h"
#include "system/process_scanner.h"

namespace scan {

namespace {

// Порог Suspicious для PE по эвристическим сигналам (документ п. 15:
// стартовые пороги — архитектурный черновик, калибруются на corpus).
// score >= 4 означает минимум два независимых сигнала.
constexpr int kSuspiciousPeScore = 4;
constexpr int kSuspiciousScriptScore = 4;

// Верхняя граница дискового кэша (п. 25): переполнение отключает пополнение.
constexpr size_t kMaxCacheEntries = 100000;

bool FileExistsNow(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}

unsigned long long GetFileSizeSafe(const std::wstring& path)
{
    WIN32_FIND_DATAW findData{};
    HANDLE find = FindFirstFileExW(path.c_str(), FindExInfoBasic, &findData,
                                   FindExSearchNameMatch, nullptr, 0);
    if (find == INVALID_HANDLE_VALUE) {
        return 0;
    }
    FindClose(find);
    return (static_cast<unsigned long long>(findData.nFileSizeHigh) << 32) | findData.nFileSizeLow;
}

std::string ToLowerAscii(const std::wstring& text)
{
    std::string result;
    result.reserve(text.size());
    for (const wchar_t c : text) {
        if (c < 0x80) {
            result += static_cast<char>(std::tolower(static_cast<int>(c)));
        }
    }
    return result;
}

// Валидный ключ кэша — строго 64 hex-символа. Повреждённая строка кэша
// игнорируется (fail-closed: вердикт пересчитывается, не подменяется).
bool IsSha256Hex(const std::string& text)
{
    if (text.size() != 64) {
        return false;
    }
    for (const char c : text) {
        const bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
        if (!hex) {
            return false;
        }
    }
    return true;
}

std::string HashTargetForReport(const std::wstring& path)
{
    std::string hex;
    return Sha256::HashFile(path, hex) ? hex : std::string();
}

std::wstring VerdictToWide(Verdict verdict)
{
    switch (verdict) {
    case Verdict::Malware: return L"malware";
    case Verdict::Suspicious: return L"suspicious";
    default: return L"clean";
    }
}

} // namespace

Scanner::Scanner(EventWriter& events, ComponentAllowlist& allowlist, HashDatabase& database)
    : events_(events)
    , allowlist_(allowlist)
    , database_(database)
{
}

void Scanner::RememberVerdict(const std::string& sha256, Verdict verdict, Detection detection)
{
    // Лимит кэша (п. 25): при переполнении новые записи не добавляются —
    // деградация до отсутствия кэша, не до неверных результатов.
    // Кэш mutated воркерами параллельно — под собственным cacheMutex_
    // (порядок захвата всегда cacheMutex_ → stateMutex_).
    std::lock_guard lock(cacheMutex_);
    if (verdictCache_.size() < kMaxCacheEntries) {
        verdictCache_[sha256] = {verdict, std::move(detection)};
    }
}

std::wstring Scanner::DiskCachePath() const
{
    // Динамический путь (аудит п. 9): длинный путь профиля не усекается.
    const std::wstring localAppData = GetEnvironmentValueDynamic(L"LOCALAPPDATA");
    if (localAppData.empty()) {
        return {};
    }
    std::wstring path = localAppData + L"\\SCU";
    CreateDirectoryW(path.c_str(), nullptr);
    return path + L"\\scan-cache.txt";
}

// Профиль скана в заголовке кэша (п. 2 аудита): кэш валиден только для той же
// комбинации параметров, влияющих на вердикт. Изменение любого параметра
// (scripts/archives/persistence/processes/maxFileSize/эвристики) полностью
// инвалидирует сохранённый кэш.
std::string Scanner::CacheProfileTag() const
{
    std::ostringstream out;
    out << "scripts=" << (scanScripts_ ? 1 : 0)
        << ";archives=" << (scanArchives_ ? 1 : 0)
        << ";persistence=" << (scanPersistence_ ? 1 : 0)
        << ";processes=" << (scanProcesses_ ? 1 : 0)
        << ";maxbytes=" << maxFileSize_
        << ";heur=" << kHeuristicVersion;
    return out.str();
}

// Формат: заголовок
// "#scucache v1 engine=<ver> db=<ver> profile=<scripts=N;archives=N;...>",
// далее строки "sha256<TAB>verdict<TAB>ruleId<TAB>score<TAB>signed<TAB>description".
// Несовпадение версии движка, базы или профиля — весь кэш отбрасывается.
void Scanner::LoadDiskCache()
{
    const std::wstring path = DiskCachePath();
    if (path.empty()) {
        return;
    }

    std::ifstream file(path);
    if (!file.is_open()) {
        return;
    }

    std::string header;
    if (!std::getline(file, header)) {
        return;
    }
    const std::string expected = std::string("#scucache v1 engine=") + kEngineVersion
                                 + " db=" + std::string(database_.Version().begin(), database_.Version().end())
                                 + " profile=" + CacheProfileTag();
    if (header != expected) {
        return; // другая версия движка/базы/профиля — кэш недействителен
    }

    std::string line;
    while (std::getline(file, line) && verdictCache_.size() < kMaxCacheEntries) {
        std::istringstream stream(line);
        std::string sha256;
        std::string verdictText;
        std::string ruleId;
        std::string scoreText;
        std::string signedText;
        std::string description;
        if (!std::getline(stream, sha256, '\t') || !std::getline(stream, verdictText, '\t')) {
            continue; // повреждённая строка — пропускается, не превращается в Clean
        }
        if (!IsSha256Hex(sha256)) {
            continue;
        }
        if (verdictText != "malware" && verdictText != "suspicious" && verdictText != "clean") {
            continue; // неизвестный вердикт — fail-closed: пересчёт по-настоящему
        }

        const Verdict verdict = verdictText == "malware" ? Verdict::Malware
                                : verdictText == "suspicious" ? Verdict::Suspicious
                                                              : Verdict::Clean;
        if (verdict == Verdict::Clean) {
            verdictCache_[sha256] = {Verdict::Clean, Detection{}};
            continue;
        }

        std::getline(stream, ruleId, '\t');
        std::getline(stream, scoreText, '\t');
        std::getline(stream, signedText, '\t');
        std::getline(stream, description);

        Detection detection;
        detection.sha256 = sha256;
        detection.verdict = verdict;
        detection.ruleId = std::wstring(ruleId.begin(), ruleId.end());
        detection.description = std::wstring(description.begin(), description.end());
        detection.score = atoi(scoreText.c_str());
        detection.signedFile = signedText == "1";
        verdictCache_[sha256] = {verdict, std::move(detection)};
    }
}

void Scanner::SaveDiskCache()
{
    const std::wstring path = DiskCachePath();
    if (path.empty()) {
        return;
    }

    std::ostringstream out;
    out << "#scucache v1 engine=" << kEngineVersion
        << " db=" << std::string(database_.Version().begin(), database_.Version().end())
        << " profile=" << CacheProfileTag() << "\n";
    for (const auto& [sha256, cached] : verdictCache_) {
        out << sha256 << '\t' << VerdictToString(cached.verdict);
        if (cached.verdict == Verdict::Clean) {
            out << '\n';
            continue;
        }
        out << '\t' << std::string(cached.detection.ruleId.begin(), cached.detection.ruleId.end())
            << '\t' << cached.detection.score
            << '\t' << (cached.detection.signedFile ? '1' : '0')
            << '\t' << std::string(cached.detection.description.begin(), cached.detection.description.end())
            << '\n';
    }

    const std::string content = out.str();
    const std::wstring tempPath = path + L".tmp";
    HANDLE file = CreateFileW(tempPath.c_str(), GENERIC_WRITE, 0,
                              nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return;
    }
    DWORD written = 0;
    if (WriteFile(file, content.data(), static_cast<DWORD>(content.size()), &written, nullptr)
        && written == content.size()) {
        CloseHandle(file);
        MoveFileExW(tempPath.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING);
    } else {
        CloseHandle(file);
        DeleteFileW(tempPath.c_str());
    }
}

bool Scanner::Run(const ScanOptions& options, ScanStats& stats, std::vector<Detection>& detections)
{
    maxFileSize_ = options.maxFileSize;
    scanArchives_ = options.scanArchives;
    scanScripts_ = options.scanScripts;
    scanPersistence_ = options.scanPersistence;
    scanProcesses_ = options.scanProcesses;
    useDiskCache_ = options.useDiskCache;

    // Test-hook (только для интеграционных тестов): SCU_TEST_CANCEL_AFTER=<N>
    // прерывает скан после N обработанных файлов — тем же флагом, что и отмена
    // пользователя, поэтому поведение идентично прод-отмене (частичный отчёт).
    {
        wchar_t cancelAfter[16]{};
        if (GetEnvironmentVariableW(L"SCU_TEST_CANCEL_AFTER", cancelAfter, 16) > 0) {
            const unsigned long long parsed = wcstoull(cancelAfter, nullptr, 10);
            if (parsed > 0) {
                testCancelAfter_ = parsed;
            }
        }
    }

    // 1. Обход дерева — быстрый однопоточный этап, результат в вектор.
    std::vector<FileEntry> entries;
    DirectoryEnumerator enumerator;
    enumerator.Enumerate(
        options.path,
        [&entries](const FileEntry& entry) {
            entries.push_back(entry);
        },
        [this, &stats](const std::wstring& path, unsigned long code) {
            std::lock_guard lock(stateMutex_);
            stats.errors++;
            wchar_t codeText[16];
            swprintf_s(codeText, L"%lu", code);
            events_.Warning(L"directory error (" + std::wstring(codeText) + L"): " + path);
        },
        // Намеренный пропуск обхода (reparse/max-depth/offline/device) — потеря
        // покрытия, а не пустой результат (аудит 2, п. 3–5): фиксируется как
        // filesSkipped, чтобы итог стал Partial.
        [this, &stats](const std::wstring& path, const wchar_t* reason) {
            std::lock_guard lock(stateMutex_);
            stats.filesSkipped++;
            events_.Warning(L"skipped (" + std::wstring(reason) + L"): " + path);
        },
        isCancelled_);

    // 2. Дисковый кэш загружается до воркеров (п. 25). Этапы однопоточные:
    //    блокировка не нужна, join воркеров даёт happens-before для записи.
    if (useDiskCache_) {
        LoadDiskCache();
    }

    // 3. Фиксированный thread pool (п. 26): каждому воркеру — следующий файл
    // по atomic-индексу. ScanFile сам держит stateMutex_ и сам пишет progress.
    const unsigned cores = std::thread::hardware_concurrency() > 1
        ? static_cast<unsigned>(std::thread::hardware_concurrency()) - 1
        : 1;
    const unsigned threadCount = options.workerThreads != 0
        ? options.workerThreads
        : std::clamp(cores, 1u, 8u);
    if (threadCount > 1 && entries.size() > 32) {
        std::atomic<size_t> nextIndex{0};
        std::atomic<unsigned long long> processedCount{0};
        std::vector<std::thread> workers;
        workers.reserve(threadCount);
        for (unsigned t = 0; t < threadCount; ++t) {
            workers.emplace_back([this, &entries, &nextIndex, &stats, &detections, &processedCount]() {
                while (!IsCancelled()) {
                    const size_t index = nextIndex.fetch_add(1, std::memory_order_relaxed);
                    if (index >= entries.size()) {
                        return;
                    }
                    ScanFile(entries[index], stats, detections);
                    if (testCancelAfter_ > 0
                        && processedCount.fetch_add(1, std::memory_order_relaxed) + 1 >= testCancelAfter_) {
                        isCancelled_.store(true, std::memory_order_relaxed);
                    }
                }
            });
        }
        for (auto& worker : workers) {
            worker.join();
        }
    } else {
        unsigned long long processed = 0;
        for (const FileEntry& entry : entries) {
            if (IsCancelled()) {
                break;
            }
            ScanFile(entry, stats, detections);
            if (testCancelAfter_ > 0 && ++processed >= testCancelAfter_) {
                isCancelled_.store(true, std::memory_order_relaxed);
            }
        }
    }

    // 4. Persistence и процессы — последовательные этапы после воркеров (п. 24).
    if (!IsCancelled() && options.mode == ScanMode::Custom) {
        if (options.scanPersistence) {
            ScanPersistence(stats, detections);
        }
        if (!IsCancelled() && options.scanProcesses) {
            ScanProcesses(stats, detections);
        }
    }

    // 5. Кэш сохраняется один раз в конце скана (воркеры уже join — потокобопасно).
    if (useDiskCache_) {
        SaveDiskCache();
    }

    return !IsCancelled();
}

void Scanner::ScanFile(const FileEntry& entry, ScanStats& stats, std::vector<Detection>& detections)
{
    // Анализ выполняется ВНЕ общих блокировок (аудит 2, п. 15): SHA-256,
    // WinVerifyTrust и PE-разбор не должны сериализовать воркеры.
    // AnalyzeFile сам синхронизирует короткие критические секции
    // (stats/detections/verdictCache), events_ имеет собственный мьютекс.
    bool needsArchiveScan = false;
    AnalyzeFile(entry.path, entry.path, entry.size, L"", false, stats, detections, needsArchiveScan);

    {
        std::lock_guard lock(stateMutex_);
        events_.Progress(stats, entry.path);
    }

    // Полный pipeline для архива (ScanMode::File и Full Scan, документ п. 20/24):
    // извлечение членов с лимитами и скан каждого. Само извлечение — вне
    // блокировки (тяжёлая операция), синхронизация — внутри ProcessMember.
    if (needsArchiveScan && scanArchives_ && !IsCancelled()) {
        RunArchiveScan(entry.path, entry.path, stats, detections);
    }
}

Verdict Scanner::RunArchiveScan(const std::wstring& path,
                                const std::wstring& containerPath,
                                ScanStats& stats,
                                std::vector<Detection>& detections)
{
    Verdict worst = Verdict::Clean;
    ArchiveLimits limits;
    ArchiveScanner scanner(
        limits,
        isCancelled_,
        [this, &stats, &detections, &worst, containerPath](const std::wstring& virtualPath,
                                                            const std::wstring& realPath,
                                                            unsigned long long size) {
            const Verdict verdict = ProcessMember(virtualPath, realPath, size, containerPath, stats, detections);
            if (verdict == Verdict::Malware) {
                worst = Verdict::Malware;
            } else if (verdict == Verdict::Suspicious && worst != Verdict::Malware) {
                worst = Verdict::Suspicious;
            }
            return !IsCancelled();
        },
        [this, &stats](const ArchiveIssue& issue) {
            std::lock_guard lock(stateMutex_);
            // Ошибки vs пропуски формализованы (аудит п. 3): технический сбой
            // идёт в errors (exit code 1 → Partial), ограничение покрытия —
            // в filesSkipped (Partial без exit code 1).
            if (issue.technical) {
                stats.errors++;
                events_.Error(L"archive " + issue.code + L": " + issue.virtualPath
                              + (issue.member.empty() ? L"" : L" :: " + issue.member));
            } else {
                stats.filesSkipped++;
                events_.Warning(L"archive " + issue.code + L": " + issue.virtualPath
                                + (issue.member.empty() ? L"" : L" :: " + issue.member));
            }
        });
    scanner.ScanZip(path, path, 0);
    return worst;
}

Verdict Scanner::ProcessMember(const std::wstring& virtualPath,
                            const std::wstring& realPath,
                            unsigned long long size,
                            const std::wstring& containerPath,
                            ScanStats& stats,
                            std::vector<Detection>& detections)
{
    if (IsCancelled()) {
        return Verdict::Clean;
    }

    // Анализ члена — вне общих блокировок (см. ScanFile, аудит 2, п. 15).
    bool needsArchiveScan = false;
    const Verdict verdict = AnalyzeFile(virtualPath, realPath, size, containerPath, true,
                                        stats, detections, needsArchiveScan);

    {
        std::lock_guard lock(stateMutex_);
        events_.Progress(stats, virtualPath);
    }
    return verdict;
}

Verdict Scanner::AnalyzeFile(const std::wstring& displayPath,
                             const std::wstring& realPath,
                             unsigned long long size,
                             const std::wstring& containerPath,
                             bool isVirtual,
                             ScanStats& stats,
                             std::vector<Detection>& detections,
                             bool& needsArchiveScan,
                             bool countInFileStats)
{
    needsArchiveScan = false;

    // Фильтр по размеру (документ п. 2.A): слишком большие файлы не анализируем.
    // Это потеря покрытия для ЛЮБОГО источника (аудит 2, п. 7): persistence-цель
    // с size > maxFileSize больше не исчезает молча — итог становится Partial.
    if (size > maxFileSize_) {
        {
            std::lock_guard lock(stateMutex_);
            stats.filesSkipped++;
        }
        events_.Warning(L"skipped oversized: " + displayPath);
        return Verdict::Clean;
    }

    std::string sha256;
    if (!Sha256::HashFile(realPath, sha256)) {
        {
            std::lock_guard lock(stateMutex_);
            stats.errors++;
        }
        events_.Warning(L"hash failed: " + displayPath);
        return Verdict::Clean;
    }

    // 1. Hash lookup по базе IOC — ВСЕГДА до кэша (п. 1 аудита): подписанная
    //    база обновляется, кэш — нет, поэтому кэш не имеет права перекрывать
    //    её вердикт (hash в malware-базе + чистый кэш = malware). Выполняется
    //    даже для allowlist-компонентов (документ п. 19). database_/allowlist_
    //    иммутабельны во время скана — читаются без блокировки.
    Verdict dbVerdict;
    std::wstring dbName;
    if (database_.Lookup(sha256, dbVerdict, dbName)) {
        Detection detection;
        detection.path = displayPath;
        detection.sha256 = sha256;
        detection.verdict = dbVerdict;
        detection.ruleId = L"HASH-DB";
        detection.description = dbName.empty() ? L"Known malicious hash" : dbName;
        detection.containerPath = containerPath;
        detection.isVirtual = isVirtual;
        {
            std::lock_guard lock(stateMutex_);
            if (countInFileStats) {
                stats.filesScanned++;
            }
            stats.detections++;
            detections.push_back(detection);
        }
        events_.DetectionEvent(detection);
        RememberVerdict(sha256, dbVerdict, detection);
        return dbVerdict;
    }

    if (countInFileStats) {
        std::lock_guard lock(stateMutex_);
        stats.filesScanned++;
    }

    // 2. Собственные компоненты SCU: clean без эвристик (документ п. 29).
    if (allowlist_.Contains(sha256)) {
        RememberVerdict(sha256, Verdict::Clean, Detection{});
        return Verdict::Clean;
    }

    // 3. Классификация по содержимому — ДО кэша (п. 3 аудита): «чистый»
    //    вердикт контейнера из кэша не доказывает безопасность содержимого,
    //    поэтому архив не может пройти мимо archive scan через cache hit.
    //    Классификация дешёвая (magic bytes) в сравнении с эвристиками.
    const FileType fileType = ClassifyFile(realPath);
    if (fileType == FileType::ArchiveZip) {
        needsArchiveScan = true;
        // Контейнер НЕ кэшируется вовсе: обновление базы может найти malware
        // внутри неизменённого архива — члены сканируются при каждой встрече.
        return Verdict::Clean;
    }

    // 4. Кэш вердиктов по SHA-256 (п. 25): только для вердиктов, посчитанных
    //    эвристиками (WinVerifyTrust/PE/скрипты); повторные копии того же файла
    //    не проходят их повторно. DB выше уже сказала «не в базе», а архив
    //    выше уже ушёл в archive scan — кэш не может замаскировать ни детект
    //    базы, ни содержимое контейнера. Кэш — под собственным cacheMutex_
    //    (порядок захвата всегда cacheMutex_ → stateMutex_).
    {
        std::lock_guard cacheLock(cacheMutex_);
        const auto cached = verdictCache_.find(sha256);
        if (cached != verdictCache_.end()) {
            if (cached->second.verdict != Verdict::Clean) {
                Detection clone = cached->second.detection;
                clone.path = displayPath;
                clone.containerPath = containerPath;
                clone.isVirtual = isVirtual;
                std::lock_guard stateLock(stateMutex_);
                stats.detections++;
                detections.push_back(clone);
                events_.DetectionEvent(clone);
            }
            return cached->second.verdict;
        }
    }

    // === Тяжёлый анализ — без общих блокировок ===

    if (fileType == FileType::Script && scanScripts_) {
        const ScriptAnalysis script = ScriptScanner::Analyze(realPath);
        if (script.score >= kSuspiciousScriptScore) {
            Detection detection;
            detection.path = displayPath;
            detection.sha256 = sha256;
            detection.verdict = Verdict::Suspicious;
            detection.ruleId = L"SCRIPT-HEURISTIC";
            detection.description = L"Multiple independent script signals";
            detection.score = script.score;
            detection.signals = script.signals;
            detection.containerPath = containerPath;
            detection.isVirtual = isVirtual;
            {
                std::lock_guard lock(stateMutex_);
                detections.push_back(detection);
                stats.detections++;
            }
            events_.DetectionEvent(detection);
            RememberVerdict(sha256, Verdict::Suspicious, detection);
            return Verdict::Suspicious;
        }
        RememberVerdict(sha256, Verdict::Clean, Detection{});
        return Verdict::Clean;
    }

    if (fileType != FileType::PE) {
        RememberVerdict(sha256, Verdict::Clean, Detection{});
        return Verdict::Clean;
    }

    const SignatureCheck signature = SignatureVerifier::Verify(realPath);
    const PeAnalysis pe = PeAnalyzer::Analyze(realPath);

    int score = pe.score;
    std::vector<std::wstring> signals = pe.signals;
    if (!signature.trusted) {
        score += 1;
        signals.push_back(L"unsigned-pe");
    }

    if (score >= kSuspiciousPeScore) {
        Detection detection;
        detection.path = displayPath;
        detection.sha256 = sha256;
        detection.verdict = Verdict::Suspicious;
        detection.ruleId = L"PE-HEURISTIC";
        detection.description = L"Multiple independent PE signals";
        detection.signedFile = signature.trusted;
        detection.publisher = signature.publisher;
        detection.score = score;
        detection.signals = signals;
        detection.containerPath = containerPath;
        detection.isVirtual = isVirtual;
        {
            std::lock_guard lock(stateMutex_);
            detections.push_back(detection);
            stats.detections++;
        }
        events_.DetectionEvent(detection);
        RememberVerdict(sha256, Verdict::Suspicious, detection);
        return Verdict::Suspicious;
    }

    RememberVerdict(sha256, Verdict::Clean, Detection{});
    return Verdict::Clean;
}

void Scanner::ScanPersistence(ScanStats& stats, std::vector<Detection>& detections)
{
    // Кэш вердиктов целей: одна цель анализируется один раз, сколько бы
    // записей (служб, задач) на неё ни указывало.
    std::unordered_map<std::wstring, Verdict> verdictCache;

    PersistenceScanner persistence;
    persistence.Enumerate(
        [&](const PersistenceEntry& entry) {
            if (IsCancelled()) {
                return;
            }

            const std::wstring displayPath = entry.location + L"\\" + entry.name;
            events_.Progress(stats, displayPath);

        Verdict verdict = Verdict::Clean;
        std::vector<std::wstring> signals;
        int score = 0;

        const bool hasTarget = !entry.targetPath.empty() && FileExistsNow(entry.targetPath);
        Verdict targetVerdict = Verdict::Clean;
        if (hasTarget) {
            const auto cached = verdictCache.find(entry.targetPath);
            if (cached != verdictCache.end()) {
                targetVerdict = cached->second;
            } else {
                bool needsArchiveScan = false;
                targetVerdict = AnalyzeFile(displayPath, entry.targetPath, GetFileSizeSafe(entry.targetPath),
                                            L"", false, stats, detections, needsArchiveScan,
                                            /*countInFileStats=*/false);

                // Persistence-цель — архив: AnalyzeFile анализирует только сам
                // контейнер (п. 4 аудита). Полный archive-скан обязателен, иначе
                // вредонос внутри цели остаётся скрытым; худший вердикт члена
                // прокидывается наружу как вердикт цели.
                if (targetVerdict == Verdict::Clean && needsArchiveScan && scanArchives_ && !IsCancelled()) {
                    targetVerdict = RunArchiveScan(entry.targetPath, entry.targetPath, stats, detections);
                }

                verdictCache[entry.targetPath] = targetVerdict;
            }

            if (targetVerdict != Verdict::Clean) {
                verdict = targetVerdict;
                signals.push_back(L"persistence-target:" + VerdictToWide(targetVerdict));
                score += 2;
            }
        }

        // П. SCAN-03: командная строка анализируется ВСЕГДА, а не только когда
        // цель отсутствует. Чистый host-exe (powershell.exe, wscript.exe) с
        // вредоносными аргументами (-enc <payload>, /c script.vbs) раньше
        // проходил как clean, пока существовал и был чист сам exe.
        const std::string commandLower = ToLowerAscii(entry.command);
        const ScriptAnalysis script = ScriptScanner::AnalyzeText(commandLower);
        if (script.score >= kSuspiciousScriptScore) {
            verdict = verdict == Verdict::Clean ? Verdict::Suspicious : verdict;
            score = std::max(score, script.score);
            signals.insert(signals.end(), script.signals.begin(), script.signals.end());
        }

        if (verdict == Verdict::Clean) {
            return; // и цель, и командная строка чистые
        }

        Detection detection;
        detection.path = displayPath;
        detection.sha256 = entry.targetPath.empty() ? std::string() : HashTargetForReport(entry.targetPath);
        detection.verdict = verdict;
        detection.ruleId = L"PERSISTENCE";
        detection.description = targetVerdict != Verdict::Clean
            ? std::wstring(L"Autostart entry correlated with ") + VerdictToWide(targetVerdict) + L" target"
            : std::wstring(L"Suspicious autostart command");
        detection.score = score;
        detection.signals = signals;
        detection.source = L"persistence";
        events_.DetectionEvent(detection);
        detections.push_back(detection);
        stats.detections++;
    },
        // Намеренный пропуск записи автозапуска (глубина, reparse, oversized
        // registry value, битый XML задачи) — потеря покрытия (аудит 2, п. 7/11/12).
        [this, &stats](const std::wstring& path, const wchar_t* reason) {
            stats.filesSkipped++;
            events_.Warning(L"persistence skipped (" + std::wstring(reason) + L"): " + path);
        });
}

void Scanner::ScanProcesses(ScanStats& stats, std::vector<Detection>& detections)
{
    // Снапшот процессов: недоступность — техническая ошибка скана, а не
    // «процессов нет» (аудит 2, п. 6).
    const bool enumerated = ProcessScanner::Enumerate([&](const ProcessInfo& process) {
        if (IsCancelled()) {
            return;
        }

        // Пустой exePath (OpenProcess/QueryFullProcessImageNameW fail) — объект
        // не проверен: coverage skip, а не молчаливое «не найдено».
        if (process.exePath.empty()) {
            stats.filesSkipped++;
            wchar_t pidText[16];
            swprintf_s(pidText, L"%lu", process.pid);
            events_.Warning(L"process path unavailable (PID " + std::wstring(pidText) + L"): "
                            + process.name);
            return;
        }

        std::string sha256;
        if (!Sha256::HashFile(process.exePath, sha256)) {
            stats.errors++;
            events_.Warning(L"process hash failed: " + process.exePath);
            return;
        }

        // В срезе детект — только hash-DB (п. 18: подписи/пути недостаточны).
        Verdict dbVerdict;
        std::wstring dbName;
        if (!database_.Lookup(sha256, dbVerdict, dbName)
            || dbVerdict != Verdict::Malware) {
            return;
        }

        wchar_t pidText[16];
        swprintf_s(pidText, L"%lu", process.pid);

        Detection detection;
        detection.path = process.exePath;
        detection.sha256 = sha256;
        detection.verdict = Verdict::Malware;
        detection.ruleId = L"HASH-DB";
        detection.description = L"Malicious process: " + dbName + L" (PID " + pidText + L")";
        detection.source = L"process";
        events_.DetectionEvent(detection);
        detections.push_back(detection);
        stats.detections++;
    });

    if (!enumerated) {
        stats.errors++;
        events_.Warning(L"process snapshot unavailable");
    }
}

} // namespace scan
