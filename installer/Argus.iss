; Inno Setup script for Argus. Build with:
;   iscc /DAppVersion=1.0.0 /DSourceDir=..\artifacts\publish installer\Argus.iss
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

[Setup]
AppId={{B7E4D1C2-5A3F-4E8B-9C61-0A5E7D1F2C33}
AppName=Argus
AppVersion={#AppVersion}
AppPublisher=Argus
DefaultDirName={autopf}\Argus
DefaultGroupName=Argus
UninstallDisplayIcon={app}\Argus.exe
SetupIconFile=..\src\Argus\Assets\Argus.ico
OutputDir=Output
OutputBaseFilename=Argus-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
DisableProgramGroupPage=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Argus"; Filename: "{app}\Argus.exe"
Name: "{autodesktop}\Argus"; Filename: "{app}\Argus.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Argus.exe"; Description: "Launch Argus"; Flags: nowait postinstall skipifsilent

[Code]
procedure KillArgus();
var rc: Integer;
begin
  Exec('taskkill.exe', '/im Argus.exe /f', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillArgus();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var dataDir: String; rc: Integer;
begin
  if CurUninstallStep = usUninstall then KillArgus();
  if CurUninstallStep = usPostUninstall then
  begin
    dataDir := ExpandConstant('{userappdata}\Argus');
    if DirExists(dataDir) then
      if MsgBox('Also delete your Argus settings, activity log and enrolled face data?', mbConfirmation, MB_YESNO) = IDYES then
        DelTree(dataDir, True, True, True);
    // remove the "Start with Windows" entry the app may have created
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Argus');
  end;
end;
