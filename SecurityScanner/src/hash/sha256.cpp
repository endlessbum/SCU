#include "hash/sha256.h"

#include <windows.h>
#include <bcrypt.h>

#include <array>
#include <vector>

// ntstatus.h не подключаем (конфликт макросов с windows.h); нужные коды
// повторяем локально — значения фиксированы ABI.
#ifndef STATUS_SUCCESS
#define STATUS_SUCCESS ((NTSTATUS)0x00000000L)
#endif
#ifndef STATUS_UNSUCCESSFUL
#define STATUS_UNSUCCESSFUL ((NTSTATUS)0xC0000001L)
#endif

#pragma comment(lib, "bcrypt.lib")

namespace scan {

namespace {

constexpr DWORD kChunkSize = 1024 * 1024;

bool ToHex(const std::vector<unsigned char>& hash, std::string& hexOut)
{
    static const char kDigits[] = "0123456789abcdef";
    hexOut.clear();
    hexOut.reserve(hash.size() * 2);
    for (const unsigned char byte : hash) {
        hexOut += kDigits[byte >> 4];
        hexOut += kDigits[byte & 0x0F];
    }
    return true;
}

} // namespace

bool Sha256::HashFile(const std::wstring& path, std::string& hexOut)
{
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != STATUS_SUCCESS) {
        return false;
    }

    BCRYPT_HASH_HANDLE hash = nullptr;
    std::vector<unsigned char> hashObject;
    DWORD hashObjectSize = 0;
    DWORD hashSize = 0;
    DWORD bytesReturned = 0;
    NTSTATUS status = STATUS_UNSUCCESSFUL;
    HANDLE file = INVALID_HANDLE_VALUE;
    bool ok = false;
    std::vector<unsigned char> digest;

    do {
        status = BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
                                   reinterpret_cast<PUCHAR>(&hashObjectSize),
                                   sizeof(hashObjectSize), &bytesReturned, 0);
        if (status != STATUS_SUCCESS) {
            break;
        }
        status = BCryptGetProperty(algorithm, BCRYPT_HASH_LENGTH,
                                   reinterpret_cast<PUCHAR>(&hashSize),
                                   sizeof(hashSize), &bytesReturned, 0);
        if (status != STATUS_SUCCESS) {
            break;
        }

        hashObject.resize(hashObjectSize);
        status = BCryptCreateHash(algorithm, &hash, hashObject.data(),
                                  hashObjectSize, nullptr, 0, 0);
        if (status != STATUS_SUCCESS) {
            break;
        }

        file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) {
            break;
        }

        std::vector<unsigned char> chunk(kChunkSize);
        while (true) {
            DWORD read = 0;
            if (!ReadFile(file, chunk.data(), kChunkSize, &read, nullptr)) {
                break;
            }
            if (read == 0) {
                ok = true;
                break;
            }
            status = BCryptHashData(hash, chunk.data(), read, 0);
            if (status != STATUS_SUCCESS) {
                ok = false;
                break;
            }
        }
        if (!ok) {
            break;
        }

        digest.resize(hashSize);
        status = BCryptFinishHash(hash, digest.data(), hashSize, 0);
        if (status != STATUS_SUCCESS) {
            ok = false;
            break;
        }

        ok = ToHex(digest, hexOut);
    } while (false);

    if (file != INVALID_HANDLE_VALUE) {
        CloseHandle(file);
    }
    if (hash != nullptr) {
        BCryptDestroyHash(hash);
    }
    if (algorithm != nullptr) {
        BCryptCloseAlgorithmProvider(algorithm, 0);
    }
    return ok;
}

} // namespace scan
