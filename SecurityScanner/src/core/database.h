#pragma once

// Signature database, срез v1 (документ п. 12):
// - встроенный минимум: EICAR (обязательный smoke-тест, документ п. 40);
// - опциональный внешний файл security/database/hashes.txt рядом с exe,
//   формат строки: "sha256<TAB>verdict<TAB>name" (verdict: malware|suspicious).
// Обновления подписанными пакетами — следующая итерация (п. 30/33).

#include <string>

#include <unordered_map>

namespace scan {

enum class Verdict;

// Активный каталог базы (аудит 2, п. 16): generations/<id> из current.json —
// единый атомарный commit-поинтер установки пакета. Файл указателя отсутствует
// или бит → легаси-раскладка (hashes.txt прямо в databaseDir). id из указателя
// валидируется по белому списку символов — подмена на "..\.." не проходит.
std::wstring ResolveDatabaseDir(const std::wstring& databaseDir);

class HashDatabase {
public:
    // devUnsignedOk — только dev-сборки без пина (ScannerRunner передаёт
    // --dev-unsigned-ok исключительно при HasSigningPin == false): разрешает
    // загрузку базы БЕЗ подписи. Подписанная, но не сошедшаяся база отклоняется
    // всегда, включая dev.
    explicit HashDatabase(bool devUnsignedOk = false);

    bool Lookup(const std::string& sha256Hex, Verdict& verdict, std::wstring& name) const;
    bool IsEmpty() const { return entries_.empty(); }

    // Database freshness (п. 32): версия/дата из db-version.json активной базы.
    const std::wstring& Version() const { return version_; }
    const std::wstring& Date() const { return date_; }

    // Непусто, если внешняя база НЕ загружена из-за подписи (fail-closed,
    // OBS-05 п. 3): отсутствует hashes.txt.sig (прод) или подпись не сошлась.
    // Сканер сообщает это событием error; работает только встроенный минимум.
    const std::wstring& SignatureError() const { return signatureError_; }

private:
    void LoadVersionInfo(const std::wstring& activeDir);
    void LoadEntries(std::istream& file);

    struct Entry {
        Verdict verdict;
        std::wstring name;
    };
    std::unordered_map<std::string, Entry> entries_;
    std::wstring databaseDir_;
    std::wstring version_;
    std::wstring date_;
    std::wstring signatureError_;
};

} // namespace scan
