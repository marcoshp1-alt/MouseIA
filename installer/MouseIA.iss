; MouseIA installer (Inno Setup 6). Installs per user: no administrator rights needed.
#define AppName "MouseIA"
#ifndef AppVersion
  #define AppVersion "1.3.2"
#endif

[Setup]
AppId={{7C1B2F7E-4B8A-4E7B-9C1F-6D2A9E5B3A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Odorizzi
AppPublisherURL=https://www.youtube.com/@odorizzioficial
AppSupportURL=https://github.com/odorizzioficial
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=MouseIA-Setup-{#AppVersion}
SetupIconFile=..\src\MouseIA\Assets\MouseIA.ico
UninstallDisplayIcon={app}\MouseIA.exe
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=force
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\MouseIA.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\MouseIA.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\MouseIA.exe"; Tasks: desktopicon

[Registry]
; The app adds itself to startup; remove that entry when uninstalling.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "MouseIA"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\MouseIA.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Automatic updates install silently: open MouseIA again afterwards.
Filename: "{app}\MouseIA.exe"; Parameters: "--updated"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM MouseIA.exe /F"; Flags: runhidden; RunOnceId: "StopMouseIA"
