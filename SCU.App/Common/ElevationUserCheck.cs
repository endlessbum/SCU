using System.Management;

namespace SCU.Common;

// Запущен ли SCU под учётной записью, отличной от интерактивного пользователя
// (типичный случай: перезапуск через «Запуск от администратора» с чужими
// админ-креденциалами). WebView2 в таком режиме известным образом зависает при
// создании среды (MicrosoftEdge/WebView2Feedback #4672, #932) — UI честно
// предупреждает об этом заранее.
public static class ElevationUserCheck
{
    private static int _checked;
    private static bool? _mismatch;

    public static bool RunsAsDifferentUserThanInteractive()
    {
        if (Interlocked.Exchange(ref _checked, 1) == 1)
        {
            return _mismatch == true;
        }

        try
        {
            var currentUser = Environment.UserDomainName + "\\" + Environment.UserName;

            using var searcher = new ManagementObjectSearcher(
                "SELECT OwnerDomain, OwnerUser FROM Win32_Process WHERE Name='explorer.exe'");
            foreach (var process in searcher.Get())
            {
                var domain = process["OwnerDomain"]?.ToString();
                var user = process["OwnerUser"]?.ToString();
                if (string.IsNullOrEmpty(user))
                {
                    continue;
                }

                var mismatch = !string.Equals(currentUser, domain + "\\" + user, StringComparison.OrdinalIgnoreCase);
                _mismatch = mismatch;
                return mismatch;
            }

            // explorer не найден (неинтерактивная сессия) — считать совпадением нельзя.
            _mismatch = false;
        }
        catch
        {
            // WMI недоступен/запрещён — предупреждение просто не показывается.
            _mismatch = false;
        }

        return _mismatch == true;
    }
}
