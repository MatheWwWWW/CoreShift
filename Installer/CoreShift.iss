#ifndef AppVersion
  #error AppVersion must match the version of the published CoreShift application.
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

[CustomMessages]
russian.UpdateWaitFailed=Не удалось дождаться выхода CoreShift. Закройте приложение через меню значка в трее и повторите установку.
english.UpdateWaitFailed=CoreShift did not exit. Close it from the tray menu and try installing again.

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,SmartResourceDispatcher.exe,SmartResourceDispatcher.runtimeconfig.json,SmartResourceDispatcher.deps.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CoreShift"; Filename: "{app}\CoreShift.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\CoreShift"; Filename: "{app}\CoreShift.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\CoreShift.Desktop.exe"; Description: "{cm:LaunchProgram,CoreShift}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\CoreShift.Desktop.exe"; WorkingDir: "{app}"; Flags: nowait skipifnotsilent; Check: IsInAppUpdate

[Code]
function OpenProcess(DesiredAccess: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function IsInAppUpdate: Boolean;
begin
  Result := StrToIntDef(ExpandConstant('{param:CORESHIFTUPDATEPID|}'), 0) > 0;
end;

function InitializeSetup: Boolean;
var
  ProcessHandle: THandle;
begin
  Result := True;
  if not IsInAppUpdate then
    Exit;

  { Wait until the previous version releases its executable and DLLs. }
  ProcessHandle := OpenProcess($00100000, False, StrToIntDef(ExpandConstant('{param:CORESHIFTUPDATEPID|}'), 0));
  if ProcessHandle <> 0 then
  begin
    try
      Result := WaitForSingleObject(ProcessHandle, 30000) = 0;
    finally
      CloseHandle(ProcessHandle);
    end;
    if not Result then
      SuppressibleMsgBox(CustomMessage('UpdateWaitFailed'), mbError, MB_OK, IDOK);
  end;
end;
