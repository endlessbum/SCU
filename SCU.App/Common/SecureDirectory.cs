using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SCU.Common;

// Каталог с DACL «только Administrators и SYSTEM — полный доступ». Наследование
// отключается: иначе менее привилегированный процесс мог бы заранее подменить
// содержимое или сделать это между проверкой и использованием (TOCTOU).
// Применяется там, где повышенный SCU пишет то, что потом сам же исполняет
// или восстанавливает (установщики рантаймов, карантин).
public static class SecureDirectory
{
    public static void EnsureAdminsOnly(string directory)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var allowed = new HashSet<SecurityIdentifier> { administrators, system };

        var rules = new DirectorySecurity();
        rules.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        rules.SetOwner(administrators);
        rules.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        rules.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        var info = new DirectoryInfo(directory);
        info.SetAccessControl(rules);

        // Fail closed: наследование должно быть выключено, а каждый ACE — только для
        // Administrators/SYSTEM. Лишний Allow для Users/Everyone оставляет TOCTOU.
        var actual = info.GetAccessControl(AccessControlSections.Access);
        if (!actual.AreAccessRulesProtected)
        {
            throw new SecurityException("Не удалось отключить наследование DACL.");
        }

        var actualRules = actual.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var seen = new HashSet<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in actualRules)
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!allowed.Contains(sid))
            {
                throw new SecurityException($"Обнаружен неразрешённый ACL для SID {sid.Value}.");
            }

            if (rule.AccessControlType != AccessControlType.Allow || rule.FileSystemRights != FileSystemRights.FullControl)
            {
                throw new SecurityException($"Обнаружен слишком узкий/запрещающий ACL для SID {sid.Value}; ожидается FullControl Allow.");
            }

            seen.Add(sid);
        }

        if (!seen.SetEquals(allowed))
        {
            throw new SecurityException("Защищённый каталог не содержит полного доступа Administrators и SYSTEM.");
        }
    }
}
