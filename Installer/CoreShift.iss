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
russian.AppStillRunning=Файл CoreShift занят. В меню значка CoreShift в трее выберите «Выход», затем нажмите «Повторить».
english.AppStillRunning=CoreShift is in use. Choose Exit from the CoreShift tray icon menu, then click Retry.

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,SmartResourceDispatcher.exe,SmartResourceDispatcher.runtimeconfig.json,SmartResourceDispatcher.deps.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CoreShift"; Filename: "{app}\CoreShift.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\CoreShift"; Filename: "{app}\CoreShift.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\CoreShift.Desktop.exe"; Description: "{cm:LaunchProgram,CoreShift}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\CoreShift.Desktop.exe"; WorkingDir: "{app}"; Flags: nowait skipifnotsilent; Check: IsInAppUpdate

[Code]
const
  FileWriteData = $0002;
  ShareReadWriteDelete = $0007;
  OpenExisting = 3;
  ErrorSharingViolation = 32;
  ErrorLockViolation = 33;
  ProcessSetInformation = $0200;
  ProcessQueryLimitedInformation = $1000;

function OpenProcess(DesiredAccess: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function OpenFileForWrite(FileName: String; DesiredAccess, ShareMode: LongWord; SecurityAttributes: THandle;
  CreationDisposition, FlagsAndAttributes: LongWord; TemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function FindNextWindow(ParentWindow, PreviousWindow, ClassName: THandle; WindowName: String): THandle;
  external 'FindWindowExW@user32.dll stdcall';
function GetWindowThreadProcessId(Window: THandle; var ProcessId: LongWord): LongWord;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function QueryFullProcessImageName(Process: THandle; Flags: LongWord; ImagePath: String;
  var PathLength: LongWord): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';

function CanCloseCoreShift(const FileName: String): Boolean;
var
  Window, ProcessHandle: THandle;
  ProcessId, PathLength: LongWord;
  ImagePath: String;
  Matches: Boolean;
begin
  Result := False;
  Window := FindNextWindow(0, 0, 0, 'CoreShift');
  while Window <> 0 do
  begin
    if GetWindowThreadProcessId(Window, ProcessId) <> 0 then
    begin
      ProcessHandle := OpenProcess(ProcessQueryLimitedInformation, False, ProcessId);
      if ProcessHandle = 0 then
      begin
        Result := False;
        Exit;
      end;

      try
        PathLength := 32768;
        SetLength(ImagePath, PathLength);
        Matches := QueryFullProcessImageName(ProcessHandle, 0, ImagePath, PathLength) and
          SameText(Copy(ImagePath, 1, PathLength), FileName);
      finally
        CloseHandle(ProcessHandle);
      end;

      if Matches then
      begin
        { Leave graceful closure to Restart Manager if Setup has sufficient rights. }
        ProcessHandle := OpenProcess(ProcessSetInformation, False, ProcessId);
        Result := ProcessHandle <> 0;
        if not Result then
          Exit;
        CloseHandle(ProcessHandle);
      end;
    end;
    Window := FindNextWindow(0, Window, 0, 'CoreShift');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  FileName: String;
  FileHandle: THandle;
  ErrorCode: Integer;
begin
  Result := '';
  FileName := ExpandConstant('{app}\CoreShift.Desktop.exe');
  while FileExists(FileName) do
  begin
    FileHandle := OpenFileForWrite(FileName, FileWriteData, ShareReadWriteDelete, 0, OpenExisting, 0, 0);
    if FileHandle <> THandle(-1) then
    begin
      CloseHandle(FileHandle);
      Exit;
    end;

    ErrorCode := DLLGetLastError;
    if (ErrorCode <> ErrorSharingViolation) and (ErrorCode <> ErrorLockViolation) then
      Exit;
    if CanCloseCoreShift(FileName) then
      Exit;

    Log('CoreShift.Desktop.exe is still in use before file installation.');
    if SuppressibleMsgBox(CustomMessage('AppStillRunning'), mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := CustomMessage('AppStillRunning');
      Exit;
    end;
  end;
end;

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
