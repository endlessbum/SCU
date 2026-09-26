# Сбор clean-corpus (документ п. 40, чистая часть): копирует подписанные
# Microsoft exe/dll из системных каталогов и Program Files в целевую папку
# с манифестом. Запускать на заведомо чистой машине/VM.
#
#   powershell -File collect-clean-corpus.ps1 -OutputDir C:\corpus\clean -MaxFiles 300
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDir,

    [int] $MaxFiles = 300
)

$ErrorActionPreference = 'Stop'

if (Test-Path $OutputDir) {
    throw "OutputDir уже существует: $OutputDir — укажите новую папку (корпус не смешивается)"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$searchRoots = @(
    "$env:SystemRoot\System32",
    "$env:ProgramFiles"
)

$candidates = @()
foreach ($root in $searchRoots) {
    if (-not (Test-Path $root)) { continue }
    $candidates += Get-ChildItem -Path $root -Recurse -Include *.exe, *.dll -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -gt 256KB -and $_.Length -lt 20MB }
}

$collected = 0
$manifest = New-Object System.Collections.Generic.List[string]
$manifest.Add("file`tsha256`tsignedBy")

$sha256 = [System.Security.Cryptography.SHA256]::Create()

foreach ($file in $candidates) {
    if ($collected -ge $MaxFiles) { break }

    $signature = Get-AuthenticodeSignature -FilePath $file.FullName
    if ($signature.Status -ne 'Valid') { continue }

    $hash = [BitConverter]::ToString($sha256.ComputeHash(
        [IO.File]::ReadAllBytes($file.FullName))).Replace('-', '').ToLowerInvariant()

    $targetPath = Join-Path $OutputDir ("clean-{0:D4}{1}" -f $collected, $file.Extension.ToLowerInvariant())
    Copy-Item -LiteralPath $file.FullName -Destination $targetPath
    $manifest.Add(("{0}`t{1}`t{2}" -f (Split-Path $file.FullName -Leaf), $hash, $signature.SignerCertificate.Subject))
    $collected++
}

$manifest | Set-Content (Join-Path $OutputDir 'manifest.tsv') -Encoding UTF8
Write-Output "collected $collected signed clean files into $OutputDir (manifest.tsv)"
