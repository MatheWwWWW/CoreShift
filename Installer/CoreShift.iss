#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #error PublishDir must point to the self-contained Windows x64 publish directory.
#endif

[Setup]
AppId={{998EEA5D-ED30-49E3-A992-FF2AC2A9D7BB}
AppName=CoreShift
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
DefaultDirName={localappdata}\Programs\CoreShift
DefaultGroupName=CoreShift
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\CoreShift.Desktop.exe
SetupIconFile=..\Desktop\CoreShift.ico
OutputBaseFilename=CoreShift-Setup-{#AppVersion}-x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.14393
PrivilegesRequired=lowest
WizardStyle=modern dark
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,SmartResourceDispatcher.exe,SmartResourceDispatcher.runtimeconfig.json,SmartResourceDispatcher.deps.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CoreShift"; Filename: "{app}\CoreShift.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\CoreShift"; Filename: "{app}\CoreShift.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\CoreShift.Desktop.exe"; Description: "{cm:LaunchProgram,CoreShift}"; Flags: nowait postinstall skipifsilent
