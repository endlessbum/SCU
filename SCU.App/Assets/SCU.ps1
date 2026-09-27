[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Action,
    [string]$OutFile,
    [string]$InFile,
    [string]$Services,
    [string]$Tasks,
    [string]$BackupDir,
    [string]$ListFile,
    [string]$Names,
    [string]$Path,
    [string]$ComponentId,
    [int]$Index = 0,     [Int64]$MinBytes = 262144
)

$ErrorActionPreference = 'Stop'
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$LogFile = $env:SCU_LOGFILE
if (-not $LogFile) {
    try {
        $logDir = Join-Path $ProjectDir 'logs'
        if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force -ErrorAction Stop | Out-Null }$stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
        $LogFile = Join-Path $logDir ('SCU_' + $stamp + '.log'); $n = 1
        while (Test-Path -LiteralPath $LogFile) {$LogFile = Join-Path $logDir ('SCU_' +$stamp + '_' + $n + '.log'); $n++
        }
    } catch {
        $LogFile = $null
        Write-Warning ('Failed to prepare log file: ' + $_.Exception.Message)
    }
}

$script:LogWriteFailureReported = $false

function Write-Line([string]$Message) {
    Write-Host $Message
    if ($LogFile) {
        try {
            Add-Content -LiteralPath $LogFile -Value ('[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff') + '] PS | ' +$Message) -Encoding UTF8 -ErrorAction Stop
        } catch {
            if (-not $script:LogWriteFailureReported) {
                $script:LogWriteFailureReported = $true
                [Console]::Error.WriteLine('[LOG-ERROR] Failed to write log: ' + $_.Exception.Message)
            }
        }
    }
}

function Finish-Action([int]$Code, [string]$Result) {
    $status = if ($Code -eq 0) { 'OK' } else { 'ERROR' }
    Write-Line ("RESULT | action= $Action | rc= $Code | status= $status |$Result")
    exit $Code
}

trap {
    try {
        Write-Line ("FATAL | action= $Action | rc=99 | " + $_.Exception.Message)
    } catch { }
    exit 99
}

function Write-TextFile([string]$File, [System.Collections.IEnumerable]$Lines) {$enc = New-Object System.Text.UTF8Encoding($false); $arr = @()
    foreach ($l in $Lines) {
        $arr += [string]$l
    }
    [System.IO.File]::WriteAllLines($File, $arr,$enc)
}

function Get-TaskObject([string]$Full) {
    $i = $Full.LastIndexOf('\')
    if ($i -lt 0) { return $null }
    $tPath = $Full.Substring(0, $i + 1); $tName = $Full.Substring($i + 1)
    return Get-ScheduledTask -TaskPath $tPath -TaskName $tName -ErrorAction SilentlyContinue
}

switch ($Action.ToLowerInvariant()) {

    'servicesbackup' {
        $result = New-Object System.Collections.Generic.List[string]; $fail = 0
        foreach ($svc in ($Services -split ',')) {
            $svc = $svc.Trim()
            if (-not $svc) { continue }$key = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
            if (-not (Test-Path -LiteralPath $key)) {
                $result.Add("$svc|MISSING||")
                Write-Line ("MISS: " + $svc)
                continue
            }
            try {
                $props = Get-ItemProperty -LiteralPath $key -ErrorAction Stop
                if (-not ($props.PSObject.Properties.Name -contains 'Start')) { throw 'Start property missing' }
                $start = [string]$props.Start
                $delayed = ''
                if ($props.PSObject.Properties.Name -contains 'DelayedAutostart') { $delayed = [string]$props.DelayedAutostart }
                $svcObj = Get-Service -Name $svc -ErrorAction Stop
                $state = $svcObj.Status.ToString()
                if ($state -ne 'Running' -and $state -ne 'Stopped') { throw ('unsupported service state: ' + $state) }$result.Add("$svc|$start|$state|$delayed")
                Write-Line ("BACKUP: $svc Start= $start DelayedAutostart= $delayed State= $state")
            } catch {
                $fail++
                Write-Line ("ERR : $svc backup error: " + $_.Exception.Message)
            }
        }
        if ($OutFile) { Write-TextFile $OutFile $result }
        Write-Line ("DONE: service backup entries=" + $result.Count + " fail=" + $fail)
        if ($fail -gt 0) { Finish-Action 1 'backup incomplete' }
        Finish-Action 0 'backup complete'
    }

    'servicesrestore' {
        if (-not $InFile -or -not (Test-Path -LiteralPath $InFile)) { 
            Write-Line 'ERR: services backup file not found'
            Finish-Action 2 'completed' 
        }
        $startMap = @{ '0'='boot'; '1'='system'; '2'='auto'; '3'='demand'; '4'='disabled' };
        $filter = @()
        if ($Services) { 
            foreach ($f in ($Services -split ',')) { 
                $f = $f.Trim()
                if ($f) { $filter += $f.ToLowerInvariant() } 
            } 
        }
        $ok = 0; $fail = 0; $warn = 0
        foreach ($line in (Get-Content -LiteralPath $InFile -Encoding UTF8)) {
            if (-not $line.Trim()) { continue }
            $p = $line -split '\|'
            if ($p.Count -lt 3) {$fail++; Write-Line 'ERR : malformed service backup line'; continue }
            $svc = $p[0]
            if ($filter.Count -gt 0 -and ($filter -notcontains $svc.ToLowerInvariant())) { continue }$start = $p[1];$state = $p[2]; $delayed = if ($p.Count -ge 4) {$p[3] } else { '' }
            if ($start -eq 'MISSING' -or $start -eq '') { Write-Line ("SKIP: $svc (missing)"); continue }
            $target = $startMap[$start]
            if (-not $target) {$fail++; Write-Line ("ERR : $svc unknown start type $start"); continue }
            $startNumMap = @{ 'boot'=0; 'system'=1; 'auto'=2; 'demand'=3; 'disabled'=4 }
            & sc.exe config $svc start= $target | Out-Null
            $rc = $LASTEXITCODE
            if ($rc -ne 0) {
                # Ряд служб (msiserver и др.) запрещает sc config — пишем Start напрямую в реестр.
                try {
                    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
                    Set-ItemProperty -LiteralPath $key -Name Start -Type DWord -Value ([int]$startNumMap[$target]) -Force -ErrorAction Stop
                    $warn++; Write-Line ("WARN: $svc sc config ($rc), Start записан в реестр напрямую")
                } catch {
                    $fail++; Write-Line ("ERR : $svc sc config $target ($rc)")
                    continue
                }
            }
            try {
                $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
                if ($delayed -eq '1') {
                    if ($start -eq '2') {                          & sc.exe config $svc start= 'delayed-auto' | Out-Null
                        $rc = $LASTEXITCODE
                        if ($rc -ne 0) { throw "sc delayed-auto: $rc" } 
                    }
                    Set-ItemProperty -LiteralPath $key -Name DelayedAutostart -Type DWord -Value 1 -Force -ErrorAction Stop
                } elseif ($delayed -eq '0') {
                    Set-ItemProperty -LiteralPath $key -Name DelayedAutostart -Type DWord -Value 0 -Force -ErrorAction Stop
                } else {
                    Remove-ItemProperty -LiteralPath $key -Name DelayedAutostart -ErrorAction SilentlyContinue
                }
            } catch { $warn++; Write-Line ("WARN: $svc DelayedAutostart: $($_.Exception.Message)") }
            
            $stateOk = $true
            try {
                $svcObj = Get-Service -Name $svc -ErrorAction Stop
                if ($state -eq 'Running' -and $svcObj.Status -ne 'Running') {
                    if ($target -eq 'disabled') {
                        & sc.exe config $svc start= 'demand' | Out-Null
                        $rc = $LASTEXITCODE
                        if ($rc -ne 0) { throw "temporary demand: $rc" }
                        Start-Service -Name $svc -ErrorAction Stop
                        & sc.exe config $svc start= 'disabled' | Out-Null
                        $rc = $LASTEXITCODE
                        if ($rc -ne 0) { throw "restore disabled: $rc" }
                    } else { Start-Service -Name $svc -ErrorAction Stop }
                } elseif ($state -eq 'Stopped' -and $svcObj.Status -ne 'Stopped') { 
                    Stop-Service -Name $svc -Force -ErrorAction Stop 
                }
            } catch { $stateOk = $false; $fail++; Write-Line ("ERR : $svc state error: $($_.Exception.Message)") }
            
            if ($stateOk) { $ok++; Write-Line ("OK  : $svc restored") }
        }
        Write-Line ("DONE: ok= $ok fail= $fail warn= $warn")
        if ($fail -gt 0 -or $warn -gt 0) { Finish-Action 1 'completed' }
        Finish-Action 0 'completed'
    }

    'tasksbackup' {
        $res = New-Object System.Collections.Generic.List[string]
        foreach ($full in ($Tasks -split ';')) {
            $full = $full.Trim()
            if (-not $full) { continue }
            $t = Get-TaskObject $full
            if (-not $t) { $res.Add("$full`tMISSING"); continue }
            $res.Add("$full`t" + $t.State.ToString())
        }
        if ($OutFile) { Write-TextFile $OutFile $res }
        Write-Line ("OK: tasks backed up: " + $res.Count)
        Finish-Action 0 'completed'
    }

    'tasksdisable' {
        if ($OutFile -and (Test-Path -LiteralPath $OutFile)) {
            $arch = $OutFile + '.' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.bak'
            Move-Item -LiteralPath $OutFile -Destination $arch -Force
        }
        $res = New-Object System.Collections.Generic.List[string]; $fail = 0
        foreach ($full in ($Tasks -split ';')) {
            $full = $full.Trim()
            if (-not $full) { continue }
            $t = Get-TaskObject $full
            if (-not $t) {
                $res.Add("$full`tMISSING")
                Write-Line ("MISS: " + $full)
                continue
            }
            $res.Add("$full`t" + $t.State.ToString())
            $i = $full.LastIndexOf('\')
            $tPath = $full.Substring(0, $i + 1); $tName = $full.Substring($i + 1)
            try {
                Disable-ScheduledTask -TaskPath $tPath -TaskName $tName -ErrorAction Stop | Out-Null
                Write-Line ("OFF : " + $tName)
            }
            catch {
                $fail++
                Write-Line ("ERR : " + $tName + " - " + $_.Exception.Message)
            }
        }
        if ($OutFile) { Write-TextFile $OutFile $res }
        Write-Line ("DONE: fail=" + $fail)
        if ($fail -gt 0) { Finish-Action 1 'completed' }
        Finish-Action 0 'completed'
    }

    'tasksrestore' {
        if (-not $InFile -or -not (Test-Path -LiteralPath $InFile)) {             Write-Line 'ERR: tasks backup file not found'             Finish-Action 2 'completed'         }$fail = 0
        foreach ($line in (Get-Content -LiteralPath $InFile -Encoding UTF8)) {
            if (-not $line.Trim()) { continue }
            $p = $line -split "`t"
            $full = $p[0]
            $orig = ''
            if ($p.Count -ge 2) { $orig = $p[1] }
            $t = Get-TaskObject $full
            if (-not $t) {
                if ($orig -eq 'MISSING') { continue }
                $fail++
                Write-Line ("ERR : " + $full + ' task missing')
                continue
            }
            $i = $full.LastIndexOf('\')
            $tPath = $full.Substring(0, $i + 1)
            $tName = $full.Substring($i + 1)
            try {
                if ($orig -eq 'Disabled') {
                    Disable-ScheduledTask -TaskPath $tPath -TaskName $tName -ErrorAction Stop | Out-Null
                    Write-Line ("OFF : " + $tName)
                }
                else {
                    Enable-ScheduledTask -TaskPath $tPath -TaskName $tName -ErrorAction Stop | Out-Null
                    Write-Line ("ON  : " + $tName)
                }
            }
            catch {
                $fail++
                Write-Line ("ERR : " + $tName + " - " + $_.Exception.Message)
            }
        }
        Write-Line ("DONE: fail= $fail")
        if ($fail -gt 0) { Finish-Action 1 'completed' }
        Finish-Action 0 'completed'
    }

    'startuplist' {
        $items = New-Object System.Collections.Generic.List[object]
        $paths = @(
            @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\Run'; Label = 'HKCU Run' },
            @{ Hive = 'HKLM'; Path = 'Software\Microsoft\Windows\CurrentVersion\Run'; Label = 'HKLM Run' },
            @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\RunOnce'; Label = 'HKCU RunOnce' },
            @{ Hive = 'HKLM'; Path = 'Software\Microsoft\Windows\CurrentVersion\RunOnce'; Label = 'HKLM RunOnce' }
        )
        foreach ($p in $paths) {
            $psPath = if ($p.Hive -eq 'HKCU') { 'HKCU:\' + $p.Path } else { 'HKLM:\' + $p.Path }
            if (-not (Test-Path $psPath)) { continue }
            $key = Get-Item -Path $psPath -ErrorAction SilentlyContinue
            if (-not $key) { continue }
            foreach ($n in $key.GetValueNames()) {
                if (-not $n) { continue }
                $v = $key.GetValue($n, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $items.Add([pscustomobject]@{ Source = $p.Label; Name = $n; Cmd = [string]$v })
            }
        }
        $folders = @(
            @{ Path = [Environment]::GetFolderPath('Startup'); Label = 'Startup User' },
            @{ Path = [Environment]::GetFolderPath('CommonStartup'); Label = 'Startup Common' }
        )
        foreach ($f in $folders) {
            if (Test-Path -LiteralPath $f.Path) {
                Get-ChildItem -LiteralPath $f.Path -Force -ErrorAction SilentlyContinue |
                    Where-Object { -not $_.PSIsContainer } |
                    ForEach-Object {
                        $items.Add([pscustomobject]@{ Source = $f.Label; Name = $_.Name; Cmd = $_.FullName })
                    }
            }
        }
        $lines = New-Object System.Collections.Generic.List[string]
        $n = 0
        foreach ($it in $items) {
            $n++
            $cmd = ([string]$it.Cmd) -replace "`t", ' '
            $lines.Add(($n.ToString() + "`t" + $it.Source + "`t" + $it.Name + "`t" + $cmd))
        }
        if ($OutFile) { Write-TextFile $OutFile $lines }
        Finish-Action 0 'completed'
    }

    'startupdisable' {
        if (-not $ListFile -or -not (Test-Path -LiteralPath $ListFile)) {
            Write-Line 'ERR: startup list not found'
            Finish-Action 2 'completed'
        }
        $row = $null
        foreach ($l in (Get-Content -LiteralPath $ListFile -Encoding UTF8)) {
            $p = $l -split "`t", 4
            if ($p.Count -ge 4 -and $p[0] -eq [string]$Index) { $row = $p; break }
        }
        if (-not $row) {
            Write-Line 'ERR: item index not found'
            Finish-Action 3 'completed'
        }
        $src = $row[1]; $name = $row[2]; $cmd = $row[3]
        if (-not (Test-Path -LiteralPath $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }
        $mf = Join-Path $BackupDir 'manifest.json'
        $items = @()
        if (Test-Path -LiteralPath $mf) {
            try {
                $doc = Get-Content -LiteralPath $mf -Raw | ConvertFrom-Json
                $items = @($doc.items)
            }
            catch { $items = @() }
        }
        $entry = $null
        if ($src -match '^HKCU |^HKLM ') {
            $hive = if ($src -like 'HKCU*') { 'HKCU' } else { 'HKLM' }
            $sub = if ($src -like '*RunOnce') { 'Software\Microsoft\Windows\CurrentVersion\RunOnce' } else { 'Software\Microsoft\Windows\CurrentVersion\Run' }
            $psPath = $hive + ':\' + $sub
            $key = Get-Item -Path $psPath -ErrorAction Stop
            $kind = $key.GetValueKind($name).ToString(); $val = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); $entry = [pscustomobject]@{ type = 'reg'; hive = $hive; subkey = $sub; name = $name; kind = $kind; value = $val; src = $src }
            Remove-ItemProperty -Path $psPath -Name $name -Force -ErrorAction Stop
            Write-Line ('OK: removed from ' + $src + ': ' +$name)
        }
        else {
            try {
                $sourcePath = [System.IO.Path]::GetFullPath([string]$cmd)
            }
            catch {
                throw ('Некорректный путь автозагрузки: ' + [string]$cmd)
            }

            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                Write-Line ('ERR: file not found: ' + $sourcePath)
                Finish-Action 4 'completed'
            }

            # Не включаем исходное имя файла в имя backup-файла: это исключает
            # ошибку Invalid path при передаче необычного имени внешнего файла.
            $stored = Join-Path $BackupDir ('file_' + ([guid]::NewGuid().ToString('N')) + '.bak')
            Move-Item -LiteralPath $sourcePath -Destination $stored -Force -ErrorAction Stop
            $entry = [pscustomobject]@{ type = 'file'; original = $sourcePath; stored = $stored; name = $name; src = $src }
            Write-Line ('OK: moved to backup: ' + $name)
        }
        $items = @($items) + @($entry)
        $out = [pscustomobject]@{ version = 1; items = @($items) }
        ($out | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $mf -Encoding UTF8
        Finish-Action 0 'completed'
    }

    'startuprestore' {
        $mf = Join-Path $BackupDir 'manifest.json'
        if (-not (Test-Path -LiteralPath $mf)) { Write-Line 'ERR: startup manifest not found'; Finish-Action 2 'completed' }
        try { $doc = Get-Content -LiteralPath $mf -Raw | ConvertFrom-Json } catch { Write-Line 'ERR: invalid manifest'; Finish-Action 3 'completed' }
        $items = @($doc.items)
        if ($items.Count -eq 0) { Write-Line 'EMPTY: backup is empty'; Finish-Action 0 'completed' }$remaining = New-Object System.Collections.Generic.List[object]
        foreach ($e in $items) { [void]$remaining.Add($e) }
        $ok = 0; $fail = 0
        foreach ($e in $items) {
            $itemOk = $false
            try {
                if ($e.type -eq 'reg') {$psPath = $e.hive + ':\' +$e.subkey
                    if (-not (Test-Path -LiteralPath $psPath)) { New-Item -Path $psPath -Force -ErrorAction Stop | Out-Null }$pt = 'String'
                    switch ($e.kind) {
                        'String' { $pt = 'String' }
                        'ExpandString' { $pt = 'ExpandString' }
                        'DWord' { $pt = 'DWord' }
                        'QWord' { $pt = 'QWord' }
                        'MultiString' { $pt = 'MultiString' }
                        'Binary' { $pt = 'Binary' }
                    }
                    $v = $e.value
                    if ($pt -eq 'MultiString') { $v = [string[]]@($v) }
                    elseif ($pt -eq 'Binary') {$v = [byte[]]@($v | ForEach-Object { [byte]$_ }) }
                    elseif ($pt -eq 'DWord') { $v = [int]$v }
                    elseif ($pt -eq 'QWord') { $v = [long]$v }
                    else { $v = [string]$v }
                    New-ItemProperty -Path $psPath -Name $e.name -Value $v -PropertyType $pt -Force -ErrorAction Stop | Out-Null
                    Write-Line ('OK  : ' + $e.src + ': ' +$e.name); $itemOk = $true
                } elseif ($e.type -eq 'file') {
                    if (-not (Test-Path -LiteralPath $e.stored)) { throw 'backup file missing' }
                    if (Test-Path -LiteralPath $e.original) { throw 'target file exists' }
                    $parent = Split-Path -Parent $e.original
                    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force -ErrorAction Stop | Out-Null }
                    Move-Item -LiteralPath $e.stored -Destination $e.original -ErrorAction Stop
                    Write-Line ('OK  : ' + $e.original); $itemOk = $true
                } else { throw ('unknown entry type: ' + $e.type) }
            } catch { $fail++; Write-Line ('ERR : ' + [string]$e.name + ' - ' +$_.Exception.Message) }
            
            if ($itemOk) {$ok++
                for ($i = $remaining.Count - 1; $i -ge 0; $i--) {
                    if (($remaining[$i].type -eq $e.type) -and ([string]$remaining[$i].name -eq [string]$e.name) -and ([string]$remaining[$i].src -eq [string]$e.src)) {
                        $remaining.RemoveAt($i)
                        break
                    }
                }
            }
        }
        $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
        if ($remaining.Count -eq 0) {
            try { Move-Item -LiteralPath $mf -Destination (Join-Path $BackupDir ('manifest_restored_' +$stamp + '.json')) -Force } catch {}
            ([pscustomobject]@{ version = 1; items = @() } | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $mf -Encoding UTF8
        } else {
            ([pscustomobject]@{ version = 1; items = @($remaining) } | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $mf -Encoding UTF8
        }
        Write-Line ("DONE: ok= $ok fail= $fail remaining=$($remaining.Count)")
        if ($fail -gt 0) { Finish-Action 1 'completed' }
        Finish-Action 0 'completed'
    }

    'emptyrecyclebin' {
        try {
            Clear-RecycleBin -Force -ErrorAction Stop
            Write-Line 'OK: recycle bin cleared'
            Finish-Action 0 'completed'
        }
        catch {
            $msg =$_.Exception.Message
            # Копаемся до внутреннего исключения: -ErrorAction Stop оборачивает ошибку
            # в ActionPreferenceStopException, у которого HResult не оригинальный.
            $inner = $_.Exception
            while ($inner.InnerException) { $inner = $inner.InnerException }
            # 0x80070002 на пустой корзине / «Корзина пуста» — нормальное состояние, не ошибка.
            if ($inner.HResult -eq -2147024894 -or $msg -match '(?i)Recycle Bin is empty' -or $msg -match 'пуста') {
                Write-Line ('OK: recycle bin already empty')
                Finish-Action 0 'already empty'
            }
            Write-Line ('ERR: ' + $msg)
            Finish-Action 1 'clear failed'
        }
    }

    'verifyexe' {
        if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { Write-Line 'MISSING'; Finish-Action 2 'completed' }
        try { $fi = Get-Item -LiteralPath $Path -ErrorAction Stop } catch { Write-Line ('STAT_ERROR: ' +$_.Exception.Message); Finish-Action 3 'completed' }
        if ($fi.Length -lt $MinBytes) { Write-Line ('TOO_SMALL: ' +$fi.Length + ' bytes'); Finish-Action 3 'completed' }
        try { $sig = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop } catch { Write-Line ('SIGNATURE_ERROR: ' +$_.Exception.Message); Finish-Action 6 'completed' }
        if ($sig.Status -ne 'Valid') { Write-Line ('BAD_STATUS: ' +$sig.Status); Finish-Action 4 'completed' }
        $subj = '';$simple = ''
        if ($sig.SignerCertificate) {
            $subj = [string]$sig.SignerCertificate.Subject
            try { $simple = [string]$sig.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false) } catch {}
        }
        if ($subj -notmatch '(?i)O=Microsoft Corporation' -and $simple -notmatch '(?i)^Microsoft Corporation$') {
            Write-Line ('BAD_PUBLISHER: ' + $subj); Finish-Action 5 'completed'
        }
        Write-Line ('Microsoft Corporation; size=' + $fi.Length); Finish-Action 0 'completed'
    }

    'appxscan' {
        $map = [ordered]@{
            'Cam' = @('WindowsCamera'); 'Dev' = @('DevHome'); 'Hub' = @('WindowsFeedbackHub');
            'Copilot' = @('Copilot', 'Windows.Ai'); 'Bing' = @('BingSearch'); 'Clip' = @('Clipchamp');
            'News' = @('BingNews'); 'Teams' = @('MSTeams'); 'ToDo' = @('Todos');
            'Outlook' = @('OutlookForWindows'); 'Power' = @('PowerAutomateDesktop'); 'Quick' = @('QuickAssist');
            'Sol' = @('SolitaireCollection'); 'Sound' = @('WindowsSoundRecorder'); 'Sticky' = @('StickyNotes');
            'Store' = @('WindowsStore', 'StorePurchaseApp');
            # Xbox: только съёмные компоненты (совпадает с AppxRemove Names). Общий шаблон
            # 'Xbox' ловил несъёмный системный XboxGameCallableUI — после удаления всех
            # компонентов скан вечно показывал «установлено».
            'Xbox' = @('XboxApp', 'GamingApp', 'XboxGamingOverlay', 'XboxGameOverlay', 'XboxIdentityProvider', 'XboxSpeechToTextOverlay', 'Xbox.TCUI')
        }
        try { 
            $installed = @(Get-AppxPackage -AllUsers -ErrorAction Stop) 
        } catch {
            $lines = New-Object System.Collections.Generic.List[string]; $lines.Add('scan=0')
            foreach ($k in $map.Keys) { $lines.Add($k + '=unknown') }
            if ($OutFile) { Write-TextFile $OutFile $lines } else { foreach ($l in $lines) { Write-Line $l } }
            Write-Line ('SCAN_ERROR: ' + $_.Exception.Message)
            Finish-Action 1 'completed'
        }
        $lines = New-Object System.Collections.Generic.List[string]; $lines.Add('scan=1')
        foreach ($k in $map.Keys) {$hit = 0; $hitNames = @()
            foreach ($pat in $map[$k]) {
                foreach ($p in $installed) {
                    if ($p.Name -and $p.Name -like ('*' + $pat + '*')) {$hit = 1; $hitNames += $p.Name }
                }
            }
            $lines.Add($k + '=' +$hit)
            # Xbox: имена оставшихся пакетов — для показа «Не удалось удалить: …».
            if ($k -eq 'Xbox' -and $hitNames.Count -gt 0) { $lines.Add('XboxLeft=' + ((@($hitNames | Sort-Object -Unique)) -join ',') ) }
        }
        if ($OutFile) { Write-TextFile $OutFile $lines } else { foreach ($l in $lines) { Write-Line $l } }
        Finish-Action 0 'completed'
    }

    'appxremove' {
        $pats = @()
        foreach ($x in ($Names -split ',')) {
            $x = $x.Trim()
            if ($x) { $pats += $x }
        }
        if ($pats.Count -eq 0) { Write-Line 'ERR: no package name specified'; Finish-Action 2 'completed' }
        try { $pkgs = @(Get-AppxPackage -AllUsers -ErrorAction Stop) } catch { Write-Line ('ERR: failed to get packages: ' + $_.Exception.Message); Finish-Action 10 'completed' }$matches = @($pkgs | Where-Object {$name = $_.Name; $name -and ($pats | Where-Object {$name -like ('*' + $_ + '*') }) } | Sort-Object PackageFullName -Unique); $prov = @(); $provOk = $true
        try { $prov = @(Get-AppxProvisionedPackage -Online -ErrorAction Stop) } catch {$provOk = $false; Write-Line ('WARN: failed to read provisioned packages') }$provMatches = @()
        if ($provOk) {
            $provMatches = @($prov | Where-Object { $name =$_.DisplayName; $name -and ($pats | Where-Object { $name -like ('*' +$_ + '*') }) } | Sort-Object PackageName -Unique)
        }
        if ($matches.Count -eq 0 -and $provOk -and $provMatches.Count -eq 0) { Write-Line 'ALREADY_ABSENT: package not found'; Finish-Action 0 'completed' }
        if ($matches.Count -eq 0 -and -not $provOk) { Write-Line 'ERR: package not found and provisioned state unknown'; Finish-Action 1 'completed' }
        
        $removed = 0; $err = 0
        foreach ($p in $matches) {
            if ($p.NonRemovable -eq $true) { $err++; Write-Line ('ERR: non-removable package: ' +$p.Name); continue }
            try { Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction Stop; $removed++; Write-Line ('REMOVED: ' +$p.PackageFullName) } catch { $err++; Write-Line ('ERR: ' +$_.Exception.Message) }
        }
        if ($provOk) {
            foreach ($d in $provMatches) {
                try { Remove-AppxProvisionedPackage -Online -PackageName $d.PackageName -ErrorAction Stop | Out-Null; $removed++; Write-Line ('PROVISIONED_REMOVED: ' +$d.PackageName) } catch { $err++; Write-Line ('ERR: ' +$_.Exception.Message) }
            }
        }
        
        $verifyInstalled = @()
        try { $verifyInstalled = @(Get-AppxPackage -AllUsers -ErrorAction Stop | Where-Object { $name =$_.Name; $name -and ($pats | Where-Object { $name -like ('*' +$_ + '*') }) }) } catch { $err++ }$verifyProv = @()
        if ($provOk) {
            try { $verifyProv = @(Get-AppxProvisionedPackage -Online -ErrorAction Stop | Where-Object {$name = $_.DisplayName; $name -and ($pats | Where-Object {$name -like ('*' + $_ + '*') }) }) } catch {$provOk = $false; $err++ }
        }
        
        Write-Line ('REMOVED=' + $removed)
        Write-Line ('ERRORS=' + $err)
        
        if ($verifyInstalled.Count -eq 0 -and $provOk -and $verifyProv.Count -eq 0 -and $err -eq 0) { Write-Line 'RESULT=ABSENT'; Finish-Action 0 'completed' }
        Finish-Action 1 'completed'
    }

    'componentslist' {
        Write-Line "OK: components listed"
        Finish-Action 0 'completed'
    }

    'componentsinstall' {
        if (-not $ComponentId) {
            Write-Line "ERR: component id not specified"
            Finish-Action 2 'completed'
        }
        Write-Line ("INSTALL: installing component $ComponentId")
        Finish-Action 0 'completed'
    }

    default {
        Write-Line ('ERR: unknown action "' + $Action + '"')
        Finish-Action 9 'completed'
    }
}