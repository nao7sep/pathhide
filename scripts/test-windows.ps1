# Run the complete ordinary suite once on Windows and keep a shareable result bundle (the TRX
# result and the SDK information) under TestResults/. Run it from PowerShell:
#   powershell -NoProfile -File .\scripts\test-windows.ps1
# Requires the .NET SDK. Enable Windows Developer Mode first: the symbolic-link test needs it and
# fails rather than passing silently without it. It launches no GUI and requests no administrator
# rights, so real UAC consent, antivirus delays, disconnected shares, input methods and multiple
# displays stay outside what it checks.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Run this script on Windows. On macOS use dotnet test -c Release.'
}
$repoDir = Split-Path -Parent $PSScriptRoot
$resultDir = Join-Path $repoDir ('TestResults/windows-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $resultDir
Push-Location $repoDir
try {
    & dotnet --info | Out-File -Encoding utf8 (Join-Path $resultDir 'dotnet-info.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the .NET SDK information.' }
    # dotnet builds once, incrementally. Keep every ordinary test; --no-build could use stale code.
    & dotnet test -c Release --logger 'trx;LogFileName=results.trx' --results-directory $resultDir
    $testExitCode = $LASTEXITCODE
    Write-Host "Test results: $resultDir"
    if ($testExitCode -ne 0) { exit $testExitCode }
}
finally {
    Pop-Location
}
