; Inno Setup 6 script for OmniSpec.
;
; Build (from the repository root, after `dotnet publish` to publish\win-x64):
;   iscc /DAppVersion=1.0.0 /DSourceDir=<absolute publish dir> /DOutputDir=<absolute output dir> installer\OmniSpec.iss
;
; Relative paths below are resolved relative to this script's folder.
; This installer copies OmniSpec's files, creates shortcuts and optionally a per-user startup entry and a scheduled task
; that starts OmniSpec with administrator rights without a UAC prompt ("Always run as administrator").
; It never installs drivers or services.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "OmniSpec"
#define AppPublisher "OmniSpec contributors"
#define AppExeName "OmniSpec.exe"

[Setup]
AppId={{8C3F6A9E-2B1D-4E7A-9F3C-5D2E1A0B7C44}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; OmniSpec is installed under Program Files (administrator-only writable). This is required for the optional
; auto-elevation task: a task that runs with highest privileges must never point at a user-replaceable file.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=OmniSpec-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\OmniSpec.App\Assets\omnispec.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start OmniSpec when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; PawnIO sensor driver setup (GPL-2.0, unmodified, by namazso; source: https://github.com/namazso/PawnIO).
; Placed in installer\redist by the release workflow after SHA-256 verification. Installed silently only if missing.
Source: "redist\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: PawnIoMissing
Source: "redist\PawnIO-COPYING.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Per-user autostart entry (HKCU), removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: startup

[Run]
; The PawnIO sensor driver is part of OmniSpec's setup: installed silently (no extra window) when not already present.
; It is left installed on uninstall because other monitoring tools share it.
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "-install -silent"; StatusMsg: "Installing the sensor driver..."; Flags: runhidden waituntilterminated skipifdoesntexist; Check: PawnIoMissing
; Registers the scheduled task "\OmniSpec\OmniSpec (administrator)" for the installing user (runs elevated as part of setup).
Filename: "{app}\{#AppExeName}"; Parameters: "--register-elevated-task --exit"; StatusMsg: "Configuring full sensor access..."; Flags: runhidden waituntilterminated
; Launches as the original (non-elevated) user; OmniSpec then hands off to the elevated task if it is registered.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""\OmniSpec\OmniSpec (administrator)"" /f"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveElevatedTask"

[Code]
function PawnIoMissing: Boolean;
begin
  { PawnIO registers itself under this key (the same check LibreHardwareMonitor uses). }
  Result := not RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO');
end;