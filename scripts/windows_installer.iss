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

function CoCreateGuid(var Guid: TGUID): Integer;
  external 'CoCreateGuid@ole32.dll stdcall';

function StringFromGUID2(var Guid: TGUID; GuidString: String; MaxChars: Integer): Integer;
  external 'StringFromGUID2@ole32.dll stdcall';

procedure CurStepChanged(CurStep: TSetupStep);
var
  Guid: TGUID;
  Cycle: String;
  Length: Integer;
begin
  if CurStep = ssPostInstall then begin
    SHChangeNotify($08000000, 0, 0, 0);
    if CoCreateGuid(Guid) <> 0 then
      RaiseException('Unable to create the installation cycle ID.');
    SetLength(Cycle, 40);
    Length := StringFromGUID2(Guid, Cycle, 40);
    if Length < 2 then
      RaiseException('Unable to format the installation cycle ID.');
    SetLength(Cycle, Length - 1);
    if not RegWriteStringValue(HKEY_LOCAL_MACHINE,
      'Software\Intensive Listening', 'InstallCycle', Cycle) then
      RaiseException('Unable to save the installation cycle ID.');
  end;
end;
