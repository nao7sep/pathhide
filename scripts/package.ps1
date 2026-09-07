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

# Debug symbols are useful to developers but add about 105 MB of uncompressed
# payload. Keep them in build outputs, not in the user-facing package.
Get-ChildItem -LiteralPath publish-win -Recurse -File -Filter *.pdb | Remove-Item -Force
if (Get-ChildItem -LiteralPath publish-win -Recurse -File -Filter *.pdb) {
    throw "Debug symbols remain in publish-win after release cleanup."
}
Copy-Item -LiteralPath LICENSE -Destination publish-win/LICENSE.txt

# Portable: zip the self-contained folder as-is.
Compress-Archive -Path publish-win/* -DestinationPath "dist/$AppName-$Version-win.zip" -Force

# Installer: Inno Setup. iscc is pre-installed on the windows-latest runner;
# local development may use either the per-user or machine-wide installation.
$isccCommand = Get-Command iscc -ErrorAction SilentlyContinue
$iscc = if ($isccCommand) { $isccCommand.Source } else { $null }
if (-not $iscc) {
    $isccCandidates = @(
        if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe" }
        if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe" }
        if ($env:ProgramFiles) { Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe" }
    )
    $iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw "Inno Setup compiler not found. Install Inno Setup 6 for the current user or machine, or add ISCC.exe to PATH."
}
& $iscc "/DMyAppVersion=$Version" scripts/pathhide.iss

Get-ChildItem dist
