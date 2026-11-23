; Inno Setup script for OptiGame+. Build with installer\build.ps1 (it passes AppVersion and DistDir).
; Per-user install (no administrator needed) into %LOCALAPPDATA%\Programs\OptiGame, which is where the in-app updater
; expects to find it. Your settings, backups and history live in %LOCALAPPDATA%\GameOpt and are never touched by
; installing, updating or uninstalling.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef DistDir
  #define DistDir "..\dist"
#endif

[Setup]
AppId={{7C1E3B52-5A0D-4E6B-9F55-2B6A0C3D8A11}
AppName=OptiGame+
AppVersion={#AppVersion}
AppVerName=OptiGame+ {#AppVersion}
AppPublisher=yourbirthcertificate
AppPublisherURL=https://github.com/yourbirthcertificate/game-optimizer
AppSupportURL=https://github.com/yourbirthcertificate/game-optimizer/issues
AppUpdatesURL=https://github.com/yourbirthcertificate/game-optimizer/releases
DefaultDirName={autopf}\OptiGame
DefaultGroupName=OptiGame+
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#DistDir}
OutputBaseFilename=OptiGame-Setup-{#AppVersion}
SetupIconFile=..\src\OptiGame.App\Assets\app.ico
UninstallDisplayIcon={app}\OptiGame.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
LicenseFile=..\LICENSE
WizardImageFile=assets\side.bmp
WizardSmallImageFile=assets\corner.bmp
InfoBeforeFile=before.txt
InfoAfterFile=after.txt
CloseApplications=yes
RestartApplications=no
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to OptiGame+
WelcomeLabel2=OptiGame+ {#AppVersion} finds the games on this PC, recommends graphics settings for each, and clears resources while you play.%n%nEvery change is previewed first, backed up, and can be undone. Your settings, backups and history are never touched by installing or updating.
FinishedHeadingLabel=OptiGame+ is ready

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start OptiGame+ with &Windows (hidden in the tray, so it can boost games automatically)"; Flags: unchecked

[Files]
Source: "whatsnew.txt"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#DistDir}\OptiGame.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DistDir}\optigame-cli.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CREDITS.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\OptiGame+"; Filename: "{app}\OptiGame.exe"
Name: "{autodesktop}\OptiGame+"; Filename: "{app}\OptiGame.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "OptiGame"; ValueData: """{app}\OptiGame.exe"" --tray"; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Normal install: offer to start it. Silent install (the in-app updater): start it again straight away.
Filename: "{app}\OptiGame.exe"; Description: "Start OptiGame+"; Flags: nowait postinstall skipifsilent
Filename: "{app}\OptiGame.exe"; Flags: nowait; Check: RelaunchAfterSilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\GameOpt\update"

[Code]
var
  Tips: array[0..3] of String;

procedure InitializeWizard;
var
  Notes: AnsiString;
  Page: TOutputMsgMemoWizardPage;
begin
  Tips[0] := 'Preview every change first. Nothing is written until you press Apply.';
  Tips[1] := 'Each Apply makes a backup. Undo is on the game''s page.';
  Tips[2] := 'Turn on Auto-boost on the Dashboard to clear resources when a game starts.';
  Tips[3] := 'Can this PC run it? on a game''s page checks it against your hardware.';

  ExtractTemporaryFile('whatsnew.txt');
  if LoadStringFromFile(ExpandConstant('{tmp}\whatsnew.txt'), Notes) then
  begin
    Page := CreateOutputMsgMemoPage(wpWelcome, 'What is new', 'Changes in version {#AppVersion}',
      'This is the changelog entry for this version.', String(Notes));
    Page.RichEditViewer.Font.Name := 'Segoe UI';
  end;
end;

{ Shows a different tip as the install moves along. Silent installs never show it. }
procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
var
  I: Integer;
begin
  if MaxProgress <= 0 then Exit;
  I := (CurProgress * 4) div (MaxProgress + 1);
  if I > 3 then I := 3;
  WizardForm.PageDescriptionLabel.Caption := 'Tip: ' + Tips[I];
end;

function RelaunchAfterSilent: Boolean;
begin
  { /NOLAUNCH=1 lets a test install run without starting the app. }
  Result := WizardSilent and (ExpandConstant('{param:NOLAUNCH|0}') <> '1');
end;
