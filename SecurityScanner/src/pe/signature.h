#pragma once

// Обобщённая проверка цифровой подписи (документ п. 8) — аналог
// Common/SignatureVerifier.cs, но без фильтра по издателю:
// WinVerifyTrust (GENERIC_VERIFY_V2) + извлечение имени подписавшего
// из сертификата подписчика через crypt32.
// Fail-closed: если WinVerifyTrust недоступен, файл считается неподписанным
// (fallback на цепочку сертификата намеренно запрещён — как и в SCU.cs).

#include <string>

namespace scan {

struct SignatureCheck {
    bool trusted = false;    // Authenticode-подпись валидна и доверена
    bool hasSignature = false;
    std::wstring publisher;  // Simple Display Name сертификата подписчика
};

class SignatureVerifier {
public:
    static SignatureCheck Verify(const std::wstring& path);

    // Отпечаток сертификата подписчика (CERT_HASH_PROP_ID, SHA-1, uppercase hex).
    // Для пиннинга целостности ScannerCore: не требует доверенного корня —
    // точное совпадение отпечатка доказывает неизменность подписи.
    // false, если подписи нет или сертификат не извлечён.
    static bool GetSigningCertHash(const std::wstring& path, std::string& sha1ThumbprintUpperHex);
};

} // namespace scan
