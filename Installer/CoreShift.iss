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
DisableWelcomePage=no
WizardImageFile=Artwork\Welcome.bmp
WizardSmallImageFile=Artwork\Mark.bmp
WizardImageStretch=yes
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
russian.WelcomeTitle=Установка CoreShift
english.WelcomeTitle=Install CoreShift
#ifdef ShifterDir
russian.WelcomeBody=Живые графики, диагностика Windows и Шифтер, локальный помощник со скриншотами.%n%nМодель Шифтера уже включена в установщик.
english.WelcomeBody=Live charts, Windows diagnostics, and Shifter, a local assistant that understands screenshots.%n%nShifter's model is included in Setup.
#else
russian.WelcomeBody=Живые графики, интерактивная схема ПК, профиль фокуса и диагностика Windows.%n%nДальше можно выбрать тему, язык и эффекты интерфейса.
english.WelcomeBody=Live charts, an interactive PC layout, focus profiles, and Windows diagnostics.%n%nChoose your theme, language, and visual effects in the next steps.
#endif
russian.FirstRunTitle=Первый запуск
english.FirstRunTitle=First launch
russian.FirstRunDescription=Выберите начальное поведение CoreShift
english.FirstRunDescription=Choose how CoreShift starts
russian.FirstRunHelp=Эти параметры можно изменить в приложении.
english.FirstRunHelp=You can change these options in CoreShift.
russian.OptionMinimized=Открывать CoreShift свёрнутым
english.OptionMinimized=Start CoreShift minimized
russian.OptionUpdates=Проверять обновления CoreShift
english.OptionUpdates=Check for CoreShift updates
russian.OptionLanguage=Язык CoreShift
english.OptionLanguage=CoreShift language
russian.OptionSystemLanguage=Как в Windows
english.OptionSystemLanguage=Follow Windows
russian.OptionTheme=Тема CoreShift
english.OptionTheme=CoreShift theme
russian.OptionDark=Тёмная
english.OptionDark=Dark
russian.OptionLight=Светлая
english.OptionLight=Light
russian.OptionReduceMotion=Запустить без анимации интерфейса
english.OptionReduceMotion=Start with interface animations off
russian.InstalledTitle=CoreShift готов к запуску
english.InstalledTitle=CoreShift is ready to launch
#ifdef ShifterDir
russian.ShifterRuntimeFailed=Для Шифтера нужен Microsoft Visual C++ Redistributable (x64). Разрешите его установку и повторите установку CoreShift.
english.ShifterRuntimeFailed=Shifter requires the Microsoft Visual C++ Redistributable (x64). Allow the prerequisite to install and retry CoreShift Setup.
russian.ShifterRuntimeRestart=Перезапустите Windows, чтобы завершить установку библиотек Шифтера, и снова запустите установщик CoreShift.
english.ShifterRuntimeRestart=Restart Windows to finish installing Shifter's runtime, then run CoreShift Setup again.
#endif

[Files]
#ifdef ShifterDir
Source: "{#ShifterDir}\vc_redist.x64.exe"; Flags: dontcopy
#endif
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,SmartResourceDispatcher.exe,SmartResourceDispatcher.runtimeconfig.json,SmartResourceDispatcher.deps.json"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef ShifterDir
Source: "{#ShifterDir}\*"; DestDir: "{app}\Shifter"; Excludes: "*.gguf,vc_redist.x64.exe"; Flags: ignoreversion
Source: "{#ShifterDir}\*.gguf"; DestDir: "{app}\Shifter"; Flags: ignoreversion nocompression solidbreak
#endif

[Icons]
Name: "{group}\CoreShift"; Filename: "{app}\CoreShift.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\CoreShift"; Filename: "{app}\CoreShift.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "CoreShift"; Flags: dontcreatekey uninsdeletevalue

[INI]
Filename: "{app}\first-run.ini"; Section: "CoreShift"; Key: "Language"; String: "{code:InitialLanguage}"; Flags: uninsdeleteentry; Check: WriteInitialOptions
Filename: "{app}\first-run.ini"; Section: "CoreShift"; Key: "StartMinimized"; String: "{code:InitialMinimized}"; Flags: uninsdeleteentry; Check: WriteInitialOptions
Filename: "{app}\first-run.ini"; Section: "CoreShift"; Key: "CheckUpdates"; String: "{code:InitialUpdates}"; Flags: uninsdeleteentry; Check: WriteInitialOptions
Filename: "{app}\first-run.ini"; Section: "CoreShift"; Key: "Theme"; String: "{code:InitialTheme}"; Flags: uninsdeleteentry; Check: WriteInitialOptions
Filename: "{app}\first-run.ini"; Section: "CoreShift"; Key: "ReduceMotion"; String: "{code:InitialMotion}"; Flags: uninsdeleteentry; Check: WriteInitialOptions

[Run]
Filename: "{app}\CoreShift.Desktop.exe"; Description: "{cm:LaunchProgram,CoreShift}"; Flags: nowait postinstall skipifsilent shellexec
Filename: "{app}\CoreShift.Desktop.exe"; WorkingDir: "{app}"; Flags: nowait skipifnotsilent shellexec; Check: IsInAppUpdate

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

#ifdef ShifterDir
function HasShifterRuntime: Boolean;
var
  RuntimeKey: String;
  Installed, Major, Minor: Cardinal;
begin
  RuntimeKey := 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
  Result := RegQueryDWordValue(HKLM64, RuntimeKey, 'Installed', Installed) and (Installed = 1) and
    RegQueryDWordValue(HKLM64, RuntimeKey, 'Major', Major) and
    RegQueryDWordValue(HKLM64, RuntimeKey, 'Minor', Minor) and
    ((Major > 14) or ((Major = 14) and (Minor >= 51))) and
    FileExists(ExpandConstant('{sys}\msvcp140.dll')) and
    FileExists(ExpandConstant('{sys}\vcruntime140_1.dll'));
end;

function PrepareShifterRuntime(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if HasShifterRuntime then
    Exit;

  ExtractTemporaryFile('vc_redist.x64.exe');
  if not ShellExec('runas', ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '',
                   SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then
    Result := CustomMessage('ShifterRuntimeFailed')
  else if ExitCode = 3010 then
  begin
    NeedsRestart := True;
    Result := CustomMessage('ShifterRuntimeRestart');
  end
  else if not HasShifterRuntime then
  begin
    Log('Visual C++ Redistributable returned ' + IntToStr(ExitCode));
    Result := CustomMessage('ShifterRuntimeFailed');
  end;
end;
#endif

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
      Break;
    end;

    ErrorCode := DLLGetLastError;
    if (ErrorCode <> ErrorSharingViolation) and (ErrorCode <> ErrorLockViolation) then
      Break;
    if CanCloseCoreShift(FileName) then
      Break;

    Log('CoreShift.Desktop.exe is still in use before file installation.');
    if SuppressibleMsgBox(CustomMessage('AppStillRunning'), mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := CustomMessage('AppStillRunning');
      Exit;
    end;
  end;
#ifdef ShifterDir
  Result := PrepareShifterRuntime(NeedsRestart);
#endif
end;

function IsInAppUpdate: Boolean;
begin
  Result := StrToIntDef(ExpandConstant('{param:CORESHIFTUPDATEPID|}'), 0) > 0;
end;

var
  FirstRunPage: TInputOptionWizardPage;
  LanguageBox: TNewComboBox;
  ThemeBox: TNewComboBox;
  SaveInitialOptions: Boolean;

procedure InitializeWizard;
var
  LanguageLabel, ThemeLabel: TNewStaticText;
begin
  WizardForm.WelcomeLabel1.Caption := CustomMessage('WelcomeTitle');
  WizardForm.WelcomeLabel2.Caption := CustomMessage('WelcomeBody');
  WizardForm.FinishedHeadingLabel.Caption := CustomMessage('InstalledTitle');

  FirstRunPage := CreateInputOptionPage(wpSelectTasks, CustomMessage('FirstRunTitle'),
    CustomMessage('FirstRunDescription'), CustomMessage('FirstRunHelp'), False, False);
  FirstRunPage.Add(CustomMessage('OptionMinimized'));
  FirstRunPage.Add(CustomMessage('OptionUpdates'));
  FirstRunPage.Add(CustomMessage('OptionReduceMotion'));
  FirstRunPage.Values[1] := True;
  FirstRunPage.CheckListBox.Height := ScaleY(78);

  LanguageLabel := TNewStaticText.Create(FirstRunPage);
  LanguageLabel.Parent := FirstRunPage.Surface;
  LanguageLabel.Top := FirstRunPage.CheckListBox.Top + FirstRunPage.CheckListBox.Height + ScaleY(12);
  LanguageLabel.Caption := CustomMessage('OptionLanguage');

  LanguageBox := TNewComboBox.Create(FirstRunPage);
  LanguageBox.Parent := FirstRunPage.Surface;
  LanguageBox.Style := csDropDownList;
  LanguageBox.Top := LanguageLabel.Top + LanguageLabel.Height + ScaleY(5);
  LanguageBox.Width := ScaleX(185);
  LanguageBox.Items.Add('Русский');
  LanguageBox.Items.Add('English');
  LanguageBox.Items.Add(CustomMessage('OptionSystemLanguage'));
  if ActiveLanguage = 'russian' then
    LanguageBox.ItemIndex := 0
  else
    LanguageBox.ItemIndex := 1;

  ThemeLabel := TNewStaticText.Create(FirstRunPage);
  ThemeLabel.Parent := FirstRunPage.Surface;
  ThemeLabel.Left := LanguageBox.Left + LanguageBox.Width + ScaleX(18);
  ThemeLabel.Top := LanguageLabel.Top;
  ThemeLabel.Caption := CustomMessage('OptionTheme');

  ThemeBox := TNewComboBox.Create(FirstRunPage);
  ThemeBox.Parent := FirstRunPage.Surface;
  ThemeBox.Left := ThemeLabel.Left;
  ThemeBox.Top := LanguageBox.Top;
  ThemeBox.Width := LanguageBox.Width;
  ThemeBox.Style := csDropDownList;
  ThemeBox.Items.Add(CustomMessage('OptionDark'));
  ThemeBox.Items.Add(CustomMessage('OptionLight'));
  ThemeBox.Items.Add(CustomMessage('OptionSystemLanguage'));
  ThemeBox.ItemIndex := 0;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = FirstRunPage.ID) and
    (IsInAppUpdate or FileExists(AddBackslash(WizardDirValue) + 'CoreShift.Desktop.exe'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    SaveInitialOptions := not IsInAppUpdate and
      not FileExists(AddBackslash(WizardDirValue) + 'CoreShift.Desktop.exe');
end;

function WriteInitialOptions: Boolean;
begin
  Result := SaveInitialOptions;
end;

function InitialLanguage(Param: String): String;
begin
  Result := IntToStr(LanguageBox.ItemIndex);
end;

function InitialTheme(Param: String): String;
begin
  Result := IntToStr(ThemeBox.ItemIndex);
end;

function InitialMotion(Param: String): String;
begin
  if FirstRunPage.Values[2] then Result := '1' else Result := '0';
end;

function InitialMinimized(Param: String): String;
begin
  if FirstRunPage.Values[0] then Result := '1' else Result := '0';
end;

function InitialUpdates(Param: String): String;
begin
  if FirstRunPage.Values[1] then Result := '1' else Result := '0';
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
