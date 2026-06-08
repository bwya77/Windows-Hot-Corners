; Hot Corners installer (Inno Setup). Built in CI, once per architecture:
;
;   ISCC /DAppVersion=<version> /DArch=x64|arm64 /DSourceDir=<publish dir> /O<out dir> installer\HotCorners.iss
;
; Installs the self-contained tray app into Program Files, adds a Start Menu shortcut,
; and registers a clean uninstaller. Launch-at-login stays app-owned (the tray app
; reconciles the HKCU Run key from its setting); the uninstaller removes that Run value
; so it never dangles after removal. On install, any prior per-user (%LocalAppData%)
; installation is stopped and removed so users coming from the old script-based install
; end up with a single canonical Program Files copy.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-" + Arch
#endif

[Setup]
AppId={{4A1D8E7B-6F94-4F2E-A1E1-3D6B9C2A1A6E}
AppName=Hot Corners
AppVersion={#AppVersion}
AppPublisher=Bradley Wyatt
AppPublisherURL=https://github.com/bwya77/Windows-Hot-Corners
AppSupportURL=https://github.com/bwya77/Windows-Hot-Corners/issues
AppUpdatesURL=https://github.com/bwya77/Windows-Hot-Corners/releases
DefaultDirName={autopf}\Hot Corners
DefaultGroupName=Hot Corners
DisableProgramGroupPage=yes
DisableDirPage=auto
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\HotCorners.exe
UninstallDisplayName=Hot Corners
OutputBaseFilename=HotCornersSetup-{#AppVersion}-win-{#Arch}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
; Close a running Hot Corners before replacing files so the in-app update can replace the
; locked executable. Relaunch is handled explicitly in [Run] (including silent installs),
; not via Restart Manager, which does not reliably restart the app after a silent update.
CloseApplications=yes
RestartApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
#endif

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Hot Corners"; Filename: "{app}\HotCorners.exe"
Name: "{autodesktop}\Hot Corners"; Filename: "{app}\HotCorners.exe"; Tasks: desktopicon

[Tasks]
Name: "startupwithwindows"; Description: "Start Hot Corners when I sign in to Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
; Launch after install. When "Start with Windows" was ticked, pass --enable-startup so the app
; persists the LaunchAtLogin setting and registers the HKCU Run key itself (the app stays the
; single owner of that key). runasoriginaluser drops admin so the tray app and its per-user
; settings run as the actual user, not the elevated installer account.
Filename: "{app}\HotCorners.exe"; Parameters: "--enable-startup"; Description: "Launch Hot Corners"; Tasks: startupwithwindows; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\HotCorners.exe"; Description: "Launch Hot Corners"; Tasks: not startupwithwindows; Flags: nowait postinstall skipifsilent runasoriginaluser
; Silent in-app update path: postinstall checkbox above is skipped under /SILENT and /VERYSILENT,
; so relaunch explicitly here. The app reconciles its own launch-at-login from settings on
; startup, so --enable-startup is only needed when the user opted in via the task (typically
; first install). runasoriginaluser returns to the invoking user since the installer runs elevated.
Filename: "{app}\HotCorners.exe"; Parameters: "--enable-startup"; Flags: nowait runasoriginaluser; Tasks: startupwithwindows; Check: WizardSilent
Filename: "{app}\HotCorners.exe"; Flags: nowait runasoriginaluser; Tasks: not startupwithwindows; Check: WizardSilent

[Code]
// Best-effort cleanup of the legacy per-user install at %LocalAppData%\Programs\HotCorners that
// the early install.ps1 script created. Stop the process there (if it's running from the user
// dir) and delete the folder so we end up with a single canonical Program Files install.
function InitializeSetup(): Boolean;
var
  oldUserDir: string;
  resultCode: Integer;
begin
  oldUserDir := ExpandConstant('{localappdata}\Programs\HotCorners');
  if DirExists(oldUserDir) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM HotCorners.exe', '', SW_HIDE, ewWaitUntilTerminated, resultCode);
    Sleep(400);
    DelTree(oldUserDir, True, True, True);
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Remove the app-owned "Start with Windows" entry so it doesn't point at a deleted exe
  // after uninstall. User settings under %APPDATA%\HotCorners are intentionally left in place.
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'HotCorners');
end;
