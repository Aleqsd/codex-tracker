#ifndef AppVersion
  #define AppVersion "0.7.0"
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef ArtifactDir
  #define ArtifactDir "..\artifacts"
#endif
#ifdef TestBuild
  #define AppIdValue "CodexTracker.InstallerTest"
  #define AppMutexValue "Local\CodexTracker.InstallerTest"
  #define OutputName "CodexTracker-" + AppVersion + "-TestSetup"
  #define AppFolder "CodexTrackerInstallerTest"
#else
  #define AppIdValue "{{C84B03F8-B16D-4A0C-9249-148C613C3581}"
  #define AppMutexValue "Local\CodexTracker"
  #define OutputName "CodexTracker-" + AppVersion + "-Setup"
  #define AppFolder "CodexTracker"
#endif

[Setup]
AppId={#AppIdValue}
AppName=Codex Tracker
AppVersion={#AppVersion}
AppPublisher=Aleqsd
AppPublisherURL=https://github.com/Aleqsd/codex-tracker
AppSupportURL=https://github.com/Aleqsd/codex-tracker/issues
AppUpdatesURL=https://github.com/Aleqsd/codex-tracker/releases
DefaultDirName={localappdata}\Programs\{#AppFolder}
DefaultGroupName=Codex Tracker
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#ArtifactDir}
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic windows11 hidebevels
WizardSizePercent=125
DefaultDialogFontName=Segoe UI
WizardImageFile=Assets\Welcome.bmp
WizardSmallImageFile=Assets\Mark.bmp
WizardImageFileDynamicDark=Assets\Welcome.bmp
WizardSmallImageFileDynamicDark=Assets\Mark.bmp
WizardImageBackColor=$181818
WizardImageBackColorDynamicDark=$181818
WizardImageStretch=yes
DisableWelcomePage=no
DisableDirPage=yes
DisableReadyPage=yes
DisableFinishedPage=no
UninstallDisplayIcon={app}\CodexTracker.exe
SetupIconFile=..\src\CodexTracker.App\Assets\App.ico
CloseApplications=no
RestartApplications=no
UninstallLogMode=append
UsePreviousTasks=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
french.WelcomeLabel1=Bienvenue dans%nCodex Tracker
french.WelcomeLabel2=Vos comptes, vos quotas et vos prochains resets.%nAccessibles en un clic, près de l’horloge.%n%nL’installation prend quelques instants et ne nécessite pas de droits administrateur.%n%nVos comptes et réglages existants sont conservés.
french.FinishedHeadingLabel=Tout est prêt.
french.WizardSelectTasks=Un dernier réglage
french.SelectTasksDesc=Retrouvez Codex Tracker comme vous le souhaitez.
french.SelectTasksLabel2=Ce choix reste modifiable à tout moment dans les réglages de l’application.
french.FinishedLabel=Codex Tracker est installé.%n%nRetrouvez l’icône près de l’horloge pour consulter vos quotas et configurer vos rappels.%n%nVous pouvez rendre cette icône toujours visible depuis les paramètres de la barre des tâches Windows.
english.WelcomeLabel1=Welcome to%nCodex Tracker
english.WelcomeLabel2=Your accounts, quotas and upcoming resets.%nOne click away, next to the clock.%n%nSetup takes a moment and does not require administrator rights.%n%nYour existing accounts and preferences are preserved.
english.FinishedHeadingLabel=You’re all set.
english.WizardSelectTasks=One last preference
english.SelectTasksDesc=Keep Codex Tracker close at hand.
english.SelectTasksLabel2=You can change this preference later in the application settings.
english.FinishedLabel=Codex Tracker is installed.%n%nFind the tray icon next to the clock to see your quotas and configure reminders.%n%nYou can keep the icon visible from Windows taskbar settings.

[Tasks]
Name: "startup"; Description: "Lancer Codex Tracker à l’ouverture de ma session Windows"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
#ifndef TestBuild
Name: "{autoprograms}\Codex Tracker"; Filename: "{app}\CodexTracker.exe"
#endif

[Registry]
#ifndef TestBuild
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CodexTracker"; ValueData: """{app}\CodexTracker.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue
#endif

[Run]
Filename: "{app}\CodexTracker.exe"; Parameters: "--background"; Description: "Ouvrir Codex Tracker"; Flags: nowait postinstall skipifsilent

[Code]
procedure InitializeWizard();
begin
  WizardForm.WelcomeLabel1.Font.Size := 20;
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];
  WizardForm.FinishedHeadingLabel.Font.Size := 20;
  WizardForm.FinishedHeadingLabel.Font.Style := [fsBold];
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectTasks then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
end;

function CloseTracker(): Boolean;
var
  ExitCode: Integer;
  Attempt: Integer;
  Executable: String;
begin
  Result := True;
  if not CheckForMutexes('{#AppMutexValue}') then exit;
  Executable := ExpandConstant('{app}\CodexTracker.exe');
  if FileExists(Executable) then
    Exec(Executable, '--exit', ExpandConstant('{app}'), SW_HIDE, ewNoWait, ExitCode);
  for Attempt := 1 to 150 do
  begin
    if not CheckForMutexes('{#AppMutexValue}') then exit;
    Sleep(100);
  end;
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not CloseTracker() then
    Result := 'Codex Tracker est encore ouvert. Quittez-le depuis son icône près de l’horloge, puis réessayez.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseTracker();
  if not Result then
    MsgBox('Quittez Codex Tracker depuis son icône près de l’horloge avant de le désinstaller.', mbError, MB_OK);
end;

// Deliberately no UninstallDelete entry: private data under LocalAppData\CodexTracker is preserved.
