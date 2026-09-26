# One-time generator of the database signing key pair (SecurityScanner).
# Private key goes to tools/database-builder (NOT shipped with the app),
# public key is embedded into ScannerCore (src/core/database_package.h).
$ErrorActionPreference = 'Stop'

$ec = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::NamedCurves::nistP256)
if ($null -eq $ec) { throw 'ECDsa create failed' }

$params = $ec.ExportExplicitParameters($true)
$x = ($params.X | ForEach-Object { $_.ToString('x2') }) -join ''
$y = ($params.Y | ForEach-Object { $_.ToString('x2') }) -join ''

$dir = Join-Path $PSScriptRoot 'database-builder'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
[IO.File]::WriteAllText((Join-Path $dir 'db-signing-private.pem'), $ec.ExportECPrivateKeyPem())
[IO.File]::WriteAllText((Join-Path $dir 'db-signing-public.pem'), $ec.ExportSubjectPublicKeyInfoPem())

Write-Output "PUBLIC_XY=$x$y"
Write-Output 'keys written'
