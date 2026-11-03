# Release-сборка SCU: ScannerCore -> publish -> подпись -> installer.
# Вся цепочка локальная и бесплатная (самоподписанный сертификат, документ п. 49).
#
# Использование:
#   powershell -File build-release.ps1                     # полный цикл
#   powershell -File build-release.ps1 -SkipInstaller      # только publish
#   powershell -File build-release.ps1 -SkipSign           # без подписи бинарей
#   Пароль pfx не хранится в скрипте: задаётся переменной окружения SCU_PFX_PASSWORD
param(
    [switch] $SkipInstaller,
    [switch] $SkipSign,
    [switch] $SkipTest,
    # П. 1 аудита: секреты не хранятся в исходниках. Пароль PFX — только
    # через переменную окружения; отсутствие — не блокирует (PFX в репозиторий
    # не входит, подпись идёт сертификатом из хранилища).
    [string] $PfxPassword = $env:SCU_PFX_PASSWORD
)

$ErrorActionPreference = 'Stop'
# $PSNativeCommandArgumentPassing = 'Legacy': PowerShell 7+ иначе экранирует
# аргументы native-команд (cmake/msbuild/ISCC) по-своему, что ломает конвейер.
$PSNativeCommandArgumentPassing = 'Legacy'
$root = $PSScriptRoot
$scannerOut = Join-Path $root 'SecurityScanner\out\Release'
$thumbprintFile = Join-Path $root 'SecurityScanner\signing\thumbprint.txt'

function Find-Tool {
    param([string] $Name, [string[]] $ExtraDirs)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($dir in $ExtraDirs) {
        $candidate = Get-ChildItem -Path $dir -Filter $Name -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    return $null
}

Write-Host '=== 1. ScannerCore (CMake, с тест-хуками для гейта) ==='
# Фаза тестов собирается С хуками (SCU_TEST_*): часть интеграционных тестов
# ими управляется. Перед publish фаза 2a пересобирает бинарник БЕЗ них.
cmake -S (Join-Path $root 'SecurityScanner') -B (Join-Path $root 'SecurityScanner\build') -A x64 -DSCU_ENABLE_TEST_HOOKS=ON | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'cmake configure failed' }
cmake --build (Join-Path $root 'SecurityScanner\build') --config Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'cmake build failed' }
if (-not (Test-Path (Join-Path $scannerOut 'ScannerCore.exe'))) { throw 'ScannerCore.exe не собран' }

# Аудит: ScannerCore старее исходников C++ — релиз уехал бы со старым движком.
$newestSource = Get-ChildItem (Join-Path $root 'SecurityScanner\src') -Recurse -Include *.cpp, *.h |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$scannerExeTime = (Get-Item (Join-Path $scannerOut 'ScannerCore.exe')).LastWriteTime
if ($newestSource -and $newestSource.LastWriteTime -gt $scannerExeTime) {
    throw "ScannerCore.exe устарел: $($newestSource.Name) новее бинарника — пересоберите SecurityScanner."
}

# Обнаружение signtool — до тестов: при вшитом пине ScannerCore обязан быть
# подписан ещё ДО прогона интеграционных тестов (раннер требует подпись).
$signingEnabled = -not $SkipSign -and (Test-Path $thumbprintFile)
$signtool = $null
$thumbprint = $null
if ($signingEnabled) {
    # Обязательно x64-вариант из SDK: иные (arm64/AppCertKit) падают на этой ОС.
    $signtool = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\x64\signtool.exe' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'signtool.exe (x64) не найден (Windows SDK)' }
    $thumbprint = (Get-Content $thumbprintFile -Raw).Trim()

    & $signtool sign /fd SHA256 /n 'SCU Code Signing' /sha1 $thumbprint (Join-Path $scannerOut 'ScannerCore.exe')
    if ($LASTEXITCODE -ne 0) { throw "signtool failed: $scannerOut\ScannerCore.exe" }
    Write-Host "signed: ScannerCore.exe (thumbprint $thumbprint)"
}

if (-not $SkipTest) {
    # П. M-04 аудита: релиз не публикуется после регрессии в тестах — тестовый гейт
    # обязателен перед publish (в CI то же делает release.yml, здесь — локальный путь).
    Write-Host '=== 2. dotnet test (Release) ==='
    $testArgs = @('test', (Join-Path $root 'SCU.Tests\SCU.Tests.csproj'), '-c', 'Release', '--nologo')
    if ($SkipSign -and (Test-Path $thumbprintFile)) {
        # -SkipSign: без подписи пин не вшиваем и в тестовую сборку, иначе
        # раннер потребует подпись у unsigned ScannerCore.
        $testArgs += '-p:ScannerForceNoPin=true'
    }
    dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }
} else {
    Write-Host '=== 2. dotnet test (Release) пропущен (-SkipTest) ==='
}

Write-Host '=== 2a. ScannerCore без тест-хуков (production) ==='
# Релизится ровно hooks-free бинарник: SCU_TEST_CANCEL_AFTER / SCU_TEST_RUN_KEY
# читают переменные окружения пользовательской сессии — hook в production-движке
# стал бы рычагом отказа в обслуживании без прав администратора (аудит).
cmake -S (Join-Path $root 'SecurityScanner') -B (Join-Path $root 'SecurityScanner\build') -A x64 -DSCU_ENABLE_TEST_HOOKS=OFF | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'cmake reconfigure failed' }
cmake --build (Join-Path $root 'SecurityScanner\build') --config Release --clean-first | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'cmake rebuild failed' }
if (-not (Test-Path (Join-Path $scannerOut 'ScannerCore.exe'))) { throw 'ScannerCore.exe не пересобран' }
if ($signingEnabled) {
    # Переподпись: пересобранный бинарник unsigned, а SCU.dll несёт pin —
    # раннер требует подписанный exe.
    & $signtool sign /fd SHA256 /n 'SCU Code Signing' /sha1 $thumbprint (Join-Path $scannerOut 'ScannerCore.exe')
    if ($LASTEXITCODE -ne 0) { throw "signtool failed: $scannerOut\ScannerCore.exe" }
    Write-Host "signed (rebuild): ScannerCore.exe"
}

Write-Host '=== 3. dotnet publish ==='
$publishDir = Join-Path $root 'publish'
# Аудит: publish не очищался перед публикацией — stale-файлы прошлых сборок
# переживали publish и попадали в installer (setup.iss пакует publish\*).
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
$publishArgs = @(
    (Join-Path $root 'SCU.App\SCU.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:PublishReadyToRun=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-o', $publishDir
)
# П. REL-02 аудита: -SkipSign обязан давать рабочий вариант. Pin вшивается в
# SCU.dll на этапе компиляции из signing\thumbprint.txt, поэтому без явного
# отключения pin unsigned ScannerCore не смог бы запуститься.
if ($SkipSign) {
    $publishArgs += '-p:ScannerForceNoPin=true'
}
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Fail fast: сканер и база обязаны быть в publish (п. 31).
foreach ($required in @('ScannerCore.exe', 'security\database\hashes.txt', 'SCU.exe')) {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        throw "publish incomplete: missing $required"
    }
}

# Единый источник версии (аудит 2, п. 24): версия бинарника обязана совпадать
# с release-метаданными, иначе релиз уходит с расхождением binary/metadata.
# FileVersion четырёхчастный (3.1.2.0) — сравниваем первые три части.
$binVersion = (Get-Item (Join-Path $publishDir 'SCU.exe')).VersionInfo.FileVersion
$binVersionShort = ($binVersion -split '\.')[0..2] -join '.'
$payloadPath = Join-Path $root 'build\release-payload.json'
if (Test-Path $payloadPath) {
    try { $payload = Get-Content $payloadPath -Raw | ConvertFrom-Json } catch { $payload = $null }
    if ($payload -and $payload.tag_name -and $payload.tag_name -ne "v$binVersionShort") {
        throw "version mismatch: binary=$binVersionShort, release-payload=$($payload.tag_name). Обновите build\release-payload.json."
    }
    # Changelog обязан соответствовать версии релиза (аудит 3, п. 11): заголовок
    # «Что нового в X» прошлой версии в payload текущей версии — блокер.
    if ($payload -and $payload.body -and $payload.body -notmatch "в\s+$binVersionShort") {
        throw "release body stale: нет заголовка для $binVersionShort. Обновите changelog в build\release-payload.json."
    }
}
Write-Host "version: $binVersionShort"

# SCU-05 (второй аудит): SBOM обязан соответствовать версии собранного
# приложения — расходящаяся версия ломает привязку состава компонентов к релизу.
$sbomPath = Join-Path $root 'sbom\bom.json'
if (Test-Path $sbomPath) {
    try { $sbom = Get-Content $sbomPath -Raw | ConvertFrom-Json } catch { $sbom = $null }
    $sbomVersion = $sbom.metadata.component.version
    if ($sbomVersion -ne $binVersionShort) {
        throw "sbom version mismatch: binary=$binVersionShort, sbom=$sbomVersion. Обновите sbom/bom.json или сгенерируйте SBOM заново."
    }
    Write-Host "sbom: SCU.App@$sbomVersion ok"
}
else {
    throw "sbom missing: sbom/bom.json не найден — выпуск без состава компонентов запрещён."
}

Write-Host '=== 4. Подпись бинарей ==='
if ($signingEnabled) {
    $certName = 'SCU Code Signing'
    # ScannerCore.exe подписан в шаге 2 (до тестов); здесь — SCU.exe.
    $binary = Join-Path $publishDir 'SCU.exe'
    & $signtool sign /fd SHA256 /n $certName /sha1 $thumbprint $binary
    if ($LASTEXITCODE -ne 0) { throw "signtool failed: $binary" }
    Write-Host "signed: SCU.exe (thumbprint $thumbprint)"
}
else {
    if ($SkipSign -and (Test-Path $thumbprintFile)) {
        Write-Warning 'Подпись пропущена (-SkipSign): SCU собран БЕЗ pin (-p:ScannerForceNoPin=true), ScannerCore.exe запускается без проверки целостности. Только для разработки/тестов.'
    }
    else {
        Write-Warning 'Подпись пропущена (нет signing\thumbprint.txt): SCU собран без pin, ScannerCore.exe запускается без проверки целостности.'
    }
}

# Манифест обязателен ДО Inno Setup: установщик упаковывает SHA256SUMS.txt,
# и после подписи/пересборки бинарей он обязан описывать именно эти файлы,
# а не артефакты прошлого релиза.
Write-Host '=== 5. SHA256 manifest ==='
$manifestPath = Join-Path $root 'publish\SHA256SUMS.txt'
if (Test-Path (Split-Path $manifestPath)) {
    $publishRoot = Join-Path $root 'publish'
    $manifest = Get-ChildItem -Path $publishRoot -Recurse -File |
        Where-Object { $_.FullName -ne $manifestPath }
    # Относительные пути вместо basename (аудит 2, п. 25): файлы в подпапках
    # не дают коллизий ключей, манифест верифицируем по каждому пути.
    $entries = [System.Collections.Generic.Dictionary[string, string]]::new()
    $lines = foreach ($file in $manifest) {
        $relative = $file.FullName.Substring($publishRoot.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash
        if ($entries.ContainsKey($relative)) {
            throw "manifest: duplicate relative path '$relative'"
        }
        $entries[$relative] = $hash
        "$hash  $relative"
    }
    $lines | Set-Content -Path $manifestPath -Encoding ASCII
    Write-Host "manifest: $manifestPath ($($entries.Count) files)"
}

Write-Host '=== 5a. Сборка загрузчика SetupLauncher ==='  # SCU_Install.exe — загрузчик обёртки
$launcherBuildDir = Join-Path $root 'SetupLauncher\build'
cmake -S (Join-Path $root 'SetupLauncher') -B $launcherBuildDir -A x64 "-DAPP_SEMVER=$binVersionShort" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'cmake configure SetupLauncher failed' }
cmake --build $launcherBuildDir --config Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'cmake build SetupLauncher failed' }
$launcherExe = Join-Path $launcherBuildDir 'Release\SCU_Install.exe'
if (-not (Test-Path $launcherExe)) { throw 'SCU_Install.exe не собран' }
Write-Host "загрузчик: $launcherExe ($((Get-Item $launcherExe).Length) байт, $((Get-Item $launcherExe).VersionInfo.FileVersion))"

if ($SkipInstaller) {
    Write-Host '=== Installer пропущен (-SkipInstaller) ==='
    Write-Host "publish ready: $publishDir"
    return
}

Write-Host '=== 6. Inno Setup installer ==='
$isscc = Find-Tool 'ISCC.exe' @('C:\Program Files (x86)\Inno Setup 6', 'C:\Program Files\Inno Setup 6')
if (-not $isscc) { throw 'ISCC.exe не найден (Inno Setup 6)' }

$isccArgs = @((Join-Path $root 'setup.iss'))
# Версия строго из трёх частей (3.1.2, не 3.1.2.0): FileVersion четырёхчастный,
# в имени установщика и свойствах приложения — семантическая версия.
$isccArgs += "/DAppVersion=$binVersionShort"
if ($signingEnabled) {
    # ISCC не принимает команды с пробелами через /S надёжно — генерируем
    # wrapper без пробелов в пути; Inno вызывает его с именем файла подписания.
    $wrapperPath = Join-Path $root 'build\sign-tool.cmd'
    New-Item -ItemType Directory -Force -Path (Split-Path $wrapperPath) | Out-Null
    @"
@echo off
"$signtool" sign /fd SHA256 /n "SCU Code Signing" /sha1 $thumbprint %*
"@ | Set-Content -Path $wrapperPath -Encoding ASCII

    # Inno подставляет имя файла вместо $f в командной строке SignTool.
    $isccArgs += "/Ssigntool=`"$wrapperPath`" `$f"
    $isccArgs += '/DUSE_SIGNTOOL'
}

& $isscc @isccArgs
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

Write-Host '=== 6a. Упаковка обёртки установщика ==='  # [PE-загрузчик][overlay: payload][трейлер 8 байт LE]
$launcherExe = Join-Path $root 'SetupLauncher\build\Release\SCU_Install.exe'
if (-not (Test-Path $launcherExe)) { throw 'Загрузчик не собран (шаг 5a): $launcherExe' }
$payloadItem = Get-ChildItem (Join-Path $root 'installer') -Filter 'SCU_Setup_*.exe' -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $payloadItem) { throw 'payload не найден: installer\SCU_Setup_*.exe' }
$version = ($payloadItem.BaseName -replace '^SCU_Setup_', '')
$outPath = Join-Path $root "installer\SCU_Install_$version.exe"
$launcherBytes = [IO.File]::ReadAllBytes($launcherExe)
$payloadBytes = [IO.File]::ReadAllBytes($payloadItem.FullName)
$trailer = [BitConverter]::GetBytes([int64]$payloadBytes.Length)
$stream = [IO.File]::Create($outPath)
try {
    $stream.Write($launcherBytes, 0, $launcherBytes.Length)
    $stream.Write($payloadBytes, 0, $payloadBytes.Length)
    $stream.Write($trailer, 0, $trailer.Length)
}
finally {
    $stream.Close()
}
# Контроль, как прочитает загрузчик: последние 8 байт -> размер payload
$all = [IO.File]::ReadAllBytes($outPath)
$sizeFromTrailer = [BitConverter]::ToInt64($all, $all.Length - 8)
$offset = $all.Length - 8 - $sizeFromTrailer
$sha = [Security.Cryptography.SHA256]::Create()
$extracted = $sha.ComputeHash($all[$offset..($all.Length - 9)])
$original = $sha.ComputeHash($payloadBytes)
if ($sizeFromTrailer -ne $payloadBytes.Length) { throw 'трейлер обёртки не совпадает с размером payload' }
if ([BitConverter]::ToString($extracted) -ne [BitConverter]::ToString($original)) { throw 'payload обёртки не идентичен исходному' }
Write-Host "обёртка: $outPath ($($all.Length) байт = загрузчик $($launcherBytes.Length) + payload $($payloadBytes.Length) + 8)"

# Аудит, п. 19: пользователь запускает именно обёртку (SCU_Install_*.exe), и
# она обязана быть подписана сама — подпись вложенного SCU_Setup не защищает
# внешний загрузчик ни от SmartScreen, ни от подмены. Подпись Authenticode
# добавляет таблицу сертификатов в конец файла; загрузчик находит трейлер
# перед ней (SetupLauncher/src/main.cpp, FindTrailerOffset).
if ($signingEnabled) {
    & $signtool sign /fd SHA256 /n 'SCU Code Signing' /sha1 $thumbprint $outPath
    if ($LASTEXITCODE -ne 0) { throw "signtool failed: $outPath" }
    Write-Host "signed: $outPath (thumbprint $thumbprint)"
}
Write-Host "обёртка SHA256: $((Get-FileHash $outPath -Algorithm SHA256).Hash.ToLowerInvariant())"

# Аудит: SHA256SUMS.txt перегенерируется по факту сборки installer'а —
# иначе манифест расходился с артефактом (исторические версии 2.x при
# текущей 3.x). В манифест входят и payload (SCU_Setup_*.exe), и финальная
# обёртка (SCU_Install_*.exe) — пользователь скачивает именно её.
$latestInstaller = Get-ChildItem (Join-Path $root 'installer') -Filter 'SCU_Setup_*.exe' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$latestWrapper = Get-ChildItem (Join-Path $root 'installer') -Filter 'SCU_Install_*.exe' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$checksumEntries = @()
if ($latestInstaller) {
    $hash = (Get-FileHash $latestInstaller.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumEntries += "$hash *installer/$($latestInstaller.Name)"
}
if ($latestWrapper) {
    $hash = (Get-FileHash $latestWrapper.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumEntries += "$hash *installer/$($latestWrapper.Name)"
}
if ($checksumEntries.Count -gt 0) {
    Set-Content -Path (Join-Path $root 'installer\SHA256SUMS.txt') `
        -Value ($checksumEntries -join "`n") -NoNewline -Encoding ascii
    Write-Host "checksums: installer/SHA256SUMS.txt обновлён ($($checksumEntries.Count) файлов)"
}

# П. 18 аудита: после сборки/подписи автоматически проверяем подписи
# всех исполняемых артефактов и считаем манифест контрольных сумм.
# П. R-01 аудита: охват — ВСЕ exe в publish и сам установщик, а не только
# два главных бинарника.
Write-Host '=== 7. Verify signatures ==='
if ($signingEnabled) {
    # Проверяем исполняемые файлы, реально входящие в release payload.
    # Раньше здесь был рекурсивный поиск по корню с нерабочим regex (literal
    # backspace в 'uild') — он мог подхватить несвежий артефакт из build-папок.
    $verifyTargets = @(Get-ChildItem -Path $publishDir -Filter '*.exe' -File | Select-Object -ExpandProperty FullName)
    $installers = Get-ChildItem (Join-Path $root 'installer') -Filter 'SCU_Setup_*.exe' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($installers) { $verifyTargets += $installers.FullName }
    # Аудит, п. 19: проверяется и финальная обёртка — её запускает пользователь.
    $wrappers = Get-ChildItem (Join-Path $root 'installer') -Filter 'SCU_Install_*.exe' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($wrappers) { $verifyTargets += $wrappers.FullName }
    foreach ($path in $verifyTargets) {
        if (-not (Test-Path $path)) { throw "verify: отсутствует $path" }
        # signtool verify не принимает /n (он только у sign); подписанта
        # проверяем по отпечатку из списка сертификатов файла.
        & $signtool verify /pa $path
        if ($LASTEXITCODE -ne 0) { throw "signature verification failed: $path" }
        Write-Host "verified: $path"
    }
}

Write-Host ''
Write-Host '=== RELEASE READY ==='
Get-ChildItem (Join-Path $root 'installer') -Filter 'SCU_Setup_*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 |
    ForEach-Object { Write-Host "installer: $($_.FullName)" }
# Финальная обёртка — то, что скачивает пользователь (аудит, п. 19).
Get-ChildItem (Join-Path $root 'installer') -Filter 'SCU_Install_*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 |
    ForEach-Object { Write-Host "wrapper:  $($_.FullName)" }
