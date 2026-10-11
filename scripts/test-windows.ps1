# Run the complete ordinary suite once and retain a shareable result bundle.
# Requires the .NET SDK; does not launch the GUI or request administrator rights.
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
