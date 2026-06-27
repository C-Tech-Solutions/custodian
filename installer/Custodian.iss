#define MyAppName "Custodian Disk Analyzer"
#define MyAppVersion "1.5.0"
#define MyAppPublisher "Custodian"
#define PublishDir "..\artifacts\portable\Custodian"

[Setup]
AppId={{8B0648E4-6BC2-4CA6-9FD6-2752E46CC62F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Custodian
DefaultGroupName=Custodian
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=CustodianSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Custodian Disk Analyzer"; Filename: "{app}\Custodian.App.exe"
Name: "{group}\Custodian CLI"; Filename: "{app}\cli\Custodian.Cli.exe"
Name: "{group}\Custodian TUI"; Filename: "{app}\tui\Custodian.Tui.exe"
Name: "{autodesktop}\Custodian Disk Analyzer"; Filename: "{app}\Custodian.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\Custodian.App.exe"; Description: "Launch Custodian"; Flags: nowait postinstall skipifsilent
