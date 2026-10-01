using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using SCU.Common;

namespace SCU.Common;

// Проверка подписи и издателя Microsoft на нативном .NET.
// Заменяет PS-действие VerifyExe: Get-AuthenticodeSignature в некоторых сессиях
// падал с ошибкой загрузки Microsoft.PowerShell.Security (код 6) без реальной причины.
//
// Целостность файла проверяется через WinVerifyTrust (GENERIC_VERIFY_V2): файл
// с внедрённым чужим (скопированным) сертификатом не проходит — хэш подписи не
// совпадает с содержимым. Раньше проверялась только цепочка сертификата из
// cert-таблицы PE, поэтому PE со скопированным легитимным сертификатом
// «Microsoft Corporation» проходил проверку без приватного ключа.
// Если WinVerifyTrust недоступен, проверка завершается с ошибкой: fallback только на
// цепочку сертификата не доказывает целостность PE и потому здесь намеренно запрещён.
public static class SignatureVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int UntrustedRoot = unchecked((int)0x800B0109);
    private const int UntrustedChain = unchecked((int)0x800B010A);
    private const int UntrustedTestRoot = unchecked((int)0x800B010D);
    private const int UntrustedCa = unchecked((int)0x800B0112);

    public static Result VerifyMicrosoftSigned(string path, long minBytes = 262144)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Result.Failure("Файл не найден: " + path, 2);
        }

        var size = new FileInfo(path).Length;
        if (size < minBytes)
        {
            return Result.Failure($"Файл слишком мал: {size} байт (ожидалось не меньше {minBytes}) — вероятно, скачалась страница ошибки.", 3);
        }

        // 1. Целостность и доверие: WinVerifyTrust одновременно проверяет Authenticode
        // и то, что хэш подписи соответствует текущему содержимому PE. Если провайдер
        // недоступен, доверенная проверка невозможна — fail-closed.
        var trust = VerifyFileTrust(path);
        if (!trust.IsSuccess)
        {
            return Result.Failure($"Проверка подписи не пройдена: {trust.Message}", 4);
        }

        // 2. Издатель: сертификат подписанта должен принадлежать Microsoft Corporation.
        X509Certificate2 certificate;
        try
        {
            // SYSLIB0057: X509CertificateLoader не читает сертификаты, встроенные в PE-файлы
            // (Authenticode-таблица) — только DER/PEM/PKCS#12. Для извлечения подписанта
            // из exe остаётся только устаревший конструктор.
#pragma warning disable SYSLIB0057
            certificate = new X509Certificate2(path);
#pragma warning restore SYSLIB0057
        }
        catch (Exception exception)
        {
            return Result.Failure("Файл не подписан или подпись не читается: " + exception.Message, 6);
        }

        using (certificate)
        {
            var subject = certificate.Subject;
            var simpleName = certificate.GetNameInfo(X509NameType.SimpleName, false);
            if (!subject.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                && !simpleName.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure($"Издатель не Microsoft: {subject}", 5);
            }

            // WinVerifyTrust уже подтвердил Authenticode, хэш и доверенную цепочку.
            return Result.Success($"Microsoft Corporation; size={size}; subject={simpleName}; authenticode ok");
        }
    }

    // Публичная обёртка WinVerifyTrust для pin-проверок собственных бинарей
    // (ScannerCore): подтверждает Authenticode И совпадение хэша подписи с текущим
    // содержимым PE — сертификат, скопированный в подменённый файл, не пройдёт.
    public static Result VerifyAuthenticodeIntegrity(string path) => VerifyFileTrust(path);

    // Коды "подпись валидна, но корень не в доверенных": это единственные
    // отказы WinVerifyTrust, допустимые для пиннутого собственного бинаря.
    // Самоподписанный сертификатrelease-цепочки (документ п. 49) не имеет
    // доверенного корня по определению; C++-сторона pin-режима его принимает
    // (main.cpp: GetSigningCertHash не требует доверенного корня), поэтому и
    // C#-проверка обязана принимать — иначе ScannerCore собирался бы, но
    // отвергался бы GUI (п. SEC-01 аудита). Дайджест-ошибки (TRUST_E_BAD_DIGEST)
    // сюда не входят: подменённый файл со скопированным сертификатом отклоняется.
    private static readonly int[] UntrustedRootOnlyCodes =
    {
        UntrustedRoot,      // CERT_E_UNTRUSTEDROOT 0x800B0109
        UntrustedChain,     // CERT_E_CHAINING     0x800B010A
        UntrustedTestRoot,  // CERT_E_UNTRUSTEDTESTROOT 0x800B010D
        UntrustedCa         // CERT_E_UNTRUSTEDCA  0x800B0112
    };

    // Пин-проверка собственного бинаря: целостность (хэш подписи == содержимому)
    // обязательна всегда; доверенная цепочка — нет (thumbprint сверяет вызывающий
    // код). Возвращает успех и для полностью доверенной подписи, и для
    // self-signed с валидным дайджестом.
    public static Result VerifyPinnedBinaryIntegrity(string path)
    {
        var trust = VerifyFileTrust(path);
        if (trust.IsSuccess)
        {
            return trust;
        }

        if (Array.IndexOf(UntrustedRootOnlyCodes, trust.Code) >= 0)
        {
            return Result.Success("authenticode digest ok; root not trusted (pinned binary)");
        }

        return trust;
    }

    // Authenticode-проверка файла: 0 — подпись действительна и хэш совпадает.
    private static Result VerifyFileTrust(string path)
    {
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };

            var data = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                dwStateAction = WtdStateActionVerify
            };
            data.pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(fileInfo, data.pFile, fDeleteOld: false);

                var actionId = GenericVerifyV2;
                var hr = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);

                // Завершаем сессию проверки, если она была открыта.
                data.dwStateAction = WtdStateActionClose;
                WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);

                return hr switch
                {
                    0 => Result.Success("authenticode ok"),
                    TrustEProviderUnknown => Result.Failure("Провайдер wintrust недоступен.", TrustEProviderUnknown),
                    TrustENoSignature => Result.Failure("Файл не имеет подписи Authenticode.", hr),
                    TrustEBadDigest => Result.Failure("Хэш файла не совпадает с подписью — файл изменён.", hr),
                    _ => Result.Failure($"Ошибка Authenticode 0x{hr:X8} ({hr}).", hr)
                };
            }
            finally
            {
                Marshal.FreeHGlobal(data.pFile);
            }
        }
        catch (Exception exception)
        {
            return Result.Failure("WinVerifyTrust недоступен: " + exception.Message, TrustEProviderUnknown);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hWnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public int cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
