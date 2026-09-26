#include "core/database.h"

#include "core/types.h"

#include <windows.h>

#include <cctype>
#include <fstream>
#include <sstream>

namespace scan {

namespace {

// SHA-256 стандартной EICAR-тестовой строки (публично опубликованный
// справочный хеш, документ п. 40).
constexpr const char* kEicarSha256 = "275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f";

std::string ToLower(std::string value)
{
    for (char& c : value) {
        c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
    return value;
}

} // namespace

HashDatabase::HashDatabase()
{
    entries_[kEicarSha256] = {Verdict::Malware, L"EICAR-Test-File"};

    // Опциональная внешняя база рядом с exe (offline-установка пакета, п. 31).
    wchar_t pathBuffer[MAX_PATH]{};
    if (GetModuleFileNameW(nullptr, pathBuffer, MAX_PATH) == 0) {
        return;
    }
    std::wstring directory(pathBuffer);
    const size_t slash = directory.find_last_of(L'\\');
    if (slash == std::wstring::npos) {
        return;
    }
    databaseDir_ = directory.substr(0, slash) + L"\\security\\database";

    std::ifstream file(databaseDir_ + L"\\hashes.txt");
    if (file.is_open()) {
        LoadEntries(file);
    }
    LoadVersionInfo();
}

void HashDatabase::LoadVersionInfo()
{
    std::ifstream file(databaseDir_ + L"\\db-version.json");
    if (!file.is_open()) {
        return;
    }
    std::ostringstream buffer;
    buffer << file.rdbuf();
    const std::string json = buffer.str();

    // Мини-парсер: значение поля в двойных кавычках (формат пакета фиксирован).
    auto extract = [&json](const char* field) -> std::wstring {
        const std::string key = std::string("\"") + field + "\"";
        const size_t keyPos = json.find(key);
        if (keyPos == std::string::npos) {
            return {};
        }
        const size_t colon = json.find(':', keyPos + key.size());
        const size_t open = colon == std::string::npos ? std::string::npos : json.find('"', colon);
        const size_t close = open == std::string::npos ? std::string::npos : json.find('"', open + 1);
        if (open == std::string::npos || close == std::string::npos) {
            return {};
        }
        const std::string value = json.substr(open + 1, close - open - 1);
        return std::wstring(value.begin(), value.end());
    };
    version_ = extract("version");
    date_ = extract("date");
}

void HashDatabase::LoadEntries(std::ifstream& file)
{
    std::string line;
    while (std::getline(file, line)) {
        if (line.empty() || line[0] == '#') {
            continue;
        }
        std::istringstream stream(line);
        std::string hash;
        std::string verdictText;
        std::string name;
        if (!std::getline(stream, hash, '\t') || !std::getline(stream, verdictText, '\t')) {
            continue;
        }
        std::getline(stream, name);

        const Verdict verdict = verdictText == "malware" ? Verdict::Malware
                                : verdictText == "suspicious" ? Verdict::Suspicious
                                                              : Verdict::Clean;
        if (verdict == Verdict::Clean) {
            continue;
        }
        entries_[ToLower(hash)] = {verdict, std::wstring(name.begin(), name.end())};
    }
}

bool HashDatabase::Lookup(const std::string& sha256Hex, Verdict& verdict, std::wstring& name) const
{
    const auto it = entries_.find(ToLower(sha256Hex));
    if (it == entries_.end()) {
        return false;
    }
    verdict = it->second.verdict;
    name = it->second.name;
    return true;
}

} // namespace scan
