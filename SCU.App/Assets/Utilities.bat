@echo off
chcp 65001 >nul 2>&1
setlocal EnableExtensions EnableDelayedExpansion
title Настройка Windows
cd /d "%~dp0"

mode con: cols=72 lines=42 >nul 2>&1

for /F %%a in ('echo prompt $E^| cmd') do set "ESC=%%a"
set "Reset=%ESC%[0m"
set "Bold=%ESC%[1m"
set "Red=%ESC%[31m"
set "Green=%ESC%[32m"
set "Yellow=%ESC%[33m"
set "Cyan=%ESC%[36m"
set "Gray=%ESC%[90m"

rem ---------------------------------------------------------------- пути
set "UTILDIR=%~dp0"
set "LOGDIR=%UTILDIR%logs"
set "BAKDIR=%UTILDIR%backup"
set "BAK_SVC=%BAKDIR%\Services"
set "BAK_TASKS=%BAKDIR%\Tasks"
set "BAK_STARTUP=%BAKDIR%\Startup"
set "BAK_REG=%BAKDIR%\Registry"
set "BAK_NET=%BAKDIR%\Network"
set "NET_QOS_BAK=%BAK_NET%\qos_backup.txt"
set "NET_MTU_BAK=%BAK_NET%\mtu_backup.tsv"
set "NET_TCP_BAK=%BAK_NET%\tcp_global_backup.txt"
set "NET_NB_BAK=%BAK_NET%\netbios_backup.tsv"
set "SCRIPT_PS=%UTILDIR%SCU.ps1"

set "BACKUP_SVC=%BAK_SVC%\services_backup.txt"
set "BACKUP_TASKS=%BAK_TASKS%\tasks_backup.txt"
set "BACKUP_STARTUP=%BAK_STARTUP%"
set "LIST_STARTUP=%TEMP%\startup_list.tsv"
set "TMPDIR=%TEMP%\DX_VC_Install"
set "RegAdv=HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"
set "RegExp=HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer"
set "RegDeskIcons=HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel"
set "ClsidHome={f874310e-b6b7-47dc-bc84-b9e6b38f5903}"
set "ClsidGallery={e88865ea-0e1c-4e20-9aa6-edcd0212c87c}"
set "ClsidNetwork={F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"
set "ClsidRecycle={645FF040-5081-101B-9F08-00AA002F954E}"
set "ClsidMenu={86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"

if not exist "%LOGDIR%" mkdir "%LOGDIR%" >nul 2>&1
if not exist "%BAK_SVC%" mkdir "%BAK_SVC%" >nul 2>&1
if not exist "%BAK_TASKS%" mkdir "%BAK_TASKS%" >nul 2>&1
if not exist "%BAK_STARTUP%" mkdir "%BAK_STARTUP%" >nul 2>&1
if not exist "%BAK_REG%" mkdir "%BAK_REG%" >nul 2>&1
if not exist "%BAK_NET%" mkdir "%BAK_NET%" >nul 2>&1

for /f "delims=" %%a in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd_HH-mm-ss" 2^>nul') do set "STAMP=%%a"
if not defined STAMP set "STAMP=unknown"
set "LOGFILE=%LOGDIR%\SCU_%STAMP%.log"
set "LOGFOUND="
if exist "%LOGFILE%" (
    for /l %%N in (1,1,999) do if not defined LOGFOUND if not exist "%LOGDIR%\SCU_%STAMP%_%%N.log" (
        set "LOGFILE=%LOGDIR%\SCU_%STAMP%_%%N.log"
        set "LOGFOUND=1"
    )
)
> "%LOGFILE%" echo [%DATE% %TIME%] INIT ^| log-file-open
set "LOGINITRC=!errorlevel!"
if not "!LOGINITRC!"=="0" (
    echo  %Red%[ОШИБКА]%Reset%  Не удалось создать файл журнала: "%LOGFILE%"
    set "LOGFILE="
    set "SCU_LOGFILE="
) else (
    set "SCU_LOGFILE=%LOGFILE%"
)

rem ------------------------------------------- единый список служб (истина)
rem SVCLIST — пробелы для циклов BAT; SVCCSV — тот же список через запятую для PowerShell.
set "SVCLIST=SysMain DiagTrack dmwappushservice WSearch Fax XblAuthManager XblGameSave XboxGipSvc XboxNetApiSvc RemoteRegistry RemoteAccess WbioSrvc TabletInputService MapsBroker RetailDemo wisvc WerSvc PcaSvc PrintNotify Spooler"
set "SVCCSV=%SVCLIST: =,%"
set "SVCDESC.SysMain=SysMain Superfetch"
set "SVCDESC.DiagTrack=DiagTrack телеметрия"
set "SVCDESC.dmwappushservice=dmwappush телеметрия"
set "SVCDESC.WSearch=Windows Search"
set "SVCDESC.Fax=Факс"
set "SVCDESC.XblAuthManager=Xbox Live Auth"
set "SVCDESC.XblGameSave=Xbox Live Game Save"
set "SVCDESC.XboxGipSvc=Xbox Accessory"
set "SVCDESC.XboxNetApiSvc=Xbox Networking"
set "SVCDESC.RemoteRegistry=Remote Registry"
set "SVCDESC.RemoteAccess=Routing Remote Access"
set "SVCDESC.WbioSrvc=Биометрия"
set "SVCDESC.TabletInputService=Сенсорная клавиатура"
set "SVCDESC.MapsBroker=Карты"
set "SVCDESC.RetailDemo=Retail Demo"
set "SVCDESC.wisvc=Windows Insider"
set "SVCDESC.WerSvc=Отчёты об ошибках"
set "SVCDESC.PcaSvc=Совместимость"
set "SVCDESC.PrintNotify=Уведомления печати"
set "SVCDESC.Spooler=Диспетчер печати"

rem --------------------------------- единый список задач (истина, разделитель ;)
set "TASKLIST=\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser;\Microsoft\Windows\Application Experience\ProgramDataUpdater;\Microsoft\Windows\Application Experience\StartupAppTask;\Microsoft\Windows\Customer Experience Improvement Program\Consolidator;\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip;\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector;\Microsoft\Windows\Feedback\Siuf\DmClient;\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload;\Microsoft\Windows\Windows Error Reporting\QueueReporting;\Microsoft\Windows\Autochk\Proxy;\Microsoft\Windows\PI\Sqm-Tasks;\Microsoft\Windows\NetTrace\GatherNetworkInfo;\Microsoft\Windows\CloudExperienceHost\CreateObjectTask;\Microsoft\Windows\Maps\MapsUpdateTask;\Microsoft\Windows\Maps\MapsToastTask"

rem ------------------------------------------------------ права администратора
set "ISADMIN=0"
net session >nul 2>&1 && set "ISADMIN=1"
if "%ISADMIN%"=="0" (
    for /f "delims=" %%a in ('powershell -NoProfile -Command "try { if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { 'True' } } catch {}" 2^>nul') do (
        if /i "%%a"=="True" set "ISADMIN=1"
    )
)

if not exist "%SCRIPT_PS%" (
    call :Warn "Не найден SCU.ps1 — откат служб/задач/автозагрузки и проверка файлов будут недоступны."
)

if "%ISADMIN%"=="0" (
    echo.
    echo  %Red%Утилита запущена без прав администратора.%Reset%
    echo  %Gray%Функции очистки, служб, BCD, установки и удаления ПО будут недоступны.%Reset%
    echo  %Gray%Настройки проводника и интерфейса работают и без elevation.%Reset%
    echo.
    set /p "el=  Перезапустить от имени администратора? y/n: "
    if /i "!el!"=="y" (
        set "RELAUNCH_BAT=%~f0"
        set "RELAUNCH_DIR=%~dp0"
        powershell -NoProfile -Command "try { Start-Process -FilePath $env:RELAUNCH_BAT -WorkingDirectory $env:RELAUNCH_DIR -Verb RunAs -ErrorAction Stop; exit 0 } catch { exit 1 }" >nul 2>&1
        set "UACRC=!errorlevel!"
    )
    rem Проверка UAC после выхода из блока
    if /i "!el!"=="y" (
        if "!UACRC!" NEQ "0" (
            call :Err "Не удалось запросить повышение прав или UAC был отменён."
        ) else (
            exit /b 0
        )
    )
    echo.
)

call :Log "===== SCU start (admin=%ISADMIN%) ====="

goto MainMenu

:Hdr
cls
echo.
echo  %Bold%%Cyan%%~1%Reset%
echo  %Gray%--------------------------------------------------------------------%Reset%
echo.
exit /b

:PauseBack
echo.
echo Для продолжения нажмите любую клавишу...
pause >nul
exit /b

:Ask
set "choice="
set /p "choice=  Выбор: "
exit /b

:Ok
set "MSG_RC=!errorlevel!"
echo  %Green%[OK]%Reset%  %~1
call :Log "RESULT | OK | rc=!MSG_RC! | %~1"
exit /b !MSG_RC!

:Err
set "MSG_RC=!errorlevel!"
if "!MSG_RC!"=="0" set "MSG_RC=1"
echo  %Red%[ОШИБКА]%Reset%  %~1
call :Log "RESULT | ERROR | rc=!MSG_RC! | %~1"
exit /b !MSG_RC!

:Warn
set "MSG_RC=!errorlevel!"
echo  %Yellow%[i]%Reset%  %~1
call :Log "RESULT | WARN | rc=!MSG_RC! | %~1"
exit /b !MSG_RC!

:Log
set "LOG_RC=!errorlevel!"
if not defined LOGFILE exit /b !LOG_RC!
set "LOG_MSG=%~1"
>>"%LOGFILE%" echo [%DATE% %TIME%] !LOG_MSG!
set "WRITE_RC=!errorlevel!"
rem Проверка записи в лог после выхода из перенаправления
if "!WRITE_RC!" NEQ "0" (
    echo  %Red%[LOG-ERROR]%Reset%  Не удалось записать журнал "%LOGFILE%". >>"%TEMP%\SCU_log_errors.txt" 2>nul
    echo  %Red%[LOG-ERROR]%Reset%  Не удалось записать журнал "%LOGFILE%".
)
exit /b !LOG_RC!

:FailIf
if errorlevel 1 set "OPFAIL=1"
exit /b 0

:FailIfDel
if errorlevel 2 set "OPFAIL=1"
exit /b 0

:FailIfNet
if errorlevel 3 set "OPFAIL=1"
if errorlevel 1 if not errorlevel 2 set "OPFAIL=1"
exit /b 0

:ReportOp
echo.
if "!OPFAIL!"=="0" (call :Ok "%~1") else (call :Err "%~2")
exit /b

:RequireAdmin
if "%ISADMIN%"=="1" exit /b 0
echo.
call :Warn "Эта операция требует прав администратора."
call :Log "DENY | admin required | rc=1"
exit /b 1

rem ========================================================================
:MainMenu
call :Hdr "НАСТРОЙКА WINDOWS"
echo  %Gray%  1. Осмотр и компоненты%Reset%
echo  %Bold%[ 1]%Reset%  Информация о системе
echo  %Bold%[ 2]%Reset%  Установка DirectX / VC++ / .NET
echo.
echo  %Gray%  2. Очистка%Reset%
echo  %Bold%[ 3]%Reset%  Очистка системы и диска
echo.
echo  %Gray%  3. Приватность и ПО%Reset%
echo  %Bold%[ 4]%Reset%  Удаление мусорного ПО
echo  %Bold%[ 5]%Reset%  Приватность, телеметрия, уведомления
echo.
echo  %Gray%  4. Фон системы%Reset%
echo  %Bold%[ 6]%Reset%  Службы Windows
echo  %Bold%[ 7]%Reset%  Автозагрузка
echo.
echo  %Gray%  5. Производительность%Reset%
echo  %Bold%[ 8]%Reset%  Питание, память и CPU
echo  %Bold%[ 9]%Reset%  Сеть
echo.
echo  %Gray%  6. Интерфейс и ввод%Reset%
echo  %Bold%[10]%Reset%  Интерфейс и проводник
echo  %Bold%[11]%Reset%  Ввод, браузер и игры
echo.
echo  %Gray%  7. Обслуживание%Reset%
echo  %Bold%[12]%Reset%  Поиск и целостность Windows
echo  %Bold%[13]%Reset%  Безопасность  (UAC)
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[ 0]%Reset%  Выход
echo.
call :Ask
if "%choice%"=="1" goto InfoMenu
if "%choice%"=="2" goto InstMenu
if "%choice%"=="3" goto CleanMenu
if "%choice%"=="4" goto BloatMenu
if "%choice%"=="5" goto PrivMenu
if "%choice%"=="6" goto SvcMenu
if "%choice%"=="7" goto StartUpMenu
if "%choice%"=="8" goto PerfMenu
if "%choice%"=="9" goto NetMenu
if "%choice%"=="10" goto UIMenu
if "%choice%"=="11" goto InpMenu
if "%choice%"=="12" goto MaintMenu
if "%choice%"=="13" goto SecMenu
if "%choice%"=="0" goto ExitUtil
echo.
echo  %Yellow%Неверный выбор.%Reset%
timeout /t 1 /nobreak >nul
goto MainMenu

:ExitUtil
cls
echo.
echo  %Cyan%Выход.%Reset%
echo.
endlocal
exit /b 0

rem ========================================================================
rem  1. ИНФОРМАЦИЯ
rem ========================================================================
:InfoMenu
call :Hdr "ИНФОРМАЦИЯ О КОМПЬЮТЕРЕ"
echo  %Bold%[1]%Reset%  Полный отчёт
echo  %Bold%[2]%Reset%  Система и Windows
echo  %Bold%[3]%Reset%  Процессор и материнская плата
echo  %Bold%[4]%Reset%  Память (ОЗУ)
echo  %Bold%[5]%Reset%  Видеокарта
echo  %Bold%[6]%Reset%  Диски
echo  %Bold%[7]%Reset%  Сеть
echo  %Bold%[8]%Reset%  Периферия
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto InfoAll
if "%choice%"=="2" goto InfoOS
if "%choice%"=="3" goto InfoCPU
if "%choice%"=="4" goto InfoRAM
if "%choice%"=="5" goto InfoGPU
if "%choice%"=="6" goto InfoDisk
if "%choice%"=="7" goto InfoNet
if "%choice%"=="8" goto InfoPeriph
if "%choice%"=="0" goto MainMenu
goto InfoMenu

:InfoAll
call :Hdr "ПОЛНЫЙ ОТЧЁТ"
echo  %Yellow%Сбор данных, подождите...%Reset%
echo.
set "REPORT=%TEMP%\PC_Info_Report.txt"
(
echo ============================================================
echo   ПОЛНЫЙ ОТЧЁТ О КОМПЬЮТЕРЕ
echo   %DATE% %TIME%
echo ============================================================
echo.
) > "%REPORT%"
echo  • Система...
powershell -NoProfile -Command "Get-CimInstance Win32_OperatingSystem | ForEach-Object { '--- СИСТЕМА И WINDOWS ---'; '  ОС:             '+$_.Caption; '  Версия:         '+$_.Version; '  Сборка:         '+$_.BuildNumber; '  Архитектура:    '+$_.OSArchitecture; '  Установка:      '+$_.InstallDate; '  Последний запуск: '+$_.LastBootUpTime }; $cs=Get-CimInstance Win32_ComputerSystem; '  Компьютер:      '+$cs.Name; '  Пользователь:   '+$env:USERNAME; '  Производитель:  '+$cs.Manufacturer; '  Модель:         '+$cs.Model; $b=Get-CimInstance Win32_BIOS; '  BIOS:           '+$b.Manufacturer+' '+$b.SMBIOSBIOSVersion; '  Дата BIOS:      '+$b.ReleaseDate; ''" >> "%REPORT%" 2>nul
echo  • Процессор...
powershell -NoProfile -Command "Get-CimInstance Win32_Processor | ForEach-Object { '--- ПРОЦЕССОР ---'; '  Имя:            '+$_.Name.Trim(); '  Ядра/потоки:    '+$_.NumberOfCores+' / '+$_.NumberOfLogicalProcessors; '  Частота:        '+$_.MaxClockSpeed+' МГц'; '  Сокет:          '+$_.SocketDesignation }; $bb=Get-CimInstance Win32_BaseBoard; '--- МАТЕРИНСКАЯ ПЛАТА ---'; '  Плата:          '+$bb.Manufacturer+' '+$bb.Product; '  Версия:         '+$bb.Version; '  S/N:            '+$bb.SerialNumber; ''" >> "%REPORT%" 2>nul
echo  • Память...
powershell -NoProfile -Command "$cs=Get-CimInstance Win32_ComputerSystem; '--- ОЗУ ---'; '  Всего:          '+[math]::Round($cs.TotalPhysicalMemory/1GB,2)+' ГБ'; $i=1; Get-CimInstance Win32_PhysicalMemory | ForEach-Object { $m=$_.ConfiguredClockSpeed; if(-not $m){$m=$_.Speed}; '  Модуль '+$i+':      '+[math]::Round($_.Capacity/1GB,2)+' ГБ  '+$m+' МГц  '+$_.Manufacturer.Trim()+' '+$_.PartNumber.Trim(); $i++ }; ''" >> "%REPORT%" 2>nul
echo  • Видео...
powershell -NoProfile -Command "Get-CimInstance Win32_VideoController | ForEach-Object { '--- ВИДЕО ---'; '  Карта:          '+$_.Name; '  Драйвер:        '+$_.DriverVersion; '  Дата:           '+$_.DriverDate; if($_.CurrentHorizontalResolution){'  Разрешение:     '+$_.CurrentHorizontalResolution+'x'+$_.CurrentVerticalResolution}; '' }" >> "%REPORT%" 2>nul
echo  • Диски...
powershell -NoProfile -Command "Get-CimInstance Win32_DiskDrive | ForEach-Object { '--- ДИСК ---'; '  '+$_.Model.Trim()+'  '+[math]::Round($_.Size/1GB,2)+' ГБ  ('+$_.InterfaceType+')' }; '  Тома:'; Get-CimInstance Win32_LogicalDisk | Where-Object {$_.DriveType -eq 3} | ForEach-Object { '    '+$_.DeviceID+'  '+[math]::Round($_.Size/1GB,2)+' ГБ, свободно '+[math]::Round($_.FreeSpace/1GB,2)+' ГБ  '+$_.FileSystem }; ''" >> "%REPORT%" 2>nul
echo  • Сеть...
powershell -NoProfile -Command "Get-CimInstance Win32_NetworkAdapter | Where-Object {$_.PhysicalAdapter -and $_.MACAddress} | ForEach-Object { '--- СЕТЬ ---'; '  '+$_.Name+'  MAC '+$_.MACAddress }; Get-CimInstance Win32_NetworkAdapterConfiguration | Where-Object {$_.IPEnabled} | ForEach-Object { $ip=($_.IPAddress -join ', '); $gw=($_.DefaultIPGateway -join ', '); '  '+$_.Description; '    IP: '+$ip+'  Шлюз: '+$gw }; ''" >> "%REPORT%" 2>nul
echo  • Периферия...
powershell -NoProfile -Command "'--- ПЕРИФЕРИЯ ---'; '  Клавиатуры:'; Get-CimInstance Win32_Keyboard -EA 0 | ForEach-Object { '    '+$_.Name }; '  Мыши:'; Get-CimInstance Win32_PointingDevice -EA 0 | ForEach-Object { '    '+$_.Name }; '  Мониторы:'; Get-CimInstance Win32_DesktopMonitor -EA 0 | ForEach-Object { if($_.Name){'    '+$_.Name} }; '  Аудио:'; Get-CimInstance Win32_SoundDevice -EA 0 | ForEach-Object { '    '+$_.Name }; '  Принтеры:'; $p=@(Get-CimInstance Win32_Printer -EA 0); if($p){$p|ForEach-Object{'    '+$_.Name}}else{'    (нет)'}; ''" >> "%REPORT%" 2>nul
echo. >> "%REPORT%"
echo ============================================================ >> "%REPORT%"
cls
echo.
echo  %Bold%%Cyan%ПОЛНЫЙ ОТЧЁТ%Reset%
echo  %Gray%--------------------------------------------------------------------%Reset%
echo.
type "%REPORT%"
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[1]%Reset%  Открыть отчёт в Блокноте
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" start notepad "%REPORT%"
goto InfoMenu

:InfoOS
call :Hdr "СИСТЕМА И WINDOWS"
powershell -NoProfile -Command "$os=Get-CimInstance Win32_OperatingSystem; $cs=Get-CimInstance Win32_ComputerSystem; $bios=Get-CimInstance Win32_BIOS; Write-Host '  Имя компьютера:     ' -NoNewline; Write-Host $cs.Name -ForegroundColor Cyan; Write-Host '  Пользователь:       ' -NoNewline; Write-Host $env:USERNAME -ForegroundColor Cyan; Write-Host '  ОС:                 ' -NoNewline; Write-Host $os.Caption -ForegroundColor Cyan; Write-Host '  Версия:             ' -NoNewline; Write-Host $os.Version -ForegroundColor Cyan; Write-Host '  Сборка:             ' -NoNewline; Write-Host $os.BuildNumber -ForegroundColor Cyan; Write-Host '  Архитектура:        ' -NoNewline; Write-Host $os.OSArchitecture -ForegroundColor Cyan; Write-Host '  Дата установки:     ' -NoNewline; Write-Host $os.InstallDate -ForegroundColor Cyan; Write-Host '  Последний запуск:   ' -NoNewline; Write-Host $os.LastBootUpTime -ForegroundColor Cyan; Write-Host '  Производитель ПК:   ' -NoNewline; Write-Host $cs.Manufacturer -ForegroundColor Cyan; Write-Host '  Модель ПК:          ' -NoNewline; Write-Host $cs.Model -ForegroundColor Cyan; Write-Host '  BIOS:               ' -NoNewline; Write-Host ($bios.Manufacturer+' '+$bios.SMBIOSBIOSVersion) -ForegroundColor Cyan; Write-Host '  Дата BIOS:          ' -NoNewline; Write-Host $bios.ReleaseDate -ForegroundColor Cyan;"
call :PauseBack
goto InfoMenu

:InfoCPU
call :Hdr "ПРОЦЕССОР И МАТЕРИНСКАЯ ПЛАТА"
powershell -NoProfile -Command "$cpu=Get-CimInstance Win32_Processor; $bb=Get-CimInstance Win32_BaseBoard; foreach($c in $cpu){ Write-Host '  Процессор:          ' -NoNewline; Write-Host $c.Name.Trim() -ForegroundColor Cyan; Write-Host '  Ядра / потоки:      ' -NoNewline; Write-Host ($c.NumberOfCores.ToString()+' / '+$c.NumberOfLogicalProcessors) -ForegroundColor Cyan; Write-Host '  Макс. частота:      ' -NoNewline; Write-Host ($c.MaxClockSpeed.ToString()+' МГц') -ForegroundColor Cyan; Write-Host '  Сокет:              ' -NoNewline; Write-Host $c.SocketDesignation -ForegroundColor Cyan }; Write-Host '  Мат. плата:         ' -NoNewline; Write-Host ($bb.Manufacturer+' '+$bb.Product) -ForegroundColor Cyan; Write-Host '  Версия платы:       ' -NoNewline; Write-Host $bb.Version -ForegroundColor Cyan; Write-Host '  Серийный номер:     ' -NoNewline; Write-Host $bb.SerialNumber -ForegroundColor Cyan;"
call :PauseBack
goto InfoMenu

:InfoRAM
call :Hdr "ОПЕРАТИВНАЯ ПАМЯТЬ"
powershell -NoProfile -Command "$cs=Get-CimInstance Win32_ComputerSystem; $total=[math]::Round($cs.TotalPhysicalMemory/1GB,2); Write-Host ('  Всего ОЗУ:          '+$total.ToString()+' ГБ') -ForegroundColor Cyan; $i=1; Get-CimInstance Win32_PhysicalMemory | ForEach-Object { $gb=[math]::Round($_.Capacity/1GB,2); $mhz=$_.ConfiguredClockSpeed; if(-not $mhz){$mhz=$_.Speed}; Write-Host ('  Модуль '+$i+':  '+$gb+' ГБ  '+$mhz+' МГц  '+$_.Manufacturer.Trim()+'  '+$_.PartNumber.Trim()) -ForegroundColor Cyan; $i++ }"
call :PauseBack
goto InfoMenu

:InfoGPU
call :Hdr "ВИДЕОКАРТА"
powershell -NoProfile -Command "Get-CimInstance Win32_VideoController | ForEach-Object { Write-Host '  Видеокарта:         ' -NoNewline; Write-Host $_.Name -ForegroundColor Cyan; Write-Host '  Драйвер:            ' -NoNewline; Write-Host $_.DriverVersion -ForegroundColor Cyan; Write-Host '  Дата драйвера:      ' -NoNewline; Write-Host $_.DriverDate -ForegroundColor Cyan; if($_.CurrentHorizontalResolution){ Write-Host '  Разрешение:         ' -NoNewline; Write-Host ($_.CurrentHorizontalResolution.ToString()+' x '+$_.CurrentVerticalResolution) -ForegroundColor Cyan }; Write-Host '' }"
call :PauseBack
goto InfoMenu

:InfoDisk
call :Hdr "ДИСКИ"
powershell -NoProfile -Command "Get-CimInstance Win32_DiskDrive | ForEach-Object { $gb=[math]::Round($_.Size/1GB,2); Write-Host ('  '+$_.Model.Trim()+'  —  '+$gb+' ГБ  ('+$_.InterfaceType+')') -ForegroundColor Cyan }; Write-Host '  Логические тома:'; Get-CimInstance Win32_LogicalDisk | Where-Object { $_.DriveType -eq 3 } | ForEach-Object { $free=[math]::Round($_.FreeSpace/1GB,2); $total=[math]::Round($_.Size/1GB,2); Write-Host ('    '+$_.DeviceID+'  '+$total+' ГБ (свободно '+$free+' ГБ)  '+$_.FileSystem) -ForegroundColor Cyan }"
call :PauseBack
goto InfoMenu

:InfoNet
call :Hdr "СЕТЬ"
powershell -NoProfile -Command "Get-CimInstance Win32_NetworkAdapter | Where-Object { $_.PhysicalAdapter -eq $true -and $_.MACAddress } | ForEach-Object { Write-Host ('  '+$_.Name+'  MAC: '+$_.MACAddress) -ForegroundColor Cyan }; Write-Host '  IP-адреса:'; Get-CimInstance Win32_NetworkAdapterConfiguration | Where-Object { $_.IPEnabled -eq $true } | ForEach-Object { $ip=if($_.IPAddress){$_.IPAddress -join ', '}else{'-'}; $gw=if($_.DefaultIPGateway){$_.DefaultIPGateway -join ', '}else{'-'}; Write-Host ('    '+$_.Description) -ForegroundColor DarkGray; Write-Host ('    IP: '+$ip+'  Шлюз: '+$gw) -ForegroundColor Cyan }"
call :PauseBack
goto InfoMenu

:InfoPeriph
call :Hdr "ПЕРИФЕРИЯ"
powershell -NoProfile -Command "Write-Host '  Клавиатуры:'; Get-CimInstance Win32_Keyboard -EA 0 | ForEach-Object { Write-Host ('    '+$_.Name) -ForegroundColor Cyan }; Write-Host '  Мыши:'; Get-CimInstance Win32_PointingDevice -EA 0 | ForEach-Object { Write-Host ('    '+$_.Name) -ForegroundColor Cyan }; Write-Host '  Мониторы:'; Get-CimInstance Win32_DesktopMonitor -EA 0 | ForEach-Object { if($_.Name){ Write-Host ('    '+$_.Name) -ForegroundColor Cyan } }; Write-Host '  Аудио:'; Get-CimInstance Win32_SoundDevice -EA 0 | ForEach-Object { Write-Host ('    '+$_.Name) -ForegroundColor Cyan }; Write-Host '  Принтеры:'; $p=@(Get-CimInstance Win32_Printer -EA 0); if($p.Count -gt 0){ $p | ForEach-Object { Write-Host ('    '+$_.Name) -ForegroundColor Cyan } } else { Write-Host '    (нет)' -ForegroundColor DarkGray }"
call :PauseBack
goto InfoMenu

rem ========================================================================
rem  2. УСТАНОВКА RUNTIME
rem ========================================================================
:InstMenu
call :RequireAdmin
set "ADMINRC=!errorlevel!"
if "!ADMINRC!" NEQ "0" goto MainMenu
call :Hdr "УСТАНОВКА DIRECTX / VC++ / .NET"
echo  %Bold%[1]%Reset%  Установить всё  (DX + VC++ + .NET)
echo  %Bold%[2]%Reset%  Только DirectX
echo  %Bold%[3]%Reset%  Только Visual C++ Redistributable
echo  %Bold%[4]%Reset%  Только .NET Desktop Runtime
echo  %Bold%[5]%Reset%  Проверить установленные runtime
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto InstAll
if "%choice%"=="2" goto InstDX
if "%choice%"=="3" goto InstVC
if "%choice%"=="4" goto InstNET
if "%choice%"=="5" goto InstCheck
if "%choice%"=="0" goto MainMenu
goto InstMenu

:InstAll
set "INSTALL_FAIL=0"
call :DoDirectX
if errorlevel 1 set "INSTALL_FAIL=1"
call :DoVC
if errorlevel 1 set "INSTALL_FAIL=1"
call :DoNET
if errorlevel 1 set "INSTALL_FAIL=1"
goto InstDone
:InstDX
call :DoDirectX
if errorlevel 1 (set "INSTALL_FAIL=1") else (set "INSTALL_FAIL=0")
goto InstDone
:InstVC
call :DoVC
if errorlevel 1 (set "INSTALL_FAIL=1") else (set "INSTALL_FAIL=0")
goto InstDone
:InstNET
call :DoNET
if errorlevel 1 (set "INSTALL_FAIL=1") else (set "INSTALL_FAIL=0")
goto InstDone

:DownloadFile
rem %1 = URL, %2 = целевой файл. Возвращает 0 только при успешном скачивании.
if exist "%~2" del /f /q "%~2" >nul 2>&1
set "DLRC=0"
curl -fL -o "%~2" --retry 3 --connect-timeout 20 "%~1" 2>nul
set "DLRC=!errorlevel!"
if "!DLRC!"=="0" if exist "%~2" goto DownloadFileOK
set "DL_URL=%~1"
set "DL_FILE=%~2"
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Invoke-WebRequest -Uri $env:DL_URL -OutFile $env:DL_FILE -UseBasicParsing -ErrorAction Stop; exit 0 } catch { exit 1 }" >nul 2>&1
set "DLRC=!errorlevel!"
if not "!DLRC!"=="0" goto DownloadFileFail
if not exist "%~2" goto DownloadFileFail
:DownloadFileOK
for %%F in ("%~2") do set "DLSIZE=%%~zF"
if not defined DLSIZE set "DLSIZE=0"
if "!DLSIZE!"=="0" goto DownloadFileFail
call :Log "GET  | %~1 -> %~2 | bytes=!DLSIZE! | rc=0"
exit /b 0
:DownloadFileFail
call :Log "GET-FAIL | %~1 | rc=!DLRC! | file=%~2"
if exist "%~2" del /f /q "%~2" >nul 2>&1
exit /b 1

:VerifyFile
rem %1 = файл, %2 = подпись. Размер + Authenticode + издатель Microsoft.
if not exist "%~1" (
    call :Err "%~2: файл не скачан"
    exit /b 1
)
set "SIGSIZE=0"
for %%F in ("%~1") do set "SIGSIZE=%%~zF"
if !SIGSIZE! LSS 262144 (
    call :Err "%~2: файл подозрительно мал (!SIGSIZE! bytes)"
    exit /b 1
)
if not exist "%SCRIPT_PS%" (
    call :Err "%~2: SCU.ps1 отсутствует — запуск без проверки запрещён"
    exit /b 1
)
set "VERIFY_PATH=%~1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action VerifyExe -Path "%VERIFY_PATH%" >"%TEMP%\sigcheck.txt" 2>&1
set "SIGRC=!errorlevel!"
set "SIGMSG="
if exist "%TEMP%\sigcheck.txt" set /p "SIGMSG="<"%TEMP%\sigcheck.txt"
if not "!SIGRC!"=="0" (
    call :Err "%~2: проверка не пройдена (!SIGMSG!)"
    exit /b 1
)
call :Log "SIG-OK | %~1 | !SIGMSG!"
call :Ok "%~2: подпись Microsoft подтверждена"
exit /b 0

:DoDirectX
call :Hdr "DIRECTX"
if not exist "%TMPDIR%" mkdir "%TMPDIR%" >nul 2>&1
set "DXFILE=%TMPDIR%\dxwebsetup.exe"
echo  %Yellow%Скачиваю dxwebsetup.exe...%Reset%
call :DownloadFile "https://download.microsoft.com/download/1/7/1/1718CCC4-6315-4D8E-9543-8E28A4E18C4C/dxwebsetup.exe" "%DXFILE%"
if errorlevel 1 (call :Err "Не удалось скачать DirectX" & exit /b 1)
call :VerifyFile "%DXFILE%" "DirectX"
if errorlevel 1 exit /b 1
echo.
echo  %Yellow%Запускаю установку DirectX...%Reset%
start /wait "" "%DXFILE%" /Q
set "DXRC=!errorlevel!"
call :Log "INSTALL | DirectX | rc=!DXRC!"
if "!DXRC!"=="0" (call :Ok "DirectX установлен" & exit /b 0)
if "!DXRC!"=="3010" (call :Ok "DirectX установлен, нужна перезагрузка" & exit /b 0)
call :Err "Установщик DirectX завершился с кодом !DXRC!"
exit /b 1

:DoVC
call :Hdr "VISUAL C++ REDISTRIBUTABLE"
if not exist "%TMPDIR%" mkdir "%TMPDIR%" >nul 2>&1
set "VC64=%TMPDIR%\vc_redist.x64.exe"
set "VC86=%TMPDIR%\vc_redist.x86.exe"
set "VC64READY=0"
set "VC86READY=0"
set "VCFAIL=0"
echo  %Yellow%Скачиваю x64...%Reset%
call :DownloadFile "https://aka.ms/vc14/vc_redist.x64.exe" "%VC64%"
if errorlevel 1 (call :Err "VC++ x64: скачивание не удалось") else (
    call :VerifyFile "%VC64%" "VC++ x64"
    if not errorlevel 1 set "VC64READY=1"
)
echo  %Yellow%Скачиваю x86...%Reset%
call :DownloadFile "https://aka.ms/vc14/vc_redist.x86.exe" "%VC86%"
if errorlevel 1 (call :Err "VC++ x86: скачивание не удалось") else (
    call :VerifyFile "%VC86%" "VC++ x86"
    if not errorlevel 1 set "VC86READY=1"
)
if "%VC64READY%"=="0" if "%VC86READY%"=="0" (call :Err "Ни один установщик Visual C++ не прошёл проверку" & exit /b 1)
if "%VC64READY%"=="1" (
    echo  • Установка x64...
    "%VC64%" /install /quiet /norestart
    set "RC=!errorlevel!"
    call :Log "INSTALL | VC++ x64 | rc=!RC!"
    if "!RC!"=="0" (call :Ok "x64 установлен") else if "!RC!"=="1638" (call :Ok "x64 уже установлен") else if "!RC!"=="3010" (call :Ok "x64 установлен, нужна перезагрузка") else if "!RC!"=="1641" (call :Ok "x64 установлен, инициирована перезагрузка") else (call :Err "x64 завершился с кодом !RC!" & set "VCFAIL=1")
)
if "%VC86READY%"=="1" (
    echo  • Установка x86...
    "%VC86%" /install /quiet /norestart
    set "RC=!errorlevel!"
    call :Log "INSTALL | VC++ x86 | rc=!RC!"
    if "!RC!"=="0" (call :Ok "x86 установлен") else if "!RC!"=="1638" (call :Ok "x86 уже установлен") else if "!RC!"=="3010" (call :Ok "x86 установлен, нужна перезагрузка") else if "!RC!"=="1641" (call :Ok "x86 установлен, инициирована перезагрузка") else (call :Err "x86 завершился с кодом !RC!" & set "VCFAIL=1")
)
if "!VCFAIL!"=="1" exit /b 1
exit /b 0

:DotnetHas
rem %1 = мажорная версия Desktop Runtime. 0 = установлена.
if not exist "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App" exit /b 1
for /f "delims=" %%v in ('dir /b /ad "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App" 2^>nul') do (
    echo %%v| findstr /b "%~1." >nul && exit /b 0
)
exit /b 1

:InstallDotnet
rem %1 = версия, %2 = отображаемая подпись, %3 = официальный URL.
set "DVER=%~1"
call :DotnetHas %DVER%
if not errorlevel 1 (call :Ok "Desktop Runtime !DVER! уже установлен" & exit /b 0)
echo  • .NET !DVER! Desktop x64
set "DFILE=%TMPDIR%\windowsdesktop-runtime-!DVER!-x64.exe"
call :DownloadFile "%~3" "!DFILE!"
if errorlevel 1 (call :Err ".NET !DVER!: не удалось скачать" & exit /b 1)
call :VerifyFile "!DFILE!" ".NET !DVER!"
if errorlevel 1 exit /b 1
"!DFILE!" /install /quiet /norestart
set "RC=!errorlevel!"
call :Log "INSTALL | .NET !DVER! | rc=!RC!"
if "!RC!"=="0" (call :Ok ".NET !DVER! установлен" & exit /b 0)
if "!RC!"=="1638" (call :Ok ".NET !DVER! уже установлен" & exit /b 0)
if "!RC!"=="3010" (call :Ok ".NET !DVER! установлен, нужна перезагрузка" & exit /b 0)
if "!RC!"=="1641" (call :Ok ".NET !DVER! установлен, инициирована перезагрузка" & exit /b 0)
call :Err ".NET !DVER! завершился с кодом !RC!"
exit /b 1

:DoNET
call :Hdr ".NET DESKTOP RUNTIME"
if not exist "%TMPDIR%" mkdir "%TMPDIR%" >nul 2>&1
set "NETFAIL=0"
echo  %Gray%Проверяю установленные версии...%Reset%
for %%V in (10 9 8) do call :DotnetHas %%V && call :Ok "Desktop Runtime %%V уже установлен"
echo.
echo  %Gray%Поддерживаемые на 17.09.2026 ветки: .NET 10 (LTS), .NET 9 (STS), .NET 8 (LTS/maintenance).%Reset%
call :InstallDotnet 10 ".NET 10" "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
if errorlevel 1 set "NETFAIL=1"
call :InstallDotnet 9 ".NET 9" "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe"
if errorlevel 1 set "NETFAIL=1"
call :InstallDotnet 8 ".NET 8" "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
if errorlevel 1 set "NETFAIL=1"
if "!NETFAIL!"=="1" exit /b 1
exit /b 0

:InstCheck
call :Hdr "ПРОВЕРКА RUNTIME"
echo  %Bold%.NET Desktop Runtime:%Reset%
set "NETANY=0"
for %%V in (10 9 8) do (
    call :DotnetHas %%V
    if not errorlevel 1 (echo    • .NET %%V Desktop:  установлен & set "NETANY=1")
)
if "%NETANY%"=="0" echo    %Gray%(не найдены в "%ProgramFiles%\dotnet")%Reset%
if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    echo.
    echo  %Gray%dotnet --list-runtimes:%Reset%
    "%ProgramFiles%\dotnet\dotnet.exe" --list-runtimes 2>nul
)
echo.
echo  %Bold%Visual C++ Redistributable:%Reset%
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" /s /f "Microsoft Visual C++" 2>nul | findstr /i "DisplayName" | findstr /i "Redistributable"
reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" /s /f "Microsoft Visual C++" 2>nul | findstr /i "DisplayName" | findstr /i "Redistributable"
echo.
echo  %Bold%DirectX:%Reset%
if exist "%SystemRoot%\System32\d3dx9_43.dll" (call :Ok "d3dx9_43.dll найден") else (call :Warn "d3dx9_43.dll не найден")
call :PauseBack
goto InstMenu

:InstDone
echo.
if "%INSTALL_FAIL%"=="1" (
    echo  %Bold%%Red%ЗАВЕРШЕНО С ОШИБКАМИ%Reset%
    call :Log "INSTALL-RESULT | FAIL"
) else (
    echo  %Bold%%Green%ГОТОВО%Reset%
    call :Log "INSTALL-RESULT | OK"
)
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  Временные файлы: %TMPDIR%
echo  %Yellow%Рекомендуется перезагрузить компьютер при сообщении установщика.%Reset%
call :PauseBack
goto InstMenu

rem ========================================================================
rem  3. ОЧИСТКА
rem ========================================================================
:CleanMenu
call :RequireAdmin
set "ADMINRC=!errorlevel!"
if "!ADMINRC!" NEQ "0" goto MainMenu
call :Hdr "ОЧИСТКА СИСТЕМЫ И ДИСКА"
echo  %Gray%  Быстрая очистка%Reset%
echo  %Bold%[ 1]%Reset%  Полная очистка  (корзина + temp + браузеры + обновления)
echo  %Bold%[ 2]%Reset%  Корзина
echo  %Bold%[ 3]%Reset%  Папки Temp
echo  %Bold%[ 4]%Reset%  Кэш браузеров
echo  %Bold%[ 5]%Reset%  Старые обновления Windows
echo  %Bold%[ 6]%Reset%  Кэш проводника  (иконки, эскизы, шрифты)
echo  %Bold%[ 7]%Reset%  DNS / Winsock
echo.
echo  %Gray%  Глубокая очистка диска%Reset%
echo  %Bold%[ 8]%Reset%  WinSxS — очистка компонентов
echo  %Bold%[ 9]%Reset%  WinSxS — ResetBase  %Red%(осторожно)%Reset%
echo  %Bold%[10]%Reset%  Файл гибернации hiberfil.sys
echo  %Bold%[11]%Reset%  Точки восстановления
echo  %Bold%[12]%Reset%  Кэш Delivery Optimization
echo  %Bold%[13]%Reset%  Всё безопасное сразу  (8+12)
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[ 0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto ClnFull
if "%choice%"=="2" goto ClnRecycle
if "%choice%"=="3" goto ClnTemp
if "%choice%"=="4" goto ClnBrowsers
if "%choice%"=="5" goto ClnWinUpd
if "%choice%"=="6" goto ClnExplorer
if "%choice%"=="7" goto ClnDNSMenu
if "%choice%"=="8" goto ClnWinSxS
if "%choice%"=="9" goto ClnResetBase
if "%choice%"=="10" goto ClnHiber
if "%choice%"=="11" goto ClnRestore
if "%choice%"=="12" goto ClnDelivery
if "%choice%"=="13" goto ClnSafeAll
if "%choice%"=="0" goto MainMenu
goto CleanMenu

:ClnFull
set "CLNFAIL=0"
call :CleanRecycle
call :CleanTemp
call :CleanBrowsers
call :CleanWinUpdate
if errorlevel 1 set "CLNFAIL=1"
goto ClnDone
:ClnRecycle
call :CleanRecycle
goto ClnDone
:ClnTemp
call :CleanTemp
goto ClnDone
:ClnBrowsers
call :CleanBrowsers
goto ClnDone
:ClnWinUpd
call :CleanWinUpdate
set "CLNFAIL=!errorlevel!"
goto ClnDone

:CountFiles
rem %1 = папка, %2 = маска. Результат в CNT.
set "CNT=0"
for /f %%a in ('powershell -NoProfile -Command "try { (Get-ChildItem -LiteralPath '%~1' -Filter '%~2' -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object).Count } catch { 0 }" 2^>nul') do set "CNT=%%a"
exit /b

:CleanDirContents
rem %1 = папка, %2 = подпись, %3 = маска (по умолчанию *)
set "MASK=%~3"
if "%MASK%"=="" set "MASK=*"
if not exist "%~1" (
    echo  %Gray%[—]%Reset%  %~2: нет папки
    call :Log "SKIP | %~2 (нет папки)"
    exit /b
)
call :CountFiles "%~1" "%MASK%"
set "BEFORE=!CNT!"
del /f /s /q "%~1\%MASK%" >nul 2>&1
for /d %%i in ("%~1\*") do rd /s /q "%%i" >nul 2>&1
call :CountFiles "%~1" "%MASK%"
set "AFTER=!CNT!"
if "!BEFORE!"=="0" (
    echo  %Green%[OK]%Reset%  %~2 (уже пусто)
) else if "!AFTER!"=="0" (
    echo  %Green%[OK]%Reset%  %~2 (удалено файлов: !BEFORE!)
) else (
    echo  %Yellow%[i]%Reset%  %~2 - удалено !BEFORE!, осталось !AFTER! ^(занятые файлы пропущены^)
)
call :Log "CLEAN | %~2 before=!BEFORE! after=!AFTER!"
exit /b

:CleanRecycle
call :Hdr "ОЧИСТКА КОРЗИНЫ"
echo  %Yellow%Очищаю корзину...%Reset%
if exist "%SCRIPT_PS%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action EmptyRecycleBin
    if "!errorlevel!"=="0" (call :Ok "Корзина очищена") else (call :Err "Корзина не очищена (код !errorlevel!)")
) else (
    call :Warn "SCU.ps1 не найден — очистка корзины недоступна."
)
echo.
exit /b

:CleanTemp
call :Hdr "ОЧИСТКА ПАПОК TEMP"
echo  %Yellow%Очищаю временные файлы...%Reset%
echo  %Gray%(занятые файлы будут пропущены)%Reset%
echo.
if defined TEMP call :CleanDirContents "%TEMP%" "TEMP пользователя"
call :CleanDirContents "%SystemRoot%\Temp" "Windows\Temp"
call :CleanDirContents "%SystemRoot%\Prefetch" "Prefetch" "*.pf"
call :CleanDirContents "%APPDATA%\Microsoft\Windows\Recent" "Recent"
call :CleanDirContents "%LOCALAPPDATA%\Microsoft\Windows\INetCache" "INetCache"
echo.
exit /b

:CleanBrowsers
call :Hdr "ОЧИСТКА БРАУЗЕРОВ"
echo  %Yellow%Очищаю кэш (закладки и пароли не затрагиваются)...%Reset%
echo  %Gray%Закройте браузеры, иначе часть кэша будет занята.%Reset%
echo.
set "BROWSER_HIT=0"
call :CleanChromium "%LOCALAPPDATA%\Google\Chrome\User Data" "Google Chrome"
call :CleanChromium "%LOCALAPPDATA%\Microsoft\Edge\User Data" "Microsoft Edge"
call :CleanChromium "%LOCALAPPDATA%\Yandex\YandexBrowser\User Data" "Яндекс.Браузер"
call :CleanChromium "%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data" "Brave"
call :CleanChromium "%LOCALAPPDATA%\Vivaldi\User Data" "Vivaldi"
call :CleanChromium "%LOCALAPPDATA%\Chromium\User Data" "Chromium"
call :CleanFirefox "%APPDATA%\Mozilla\Firefox\Profiles" "Mozilla Firefox"
call :CleanOpera "%APPDATA%\Opera Software\Opera Stable" "Opera"
call :CleanOpera "%APPDATA%\Opera Software\Opera GX Stable" "Opera GX"
if "%BROWSER_HIT%"=="0" echo  %Gray%Поддерживаемые браузеры не найдены.%Reset%
echo.
exit /b

:CleanChromium
rem %1 = User Data, %2 = имя браузера
if not exist "%~1" exit /b
set "BROWSER_HIT=1"
echo  • %~2
for /d %%p in ("%~1\*") do (
    call :CleanDirContents "%%p\Cache" "%~2: %%~nxp Cache"
    call :CleanDirContents "%%p\Code Cache" "%~2: %%~nxp Code Cache"
    call :CleanDirContents "%%p\GPUCache" "%~2: %%~nxp GPUCache"
    call :CleanDirContents "%%p\Service Worker\CacheStorage" "%~2: %%~nxp SW Cache"
)
exit /b

:CleanFirefox
rem %1 = каталог Profiles, %2 = имя браузера
if not exist "%~1" exit /b
set "BROWSER_HIT=1"
echo  • %~2
for /d %%p in ("%~1\*") do (
    call :CleanDirContents "%%p\cache2" "%~2: %%~nxp cache2"
    call :CleanDirContents "%%p\startupCache" "%~2: %%~nxp startupCache"
    call :CleanDirContents "%%p\OfflineCache" "%~2: %%~nxp OfflineCache"
)
exit /b

:CleanOpera
rem %1 = профиль Opera, %2 = имя браузера
if not exist "%~1" exit /b
set "BROWSER_HIT=1"
echo  • %~2
call :CleanDirContents "%~1\Cache" "%~2: Cache"
call :CleanDirContents "%~1\GPUCache" "%~2: GPUCache"
call :CleanDirContents "%~1\Code Cache" "%~2: Code Cache"
exit /b

:CleanWinUpdate
call :Hdr "ОЧИСТКА СТАРЫХ ОБНОВЛЕНИЙ WINDOWS"
echo  %Yellow%Очищаю кэш и старые компоненты обновлений...%Reset%
echo.
set "WFAIL=0"
set "WU_SVCS=wuauserv,bits,cryptsvc,msiserver"
echo  • Сохранение исходного состояния служб
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — очистка без резерва служб отменена."
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action ServicesBackup -OutFile "%BAK_SVC%\wu_backup.txt" -Services "%WU_SVCS%"
set "WUBKRC=!errorlevel!"
if not "!WUBKRC!"=="0" (
    call :Err "Не удалось сохранить состояние служб (код !WUBKRC!) — остановка служб отменена."
    call :Log "WU | backup FAILED rc=!WUBKRC!"
    exit /b 1
)
if not exist "%BAK_SVC%\wu_backup.txt" (
    call :Err "Файл резерва служб не создан — очистка отменена."
    exit /b 1
)
call :Ok "состояние служб сохранено"
echo  • Остановка служб Windows Update
for %%S in (wuauserv bits cryptsvc msiserver) do (
    sc query "%%S" >nul 2>&1
    if not errorlevel 1 (
        sc stop "%%S" >nul 2>&1
        if errorlevel 1 (echo       %Gray%%%S: уже остановлена или не удалось остановить%Reset%) else (echo       %Gray%%%S: остановлена%Reset%)
    ) else (
        echo       %Gray%%%S: служба не найдена%Reset%
    )
)
call :Log "WU   | службы остановлены"
echo.
echo  • SoftwareDistribution\Download
call :CleanDirContents "%SystemRoot%\SoftwareDistribution\Download" "кэш загрузок"
echo  • Catroot2
if exist "%SystemRoot%\System32\catroot2" (
    if exist "%SystemRoot%\System32\catroot2.old" move "%SystemRoot%\System32\catroot2.old" "%SystemRoot%\System32\catroot2.old.%STAMP%" >nul 2>&1
    move "%SystemRoot%\System32\catroot2" "%SystemRoot%\System32\catroot2.old" >nul 2>&1
    if errorlevel 1 (
        call :Err "Catroot2 не переименован (папка занята)"
        set "WFAIL=1"
    ) else (
        mkdir "%SystemRoot%\System32\catroot2" >nul 2>&1
        if errorlevel 1 (
            call :Err "Catroot2 переименован, но новая папка не создана"
            set "WFAIL=1"
        ) else (
            call :Ok "Catroot2 пересоздан"
            rd /s /q "%SystemRoot%\System32\catroot2.old" >nul 2>&1
        )
    )
) else (
    echo  %Gray%[—]%Reset%  catroot2: нет папки
)
echo  • Delivery Optimization
call :CleanDirContents "%SystemRoot%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache" "DO: NetworkService cache"
call :CleanDirContents "%ProgramData%\Microsoft\Windows\DeliveryOptimization\Cache" "DO: ProgramData cache"
echo  • DISM StartComponentCleanup
echo    %Gray%(может занять несколько минут...)%Reset%
Dism.exe /Online /Cleanup-Image /StartComponentCleanup
set "DRC=!errorlevel!"
if "!DRC!"=="0" (call :Ok "старые компоненты удалены") else (call :Err "DISM завершился с кодом !DRC!" & set "WFAIL=1")
echo.
echo  • Восстановление исходного состояния служб
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action ServicesRestore -InFile "%BAK_SVC%\wu_backup.txt"
set "WURC=!errorlevel!"
if "!WURC!"=="0" (call :Ok "исходное состояние служб восстановлено") else (call :Warn "часть служб не удалось вернуть — см. лог" & set "WFAIL=1")
echo.
if "!WFAIL!"=="0" (call :Log "WU | cleanup result=OK" & exit /b 0)
call :Log "WU | cleanup result=FAIL"
exit /b 1

:ClnExplorer
call :Hdr "КЭШ ПРОВОДНИКА"
echo  %Yellow%Останавливаю проводник...%Reset%
set "EXP_WAS_RUNNING=0"
tasklist /fi "imagename eq explorer.exe" 2>nul | findstr /i "explorer.exe" >nul && set "EXP_WAS_RUNNING=1"
taskkill /f /im explorer.exe >nul 2>&1
timeout /t 1 /nobreak >nul
echo  [1/3]  Кэш эскизов...
del /f /q "%LocalAppData%\Microsoft\Windows\Explorer\thumbcache_*.db" >nul 2>&1
echo  [2/3]  Кэш иконок...
del /f /q "%LocalAppData%\Microsoft\Windows\Explorer\iconcache_*.db" >nul 2>&1
del /f /q "%LocalAppData%\IconCache.db" >nul 2>&1
echo  [3/3]  Кэш шрифтов...
net stop FontCache >nul 2>&1
net stop FontCache3.0.0.0 >nul 2>&1
del /f /q "%WinDir%\ServiceProfiles\LocalService\AppData\Local\FontCache\*.dat" >nul 2>&1
net start FontCache >nul 2>&1
net start FontCache3.0.0.0 >nul 2>&1
if "%EXP_WAS_RUNNING%"=="1" (
    start "" explorer.exe
    call :Ok "Кэш проводника очищен, проводник перезапущен"
) else (
    call :Ok "Кэш проводника очищен"
)
call :Log "CLEAN | explorer cache (занятые файлы могли быть пропущены)"
call :PauseBack
goto CleanMenu

:ClnDNSMenu
call :Hdr "ОЧИСТКА DNS"
echo  %Bold%[1]%Reset%  Очистить DNS-кэш
echo  %Bold%[2]%Reset%  Очистить DNS + сброс Winsock / IP
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto ClnDNS
if "%choice%"=="2" goto ClnDNSAll
if "%choice%"=="0" goto CleanMenu
goto ClnDNSMenu

:ClnDNS
call :Hdr "ОЧИСТКА DNS-КЭША"
echo  %Yellow%Выполняю ipconfig /flushdns...%Reset%
echo.
ipconfig /flushdns >nul 2>&1
if errorlevel 1 (call :Err "Не удалось очистить DNS-кэш") else (call :Ok "DNS-кэш очищен")
call :PauseBack
goto ClnDNSMenu

:ClnDNSAll
call :Hdr "ОЧИСТКА DNS + СБРОС WINSOCK"
echo  %Yellow%Очищаю DNS-кэш...%Reset%
ipconfig /flushdns >nul 2>&1
if errorlevel 1 (call :Err "DNS-кэш не очищен") else (call :Ok "DNS-кэш очищен")
echo.
echo  %Yellow%Сбрасываю кэш NetBIOS...%Reset%
nbtstat -R >nul 2>&1
set "NBRC=!errorlevel!"
nbtstat -RR >nul 2>&1
set "NBRRC=!errorlevel!"
if "!NBRC!"=="0" if "!NBRRC!"=="0" (call :Ok "NetBIOS сброшен") else (call :Warn "NetBIOS: коды !NBRC!/!NBRRC!")
echo.
echo  %Yellow%Сбрасываю каталог Winsock...%Reset%
netsh winsock reset >nul 2>&1
if errorlevel 1 (call :Err "Winsock не сброшен") else (call :Ok "Winsock сброшен")
echo.
echo  %Yellow%Сбрасываю стек IP...%Reset%
netsh int ip reset >nul 2>&1
if errorlevel 1 (call :Err "IP-стек не сброшен") else (call :Ok "IP-стек сброшен")
echo.
echo  %Yellow%Рекомендуется перезагрузить компьютер.%Reset%
call :PauseBack
goto ClnDNSMenu

:ClnWinSxS
call :Hdr "WINSxS — ОЧИСТКА КОМПОНЕНТОВ"
echo  Удаляет заменённые компоненты обновлений.
echo  %Yellow%Может занять 5–30 минут.%Reset%
echo.
set /p "conf=  Продолжить? y/n: "
if /i not "%conf%"=="y" goto CleanMenu
echo.
echo  %Yellow%DISM /StartComponentCleanup...%Reset%
echo.
Dism.exe /Online /Cleanup-Image /StartComponentCleanup
set "DRC=!errorlevel!"
echo.
if "!DRC!"=="0" (call :Ok "Очистка WinSxS завершена") else (call :Err "DISM завершился с кодом !DRC!")
call :PauseBack
goto CleanMenu

:ClnResetBase
call :Hdr "WINSxS — RESETBASE"
echo  %Red%ВНИМАНИЕ:%Reset%
echo  - Нельзя будет удалить уже установленные обновления
echo  - Операция необратима
echo.
set /p "conf=  Точно продолжить? y/n: "
if /i not "%conf%"=="y" goto CleanMenu
set /p "conf2=  Ещё раз подтвердите (y): "
if /i not "%conf2%"=="y" goto CleanMenu
echo.
Dism.exe /Online /Cleanup-Image /StartComponentCleanup /ResetBase
set "DRC=!errorlevel!"
echo.
if "!DRC!"=="0" (call :Ok "ResetBase выполнен") else (call :Err "DISM завершился с кодом !DRC!")
call :PauseBack
goto CleanMenu

:ClnHiber
call :Hdr "ФАЙЛ ГИБЕРНАЦИИ"
echo  hiberfil.sys занимает место ≈ размеру ОЗУ.
echo  %Gray%(Быстрый запуск тоже использует гибернацию.)%Reset%
echo.
echo  %Bold%[1]%Reset%  Выключить гибернацию  (удалить hiberfil.sys)
echo  %Bold%[2]%Reset%  Включить гибернацию
echo  %Bold%[3]%Reset%  Показать статус
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto CleanMenu
if "%choice%"=="1" (
    powercfg /h off >nul 2>&1
    if errorlevel 1 (
        call :Err "Не удалось выключить гибернацию"
    ) else if exist "%SystemDrive%\hiberfil.sys" (
        call :Warn "Команда выполнена, но hiberfil.sys ещё присутствует"
    ) else (
        call :Ok "Гибернация выключена, hiberfil.sys удалён"
    )
    echo.
)
if "%choice%"=="2" (
    powercfg /h on >nul 2>&1
    if errorlevel 1 (
        call :Err "Не удалось включить гибернацию"
    ) else (
        call :Ok "Гибернация включена"
    )
    echo.
)
if "%choice%"=="3" (
    echo.
    powercfg /a
    if exist "%SystemDrive%\hiberfil.sys" (
        for %%F in ("%SystemDrive%\hiberfil.sys") do echo  hiberfil.sys: %%~zF байт
    ) else (
        echo  hiberfil.sys: отсутствует
    )
)
call :PauseBack
goto CleanMenu

:ClnRestore
call :Hdr "ТОЧКИ ВОССТАНОВЛЕНИЯ / ТЕНЕВЫЕ КОПИИ"
echo  %Bold%[1]%Reset%  Показать теневые копии и точки восстановления
echo  %Bold%[2]%Reset%  Удалить ВСЕ теневые копии / точки   %Red%(необратимо)%Reset%
echo  %Bold%[3]%Reset%  Удалить одну самую старую теневую копию
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto CleanMenu
if "%choice%"=="1" (
    echo.
    vssadmin list shadows
    echo.
    echo  %Gray%Точки восстановления:%Reset%
    powershell -NoProfile -Command "try { Get-ComputerRestorePoint -ErrorAction Stop | Select-Object CreationTime, Description, SequenceNumber | Format-Table -AutoSize } catch { Write-Host '  (точки восстановления недоступны или защита отключена)' }"
)
if "%choice%"=="2" (
    echo.
    echo  %Red%ВНИМАНИЕ: будут удалены ВСЕ теневые копии и точки восстановления.%Reset%
    echo  %Red%Это НЕОБРАТИМО и лишает возможности отката системы.%Reset%
    set /p "conf=  Введите yes для подтверждения: "
    if /i "!conf!"=="yes" (
        vssadmin delete shadows /all /quiet
        set "VSSRC=!errorlevel!"
        call :Log "VSS | delete all shadows | rc=!VSSRC!"
        if "!VSSRC!"=="0" (call :Ok "Все теневые копии удалены") else (call :Err "Не удалось удалить теневые копии (код !VSSRC!)")
    ) else (
        echo  %Gray%Отменено.%Reset%
    )
)
if "%choice%"=="3" (
    echo.
    echo  %Yellow%Будет удалена ОДНА самая старая теневая копия тома %SystemDrive%.%Reset%
    set /p "conf=  Продолжить? y/n: "
    if /i "!conf!"=="y" (
        vssadmin delete shadows /for=%SystemDrive% /oldest /quiet
        set "VSSRC=!errorlevel!"
        call :Log "VSS | delete oldest shadow on %SystemDrive% | rc=!VSSRC!"
        if "!VSSRC!"=="0" (call :Ok "Самая старая теневая копия удалена") else (call :Err "Не удалось удалить старейшую копию (код !VSSRC!)")
        echo  %Gray%Можно повторить, пока не останется нужное число копий.%Reset%
    ) else (
        echo  %Gray%Отменено.%Reset%
    )
)
call :PauseBack
goto CleanMenu

:ClnDelivery
call :Hdr "DELIVERY OPTIMIZATION"
echo  %Yellow%Очищаю кэш оптимизации доставки...%Reset%
echo.
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — очистка Delivery Optimization отменена."
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action ServicesBackup -OutFile "%BAK_SVC%\dosvc_backup.txt" -Services "dosvc"
set "DOBKRC=!errorlevel!"
if not "!DOBKRC!"=="0" (
    call :Err "Не удалось сохранить состояние dosvc (код !DOBKRC!) — остановка службы отменена."
    exit /b 1
)
if not exist "%BAK_SVC%\dosvc_backup.txt" (
    call :Err "Резерв dosvc не создан — очистка отменена."
    exit /b 1
)
call :Ok "состояние службы сохранено"
sc stop dosvc >nul 2>&1
set "DOSVCRC=!errorlevel!"
if "!DOSVCRC!" NEQ "0" (
    echo  %Gray%dosvc: уже остановлена или не удалось остановить%Reset%
) else (
    echo  %Gray%dosvc: остановлена%Reset%
)
echo.
call :CleanDirContents "%SystemRoot%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache" "DO: NetworkService cache"
call :CleanDirContents "%ProgramData%\Microsoft\Windows\DeliveryOptimization\Cache" "DO: ProgramData cache"
call :CleanDirContents "%SystemRoot%\SoftwareDistribution\DeliveryOptimization" "DO: SoftwareDistribution"
echo.
if exist "%SCRIPT_PS%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action ServicesRestore -InFile "%BAK_SVC%\dosvc_backup.txt"
    if "!errorlevel!"=="0" (call :Ok "исходное состояние службы dosvc восстановлено") else (call :Warn "dosvc: не удалось полностью восстановить состояние")
) else (
    net start dosvc >nul 2>&1
    call :Warn "SCU.ps1 не найден — dosvc запущен без учёта исходного состояния."
)
call :PauseBack
goto CleanMenu

:ClnSafeAll
call :Hdr "БЕЗОПАСНАЯ ОЧИСТКА"
echo  Будет выполнено:
echo    1. WinSxS StartComponentCleanup
echo    2. Delivery Optimization cache
echo.
set /p "conf=  Продолжить? y/n: "
if /i not "%conf%"=="y" goto CleanMenu
echo.
echo  %Yellow%[1/2] WinSxS...%Reset%
Dism.exe /Online /Cleanup-Image /StartComponentCleanup
set "DRC=!errorlevel!"
if "!DRC!"=="0" (call :Ok "WinSxS очищен") else (call :Err "WinSxS: DISM код !DRC!")
echo.
echo  %Yellow%[2/2] Delivery Optimization...%Reset%
set "DOFAIL=0"
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — шаг Delivery Optimization пропущен."
    set "DOFAIL=1"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action ServicesBackup -OutFile "%BAK_SVC%\dosvc_backup.txt" -Services "dosvc"
    set "DOBKRC=!errorlevel!"
    if not "!DOBKRC!"=="0" (
        call :Err "Не удалось сохранить состояние dosvc (код !DOBKRC!) — шаг пропущен."
        set "DOFAIL=1"
    ) else if not exist "%BAK_SVC%\dosvc_backup.txt" (
        call :Err "Резерв dosvc не создан — шаг пропущен."
        set "DOFAIL=1"
    ) else (
        call :Ok "состояние dosvc сохранено"
        sc stop dosvc >nul 2>&1
        call :CleanDirContents "%SystemRoot%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache" "DO: NetworkService cache"
        call :CleanDirContents "%ProgramData%\Microsoft\Windows\DeliveryOptimization\Cache" "DO: ProgramData cache"
        powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action ServicesRestore -InFile "%BAK_SVC%\dosvc_backup.txt"
        set "DORC=!errorlevel!"
        if not "!DORC!"=="0" (call :Err "dosvc: не удалось полностью восстановить состояние (код !DORC!)" & set "DOFAIL=1") else call :Ok "исходное состояние dosvc восстановлено"
    )
)
echo.
if not "!DRC!"=="0" set "DOFAIL=1"
if "!DOFAIL!"=="0" (echo  %Bold%%Green%ГОТОВО%Reset%) else (echo  %Bold%%Yellow%ЗАВЕРШЕНО С ПРЕДУПРЕЖДЕНИЯМИ%Reset%)
call :PauseBack
goto CleanMenu

:ClnDone
echo.
if "!CLNFAIL!"=="1" (echo  %Bold%%Yellow%ОЧИСТКА ЗАВЕРШЕНА С ОШИБКАМИ%Reset%) else (echo  %Bold%%Green%ГОТОВО%Reset%)
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  Для лучшего результата закройте браузеры перед очисткой.
call :PauseBack
goto CleanMenu

rem ========================================================================
rem  4. УДАЛЕНИЕ МУСОРНОГО ПО
rem ========================================================================
:BloatMenu
call :RequireAdmin
if errorlevel 1 goto MainMenu
call :Hdr "УДАЛЕНИЕ МУСОРНОГО ПО"
echo  %Yellow%Сканирование фактически установленных AppX...%Reset%
call :BloatCheck
set "BLOATSCANRC=!errorlevel!"
cls
echo.
echo  %Bold%%Cyan%УДАЛЕНИЕ МУСОРНОГО ПО%Reset%
echo  %Gray%--------------------------------------------------------------------%Reset%
if "!BLOATSCANRC!"=="1" echo  %Red%Полный список AppX не прочитан — удаление заблокировано, статусы не считаются подтверждёнными.%Reset%
call :BloatState Cam "Камера"
call :BloatState Dev "Центр разработки"
call :BloatState Hub "Центр отзывов"
call :BloatState Copilot "Copilot"
call :BloatState Bing "Поиск Microsoft Bing"
call :BloatState Clip "Microsoft Clipchamp"
call :BloatState News "Новости Microsoft"
call :BloatState Teams "Microsoft Teams"
call :BloatState ToDo "Microsoft To Do"
call :BloatState Outlook "Outlook"
call :BloatState Power "Power Automate"
call :BloatState Quick "Быстрая помощь"
call :BloatState Sol "Косынка"
call :BloatState Sound "Звукозапись"
call :BloatState Sticky "Записки"
call :BloatState Store "Microsoft Store"
call :BloatState Xbox "Xbox"
if "!_Edge!"=="1" (set "s=%Red%Установлено%Reset%") else if "!_EdgeScan!"=="0" (set "s=%Yellow%Неизвестно%Reset%") else (set "s=%Green%Отсутствует%Reset%")
echo    Microsoft Edge : !s!
echo.
echo  %Gray%[ 1..18 ] удаления AppX/Edge; Store, Edge и «Удалить всё» помечены как необратимые.%Reset%
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[ 1]%Reset%  Камера
echo  %Bold%[ 2]%Reset%  Центр разработки
echo  %Bold%[ 3]%Reset%  Центр отзывов
echo  %Bold%[ 4]%Reset%  Copilot
echo  %Bold%[ 5]%Reset%  Поиск Microsoft Bing
echo  %Bold%[ 6]%Reset%  Microsoft Clipchamp
echo  %Bold%[ 7]%Reset%  Новости Microsoft
echo  %Bold%[ 8]%Reset%  Microsoft Teams
echo  %Bold%[ 9]%Reset%  Microsoft To Do
echo  %Bold%[10]%Reset%  Outlook
echo  %Bold%[11]%Reset%  Power Automate
echo  %Bold%[12]%Reset%  Быстрая помощь
echo  %Bold%[13]%Reset%  Косынка
echo  %Bold%[14]%Reset%  Звукозапись
echo  %Bold%[15]%Reset%  Записки
echo  %Bold%[16]%Reset%  Microsoft Store %Red%[НЕОБР.]%Reset%
echo  %Bold%[17]%Reset%  Xbox
echo  %Bold%[18]%Reset%  Microsoft Edge %Red%[НЕОБР.]%Reset%
echo  %Red%[ A]%Reset%  Удалить всё перечисленное %Red%[НЕОБР.]%Reset%
echo  %Bold%[ 0]%Reset%  Назад
echo.
call :Ask
if "!BLOATSCANRC!"=="1" goto BloatMenu
if "%choice%"=="1" call :RemoveApp "Microsoft.WindowsCamera"
if "%choice%"=="2" call :RemoveApp "Microsoft.Windows.DevHome"
if "%choice%"=="3" call :RemoveApp "Microsoft.WindowsFeedbackHub"
if "%choice%"=="4" call :BloatCopilot
if "%choice%"=="5" call :RemoveApp "BingSearch"
if "%choice%"=="6" call :RemoveApp "Clipchamp.Clipchamp"
if "%choice%"=="7" call :RemoveApp "Microsoft.BingNews"
if "%choice%"=="8" (taskkill /f /im msteams.exe >nul 2>&1 & call :RemoveApp "MSTeams")
if "%choice%"=="9" (taskkill /f /im Todo.exe >nul 2>&1 & call :RemoveApp "Microsoft.Todos")
if "%choice%"=="10" (taskkill /f /im olk.exe >nul 2>&1 & call :RemoveApp "Microsoft.OutlookForWindows")
if "%choice%"=="11" goto BloatPower
if "%choice%"=="12" call :RemoveApp "MicrosoftCorporationII.QuickAssist"
if "%choice%"=="13" call :RemoveApp "Microsoft.MicrosoftSolitaireCollection"
if "%choice%"=="14" call :RemoveApp "Microsoft.WindowsSoundRecorder"
if "%choice%"=="15" call :RemoveApp "Microsoft.MicrosoftStickyNotes"
if "%choice%"=="16" goto BloatStore
if "%choice%"=="17" goto BloatXboxAction
if "%choice%"=="18" goto BloatEdgeAction
if /i "%choice%"=="A" goto BloatAll
if "%choice%"=="0" goto MainMenu
goto BloatMenu

:: --- separate menu handlers: no parenthesized IF blocks around CALL/GOTO ---
:BloatXboxAction
call :BloatXbox
set "BLOAT_ACTION_RC=!errorlevel!"
call :PauseBack
goto BloatMenu

:BloatEdgeAction
call :BloatEdge
set "BLOAT_ACTION_RC=!errorlevel!"
call :PauseBack
goto BloatMenu

:BloatState
set "s=%Red%НЕИЗВЕСТНО%Reset%"
set "v=unknown"
if /i "%~1"=="Cam" set "v=!_Cam!"
if /i "%~1"=="Dev" set "v=!_Dev!"
if /i "%~1"=="Hub" set "v=!_Hub!"
if /i "%~1"=="Copilot" set "v=!_Copilot!"
if /i "%~1"=="Bing" set "v=!_Bing!"
if /i "%~1"=="Clip" set "v=!_Clip!"
if /i "%~1"=="News" set "v=!_News!"
if /i "%~1"=="Teams" set "v=!_Teams!"
if /i "%~1"=="ToDo" set "v=!_ToDo!"
if /i "%~1"=="Outlook" set "v=!_Outlook!"
if /i "%~1"=="Power" set "v=!_Power!"
if /i "%~1"=="Quick" set "v=!_Quick!"
if /i "%~1"=="Sol" set "v=!_Sol!"
if /i "%~1"=="Sound" set "v=!_Sound!"
if /i "%~1"=="Sticky" set "v=!_Sticky!"
if /i "%~1"=="Store" set "v=!_Store!"
if /i "%~1"=="Xbox" set "v=!_Xbox!"
if "!_Scan!"=="1" if "!v!"=="1" set "s=%Red%Установлено%Reset%"
if "!_Scan!"=="1" if "!v!"=="0" set "s=%Green%Отсутствует%Reset%"
echo    %~2 : !s!
exit /b 0

:BloatCopilot
echo.
echo  %Red%Удаление Copilot — необратимое для установленных AppX.%Reset%
set /p "conf=  Введите YES для подтверждения: "
if /i not "!conf!"=="YES" exit /b 1
taskkill /f /im msedgewebview2.exe >nul 2>&1
taskkill /f /im msedge.exe >nul 2>&1
winget uninstall --name "Microsoft 365 Copilot" --silent --accept-source-agreements >nul 2>&1
set "WRC=!errorlevel!"
call :Log "WINGET | Microsoft 365 Copilot | rc=!WRC!"
call :RemoveApp "Copilot,Windows.Ai"
exit /b !errorlevel!

:BloatBing
call :RemoveApp "BingSearch"
exit /b !errorlevel!

:BloatPower
echo. & echo  %Red%Завершение процессов Power Automate...%Reset%
taskkill /f /im PowerAutomate.exe >nul 2>&1
taskkill /f /im PAD.Console.Host.exe >nul 2>&1
taskkill /f /im PAD.DesktopBehavior.exe >nul 2>&1
call :RemoveApp "Microsoft.PowerAutomateDesktop"
goto BloatMenu

:BloatStore
echo.
echo  %Red%Microsoft Store может потребоваться другим приложениям.%Reset%
echo  %Red%Удаление Store — необратимая операция в рамках этой утилиты.%Reset%
set /p "conf=  Введите YES для подтверждения: "
if /i not "!conf!"=="YES" goto BloatMenu
call :RemoveStore
set "RC=!errorlevel!"
if not "!RC!"=="0" call :Err "Store: операция завершилась с кодом !RC!"
goto BloatMenu

:RemoveStore
call :RemoveApp "Microsoft.WindowsStore,Microsoft.StorePurchaseApp,Microsoft.Services.Store.Engagement"
exit /b !errorlevel!

:BloatXbox
echo.
echo  %Red%Завершение процессов Xbox...%Reset%
taskkill /f /im XboxPcApp.exe >nul 2>&1
taskkill /f /im GameBar.exe >nul 2>&1
taskkill /f /im GameBarFTServer.exe >nul 2>&1
taskkill /f /im GamingServices.exe >nul 2>&1
echo  %Gray%Удаление связанных AppX одним проходом...%Reset%
call :RemoveApp "Microsoft.XboxApp,Microsoft.GamingApp,Microsoft.XboxGamingOverlay,Microsoft.XboxGameOverlay,Microsoft.XboxIdentityProvider,Microsoft.XboxSpeechToTextOverlay,Microsoft.Xbox.TCUI"
set "XBOXRC=!errorlevel!"
call :BloatCheck
set "XBOXCHECK=!errorlevel!"
if "!XBOXRC!"=="0" if "!XBOXCHECK!"=="0" (
    call :Ok "Xbox: удаление подтверждено повторным сканированием"
    exit /b 0
)
call :Err "Xbox: удаление завершилось не полностью; см. лог"
exit /b 1

:BloatEdge
echo.
echo  %Red%Удаление Microsoft Edge — необратимая операция.%Reset%
echo  %Red%Это может затронуть WebView2 и приложения, которые его используют.%Reset%
set "conf="
set /p "conf=  Введите YES для подтверждения: "
if /i not "!conf!"=="YES" exit /b 1
call :RemoveEdge
exit /b !errorlevel!

:RemoveEdge
taskkill /f /im msedge.exe >nul 2>&1
taskkill /f /im msedgewebview2.exe >nul 2>&1
set "EDGE_SETUP_FILE=%TEMP%\SCU_edge_setup.txt"
if exist "!EDGE_SETUP_FILE!" del /f /q "!EDGE_SETUP_FILE!" >nul 2>&1
set "EDGE_SETUP="
powershell -NoProfile -ExecutionPolicy Bypass -Command "$paths=@();$pf=[Environment]::GetEnvironmentVariable('ProgramFiles');$pfx=[Environment]::GetEnvironmentVariable('ProgramFiles(x86)');if($pf){$paths+=Join-Path $pf 'Microsoft\Edge\Application'};if($pfx){$paths+=Join-Path $pfx 'Microsoft\Edge\Application'};foreach($d in $paths){if($d -and (Test-Path -LiteralPath $d)){ $f=Get-ChildItem -LiteralPath $d -Filter setup.exe -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1;if($f){$f.FullName;break}}}" >"!EDGE_SETUP_FILE!" 2>nul
if exist "!EDGE_SETUP_FILE!" set /p "EDGE_SETUP="<"!EDGE_SETUP_FILE!"
if exist "!EDGE_SETUP_FILE!" del /f /q "!EDGE_SETUP_FILE!" >nul 2>&1
if defined EDGE_SETUP (
    call :VerifyFile "!EDGE_SETUP!" "Microsoft Edge setup"
    if errorlevel 1 exit /b 1
    "!EDGE_SETUP!" --uninstall --system-level --force-uninstall --verbose-logging
    set "EDGE_RC=!errorlevel!"
    call :Log "EDGE-SETUP-UNINSTALL | rc=!EDGE_RC!"
    if not "!EDGE_RC!"=="0" if not "!EDGE_RC!"=="3010" exit /b 1
)
winget uninstall --name "Microsoft Edge" --silent --accept-source-agreements >nul 2>&1
set "WRC=!errorlevel!"
call :Log "WINGET | Microsoft Edge | rc=!WRC!"
powershell -NoProfile -ExecutionPolicy Bypass -Command "try {$left=0;foreach($a in @(Get-AppxPackage -AllUsers -ErrorAction Stop)){if($a.Name -like '*MicrosoftEdge*'){$left++}};$files=0;$pf=[Environment]::GetEnvironmentVariable('ProgramFiles');$pfx=[Environment]::GetEnvironmentVariable('ProgramFiles(x86)');foreach($d in @($pf,$pfx)){if($d){$f=Join-Path $d 'Microsoft\Edge\Application\msedge.exe';if(Test-Path -LiteralPath $f){$files++}}};if($left -eq 0 -and $files -eq 0){exit 0};exit 1}catch{exit 2}"
set "VERIFY_RC=!errorlevel!"
call :Log "VERIFY | Edge absent | rc=!VERIFY_RC!"
if "!VERIFY_RC!"=="0" (call :Ok "Microsoft Edge отсутствует после удаления" & exit /b 0)
if "!VERIFY_RC!"=="2" (call :Err "Не удалось достоверно проверить AppX Microsoft Edge" & exit /b 1)
call :Err "Microsoft Edge всё ещё обнаруживается после удаления"
exit /b 1

:BloatAll
echo.
echo  %Red%Удаление ВСЕХ перечисленных AppX/Edge — необратимая операция.%Reset%
set /p "conf=  Введите DELETE для подтверждения: "
if /i not "!conf!"=="DELETE" goto BloatMenu
call :BloatCheck
if errorlevel 1 (call :Err "Полный список AppX недоступен — массовое удаление отменено" & goto BloatMenu)
set "BLOATFAIL=0"
taskkill /f /im PowerAutomate.exe >nul 2>&1
taskkill /f /im PAD.Console.Host.exe >nul 2>&1
taskkill /f /im PAD.DesktopBehavior.exe >nul 2>&1
taskkill /f /im Todo.exe >nul 2>&1
taskkill /f /im msteams.exe >nul 2>&1
taskkill /f /im olk.exe >nul 2>&1
taskkill /f /im XboxPcApp.exe >nul 2>&1
taskkill /f /im GameBar.exe >nul 2>&1
taskkill /f /im GamingServices.exe >nul 2>&1
for %%A in ("Microsoft.WindowsCamera" "Microsoft.Windows.DevHome" "Microsoft.WindowsFeedbackHub" "Copilot,Windows.Ai" "BingSearch" "Clipchamp.Clipchamp" "Microsoft.BingNews" "MSTeams" "Microsoft.Todos" "Microsoft.OutlookForWindows" "Microsoft.PowerAutomateDesktop" "MicrosoftCorporationII.QuickAssist" "Microsoft.MicrosoftSolitaireCollection" "Microsoft.WindowsSoundRecorder" "Microsoft.MicrosoftStickyNotes" "Microsoft.WindowsStore,Microsoft.StorePurchaseApp,Microsoft.Services.Store.Engagement" "Microsoft.XboxApp,Microsoft.GamingApp,Microsoft.XboxGamingOverlay,Microsoft.XboxGameOverlay,Microsoft.XboxIdentityProvider") do (
    call :RemoveApp "%%~A"
    if errorlevel 1 set "BLOATFAIL=1"
)
call :RemoveEdge
if errorlevel 1 set "BLOATFAIL=1"
call :BloatCheck
if errorlevel 1 set "BLOATFAIL=1"
if "!BLOATFAIL!"=="0" (
    set "ALLZ=!_Cam!!_Dev!!_Hub!!_Copilot!!_Bing!!_Clip!!_News!!_Teams!!_ToDo!!_Outlook!!_Power!!_Quick!!_Sol!!_Sound!!_Sticky!!_Store!!_Xbox!!_Edge!"
    if not "!ALLZ!"=="000000000000000000" set "BLOATFAIL=1"
)
if "!BLOATFAIL!"=="0" (call :Ok "AppX/Edge: повторная проверка подтверждает отсутствие") else call :Err "Массовое удаление завершилось не полностью; см. лог"
call :PauseBack
goto BloatMenu

:RemoveApp
echo. & echo  %Red%Удаление %~1...%Reset%
if not exist "%SCRIPT_PS%" (call :Err "SCU.ps1 не найден — удаление запрещено" & exit /b 2)
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action AppxRemove -Names "%~1"
set "APPXRC=!errorlevel!"
call :Log "APPX-REMOVE | %~1 | rc=!APPXRC!"
if "!APPXRC!"=="0" (call :Ok "%~1: пакет отсутствует после проверки" & exit /b 0)
call :Err "%~1: удаление не подтверждено повторным сканированием (rc=!APPXRC!)"
exit /b !APPXRC!

:BloatCheck
set "_Scan=0" & set "_Cam=0" & set "_Dev=0" & set "_Hub=0" & set "_Copilot=0"
set "_Bing=0" & set "_Clip=0" & set "_News=0" & set "_Teams=0"
set "_ToDo=0" & set "_Outlook=0" & set "_Power=0" & set "_Quick=0"
set "_Sol=0" & set "_Sound=0" & set "_Sticky=0"
set "_Store=0" & set "_Xbox=0" & set "_Edge=0" & set "_EdgeScan=0"
set "APPX_FILE=%TEMP%\appx_scan.tsv"
if exist "%APPX_FILE%" del /f /q "%APPX_FILE%" >nul 2>&1
if not exist "%SCRIPT_PS%" exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action AppxScan -OutFile "%APPX_FILE%" >nul 2>&1
set "APPXRC=!errorlevel!"
if not "!APPXRC!"=="0" exit /b 1
if not exist "%APPX_FILE%" exit /b 1
for /f "usebackq tokens=1,2 delims==" %%a in ("%APPX_FILE%") do (
    if /i "%%a"=="scan" set "_Scan=%%b"
    if /i "%%a"=="Cam" set "_Cam=%%b"
    if /i "%%a"=="Dev" set "_Dev=%%b"
    if /i "%%a"=="Hub" set "_Hub=%%b"
    if /i "%%a"=="Copilot" set "_Copilot=%%b"
    if /i "%%a"=="Bing" set "_Bing=%%b"
    if /i "%%a"=="Clip" set "_Clip=%%b"
    if /i "%%a"=="News" set "_News=%%b"
    if /i "%%a"=="Teams" set "_Teams=%%b"
    if /i "%%a"=="ToDo" set "_ToDo=%%b"
    if /i "%%a"=="Outlook" set "_Outlook=%%b"
    if /i "%%a"=="Power" set "_Power=%%b"
    if /i "%%a"=="Quick" set "_Quick=%%b"
    if /i "%%a"=="Sol" set "_Sol=%%b"
    if /i "%%a"=="Sound" set "_Sound=%%b"
    if /i "%%a"=="Sticky" set "_Sticky=%%b"
    if /i "%%a"=="Store" set "_Store=%%b"
    if /i "%%a"=="Xbox" set "_Xbox=%%b"
)
if not "!_Scan!"=="1" exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$pf=[Environment]::GetEnvironmentVariable('ProgramFiles');$pfx=[Environment]::GetEnvironmentVariable('ProgramFiles(x86)');$found=0;foreach($f in @($pf,$pfx)){if($f){$p=Join-Path $f 'Microsoft\Edge\Application\msedge.exe';if(Test-Path -LiteralPath $p){$found=1}}};if($found -eq 1){exit 0}else{exit 1}"
if errorlevel 1 (set "_Edge=0") else (set "_Edge=1")
set "_EdgeScan=1"
exit /b 0

rem ========================================================================
rem  5. ПРИВАТНОСТЬ
rem ========================================================================
:PrivMenu
call :RequireAdmin
if errorlevel 1 goto MainMenu
call :Hdr "ПРИВАТНОСТЬ, ТЕЛЕМЕТРИЯ, УВЕДОМЛЕНИЯ"
echo  %Bold%[1]%Reset%  Телеметрия и реклама
echo  %Bold%[2]%Reset%  Уведомления и советы
echo  %Bold%[3]%Reset%  Фоновые UWP-приложения
echo  %Bold%[4]%Reset%  Планировщик: телеметрия / CEIP
echo  %Bold%[5]%Reset%  Windows Copilot AI
echo  %Bold%[6]%Reset%  Оптимизация доставки
echo  %Bold%[7]%Reset%  Все сразу
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto TeleMenu
if "%choice%"=="2" goto NotifyMenu
if "%choice%"=="3" goto UWPMenu
if "%choice%"=="4" goto TasksMenu
if "%choice%"=="5" goto CopilotMenu
if "%choice%"=="6" goto DOMenu
if "%choice%"=="7" goto QuietAll
if "%choice%"=="0" goto MainMenu
goto PrivMenu

:TeleMenu
call :Hdr "ТЕЛЕМЕТРИЯ И РЕКЛАМА"
echo  %Bold%[1]%Reset%  Выкл
echo  %Bold%[2]%Reset%  Вкл
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto TeleOff
if "%choice%"=="2" goto TeleOn
if "%choice%"=="0" goto PrivMenu
goto TeleMenu

:TeleOff
echo.
echo  %Yellow%Отключение телеметрии и рекламы...%Reset%
set "OPFAIL=0"
sc query DiagTrack >nul 2>&1
if not errorlevel 1 (
    sc config DiagTrack start= disabled >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
    sc query DiagTrack 2>nul | findstr /i "RUNNING" >nul 2>&1
    if not errorlevel 1 (
        sc stop DiagTrack >nul 2>&1
        if errorlevel 1 set "OPFAIL=1"
    )
) else (
    call :Log "TELE | DiagTrack absent"
)
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection" /v AllowTelemetry /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement" /v ScoobeSystemSettingEnabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKLM\SOFTWARE\Policies\Microsoft\SQMClient\Windows" /v CEIPEnable /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338389Enabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338387Enabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338393Enabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-353694Enabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-353696Enabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-310093Enabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SystemPaneSuggestionsEnabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SilentInstalledAppsEnabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\AppCompat" /v AITEnable /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy" /v TailoredExperiencesWithDiagnosticDataEnabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo" /v Enabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Input\TIPC" /v Enabled /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\TabletPC" /v PreventHandwritingDataSharing /t REG_DWORD /d 1 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports" /v PreventHandwritingErrorReports /t REG_DWORD /d 1 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKCU\Software\Microsoft\Siuf\Rules" /v NumberOfSIUFInPeriod /t REG_DWORD /d 0 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location" /v Value /t REG_SZ /d "Deny" /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization" /v NoLockScreenCamera /t REG_DWORD /d 1 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
reg add "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting" /v Disabled /t REG_DWORD /d 1 /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
if "!OPFAIL!"=="0" (call :Ok "Телеметрия и реклама отключены") else (call :Err "Не удалось полностью отключить телеметрию и рекламу — см. лог")
call :PauseBack
goto TeleMenu

:TeleOn
echo.
echo  %Yellow%Включение телеметрии и рекламы...%Reset%
set "OPFAIL=0"
sc query DiagTrack >nul 2>&1
if not errorlevel 1 (
    sc config DiagTrack start= auto >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
    sc start DiagTrack >nul 2>&1
    if errorlevel 1 (
        call :Log "TELE | DiagTrack start FAILED"
        set "OPFAIL=1"
    )
) else (
    call :Log "TELE | DiagTrack absent"
)
reg query "HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection" /v AllowTelemetry >nul 2>&1
if not errorlevel 1 (
    reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection" /v AllowTelemetry /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement" /v ScoobeSystemSettingEnabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement" /v ScoobeSystemSettingEnabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKLM\SOFTWARE\Policies\Microsoft\SQMClient\Windows" /v CEIPEnable >nul 2>&1
if not errorlevel 1 (
    reg delete "HKLM\SOFTWARE\Policies\Microsoft\SQMClient\Windows" /v CEIPEnable /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338389Enabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338389Enabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338387Enabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338387Enabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338393Enabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338393Enabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-353694Enabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-353694Enabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-353696Enabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-353696Enabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-310093Enabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-310093Enabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SystemPaneSuggestionsEnabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SystemPaneSuggestionsEnabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SilentInstalledAppsEnabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SilentInstalledAppsEnabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKLM\SOFTWARE\Policies\Microsoft\Windows\AppCompat" /v AITEnable >nul 2>&1
if not errorlevel 1 (
    reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\AppCompat" /v AITEnable /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy" /v TailoredExperiencesWithDiagnosticDataEnabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy" /v TailoredExperiencesWithDiagnosticDataEnabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo" /v Enabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo" /v Enabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Input\TIPC" /v Enabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Input\TIPC" /v Enabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKLM\SOFTWARE\Policies\Microsoft\Windows\TabletPC" /v PreventHandwritingDataSharing >nul 2>&1
if not errorlevel 1 (
    reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\TabletPC" /v PreventHandwritingDataSharing /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKLM\SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports" /v PreventHandwritingErrorReports >nul 2>&1
if not errorlevel 1 (
    reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports" /v PreventHandwritingErrorReports /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKCU\Software\Microsoft\Siuf\Rules" /v NumberOfSIUFInPeriod >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\Software\Microsoft\Siuf\Rules" /v NumberOfSIUFInPeriod /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location" /v Value >nul 2>&1
if not errorlevel 1 (
    reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location" /v Value /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization" /v NoLockScreenCamera >nul 2>&1
if not errorlevel 1 (
    reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization" /v NoLockScreenCamera /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
reg query "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting" /v Disabled >nul 2>&1
if not errorlevel 1 (
    reg delete "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting" /v Disabled /f >nul 2>&1
    if errorlevel 1 set "OPFAIL=1"
)
if "!OPFAIL!"=="0" (call :Ok "Телеметрия и реклама включены") else (call :Err "Не удалось полностью включить телеметрию и рекламу — см. лог")
call :PauseBack
goto TeleMenu

:NotifyMenu
call :Hdr "УВЕДОМЛЕНИЯ И СОВЕТЫ"
echo  %Bold%[1]%Reset%  Отключить советы и предложения Windows
echo  %Bold%[2]%Reset%  Включить советы
echo  %Bold%[3]%Reset%  Отключить уведомления приложений
echo  %Bold%[4]%Reset%  Включить уведомления приложений
echo  %Bold%[5]%Reset%  Отключить «Предложить способы завершения настройки»
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto PrivMenu
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338389Enabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338393Enabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-353694Enabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-353696Enabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SoftLandingEnabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SystemPaneSuggestionsEnabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement" /v ScoobeSystemSettingEnabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy" /v TailoredExperiencesWithDiagnosticDataEnabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    call :ReportOp "Советы и предложения отключены" "Не удалось полностью отключить советы и предложения"
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338389Enabled /t REG_DWORD /d 1 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SoftLandingEnabled /t REG_DWORD /d 1 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SystemPaneSuggestionsEnabled /t REG_DWORD /d 1 /f >nul
    call :FailIf
    call :ReportOp "Советы включены" "Не удалось полностью включить советы"
)
if "%choice%"=="3" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\PushNotifications" /v ToastEnabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Policies\Microsoft\Windows\Explorer" /v DisableNotificationCenter /t REG_DWORD /d 1 /f >nul
    call :FailIf
    call :ReportOp "Уведомления / центр отключены" "Не удалось отключить уведомления"
    echo  %Yellow%Может потребоваться выход из системы.%Reset%
)
if "%choice%"=="4" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\PushNotifications" /v ToastEnabled /t REG_DWORD /d 1 /f >nul
    call :FailIf
    reg delete "HKCU\Software\Policies\Microsoft\Windows\Explorer" /v DisableNotificationCenter /f >nul 2>&1
    call :FailIfDel
    call :ReportOp "Уведомления включены" "Не удалось включить уведомления"
)
if "%choice%"=="5" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement" /v ScoobeSystemSettingEnabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-310093Enabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    call :ReportOp "«Завершение настройки» отключено" "Не удалось отключить «Завершение настройки»"
)
call :PauseBack
goto NotifyMenu

:UWPMenu
call :Hdr "ФОНОВЫЕ UWP-ПРИЛОЖЕНИЯ"
echo  %Bold%[1]%Reset%  Выкл
echo  %Bold%[2]%Reset%  Вкл
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto PrivMenu
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications" /v GlobalUserDisabled /t REG_DWORD /d 1 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Search" /v BackgroundAppGlobalToggle /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKLM\SYSTEM\CurrentControlSet\Services\embeddedmode" /v Start /t REG_DWORD /d 4 /f >nul
    call :FailIf
    call :ReportOp "Фоновые UWP отключены" "Не удалось полностью отключить фоновые UWP"
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications" /v GlobalUserDisabled /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Search" /v BackgroundAppGlobalToggle /t REG_DWORD /d 1 /f >nul
    call :FailIf
    reg add "HKLM\SYSTEM\CurrentControlSet\Services\embeddedmode" /v Start /t REG_DWORD /d 3 /f >nul
    call :FailIf
    call :ReportOp "Фоновые UWP включены" "Не удалось полностью включить фоновые UWP"
)
call :PauseBack
goto UWPMenu

:TasksMenu
call :Hdr "ПЛАНИРОВЩИК — ТЕЛЕМЕТРИЯ / CEIP"
echo  %Bold%[1]%Reset%  Отключить задачи телеметрии и CEIP
echo  %Bold%[2]%Reset%  Включить задачи обратно  (из резерва)
echo  %Bold%[3]%Reset%  Показать статус задач
echo  %Bold%[4]%Reset%  Сохранить текущее состояние в резерв
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto PrivMenu
if "%choice%"=="1" goto TasksOff
if "%choice%"=="2" goto TasksOn
if "%choice%"=="3" goto TasksShow
if "%choice%"=="4" goto TasksSave
goto TasksMenu

:TasksOff
call :Hdr "ОТКЛЮЧЕНИЕ ЗАДАЧ ТЕЛЕМЕТРИИ"
call :TasksDisableList "%TASKLIST%"
call :PauseBack
goto TasksMenu

:TasksOn
call :Hdr "ВКЛЮЧЕНИЕ ЗАДАЧ ТЕЛЕМЕТРИИ"
call :TasksRestoreAll
call :PauseBack
goto TasksMenu

:TasksSave
call :Hdr "СОХРАНЕНИЕ СОСТОЯНИЯ ЗАДАЧ"
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — действие недоступно."
    call :PauseBack
    goto TasksMenu
)
if not exist "%BAK_TASKS%" mkdir "%BAK_TASKS%" >nul 2>&1
set "TSAVEFILE=%BAK_TASKS%\tasks_state_%STAMP%.txt"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action TasksBackup -Tasks "%TASKLIST%" -OutFile "!TSAVEFILE!"
set "TSRC=!errorlevel!"
echo.
if "!TSRC!"=="0" (call :Ok "Сохранено: !TSAVEFILE!") else (call :Err "Не удалось сохранить состояние задач (код !TSRC!)")
call :PauseBack
goto TasksMenu

:TasksDisableList
rem %1 = список задач через ";". Первый запуск создаёт резерв, повторные его не затирают.
set "TLIST=%~1"
if not exist "%BAK_TASKS%" mkdir "%BAK_TASKS%" >nul 2>&1
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — управление задачами недоступно."
    exit /b 1
)
echo  %Yellow%Резерв и отключение задач...%Reset%
echo.
if exist "%BACKUP_TASKS%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action TasksDisable -Tasks "!TLIST!"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action TasksDisable -Tasks "!TLIST!" -OutFile "%BACKUP_TASKS%"
)
set "TRC=!errorlevel!"
echo.
if "!TRC!"=="0" (
    call :Ok "Задачи отключены. Резерв: %BACKUP_TASKS%"
) else (
    call :Warn "Часть задач не удалось отключить (код !TRC!) — см. вывод выше."
)
call :Log "TASK | disable rc=!TRC!"
exit /b !TRC!

:TasksRestoreAll
if not exist "%BACKUP_TASKS%" (
    call :Warn "Нет резерва задач — сначала отключите задачи или сохраните состояние."
    exit /b 1
)
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — восстановление недоступно."
    exit /b 1
)
echo  %Yellow%Восстанавливаю исходное состояние задач...%Reset%
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action TasksRestore -InFile "%BACKUP_TASKS%"
set "TRRC=!errorlevel!"
echo.
if "!TRRC!"=="0" (call :Ok "Задачи возвращены в исходное состояние") else (call :Warn "Часть задач не удалось вернуть (код !TRRC!) — см. вывод выше.")
call :Log "TASK | restore rc=!TRRC!"
exit /b !TRRC!

:TasksShow
echo.
schtasks /query /fo LIST 2>nul | findstr /i /c:"Microsoft Compatibility Appraiser" /c:"ProgramDataUpdater" /c:"Consolidator" /c:"UsbCeip" /c:"DmClient" /c:"QueueReporting" /c:"DiskDiagnosticDataCollector" /c:"Status:" /c:"TaskName:"
echo.
echo  %Gray%(полный список — в Планировщике заданий)%Reset%
call :PauseBack
goto TasksMenu

:CopilotMenu
call :Hdr "WINDOWS COPILOT AI"
echo  %Bold%[1]%Reset%  Выкл
echo  %Bold%[2]%Reset%  Вкл
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto PrivMenu
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKCU\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot" /v TurnOffWindowsCopilot /t REG_DWORD /d 1 /f >nul 2>&1
    call :FailIf
    reg add "HKCU\SOFTWARE\Policies\Microsoft\Windows\WindowsAI" /v DisableAIDataAnalysis /t REG_DWORD /d 1 /f >nul 2>&1
    call :FailIf
    call :ReportOp "Windows Copilot AI отключён" "Не удалось отключить Windows Copilot AI"
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg delete "HKCU\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot" /v TurnOffWindowsCopilot /f >nul 2>&1
    call :FailIfDel
    reg delete "HKCU\SOFTWARE\Policies\Microsoft\Windows\WindowsAI" /v DisableAIDataAnalysis /f >nul 2>&1
    call :FailIfDel
    call :ReportOp "Windows Copilot AI включён" "Не удалось включить Windows Copilot AI"
)
call :PauseBack
goto CopilotMenu

:DOMenu
call :Hdr "ОПТИМИЗАЦИЯ ДОСТАВКИ"
echo  %Bold%[1]%Reset%  Выкл
echo  %Bold%[2]%Reset%  Вкл
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto PrivMenu
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization" /v DODownloadMode /t REG_DWORD /d 0 /f >nul 2>&1
    call :FailIf
    sc config DoSvc start= disabled >nul 2>&1
    call :FailIf
    net stop DoSvc >nul 2>&1
    call :FailIfNet
    echo. & if "!OPFAIL!"=="0" (call :Ok "Оптимизация доставки отключена") else (call :Err "Не удалось полностью отключить оптимизацию доставки")
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization" /v DODownloadMode /f >nul 2>&1
    call :FailIfDel
    sc config DoSvc start= demand >nul 2>&1
    call :FailIf
    net start DoSvc >nul 2>&1
    call :FailIfNet
    echo. & if "!OPFAIL!"=="0" (call :Ok "Оптимизация доставки включена") else (call :Err "Не удалось полностью включить оптимизацию доставки")
)
call :PauseBack
goto DOMenu

:QuietAll
call :Hdr "ТИХИЙ РЕЖИМ"
echo  Отключит:
echo    - советы и предложения Windows
echo    - фоновые UWP
echo    - задачи телеметрии / CEIP / Feedback
echo    - телеметрию DiagTrack
echo    - Copilot
echo.
set /p "conf=  Продолжить? y/n: "
if /i not "%conf%"=="y" goto PrivMenu
echo.
set "OPFAIL=0"
echo  %Yellow%Советы...%Reset%
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338389Enabled /t REG_DWORD /d 0 /f >nul
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338393Enabled /t REG_DWORD /d 0 /f >nul
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SoftLandingEnabled /t REG_DWORD /d 0 /f >nul
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SystemPaneSuggestionsEnabled /t REG_DWORD /d 0 /f >nul
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement" /v ScoobeSystemSettingEnabled /t REG_DWORD /d 0 /f >nul
call :FailIf
if "!OPFAIL!"=="0" (call :Ok "советы") else (call :Err "советы: часть ключей не применена")
echo  %Yellow%Фоновые UWP...%Reset%
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications" /v GlobalUserDisabled /t REG_DWORD /d 1 /f >nul
call :FailIf
if "!OPFAIL!"=="0" (call :Ok "UWP") else (call :Err "UWP: не удалось записать параметр")
echo  %Yellow%Планировщик...%Reset%
call :TasksDisableList "%TASKLIST%"
if errorlevel 1 set "OPFAIL=1"
echo  %Yellow%DiagTrack / Copilot...%Reset%
call :EnsureBackup
call :DisSvc DiagTrack
if errorlevel 1 set "OPFAIL=1"
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection" /v AllowTelemetry /t REG_DWORD /d 0 /f >nul 2>&1
call :FailIf
reg add "HKCU\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot" /v TurnOffWindowsCopilot /t REG_DWORD /d 1 /f >nul 2>&1
call :FailIf
if "!OPFAIL!"=="0" (call :Ok "DiagTrack / Copilot") else (call :Err "DiagTrack / Copilot: часть шагов не выполнена")
echo.
if "!OPFAIL!"=="0" (echo  %Bold%%Green%ГОТОВО%Reset%) else (echo  %Bold%%Red%ГОТОВО С ОШИБКАМИ%Reset%)
call :PauseBack
goto PrivMenu

rem ========================================================================
rem  6. СЛУЖБЫ WINDOWS
rem ========================================================================
:SvcMenu
call :RequireAdmin
if errorlevel 1 goto MainMenu
call :Hdr "СЛУЖБЫ WINDOWS"
echo  %Green%[раб.]%Reset% работает  %Yellow%[стоп.]%Reset% остановлена  %Gray%[ручн.]%Reset% вручную
echo  %Red%[откл.]%Reset% отключена  %Gray%[нет]%Reset% не установлена
echo  %Gray%--------------------------------------------------------------------%Reset%
echo.
set "SVCIDX=0"
for %%S in (%SVCLIST%) do (
    set /a SVCIDX+=1
    call :PrintStatus !SVCIDX! %%S "!SVCDESC.%%S!"
)
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[A]%Reset%  Отключить все     %Bold%[B]%Reset%  Включить все
echo  %Bold%[R]%Reset%  Откат             %Bold%[S]%Reset%  Сохранить текущие
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if /i "%choice%"=="0" goto MainMenu
if /i "%choice%"=="A" goto SvcDisableAll
if /i "%choice%"=="B" goto SvcEnableAll
if /i "%choice%"=="R" goto SvcRestore
if /i "%choice%"=="S" goto SvcSaveBackup
set "SVCSEL="
set "SVCIDX=0"
for %%S in (%SVCLIST%) do (
    set /a SVCIDX+=1
    if "!SVCIDX!"=="%choice%" set "SVCSEL=%%S"
)
if defined SVCSEL (
    call :ToggleSvc !SVCSEL!
    goto SvcMenu
)
echo.
echo  %Yellow%Неверный выбор.%Reset%
timeout /t 1 /nobreak >nul
goto SvcMenu

:PrintStatus
set "NUM=%~1"
set "SVC=%~2"
set "NAM=%~3"
set "TAG=%Gray%[нет]%Reset%"
sc query "%SVC%" >nul 2>&1
if errorlevel 1 goto :PrintStatusOut
set "SVDIS=0"
sc qc "%SVC%" 2>nul | findstr /i "DISABLED" >nul 2>&1 && set "SVDIS=1"
if "!SVDIS!"=="1" (
    set "TAG=%Red%[откл.]%Reset%"
) else (
    set "TAG=%Yellow%[ручн.]%Reset%"
    sc qc "%SVC%" 2>nul | findstr /i "AUTO_START" >nul 2>&1 && set "TAG=%Yellow%[стоп.]%Reset%"
    sc query "%SVC%" 2>nul | findstr /i "RUNNING" >nul 2>&1 && set "TAG=%Green%[раб.]%Reset%"
)
:PrintStatusOut
if %NUM% LSS 10 (echo  %Bold%[ %NUM%]%Reset%  !TAG!  %NAM%) else (echo  %Bold%[%NUM%]%Reset%  !TAG!  %NAM%)
exit /b

:ToggleSvc
set "SVC=%~1"
sc query "%SVC%" >nul 2>&1
if errorlevel 1 (
    echo.
    echo  %Gray%Служба %SVC% отсутствует.%Reset%
    timeout /t 1 >nul
    exit /b
)
call :EnsureBackup
set "SVDIS=0"
sc qc "%SVC%" 2>nul | findstr /i "DISABLED" >nul 2>&1 && set "SVDIS=1"
if "!SVDIS!"=="1" (
    call :SvcRestoreOne "%SVC%"
    set "SVC_RC=!errorlevel!"
) else (
    call :DisSvc "%SVC%"
    set "SVC_RC=!errorlevel!"
)
timeout /t 1 >nul
exit /b !SVC_RC!

rem Восстанавливает одну службу из резерва. Если резерва нет — включает вручную.
:SvcRestoreOne
set "SVC=%~1"
set "ONELINE="
if exist "%BACKUP_SVC%" for /f "usebackq delims=" %%L in ("%BACKUP_SVC%") do (
    for /f "tokens=1 delims=|" %%a in ("%%L") do if /i "%%a"=="%SVC%" set "ONELINE=%%L"
)
if not defined ONELINE (
    sc config "%SVC%" start= demand >nul 2>&1
    if errorlevel 1 (
        call :Err "%SVC%: не удалось включить"
        exit /b 1
    )
    call :SvcIsRunning "%SVC%"
    if "!RUNFLAG!"=="1" (
        echo  %Green%[вкл]%Reset%  %SVC%
        exit /b 0
    )
    sc start "%SVC%" >nul 2>&1
    set "START_RC=!errorlevel!"
    if not "!START_RC!"=="0" (
        call :Err "%SVC%: не удалось запустить (!START_RC!)"
        exit /b 1
    )
    call :SvcIsRunning "%SVC%"
    if "!RUNFLAG!"=="1" (
        echo  %Green%[вкл]%Reset%  %SVC%
        exit /b 0
    )
    echo  %Yellow%[вкл]%Reset%  %SVC%  %Gray%(запуск не подтверждён)%Reset%
    exit /b 1
)
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — восстановление из резерва недоступно"
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action ServicesRestore -InFile "%BACKUP_SVC%" -Services "%SVC%" >nul 2>&1
set "ONERC=!errorlevel!"
if "!ONERC!"=="0" (
    echo  %Green%[вкл]%Reset%  %SVC%
    exit /b 0
)
call :Err "%SVC%: не удалось восстановить исходное состояние (!ONERC!)"
exit /b !ONERC!

:SvcDisableAll
call :Hdr "ОТКЛЮЧЕНИЕ СЛУЖБ"
call :EnsureBackup
if not "!SVCOK!"=="1" (
    echo.
    call :Err "Без резервной копии массовое отключение запрещено."
    call :Log "SVC  | disable-all aborted: no backup"
    call :PauseBack
    goto SvcMenu
)
set "SVCFAIL=0"
for %%S in (%SVCLIST%) do (
    call :DisSvc %%S
    if errorlevel 1 set "SVCFAIL=1"
)
echo.
if "!SVCFAIL!"=="0" (call :Ok "Готово: все доступные службы обработаны") else call :Err "Часть служб не отключена; см. лог"
call :PauseBack
goto SvcMenu

:SvcEnableAll
call :Hdr "ВКЛЮЧЕНИЕ СЛУЖБ"
if exist "%BACKUP_SVC%" (
    echo  %Gray%Есть резерв — восстанавливаю исходное состояние служб...%Reset%
    echo.
    if not exist "%SCRIPT_PS%" (
        call :Err "SCU.ps1 не найден — восстановление из резерва недоступно."
        call :PauseBack
        goto SvcMenu
    )
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action ServicesRestore -InFile "%BACKUP_SVC%"
    set "SVERC=!errorlevel!"
    echo.
    if "!SVERC!"=="0" (call :Ok "Службы возвращены в исходное состояние") else (call :Warn "Часть служб не удалось вернуть (код !SVERC!) — см. лог.")
) else (
    set "SVCFAIL=0"
    for %%S in (%SVCLIST%) do (
        call :EnSvc %%S
        if errorlevel 1 set "SVCFAIL=1"
    )
    echo.
    if "!SVCFAIL!"=="0" (call :Ok "Готово: все доступные службы обработаны") else call :Err "Часть служб не включена; см. лог"
)
call :PauseBack
goto SvcMenu

:SvcIsRunning
rem %1 = служба. RUNFLAG=1, если состояние RUNNING.
set "RUNFLAG=0"
sc query "%~1" 2>nul | findstr /i "RUNNING" >nul 2>&1 && set "RUNFLAG=1"
exit /b

:DisSvc
set "SVC=%~1"
sc query "%SVC%" >nul 2>&1
if errorlevel 1 (echo  %Gray%[нет]%Reset%  %SVC% & exit /b 0)
call :SvcIsRunning "%SVC%"
set "RUN0=!RUNFLAG!"
sc config "%SVC%" start= disabled >nul 2>&1
if errorlevel 1 (
    echo  %Red%[X]%Reset%    %SVC%  %Gray%(не удалось задать тип запуска)%Reset%
    call :Log "SVC  | %SVC% disable FAILED"
    exit /b 1
)
if "!RUN0!"=="1" (
    sc stop "%SVC%" >nul 2>&1
    if errorlevel 1 (
        echo  %Yellow%[!]%Reset%  %SVC%  %Gray%(тип отключён, остановить не удалось)%Reset%
        call :Log "SVC  | %SVC% disabled, stop FAILED"
        exit /b 1
    )
)
echo  %Red%[откл]%Reset%  %SVC%
call :Log "SVC  | %SVC% -> disabled"
exit /b 0

:EnSvc
set "SVC=%~1"
sc query "%SVC%" >nul 2>&1
if errorlevel 1 (echo  %Gray%[нет]%Reset%  %SVC% & exit /b 0)
sc config "%SVC%" start= demand >nul 2>&1
if errorlevel 1 (
    echo  %Red%[X]%Reset%    %SVC%  %Gray%(не удалось включить)%Reset%
    call :Log "SVC  | %SVC% enable FAILED"
    exit /b 1
)
call :SvcIsRunning "%SVC%"
if "!RUNFLAG!"=="1" (
    echo  %Green%[вкл]%Reset%  %SVC%
    call :Log "SVC  | %SVC% already running after enable rc=0"
    exit /b 0
)
sc start "%SVC%" >nul 2>&1
set "START_RC=!errorlevel!"
if not "!START_RC!"=="0" (
    call :Log "SVC  | %SVC% start FAILED rc=!START_RC!"
    call :Err "%SVC%: команда запуска завершилась с кодом !START_RC!"
    exit /b 1
)
call :SvcIsRunning "%SVC%"
if "!RUNFLAG!"=="1" (
    echo  %Green%[вкл]%Reset%  %SVC%
    call :Log "SVC  | %SVC% -> demand/running rc=0"
    exit /b 0
)
echo  %Yellow%[вкл]%Reset%  %SVC%  %Gray%(запуск не подтверждён)%Reset%
call :Log "SVC  | %SVC% start not confirmed rc=0"
exit /b 1

:EnsureBackup
rem Гарантирует наличие резерва. SVCOK=1 при успехе.
set "SVCOK=0"
if exist "%BACKUP_SVC%" set "SVCOK=1"
if "!SVCOK!"=="1" exit /b 0
call :WriteBackup
exit /b

:SvcSaveBackup
call :WriteBackup
echo.
if "!SVCOK!"=="1" (call :Ok "Сохранено: %BACKUP_SVC%") else (call :Err "Не удалось сохранить состояние служб")
call :PauseBack
goto SvcMenu

:WriteBackup
if not exist "%BAK_SVC%" mkdir "%BAK_SVC%" >nul 2>&1
set "SVCOK=0"
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — резерв служб невозможен."
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action ServicesBackup -Services "%SVCCSV%" -OutFile "%BACKUP_SVC%" >nul 2>&1
set "BKRC=!errorlevel!"
if not "!BKRC!"=="0" (
    call :Err "Не удалось сохранить состояние служб (код !BKRC!)."
    call :Log "SVC  | backup FAILED rc=!BKRC!"
    exit /b 1
)
if not exist "%BACKUP_SVC%" (
    call :Err "Файл резерва служб не создан."
    exit /b 1
)
set "SVCOK=1"
call :Log "SVC  | backup saved: %BACKUP_SVC%"
exit /b 0

:SvcRestore
if not exist "%BACKUP_SVC%" (
    echo.
    call :Warn "Нет файла отката. Сначала измените службы или нажмите S."
    call :PauseBack
    goto SvcMenu
)
call :Hdr "ОТКАТ СЛУЖБ"
echo  Файл: %BACKUP_SVC%
echo.
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — откат недоступен."
    call :PauseBack
    goto SvcMenu
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action ServicesRestore -InFile "%BACKUP_SVC%"
set "SVRRC=!errorlevel!"
echo.
if "!SVRRC!"=="0" (
    call :Ok "Откат выполнен"
    call :Log "SVC  | restore OK"
) else (
    call :Warn "Откат завершён с ошибками (код !SVRRC!) — см. лог."
    call :Log "SVC  | restore rc=!SVRRC!"
)
call :PauseBack
goto SvcMenu

rem ========================================================================
rem  7. АВТОЗАГРУЗКА
rem ========================================================================
:StartUpMenu
call :RequireAdmin
if errorlevel 1 goto MainMenu
call :Hdr "АВТОЗАГРУЗКА ПРОГРАММ"
echo  %Bold%[1]%Reset%  Показать все элементы автозагрузки
echo  %Bold%[2]%Reset%  Отключить элемент  (по номеру)
echo  %Bold%[3]%Reset%  Включить обратно из резерва
echo  %Bold%[4]%Reset%  Открыть папки Startup
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto SUShow
if "%choice%"=="2" goto SUDisable
if "%choice%"=="3" goto SUEnable
if "%choice%"=="4" goto SUFolders
if "%choice%"=="0" goto MainMenu
goto StartUpMenu

:SUShow
call :Hdr "СПИСОК АВТОЗАГРУЗКИ"
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — сканирование недоступно."
    call :PauseBack
    goto StartUpMenu
)
echo  %Yellow%Сканирую реестр и папки Startup...%Reset%
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action StartupList -OutFile "%LIST_STARTUP%"
if errorlevel 1 (
    echo.
    call :Err "Не удалось получить список автозагрузки."
    call :PauseBack
    goto StartUpMenu
)
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Gray%Список сохранён. Пункт [2] отключает элемент по номеру.%Reset%
call :PauseBack
goto StartUpMenu

:SUDisable
call :Hdr "ОТКЛЮЧЕНИЕ ЭЛЕМЕНТА"
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — действие недоступно."
    call :PauseBack
    goto StartUpMenu
)
if not exist "%LIST_STARTUP%" (
    call :Warn "Сначала выполните пункт [1] — показать список."
    call :PauseBack
    goto StartUpMenu
)
echo  %Gray%Введите номер элемента из списка [1].%Reset%
echo.
set /p "num=  Номер: "
if "%num%"=="" goto StartUpMenu
if "%num%"=="0" goto StartUpMenu
echo %num%|findstr /r "^[0-9][0-9]*$" >nul
if errorlevel 1 (
    echo.
    call :Err "Нужно ввести номер (число)."
    call :PauseBack
    goto StartUpMenu
)
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action StartupDisable -ListFile "%LIST_STARTUP%" -Index %num% -BackupDir "%BACKUP_STARTUP%"
set "SURC=!errorlevel!"
echo.
if "!SURC!"=="0" (
    call :Ok "Элемент отключён (резерв: %BACKUP_STARTUP%)"
) else (
    call :Err "Не удалось отключить элемент (код !SURC!) — см. вывод выше."
)
call :Log "STARTUP | disable idx=%num% rc=!SURC!"
call :PauseBack
goto StartUpMenu

:SUEnable
call :Hdr "ВКЛЮЧЕНИЕ ИЗ РЕЗЕРВА"
if not exist "%SCRIPT_PS%" (
    call :Err "SCU.ps1 не найден — восстановление недоступно."
    call :PauseBack
    goto StartUpMenu
)
if not exist "%BACKUP_STARTUP%\manifest.json" (
    call :Warn "Резерв автозагрузки пуст — нечего восстанавливать."
    call :PauseBack
    goto StartUpMenu
)
echo  %Yellow%Восстанавливаю элементы автозагрузки...%Reset%
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PS%" -Action StartupRestore -BackupDir "%BACKUP_STARTUP%"
set "SURC=!errorlevel!"
echo.
if "!SURC!"=="0" (
    call :Ok "Восстановление завершено"
) else (
    call :Warn "Часть элементов не удалось вернуть (код !SURC!) — см. вывод выше."
)
call :Log "STARTUP | restore rc=!SURC!"
call :PauseBack
goto StartUpMenu

:SUFolders
explorer shell:startup
explorer shell:common startup
goto StartUpMenu

rem ========================================================================
rem  8. ПИТАНИЕ, ПАМЯТЬ И CPU
rem ========================================================================
:PerfMenu
call :RequireAdmin
if errorlevel 1 goto MainMenu
call :Hdr "ПИТАНИЕ, ПАМЯТЬ И CPU"
echo  %Bold%[1]%Reset%  План электропитания
echo  %Bold%[2]%Reset%  Гибернация
echo  %Bold%[3]%Reset%  Быстрый запуск
echo  %Bold%[4]%Reset%  Файл подкачки
echo  %Bold%[5]%Reset%  Максимум CPU и ОЗУ  (bcdedit)
echo  %Bold%[6]%Reset%  Температура и лимиты питания CPU
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto PowerMenu
if "%choice%"=="2" goto HiberMenu
if "%choice%"=="3" goto FastBootMenu
if "%choice%"=="4" goto PFMenu
if "%choice%"=="5" goto CPUMaxMenu
if "%choice%"=="6" goto CpuThermalMenu
if "%choice%"=="0" goto MainMenu
goto PerfMenu

:PowerMenu
call :Hdr "ПЛАН ЭЛЕКТРОПИТАНИЯ"
echo  %Bold%[1]%Reset%  Высокая производительность
echo  %Bold%[2]%Reset%  Максимальная
echo  %Bold%[3]%Reset%  Сбалансированная
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto PerfMenu
if "%choice%"=="1" (
    echo.
    echo  %Yellow%Включение плана "Высокая производительность"...%Reset%
    powershell -NoProfile -Command "$schemes = powercfg /l | Where-Object { $_ -match '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c|High|\u0412\u044b\u0441\u043e\u043a\u0430\u044f' }; if (-not $schemes) { $new = powercfg -duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c; $guid = $new -replace '.*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}).*', '$1'; powercfg /setactive $guid } else { $keep = $schemes | Select-Object -First 1; $guid = $keep -replace '.*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}).*', '$1'; powercfg /setactive $guid; $remove = $schemes | Select-Object -Skip 1; if ($remove) { foreach ($s in $remove) { $delGuid = $s -replace '.*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}).*', '$1'; powercfg /delete $delGuid } } ; if ($LASTEXITCODE -eq 0) { exit 0 } else { exit 1 } }" >nul 2>&1
    set "PWR_RC=!errorlevel!"
    if "!PWR_RC!"=="0" (call :Ok "План установлен") else (call :Err "План не установлен (код !PWR_RC!)")
)
if "%choice%"=="2" (
    echo.
    echo  %Yellow%Включение плана "Максимальная производительность"...%Reset%
    powershell -NoProfile -Command "$schemes = powercfg /l | Where-Object { $_ -match 'e9a42b02-d5df-448d-aa00-03f14749eb61|Ultimate|\u041c\u0430\u043a\u0441\u0438\u043c\u0430\u043b\u044c\u043d\u0430\u044f' }; if (-not $schemes) { $new = powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61; $guid = $new -replace '.*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}).*', '$1'; powercfg /setactive $guid } else { $keep = $schemes | Select-Object -First 1; $guid = $keep -replace '.*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}).*', '$1'; powercfg /setactive $guid; $remove = $schemes | Select-Object -Skip 1; if ($remove) { foreach ($s in $remove) { $delGuid = $s -replace '.*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}).*', '$1'; powercfg /delete $delGuid } } ; if ($LASTEXITCODE -eq 0) { exit 0 } else { exit 1 } }" >nul 2>&1
    set "PWR_RC=!errorlevel!"
    if "!PWR_RC!"=="0" (call :Ok "План установлен") else (call :Err "План не установлен (код !PWR_RC!)")
)
if "%choice%"=="3" (
    echo.
    echo  %Yellow%Включение плана "Сбалансированный"...%Reset%
    powershell -NoProfile -Command "$schemes = powercfg /l | Where-Object { $_ -match '381b4222-f694-41f0-9685-ff5bb260df2e|Balanced|\u0421\u0431\u0430\u043b\u0430\u043d\u0441\u0438\u0440\u043e\u0432\u0430\u043d\u043d\u0430\u044f' }; if (-not $schemes) { $new = powercfg -duplicatescheme 381b4222-f694-41f0-9685-ff5bb260df2e; $guid = $new -replace '.*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}).*', '$1'; powercfg /setactive $guid } else { $keep = $schemes | Select-Object -First 1; $guid = $keep -replace '.*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}).*', '$1'; powercfg /setactive $guid; $remove = $schemes | Select-Object -Skip 1; if ($remove) { foreach ($s in $remove) { $delGuid = $s -replace '.*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}).*', '$1'; powercfg /delete $delGuid } } ; if ($LASTEXITCODE -eq 0) { exit 0 } else { exit 1 } }" >nul 2>&1
    set "PWR_RC=!errorlevel!"
    if "!PWR_RC!"=="0" (call :Ok "План установлен") else (call :Err "План не установлен (код !PWR_RC!)")
)
call :PauseBack
goto PowerMenu

:HiberMenu
call :Hdr "ГИБЕРНАЦИЯ"
echo  %Bold%[1]%Reset%  Выкл
echo  %Bold%[2]%Reset%  Вкл
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto PerfMenu
if "%choice%"=="1" (
    powercfg /h off >nul 2>&1
    set "RC=!errorlevel!"
    if "!RC!"=="0" (echo. & call :Ok "Гибернация отключена") else (echo. & call :Err "Не удалось отключить гибернацию (код !RC!)")
)
if "%choice%"=="2" (
    powercfg /h on >nul 2>&1
    set "RC=!errorlevel!"
    if "!RC!"=="0" (echo. & call :Ok "Гибернация включена") else (echo. & call :Err "Не удалось включить гибернацию (код !RC!)")
)
call :PauseBack
goto HiberMenu

:FastBootMenu
call :Hdr "БЫСТРЫЙ ЗАПУСК"
echo  %Bold%[1]%Reset%  Выкл
echo  %Bold%[2]%Reset%  Вкл
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto PerfMenu
if "%choice%"=="1" (
    reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power" /v HiberbootEnabled /t REG_DWORD /d 0 /f >nul 2>&1
    set "RC=!errorlevel!"
    if "!RC!"=="0" (echo. & call :Ok "Быстрый запуск отключён") else (echo. & call :Err "Не удалось отключить быстрый запуск (код !RC!)")
)
if "%choice%"=="2" (
    reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power" /v HiberbootEnabled /t REG_DWORD /d 1 /f >nul 2>&1
    set "RC=!errorlevel!"
    if "!RC!"=="0" (echo. & call :Ok "Быстрый запуск включён") else (echo. & call :Err "Не удалось включить быстрый запуск (код !RC!)")
)
call :PauseBack
goto FastBootMenu

:PFMenu
call :Hdr "ФАЙЛ ПОДКАЧКИ"
echo  %Bold%[1]%Reset%  4 ГБ ОЗУ   -^>  6144 МБ  %Gray%(6 ГБ)%Reset%
echo  %Bold%[2]%Reset%  8 ГБ ОЗУ   -^>  8192 МБ  %Gray%(8 ГБ)%Reset%
echo  %Bold%[3]%Reset%  16 ГБ ОЗУ  -^>  4096 МБ  %Gray%(4 ГБ)%Reset%
echo  %Bold%[4]%Reset%  32 ГБ ОЗУ  -^>  2048 МБ  %Gray%(2 ГБ)%Reset%
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[5]%Reset%  Показать текущие настройки
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" call :PFApply 6144
if "%choice%"=="2" call :PFApply 8192
if "%choice%"=="3" call :PFApply 4096
if "%choice%"=="4" call :PFApply 2048
if "%choice%"=="5" goto PFShow
if "%choice%"=="0" goto PerfMenu
goto PFMenu

:PFShow
call :Hdr "ТЕКУЩИЕ НАСТРОЙКИ ПОДКАЧКИ"
powershell -NoProfile -Command "try { $pf = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management' -ErrorAction Stop).PagingFiles; Write-Host ('  PagingFiles : ' + $pf) } catch { Write-Host '  PagingFiles : (не задано)' }; $auto = (Get-CimInstance Win32_ComputerSystem).AutomaticManagedPagefile; Write-Host ('  Automatic   : ' + $auto)"
echo.
echo  %Yellow%Изменения вступают в силу после перезагрузки.%Reset%
call :PauseBack
goto PFMenu

:PFApply
set "Size=%~1"
set "PS1=%TEMP%\setpf_%RANDOM%.ps1"
call :Hdr "УСТАНОВКА ФАЙЛА ПОДКАЧКИ"
echo  %Yellow%Размер: %Size% МБ%Reset%
echo.
echo  Подождите...
echo.
> "%PS1%" echo $ErrorActionPreference = 'Stop'
>> "%PS1%" echo try {
>> "%PS1%" echo   $size = %Size%
>> "%PS1%" echo   $cs = Get-CimInstance -ClassName Win32_ComputerSystem
>> "%PS1%" echo   if ($cs.AutomaticManagedPagefile) {
>> "%PS1%" echo     $cs.AutomaticManagedPagefile = $false
>> "%PS1%" echo     Set-CimInstance -InputObject $cs
>> "%PS1%" echo     Write-Host '  [1/3] Автоматическое управление отключено' -ForegroundColor Green
>> "%PS1%" echo   } else {
>> "%PS1%" echo     Write-Host '  [1/3] Автоматическое управление уже отключено' -ForegroundColor Green
>> "%PS1%" echo   }
>> "%PS1%" echo   $regPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
>> "%PS1%" echo   $value = "C:\pagefile.sys $size $size"
>> "%PS1%" echo   Set-ItemProperty -Path $regPath -Name 'PagingFiles' -Value $value -Type MultiString
>> "%PS1%" echo   Write-Host "  [2/3] Размер установлен: $size МБ" -ForegroundColor Green
>> "%PS1%" echo   try { Set-ItemProperty -Path $regPath -Name 'ExistingPageFiles' -Value @() -Type MultiString -ErrorAction SilentlyContinue } catch {}
>> "%PS1%" echo   Write-Host '  [3/3] Готово' -ForegroundColor Green
>> "%PS1%" echo   Write-Host ''
>> "%PS1%" echo   Write-Host '  УСПЕШНО. Перезагрузите компьютер.' -ForegroundColor Yellow
>> "%PS1%" echo } catch {
>> "%PS1%" echo   Write-Host ('  ОШИБКА: ' + $_.Exception.Message) -ForegroundColor Red
>> "%PS1%" echo   exit 1
>> "%PS1%" echo }
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
set "ERR=!errorlevel!"
del "%PS1%" >nul 2>&1
if !ERR! NEQ 0 (
    echo.
    call :Err "Что-то пошло не так."
)
call :PauseBack
exit /b


:CPUMaxMenu
call :Hdr "ОГРАНИЧЕНИЯ CPU И ОЗУ"
echo  %Bold%[1]%Reset%  Убрать ограничения numproc / truncatememory
echo  %Bold%[2]%Reset%  Показать текущие параметры BCD
echo  %Bold%[3]%Reset%  Повторить сброс ограничений
echo  %Gray%Отсутствие этих параметров = Windows не ограничивает число CPU/объём ОЗУ через них.%Reset%
echo.
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto CPUMaxApply
if "%choice%"=="2" goto CPUMaxShow
if "%choice%"=="3" goto CPUMaxReset
if "%choice%"=="0" goto PerfMenu
goto CPUMaxMenu

:CPUMaxApply
call :Hdr "УБРАТЬ ОГРАНИЧЕНИЯ CPU/ОЗУ"
call :CPUMaxClearLimits
if errorlevel 1 (call :Err "BCD: не удалось полностью снять ограничения") else (call :Ok "BCD: numproc/truncatememory не заданы")
call :PauseBack
goto CPUMaxMenu

:CPUMaxShow
call :Hdr "ТЕКУЩИЕ ПАРАМЕТРЫ BCD"
set "BCDFOUND=0"
for /f "delims=" %%l in ('bcdedit /enum {current} 2^>nul ^| findstr /i /r /c:"^[ ]*numproc[ ]" /c:"^[ ]*truncatememory[ ]"') do (
    echo    %%l
    set "BCDFOUND=1"
)
if "!BCDFOUND!"=="0" echo  %Green%numproc и truncatememory отсутствуют.%Reset%
call :PauseBack
goto CPUMaxMenu

:CPUMaxReset
call :CPUMaxClearLimits
if errorlevel 1 call :Err "BCD: сброс не подтверждён" else call :Ok "BCD: повторная проверка подтверждает отсутствие ограничений"
call :PauseBack
goto CPUMaxMenu

:CPUMaxClearLimits
set "BCDFAIL=0"
set "BCDNUM=0"
set "BCDRAM=0"
for /f "tokens=1,2" %%A in ('bcdedit /enum {current} 2^>nul ^| findstr /i /r /c:"^[ ]*numproc[ ]" /c:"^[ ]*truncatememory[ ]"') do (
    if /i "%%A"=="numproc" set "BCDNUM=1"
    if /i "%%A"=="truncatememory" set "BCDRAM=1"
)
if "!BCDNUM!"=="1" (
    bcdedit /deletevalue {current} numproc >nul 2>&1
    set "RC=!errorlevel!"
    call :Log "BCD | delete numproc | rc=!RC!"
    if not "!RC!"=="0" set "BCDFAIL=1"
) else call :Log "BCD | numproc absent | no-op"
if "!BCDRAM!"=="1" (
    bcdedit /deletevalue {current} truncatememory >nul 2>&1
    set "RC=!errorlevel!"
    call :Log "BCD | delete truncatememory | rc=!RC!"
    if not "!RC!"=="0" set "BCDFAIL=1"
) else call :Log "BCD | truncatememory absent | no-op"
set "BCDNUM2=0"
set "BCDRAM2=0"
for /f "tokens=1,2" %%A in ('bcdedit /enum {current} 2^>nul ^| findstr /i /r /c:"^[ ]*numproc[ ]" /c:"^[ ]*truncatememory[ ]"') do (
    if /i "%%A"=="numproc" set "BCDNUM2=1"
    if /i "%%A"=="truncatememory" set "BCDRAM2=1"
)
if "!BCDNUM2!"=="1" set "BCDFAIL=1"
if "!BCDRAM2!"=="1" set "BCDFAIL=1"
exit /b !BCDFAIL!

:CpuThermalMenu
call :Hdr "ТЕМПЕРАТУРА И ЛИМИТЫ ПИТАНИЯ CPU"
echo  %Yellow%Читаю текущие параметры процессора...%Reset%
echo.
echo  %Bold%Лимиты питания процессора (powercfg, активная схема):%Reset%
set "PWRQUERY=%TEMP%\SCU_cpu_limits.txt"
powercfg /query SCHEME_CURRENT SUB_PROCESSOR >"%PWRQUERY%" 2>nul
set "QRYRC=!errorlevel!"
call :Log "CPU  | powercfg query SUB_PROCESSOR | rc=!QRYRC!"
if not "!QRYRC!"=="0" (
    call :Warn "powercfg не вернул параметры питания CPU (код !QRYRC!)."
) else (
    findstr /i /c:"GUID параметра" /c:"Power Setting GUID" /c:"Текущий параметр" /c:"Current " "!PWRQUERY!" 2>nul
)
echo.
echo  %Bold%Температура (ACPI thermal zone, если датчик доступен):%Reset%
powershell -NoProfile -Command "try { $z = @(Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction Stop); if ($z.Count -eq 0) { '  (датчики не найдены)'; exit 0 }; foreach ($x in $z) { Write-Host ('  ' + $x.InstanceName + ': ' + ('{0:N1}' -f (($x.CurrentTemperature / 10) - 273.15)) + ' C') }; exit 0 } catch { '  (датчик температуры недоступен)'; exit 0 }"
echo.
echo  %Gray%Раздел только читает параметры; изменения не выполняются.%Reset%
if exist "!PWRQUERY!" del /f /q "!PWRQUERY!" >nul 2>&1
call :PauseBack
goto PerfMenu

:NetMenu
call :RequireAdmin
if errorlevel 1 goto MainMenu
call :Hdr "СЕТЕВЫЕ ПАРАМЕТРЫ"
echo  Сначала просматриваются текущие значения; профиль не гарантирует снижение пинга.
echo  %Bold%[1]%Reset%  Показать текущие TCP/IP и QoS
echo  %Bold%[2]%Reset%  TCP Auto-Tuning
echo  %Bold%[3]%Reset%  ECN
echo  %Bold%[4]%Reset%  MTU
echo  %Bold%[5]%Reset%  QoS-политика
echo  %Bold%[6]%Reset%  NetBIOS over TCP/IP
echo.
echo  %Bold%[7]%Reset%  Профиль для игр (Auto-Tuning/ECN/QoS)
echo  %Bold%[8]%Reset%  Сброс изменяемых параметров к сохранённым исходным
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto NetShow
if "%choice%"=="2" goto NetAuto
if "%choice%"=="3" goto NetECN
if "%choice%"=="4" goto NetMTU
if "%choice%"=="5" goto NetQoS
if "%choice%"=="6" goto NetBIOS
if "%choice%"=="7" goto NetGaming
if "%choice%"=="8" goto NetReset
if "%choice%"=="0" goto MainMenu
goto NetMenu

:NetShow
call :Hdr "ТЕКУЩИЕ НАСТРОЙКИ"
echo  %Bold%TCP Global:%Reset%
netsh int tcp show global
echo.
echo  %Bold%MTU интерфейсов:%Reset%
netsh interface ipv4 show subinterfaces
echo.
echo  %Bold%QoS override:%Reset%
reg query "HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched" /v NonBestEffortLimit 2>nul
if errorlevel 1 echo   (override отсутствует; действует политика Windows/GPO)
call :PauseBack
goto NetMenu

:NetAuto
call :Hdr "TCP AUTO-TUNING"
netsh int tcp show global | findstr /i "Receive Window Auto-Tuning"
echo.
echo  %Bold%[1]%Reset%  normal
echo  %Bold%[2]%Reset%  disabled
echo  %Bold%[3]%Reset%  highlyrestricted
echo  %Bold%[4]%Reset%  restricted
echo  %Bold%[5]%Reset%  experimental
echo  %Bold%[D]%Reset%  default/normal
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto NetMenu
set "NETAUTO="
if /i "%choice%"=="1" set "NETAUTO=normal"
if /i "%choice%"=="2" set "NETAUTO=disabled"
if /i "%choice%"=="3" set "NETAUTO=highlyrestricted"
if /i "%choice%"=="4" set "NETAUTO=restricted"
if /i "%choice%"=="5" set "NETAUTO=experimental"
if /i "%choice%"=="D" set "NETAUTO=normal"
if not defined NETAUTO goto NetMenu
call :NetTCPGlobalSave
if errorlevel 1 (call :Err "Не удалось сохранить исходные TCP Global параметры — изменение отменено" & goto NetMenu)
netsh int tcp set global autotuninglevel=!NETAUTO! >nul 2>&1
set "RC=!errorlevel!"
call :Log "NET | Auto-Tuning=!NETAUTO! | rc=!RC!"
if "!RC!"=="0" call :Ok "Auto-Tuning = !NETAUTO!" else call :Err "Auto-Tuning: код !RC!"
call :PauseBack
goto NetMenu

:NetECN
call :Hdr "ECN"
netsh int tcp show global | findstr /i "ECN Capability"
echo.
echo  %Bold%[1]%Reset%  enabled
echo  %Bold%[2]%Reset%  disabled
echo  %Bold%[D]%Reset%  default
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto NetMenu
set "NETECN="
if /i "%choice%"=="1" set "NETECN=enabled"
if /i "%choice%"=="2" set "NETECN=disabled"
if /i "%choice%"=="D" set "NETECN=default"
if not defined NETECN goto NetMenu
call :NetTCPGlobalSave
if errorlevel 1 (call :Err "Не удалось сохранить исходные TCP Global параметры — изменение отменено" & goto NetMenu)
netsh int tcp set global ecncapability=!NETECN! >nul 2>&1
set "RC=!errorlevel!"
call :Log "NET | ECN=!NETECN! | rc=!RC!"
if "!RC!"=="0" call :Ok "ECN = !NETECN!" else call :Err "ECN: код !RC!"
call :PauseBack
goto NetMenu

:NetMTU
call :Hdr "MTU"
echo  Текущие интерфейсы:
netsh interface ipv4 show subinterfaces
echo.
echo  %Bold%[1]%Reset%  MTU 1500
echo  %Bold%[2]%Reset%  MTU 1472
echo  %Bold%[3]%Reset%  MTU 1400
echo  %Bold%[4]%Reset%  Задать своё значение (576..1500)
echo  %Bold%[R]%Reset%  Восстановить сохранённые MTU
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto NetMenu
if /i "%choice%"=="R" (call :NetMTURestore & call :PauseBack & goto NetMenu)
set "MTUVAL="
if "%choice%"=="1" set "MTUVAL=1500"
if "%choice%"=="2" set "MTUVAL=1472"
if "%choice%"=="3" set "MTUVAL=1400"
if "%choice%"=="4" set /p "MTUVAL=  MTU: "
if not defined MTUVAL goto NetMenu
set /a MTUN=MTUVAL+0 >nul 2>&1
if !MTUN! LSS 576 (call :Err "MTU меньше 576 не принят" & goto NetMenu)
if !MTUN! GTR 1500 (call :Err "MTU больше 1500 не принят этим пунктом" & goto NetMenu)
set "IFACE="
echo.
echo  %Yellow%Введите имя интерфейса ровно как в списке.%Reset%
set /p "IFACE=  Интерфейс: "
if not defined IFACE goto NetMenu
set "NET_IFACE=!IFACE!"
call :NetMTUSave
if errorlevel 1 (call :Err "Не удалось сохранить исходный MTU — изменение отменено" & goto NetMenu)
netsh interface ipv4 set subinterface "!IFACE!" mtu=!MTUVAL! store=persistent >nul 2>&1
set "RC=!errorlevel!"
call :Log "NET | MTU !IFACE!=!MTUVAL! | rc=!RC!"
if "!RC!"=="0" call :Ok "MTU !MTUVAL! установлен для !IFACE!" else call :Err "MTU: код !RC!"
call :PauseBack
goto NetMenu

:NetTCPGlobalSave
if exist "%NET_TCP_BAK%" exit /b 0
powershell -NoProfile -Command "$file=$env:NET_TCP_BAK;try{$lines=@(netsh int tcp show global);$auto='';$ecn='';foreach($l in $lines){$s=[string]$l;if($s -match '(?i)(?:Auto.?Tuning|автонастр\w*|автоматичес\w*).{0,100}:\s*(disabled|highlyrestricted|restricted|normal|experimental)\s*$'){$auto=$Matches[1]};if($s -match '(?i)ECN[^:]*:\s*(enabled|disabled|default)\s*$'){$ecn=$Matches[1]}};if(-not $auto -or -not $ecn){throw 'Не удалось разобрать TCP Global параметры из netsh'};Set-Content -LiteralPath $file -Value @('autotuning='+$auto,'ecn='+$ecn) -Encoding UTF8 -ErrorAction Stop;exit 0}catch{exit 1}"
set "RC=!errorlevel!"
call :Log "NET-BACKUP | TCP Global | rc=!RC! | file=%NET_TCP_BAK%"
exit /b !RC!

:NetTCPGlobalRestore
if not exist "%NET_TCP_BAK%" (call :Log "NET-RESTORE | TCP Global | no backup" & exit /b 0)
set "NET_TCP_AUTO="
set "NET_TCP_ECN="
for /f "usebackq tokens=1,* delims==" %%A in ("%NET_TCP_BAK%") do (
    if /i "%%A"=="autotuning" set "NET_TCP_AUTO=%%B"
    if /i "%%A"=="ecn" set "NET_TCP_ECN=%%B"
)
if not defined NET_TCP_AUTO if not defined NET_TCP_ECN (
    call :Err "TCP Global backup повреждён"
    exit /b 1
)
set "RC=0"
if defined NET_TCP_AUTO (
    netsh int tcp set global autotuninglevel=!NET_TCP_AUTO! >nul 2>&1
    set "RCA=!errorlevel!"
    call :Log "NET-RESTORE | Auto-Tuning=!NET_TCP_AUTO! | rc=!RCA!"
    if not "!RCA!"=="0" set "RC=1"
)
if defined NET_TCP_ECN (
    netsh int tcp set global ecncapability=!NET_TCP_ECN! >nul 2>&1
    set "RCE=!errorlevel!"
    call :Log "NET-RESTORE | ECN=!NET_TCP_ECN! | rc=!RCE!"
    if not "!RCE!"=="0" set "RC=1"
)
if not "!RC!"=="0" (
    call :Err "TCP Global параметры не восстановлены; backup сохранён"
    exit /b 1
)
powershell -NoProfile -Command "$file=$env:NET_TCP_BAK;try{$lines=@(netsh int tcp show global);$auto='';$ecn='';foreach($l in $lines){$s=[string]$l;if($s -match '(?i)(?:Auto.?Tuning|автонастр\w*|автоматичес\w*).{0,100}:\s*(disabled|highlyrestricted|restricted|normal|experimental)\s*$'){$auto=$Matches[1]};if($s -match '(?i)ECN[^:]*:\s*(enabled|disabled|default)\s*$'){$ecn=$Matches[1]}};$rows=@(Get-Content -LiteralPath $file -Encoding UTF8);$ba=($rows|Where-Object{$_ -like 'autotuning=*'}|Select-Object -First 1);$be=($rows|Where-Object{$_ -like 'ecn=*'}|Select-Object -First 1);if(-not $ba -or -not $be){exit 1};$ba=$ba.Substring(11);$be=$be.Substring(4);if($auto -ne $ba -or $ecn -ne $be){exit 1};exit 0}catch{exit 1}"
set "RC=!errorlevel!"
call :Log "NET-RESTORE | TCP Global verify | rc=!RC!"
if "!RC!"=="0" (del /f /q "%NET_TCP_BAK%" >nul 2>&1 & exit /b 0)
call :Err "TCP Global параметры не подтверждены после восстановления; backup сохранён"
exit /b 1

:NetBIOSSave
if exist "%NET_NB_BAK%" exit /b 0
powershell -NoProfile -Command "$file=$env:NET_NB_BAK;try{$rows=@();$ifs=@(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=TRUE' -ErrorAction Stop);if($ifs.Count -eq 0){throw 'активные IPv4-интерфейсы не найдены'};foreach($i in $ifs){$rows += ([string]$i.Index+'`t'+[string]$i.TcpipNetbiosOptions)};Set-Content -LiteralPath $file -Value $rows -Encoding UTF8 -ErrorAction Stop;exit 0}catch{exit 1}"
set "RC=!errorlevel!"
call :Log "NET-BACKUP | NetBIOS | rc=!RC! | file=%NET_NB_BAK%"
exit /b !RC!

:NetBIOSRestore
if not exist "%NET_NB_BAK%" (call :Log "NET-RESTORE | NetBIOS | no backup" & exit /b 0)
powershell -NoProfile -Command "$file=$env:NET_NB_BAK;try{$ifs=@(Get-CimInstance Win32_NetworkAdapterConfiguration -ErrorAction Stop);$bad=0;foreach($r in @(Get-Content -LiteralPath $file -Encoding UTF8)){if($r -notmatch '^(\d+)\t([012])$'){$bad++;continue};$idx=[int]$Matches[1];$mode=[int]$Matches[2];$i=$ifs|Where-Object{$_.Index -eq $idx}|Select-Object -First 1;if(-not $i){$bad++;continue};$ret=$i.SetTcpipNetbios($mode);if($ret.ReturnValue -ne 0){$bad++;continue};$check=(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter ('Index='+$idx) -ErrorAction Stop).TcpipNetbiosOptions;if($check -ne $mode){$bad++}};if($bad -gt 0){exit 1}else{exit 0}}catch{exit 1}"
set "RC=!errorlevel!"
call :Log "NET-RESTORE | NetBIOS | rc=!RC!"
if "!RC!"=="0" (del /f /q "%NET_NB_BAK%" >nul 2>&1 & exit /b 0)
call :Err "NetBIOS не восстановлен; backup сохранён"
exit /b 1

:NetQoS
call :Hdr "QoS-ПОЛИТИКА"
echo  Текущее значение:
reg query "HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched" /v NonBestEffortLimit 2>nul
if errorlevel 1 echo   (override отсутствует)
echo.
echo  %Bold%[1]%Reset%  Сбросить override (удалить значение)
echo  %Bold%[2]%Reset%  Установить 0%%
echo  %Bold%[3]%Reset%  Установить 20%%
echo  %Bold%[R]%Reset%  Восстановить исходное сохранённое значение
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto NetMenu
if /i "%choice%"=="R" (call :NetQoSRestore & call :PauseBack & goto NetMenu)
set "QOSVAL="
if "%choice%"=="1" set "QOSVAL=DELETE"
if "%choice%"=="2" set "QOSVAL=0"
if "%choice%"=="3" set "QOSVAL=20"
if not defined QOSVAL goto NetMenu
call :NetQoSSave
if errorlevel 1 (call :Err "Не удалось сохранить исходный QoS — изменение отменено" & goto NetMenu)
if /i "!QOSVAL!"=="DELETE" (reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched" /v NonBestEffortLimit /f >nul 2>&1) else (reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched" /v NonBestEffortLimit /t REG_DWORD /d !QOSVAL! /f >nul 2>&1)
set "RC=!errorlevel!"
call :Log "NET | QoS=!QOSVAL! | rc=!RC!"
if "!RC!"=="0" call :Ok "QoS override изменён" else call :Err "QoS: код !RC!"
call :PauseBack
goto NetMenu

:NetBIOS
call :Hdr "NetBIOS over TCP/IP"
echo  Изменение применяется ко всем активным IPv4-интерфейсам и проверяет ReturnValue.
echo  %Bold%[1]%Reset%  Отключить (2)
echo  %Bold%[2]%Reset%  Включить через DHCP (0)
echo  %Bold%[3]%Reset%  Сбросить кэш (nbtstat -R / -RR)
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto NetMenu
if "%choice%"=="3" (
    nbtstat -R >nul 2>&1
    set "RC1=!errorlevel!"
    nbtstat -RR >nul 2>&1
    set "RC2=!errorlevel!"
    call :Log "NET | nbtstat cache reset | rcR=!RC1! rcRR=!RC2!"
    if "!RC1!"=="0" if "!RC2!"=="0" (call :Ok "Кэш NetBIOS сброшен") else call :Err "Сброс кэша NetBIOS не полностью выполнен"
    call :PauseBack
    goto NetMenu
)
set "NBMODE="
if "%choice%"=="1" set "NBMODE=2"
if "%choice%"=="2" set "NBMODE=0"
if not defined NBMODE goto NetMenu
set "NETNBMODE=!NBMODE!"
call :NetBIOSSave
if errorlevel 1 (call :Err "Не удалось сохранить исходный NetBIOS — изменение отменено" & goto NetMenu)
powershell -NoProfile -Command "$mode=[int]$env:NETNBMODE;$bad=0;$n=0;foreach($i in @(Get-CimInstance Win32_NetworkAdapterConfiguration -ErrorAction Stop)){if($i.IPEnabled){$n++;$r=$i.SetTcpipNetbios($mode);if($r.ReturnValue -ne 0){$bad++}}};if($n -eq 0 -or $bad -gt 0){exit 1};exit 0"
set "RC=!errorlevel!"
call :Log "NET | NetBIOS mode=!NBMODE! | rc=!RC!"
if "!RC!"=="0" call :Ok "NetBIOS mode !NBMODE! применён ко всем активным интерфейсам" else call :Err "NetBIOS: один или несколько интерфейсов не изменены"
call :PauseBack
goto NetMenu

:NetGaming
call :Hdr "ПРОФИЛЬ ДЛЯ ИГР"
echo  Этот профиль отключает Auto-Tuning, отключает ECN и задаёт QoS override=0%%.
echo  Он не гарантирует снижение задержки или повышение скорости.
set /p "conf=  Продолжить? y/n: "
if /i not "%conf%"=="y" goto NetMenu
call :NetTCPGlobalSave
if errorlevel 1 (call :Err "Не удалось сохранить исходные TCP Global параметры — профиль отменён" & goto NetMenu)
call :NetQoSSave
if errorlevel 1 (call :Err "Не удалось сохранить QoS — профиль отменён" & goto NetMenu)
set "BFAIL=0"
netsh int tcp set global autotuninglevel=disabled >nul 2>&1
set "RC=!errorlevel!"
call :Log "NET-PROFILE | Auto-Tuning=disabled | rc=!RC!"
if not "!RC!"=="0" set "BFAIL=1"
netsh int tcp set global ecncapability=disabled >nul 2>&1
set "RC=!errorlevel!"
call :Log "NET-PROFILE | ECN=disabled | rc=!RC!"
if not "!RC!"=="0" set "BFAIL=1"
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched" /v NonBestEffortLimit /t REG_DWORD /d 0 /f >nul 2>&1
set "RC=!errorlevel!"
call :Log "NET-PROFILE | QoS=0 | rc=!RC!"
if not "!RC!"=="0" set "BFAIL=1"
if "!BFAIL!"=="0" call :Ok "Профиль применён; проверка/сброс доступны в меню" else call :Err "Профиль применён частично — см. лог"
call :PauseBack
goto NetMenu

:NetReset
call :Hdr "СБРОС СЕТЕВЫХ ПАРАМЕТРОВ"
set /p "conf=  Сбросить изменяемые этим разделом параметры к сохранённым? y/n: "
if /i not "%conf%"=="y" goto NetMenu
set "NETFAIL=0"
call :NetTCPGlobalRestore
if errorlevel 1 set "NETFAIL=1"
call :NetQoSRestore
if errorlevel 1 set "NETFAIL=1"
call :NetMTURestore
if errorlevel 1 set "NETFAIL=1"
call :NetBIOSRestore
if errorlevel 1 set "NETFAIL=1"
netsh int tcp show global | findstr /i "Receive Window Auto-Tuning ECN Capability"
if "!NETFAIL!"=="0" (
    call :Ok "Сброс завершён: сохранённые TCP Global, QoS, MTU и NetBIOS восстановлены"
) else (
    call :Err "Сброс завершён не полностью — см. лог/backup"
)
call :PauseBack
goto NetMenu

:NetQoSSave
if exist "%NET_QOS_BAK%" exit /b 0
powershell -NoProfile -Command "$path='HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched';$file=$env:NET_QOS_BAK;try{$o=Get-ItemProperty -LiteralPath $path -Name NonBestEffortLimit -ErrorAction SilentlyContinue;$out=@();if($o -and ($o.PSObject.Properties.Name -contains 'NonBestEffortLimit')){$out+='present=1';$out+=('value='+[string]$o.NonBestEffortLimit)}else{$out+='present=0'};Set-Content -LiteralPath $file -Value $out -Encoding UTF8 -ErrorAction Stop;exit 0}catch{exit 1}"
set "RC=!errorlevel!"
call :Log "NET-BACKUP | QoS | rc=!RC! | file=%NET_QOS_BAK%"
exit /b !RC!

:NetQoSRestore
if not exist "%NET_QOS_BAK%" (call :Log "NET-RESTORE | QoS | no backup" & exit /b 0)
set "NET_QOS_PRESENT="
set "NET_QOS_VALUE="
for /f "usebackq tokens=1,* delims==" %%A in ("%NET_QOS_BAK%") do (
    if /i "%%A"=="present" set "NET_QOS_PRESENT=%%B"
    if /i "%%A"=="value" set "NET_QOS_VALUE=%%B"
)
if "!NET_QOS_PRESENT!"=="1" (
    reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched" /v NonBestEffortLimit /t REG_DWORD /d !NET_QOS_VALUE! /f >nul 2>&1
    set "RC=!errorlevel!"
) else (
    reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched" /v NonBestEffortLimit /f >nul 2>&1
    set "RCD=!errorlevel!"
    reg query "HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched" /v NonBestEffortLimit >nul 2>&1
    set "RCQ=!errorlevel!"
    if "!RCQ!"=="1" (set "RC=0") else set "RC=1"
)
call :Log "NET-RESTORE | QoS | rc=!RC!"
if "!RC!"=="0" (del /f /q "%NET_QOS_BAK%" >nul 2>&1 & exit /b 0)
call :Err "QoS не восстановлен; backup сохранён"
exit /b 1

:NetMTUSave
powershell -NoProfile -Command "$file=$env:NET_MTU_BAK;$name=$env:NET_IFACE;try{if(-not $name){exit 2};$ifs=@(Get-NetIPInterface -AddressFamily IPv4 -ErrorAction Stop);$hit=$null;foreach($i in $ifs){if($i.InterfaceAlias -eq $name){$hit=$i;break}};if(-not $hit){exit 3};$line=$name+'`t'+[string]$hit.NlMtu;$rows=@();if(Test-Path -LiteralPath $file){$rows=@(Get-Content -LiteralPath $file -Encoding UTF8)};$exists=$false;foreach($r in $rows){if($r -eq $line){$exists=$true}};if(-not $exists){Add-Content -LiteralPath $file -Value $line -Encoding UTF8 -ErrorAction Stop};exit 0}catch{exit 1}"
set "RC=!errorlevel!"
call :Log "NET-BACKUP | MTU !NET_IFACE! | rc=!RC! | file=%NET_MTU_BAK%"
exit /b !RC!

:NetMTURestore
if not exist "%NET_MTU_BAK%" (call :Log "NET-RESTORE | MTU | no backup" & exit /b 0)
powershell -NoProfile -Command "$file=$env:NET_MTU_BAK;try{$rows=@(Get-Content -LiteralPath $file -Encoding UTF8);$bad=0;foreach($r in $rows){$parts=$r -split "`t",2;if($parts.Count -ne 2){$bad++;continue};try{Set-NetIPInterface -InterfaceAlias $parts[0] -AddressFamily IPv4 -NlMtu ([int]$parts[1]) -ErrorAction Stop}catch{$bad++}};if($bad -gt 0){exit 1}else{exit 0}}catch{exit 1}"
set "RC=!errorlevel!"
call :Log "NET-RESTORE | MTU | rc=!RC!"
if "!RC!"=="0" (del /f /q "%NET_MTU_BAK%" >nul 2>&1 & exit /b 0)
call :Err "MTU не восстановлен; backup сохранён"
exit /b 1

:UIMenu
call :Hdr "ИНТЕРФЕЙС И ПРОВОДНИК"
echo  %Bold%[1]%Reset%  Настройка проводника
echo  %Bold%[2]%Reset%  Визуальные эффекты
echo  %Bold%[3]%Reset%  Задержка меню
echo  %Bold%[4]%Reset%  Сжатие обоев
echo  %Bold%[5]%Reset%  Раздел «Рекомендуем» в меню Пуск
echo  %Bold%[6]%Reset%  Очистить панель задач
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto ExpMenu
if "%choice%"=="2" goto FXMenu
if "%choice%"=="3" goto DelayMenu
if "%choice%"=="4" goto WallMenu
if "%choice%"=="5" goto RecMenu
if "%choice%"=="6" goto TaskbarClean
if "%choice%"=="0" goto MainMenu
goto UIMenu

:ExpMenu
call :Hdr "НАСТРОЙКА ПРОВОДНИКА"
call :ExpStatus
if "!_OpenLoc!"=="1" (set "s=%Green%Этот компьютер%Reset%") else (set "s=%Red%Главная%Reset%")
echo  %Bold%[1]%Reset%  Открывать проводник      : !s!
if "!_HideHome!"=="0" (set "s=%Green%Скрыта%Reset%") else (set "s=%Red%Видна%Reset%")
echo  %Bold%[2]%Reset%  Кнопка «Главная»         : !s!
if "!_HideGallery!"=="0" (set "s=%Green%Скрыта%Reset%") else (set "s=%Red%Видна%Reset%")
echo  %Bold%[3]%Reset%  Кнопка «Галерея»         : !s!
if "!_HideNetwork!"=="0" (set "s=%Green%Скрыта%Reset%") else (set "s=%Red%Видна%Reset%")
echo  %Bold%[4]%Reset%  Кнопка «Сеть»            : !s!
if "!_ShowRecycle!"=="1" (set "s=%Green%Видна%Reset%") else (set "s=%Red%Скрыта%Reset%")
echo  %Bold%[5]%Reset%  Корзина (навигация)      : !s!
if "!_DeskRecycle!"=="0" (set "s=%Red%Видна%Reset%") else (set "s=%Green%Скрыта%Reset%")
echo  %Bold%[6]%Reset%  Корзина на рабочем столе : !s!
if "!_Compact!"=="1" (set "s=%Green%Вкл%Reset%") else (set "s=%Red%Выкл%Reset%")
echo  %Bold%[7]%Reset%  Компактный вид           : !s!
if "!_Privacy!"=="0" (set "s=%Green%Выкл%Reset%") else (set "s=%Red%Вкл%Reset%")
echo  %Bold%[8]%Reset%  Недавние файлы           : !s!
if "!_CtxMenu!"=="1" (set "s=%Green%Классическое%Reset%") else (set "s=%Red%Современное%Reset%")
echo  %Bold%[9]%Reset%  Контекстное меню         : !s!
echo  %Gray%--------------------------------------------------------------------%Reset%
if "!_Ext!"=="0" (set "s=%Green%Видны%Reset%") else (set "s=%Red%Скрыты%Reset%")
echo  %Bold%[E]%Reset%  Расширения файлов       : !s!
if "!_Hidden!"=="1" (set "s=%Green%Видны%Reset%") else (set "s=%Red%Скрыты%Reset%")
echo  %Bold%[H]%Reset%  Скрытые файлы           : !s!
if "!_FullPath!"=="1" (set "s=%Green%Вкл%Reset%") else (set "s=%Red%Выкл%Reset%")
echo  %Bold%[P]%Reset%  Полный путь в заголовке : !s!
if "!_CheckBoxes!"=="1" (set "s=%Green%Вкл%Reset%") else (set "s=%Red%Выкл%Reset%")
echo  %Bold%[C]%Reset%  Флажки элементов        : !s!
if "!_OneDrive!"=="0" (set "s=%Green%Скрыт%Reset%") else (set "s=%Red%Виден%Reset%")
echo  %Bold%[O]%Reset%  OneDrive в навиг.       : !s!
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[R]%Reset%  %Yellow%Перезапустить проводник%Reset%
echo  %Bold%[A]%Reset%  %Green%Применить всё%Reset%
echo  %Bold%[D]%Reset%  %Red%По умолчанию%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if /i "%choice%"=="0" goto UIMenu
if /i "%choice%"=="A" goto ExpApplyAll
if /i "%choice%"=="D" goto ExpDefaults
if /i "%choice%"=="R" goto ExpRestart
if "%choice%"=="1" goto ExpOpen
if "%choice%"=="2" goto ExpHome
if "%choice%"=="3" goto ExpGallery
if "%choice%"=="4" goto ExpNetwork
if "%choice%"=="5" goto ExpRecycle
if "%choice%"=="6" goto ExpDeskRecycle
if "%choice%"=="7" goto ExpCompact
if "%choice%"=="8" goto ExpPrivacy
if "%choice%"=="9" goto ExpCtx
if /i "%choice%"=="E" goto ExpExt
if /i "%choice%"=="H" goto ExpHidden
if /i "%choice%"=="P" goto ExpPath
if /i "%choice%"=="C" goto ExpCheck
if /i "%choice%"=="O" goto ExpOneDrive
goto ExpMenu

:ExpOpen
if "!_OpenLoc!"=="1" (reg add "%RegAdv%" /v LaunchTo /t REG_DWORD /d 2 /f >nul) else (reg add "%RegAdv%" /v LaunchTo /t REG_DWORD /d 1 /f >nul)
goto ExpMenu
:ExpHome
if "!_HideHome!"=="0" (reg add "HKCU\Software\Classes\CLSID\%ClsidHome%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 1 /f >nul) else (reg add "HKCU\Software\Classes\CLSID\%ClsidHome%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 0 /f >nul)
goto ExpMenu
:ExpGallery
if "!_HideGallery!"=="0" (reg add "HKCU\Software\Classes\CLSID\%ClsidGallery%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 1 /f >nul) else (reg add "HKCU\Software\Classes\CLSID\%ClsidGallery%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 0 /f >nul)
goto ExpMenu
:ExpNetwork
if "!_HideNetwork!"=="0" (reg add "HKCU\Software\Classes\CLSID\%ClsidNetwork%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 1 /f >nul) else (reg add "HKCU\Software\Classes\CLSID\%ClsidNetwork%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 0 /f >nul)
goto ExpMenu
:ExpRecycle
if "!_ShowRecycle!"=="1" (reg add "HKCU\Software\Classes\CLSID\%ClsidRecycle%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 0 /f >nul) else (reg add "HKCU\Software\Classes\CLSID\%ClsidRecycle%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 1 /f >nul)
goto ExpMenu
:ExpDeskRecycle
if "!_DeskRecycle!"=="0" (reg add "%RegDeskIcons%" /v "%ClsidRecycle%" /t REG_DWORD /d 1 /f >nul) else (reg delete "%RegDeskIcons%" /v "%ClsidRecycle%" /f >nul)
goto ExpMenu
:ExpCompact
if "!_Compact!"=="1" (reg add "%RegAdv%" /v UseCompactMode /t REG_DWORD /d 0 /f >nul) else (reg add "%RegAdv%" /v UseCompactMode /t REG_DWORD /d 1 /f >nul)
goto ExpMenu
:ExpPrivacy
if "!_Privacy!"=="0" (
    reg add "%RegExp%" /v ShowRecent /t REG_DWORD /d 1 /f >nul
    reg add "%RegExp%" /v ShowFrequent /t REG_DWORD /d 1 /f >nul
    reg add "%RegExp%" /v ShowCloudFilesInQuickAccess /t REG_DWORD /d 1 /f >nul
    reg add "%RegAdv%" /v Start_TrackDocs /t REG_DWORD /d 1 /f >nul
) else (
    reg add "%RegExp%" /v ShowRecent /t REG_DWORD /d 0 /f >nul
    reg add "%RegExp%" /v ShowFrequent /t REG_DWORD /d 0 /f >nul
    reg add "%RegExp%" /v ShowCloudFilesInQuickAccess /t REG_DWORD /d 0 /f >nul
    reg add "%RegAdv%" /v Start_TrackDocs /t REG_DWORD /d 0 /f >nul
)
goto ExpMenu
:ExpCtx
if "!_CtxMenu!"=="1" (reg delete "HKCU\Software\Classes\CLSID\%ClsidMenu%" /f >nul 2>&1) else (reg add "HKCU\Software\Classes\CLSID\%ClsidMenu%\InprocServer32" /ve /f >nul)
goto ExpMenu
:ExpExt
if "!_Ext!"=="0" (reg add "%RegAdv%" /v HideFileExt /t REG_DWORD /d 1 /f >nul) else (reg add "%RegAdv%" /v HideFileExt /t REG_DWORD /d 0 /f >nul)
goto ExpMenu
:ExpHidden
if "!_Hidden!"=="1" (
    reg add "%RegAdv%" /v Hidden /t REG_DWORD /d 2 /f >nul
    reg add "%RegAdv%" /v ShowSuperHidden /t REG_DWORD /d 0 /f >nul
) else (
    reg add "%RegAdv%" /v Hidden /t REG_DWORD /d 1 /f >nul
    reg add "%RegAdv%" /v ShowSuperHidden /t REG_DWORD /d 1 /f >nul
)
goto ExpMenu
:ExpPath
if "!_FullPath!"=="1" (reg add "%RegAdv%" /v FullPathAddress /t REG_DWORD /d 0 /f >nul) else (reg add "%RegAdv%" /v FullPathAddress /t REG_DWORD /d 1 /f >nul)
goto ExpMenu
:ExpCheck
if "!_CheckBoxes!"=="1" (reg add "%RegAdv%" /v AutoCheckSelect /t REG_DWORD /d 0 /f >nul) else (reg add "%RegAdv%" /v AutoCheckSelect /t REG_DWORD /d 1 /f >nul)
goto ExpMenu
:ExpOneDrive
if "!_OneDrive!"=="0" (
    reg delete "HKCU\Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}" /v System.IsPinnedToNameSpaceTree /f >nul 2>&1
    reg add "HKCU\Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 1 /f >nul
) else (
    reg add "HKCU\Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 0 /f >nul
)
goto ExpMenu

:ExpApplyAll
reg add "%RegAdv%" /v LaunchTo /t REG_DWORD /d 1 /f >nul
reg add "HKCU\Software\Classes\CLSID\%ClsidHome%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 0 /f >nul
reg add "HKCU\Software\Classes\CLSID\%ClsidGallery%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 0 /f >nul
reg add "HKCU\Software\Classes\CLSID\%ClsidNetwork%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 0 /f >nul
reg add "HKCU\Software\Classes\CLSID\%ClsidRecycle%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 1 /f >nul
reg add "%RegDeskIcons%" /v "%ClsidRecycle%" /t REG_DWORD /d 1 /f >nul
reg add "%RegAdv%" /v UseCompactMode /t REG_DWORD /d 1 /f >nul
reg add "%RegExp%" /v ShowRecent /t REG_DWORD /d 0 /f >nul
reg add "%RegExp%" /v ShowFrequent /t REG_DWORD /d 0 /f >nul
reg add "%RegExp%" /v ShowCloudFilesInQuickAccess /t REG_DWORD /d 0 /f >nul
reg add "%RegAdv%" /v Start_TrackDocs /t REG_DWORD /d 0 /f >nul
reg add "HKCU\Software\Classes\CLSID\%ClsidMenu%\InprocServer32" /ve /f >nul
reg add "%RegAdv%" /v HideFileExt /t REG_DWORD /d 0 /f >nul
reg add "%RegAdv%" /v Hidden /t REG_DWORD /d 1 /f >nul
reg add "%RegAdv%" /v ShowSuperHidden /t REG_DWORD /d 1 /f >nul
reg add "%RegAdv%" /v FullPathAddress /t REG_DWORD /d 1 /f >nul
reg add "%RegAdv%" /v AutoCheckSelect /t REG_DWORD /d 0 /f >nul
reg add "HKCU\Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 0 /f >nul
goto ExpRestart

:ExpDefaults
reg add "%RegAdv%" /v LaunchTo /t REG_DWORD /d 2 /f >nul
reg add "HKCU\Software\Classes\CLSID\%ClsidHome%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 1 /f >nul
reg add "HKCU\Software\Classes\CLSID\%ClsidGallery%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 1 /f >nul
reg add "HKCU\Software\Classes\CLSID\%ClsidNetwork%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 1 /f >nul
reg add "HKCU\Software\Classes\CLSID\%ClsidRecycle%" /v System.IsPinnedToNameSpaceTree /t REG_DWORD /d 0 /f >nul
reg delete "%RegDeskIcons%" /v "%ClsidRecycle%" /f >nul 2>&1
reg add "%RegAdv%" /v UseCompactMode /t REG_DWORD /d 0 /f >nul
reg add "%RegExp%" /v ShowRecent /t REG_DWORD /d 1 /f >nul
reg add "%RegExp%" /v ShowFrequent /t REG_DWORD /d 1 /f >nul
reg add "%RegExp%" /v ShowCloudFilesInQuickAccess /t REG_DWORD /d 1 /f >nul
reg add "%RegAdv%" /v Start_TrackDocs /t REG_DWORD /d 1 /f >nul
reg delete "HKCU\Software\Classes\CLSID\%ClsidMenu%" /f >nul 2>&1
reg add "%RegAdv%" /v HideFileExt /t REG_DWORD /d 1 /f >nul
reg add "%RegAdv%" /v Hidden /t REG_DWORD /d 2 /f >nul
reg add "%RegAdv%" /v ShowSuperHidden /t REG_DWORD /d 0 /f >nul
reg add "%RegAdv%" /v FullPathAddress /t REG_DWORD /d 0 /f >nul
reg add "%RegAdv%" /v AutoCheckSelect /t REG_DWORD /d 0 /f >nul
reg delete "HKCU\Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}" /v System.IsPinnedToNameSpaceTree /f >nul 2>&1
goto ExpRestart

:ExpRestart
taskkill /f /im explorer.exe >nul 2>&1
start explorer.exe
timeout /t 2 /nobreak >nul
goto ExpMenu

:ExpStatus
call :GetRegValue "%RegAdv%" "LaunchTo" 2 _OpenLoc
call :GetRegValue "HKCU\Software\Classes\CLSID\%ClsidHome%" "System.IsPinnedToNameSpaceTree" 1 _HideHome
call :GetRegValue "HKCU\Software\Classes\CLSID\%ClsidGallery%" "System.IsPinnedToNameSpaceTree" 1 _HideGallery
call :GetRegValue "HKCU\Software\Classes\CLSID\%ClsidNetwork%" "System.IsPinnedToNameSpaceTree" 1 _HideNetwork
call :GetRegValue "HKCU\Software\Classes\CLSID\%ClsidRecycle%" "System.IsPinnedToNameSpaceTree" 0 _ShowRecycle
call :GetRegValue "%RegDeskIcons%" "%ClsidRecycle%" 0 _DeskRecycle
call :GetRegValue "%RegAdv%" "UseCompactMode" 0 _Compact
call :GetRegValue "%RegExp%" "ShowRecent" 1 _Privacy
reg query "HKCU\Software\Classes\CLSID\%ClsidMenu%\InprocServer32" >nul 2>&1
if %errorlevel% EQU 0 (set "_CtxMenu=1") else (set "_CtxMenu=0")
call :GetRegValue "%RegAdv%" "HideFileExt" 1 _Ext
call :GetRegValue "%RegAdv%" "Hidden" 2 _Hidden
call :GetRegValue "%RegAdv%" "FullPathAddress" 0 _FullPath
call :GetRegValue "%RegAdv%" "AutoCheckSelect" 0 _CheckBoxes
call :GetRegValue "HKCU\Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}" "System.IsPinnedToNameSpaceTree" 1 _OneDrive
exit /b

:GetRegValue
set "%4=%3"
for /f "tokens=3" %%a in ('reg query "%~1" /v "%~2" 2^>nul') do (set /a "%4=%%a")
exit /b

:FXMenu
call :Hdr "ВИЗУАЛЬНЫЕ ЭФФЕКТЫ"
echo  %Bold%[1]%Reset%  Наилучшее быстродействие  (всё выкл.)
echo  %Bold%[2]%Reset%  Наилучшее оформление      (всё вкл.)
echo  %Bold%[3]%Reset%  Рекомендуемые Windows
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[4]%Reset%  Анимации окон и меню
echo  %Bold%[5]%Reset%  Прозрачность Win10/11
echo  %Bold%[6]%Reset%  Тени и сглаживание
echo  %Bold%[7]%Reset%  Показать эскизы вместо значков
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[G]%Reset%  Открыть окно Windows  (SystemPropertiesPerformance)
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto FXBestPerf
if "%choice%"=="2" goto FXBestLook
if "%choice%"=="3" goto FXDefault
if "%choice%"=="4" goto FXAnim
if "%choice%"=="5" goto FXTrans
if "%choice%"=="6" goto FXShadows
if "%choice%"=="7" goto FXThumbs
if /i "%choice%"=="G" goto FXGUI
if "%choice%"=="0" goto UIMenu
goto FXMenu

:FXBestPerf
call :Hdr "НАИЛУЧШЕЕ БЫСТРОДЕЙСТВИЕ"
echo  %Yellow%Отключаю все визуальные эффекты...%Reset%
echo.
set "OPFAIL=0"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects" /v VisualFXSetting /t REG_DWORD /d 2 /f >nul 2>&1
call :FailIf
reg add "HKCU\Control Panel\Desktop" /v UserPreferencesMask /t REG_BINARY /d 9012078010000000 /f >nul 2>&1
call :FailIf
reg add "HKCU\Control Panel\Desktop" /v MinAnimate /t REG_SZ /d 0 /f >nul 2>&1
call :FailIf
reg add "HKCU\Control Panel\Desktop\WindowMetrics" /v MinAnimate /t REG_SZ /d 0 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarAnimations /t REG_DWORD /d 0 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ListviewAlphaSelect /t REG_DWORD /d 0 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ListviewShadow /t REG_DWORD /d 0 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v IconsOnly /t REG_DWORD /d 1 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\DWM" /v EnableAeroPeek /t REG_DWORD /d 0 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\DWM" /v AlwaysHibernateThumbnails /t REG_DWORD /d 0 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" /v EnableTransparency /t REG_DWORD /d 0 /f >nul 2>&1
call :FailIf
if "!OPFAIL!"=="0" (
    call :Ok "VisualFXSetting = 2  (быстродействие)"
    call :Ok "Анимации / прозрачность / тени выкл."
) else (
    call :Err "Не все визуальные эффекты удалось отключить"
)
echo.
echo  %Yellow%Чтобы применить полностью — выйдите из системы или перезагрузите.%Reset%
call :RestartExplorer
call :PauseBack
goto FXMenu

:FXBestLook
call :Hdr "НАИЛУЧШЕЕ ОФОРМЛЕНИЕ"
echo  %Yellow%Включаю все визуальные эффекты...%Reset%
echo.
set "OPFAIL=0"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects" /v VisualFXSetting /t REG_DWORD /d 1 /f >nul 2>&1
call :FailIf
reg add "HKCU\Control Panel\Desktop" /v UserPreferencesMask /t REG_BINARY /d 9E3E078012000000 /f >nul 2>&1
call :FailIf
reg add "HKCU\Control Panel\Desktop" /v MinAnimate /t REG_SZ /d 1 /f >nul 2>&1
call :FailIf
reg add "HKCU\Control Panel\Desktop\WindowMetrics" /v MinAnimate /t REG_SZ /d 1 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarAnimations /t REG_DWORD /d 1 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ListviewAlphaSelect /t REG_DWORD /d 1 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ListviewShadow /t REG_DWORD /d 1 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v IconsOnly /t REG_DWORD /d 0 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\DWM" /v EnableAeroPeek /t REG_DWORD /d 1 /f >nul 2>&1
call :FailIf
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" /v EnableTransparency /t REG_DWORD /d 1 /f >nul 2>&1
call :FailIf
if "!OPFAIL!"=="0" (call :Ok "VisualFXSetting = 1  (оформление)") else (call :Err "Не все визуальные эффекты удалось включить")
call :RestartExplorer
call :PauseBack
goto FXMenu

:FXDefault
call :Hdr "РЕКОМЕНДУЕМЫЕ WINDOWS"
set "OPFAIL=0"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects" /v VisualFXSetting /t REG_DWORD /d 0 /f >nul 2>&1
call :FailIf
if "!OPFAIL!"=="0" (call :Ok "VisualFXSetting = 0  (решает Windows)") else (call :Err "Не удалось сбросить VisualFXSetting")
echo.
echo  %Yellow%Откройте также пункт [G] для тонкой настройки.%Reset%
call :PauseBack
goto FXMenu

:FXAnim
call :Hdr "АНИМАЦИИ"
echo  %Bold%[1]%Reset%  Выключить анимации
echo  %Bold%[2]%Reset%  Включить анимации
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKCU\Control Panel\Desktop" /v MinAnimate /t REG_SZ /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Control Panel\Desktop\WindowMetrics" /v MinAnimate /t REG_SZ /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarAnimations /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects" /v VisualFXSetting /t REG_DWORD /d 3 /f >nul
    call :FailIf
    call :ReportOp "Анимации выключены" "Не удалось выключить анимации"
    call :RestartExplorer
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg add "HKCU\Control Panel\Desktop" /v MinAnimate /t REG_SZ /d 1 /f >nul
    call :FailIf
    reg add "HKCU\Control Panel\Desktop\WindowMetrics" /v MinAnimate /t REG_SZ /d 1 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarAnimations /t REG_DWORD /d 1 /f >nul
    call :FailIf
    call :ReportOp "Анимации включены" "Не удалось включить анимации"
    call :RestartExplorer
)
call :PauseBack
goto FXMenu

:FXTrans
call :Hdr "ПРОЗРАЧНОСТЬ"
echo  %Bold%[1]%Reset%  Выключить прозрачность
echo  %Bold%[2]%Reset%  Включить прозрачность
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" /v EnableTransparency /t REG_DWORD /d 0 /f >nul
    call :FailIf
    call :ReportOp "Прозрачность выключена" "Не удалось выключить прозрачность"
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" /v EnableTransparency /t REG_DWORD /d 1 /f >nul
    call :FailIf
    call :ReportOp "Прозрачность включена" "Не удалось включить прозрачность"
)
call :PauseBack
goto FXMenu

:FXShadows
call :Hdr "ТЕНИ И СГЛАЖИВАНИЕ"
echo  %Bold%[1]%Reset%  Выключить тени / Aero Peek
echo  %Bold%[2]%Reset%  Включить тени / Aero Peek
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ListviewShadow /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\DWM" /v EnableAeroPeek /t REG_DWORD /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects" /v VisualFXSetting /t REG_DWORD /d 3 /f >nul
    call :FailIf
    call :ReportOp "Тени и Aero Peek выключены" "Не удалось выключить тени / Aero Peek"
    call :RestartExplorer
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ListviewShadow /t REG_DWORD /d 1 /f >nul
    call :FailIf
    reg add "HKCU\Software\Microsoft\Windows\DWM" /v EnableAeroPeek /t REG_DWORD /d 1 /f >nul
    call :FailIf
    call :ReportOp "Тени и Aero Peek включены" "Не удалось включить тени / Aero Peek"
    call :RestartExplorer
)
call :PauseBack
goto FXMenu

:FXThumbs
call :Hdr "ЭСКИЗЫ / ЗНАЧКИ"
echo  %Bold%[1]%Reset%  Значки вместо эскизов  (быстрее)
echo  %Bold%[2]%Reset%  Эскизы вместо значков  (красивее)
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v IconsOnly /t REG_DWORD /d 1 /f >nul
    call :FailIf
    call :ReportOp "Показывать значки" "Не удалось переключить на значки"
    call :RestartExplorer
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v IconsOnly /t REG_DWORD /d 0 /f >nul
    call :FailIf
    call :ReportOp "Показывать эскизы" "Не удалось переключить на эскизы"
    call :RestartExplorer
)
call :PauseBack
goto FXMenu

:FXGUI
start SystemPropertiesPerformance.exe
goto FXMenu

:RestartExplorer
echo.
echo  %Yellow%Перезапустить Проводник сейчас?%Reset%
set /p "r=  y/n: "
if /i "%r%"=="y" (
    taskkill /f /im explorer.exe >nul 2>&1
    start explorer.exe
    call :Ok "Explorer перезапущен"
)
exit /b

:DelayMenu
call :Hdr "ЗАДЕРЖКА МЕНЮ"
echo  %Bold%[1]%Reset%  20 мс
echo  %Bold%[2]%Reset%  400 мс
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto UIMenu
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKCU\Control Panel\Desktop" /v MenuShowDelay /t REG_SZ /d 20 /f >nul 2>&1
    call :FailIf
    call :ReportOp "Задержка меню: 20 мс" "Не удалось задать задержку меню 20 мс"
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg add "HKCU\Control Panel\Desktop" /v MenuShowDelay /t REG_SZ /d 400 /f >nul 2>&1
    call :FailIf
    call :ReportOp "Задержка меню: 400 мс" "Не удалось задать задержку меню 400 мс"
)
call :PauseBack
goto DelayMenu

:WallMenu
call :Hdr "СЖАТИЕ ОБОЕВ"
echo  %Bold%[1]%Reset%  Выкл  (качество 100%%)
echo  %Bold%[2]%Reset%  Вкл   (по умолчанию)
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto UIMenu
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKCU\Control Panel\Desktop" /v JPEGImportQuality /t REG_DWORD /d 100 /f >nul 2>&1
    call :FailIf
    call :ReportOp "Сжатие обоев отключено" "Не удалось отключить сжатие обоев"
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg delete "HKCU\Control Panel\Desktop" /v JPEGImportQuality /f >nul 2>&1
    call :FailIfDel
    call :ReportOp "Сжатие обоев включено ^(по умолчанию^)" "Не удалось вернуть сжатие обоев"
)
call :PauseBack
goto WallMenu

:RecMenu
call :Hdr "РАЗДЕЛ «РЕКОМЕНДУЕМ»"
echo  %Bold%[1]%Reset%  Скрыть
echo  %Bold%[2]%Reset%  Показать
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto UIMenu
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\Explorer" /v HideRecommendedSection /t REG_DWORD /d 1 /f >nul 2>&1
    call :FailIf
    reg add "HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Start" /v HideRecommendedSection /t REG_DWORD /d 1 /f >nul 2>&1
    call :FailIf
    reg add "HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Education" /v IsEducationEnvironment /t REG_DWORD /d 1 /f >nul 2>&1
    call :FailIf
    call :ReportOp "Раздел «Рекомендуем» скрыт" "Не удалось скрыть раздел «Рекомендуем»"
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\Explorer" /v HideRecommendedSection /f >nul 2>&1
    call :FailIfDel
    reg delete "HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Start" /v HideRecommendedSection /f >nul 2>&1
    call :FailIfDel
    reg delete "HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Education" /v IsEducationEnvironment /f >nul 2>&1
    call :FailIfDel
    call :ReportOp "Раздел «Рекомендуем» показан" "Не удалось показать раздел «Рекомендуем»"
)
call :PauseBack
goto RecMenu

:TaskbarClean
call :Hdr "ОЧИСТКА ПАНЕЛИ ЗАДАЧ"
echo  %Yellow%Очистка панели задач...%Reset%
set "OPFAIL=0"
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband" /f >nul 2>&1
call :FailIfDel
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Streams\Desktop" /f >nul 2>&1
call :FailIfDel
taskkill /f /im explorer.exe >nul 2>&1
start explorer.exe
if errorlevel 1 set "OPFAIL=1"
timeout /t 2 /nobreak >nul
if "!OPFAIL!"=="0" (call :Ok "Готово") else (call :Err "Очистка панели задач выполнена не полностью")
call :PauseBack
goto UIMenu

rem ========================================================================
rem  11. ВВОД, БРАУЗЕР И ИГРЫ
rem ========================================================================
:InpMenu
call :RequireAdmin
if errorlevel 1 goto MainMenu
call :Hdr "ВВОД, БРАУЗЕР И ИГРЫ"
echo  %Bold%[1]%Reset%  Ускорение мыши
echo  %Bold%[2]%Reset%  Залипание клавиш
echo  %Bold%[3]%Reset%  Ускорение запуска Edge
echo  %Bold%[4]%Reset%  Game Bar / DVR / Game Mode
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto MouseMenu
if "%choice%"=="2" goto StickyMenu
if "%choice%"=="3" goto EdgeMenu
if "%choice%"=="4" goto GameMenu
if "%choice%"=="0" goto MainMenu
goto InpMenu

:MouseMenu
call :Hdr "УСКОРЕНИЕ МЫШИ"
echo  %Bold%[1]%Reset%  Выкл
echo  %Bold%[2]%Reset%  Вкл
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto InpMenu
if "%choice%"=="1" (
    echo.
    echo  %Yellow%Отключение ускорения мыши...%Reset%
    set "OPFAIL=0"
    reg add "HKCU\Control Panel\Mouse" /v MouseSpeed /t REG_SZ /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Control Panel\Mouse" /v MouseThreshold1 /t REG_SZ /d 0 /f >nul
    call :FailIf
    reg add "HKCU\Control Panel\Mouse" /v MouseThreshold2 /t REG_SZ /d 0 /f >nul
    call :FailIf
    powershell -NoProfile -Command "$code='using System.Runtime.InteropServices; public class W32 { [DllImport(\"user32.dll\")] public static extern bool SystemParametersInfo(uint a, uint b, int[] c, uint d); }'; Add-Type -TypeDefinition $code; $p=[int[]]@(0,0,0); [W32]::SystemParametersInfo(4,0,$p,3)" >nul 2>&1
    call :FailIf
    if "!OPFAIL!"=="0" (call :Ok "Ускорение мыши отключено") else (call :Err "Не удалось полностью отключить ускорение мыши")
)
if "%choice%"=="2" (
    echo.
    echo  %Yellow%Включение ускорения мыши...%Reset%
    set "OPFAIL=0"
    reg add "HKCU\Control Panel\Mouse" /v MouseSpeed /t REG_SZ /d 1 /f >nul
    call :FailIf
    reg add "HKCU\Control Panel\Mouse" /v MouseThreshold1 /t REG_SZ /d 6 /f >nul
    call :FailIf
    reg add "HKCU\Control Panel\Mouse" /v MouseThreshold2 /t REG_SZ /d 10 /f >nul
    call :FailIf
    powershell -NoProfile -Command "$code='using System.Runtime.InteropServices; public class W32 { [DllImport(\"user32.dll\")] public static extern bool SystemParametersInfo(uint a, uint b, int[] c, uint d); }'; Add-Type -TypeDefinition $code; $p=[int[]]@(6,10,1); [W32]::SystemParametersInfo(4,0,$p,3)" >nul 2>&1
    call :FailIf
    if "!OPFAIL!"=="0" (call :Ok "Ускорение мыши включено") else (call :Err "Не удалось полностью включить ускорение мыши")
)
call :PauseBack
goto MouseMenu

:StickyMenu
call :Hdr "ЗАЛИПАНИЕ КЛАВИШ"
echo  %Bold%[1]%Reset%  Выкл
echo  %Bold%[2]%Reset%  Вкл
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto InpMenu
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKCU\Control Panel\Accessibility\StickyKeys" /v Flags /t REG_SZ /d 506 /f >nul 2>&1
    call :FailIf
    reg add "HKCU\Control Panel\Accessibility\Keyboard Response" /v Flags /t REG_SZ /d 122 /f >nul 2>&1
    call :FailIf
    reg add "HKCU\Control Panel\Accessibility\ToggleKeys" /v Flags /t REG_SZ /d 58 /f >nul 2>&1
    call :FailIf
    call :ReportOp "Залипание клавиш отключено" "Не удалось отключить залипание клавиш"
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg add "HKCU\Control Panel\Accessibility\StickyKeys" /v Flags /t REG_SZ /d 510 /f >nul 2>&1
    call :FailIf
    reg add "HKCU\Control Panel\Accessibility\Keyboard Response" /v Flags /t REG_SZ /d 126 /f >nul 2>&1
    call :FailIf
    reg add "HKCU\Control Panel\Accessibility\ToggleKeys" /v Flags /t REG_SZ /d 62 /f >nul 2>&1
    call :FailIf
    call :ReportOp "Залипание клавиш включено" "Не удалось включить залипание клавиш"
)
call :PauseBack
goto StickyMenu

:EdgeMenu
call :Hdr "УСКОРЕНИЕ ЗАПУСКА EDGE"
echo  %Bold%[1]%Reset%  Выкл
echo  %Bold%[2]%Reset%  Вкл
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto InpMenu
if "%choice%"=="1" (
    set "OPFAIL=0"
    reg add "HKLM\SOFTWARE\Policies\Microsoft\Edge" /v StartupBoostEnabled /t REG_DWORD /d 0 /f >nul 2>&1
    call :FailIf
    reg add "HKLM\SOFTWARE\Policies\Microsoft\Edge" /v BackgroundModeEnabled /t REG_DWORD /d 0 /f >nul 2>&1
    call :FailIf
    call :ReportOp "Ускорение запуска Edge отключено" "Не удалось отключить ускорение запуска Edge"
)
if "%choice%"=="2" (
    set "OPFAIL=0"
    reg delete "HKLM\SOFTWARE\Policies\Microsoft\Edge" /v StartupBoostEnabled /f >nul 2>&1
    call :FailIfDel
    reg delete "HKLM\SOFTWARE\Policies\Microsoft\Edge" /v BackgroundModeEnabled /f >nul 2>&1
    call :FailIfDel
    call :ReportOp "Ускорение запуска Edge включено" "Не удалось включить ускорение запуска Edge"
)
call :PauseBack
goto EdgeMenu

:GameMenu
call :Hdr "GAME BAR / DVR / OVERLAY"
echo  %Bold%[1]%Reset%  Отключить всё  (Game Bar, DVR, запись)
echo  %Bold%[2]%Reset%  Включить всё обратно
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[3]%Reset%  Только Game Bar
echo  %Bold%[4]%Reset%  Только фоновая запись / DVR
echo  %Bold%[5]%Reset%  Только Game Mode
echo  %Bold%[6]%Reset%  Показать текущий статус
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto GameOffAll
if "%choice%"=="2" goto GameOnAll
if "%choice%"=="3" goto GameBarOnly
if "%choice%"=="4" goto GameDVROnly
if "%choice%"=="5" goto GameModeOnly
if "%choice%"=="6" goto GameStatus
if "%choice%"=="0" goto InpMenu
goto GameMenu

:GameOffAll
call :Hdr "ОТКЛЮЧЕНИЕ GAME BAR / DVR"
set "GFAIL=0"
call :SetGameBar 0
if errorlevel 1 set "GFAIL=1"
call :SetDVR 0
if errorlevel 1 set "GFAIL=1"
call :SetGameMode 1
if errorlevel 1 set "GFAIL=1"
echo.
if "!GFAIL!"=="0" (
    echo  %Green%Game Bar и DVR отключены.%Reset%
    echo  %Green%Game Mode оставлен включённым  (полезно для игр).%Reset%
) else (
    call :Err "Не все параметры Game Bar / DVR / Game Mode применены"
)
call :PauseBack
goto GameMenu

:GameOnAll
call :Hdr "ВКЛЮЧЕНИЕ GAME BAR / DVR"
set "GFAIL=0"
call :SetGameBar 1
if errorlevel 1 set "GFAIL=1"
call :SetDVR 1
if errorlevel 1 set "GFAIL=1"
call :SetGameMode 1
if errorlevel 1 set "GFAIL=1"
echo.
if "!GFAIL!"=="0" (
    echo  %Green%Game Bar, DVR и Game Mode включены.%Reset%
) else (
    call :Err "Не все параметры Game Bar / DVR / Game Mode применены"
)
call :PauseBack
goto GameMenu

:GameBarOnly
call :Hdr "GAME BAR"
echo  %Bold%[1]%Reset%  Отключить
echo  %Bold%[2]%Reset%  Включить
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto GameMenu
if "%choice%"=="1" call :SetGameBar 0
if "%choice%"=="2" call :SetGameBar 1
call :PauseBack
goto GameMenu

:GameDVROnly
call :Hdr "ФОНОВАЯ ЗАПИСЬ / DVR"
echo  %Bold%[1]%Reset%  Отключить
echo  %Bold%[2]%Reset%  Включить
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto GameMenu
if "%choice%"=="1" call :SetDVR 0
if "%choice%"=="2" call :SetDVR 1
call :PauseBack
goto GameMenu

:GameModeOnly
call :Hdr "GAME MODE"
echo  Режим игры Windows — обычно лучше оставить ВКЛ.
echo.
echo  %Bold%[1]%Reset%  Включить Game Mode
echo  %Bold%[2]%Reset%  Выключить Game Mode
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto GameMenu
if "%choice%"=="1" call :SetGameMode 1
if "%choice%"=="2" call :SetGameMode 0
call :PauseBack
goto GameMenu

:GameStatus
call :Hdr "СТАТУС GAME BAR / DVR"
echo  %Bold%GameDVR / AppCapture:%Reset%
reg query "HKCU\System\GameConfigStore" /v GameDVR_Enabled 2>nul
reg query "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR" /v AppCaptureEnabled 2>nul
echo.
echo  %Bold%Game Bar presence:%Reset%
reg query "HKCU\SOFTWARE\Microsoft\GameBar" /v UseNexusForGameBarEnabled 2>nul
echo.
echo  %Bold%Game Mode:%Reset%
reg query "HKCU\SOFTWARE\Microsoft\GameBar" /v AutoGameModeEnabled 2>nul
reg query "HKCU\SOFTWARE\Microsoft\GameBar" /v AllowAutoGameMode 2>nul
call :PauseBack
goto GameMenu

:SetGameBar
set "VAL=%~1"
set "OPFAIL=0"
reg add "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR" /v AppCaptureEnabled /t REG_DWORD /d %VAL% /f >nul
call :FailIf
reg add "HKCU\System\GameConfigStore" /v GameDVR_Enabled /t REG_DWORD /d %VAL% /f >nul
call :FailIf
reg add "HKCU\SOFTWARE\Microsoft\GameBar" /v UseNexusForGameBarEnabled /t REG_DWORD /d %VAL% /f >nul
call :FailIf
reg add "HKCU\SOFTWARE\Microsoft\GameBar" /v ShowStartupPanel /t REG_DWORD /d 0 /f >nul
call :FailIf
reg add "HKCU\SOFTWARE\Microsoft\GameBar" /v GamePanelStartupTipOpen /t REG_DWORD /d 0 /f >nul
call :FailIf
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR" /v AllowGameDVR /t REG_DWORD /d %VAL% /f >nul
call :FailIf
if "!OPFAIL!"=="0" (
    if "%VAL%"=="0" (echo  %Red%[выкл]%Reset%  Game Bar / AppCapture) else (echo  %Green%[вкл ]%Reset%  Game Bar / AppCapture)
    exit /b 0
)
call :Err "Game Bar: не все параметры записаны"
exit /b 1

:SetDVR
set "VAL=%~1"
set "OPFAIL=0"
reg add "HKCU\System\GameConfigStore" /v GameDVR_Enabled /t REG_DWORD /d %VAL% /f >nul
call :FailIf
reg add "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR" /v AppCaptureEnabled /t REG_DWORD /d %VAL% /f >nul
call :FailIf
reg add "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR" /v HistoricalCaptureEnabled /t REG_DWORD /d %VAL% /f >nul
call :FailIf
reg add "HKCU\System\GameConfigStore" /v GameDVR_FSEBehaviorMode /t REG_DWORD /d 2 /f >nul
call :FailIf
reg add "HKCU\System\GameConfigStore" /v GameDVR_HonorUserFSEBehaviorMode /t REG_DWORD /d 1 /f >nul
call :FailIf
reg add "HKCU\System\GameConfigStore" /v GameDVR_DXGIHonorFSEWindowsCompatible /t REG_DWORD /d 1 /f >nul
call :FailIf
if "!OPFAIL!"=="0" (
    if "%VAL%"=="0" (echo  %Red%[выкл]%Reset%  Фоновая запись / DVR) else (echo  %Green%[вкл ]%Reset%  Фоновая запись / DVR)
    exit /b 0
)
call :Err "DVR: не все параметры записаны"
exit /b 1

:SetGameMode
set "VAL=%~1"
set "OPFAIL=0"
reg add "HKCU\SOFTWARE\Microsoft\GameBar" /v AutoGameModeEnabled /t REG_DWORD /d %VAL% /f >nul
call :FailIf
reg add "HKCU\SOFTWARE\Microsoft\GameBar" /v AllowAutoGameMode /t REG_DWORD /d %VAL% /f >nul
call :FailIf
if "!OPFAIL!"=="0" (
    if "%VAL%"=="0" (echo  %Red%[выкл]%Reset%  Game Mode) else (echo  %Green%[вкл ]%Reset%  Game Mode)
    exit /b 0
)
call :Err "Game Mode: не все параметры записаны"
exit /b 1

rem ========================================================================
rem  12. ПОИСК И ЦЕЛОСТНОСТЬ
rem ========================================================================
:MaintMenu
call :RequireAdmin
if errorlevel 1 goto MainMenu
call :Hdr "ПОИСК И ЦЕЛОСТНОСТЬ WINDOWS"
echo  %Bold%[1]%Reset%  Отключение индексации поиска
echo  %Bold%[2]%Reset%  Проверка целостности  (DISM + SFC)
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto IdxMenu
if "%choice%"=="2" goto SFCMenu
if "%choice%"=="0" goto MainMenu
goto MaintMenu

:IdxMenu
call :Hdr "ОТКЛЮЧЕНИЕ ИНДЕКСАЦИИ ПОИСКА"
echo  %Gray%Выберите диски, на которых нужно отключить индексацию.%Reset%
echo  %Gray%Можно указать несколько букв через пробел: C D E%Reset%
echo.
echo  %Bold%Доступные диски:%Reset%
echo.
set "DriveList="
for /f "tokens=1" %%D in ('wmic logicaldisk get deviceid 2^>nul ^| find ":"') do (
    set "drv=%%D"
    if defined drv (
        echo  %Bold%[ !drv! ]%Reset%  !drv!\
        set "DriveList=!DriveList! !drv!"
    )
)
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo.
echo  %Bold%[A]%Reset%  Все доступные диски
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if /i "%choice%"=="0" goto MaintMenu
if /i "%choice%"=="A" (
    set "Selected=%DriveList%"
    goto IdxConfirm
)
if not defined choice goto IdxMenu
set "Selected=%choice%"
goto IdxConfirm

:IdxConfirm
call :Hdr "ОТКЛЮЧЕНИЕ ИНДЕКСАЦИИ ПОИСКА"
echo  Выбрано:
for %%D in (%Selected%) do echo    %Bold%%%D\%Reset%
echo.
echo  %Yellow%Внимание:%Reset%
echo  %Gray%Для выбранных дисков файлы и папки будут помечены как%Reset%
echo  %Gray%"не индексировать содержимое". Поиск станет чуть медленнее.%Reset%
echo.
echo  %Bold%[1]%Reset%  Продолжить
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto IdxMenu
if "%choice%"=="1" goto IdxDisable
goto IdxConfirm

:IdxDisable
call :Hdr "ОТКЛЮЧЕНИЕ ИНДЕКСАЦИИ ПОИСКА"
set /a Count=0
set "IDXFAIL=0"
for %%D in (%Selected%) do (
    set /a Count+=1
    echo  %Bold%[ !Count! ]%Reset%  Обработка %%D\...
    attrib +I "%%D" /S /D >nul 2>&1
    if errorlevel 1 (
        set "IDXFAIL=1"
        echo       %Red%Не удалось обработать %%D\%Reset%
        call :Log "INDEX | disable %%D | FAILED"
    ) else (
        echo       %Green%Индексация содержимого отключена.%Reset%
        call :Log "INDEX | disable %%D | rc=0"
    )
    echo.
)
echo  %Gray%--------------------------------------------------------------------%Reset%
echo.
if "!IDXFAIL!"=="0" (
    call :Ok "Операция завершена"
) else (
    call :Err "Не все выбранные диски удалось обработать — см. вывод и лог"
)
echo.
echo  %Gray%Windows больше не будет индексировать содержимое указанных%Reset%
echo  %Gray%файлов и папок на выбранных дисках.%Reset%
echo.
echo  %Yellow%Примечание:%Reset%
echo  %Gray%Служба Windows Search не отключается полностью.%Reset%
call :PauseBack
goto IdxMenu

:SFCMenu
call :Hdr "ПРОВЕРКА ЦЕЛОСТНОСТИ WINDOWS"
echo  %Bold%[1]%Reset%  Запустить DISM + SFC
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="1" goto IntegrityCheck
if "%choice%"=="0" goto MaintMenu
goto SFCMenu

:IntegrityCheck
call :Hdr "ПРОВЕРКА ЦЕЛОСТНОСТИ WINDOWS"
echo  Шаг 1 из 2: DISM - восстановление хранилища компонентов
echo.
echo  DISM.exe /Online /Cleanup-Image /RestoreHealth
echo.
DISM.exe /Online /Cleanup-Image /RestoreHealth
set "DISM_CODE=%errorlevel%"
echo.
if "%DISM_CODE%"=="0" (
    call :Ok "DISM выполнен успешно."
) else (
    call :Err "DISM завершился с кодом %DISM_CODE%."
    echo  SFC всё равно будет запущен.
)
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo.
echo  Шаг 2 из 2: SFC - проверка системных файлов
echo.
echo  sfc.exe /scannow
echo.
sfc.exe /scannow
set "SFC_CODE=%errorlevel%"
echo.
if "%SFC_CODE%"=="0" (
    call :Ok "SFC выполнен успешно."
) else (
    call :Err "SFC завершился с кодом %SFC_CODE%."
)
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  Итог:
echo  Код DISM: %DISM_CODE%
echo  Код SFC:  %SFC_CODE%
call :PauseBack
goto SFCMenu

rem ========================================================================
rem  13. UAC
rem ========================================================================
:SecMenu
call :RequireAdmin
if errorlevel 1 goto MainMenu
call :Hdr "КОНТРОЛЬ УЧЁТНЫХ ЗАПИСЕЙ - UAC"
echo  %Bold%[1]%Reset%  Ослабить
echo  %Bold%[2]%Reset%  Вкл  (стандартный уровень)
echo.
echo  %Gray%--------------------------------------------------------------------%Reset%
echo  %Bold%[0]%Reset%  Назад
echo.
call :Ask
if "%choice%"=="0" goto MainMenu
if "%choice%"=="1" goto SecWeaken
if "%choice%"=="2" goto SecEnable
goto SecMenu

:SecWeaken
echo.
echo  %Red%Ослабление UAC снижает защиту от повышения прав.%Reset%
echo  %Red%Это потенциально разрушительная операция.%Reset%
set "conf="
set /p "conf=  Введите YES для подтверждения: "
if /i not "!conf!"=="YES" (
    echo  %Gray%Отменено.%Reset%
    call :PauseBack
    goto SecMenu
)
echo.
echo  %Yellow%Ослабление контроля учётных записей ^(UAC^)...%Reset%
set "OPFAIL=0"
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v PromptOnSecureDesktop /t REG_DWORD /d 0 /f >nul 2>&1
if errorlevel 1 set "OPFAIL=1"
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v ConsentPromptBehaviorAdmin /t REG_DWORD /d 0 /f >nul 2>&1
if errorlevel 1 set "OPFAIL=1"
if "!OPFAIL!"=="0" (call :Ok "UAC ослаблен") else (call :Err "UAC не удалось полностью ослабить — см. лог")
call :PauseBack
goto SecMenu

:SecEnable
echo.
echo  %Yellow%Включение контроля учётных записей ^(UAC^)...%Reset%
set "OPFAIL=0"
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v PromptOnSecureDesktop /t REG_DWORD /d 1 /f >nul 2>&1
if errorlevel 1 set "OPFAIL=1"
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v ConsentPromptBehaviorAdmin /t REG_DWORD /d 5 /f >nul 2>&1
if errorlevel 1 set "OPFAIL=1"
if "!OPFAIL!"=="0" (call :Ok "UAC включён") else (call :Err "UAC не удалось полностью включить — см. лог")
call :PauseBack
goto SecMenu
