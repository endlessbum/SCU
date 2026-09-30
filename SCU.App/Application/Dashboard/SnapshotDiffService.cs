using SCU.Models;

namespace SCU.AppCore.Dashboard;

// Запись diff двух снимков. Field — стабильный ключ поля (сопоставление и локализация),
// Before/After — значения «как есть» из снимков (string/int/double/bool), Delta — числовая
// дельта там, где она имеет смысл, иначе null. Локализация и форматирование — на стороне
// ViewModel: сервис ничего не знает про UI и язык интерфейса.
public sealed record SnapshotDiffEntry(
    string Field,
    object? Before,
    object? After,
    double? Delta);

// Контролируемый diff двух SystemSnapshot: список полей задан кодом, а не сериализацией
// объектов (новое поле снимка не попадёт в diff само собой — его нужно добавить явно).
// Правила по null: поле неизвестно в обоих снимках — записи нет; известно только в одном —
// запись с null на одной стороне («было неизвестно / стало X») — честное отображение.
public static class SnapshotDiffService
{
    public static IReadOnlyList<SnapshotDiffEntry> Diff(SystemSnapshot? before, SystemSnapshot? after)
    {
        if (before is null || after is null)
        {
            return [];
        }

        var entries = new List<SnapshotDiffEntry>();

        // Система и железо.
        Add(entries, "WindowsVersion", before.WindowsVersion, after.WindowsVersion);
        Add(entries, "WindowsBuild", before.WindowsBuild, after.WindowsBuild);
        Add(entries, "Cpu", before.Cpu, after.Cpu);
        Add(entries, "Ram", before.Ram, after.Ram);
        Add(entries, "Gpu", before.Gpu, after.Gpu);

        AddDisks(entries, before, after);

        // Автозагрузка, службы, задачи.
        Add(entries, "StartupCount", before.StartupCount, after.StartupCount);
        Add(entries, "ServicesOk", before.ServicesOk, after.ServicesOk);
        Add(entries, "ServicesChanged", before.ServicesChanged, after.ServicesChanged);
        Add(entries, "ServicesTotal", before.ServicesTotal, after.ServicesTotal);
        Add(entries, "ServicesMissing", before.ServicesMissing, after.ServicesMissing);
        Add(entries, "TasksTotal", before.TasksTotal, after.TasksTotal);
        Add(entries, "TasksDisabled", before.TasksDisabled, after.TasksDisabled);

        // Сеть — по компонентам, чтобы было видно, что именно изменилось.
        Add(entries, "Network.AutoTuning", before.Network?.AutoTuning, after.Network?.AutoTuning);
        Add(entries, "Network.Ecn", before.Network?.Ecn, after.Network?.Ecn);
        Add(entries, "Network.Qos", before.Network?.Qos, after.Network?.Qos);
        Add(entries, "Network.NetBios", before.Network?.NetBios, after.Network?.NetBios);

        // Питание, обновления, безопасность, приватность.
        Add(entries, "ActivePlanGuid", before.ActivePlanGuid, after.ActivePlanGuid);
        Add(entries, "UpdateBlocked", before.UpdateBlocked, after.UpdateBlocked);
        Add(entries, "UpdatePaused", before.UpdatePaused, after.UpdatePaused);
        Add(entries, "UacState", before.UacState, after.UacState);
        Add(entries, "PrivacyAppliedCount", before.PrivacyAppliedCount, after.PrivacyAppliedCount);
        Add(entries, "PrivacyTotal", before.PrivacyTotal, after.PrivacyTotal);

        return entries;
    }

    // Стабильный ключ поля свободного места диска (буква без ':' и '\').
    public static string DiskFreeField(string letter) => "Disk." + letter + ".FreeGb";

    private static void Add(List<SnapshotDiffEntry> entries, string field, object? before, object? after)
    {
        if (before is null && after is null)
        {
            return;
        }

        if (Equals(before, after))
        {
            return;
        }

        entries.Add(new SnapshotDiffEntry(field, before, after, NumericDelta(before, after)));
    }

    // Дельта только для чисел: строки/bool её не имеют (null — «дельта не применима»).
    private static double? NumericDelta(object? before, object? after)
    {
        var from = before switch
        {
            int number => (double)number,
            double value => value,
            _ => (double?)null
        };
        var to = after switch
        {
            int number => (double)number,
            double value => value,
            _ => (double?)null
        };

        return from is { } fromValue && to is { } toValue ? toValue - fromValue : null;
    }

    // Диски сопоставляются по букве; порядок — как в before, затем новые диски из after.
    private static void AddDisks(List<SnapshotDiffEntry> entries, SystemSnapshot before, SystemSnapshot after)
    {
        var beforeFree = ToFreeByLetter(before.Disks);
        var afterFree = ToFreeByLetter(after.Disks);

        var letters = beforeFree.Keys
            .Concat(afterFree.Keys.Where(letter => !beforeFree.ContainsKey(letter)))
            .ToList();

        foreach (var letter in letters)
        {
            Add(
                entries,
                DiskFreeField(letter),
                beforeFree.TryGetValue(letter, out var from) ? from : null,
                afterFree.TryGetValue(letter, out var to) ? to : null);
        }
    }

    private static Dictionary<string, double> ToFreeByLetter(IReadOnlyList<DiskSnapshot> disks)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var disk in disks)
        {
            // WMI DeviceID приходит как "C:" — буква без разделителей служит ключом сопоставления.
            var letter = disk.Letter.TrimEnd(':', '\\');
            if (letter.Length > 0)
            {
                map[letter] = disk.FreeGb;
            }
        }

        return map;
    }
}
