#include "core/database.h"

#include "core/database_package.h"
#include "core/path_util.h"
#include "core/types.h"

#include <windows.h>

#include <cctype>
#include <fstream>
#include <sstream>
#include <vector>

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

// Сырые байты файла (для проверки подписи над точным содержимым —
// текстовое чтение через getline нормализовало бы LF/CRLF и сломало дайджест).
bool ReadFileBytes(const std::wstring& path, std::vector<unsigned char>& out)
{
    std::ifstream file(path, std::ios::binary);
    if (!file.is_open()) {
        return false;
    }
    std::ostringstream buffer;
    buffer << file.rdbuf();
    const std::string text = buffer.str();
    out.assign(text.begin(), text.end());
    return true;
}

} // namespace

std::wstring ResolveDatabaseDir(const std::wstring& databaseDir)
{
    std::ifstream current(databaseDir + L"\\current.json");
    if (!current.is_open()) {
        return databaseDir; // нет указателя — легаси-раскладка
    }
    std::ostringstream buffer;
    buffer << current.rdbuf();
    const std::string json = buffer.str();

    // Мини-парсер значения "generation".
    const std::string key = "\"generation\"";
    const size_t keyPos = json.find(key);
    if (keyPos == std::string::npos) {
        return databaseDir;
    }
    const size_t colon = json.find(':', keyPos + key.size());
    const size_t open = colon == std::string::npos ? std::string::npos : json.find('"', colon);
    const size_t close = open == std::string::npos ? std::string::npos : json.find('"', open + 1);
    if (open == std::string::npos || close == std::string::npos) {
        return databaseDir;
    }
    const std::string generation = json.substr(open + 1, close - open - 1);
    if (generation.empty()) {
        return databaseDir;
    }

    // Белый список символов id: подмена указателя на "..\.." не проходит.
    for (const char c : generation) {
        const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                        || (c >= '0' && c <= '9') || c == '-' || c == '_';
        if (!ok) {
            return databaseDir;
        }
    }

    std::wstring wide(generation.begin(), generation.end());
    const std::wstring genDir = databaseDir + L"\\generations\\" + wide;
    if (GetFileAttributesW((genDir + L"\\hashes.txt").c_str()) != INVALID_FILE_ATTRIBUTES) {
        return genDir;
    }
    // Указатель на несуществующую генерацию — fallback на легаси.
    return databaseDir;
}

HashDatabase::HashDatabase(bool devUnsignedOk)
{
    entries_[kEicarSha256] = {Verdict::Malware, L"EICAR-Test-File"};

    // Опциональная внешняя база рядом с exe (offline-установка пакета, п. 31).
    // Динамический путь (аудит п. 9): длинный путь установки не усекается.
    const std::wstring selfPath = GetModulePathDynamic();
    if (selfPath.empty()) {
        return;
    }
    const std::wstring directory = DirectoryOf(selfPath);
    if (directory.empty()) {
        return;
    }
    databaseDir_ = directory + L"\\security\\database";

    // Активная база: генерация из current.json либо легаси-файлы (аудит 2, п. 16).
    const std::wstring activeDir = ResolveDatabaseDir(databaseDir_);

    std::vector<unsigned char> hashes, version, signature;
    if (!ReadFileBytes(activeDir + L"\\hashes.txt", hashes)
        || !ReadFileBytes(activeDir + L"\\db-version.json", version)) {
        // Внешних файлов нет — работает встроенный минимум (как раньше).
        return;
    }

    // Подпись активной базы (OBS-05 п. 3, аудит 2026-11-04): hashes.txt /
    // db-version.json читались с диска без проверки — результат скана зависел
    // от подменяемого состояния. Fail-closed: подписи нет (прод) или она не
    // сошлась — внешние записи не загружаются, сканер сообщает событием error.
    // Dev-сборки без пина (--dev-unsigned-ok) могут грузить базу без подписи.
    if (ReadFileBytes(activeDir + L"\\hashes.txt.sig", signature)) {
        if (!VerifyDatabaseSignature(hashes.data(), hashes.size(),
                                     version.data(), version.size(),
                                     signature.data(), signature.size())) {
            signatureError_ = L"database signature verification failed (hashes.txt.sig)";
            return;
        }
    } else if (!devUnsignedOk) {
        signatureError_ = L"database signature is missing (hashes.txt.sig)";
        return;
    }

    const std::string hashesText(hashes.begin(), hashes.end());
    std::istringstream stream(hashesText);
    LoadEntries(stream);
    LoadVersionInfo(activeDir);
}

void HashDatabase::LoadVersionInfo(const std::wstring& activeDir)
{
    std::ifstream file(activeDir + L"\\db-version.json");
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
        // UTF-8 → wide (имя/дата попадают в UI).
        const int wideLength = MultiByteToWideChar(CP_UTF8, 0, value.c_str(),
                                                   static_cast<int>(value.size()), nullptr, 0);
        if (wideLength <= 0) {
            return {};
        }
        std::wstring wide(static_cast<size_t>(wideLength), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), wide.data(), wideLength);
        return wide;
    };
    version_ = extract("version");
    date_ = extract("date");
}

void HashDatabase::LoadEntries(std::istream& file)
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

        // Аудит: legacy-база валидирует имя слабо — TAB/CR/LF в name рвали
        // TSV-строку дискового кэша (правая часть уезжала в чужие поля).
        // Очистка при загрузке: замена на пробел, имя остаётся читаемым в UI.
        // ('\n' из getline не приходит, проверка — на всякий случай.)
        for (char& c : name) {
            if (c == '\t' || c == '\r' || c == '\n') {
                c = ' ';
            }
        }

        const Verdict verdict = verdictText == "malware" ? Verdict::Malware
                                : verdictText == "suspicious" ? Verdict::Suspicious
                                                              : Verdict::Clean;
        if (verdict == Verdict::Clean) {
            continue;
        }
        // Имя — UTF-8 → wide (раньше байт-в-wchar ломал кириллицу в UI).
        const int wideLength = MultiByteToWideChar(CP_UTF8, 0, name.c_str(),
                                                   static_cast<int>(name.size()), nullptr, 0);
        std::wstring wideName;
        if (wideLength > 0) {
            wideName.resize(static_cast<size_t>(wideLength), L'\0');
            MultiByteToWideChar(CP_UTF8, 0, name.c_str(), static_cast<int>(name.size()), wideName.data(), wideLength);
        }
        entries_[ToLower(hash)] = {verdict, std::move(wideName)};
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
