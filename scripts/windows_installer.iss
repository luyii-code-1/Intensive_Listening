#ifndef AppVersion
  #error AppVersion must be supplied by the packaging script.
#endif
#ifndef ReleaseDirectory
  #error ReleaseDirectory must be supplied by the packaging script.
#endif

[Setup]
AppId=IntensiveListening.Luyii
AppName=Intensive Listening
AppVersion={#AppVersion}
AppPublisher=Luyii
DefaultDirName={autopf}\Intensive Listening
DisableDirPage=no
UsePreviousAppDir=yes
DefaultGroupName=Intensive Listening
UninstallDisplayIcon={app}\Intensive Listening.exe
OutputBaseFilename=Intensive Listening-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\assets\legal\eula_zh_cn.txt
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#ReleaseDirectory}\*"; DestDir: "{app}"; Excludes: "\mcp\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\mcp\IntensiveListening.Mcp.exe"

[Registry]
Root: HKLM; Subkey: "Software\Intensive Listening"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Intensive Listening"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"; Flags: uninsdeletekey

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Icons]
Name: "{autoprograms}\Intensive Listening"; Filename: "{app}\Intensive Listening.exe"
Name: "{autodesktop}\Intensive Listening"; Filename: "{app}\Intensive Listening.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Intensive Listening.exe"; Description: "启动 Intensive Listening"; Flags: nowait postinstall skipifsilent

[Code]
procedure SHChangeNotify(wEventId: Integer; uFlags: Integer; dwItem1: Integer; dwItem2: Integer);
  external 'SHChangeNotify@shell32.dll stdcall';

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SHChangeNotify($08000000, 0, 0, 0);
end;
