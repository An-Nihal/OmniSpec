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
Name: "adminmode"; Description: "Always run OmniSpec as administrator (full sensor access, no UAC prompt on each start)"; GroupDescription: "Full sensor access:"
Name: "pawnio"; Description: "Install the PawnIO sensor driver (CPU temperature, power, fans and voltages) - downloaded from its official release"; GroupDescription: "Full sensor access:"; Check: PawnIoMissing

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Per-user autostart entry (HKCU), removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: startup

[Run]
; PawnIO (GPL-2.0, by namazso) is not redistributed: it is downloaded from the official release during setup
; (see [Code]), verified by SHA-256, and installed only if the user kept the "pawnio" task selected.
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "-install"; StatusMsg: "Installing the PawnIO sensor driver..."; Flags: waituntilterminated skipifdoesntexist; Tasks: pawnio
; Registers the scheduled task "\OmniSpec\OmniSpec (administrator)" for the installing user (runs elevated as part of setup).
Filename: "{app}\{#AppExeName}"; Parameters: "--register-elevated-task --exit"; StatusMsg: "Configuring automatic administrator mode..."; Flags: runhidden waituntilterminated; Tasks: adminmode
; Remove a previously registered task if the option was deselected on reinstall/upgrade.
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""\OmniSpec\OmniSpec (administrator)"" /f"; Flags: runhidden waituntilterminated; Tasks: not adminmode
; Launches as the original (non-elevated) user; OmniSpec then hands off to the elevated task if it is registered.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""\OmniSpec\OmniSpec (administrator)"" /f"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveElevatedTask"

[Code]
const
  PawnIoUrl = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe';
  PawnIoSha256 = '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032';

var
  DownloadPage: TDownloadWizardPage;

function PawnIoMissing: Boolean;
begin
  { PawnIO registers itself as an installed program; the device exists once its driver is installed. }
  Result := not RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO');
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading the PawnIO sensor driver...', @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('pawnio') then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(PawnIoUrl, 'PawnIO_setup.exe', PawnIoSha256);
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if not DownloadPage.AbortedByUser then
          SuppressibleMsgBox('PawnIO could not be downloaded (' + GetExceptionMessage + '). OmniSpec will be installed without it; you can re-run setup later to add it.',
            mbInformation, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;
