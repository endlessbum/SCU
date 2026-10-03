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
    # П. 1 аудита: секреты не хранятся в исходниках. Пароль PFX — только
    # через переменную окружения; отсутствие — не блокирует (PFX в репозиторий
    # не входит, подпись идёт сертификатом из хранилища).
    [string] $PfxPassword = $env:SCU_PFX_PASSWORD
)

$ErrorActionPreference = 'Stop'
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

Write-Host '=== 1. ScannerCore (CMake) ==='
cmake -S (Join-Path $root 'SecurityScanner') -B (Join-Path $root 'SecurityScanner\build') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'cmake configure failed' }
cmake --build (Join-Path $root 'SecurityScanner\build') --config Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'cmake build failed' }
if (-not (Test-Path (Join-Path $scannerOut 'ScannerCore.exe'))) { throw 'ScannerCore.exe не собран' }

Write-Host '=== 2. dotnet publish ==='
$publishDir = Join-Path $root 'publish'
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

Write-Host '=== 3. Подпись бинарей ==='
$signingEnabled = -not $SkipSign -and (Test-Path $thumbprintFile)
if ($signingEnabled) {
    # Обязательно x64-вариант из SDK: иные (arm64/AppCertKit) падают на этой ОС.
    $signtool = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\x64\signtool.exe' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'signtool.exe (x64) не найден (Windows SDK)' }

    $thumbprint = (Get-Content $thumbprintFile -Raw).Trim()
    $certName = 'SCU Code Signing'
    # /n надёжнее /sha1 при нескольких сертификатах в хранилище; совпадение
    # отпечатка проверит сам ScannerCore (пиннинг) и signtool найдёт по имени.
    foreach ($binary in @((Join-Path $publishDir 'ScannerCore.exe'), (Join-Path $publishDir 'SCU.exe'))) {
        & $signtool sign /fd SHA256 /n $certName /sha1 $thumbprint $binary
        if ($LASTEXITCODE -ne 0) { throw "signtool failed: $binary" }
    }
    Write-Host "signed: ScannerCore.exe, SCU.exe (thumbprint $thumbprint)"
}
else {
    if ($SkipSign -and (Test-Path $thumbprintFile)) {
        Write-Warning 'Подпись пропущена (-SkipSign): SCU собран БЕЗ pin (-p:ScannerForceNoPin=true), ScannerCore.exe запускается без проверки целостности. Только для разработки/тестов.'
    }
    else {
        Write-Warning 'Подпись пропущена (нет signing\thumbprint.txt): SCU собран без pin, ScannerCore.exe запускается без проверки целостности.'
    }
}

if ($SkipInstaller) {
    Write-Host '=== Installer пропущен (-SkipInstaller) ==='
    Write-Host "publish ready: $publishDir"
    return
}

Write-Host '=== 4. Inno Setup installer ==='
$isscc = Find-Tool 'ISCC.exe' @('C:\Program Files (x86)\Inno Setup 6', 'C:\Program Files\Inno Setup 6')
if (-not $isscc) { throw 'ISCC.exe не найден (Inno Setup 6)' }

$isccArgs = @((Join-Path $root 'setup.iss'))
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

# П. 18 аудита: после сборки/подписи автоматически проверяем подписи
# всех исполняемых артефактов и считаем манифест контрольных сумм.
Write-Host '=== 5. Verify signatures + SHA256 manifest ==='
if ($signingEnabled) {
    # Проверяем ровно те файлы, которые подписали в шаге 3: точные пути в publish.
    # Раньше здесь был рекурсивный поиск по корню с нерабочим regex (literal
    # backspace в 'uild') — он мог подхватить несвежий артефакт из build-папок.
    foreach ($binary in @('ScannerCore.exe', 'SCU.exe')) {
        $path = Join-Path $publishDir $binary
        if (-not (Test-Path $path)) { throw "verify: отсутствует $path" }
        # signtool verify не принимает /n (он только у sign); подписанта
        # проверяем по отпечатку из списка сертификатов файла.
        & $signtool verify /pa $path
        if ($LASTEXITCODE -ne 0) { throw "signature verification failed: $path" }
        Write-Host "verified: $path"
    }
}

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

Write-Host ''
Write-Host '=== RELEASE READY ==='
Get-ChildItem (Join-Path $root 'installer') -Filter 'SCU_Setup_*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 |
    ForEach-Object { Write-Host "installer: $($_.FullName)" }
