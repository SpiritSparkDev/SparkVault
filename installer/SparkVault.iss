; Inno Setup script for SparkVault.
; Compile with ISCC.exe (Inno Setup 6, https://jrsoftware.org/isdl.php) or open in the
; Inno Setup IDE and press Compile. Expects a self-contained win-x64 publish output at
; .\publish\ (relative to this file) — run build.ps1 to produce it and compile in one step.

#define MyAppName "SparkVault"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Geistesfunke"
#define MyAppExeName "SparkVault.exe"

[Setup]
; Fixed AppId — do not regenerate for future versions, or Windows will treat an upgrade as a
; separate, second install instead of replacing the old one.
AppId={{3E6D9314-E204-45CB-9FA2-38C4F0F95EAE}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Per-user install: no admin rights needed, matches the app's own per-user data model
; (%AppData%\SparkVault, HKCU autostart) — see the brainstorming discussion this shipped from.
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=SparkVault-Setup
SetupIconFile=..\src\SparkVault.App\Assets\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; publish\ is produced by build.ps1 (dotnet publish, self-contained win-x64) — not checked into
; git, built fresh each time so the installer always matches the current source.
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

; Deliberately no [UninstallDelete] for %AppData%\SparkVault — uninstalling removes the program
; files only, never the user's job configuration, backup history, or logs. If a full wipe is ever
; wanted, that should be an explicit, separate opt-in, not the installer's default behavior.
