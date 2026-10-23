; Inno Setup script — builds dist\PathHide-<version>-setup.exe from the
; self-contained Windows publish in publish-win\. scripts/package.ps1 passes the version,
; product name, executable and publisher from the project. iscc is pre-installed on windows-latest.

#if !defined(MyAppVersion) || !defined(MyAppName) || !defined(MyAppExe) || !defined(MyAppPublisher)
  #error Run scripts/package.ps1, which passes MyAppVersion, MyAppName, MyAppExe and MyAppPublisher.
#endif

[Setup]
; .NET 10 runs on Windows 10 1607 and later; do not confuse technical admission with servicing dates.
MinVersion=10.0.14393
; This .iss lives in scripts/, but the Windows publish output and the dist/ output
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
; The published app is x64 only: refuse 32-bit Windows, and allow x64 and Arm64 (which runs x64).
ArchitecturesAllowed=x64compatible
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
