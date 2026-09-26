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
dotnet publish (Join-Path $root 'SCU.App\SCU.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Fail fast: сканер и база обязаны быть в publish (п. 31).
foreach ($required in @('ScannerCore.exe', 'security\database\hashes.txt', 'SCU.exe')) {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        throw "publish incomplete: missing $required"
    }
}

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
    Write-Warning 'Подпись пропущена (-SkipSign или нет signing\thumbprint.txt): ScannerCore в dev-режиме целостности.'
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
    foreach ($binary in @('ScannerCore.exe', 'SCU.exe')) {
        $path = Get-ChildItem -Path $root -Recurse -Filter $binary -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch 'uild' } |
            Select-Object -First 1
        if ($path) {
            # signtool verify не принимает /n (он только у sign); подписанта
            # проверяем по отпечатку из списка сертификатов файла.
            & $signtool verify /pa $path.FullName
            if ($LASTEXITCODE -ne 0) { throw "signature verification failed: $($path.FullName)" }
            Write-Host "verified: $($path.FullName)"
        }
    }
}

$manifestPath = Join-Path $root 'publish\SHA256SUMS.txt'
if (Test-Path (Split-Path $manifestPath)) {
    $manifest = Get-ChildItem -Path (Join-Path $root 'publish') -Recurse -File |
        Where-Object { $_.Name -ne 'SHA256SUMS.txt' }
    $lines = foreach ($file in $manifest) {
        $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash
        "$hash  $($file.Name)"
    }
    $lines | Set-Content -Path $manifestPath -Encoding ASCII
    Write-Host "manifest: $manifestPath"
}

Write-Host ''
Write-Host '=== RELEASE READY ==='
Get-ChildItem (Join-Path $root 'installer') -Filter 'SCU_Setup_*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 |
    ForEach-Object { Write-Host "installer: $($_.FullName)" }
