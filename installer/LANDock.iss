; LANDock installer (Inno Setup 6, https://jrsoftware.org/isinfo.php, free and open source).
; Built by scripts\build-installer.ps1 from the staged parts (build\stage\{app,host,client}, see scripts\package-components.ps1):
;   ISCC.exe /DAppVersion=0.2.0 /DStageDir=..\build\stage /DRepoRoot=.. LANDock.iss
;
; Installs for the current Windows user only (no administrator rights), so the program can update itself in place.
; Two parts can be chosen: Host (the laptop that shares its displays) and Client (the desktop PC that shows them).
; The one thing that needs administrator rights, the Windows Firewall rule for the host, is a separate step that Windows
; confirms with its own prompt.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0"
#endif
#ifndef StageDir
  #define StageDir "..\build\stage"
#endif
#ifndef RepoRoot
  #define RepoRoot ".."
#endif
#define AppName "LANDock"
#define FirewallRule "LANDock host"
#define ControlRule "LANDock control"

[Setup]
; keep this GUID in step with Installer.AppId in app\LANDock\Update\Updater.cs
AppId={{7C1D5A52-3E0B-4F51-9A63-1A4D0C0C0001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=LANDock contributors
AppPublisherURL=https://github.com/shohamh/LANDock
AppSupportURL=https://github.com/shohamh/LANDock/issues
AppUpdatesURL=https://github.com/shohamh/LANDock/releases
VersionInfoVersion={#NumericVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile={#RepoRoot}\LICENSE
SetupIconFile={#RepoRoot}\app\LANDock\Assets\landock.ico
UninstallDisplayIcon={app}\LANDock.exe
OutputDir={#RepoRoot}\dist
OutputBaseFilename=LANDock-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UsePreviousTasks=yes

[Types]
Name: "both"; Description: "Laptop and desktop PC (both parts)"
Name: "host"; Description: "Laptop only (shares its displays)"
Name: "client"; Description: "Desktop PC only (shows the laptop's displays)"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "app"; Description: "LANDock (tray icon, settings, automatic updates)"; Types: both host client custom; Flags: fixed
Name: "host"; Description: "Host: shares this computer's virtual displays (the laptop)"; Types: both host custom
Name: "client"; Description: "Client: shows the laptop's displays on this PC's monitors (the desktop PC)"; Types: both client custom

[Tasks]
Name: "startup"; Description: "Start LANDock when I sign in to Windows (it lives in the notification area)"
Name: "firewall"; Description: "Allow the LANDock host through the Windows Firewall on private networks (Windows asks for administrator permission once)"; Components: host; Check: not FirewallRuleExists

[Files]
Source: "{#StageDir}\app\LANDock.exe"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "{#StageDir}\host\*"; DestDir: "{app}\host"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: host
Source: "{#StageDir}\client\*"; DestDir: "{app}\client"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: client
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "{#RepoRoot}\NOTICE.md"; DestDir: "{app}"; Flags: ignoreversion; Components: app

[InstallDelete]
; the parts are replaced as a whole, so nothing of an older version is left behind (the in-app updater does the same)
Type: filesandordirs; Name: "{app}\host"; Components: host
Type: filesandordirs; Name: "{app}\client"; Components: client
Type: files; Name: "{app}\LANDock.exe.old"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\LANDock.exe"; Parameters: "--settings"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\LANDock.exe"" --tray"; Flags: uninsdeletevalue; Tasks: startup

[Run]
; one administrator prompt: replace the firewall rule for the host's program (private networks only)
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall delete rule name=""{#FirewallRule}"" & netsh advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow program=""{app}\host\bin\sunshine.exe"" profile=private enable=yes & netsh advfirewall firewall delete rule name=""{#ControlRule}"" & netsh advfirewall firewall add rule name=""{#ControlRule}"" dir=in action=allow program=""{app}\LANDock.exe"" profile=private enable=yes"; Verb: "runas"; Flags: runhidden shellexec waituntilterminated; Tasks: firewall; StatusMsg: "Allowing LANDock through the firewall..."
Filename: "{app}\LANDock.exe"; Parameters: "--tray"; Description: "Start LANDock"; Flags: nowait postinstall skipifsilent
Filename: "{app}\LANDock.exe"; Parameters: "--tray --after-update"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\host"
Type: filesandordirs; Name: "{app}\client"
Type: files; Name: "{app}\LANDock.exe.old"

[Code]
function FirewallRuleExists: Boolean;
var
  rc: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall show rule name="{#FirewallRule}"', '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  rc: Integer;
  dataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // the firewall rule is machine-wide: remove it (Windows asks for permission) when we added one
    if FirewallRuleExists then
      ShellExec('runas', ExpandConstant('{sys}\cmd.exe'), '/c netsh advfirewall firewall delete rule name="{#FirewallRule}" & netsh advfirewall firewall delete rule name="{#ControlRule}"', '', SW_HIDE, ewWaitUntilTerminated, rc);
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    dataDir := ExpandConstant('{localappdata}\LANDock');
    if DirExists(dataDir) and (not UninstallSilent) then
      if MsgBox('Also remove LANDock''s settings, pairing keys and logs?' + #13#10 + dataDir + #13#10#13#10 +
                'Keep them if you plan to install LANDock again: the pairing with your other computer then keeps working.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(dataDir, True, True, True);
  end;
end;
