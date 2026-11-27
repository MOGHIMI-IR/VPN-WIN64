#define MyAppName "MIX-MOGHIMI"
#define MyAppVersion "0.1.0-beta.2"
#define MyAppPublisher "MOGHIMI"
#define MyAppExeName "MIX-MOGHIMI.exe"

[Setup]
AppId={{B24F49C8-9298-41F4-A7DF-1B3D894DCB4A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\MIX-MOGHIMI
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\artifacts
OutputBaseFilename=MIX-MOGHIMI-Setup-x64
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
VersionInfoVersion=0.1.0.2
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Windows x64 Installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\MIX-MOGHIMI-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MIX-MOGHIMI"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\MIX-MOGHIMI"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch MIX-MOGHIMI"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--restore-proxy"; Flags: runhidden waituntilterminated skipifdoesntexist
