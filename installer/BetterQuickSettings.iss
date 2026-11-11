; Release parameters are supplied by scripts/package.ps1.
#ifndef AppVersion
  #error AppVersion must be supplied with /DAppVersion
#endif
#ifndef FileVersion
  #error FileVersion must be supplied with /DFileVersion
#endif
#ifndef PublishDir
  #error PublishDir must point to a self-contained publish directory
#endif
#ifndef ReleaseDir
  #error ReleaseDir must be supplied with /DReleaseDir
#endif

[Setup]
AppId={{74E65BA9-6D74-4D26-9F20-BC70A84E1F35}
AppName=BetterQuickSettings
AppVersion={#AppVersion}
AppVerName=BetterQuickSettings {#AppVersion}
AppPublisher=lumax90
AppPublisherURL=https://github.com/lumax90/betterquicksettings
AppSupportURL=https://github.com/lumax90/betterquicksettings/issues
AppUpdatesURL=https://github.com/lumax90/betterquicksettings/releases
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\BetterQuickSettings
DefaultGroupName=BetterQuickSettings
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
DisableDirPage=no
DisableProgramGroupPage=yes
WizardStyle=modern
LicenseFile=..\LICENSE
InfoBeforeFile=beta-notice.txt
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\BetterQuickSettings.exe
; Must match the process-lifetime mutex retained for early-preview upgrades.
AppMutex=Local\Ada.ControlCenter.85a316c7
CloseApplications=no
RestartApplications=no
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
OutputDir={#ReleaseDir}
OutputBaseFilename=BetterQuickSettings-{#AppVersion}-win-x64-Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\BetterQuickSettings"; Filename: "{app}\BetterQuickSettings.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\BetterQuickSettings"; Filename: "{app}\BetterQuickSettings.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\BetterQuickSettings.exe"; Description: "Launch BetterQuickSettings"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
  InstalledCommand: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    InstalledCommand := '"' + ExpandConstant('{app}\BetterQuickSettings.exe') + '" --background';
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'BetterQuickSettings', StartupCommand) then
    begin
      // Do not remove a startup entry that belongs to a different portable copy.
      if CompareText(StartupCommand, InstalledCommand) = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BetterQuickSettings');
    end;
  end;
  // User data is outside {app}. Never delete notes, history, or shelf copies here.
end;
