# SecurityScanner — offline on-demand сканер SCU

ScannerCore.exe (C++/Win32, CMake) + интеграция в SCU.App (C#). Без resident-служб,
без обязательной сети; сосуществует с Defender (п. 1 дизайн-документа).

## Сборка ScannerCore

```
cmake -S SecurityScanner -B SecurityScanner/build -A x64
cmake --build SecurityScanner/build --config Release
```
Готовый бинарь: `SecurityScanner\out\Release\ScannerCore.exe`. Сборка SCU копирует
его в вывод автоматически (`CopyScannerCore`/`PublishScannerRuntime` в SCU.App.csproj).

## Release-цепочка (бесплатная, самоподписанный сертификат)

```
# один раз: создать сертификат подписи (текущий пользователь)
powershell -File SecurityScanner\tools\generate-signing-cert.ps1 -PfxPassword "<пароль>"

# релиз: ScannerCore -> dotnet publish -> signtool -> Inno Setup installer
powershell -File build-release.ps1 -PfxPassword "<пароль>"
```
Этапы и флаги: `-SkipInstaller` (только publish), `-SkipSign`.

Целостность построена на **пиннинге**: отпечаток сертификата из
`signing\thumbprint.txt` вшивается в ScannerCore (CMake) и SCU.dll (csproj).
ScannerCore перед сканом сверяет отпечаток собственной подписи с пином —
несовпадение/подмена = отказ (fail-closed), доверенный корень не требуется.
Скомпрометирован сертификат → удалить `signing\thumbprint.txt` (пин выключится),
сгенерировать новый сертификат тем же скриптом, пересобрать.

## База сигнатур

- Формат: `hashes.txt` (`sha256<TAB>verdict<TAB>name`), лежит рядом с exe в
  `security\database\`; EICAR встроен в бинарь.
- Сборка подписанного пакета (п. 30/33): `dotnet run SecurityScanner\tools\database-builder\DatabaseBuilder.cs -- build --key db-signing-private.pem --hashes hashes.txt --version 2026.09.25 --out package.zip`
- Установка: вкладка «Антивирус» → «Установить пакет базы…» или
  `ScannerCore.exe update --package package.zip`. URL онлайн-обновления
  переопределяется `%AppData%\SCU\scanner-update.json`.
- Приватный ключ базы (`db-signing-private.pem`) не шипится с приложением.

## Тестирование (п. 40)

```
dotnet test SCU.Tests                      # юнит + интеграционные (EICAR, ZIP, скрипты, пин)
# clean-corpus: собранные подписанные Microsoft-бинарии, ожидание 0 FP
powershell -File SecurityScanner\tools\corpus\collect-clean-corpus.ps1 -OutputDir C:\corpus\clean
powershell -File SecurityScanner\tools\corpus\run-clean-corpus-scan.ps1 -CorpusDir C:\corpus\clean
```

## Проверка целостности вручную

```
# подписанный ScannerCore: строгий режим (без --dev-unsigned-ok)
ScannerCore.exe scan --mode file --path <файл>
# ожидаемо откажется без подписи:
# {"event":"error","message":"integrity: ... pin ..."}
```
