; Inno Setup 6 script for StaScan.
; Build with build.ps1, or: ISCC.exe /DAppVersion=1.1.0 installer\StaScan.iss (after publishing dist\StaScan.exe).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6F1C2A57-3B9E-4C1D-9E7A-5A2B8C4D0E11}
AppName=StaScan
AppVersion={#AppVersion}
AppVerName=StaScan {#AppVersion}
AppPublisher=turq-co
AppPublisherURL=https://github.com/turq-co/stascan
AppSupportURL=https://github.com/turq-co/stascan/issues
AppUpdatesURL=https://github.com/turq-co/stascan/releases
DefaultDirName={autopf}\StaScan
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\LICENSE
SetupIconFile=..\assets\StaScan.ico
UninstallDisplayIcon={app}\StaScan.exe
UninstallDisplayName=StaScan
OutputDir=..\dist
OutputBaseFilename=StaScan-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "autostart"; Description: "Start StaScan when I sign in to Windows (recommended)"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\dist\StaScan.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\StaScan"; Filename: "{app}\StaScan.exe"; Comment: "Get your PC ready for gaming"
Name: "{autodesktop}\StaScan"; Filename: "{app}\StaScan.exe"; Tasks: desktopicon

[Run]
; the sign-in task is created for the user running the installer
Filename: "{app}\StaScan.exe"; Parameters: "--enable-autostart"; Tasks: autostart; Flags: runhidden waituntilterminated; StatusMsg: "Setting up autostart..."
Filename: "{app}\StaScan.exe"; Parameters: "--disable-autostart"; Tasks: not autostart; Flags: runhidden waituntilterminated
Filename: "{app}\StaScan.exe"; Description: "Launch StaScan"; Flags: postinstall nowait skipifsilent shellexec

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im StaScan.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopStaScan"
; restores background services StaScan paused and removes its scheduled tasks
Filename: "{app}\StaScan.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "CleanupStaScan"

[Code]
// Stop a running StaScan before files are replaced during an upgrade.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im StaScan.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
