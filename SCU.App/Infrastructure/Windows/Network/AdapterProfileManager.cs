using System.Text;
using SCU.Common;
using SCU.Interop;

namespace SCU.Infrastructure.Windows.Network;

// П. 13 аудита (B4): зона «профиль адаптера NIC» из NetworkService — применение
// универсального профиля, бэкап и восстановление через PowerShell
// (Get/Set-NetAdapterAdvancedProperty). NetworkService хранит тонкий фасад.
internal sealed class AdapterProfileManager
{
    private readonly Logger _logger;
    private readonly LongProcessRunner _runner;

    // Тот же каталог резервных копий, что у NetworkService (SCU/backup/network).
    private static readonly string BackupDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup", "network");

    public AdapterProfileManager(Logger logger, LongProcessRunner runner)
    {
        _logger = logger;
        _runner = runner;
    }

    // Универсальный сбалансированный профиль физическим NIC.
    // Используются стандартизированные NDIS RegistryKeyword, поэтому локализация Windows/драйвера
    // не влияет на поиск свойства. Если конкретный драйвер не предоставляет свойство — оно пропускается.
    // Перед применением текущие значения тех же ключей сохраняются в бэкап (adapter_profile.txt).
    public async Task<Result> ApplyUniversalAdapterProfileAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var backup = await BackupAdapterProfileAsync(ct).ConfigureAwait(false);
        if (!backup.IsSuccess)
        {
            return backup;
        }

        const string script = """
$ErrorActionPreference = 'Stop'

$settings = @(
    [pscustomobject]@{ Group = 'RSS'; Key = '*RSS'; Value = '1' },
    [pscustomobject]@{ Group = 'Checksum Offload'; Key = '*TCPUDPChecksumOffloadIPv4'; Value = '3'; Fallback = @('*IPChecksumOffloadIPv4','*TCPChecksumOffloadIPv4','*UDPChecksumOffloadIPv4') },
    [pscustomobject]@{ Group = 'Checksum Offload'; Key = '*TCPUDPChecksumOffloadIPv6'; Value = '3'; Fallback = @('*TCPChecksumOffloadIPv6','*UDPChecksumOffloadIPv6') },
    [pscustomobject]@{ Group = 'LSO v2'; Key = '*LsoV2IPv4'; Value = '1' },
    [pscustomobject]@{ Group = 'LSO v2'; Key = '*LsoV2IPv6'; Value = '1' },
    [pscustomobject]@{ Group = 'Interrupt Moderation'; Key = '*InterruptModeration'; Value = '1' },
    [pscustomobject]@{ Group = 'Flow Control'; Key = '*FlowControl'; Value = '3' },
    [pscustomobject]@{ Group = 'Jumbo Packet'; Key = '*JumboPacket'; Value = '1514' },
    [pscustomobject]@{ Group = 'Energy Efficient Ethernet'; Key = '*EEE'; Value = '0' }
)

$adapters = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.Name -and $_.Status -ne 'Not Present' })
if ($adapters.Count -eq 0) {
    Write-Output 'NO_ADAPTERS'
    exit 0
}

foreach ($adapter in $adapters) {
    $name = [string]$adapter.Name
    Write-Output ("ADAPTER|{0}" -f $name)

    try {
        $props = @(Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction Stop)
    }
    catch {
        Write-Output ("ADAPTER_FAIL|{0}|{1}" -f $name, $_.Exception.Message)
        continue
    }

    $changed = $false
    $verifyKeys = New-Object System.Collections.Generic.List[string]
    $rateKeyword = $null

    foreach ($setting in $settings) {
        $candidates = @($setting.Key)
        if ($setting.PSObject.Properties.Name -contains 'Fallback') {
            $candidates += @($setting.Fallback)
        }

        $property = $null
        foreach ($candidate in $candidates) {
            $property = $props | Where-Object { $_.RegistryKeyword -eq $candidate } | Select-Object -First 1
            if ($null -ne $property) {
                break
            }
        }

        if ($null -eq $property) {
            Write-Output ("SETTING|{0}|{1}|SKIP" -f $setting.Group, $setting.Key)
            continue
        }

        try {
            Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $property.RegistryKeyword -RegistryValue $setting.Value -NoRestart -ErrorAction Stop | Out-Null
            $verifyKeys.Add([string]$property.RegistryKeyword) | Out-Null
            $changed = $true
            Write-Output ("SETTING|{0}|{1}|SET" -f $setting.Group, $property.RegistryKeyword)
        }
        catch {
            Write-Output ("SETTING|{0}|{1}|FAIL|{2}" -f $setting.Group, $property.RegistryKeyword, $_.Exception.Message)
        }
    }

    # Interrupt Moderation Rate is vendor-specific. Use it only if the driver exposes Adaptive.
    $rateProperty = $props | Where-Object {
        ($_.DisplayName -and $_.DisplayName -match '(?i)interrupt\s+moderation\s+rate') -or
        ($_.RegistryKeyword -and $_.RegistryKeyword -match '(?i)ITR|InterruptModerationRate')
    } | Select-Object -First 1
    if ($null -ne $rateProperty) {
        $validValues = @($rateProperty.ValidDisplayValues | ForEach-Object { [string]$_ })
        if ($validValues | Where-Object { $_ -ieq 'Adaptive' }) {
            try {
                if ([string]::IsNullOrWhiteSpace([string]$rateProperty.DisplayName)) {
                    Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $rateProperty.RegistryKeyword -DisplayValue 'Adaptive' -NoRestart -ErrorAction Stop | Out-Null
                }
                else {
                    Set-NetAdapterAdvancedProperty -Name $name -DisplayName $rateProperty.DisplayName -DisplayValue 'Adaptive' -NoRestart -ErrorAction Stop | Out-Null
                }
                $changed = $true
                # M-02: IMR участвует в последующей проверке наравне с остальными
                # свойствами — раньше SET не проверялся вообще.
                $rateKeyword = [string]$rateProperty.RegistryKeyword
                $verifyKeys.Add($rateKeyword) | Out-Null
                Write-Output ("SETTING|Interrupt Moderation|{0}|SET" -f $(if ([string]::IsNullOrWhiteSpace([string]$rateProperty.DisplayName)) { $rateProperty.RegistryKeyword } else { $rateProperty.DisplayName }))
            }
            catch {
                Write-Output ("SETTING|Interrupt Moderation|Interrupt Moderation Rate|FAIL|{0}" -f $_.Exception.Message)
            }
        }
        else {
            Write-Output 'SETTING|Interrupt Moderation|Interrupt Moderation Rate|SKIP'
        }
    }
    else {
        Write-Output 'SETTING|Interrupt Moderation|Interrupt Moderation Rate|SKIP'
    }

    if ($changed) {
        try {
            Write-Output 'RESTART|BEGIN'
            Restart-NetAdapter -Name $name -Confirm:$false -ErrorAction Stop | Out-Null
        }
        catch {
            Write-Output ("RESTART|FAIL|{0}" -f $_.Exception.Message)
        }

        $verifyProps = @(Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction SilentlyContinue)
        foreach ($key in $verifyKeys) {
            $current = $verifyProps | Where-Object { $_.RegistryKeyword -eq $key } | Select-Object -First 1
            # IMR задаётся через DisplayValue ('Adaptive') и проверяется по нему:
            # сырой RegistryValue у vendor-свойства — число, в лоб не сверяется.
            if ($key -eq $rateKeyword) {
                if ($null -ne $current -and [string]$current.DisplayValue -ieq 'Adaptive') {
                    Write-Output ("VERIFY|{0}|OK" -f $key)
                }
                else {
                    Write-Output ("VERIFY|{0}|FAIL" -f $key)
                }
                continue
            }
            $expected = $settings | Where-Object { $_.Key -eq $key } | Select-Object -First 1
            if ($null -eq $expected -and $key -eq '*IPChecksumOffloadIPv4') { $expected = [pscustomobject]@{ Value = '3' } }
            if ($null -eq $expected -and $key -eq '*TCPChecksumOffloadIPv4') { $expected = [pscustomobject]@{ Value = '3' } }
            if ($null -eq $expected -and $key -eq '*UDPChecksumOffloadIPv4') { $expected = [pscustomobject]@{ Value = '3' } }
            if ($null -eq $expected -and $key -eq '*TCPChecksumOffloadIPv6') { $expected = [pscustomobject]@{ Value = '3' } }
            if ($null -eq $expected -and $key -eq '*UDPChecksumOffloadIPv6') { $expected = [pscustomobject]@{ Value = '3' } }
            if ($null -eq $current -or $null -eq $expected -or [string]$current.RegistryValue -ne [string]$expected.Value) {
                Write-Output ("VERIFY|{0}|FAIL" -f $key)
            }
            else {
                Write-Output ("VERIFY|{0}|OK" -f $key)
            }
        }
    }
}
""";

        try
        {
            var result = await _runner
                .RunAsync(
                    "powershell.exe",
                    ["-NoProfile", "-NoLogo", "-NonInteractive", "-Command", script],
                    progress,
                    ct)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return Result.Failure(result.Message, result.Code);
            }

            var changed = 0;
            var skipped = 0;
            var failed = 0;
            var verifyFailed = 0;
            var adapters = 0;
            var adapterFailures = 0;
            var restartsFailed = 0;
            foreach (var line in (result.Value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line == "NO_ADAPTERS")
                {
                    continue;
                }

                if (line.StartsWith("ADAPTER|", StringComparison.Ordinal))
                {
                    adapters++;
                    continue;
                }

                if (line.StartsWith("ADAPTER_FAIL|", StringComparison.Ordinal))
                {
                    adapterFailures++;
                    continue;
                }

                if (line.StartsWith("SETTING|", StringComparison.Ordinal))
                {
                    if (line.EndsWith("|SET", StringComparison.Ordinal))
                    {
                        changed++;
                    }
                    else if (line.EndsWith("|SKIP", StringComparison.Ordinal))
                    {
                        skipped++;
                    }
                    else if (line.Contains("|FAIL|", StringComparison.Ordinal))
                    {
                        failed++;
                    }
                    continue;
                }

                if (line.StartsWith("VERIFY|", StringComparison.Ordinal) && line.EndsWith("|FAIL", StringComparison.Ordinal))
                {
                    verifyFailed++;
                    continue;
                }

                if (line.StartsWith("RESTART|FAIL|", StringComparison.Ordinal))
                {
                    restartsFailed++;
                }
            }

            if (adapters == 0 && adapterFailures == 0)
            {
                return Result.Success("Физические сетевые адаптеры не найдены.");
            }

            if (failed > 0 || verifyFailed > 0 || adapterFailures > 0)
            {
                // M-03 + SCU-01: часть значений применилась, часть — нет. Ошибки
                // чтения тоже входят в откат: чтение свойств очередного адаптера
                // могло сломаться ПОСЛЕ того, как предыдущие уже изменены и
                // перезапущены, — это то же несогласованное состояние. Успешный
                // бэкап снимался ДО первой правки, поэтому автоматический откат
                // возвращает все адаптеры к исходному состоянию; Failure больше
                // не оставляет смесь новых и старых значений.
                var rollback = await RestoreAdapterProfileAsync(ct).ConfigureAwait(false);
                var rollbackNote = rollback.IsSuccess
                    ? "изменённые значения откатаны к исходным"
                    : "АВТООТКАТ НЕ УДАЛСЯ (" + rollback.Message + "); откатите профиль кнопкой «Откатить»";
                _logger.Warn($"NET | adapter profile partial apply | changed={changed} failed={failed} verifyFailed={verifyFailed} adapterFailures={adapterFailures} | {rollbackNote}");
                return Result.Failure(
                    $"Обработано адаптеров: {adapters}; изменено: {changed}; пропущено: {skipped}; ошибок настройки: {failed}; ошибок проверки: {verifyFailed}; ошибок чтения: {adapterFailures}; ошибок перезапуска: {restartsFailed}. {rollbackNote}.");
            }

            if (restartsFailed > 0)
            {
                // Ошибка перезапуска откат не требует (SCU-01: раньше делила ветку
                // с ошибками чтения): значения применены и проверены, несогласованного
                // состояния нет.
                return Result.Failure(
                    $"Обработано адаптеров: {adapters}; изменено: {changed}; пропущено: {skipped}; ошибок чтения: {adapterFailures}; ошибок перезапуска: {restartsFailed}.");
            }

            return Result.Success(
                $"Универсальный профиль применён: адаптеров {adapters}; изменено {changed}; пропущено {skipped} неподдерживаемых свойств.");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось применить универсальный профиль: " + exception.Message);
        }
    }

    // Бэкап расширенных свойств физических адаптеров ДО применения профиля.
    // Разбор вывода backup-скрипта (SCU-01): строки "ADAPTER|<адаптер>|<keyword>|<значение|->",
    // ошибки "BACKUP_FAIL|<адаптер>|<причина>" и итог "BACKUP_TOTAL|<всего>|<успешно>".
    // Раньше фильтр оставлял только ADAPTER|, сообщения BACKUP_FAIL терялись, и
    // неполный бэкап сохранялся как успешный — для части адаптеров откат был бы
    // невозможен.
    internal sealed record AdapterBackupParse(
        IReadOnlyList<string> AdapterLines,
        IReadOnlyList<string> FailedAdapterNames,
        int ExpectedAdapters,
        int OkAdapters);

    internal static AdapterBackupParse ParseBackupOutput(string output)
    {
        var adapterLines = new List<string>();
        var failedNames = new List<string>();
        var expected = -1;
        var ok = -1;
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("ADAPTER|", StringComparison.Ordinal))
            {
                adapterLines.Add(raw);
            }
            else if (raw.StartsWith("BACKUP_FAIL|", StringComparison.Ordinal))
            {
                var parts = raw.Split('|');
                failedNames.Add(parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : "?");
            }
            else if (raw == "NO_ADAPTERS")
            {
                expected = 0;
                ok = 0;
            }
            else if (raw.StartsWith("BACKUP_TOTAL|", StringComparison.Ordinal))
            {
                var parts = raw.Split('|');
                if (parts.Length >= 3
                    && int.TryParse(parts[1], out var parsedExpected)
                    && int.TryParse(parts[2], out var parsedOk))
                {
                    expected = parsedExpected;
                    ok = parsedOk;
                }
            }
        }

        return new AdapterBackupParse(adapterLines, failedNames, expected, ok);
    }

    private static string BackupAdapterName(string adapterLine)
    {
        var parts = adapterLine.Split('|');
        return parts.Length > 1 ? parts[1] : string.Empty;
    }

    // Корректная строка бэкапа: "ADAPTER|<имя>|<keyword>|<значение>". Значение может
    // содержать '|', поэтому split с limit 4 — как в restore-скрипте (см. разбор
    // AdapterProfileRestoreScript). Пустые имя адаптера или keyword не допускаются.
    internal static bool IsValidAdapterBackupLine(string line)
    {
        var parts = line.Split('|', 4);
        return parts.Length == 4
            && parts[0] == "ADAPTER"
            && parts[1].Trim().Length > 0
            && parts[2].Trim().Length > 0;
    }

    // null — бэкап полный, применение профиля разрешено (SCU-01); иначе — причина
    // неполноты. Полнота сверяется по фактическим строкам: каждый адаптер из
    // BACKUP_TOTAL обязан быть представлен хотя бы одной строкой ADAPTER|.
    internal static string? ValidateBackupOutput(AdapterBackupParse parse)
    {
        if (parse.ExpectedAdapters < 0)
        {
            return "скрипт не сообщил итоговое число адаптеров (BACKUP_TOTAL отсутствует).";
        }

        if (parse.ExpectedAdapters == 0
            && parse.AdapterLines.Count == 0
            && parse.FailedAdapterNames.Count == 0)
        {
            // Адаптеров нет — резервировать нечего, это не неполнота.
            return null;
        }

        if (parse.FailedAdapterNames.Count > 0)
        {
            return "не удалось прочитать свойства адаптеров: " + string.Join(", ", parse.FailedAdapterNames) + ".";
        }

        var distinctAdapters = parse.AdapterLines
            .Select(BackupAdapterName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (distinctAdapters != parse.ExpectedAdapters)
        {
            return $"свойства прочитаны для {distinctAdapters} из {parse.ExpectedAdapters} адаптеров.";
        }

        return null;
    }

    internal const string AdapterProfileBackupScript = """
$ErrorActionPreference = 'Stop'

$keys = @(
    '*RSS',
    '*TCPUDPChecksumOffloadIPv4',
    '*TCPUDPChecksumOffloadIPv6',
    '*IPChecksumOffloadIPv4',
    '*TCPChecksumOffloadIPv4',
    '*TCPChecksumOffloadIPv6',
    '*UDPChecksumOffloadIPv4',
    '*UDPChecksumOffloadIPv6',
    '*LsoV2IPv4',
    '*LsoV2IPv6',
    '*InterruptModeration',
    '*FlowControl',
    '*JumboPacket',
    '*EEE'
)

$adapters = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.Name -and $_.Status -ne 'Not Present' })
if ($adapters.Count -eq 0) {
    Write-Output 'NO_ADAPTERS'
    exit 0
}

$total = $adapters.Count
$ok = 0
foreach ($adapter in $adapters) {
    $name = [string]$adapter.Name
    try {
        $props = @(Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction Stop)
    }
    catch {
        Write-Output ("BACKUP_FAIL|{0}|{1}" -f $name, $_.Exception.Message)
        continue
    }

    $ok++
    foreach ($key in $keys) {
        $property = $props | Where-Object { $_.RegistryKeyword -eq $key } | Select-Object -First 1
        if ($null -ne $property) {
            Write-Output ("ADAPTER|{0}|{1}|{2}" -f $name, $key, [string]$property.RegistryValue)
        }
        else {
            Write-Output ("ADAPTER|{0}|{1}|-" -f $name, $key)
        }
    }

    # Interrupt Moderation Rate is vendor-specific; back up its raw registry value by keyword.
    $rateProperty = $props | Where-Object {
        ($_.DisplayName -and $_.DisplayName -match '(?i)interrupt\s+moderation\s+rate') -or
        ($_.RegistryKeyword -and $_.RegistryKeyword -match '(?i)ITR|InterruptModerationRate')
    } | Select-Object -First 1
    if ($null -ne $rateProperty) {
        Write-Output ("ADAPTER|{0}|{1}|{2}" -f $name, $rateProperty.RegistryKeyword, [string]$rateProperty.RegistryValue)
    }
}

# Итог для C# (SCU-01): без него C# не отличал полный бэкап от частичного.
Write-Output ("BACKUP_TOTAL|{0}|{1}" -f $total, $ok)
""";

    // Выгрузка бэкапа профиля: PS печатает строки "ADAPTER|<имя>|<keyword>|<значение|->"
    // ("-" — свойства нет), файл пишет C#. Кодировка UTF8 с BOM — иначе Windows PowerShell 5.1
    // прочитает кириллические имена адаптеров в ANSI и откат по ним не найдёт адаптер.
    // Повторное применение бэкап не перезаписывает: в нём значения ДО первой правки.
    internal async Task<Result> BackupAdapterProfileAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(AdapterProfileBackupFile))
            {
                // Повторное применение бэкап не перезаписывает: в нём значения ДО
                // первой правки. Но и повреждённый существующий принимать нельзя —
                // откат по нему не покроет все адаптеры. Раньше достаточно было
                // одной строки ADAPTER| (OBS-03, аудит 2026-11-04): усечённый файл
                // проходил проверку. Теперь каждая непустая строка обязана быть
                // корректной записью ADAPTER|<имя>|<keyword>|<значение>.
                var existing = File.ReadAllLines(AdapterProfileBackupFile);
                if (existing.Where(line => line.Length > 0).All(IsValidAdapterBackupLine)
                    && existing.Any(line => line.Length > 0))
                {
                    return Result.Success("Бэкап уже существует.");
                }

                return Result.Failure(
                    "Существующий бэкап профиля повреждён (битый формат строк ADAPTER|). Удалите его вручную или выполните «Откатить».");
            }

            var result = await _runner
                .RunAsync(
                    "powershell.exe",
                    ["-NoProfile", "-NoLogo", "-NonInteractive", "-Command", AdapterProfileBackupScript],
                    null,
                    ct)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return Result.Failure("Не удалось выгрузить текущие свойства адаптеров: " + result.Message, result.Code);
            }

            var parse = ParseBackupOutput(result.Value ?? string.Empty);
            if (parse.ExpectedAdapters == 0
                && parse.AdapterLines.Count == 0
                && parse.FailedAdapterNames.Count == 0)
            {
                // Адаптеров нет — резервировать нечего; применение профиля само сообщит об этом.
                return Result.Success("Физические сетевые адаптеры не найдены.");
            }

            // SCU-01: неполный бэкап не сохраняется и применение не разрешает —
            // для адаптеров, чьи свойства прочитать не удалось, откат был бы невозможен.
            var invalidReason = ValidateBackupOutput(parse);
            if (invalidReason is not null)
            {
                _logger.Warn("NET | adapter profile backup incomplete | " + invalidReason);
                return Result.Failure("Бэкап неполный — применение профиля отменено: " + invalidReason);
            }

            Directory.CreateDirectory(BackupDirectory);
            // OBS-03 (аудит 2026-11-04): прямая запись оставляла бы усечённый файл при
            // сбое/отмене посреди WriteAllLines — temp+move делает замену атомарной
            // (тот же паттерн, что JsonStateFile).
            var tempFile = AdapterProfileBackupFile + ".tmp";
            try
            {
                File.WriteAllLines(tempFile, parse.AdapterLines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                File.Move(tempFile, AdapterProfileBackupFile, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempFile); } catch { /* не критично */ }
                throw;
            }
            _logger.Info($"NET | adapter profile backup | {parse.AdapterLines.Count} строк | адаптеров {parse.ExpectedAdapters}");
            return Result.Success("Бэкап сохранён.");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось сохранить бэкап свойств адаптеров: " + exception.Message);
        }
    }

    // Откат профиля: Set-NetAdapterAdvancedProperty по значениям из бэкапа (значение "-" —
    // свойства не было, пропускается), затем перезапуск изменённых адаптеров.
    // После успеха бэкап удаляется (best-effort: сбой удаления не роняет операцию).
    // Разбор вывода restore-скрипта: RESTORE|<адаптер>|<ключ>|OK/FAIL и RESTART|FAIL|<адаптер>.
    internal static (int Restored, int Failures, int RestartsFailed) SummarizeRestoreOutput(string output)
    {
        var restored = 0;
        var failures = 0;
        var restartsFailed = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("RESTORE|", StringComparison.Ordinal))
            {
                if (line.EndsWith("|OK", StringComparison.Ordinal))
                {
                    restored++;
                }
                else
                {
                    failures++;
                }
            }
            else if (line.StartsWith("RESTART|FAIL|", StringComparison.Ordinal))
            {
                restartsFailed++;
            }
        }
        return (restored, failures, restartsFailed);
    }

    public async Task<Result> RestoreAdapterProfileAsync(CancellationToken ct = default)
    {
        if (!File.Exists(AdapterProfileBackupFile))
        {
            return Result.Success("Бэкап свойств адаптеров отсутствует — восстанавливать нечего.");
        }

        var script = AdapterProfileRestoreScript.Replace(
            "<BACKUP_PATH>",
            AdapterProfileBackupFile.Replace("'", "''", StringComparison.Ordinal),
            StringComparison.Ordinal);

        try
        {
            var result = await _runner
                .RunAsync(
                    "powershell.exe",
                    ["-NoProfile", "-NoLogo", "-NonInteractive", "-Command", script],
                    null,
                    ct)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return Result.Failure(result.Message, result.Code);
            }

            var (restored, failures, restartsFailed) = SummarizeRestoreOutput(result.Value ?? string.Empty);

            if (failures > 0 || restartsFailed > 0)
            {
                return Result.Failure(
                    $"Свойства адаптеров восстановлены не полностью: значений {restored}, ошибок {failures}, ошибок перезапуска {restartsFailed}; бэкап сохранён.");
            }

            // Удаление бэкапа — необязательный хвост: не роняет успешную операцию.
            try
            {
                File.Delete(AdapterProfileBackupFile);
            }
            catch (Exception exception)
            {
                _logger.Warn("NET | adapter profile backup cleanup failed: " + exception.Message);
            }

            _logger.Info($"NET | adapter profile restored | {restored} значений");
            return Result.Success($"Свойства адаптеров восстановлены из бэкапа ({restored} значений).");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось восстановить свойства адаптеров: " + exception.Message);
        }
    }

    internal const string AdapterProfileRestoreScript = """
$ErrorActionPreference = 'Continue'

$backupPath = '<BACKUP_PATH>'
if (-not (Test-Path -LiteralPath $backupPath)) {
    Write-Output 'NO_BACKUP'
    exit 0
}

$restart = New-Object System.Collections.Generic.List[string]
foreach ($line in [System.IO.File]::ReadAllLines($backupPath)) {
    $parts = $line -split '\|', 4
    if ($parts.Count -ne 4 -or $parts[0] -ne 'ADAPTER') {
        continue
    }

    $name = $parts[1]
    $key = $parts[2]
    $value = $parts[3]
    if ($key -eq '-' -or $value -eq '-') {
        continue
    }

    try {
        Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $key -RegistryValue $value -NoRestart -ErrorAction Stop | Out-Null
        Write-Output ("RESTORE|{0}|{1}|OK" -f $name, $key)
        if (-not $restart.Contains($name)) {
            $restart.Add($name)
        }
    }
    catch {
        Write-Output ("RESTORE|{0}|{1}|FAIL|{2}" -f $name, $key, $_.Exception.Message)
    }
}

foreach ($name in $restart) {
    try {
        Write-Output 'RESTART|BEGIN'
        Restart-NetAdapter -Name $name -Confirm:$false -ErrorAction Stop | Out-Null
    }
    catch {
        Write-Output ("RESTART|FAIL|{0}" -f $_.Exception.Message)
    }
}
""";

    // Признак «есть что откатывать» — используется VM для доступности кнопки «Откатить».
    public static bool HasAdapterProfileBackup() => File.Exists(AdapterProfileBackupFile);

    internal static string AdapterProfileBackupFile => Path.Combine(BackupDirectory, "adapter_profile.txt");
}
