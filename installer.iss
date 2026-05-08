; ============================================================
; BMS Monitor — Inno Setup Script
; ICO Laboratory
; ============================================================

#define AppName      "BMS Monitor"
#define AppVersion   "1.0.1"
#define AppPublisher "ICO Laboratory"
#define AppExe       "BMSMonitor.exe"

[Setup]
AppId={{B5C6D7E8-2222-3333-4444-555566667777}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=
AppSupportURL=
DefaultDirName={autopf}\BMSMonitor
DefaultGroupName={#AppName}
AllowNoIcons=yes
LicenseFile=license.txt
OutputDir=Publish
OutputBaseFilename=BMSMonitorSetup
SetupIconFile=BMSMonitor\Assets\logo.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
PrivilegesRequired=admin
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=BMS Monitor - Cell voltages, SOC, temperatures and balancing
VersionInfoCopyright=Copyright (C) 2026 ICO Laboratory

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Buat shortcut di &Desktop"; GroupDescription: "Shortcut tambahan:"

[Files]
; Aplikasi utama
Source: "Publish\AppFiles\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Certificate ICO Laboratory — di-extract ke temp, install sebelum file utama
Source: "ico_lab.cer"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Jalankan {#AppName} sekarang"; Flags: nowait postinstall skipifsilent

; ============================================================
; Install ICO Laboratory certificate ke Trusted Root
; Dijalankan sebelum file-file app dicopy (ssInstall step)
; Karena installer berjalan sebagai admin, certutil dapat
; menulis ke LocalMachine\Root tanpa prompt tambahan.
; ============================================================
[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  CerPath: String;
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    ExtractTemporaryFile('ico_lab.cer');
    CerPath := ExpandConstant('{tmp}\ico_lab.cer');
    Exec(
      'certutil.exe',
      '-addstore Root "' + CerPath + '"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode
    );
  end;
end;
