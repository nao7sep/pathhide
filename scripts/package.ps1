# Package PathHide for Windows into dist/: an Inno Setup .exe installer + a
# portable .zip. Run by CI on windows-latest. Per the app-release-conventions the
# packaging complexity lives here so the release workflow just calls this script.
$ErrorActionPreference = "Stop"
$Repo = Split-Path -Parent $PSScriptRoot
Set-Location $Repo

$AppName = "PathHide"
$Project = "src/PathHide/PathHide.csproj"
$Version = ([regex]::Match((Get-Content Directory.Build.props -Raw), '<Version>(.*?)</Version>')).Groups[1].Value

Remove-Item -Recurse -Force publish-win, dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path dist | Out-Null

# Self-contained win-x64 publish -> a folder of PathHide.exe + its runtime.
dotnet publish $Project -c Release -r win-x64 --self-contained true -o publish-win

# Portable: zip the self-contained folder as-is.
Compress-Archive -Path publish-win/* -DestinationPath "dist/$AppName-$Version-win.zip" -Force

# Installer: Inno Setup. iscc is pre-installed on the windows-latest runner; fall
# back to its standard install path if it isn't on PATH.
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
& $iscc "/DMyAppVersion=$Version" scripts/pathhide.iss

Get-ChildItem dist
