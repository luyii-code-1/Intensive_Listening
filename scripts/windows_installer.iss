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
  InstallUuid: String;
  MarkerDirectory: String;
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
    if (not RegQueryStringValue(HKEY_LOCAL_MACHINE,
      'Software\Intensive Listening', 'InstallUuid', InstallUuid)) or
       (InstallUuid = '') then begin
      if CoCreateGuid(Guid) <> 0 then
        RaiseException('Unable to create the installation UUID.');
      SetLength(InstallUuid, 40);
      Length := StringFromGUID2(Guid, InstallUuid, 40);
      if Length < 2 then
        RaiseException('Unable to format the installation UUID.');
      SetLength(InstallUuid, Length - 1);
      if not RegWriteStringValue(HKEY_LOCAL_MACHINE,
        'Software\Intensive Listening', 'InstallUuid', InstallUuid) then
        RaiseException('Unable to save the installation UUID.');
    end;
    MarkerDirectory := ExpandConstant('{localappdata}\Intensive Listening\data');
    if not ForceDirectories(MarkerDirectory) or
       not SaveStringToFile(MarkerDirectory + '\installed.lock', Cycle, False) then
      RaiseException('Unable to save the installation completion marker.');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataRoot: String;
  DataDirectory: String;
  Association: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if RegQueryStringValue(HKEY_CURRENT_USER, 'Software\Classes\.ilp',
      '', Association) and (Association = 'IntensiveListening.ilp') then begin
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER,
      'Software\Classes\IntensiveListening.ilp');
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\.ilp');
  end;
  DataRoot := ExpandConstant('{localappdata}\Intensive Listening');
  DataDirectory := DataRoot + '\data';
  if MsgBox('是否保留本机课程、制作工程和设置数据？',
      mbConfirmation, MB_YESNO) = IDNO then begin
    DelTree(DataRoot, True, True, True);
    Exit;
  end;
  if MsgBox('是否保留 MCP 配置和已批准的智能体 UUID？' + #13#10 +
      '外部 AI 客户端中的 MCP 配置需由你在客户端中移除。',
      mbConfirmation, MB_YESNO) = IDNO then
    DelTree(DataDirectory + '\mcp', True, True, True);
end;
