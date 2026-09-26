# Генерация самоподписанного сертификата подписи кода (бесплатно, локально).
# Сертификат живёт в личном хранилище текущего пользователя + экспорт PFX.
# Отпечаток (SHA-1 thumbprint) пишется в signing\thumbprint.txt — его читают
# CMake (пиннинг в ScannerCore), csproj (пиннинг в ScannerRunner) и
# build-release.ps1 (signtool). Повторный запуск существующий сертификат находит,
# дубликаты не создаёт.
param(
    [Parameter(Mandatory = $true)]
    [string] $PfxPassword,

    [string] $CertName = "SCU Code Signing",
    [int] $YearsValid = 3
)

$ErrorActionPreference = 'Stop'

$signingDir = Join-Path $PSScriptRoot '..\signing'
New-Item -ItemType Directory -Force -Path $signingDir | Out-Null

# Существующий сертификат по subject + EKU Code Signing.
$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq "CN=$CertName" } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($existing) {
    $cert = $existing
    Write-Output "existing certificate found: $($cert.Thumbprint) (NotAfter $($cert.NotAfter))"
}
else {
    $cert = New-SelfSignedCertificate `
        -Subject "CN=$CertName" `
        -Type CodeSigningCert `
        -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears($YearsValid) `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable
    Write-Output "certificate created: $($cert.Thumbprint)"
}

$thumbprint = $cert.Thumbprint
$pfxPath = Join-Path $signingDir 'scu-signing.pfx'
$pfxBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $PfxPassword)
[IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
[IO.File]::WriteAllText((Join-Path $signingDir 'thumbprint.txt'), $thumbprint.Trim())

Write-Output "pfx written: $pfxPath"
Write-Output "thumbprint written: signing\thumbprint.txt ($thumbprint)"
Write-Output "PIN_SHA1=$thumbprint"
