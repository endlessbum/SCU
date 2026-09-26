; Инсталлятор SCU (Inno Setup 6.3+: ArchitecturesInstallIn64BitMode=x64compatible
; появился в 6.3). Версия читается из publish\SCU.exe; переопределение:
;   ISCC.exe /DAppVersion=1.2.3 setup.iss
; Перед сборкой: dotnet publish SCU.App -c Release -r win-x64 --self-contained true ^
;   -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true ^
;   -p:EnableCompressionInSingleFile=true -o publish
; Сборка: "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" setup.iss

#define AppName "SCU"

; Версия из версии сборки (FileVersion publish\SCU.exe), а не захардкоженная.
#ifndef AppVersion
  #define AppVersion GetVersionNumbersString("publish\SCU.exe")
#endif

[Setup]
AppId={{F2A7B5D8-3E4C-4B9A-9C6D-1E8F7A2B5C40}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\SCU
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=installer
OutputBaseFilename={#AppName}_Setup_{#AppVersion}
SetupIconFile=SCU.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\SCU.exe
ArchitecturesInstallIn64BitMode=x64compatible
; Обновление поверх запущенного SCU: предложить закрыть приложение.
CloseApplications=yes

; Подпись installer'а и деинсталлятора (п. 49): включается release-скриптом
; через /DUSE_SIGNTOOL + /Ssigntool="...". Без /DUSE_SIGNTOOL сборка остаётся
; рабочей, но неподписанной.
#ifdef USE_SIGNTOOL
SignTool=signtool
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Всё содержимое publish (exe + Assets со скриптами) ставится в выбранную пользователем папку
Source: "publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\SCU.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\SCU.exe"; Tasks: desktopicon

; Язык, выбранный в мастере установки, становится стартовым языком приложения
; (если у пользователя ещё нет settings.json — см. Localization.LoadLanguage).
[Registry]
Root: HKCU; Subkey: "Software\SCU"; ValueType: string; ValueName: "Language"; ValueData: "{language}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\SCU.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Журналы работы приложения, если писал их рядом с exe
Type: filesandordirs; Name: "{app}\logs"

[Code]
// Резервы, состояние и журналы живут в %AppData%\SCU и переживают удаление.
// Деинсталлятор спрашивает, оставлять ли их (переустановка сохранит резервы).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    UserDataDir := ExpandConstant('{userappdata}') + '\SCU';
    if DirExists(UserDataDir) then
      if MsgBox(
           'Удалить также данные SCU (резервы, состояние, журналы)?' + #13#10 +
           UserDataDir + #13#10 + #13#10 +
           'Если оставить, при переустановке прежние резервы и настройки сохранятся.',
           mbConfirmation, MB_YESNO) = IDYES then
        DelTree(UserDataDir, True, True, True);
  end;
end;
