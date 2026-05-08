; ============================================================
; SOC Tester — Inno Setup Script
; ICO Laboratory
; ============================================================

#define AppName      "SOC Tester"
#define AppVersion   "1.0.0"
#define AppPublisher "ICO Laboratory"
#define AppExe       "SOCTester.exe"

[Setup]
AppId={{A1B2C3D4-1111-2222-3333-444455556666}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=
AppSupportURL=
DefaultDirName={autopf}\SOCTester
DefaultGroupName={#AppName}
AllowNoIcons=yes
LicenseFile=..\license.txt
OutputDir=Publish
OutputBaseFilename=SOCTesterSetup
SetupIconFile=SOCTester\Assets\logo.ico
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
VersionInfoDescription=SOC Tester - Battery State-of-Charge Testing Tool
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
