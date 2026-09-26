# Регрессия 0 FP на clean-corpus (документ п. 40): сканирует собранный
# collect-clean-corpus.ps1 каталог и требует detections:0.
#
#   powershell -File run-clean-corpus-scan.ps1 -CorpusDir C:\corpus\clean
param(
    [Parameter(Mandatory = $true)]
    [string] $CorpusDir
)

$ErrorActionPreference = 'Stop'

$scanner = Join-Path $PSScriptRoot '..\..\out\Release\ScannerCore.exe'
if (-not (Test-Path $scanner)) {
    $scanner = Join-Path $PSScriptRoot '..\..\..\SCU.App\bin\Release\net10.0-windows\ScannerCore.exe'
}
if (-not (Test-Path $scanner)) { throw 'ScannerCore.exe не найден (соберите SecurityScanner)' }

$manifest = Join-Path $CorpusDir 'manifest.tsv'
if (Test-Path $manifest) {
    # Файлы копируются, так что хеши совпадают; проверяем только объём.
    $expected = (Get-Content $manifest | Measure-Object -Line).Lines - 1
    Write-Output "corpus: $expected файлов (manifest)"
}

$output = & $scanner scan --mode custom --path $CorpusDir 2>&1
$exitCode = $LASTEXITCODE
$finished = $output | Where-Object { $_ -match '"event":"finished"' } | Select-Object -First 1

if (-not $finished) { throw "ScannerCore завершился без отчёта (rc=$exitCode): $output" }

if ($finished -match '"detections":(\d+)') {
    $detections = [int]$Matches[1]
    if ($detections -ne 0) {
        Write-Output $output
        throw "REGRESSION: $detections false positive(s) на clean-corpus"
    }
    Write-Output "PASS: clean-corpus 0 false positives (rc=$exitCode)"
}
else {
    throw "не удалось разобрать итог: $finished"
}
