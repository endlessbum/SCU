#include "pe/signature.h"

#include <windows.h>
#include <softpub.h>
#include <wincrypt.h>
#include <wintrust.h>
#include <mscat.h>

#include <vector>

#pragma comment(lib, "wintrust.lib")
#pragma comment(lib, "crypt32.lib")

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

} // namespace scan
