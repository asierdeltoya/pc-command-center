; PC Command Center — Inno Setup script
; Builds dist\PCCommandCenter-Setup.exe from the self-contained publish output in dist\app.

#define AppName "PC Command Center"
#define AppVersion "1.0.0"
#define AppPublisher "PC Command Center"
#define AppExe "PCCommandCenter.exe"

[Setup]
AppId={{7C1F3B8E-5D2A-4E9B-9F21-3C6A8B4D2E71}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments=System diagnostics and management suite.
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs per user without admin rights by default; the user can choose "all users" (requires elevation).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=PCCommandCenter-Setup
SetupIconFile=..\src\PCCommandCenter.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "System diagnostics and management suite"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Ask a running instance (including one hidden in the tray) to exit cleanly before files are removed.
Filename: "{app}\{#AppExe}"; Parameters: "--shutdown"; Flags: runhidden waituntilterminated; RunOnceId: "ShutdownApp"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Existing: String;
begin
  Result := '';
  // Upgrade: close a running copy gracefully so its files can be replaced.
  Existing := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Existing) then
    Exec(Existing, '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Remove the "Start with Windows" entry the app may have created for this user.
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'PCCommandCenter');
  end;
end;
