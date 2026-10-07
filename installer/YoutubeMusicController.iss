#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "YouTube Music Controller"
#define MyAppPublisher "YouTube Music Controller"
#define MyAppExeName "YoutubeMusicController.Tray.exe"
#define RepositoryRoot AddBackslash(SourcePath) + ".."

[Setup]
AppId={{F82FD516-2254-43B3-9DF7-C8A46EE7E909}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\YoutubeMusicController
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#RepositoryRoot}\artifacts\installer
OutputBaseFilename=YoutubeMusicController-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\Tray\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}

[Tasks]
Name: "autostart"; Description: "Start {#MyAppName} when I sign in"; GroupDescription: "Startup:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#RepositoryRoot}\artifacts\controller\*"; DestDir: "{app}\Controller"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepositoryRoot}\artifacts\tray\*"; DestDir: "{app}\Tray"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\Tray\{#MyAppExeName}"; WorkingDir: "{app}\Tray"
Name: "{autostartup}\{#MyAppName}"; Filename: "{app}\Tray\{#MyAppExeName}"; WorkingDir: "{app}\Tray"; Tasks: autostart
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\Tray\{#MyAppExeName}"; WorkingDir: "{app}\Tray"; Tasks: desktopicon

[Run]
Filename: "{app}\Tray\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /F /T /IM YoutubeMusicController.Tray.exe"; Flags: runhidden; RunOnceId: "StopTray"
Filename: "{cmd}"; Parameters: "/c taskkill /F /T /IM YoutubeMusicController.Api.exe"; Flags: runhidden; RunOnceId: "StopController"

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\YoutubeMusicController') then
  begin
    MsgBox(
      'A legacy Windows Service installation was detected. Setup will open the old uninstaller script with Administrator permission before continuing.',
      mbInformation,
      MB_OK);
    ShellExec(
      'runas',
      ExpandConstant('{cmd}'),
      '/c sc stop YoutubeMusicController & taskkill /F /T /IM YoutubeMusicController.Tray.exe & sc delete YoutubeMusicController',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{cmd}'),
    '/c taskkill /F /T /IM YoutubeMusicController.Tray.exe 2>nul & taskkill /F /T /IM YoutubeMusicController.Api.exe 2>nul',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and (not UninstallSilent) then
  begin
    if MsgBox(
      'Also remove local settings, logs, and encrypted Twitch/Discord credentials?',
      mbConfirmation,
      MB_YESNO) = IDYES then
    begin
      DelTree(
        ExpandConstant('{localappdata}\YoutubeMusicController'),
        True,
        True,
        True);
    end;
  end;
end;
