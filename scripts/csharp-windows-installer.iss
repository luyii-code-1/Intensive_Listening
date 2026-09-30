#ifndef ReleaseDirectory
#error ReleaseDirectory must be supplied
#endif
#ifndef OutputDirectory
#error OutputDirectory must be supplied
#endif
[Setup]
AppId=IntensiveListening.2Preview.Luyii
AppName=Intensive Listening 2.0 Preview
AppVersion=2.0.0-dev
AppPublisher=Luyii
DefaultDirName={localappdata}\Programs\Intensive Listening 2.0
DefaultGroupName=Intensive Listening 2.0
UninstallDisplayIcon={app}\IL.App.exe
OutputDir={#OutputDirectory}
OutputBaseFilename=Intensive-Listening-2.0-dev-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\assets\legal\eula_zh_cn.txt
[Files]
Source: "{#ReleaseDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
[Icons]
Name: "{autoprograms}\Intensive Listening 2.0"; Filename: "{app}\IL.App.exe"
Name: "{autodesktop}\Intensive Listening 2.0"; Filename: "{app}\IL.App.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\IL.App.exe"; Description: "启动 Intensive Listening 2.0"; Flags: nowait postinstall skipifsilent
