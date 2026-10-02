#ifndef ReleaseDirectory
#error ReleaseDirectory must be supplied
#endif
#ifndef OutputDirectory
#error OutputDirectory must be supplied
#endif
[Setup]
AppId=IntensiveListening.Luyii
AppName=Intensive Listening 2 Resonance
AppVersion=2.0.0
AppPublisher=Luyii
DefaultDirName={autopf}\Intensive Listening
DisableDirPage=no
UsePreviousAppDir=yes
DefaultGroupName=Intensive Listening
UninstallDisplayIcon={app}\IL.App.exe
SetupIconFile=..\assets\installer_icon.ico
OutputDir={#OutputDirectory}
OutputBaseFilename=Intensive-Listening-2.0.0-win-x64-Setup
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
var
  ConfirmedCleanupDirectory: String;

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
  else if IsSameOrParentDirectory(Target, DataRoot) or IsSameOrParentDirectory(DataRoot, Target) then
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
  Target: String;
begin
  Target := ExpandConstant('{app}');
  Result := True;
  if not DirectoryHasContents(Target) or IsVersionOneDirectory(Target) then
    Exit;
  if ConfirmedCleanupDirectory = NormalizedDirectory(Target) then
    Exit;
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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not SaveStringToFile(ExpandConstant('{app}\installation-id.txt'),
      GetSHA256OfUnicodeString(GetDateTimeString('yyyymmddhhnnss', '-', ':') + ExpandConstant('{tmp}')), False) then
      RaiseException('Unable to record installation for first-run setup.');
end;
