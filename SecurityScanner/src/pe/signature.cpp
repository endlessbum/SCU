#include "pe/signature.h"

#include <windows.h>
#include <bcrypt.h>
#include <softpub.h>
#include <wincrypt.h>
#include <wintrust.h>
#include <mscat.h>

#include <cstring>
#include <utility>
#include <string>
#include <vector>

#pragma comment(lib, "wintrust.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "bcrypt.lib")

#ifndef STATUS_SUCCESS
#define STATUS_SUCCESS ((NTSTATUS)0x00000000L)
#endif

namespace scan {

namespace {

// WinVerifyTrust: структура для проверки файла (WINTRUST_FILE_INFO),
// ревокация по цепочке включена, UI отключён — как в SignatureVerifier.cs.
bool VerifyFileTrust(const std::wstring& path)
{
    WINTRUST_FILE_INFO fileInfo{};
    fileInfo.cbStruct = sizeof(fileInfo);
    fileInfo.pcwszFilePath = path.c_str();

    GUID actionId = WINTRUST_ACTION_GENERIC_VERIFY_V2;

    WINTRUST_DATA trustData{};
    trustData.cbStruct = sizeof(trustData);
    trustData.pPolicyCallbackData = nullptr;
    trustData.pSIPClientData = nullptr;
    trustData.dwUIChoice = WTD_UI_NONE;
    trustData.fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN;
    trustData.dwUnionChoice = WTD_CHOICE_FILE;
    trustData.dwStateAction = WTD_STATEACTION_VERIFY;
    trustData.hWVTStateData = nullptr;
    trustData.pFile = &fileInfo;

    const LONG result = WinVerifyTrust(static_cast<HWND>(INVALID_HANDLE_VALUE), &actionId, &trustData);

    trustData.dwStateAction = WTD_STATEACTION_CLOSE;
    WinVerifyTrust(static_cast<HWND>(INVALID_HANDLE_VALUE), &actionId, &trustData);

    return result == ERROR_SUCCESS;
}

bool HasEmbeddedSignature(const std::wstring& path)
{
    WINTRUST_FILE_INFO fileInfo{};
    fileInfo.cbStruct = sizeof(fileInfo);
    fileInfo.pcwszFilePath = path.c_str();

    GUID actionId = WINTRUST_ACTION_GENERIC_VERIFY_V2;

    WINTRUST_DATA trustData{};
    trustData.cbStruct = sizeof(trustData);
    trustData.dwUIChoice = WTD_UI_NONE;
    trustData.fdwRevocationChecks = WTD_REVOKE_NONE;
    trustData.dwUnionChoice = WTD_CHOICE_FILE;
    trustData.dwStateAction = WTD_STATEACTION_IGNORE;
    trustData.pFile = &fileInfo;

    const LONG result = WinVerifyTrust(static_cast<HWND>(INVALID_HANDLE_VALUE), &actionId, &trustData);
    return result != TRUST_E_NOSIGNATURE;
}

// Открытие сертификата подписчика из Authenticode подписи: общий хелпер для
// ExtractPublisher и GetSigningCertHash.
bool OpenSignerCertificate(const std::wstring& path,
                           HCERTSTORE* storeOut,
                           HCRYPTMSG* msgOut,
                           const CERT_CONTEXT** certOut)
{
    *storeOut = nullptr;
    *msgOut = nullptr;
    *certOut = nullptr;

    if (!CryptQueryObject(CERT_QUERY_OBJECT_FILE, path.c_str(),
                          CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED,
                          CERT_QUERY_FORMAT_FLAG_BINARY, 0,
                          nullptr, nullptr, nullptr,
                          storeOut, msgOut, nullptr)) {
        return false;
    }

    DWORD signerInfoSize = 0;
    if (*msgOut == nullptr
        || !CryptMsgGetParam(*msgOut, CMSG_SIGNER_INFO_PARAM, 0, nullptr, &signerInfoSize)
        || signerInfoSize == 0) {
        return false;
    }

    std::vector<unsigned char> signerInfoBuffer(signerInfoSize);
    if (!CryptMsgGetParam(*msgOut, CMSG_SIGNER_INFO_PARAM, 0,
                          signerInfoBuffer.data(), &signerInfoSize)) {
        return false;
    }

    const auto* signerInfo = reinterpret_cast<const CMSG_SIGNER_INFO*>(signerInfoBuffer.data());

    CERT_INFO certInfo{};
    certInfo.Issuer = signerInfo->Issuer;
    certInfo.SerialNumber = signerInfo->SerialNumber;

    const CERT_CONTEXT* certContext = CertFindCertificateInStore(
        *storeOut, X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, 0,
        CERT_FIND_SUBJECT_CERT, &certInfo, nullptr);
    if (certContext == nullptr) {
        return false;
    }

    *certOut = certContext;
    return true;
}

// Извлечение Simple Display Name сертификата подписчика.
bool ExtractPublisher(const std::wstring& path, std::wstring& publisher)
{
    HCERTSTORE store = nullptr;
    HCRYPTMSG message = nullptr;
    const CERT_CONTEXT* certContext = nullptr;
    bool ok = false;

    if (OpenSignerCertificate(path, &store, &message, &certContext)) {
        DWORD nameSize = CertGetNameStringW(
            certContext, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, nullptr, nullptr, 0);
        if (nameSize > 0) {
            std::vector<wchar_t> nameBuffer(nameSize);
            if (CertGetNameStringW(certContext, CERT_NAME_SIMPLE_DISPLAY_TYPE,
                                   0, nullptr, nameBuffer.data(), nameSize) > 0) {
                publisher = nameBuffer.data();
                ok = true;
            }
        }
        CertFreeCertificateContext(certContext);
    }

    if (message != nullptr) {
        CryptMsgClose(message);
    }
    if (store != nullptr) {
        CertCloseStore(store, 0);
    }
    return ok;
}

// ==== OBS-05 п. 1 (аудит 2026-11-04): дайджест подписи =====================
// Pin-режим сравнивал только отпечаток сертификата из встроенной подписи:
// подмена содержимого при целой подписи проходила (дайджест не проверялся).
// Здесь подпись проверяется целиком: валидность SignedData + совпадение
// MessageDigest из SPC_INDIRECT_DATA с фактическим Authenticode-дайджестом
// файла. Доверенный корень не требуется — работает в pin-режиме на любой машине.

// DER-чтение: однобайтовый тег + короткая/длинная форма длины. Размеры
// Authenticode-контента заведомо малы — длинных форм больше 4 байт не бывает.
struct DerValue {
    unsigned char tag = 0;
    const unsigned char* data = nullptr;
    size_t size = 0;
};

bool DerNext(const unsigned char*& cursor, const unsigned char* end, DerValue& value)
{
    if (cursor + 2 > end) {
        return false;
    }
    value.tag = *cursor++;
    size_t length = *cursor++;
    if ((length & 0x80) != 0) {
        const size_t lengthBytes = length & 0x7F;
        if (lengthBytes == 0 || lengthBytes > 4 || cursor + lengthBytes > end) {
            return false;
        }
        length = 0;
        for (size_t i = 0; i < lengthBytes; ++i) {
            length = (length << 8) | *cursor++;
        }
    }
    if (cursor + length > end) {
        return false;
    }
    value.data = cursor;
    value.size = length;
    cursor += length;
    return true;
}

// OID из DER-содержимого в точечную нотацию.
bool DecodeOid(const unsigned char* data, size_t size, std::string& oid)
{
    if (size == 0) {
        return false;
    }
    const unsigned long first = data[0];
    oid = std::to_string(first / 40) + '.' + std::to_string(first % 40);
    unsigned long value = 0;
    for (size_t i = 1; i < size; ++i) {
        value = (value << 7) | (data[i] & 0x7F);
        if ((data[i] & 0x80) == 0) {
            oid += '.' + std::to_string(value);
            value = 0;
        }
    }
    return true;
}

bool CngAlgorithmFromOid(const std::string& oid, std::wstring& algorithmId)
{
    if (oid == "2.16.840.1.101.3.4.2.1") { algorithmId = L"SHA256"; return true; }
    if (oid == "2.16.840.1.101.3.4.2.2") { algorithmId = L"SHA384"; return true; }
    if (oid == "2.16.840.1.101.3.4.2.3") { algorithmId = L"SHA512"; return true; }
    if (oid == "1.3.14.3.2.26")          { algorithmId = L"SHA1"; return true; }
    return false;
}


// Authenticode-дайджест файла. Считается CryptCATAdminCalcHashFromFileHandle2 —
// тем же API, которым Windows подписывает и верифицирует (обнуление Checksum
// и вырезание Certificate Table учтены внутри). Ручной разбор PE даёт другой
// результат: у алгоритма Windows есть неочевидные нюансы (проверено
// расхождением с `signtool verify /pa /v` на одном и том же файле).
bool ComputeAuthenticodeDigest(const std::wstring& path,
                               const std::wstring& hashAlgorithm,
                               std::vector<unsigned char>& digest)
{
    HCATADMIN admin = nullptr;
    if (!CryptCATAdminAcquireContext2(&admin, nullptr, hashAlgorithm.c_str(), nullptr, 0)) {
        return false;
    }

    bool ok = false;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
                              nullptr, OPEN_EXISTING, 0, nullptr);
    if (file != INVALID_HANDLE_VALUE) {
        DWORD hashSize = 0;
        if (CryptCATAdminCalcHashFromFileHandle2(admin, file, &hashSize, nullptr, 0)
            && hashSize > 0) {
            std::vector<unsigned char> hash(hashSize);
            if (CryptCATAdminCalcHashFromFileHandle2(admin, file, &hashSize, hash.data(), 0)) {
                digest = std::move(hash);
                ok = true;
            }
        }
        CloseHandle(file);
    }

    CryptCATAdminReleaseContext(admin, 0);
    return ok;
}

// MessageDigest + алгоритм из DER SpcIndirectDataContent (CMSG_CONTENT_PARAM):
// SEQUENCE { SEQUENCE { OID, [0] ... }, SEQUENCE { SEQUENCE { OID, params }, OCTET STRING } }
bool ExtractSignedDigest(const std::vector<unsigned char>& content,
                         std::string& digestOid,
                         std::vector<unsigned char>& digest)
{
    const unsigned char* cursor = content.data();
    const unsigned char* end = content.data() + content.size();
    DerValue outer;
    if (!DerNext(cursor, end, outer) || outer.tag != 0x30) {
        return false;
    }

    const unsigned char* child = outer.data;
    const unsigned char* childEnd = outer.data + outer.size;
    DerValue dataElement, digestInfo;
    if (!DerNext(child, childEnd, dataElement) || dataElement.tag != 0x30) {
        return false;
    }
    if (!DerNext(child, childEnd, digestInfo) || digestInfo.tag != 0x30) {
        return false;
    }

    const unsigned char* inner = digestInfo.data;
    const unsigned char* innerEnd = digestInfo.data + digestInfo.size;
    DerValue algorithmIdentifier, octetString;
    if (!DerNext(inner, innerEnd, algorithmIdentifier) || algorithmIdentifier.tag != 0x30) {
        return false;
    }
    if (!DerNext(inner, innerEnd, octetString) || octetString.tag != 0x04 || octetString.size == 0) {
        return false;
    }

    const unsigned char* alg = algorithmIdentifier.data;
    const unsigned char* algEnd = algorithmIdentifier.data + algorithmIdentifier.size;
    DerValue oidValue;
    if (!DerNext(alg, algEnd, oidValue) || oidValue.tag != 0x06) {
        return false;
    }
    if (!DecodeOid(oidValue.data, oidValue.size, digestOid)) {
        return false;
    }

    digest.assign(octetString.data, octetString.data + octetString.size);
    return true;
}

} // namespace

bool SignatureVerifier::GetSigningCertHash(const std::wstring& path, std::string& sha1ThumbprintUpperHex)
{
    HCERTSTORE store = nullptr;
    HCRYPTMSG message = nullptr;
    const CERT_CONTEXT* certContext = nullptr;
    bool ok = false;

    if (OpenSignerCertificate(path, &store, &message, &certContext)) {
        DWORD hashSize = 0;
        if (CertGetCertificateContextProperty(certContext, CERT_HASH_PROP_ID, nullptr, &hashSize)
            && hashSize > 0) {
            std::vector<unsigned char> hash(hashSize);
            if (CertGetCertificateContextProperty(certContext, CERT_HASH_PROP_ID, hash.data(), &hashSize)) {
                static const char kDigits[] = "0123456789ABCDEF";
                sha1ThumbprintUpperHex.clear();
                sha1ThumbprintUpperHex.reserve(hash.size() * 2);
                for (const unsigned char byte : hash) {
                    sha1ThumbprintUpperHex += kDigits[byte >> 4];
                    sha1ThumbprintUpperHex += kDigits[byte & 0x0F];
                }
                ok = true;
            }
        }
        CertFreeCertificateContext(certContext);
    }

    if (message != nullptr) {
        CryptMsgClose(message);
    }
    if (store != nullptr) {
        CertCloseStore(store, 0);
    }
    return ok;
}

SignatureCheck SignatureVerifier::Verify(const std::wstring& path)
{
    SignatureCheck check;
    check.hasSignature = HasEmbeddedSignature(path);
    if (!check.hasSignature) {
        return check;
    }

    // Только полная проверка Authenticode (хеш + цепочка + ревокация).
    // Успешная расшифровка сертификата без валидной подписи не считается.
    if (VerifyFileTrust(path)) {
        check.trusted = true;
        ExtractPublisher(path, check.publisher);
    }
    return check;
}

// Полная проверка встроенной подписи без доверенного корня: подпись SignedData
// валидна (ключ сертификата подписчика) И MessageDigest из SPC_INDIRECT_DATA
// совпадает с фактическим Authenticode-дайджестом файла. false — подписи нет,
// она битая или содержимое не соответствует подписи (fail-closed).
bool SignatureVerifier::VerifyEmbeddedDigest(const std::wstring& path)
{
    HCERTSTORE store = nullptr;
    HCRYPTMSG message = nullptr;
    const CERT_CONTEXT* certContext = nullptr;
    bool ok = false;

    if (OpenSignerCertificate(path, &store, &message, &certContext)) {
        do {
            // 1. Подпись SignedData действительно сделана ключом этого сертификата.
            CMSG_CTRL_VERIFY_SIGNATURE_EX_PARA verifyParams{};
            verifyParams.cbSize = sizeof(verifyParams);
            verifyParams.dwSignerIndex = 0;
            verifyParams.dwSignerType = CMSG_VERIFY_SIGNER_CERT;
            verifyParams.pvSigner = const_cast<CERT_CONTEXT*>(certContext);
            if (CryptMsgControl(message, 0, CMSG_CTRL_VERIFY_SIGNATURE_EX, &verifyParams) != TRUE) {
                break;
            }

            // 2. MessageDigest + алгоритм из подписанного содержимого.
            DWORD contentSize = 0;
            if (!CryptMsgGetParam(message, CMSG_CONTENT_PARAM, 0, nullptr, &contentSize)
                || contentSize == 0) {
                break;
            }
            std::vector<unsigned char> content(contentSize);
            if (!CryptMsgGetParam(message, CMSG_CONTENT_PARAM, 0, content.data(), &contentSize)) {
                break;
            }
            std::string digestOid;
            std::vector<unsigned char> expectedDigest;
            if (!ExtractSignedDigest(content, digestOid, expectedDigest) || expectedDigest.empty()) {
                break;
            }
            std::wstring hashAlgorithm;
            if (!CngAlgorithmFromOid(digestOid, hashAlgorithm)) {
                break;
            }

            // 3. Фактический Authenticode-дайджест текущих байтов файла.
            std::vector<unsigned char> actualDigest;
            if (!ComputeAuthenticodeDigest(path, hashAlgorithm, actualDigest)) {
                break;
            }
            ok = actualDigest == expectedDigest;
        } while (false);
        CertFreeCertificateContext(certContext);
    }

    if (message != nullptr) {
        CryptMsgClose(message);
    }
    if (store != nullptr) {
        CertCloseStore(store, 0);
    }
    return ok;
}

} // namespace scan
