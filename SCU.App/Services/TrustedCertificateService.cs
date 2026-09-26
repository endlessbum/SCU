using System.Security.Cryptography.X509Certificates;
using SCU.Common;

namespace SCU.Services;

// Корневые сертификаты Минцифры («Russian Trusted Root CA» и подчинённый
// «Russian Trusted Subordinate CA»). Оба кладутся в хранилища «Доверенные
// корневые центры сертификации» (Root) и «Промежуточные центры сертификации»
// (CA) — те же вкладки, что показывает certmgr.msc. Наличие проверяется сразу
// при открытии вкладки «Сеть», удаление выполняется из всех хранилищ сразу.
public sealed class TrustedCertificateService
{
    private const string SubjectPrefix = "Russian Trusted";

    private static readonly (StoreLocation Location, StoreName Name)[] Targets =
    [
        (StoreLocation.CurrentUser, StoreName.Root),
        (StoreLocation.CurrentUser, StoreName.CertificateAuthority),
        (StoreLocation.LocalMachine, StoreName.Root),
        (StoreLocation.LocalMachine, StoreName.CertificateAuthority)
    ];

    // Сертификат Минцифры установлен, если хотя бы одно хранилище содержит его.
    public static bool IsRussianTrustedInstalled()
    {
        try
        {
            return Targets.Any(target =>
            {
                using var store = new X509Store(target.Name, target.Location);
                store.Open(OpenFlags.ReadOnly);
                return ReadMatchingCerts(store).Count > 0;
            });
        }
        catch
        {
            return false;
        }
    }

    // Удаление из Root и CA (текущий пользователь и машина). Хранилище машины
    // требует прав администратора — при их отсутствии даётся понятная ошибка.
    public Result Remove()
    {
        var removed = 0;
        var errors = new List<string>();

        foreach (var (location, name) in Targets)
        {
            try
            {
                using var store = new X509Store(name, location);
                store.Open(OpenFlags.ReadWrite);
                var matching = ReadMatchingCerts(store);
                foreach (var certificate in matching)
                {
                    store.Remove(certificate);
                    removed++;
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{location}/{name}: {exception.Message}");
            }
        }

        if (removed == 0 && errors.Count > 0)
        {
            return Result.Failure("Не удалось открыть хранилища сертификатов: " + string.Join("; ", errors), 1);
        }

        return removed == 0
            ? Result.Success("Сертификат Минцифры не найден в хранилищах сертификатов.")
            : Result.Success($"Удалено сертификатов: {removed}.");
    }

    private static List<X509Certificate2> ReadMatchingCerts(X509Store store) =>
        store.Certificates
            .Cast<X509Certificate2>()
            .Where(c => c.GetNameInfo(X509NameType.SimpleName, false)
                .StartsWith(SubjectPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
