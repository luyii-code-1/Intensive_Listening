#ifndef ReleaseDirectory
#error ReleaseDirectory must be supplied
#endif
#ifndef OutputDirectory
#error OutputDirectory must be supplied
#endif
[Setup]
AppId=IntensiveListening.Luyii
AppName=Intensive Listening 2 Resonance
AppVersion=2.1.0
AppPublisher=Luyii
DefaultDirName={autopf}\Intensive Listening
DisableDirPage=no
UsePreviousAppDir=yes
DefaultGroupName=Intensive Listening
UninstallDisplayIcon={app}\IL.App.exe
SetupIconFile=..\assets\installer_icon.ico
OutputDir={#OutputDirectory}
OutputBaseFilename=Intensive-Listening-2.1.0-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\assets\legal\eula_zh_cn.txt
[Files]
Source: "{#ReleaseDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Dirs]
Name: "{app}"; BeforeInstall: VerifyDirectoryCleanup
[InstallDelete]
Type: filesandordirs; Name: "{app}\*"
[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
[Icons]
Name: "{autoprograms}\Intensive Listening"; Filename: "{app}\IL.App.exe"
Name: "{autodesktop}\Intensive Listening"; Filename: "{app}\IL.App.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\IL.App.exe"; Description: "启动 Intensive Listening 2 Resonance"; Flags: nowait postinstall skipifsilent
[Code]
type
  TManagedGuid = record
    D1: LongWord;
    D2, D3: Word;
    D4: array[0..7] of Byte;
  end;
function CoCreateGuid(var Value: TManagedGuid): Integer;
  external 'CoCreateGuid@ole32.dll stdcall';
var
  ConfirmedCleanupDirectory: String;

function ManagedDataDirectory: String;
begin
  Result := ExpandConstant('{param:MDNDATA|{localappdata}\Intensive Listening\data}');
end;

function InitializeSetup: Boolean;
var Source, Target: String;
begin
  Result := True;
  Source := ExpandConstant('{src}\il_precfg.yaml');
  if FileExists(Source) then begin
    Target := AddBackslash(ManagedDataDirectory) + 'il_precfg.yaml';
    if not ForceDirectories(ManagedDataDirectory) or not FileCopy(Source, Target, False) then begin
      MsgBox('无法复制预设配置到应用数据目录。', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function ValidDeviceId(Value: String): Boolean;
var I: Integer;
begin
  Result := Length(Value) = 36;
  if not Result then Exit;
  for I := 1 to 36 do
    if (I = 9) or (I = 14) or (I = 19) or (I = 24) then begin
      if Value[I] <> '-' then Result := False;
    end else if Pos(Lowercase(Value[I]), '0123456789abcdef') = 0 then Result := False;
end;

procedure RecordDeviceIdentity;
var
  Value, Registered: String;
  FirstRegistration: Boolean;
  Saved: AnsiString;
  Device: TManagedGuid;
  DataPath: String;
begin
  DataPath := ManagedDataDirectory;
  if not ForceDirectories(DataPath) then RaiseException('无法建立应用数据目录。');
  Registered := '';
  RegQueryStringValue(HKLM64, 'Software\Intensive Listening', 'InstallUuid', Registered);
  if not ValidDeviceId(Registered) then RegQueryStringValue(HKLM32, 'Software\Intensive Listening', 'InstallUuid', Registered);
  if not ValidDeviceId(Registered) then RegQueryStringValue(HKCU, 'Software\Intensive Listening', 'InstallUuid', Registered);
  FirstRegistration := not ValidDeviceId(Registered);
  Value := '';
  if LoadStringFromFile(AddBackslash(DataPath) + 'device-id.txt', Saved) then Value := Trim(String(Saved));
  if not ValidDeviceId(Value) then RegQueryStringValue(HKLM64, 'Software\Intensive Listening', 'InstallUuid', Value);
  if not ValidDeviceId(Value) then RegQueryStringValue(HKLM32, 'Software\Intensive Listening', 'InstallUuid', Value);
  if not ValidDeviceId(Value) then RegQueryStringValue(HKCU, 'Software\Intensive Listening', 'InstallUuid', Value);
  if not ValidDeviceId(Value) then begin
    if CoCreateGuid(Device) <> 0 then RaiseException('无法生成设备 UUID。');
    Value := Lowercase(Format('%.8x-%.4x-%.4x-%.2x%.2x-%.2x%.2x%.2x%.2x%.2x%.2x', [Device.D1, Device.D2, Device.D3, Device.D4[0], Device.D4[1], Device.D4[2], Device.D4[3], Device.D4[4], Device.D4[5], Device.D4[6], Device.D4[7]]));
  end;
  if not SaveStringToFile(AddBackslash(DataPath) + 'device-id.txt', Value, False) then RaiseException('无法保存设备 UUID。');
  if not RegWriteStringValue(HKLM64, 'Software\Intensive Listening', 'InstallUuid', Value) then RaiseException('无法同步设备 UUID。');
  RegWriteStringValue(HKCU, 'Software\Intensive Listening', 'InstallUuid', Value);
  if FirstRegistration then Registered := 'full' else Registered := 'welcome';
  if not SaveStringToFile(AddBackslash(DataPath) + 'installation-onboarding.txt', Registered, False) then
    RaiseException('无法记录安装引导类型。');
end;

function NormalizedDirectory(Path: String): String;
begin
  Result := Lowercase(AddBackslash(ExpandFileName(Path)));
end;

function IsSameOrParentDirectory(Parent, Child: String): Boolean;
begin
  Result := Pos(NormalizedDirectory(Parent), NormalizedDirectory(Child)) = 1;
end;

function InstallationDirectoryError: String;
var
  Target, DataRoot: String;
begin
  Result := '';
  Target := ExpandConstant('{app}');
  DataRoot := ExpandConstant('{localappdata}\Intensive Listening');
  if (NormalizedDirectory(Target) = NormalizedDirectory(AddBackslash(ExtractFileDrive(Target)))) or
     IsSameOrParentDirectory(Target, ExpandConstant('{autopf}')) or
     IsSameOrParentDirectory(Target, ExpandConstant('{win}')) or
     IsSameOrParentDirectory(ExpandConstant('{win}'), Target) then
    Result := '请选择应用专用的安装文件夹。'
  else if IsSameOrParentDirectory(Target, DataRoot) or IsSameOrParentDirectory(DataRoot, Target) or IsSameOrParentDirectory(Target, ManagedDataDirectory) or IsSameOrParentDirectory(ManagedDataDirectory, Target) then
    Result := '此路径包含本机课程或设置数据，请选择单独的程序安装目录。';
end;

function DirectoryHasContents(Path: String): Boolean;
var
  Entry: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Path) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          Result := True;
          Break;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function IsVersionOneDirectory(Path: String): Boolean;
var
  Executable, Version: String;
begin
  Executable := AddBackslash(Path) + 'Intensive Listening.exe';
  Result := FileExists(Executable) and
    (FileExists(AddBackslash(Path) + 'flutter_windows.dll') or
      (GetVersionNumbersString(Executable, Version) and (Pos('1.', Version) = 1)));
end;

function ConfirmDirectoryCleanup: Boolean;
var
  Target, Version: String;
begin
  Target := ExpandConstant('{app}');
  Result := True;
  if not DirectoryHasContents(Target) or IsVersionOneDirectory(Target) then
    Exit;
  if ConfirmedCleanupDirectory = NormalizedDirectory(Target) then
    Exit;
  if WizardSilent and (ExpandConstant('{param:MDNUPDATE|0}') = '1') and
     FileExists(AddBackslash(Target) + 'unins000.exe') and
     GetVersionNumbersString(AddBackslash(Target) + 'IL.App.exe', Version) and
     (Pos('2.', Version) = 1) then Exit;
  if WizardSilent then begin
    Log('The nonempty installation directory requires interactive deletion approval: ' + Target);
    Result := False;
    Exit;
  end;
  Result := MsgBox('此目录不为空，需要先删除目录内的全部文件和子目录才能安装。' + #13#10 + #13#10 +
    Target + #13#10 + #13#10 + '是否在安装前清空此目录？', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  if Result then
    ConfirmedCleanupDirectory := NormalizedDirectory(Target);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then
    Exit;
  Error := InstallationDirectoryError;
  if Error <> '' then begin
    MsgBox(Error, mbError, MB_OK);
    Result := False;
  end else
    Result := ConfirmDirectoryCleanup;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := InstallationDirectoryError;
  if (Result = '') and not ConfirmDirectoryCleanup then
    Result := '安装前需要确认清空目标目录。请更换为空目录，或确认删除后继续安装。';
end;

procedure VerifyDirectoryCleanup;
begin
  if DirectoryHasContents(ExpandConstant('{app}')) then
    RaiseException('安装目录清理失败，请关闭占用该目录的程序后重新安装。');
end;

procedure ImportManagedConfiguration;
var
  Source, DataDirectory, Parameters: String;
  ResultCode: Integer;
begin
  Source := ExpandConstant('{src}\il_mdn_config.yaml');
  DataDirectory := ManagedDataDirectory;
  if FileExists(Source) then begin
    Parameters := '--import-mdn-config "' + Source + '" "' + DataDirectory + '"';
    if not Exec(ExpandConstant('{app}\IL.App.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException('无法检查并导入集控配置。');
    if ResultCode = 10 then Log('Organization management is locked; keeping existing configuration.')
    else if ResultCode <> 0 then RaiseException('无法导入集控配置，请检查 YAML 和现有组织缓存。');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    RecordDeviceIdentity;
    ImportManagedConfiguration;
  end;
  if CurStep = ssPostInstall then
    if not SaveStringToFile(AddBackslash(ManagedDataDirectory) + 'installation-cycle.txt',
      GetSHA256OfUnicodeString(GetDateTimeString('yyyymmddhhnnss', '-', ':') + ExpandConstant('{tmp}')), False) then
      RaiseException('Unable to record installation for first-run setup.');
end;
