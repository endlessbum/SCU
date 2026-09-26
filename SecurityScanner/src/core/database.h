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

class HashDatabase {
public:
    HashDatabase();

    bool Lookup(const std::string& sha256Hex, Verdict& verdict, std::wstring& name) const;
    bool IsEmpty() const { return entries_.empty(); }

    // Database freshness (п. 32): версия/дата из db-version.json рядом с hashes.txt.
    const std::wstring& Version() const { return version_; }
    const std::wstring& Date() const { return date_; }

private:
    void LoadVersionInfo();
    void LoadEntries(std::ifstream& file);

    struct Entry {
        Verdict verdict;
        std::wstring name;
    };
    std::unordered_map<std::string, Entry> entries_;
    std::wstring databaseDir_;
    std::wstring version_;
    std::wstring date_;
};

} // namespace scan
