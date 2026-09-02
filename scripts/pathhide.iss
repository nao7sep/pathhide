; Inno Setup script — builds dist\PathHide-<version>-setup.exe from the
; self-contained win-x64 publish in publish-win\. The version is passed in by
; scripts/package.ps1 via /DMyAppVersion. iscc is pre-installed on windows-latest.

#define MyAppName "PathHide"
#define MyAppPublisher "Yoshinao Inoguchi"
#define MyAppExe "PathHide.exe"
#ifndef MyAppVersion
  #error MyAppVersion is not defined - pass it via  iscc /DMyAppVersion=x.y.z
#endif

[Setup]
; This .iss lives in scripts/, but the win-x64 publish output and the dist/ output
; folder are at the repo root — so resolve all source/output paths one level up.
SourceDir=..
AppName={#MyAppName}
AppId={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExe}
Uninstallable=yes
OutputDir=dist
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupIconFile=src\PathHide\icon.ico
PrivilegesRequiredOverridesAllowed=dialog

[Files]
Source: "publish-win\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
; Inno cannot recover a non-elevated user token for every elevated setup path.
; All-users installs launch later through their scoped shell shortcuts.
Filename: "{app}\{#MyAppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: not IsAdminInstallMode
