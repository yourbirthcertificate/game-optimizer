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
LicenseFile=..\LICENSE
InfoBeforeFile=before.txt
InfoAfterFile=after.txt
CloseApplications=yes
RestartApplications=no
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start OptiGame+ with &Windows (hidden in the tray, so it can boost games automatically)"; Flags: unchecked

[Files]
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
function RelaunchAfterSilent: Boolean;
begin
  { /NOLAUNCH=1 lets a test install run without starting the app. }
  Result := WizardSilent and (ExpandConstant('{param:NOLAUNCH|0}') <> '1');
end;
