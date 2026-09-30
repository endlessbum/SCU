using System.Text;
using SCU.Common;
using SCU.Interop;

namespace SCU.Infrastructure.Windows.Network;

// П. 13 аудита (B4): зона «профиль адаптера NIC» из NetworkService — применение
// универсального профиля, резерв и восстановление через PowerShell
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
    // Перед применением текущие значения тех же ключей сохраняются в резерв (adapter_profile.txt).
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

            if (failed > 0 || verifyFailed > 0 || adapterFailures > 0 || restartsFailed > 0)
            {
                return Result.Failure(
                    $"Обработано адаптеров: {adapters}; изменено: {changed}; пропущено: {skipped}; ошибок настройки: {failed}; ошибок проверки: {verifyFailed}; ошибок чтения: {adapterFailures}; ошибок перезапуска: {restartsFailed}.");
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

    // Резерв расширенных свойств физических адаптеров ДО применения профиля.
    // Фильтр строк "ADAPTER|<адаптер>|<keyword>|<значение|->" из вывода backup-скрипта.
    internal static IReadOnlyList<string> SelectBackupLines(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("ADAPTER|", StringComparison.Ordinal))
            .ToList();

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

foreach ($adapter in $adapters) {
    $name = [string]$adapter.Name
    try {
        $props = @(Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction Stop)
    }
    catch {
        Write-Output ("BACKUP_FAIL|{0}|{1}" -f $name, $_.Exception.Message)
        continue
    }

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
""";

    // Выгрузка резерва профиля: PS печатает строки "ADAPTER|<имя>|<keyword>|<значение|->"
    // ("-" — свойства нет), файл пишет C#. Кодировка UTF8 с BOM — иначе Windows PowerShell 5.1
    // прочитает кириллические имена адаптеров в ANSI и откат по ним не найдёт адаптер.
    // Повторное применение резерв не перезаписывает: в нём значения ДО первой правки.
    internal async Task<Result> BackupAdapterProfileAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(AdapterProfileBackupFile))
            {
                return Result.Success("Резерв уже существует.");
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

            var lines = SelectBackupLines(result.Value ?? string.Empty);
            if (lines.Count == 0)
            {
                // Адаптеров нет — резервировать нечего; применение профиля само сообщит об этом.
                return Result.Success("Физические сетевые адаптеры не найдены.");
            }

            Directory.CreateDirectory(BackupDirectory);
            File.WriteAllLines(AdapterProfileBackupFile, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _logger.Info($"NET | adapter profile backup | {lines.Count} строк");
            return Result.Success("Резерв сохранён.");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось сохранить резерв свойств адаптеров: " + exception.Message);
        }
    }

    // Откат профиля: Set-NetAdapterAdvancedProperty по значениям из резерва (значение "-" —
    // свойства не было, пропускается), затем перезапуск изменённых адаптеров.
    // После успеха резерв удаляется (best-effort: сбой удаления не роняет операцию).
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
            return Result.Success("Резерв свойств адаптеров отсутствует — восстанавливать нечего.");
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
                    $"Свойства адаптеров восстановлены не полностью: значений {restored}, ошибок {failures}, ошибок перезапуска {restartsFailed}; резерв сохранён.");
            }

            // Удаление резерва — необязательный хвост: не роняет успешную операцию.
            try
            {
                File.Delete(AdapterProfileBackupFile);
            }
            catch (Exception exception)
            {
                _logger.Warn("NET | adapter profile backup cleanup failed: " + exception.Message);
            }

            _logger.Info($"NET | adapter profile restored | {restored} значений");
            return Result.Success($"Свойства адаптеров восстановлены из резерва ({restored} значений).");
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
