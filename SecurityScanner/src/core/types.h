#pragma once

// Общие типы сканера. Соответствуют DTO на стороне C# (Models/Scan/*) и
// протоколу событий stdout (документ п. 4/39/44).

#include <string>
#include <vector>

namespace scan {

enum class Verdict {
    Clean,
    Suspicious,
    Malware,
    Error, // файл не просканирован (ошибка != Clean, документ п. 36)
};

struct Detection {
    std::wstring path;       // real path, virtual path для члена архива или location\name для persistence
    std::string sha256;      // hex, lowercase
    Verdict verdict = Verdict::Clean;
    std::wstring ruleId;     // e.g. "HASH-DB", "PE-HEURISTIC", "SCRIPT-HEURISTIC", "PERSISTENCE"
    std::wstring description;
    bool signedFile = false;
    std::wstring publisher;
    int score = 0;
    std::vector<std::wstring> signals;
    // Для члена архива: контейнер на диске — туда идёт карантин по умолчанию
    // (документ п. 20: изолируется исходный архив целиком).
    std::wstring containerPath;
    bool isVirtual = false;
    // Источник обнаружения: "file" | "persistence" | "process" (п. 17/18).
    std::wstring source = L"file";
};

struct ScanStats {
    unsigned long long filesScanned = 0;
    unsigned long long filesSkipped = 0;
    unsigned long long errors = 0;
    unsigned long long detections = 0;
};

enum class ScanMode { File, Custom };

struct ScanOptions {
    ScanMode mode = ScanMode::File;
    std::wstring path;
    unsigned long long maxFileSize = 100ull * 1024 * 1024; // документ п. 4
    bool scanArchives = true;     // извлечение и скан членов ZIP (п. 20)
    bool scanScripts = true;      // статический анализ скриптов (п. 19)
    bool scanPersistence = true;  // автозапуск: Run, службы, задачи (п. 17)
    bool scanProcesses = true;    // процессы по hash-DB (п. 18)
    uint32_t workerThreads = 0;   // 0 = auto (п. 26: фиксированный thread pool)
    bool useDiskCache = true;     // дисковый кэш вердиктов (п. 25)
    bool devUnsignedOk = false; // warn-only integrity в dev-сборках до code signing
};

// Версия движка — единая точка для событий и заголовка дискового кэша.
inline constexpr const char* kEngineVersion = "0.1.0";

// Версия эвристик: изменение порогов/сигналов PE/скриптового анализа должно
// сопровождаться подъёмом версии — иначе дисковый кэш вернёт устаревшие
// вердикты, посчитанные старыми эвристиками.
inline constexpr const char* kHeuristicVersion = "1";

const char* VerdictToString(Verdict verdict);

} // namespace scan
