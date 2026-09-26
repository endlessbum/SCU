#include "core/database_package.h"

#include <windows.h>
#include <bcrypt.h>

#include "miniz.h"

#include <cstring>
#include <string>
#include <unordered_set>
#include <vector>

#pragma comment(lib, "bcrypt.lib")

// ntstatus.h не подключаем (конфликт макросов с windows.h); значения — ABI.
#ifndef STATUS_SUCCESS
#define STATUS_SUCCESS ((NTSTATUS)0x00000000L)
#endif

namespace scan {

namespace {

// Публичный ключ издателя базы (ECDSA P-256, X||Y, 64 байта).
// Сгенерирован tools/database-builder (dotnet run DatabaseBuilder.cs -- genkeys).
constexpr const char* kDatabasePublicKeyXYHex =
    "acc96c01195827e2254b516ce8c171cd1c2984422e30c8bafa46f6e6f0b655698"
    "08d027d5a96143f3253b4cf89db9fca142036e9ad12443e1e5a4904743b406a";

constexpr size_t kMaxPackageBytes = 10ull * 1024 * 1024;
constexpr size_t kMaxHashesBytes = 4ull * 1024 * 1024;
// Строгая схема hashes.txt (п. 7 аудита): одна строка =
// 64 hex-символа TAB verdict TAB name LF. Любая malformed-строка отклоняет
// ВСЮ базу — частично применённая база хуже отсутствующей.
constexpr size_t kSha256HexLength = 64;
constexpr size_t kMaxNameBytes = 256;
constexpr size_t kMaxLineBytes = kSha256HexLength + 1 + 9 + 1 + kMaxNameBytes + 1;

bool IsLowerHex(const std::string& text)
{
    if (text.size() != kSha256HexLength) {
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

// UTF-8 валидация (имя из базы попадает в UI): rejects overlong, surrogates,
// обрубленные последовательности.
bool IsValidUtf8(const std::string& text)
{
    size_t i = 0;
    while (i < text.size()) {
        const unsigned char c = static_cast<unsigned char>(text[i]);
        size_t extra = 0;
        unsigned int codepoint = 0;
        if (c < 0x80) {
            ++i;
            continue;
        } else if ((c & 0xE0) == 0xC0) {
            extra = 1;
            codepoint = c & 0x1Fu;
        } else if ((c & 0xF0) == 0xE0) {
            extra = 2;
            codepoint = c & 0x0Fu;
        } else if ((c & 0xF8) == 0xF0) {
            extra = 3;
            codepoint = c & 0x07u;
        } else {
            return false;
        }
        if (text.size() - i - 1 < extra) {
            return false;
        }
        for (size_t k = 1; k <= extra; ++k) {
            const unsigned char cc = static_cast<unsigned char>(text[i + k]);
            if ((cc & 0xC0) != 0x80) {
                return false;
            }
            codepoint = (codepoint << 6) | (cc & 0x3Fu);
        }
        // Overlong и суррогаты — не UTF-8.
        if ((extra == 1 && codepoint < 0x80)
            || (extra == 2 && codepoint < 0x800)
            || (extra == 3 && codepoint < 0x10000)
            || (codepoint >= 0xD800 && codepoint <= 0xDFFF)
            || codepoint > 0x10FFFF) {
            return false;
        }
        i += extra + 1;
    }
    return true;
}

// Полная проверка схемы; при ошибке error заполняется и entries — нет.
bool ValidateHashesSchema(const std::vector<unsigned char>& hashes, int& entries, std::string& error)
{
    entries = 0;
    bool sawComment = false;
    std::unordered_set<std::string> seen;

    const std::string content(reinterpret_cast<const char*>(hashes.data()), hashes.size());
    size_t position = 0;
    int lineNumber = 0;
    while (position <= content.size()) {
        const size_t newline = content.find('\n', position);
        const std::string line = content.substr(position,
            newline == std::string::npos ? std::string::npos : newline - position);
        ++lineNumber;
        position = newline == std::string::npos ? content.size() + 1 : newline + 1;
        if (position > content.size() && line.empty() && lineNumber > 1) {
            break; // хвостовой перевод строки — норма
        }

        if (line.empty() || line[0] == '#') {
            sawComment = sawComment || line[0] == '#';
            if (newline == std::string::npos) {
                break;
            }
            continue;
        }

        if (line.size() > kMaxLineBytes) {
            error = "line " + std::to_string(lineNumber) + ": too long";
            return false;
        }
        if (line.find('\r') != std::string::npos) {
            error = "line " + std::to_string(lineNumber) + ": CR is not allowed (LF-only)";
            return false;
        }

        size_t firstTab = line.find('\t');
        size_t secondTab = firstTab == std::string::npos ? std::string::npos : line.find('\t', firstTab + 1);
        if (firstTab == std::string::npos || secondTab == std::string::npos) {
            error = "line " + std::to_string(lineNumber) + ": expected hash<TAB>verdict<TAB>name";
            return false;
        }
        if (line.find('\t', secondTab + 1) != std::string::npos) {
            error = "line " + std::to_string(lineNumber) + ": extra fields";
            return false;
        }

        const std::string hash = line.substr(0, firstTab);
        const std::string verdict = line.substr(firstTab + 1, secondTab - firstTab - 1);
        const std::string name = line.substr(secondTab + 1);

        if (!IsLowerHex(hash)) {
            error = "line " + std::to_string(lineNumber) + ": hash must be 64 lowercase hex chars";
            return false;
        }
        if (verdict != "malware" && verdict != "suspicious") {
            error = "line " + std::to_string(lineNumber) + ": invalid verdict '" + verdict + "'";
            return false;
        }
        if (name.empty() || name.size() > kMaxNameBytes) {
            error = "line " + std::to_string(lineNumber) + ": invalid name size";
            return false;
        }
        if (!IsValidUtf8(name)) {
            error = "line " + std::to_string(lineNumber) + ": name is not valid UTF-8";
            return false;
        }
        if (!seen.insert(hash).second) {
            error = "line " + std::to_string(lineNumber) + ": duplicate hash";
            return false;
        }

        ++entries;
        if (newline == std::string::npos) {
            break;
        }
    }

    // База только из комментариев — легитимный сид (EICAR встроен в сканер).
    // Полностью пустой/обрубленный файл — ошибка.
    if (entries == 0 && !sawComment) {
        error = "no entries";
        return false;
    }
    return true;
}

std::vector<unsigned char> Unhex(const char* hex, size_t byteCount)
{
    std::vector<unsigned char> bytes(byteCount);
    for (size_t i = 0; i < byteCount; ++i) {
        char hi = hex[i * 2];
        char lo = hex[i * 2 + 1];
        auto value = [](char c) -> int {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return -1;
        };
        const int high = value(hi);
        const int low = value(lo);
        if (high < 0 || low < 0) {
            return {};
        }
        bytes[i] = static_cast<unsigned char>((high << 4) | low);
    }
    return bytes;
}

bool HashBuffer(const unsigned char* data, size_t size, std::vector<unsigned char>& digest)
{
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != STATUS_SUCCESS) {
        return false;
    }

    BCRYPT_HASH_HANDLE hash = nullptr;
    std::vector<unsigned char> hashObject(1024);
    NTSTATUS status = BCryptCreateHash(algorithm, &hash, hashObject.data(),
                                       static_cast<ULONG>(hashObject.size()), nullptr, 0, 0);
    bool ok = false;
    if (status == STATUS_SUCCESS) {
        status = BCryptHashData(hash, const_cast<PUCHAR>(data), static_cast<ULONG>(size), 0);
        if (status == STATUS_SUCCESS) {
            digest.resize(32);
            status = BCryptFinishHash(hash, digest.data(), 32, 0);
            ok = status == STATUS_SUCCESS;
        }
        BCryptDestroyHash(hash);
    }
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return ok;
}

// ECDSA P-256: публичный ключ из X||Y собирается в BCRYPT_ECCPUBLIC_BLOB.
bool VerifyEcdsa(const std::vector<unsigned char>& xy,
                 const std::vector<unsigned char>& digest,
                 const std::vector<unsigned char>& signature)
{
    if (xy.size() != 64 || digest.size() != 32 || signature.size() != 64) {
        return false;
    }

#pragma pack(push, 1)
    struct EccPublicBlob {
        ULONG magic;
        ULONG cbKey;
        unsigned char key[64];
    };
#pragma pack(pop)

    EccPublicBlob blob{};
    blob.magic = BCRYPT_ECDSA_PUBLIC_P256_MAGIC;
    blob.cbKey = 32;
    memcpy(blob.key, xy.data(), 64);

    BCRYPT_ALG_HANDLE algorithm = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_ECDSA_ALGORITHM, nullptr, 0) != STATUS_SUCCESS) {
        return false;
    }

    BCRYPT_KEY_HANDLE key = nullptr;
    NTSTATUS status = BCryptImportKeyPair(algorithm, nullptr, BCRYPT_ECCPUBLIC_BLOB,
                                          &key, reinterpret_cast<PUCHAR>(&blob), sizeof(blob), 0);
    bool ok = false;
    if (status == STATUS_SUCCESS) {
        status = BCryptVerifySignature(key, nullptr,
                                       const_cast<PUCHAR>(digest.data()), static_cast<ULONG>(digest.size()),
                                       const_cast<PUCHAR>(signature.data()), static_cast<ULONG>(signature.size()), 0);
        ok = status == STATUS_SUCCESS;
        BCryptDestroyKey(key);
    }
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return ok;
}

// Извлечение члена пакета по имени из уже загруженного ZIP-буфера.
std::vector<unsigned char> ReadZipMember(const mz_zip_archive& zip, const char* name, bool& found)
{
    found = false;
    const int index = mz_zip_reader_locate_file(const_cast<mz_zip_archive*>(&zip), name, nullptr, 0);
    if (index < 0) {
        return {};
    }
    size_t size = 0;
    void* data = mz_zip_reader_extract_to_heap(const_cast<mz_zip_archive*>(&zip),
                                               static_cast<mz_uint>(index), &size, 0);
    if (data == nullptr) {
        return {};
    }
    found = true;
    std::vector<unsigned char> bytes(static_cast<const unsigned char*>(data),
                                     static_cast<const unsigned char*>(data) + size);
    mz_free(data);
    return bytes;
}

// Мини-парсер db-version.json: значения полей version/date в двойных кавычках.
std::wstring ExtractJsonString(const std::string& json, const char* field)
{
    const std::string key = std::string("\"") + field + "\"";
    const size_t keyPos = json.find(key);
    if (keyPos == std::string::npos) {
        return {};
    }
    const size_t colon = json.find(':', keyPos + key.size());
    if (colon == std::string::npos) {
        return {};
    }
    const size_t open = json.find('"', colon + 1);
    if (open == std::string::npos) {
        return {};
    }
    const size_t close = json.find('"', open + 1);
    if (close == std::string::npos) {
        return {};
    }

    const std::string value = json.substr(open + 1, close - open - 1);
    const int wideLength = MultiByteToWideChar(CP_UTF8, 0, value.c_str(),
                                               static_cast<int>(value.size()), nullptr, 0);
    std::wstring wide(static_cast<size_t>(wideLength), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), wide.data(), wideLength);
    return wide;
}

// Atomic replace: содержимое → .tmp в том же каталоге → MoveFileEx REPLACE_EXISTING.
bool AtomicWrite(const std::wstring& targetPath, const std::vector<unsigned char>& content)
{
    const std::wstring tempPath = targetPath + L".tmp";
    HANDLE file = CreateFileW(tempPath.c_str(), GENERIC_WRITE, 0,
                              nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }
    DWORD written = 0;
    const bool writeOk = WriteFile(file, content.data(), static_cast<DWORD>(content.size()), &written, nullptr)
                         && written == content.size();
    CloseHandle(file);
    if (!writeOk) {
        DeleteFileW(tempPath.c_str());
        return false;
    }
    if (!MoveFileExW(tempPath.c_str(), targetPath.c_str(), MOVEFILE_REPLACE_EXISTING)) {
        DeleteFileW(tempPath.c_str());
        return false;
    }
    return true;
}

} // namespace

void DatabasePackage::Apply(const std::wstring& packageZip,
                            const std::wstring& databaseDir,
                            DatabaseUpdateResult& result)
{
    // 1. Пакет читается в память (жёсткий лимит размера).
    HANDLE file = CreateFileW(packageZip.c_str(), GENERIC_READ,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        result.error = L"package not found";
        return;
    }
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0
        || static_cast<unsigned long long>(size.QuadPart) > kMaxPackageBytes) {
        CloseHandle(file);
        result.error = L"package too large or unreadable";
        return;
    }
    std::vector<unsigned char> packageBytes(static_cast<size_t>(size.QuadPart));
    DWORD read = 0;
    const bool readOk = ReadFile(file, packageBytes.data(), static_cast<DWORD>(packageBytes.size()), &read, nullptr)
                        && read == packageBytes.size();
    CloseHandle(file);
    if (!readOk) {
        result.error = L"package read failed";
        return;
    }

    // 2. Распаковка членов пакета.
    mz_zip_archive zip{};
    if (!mz_zip_reader_init_mem(&zip, packageBytes.data(), packageBytes.size(), 0)) {
        result.error = L"not a valid package (zip)";
        return;
    }

    bool found = false;
    const std::vector<unsigned char> hashes = ReadZipMember(zip, "hashes.txt", found);
    if (!found) {
        mz_zip_reader_end(&zip);
        result.error = L"package is missing hashes.txt";
        return;
    }
    if (hashes.size() > kMaxHashesBytes) {
        mz_zip_reader_end(&zip);
        result.error = L"hashes.txt too large";
        return;
    }

    const std::vector<unsigned char> signature = ReadZipMember(zip, "hashes.txt.sig", found);
    mz_zip_reader_end(&zip);
    if (!found) {
        result.error = L"package is missing hashes.txt.sig";
        return;
    }

    // 3. Верификация подписи (п. 33): SHA-256(hashes.txt) + ECDSA P-256.
    std::vector<unsigned char> digest;
    if (!HashBuffer(hashes.data(), hashes.size(), digest)) {
        result.error = L"hash computation failed";
        return;
    }
    if (!VerifyEcdsa(Unhex(kDatabasePublicKeyXYHex, 64), digest, signature)) {
        result.error = L"signature verification failed";
        return;
    }

    // 4. Строгая schema check (п. 7 аудита): каждая строка — 64 lowercase hex
    //    TAB verdict TAB name LF. Malformed запись отклоняет ВСЮ базу.
    std::string schemaError;
    int entries = 0;
    if (!ValidateHashesSchema(hashes, entries, schemaError)) {
        result.error = L"hashes.txt rejected: " + std::wstring(schemaError.begin(), schemaError.end());
        return;
    }
    result.entries = entries;

    // 5. Atomic replace (п. 12).
    if (!AtomicWrite(databaseDir + L"\\hashes.txt", hashes)) {
        result.error = L"failed to write hashes.txt";
        return;
    }

    // db-version.json копируется best-effort (подписан сам hashes.txt).
    mz_zip_archive zip2{};
    if (mz_zip_reader_init_mem(&zip2, packageBytes.data(), packageBytes.size(), 0)) {
        const std::vector<unsigned char> version = ReadZipMember(zip2, "db-version.json", found);
        mz_zip_reader_end(&zip2);
        if (found) {
            AtomicWrite(databaseDir + L"\\db-version.json", version);
            result.dbVersion = ExtractJsonString(
                std::string(version.begin(), version.end()), "version");
        }
    }

    result.ok = true;
}

} // namespace scan
