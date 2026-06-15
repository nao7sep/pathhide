Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$scriptExitCode = 0

# rebuild: produce a fresh self-contained Release build for Windows, then launch
# it. This is the slow launcher, run after changing source. There is no codesign
# step on Windows. run-built remains the no-build fast path after this.

function Set-Utf8Console {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [Console]::InputEncoding = $utf8NoBom
    [Console]::OutputEncoding = $utf8NoBom
    $global:OutputEncoding = $utf8NoBom
    if (Get-Command chcp.com -ErrorAction SilentlyContinue) {
        & chcp.com 65001 > $null
        $null = $LASTEXITCODE
    }
}

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Require-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Missing required command: $Name"
    }
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [int[]]$AllowedExitCodes = @(0)
    )

    & $FilePath @ArgumentList
    $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
    if ($AllowedExitCodes -notcontains $exitCode) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($ArgumentList -join ' ')"
    }
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoDir = Split-Path -Parent $scriptDir
$projectFile = Join-Path $repoDir "PathHide.csproj"
$publishDir = Join-Path $repoDir "bin/Release/net10.0/win-x64/publish"
$exePath = Join-Path $publishDir "PathHide.exe"

try {
    Set-Utf8Console
    Require-Command dotnet

    Set-Location $repoDir

    Write-Step "Removing stale publish output"
    # Clear the publish dir first so a build that fails to emit a file cannot be
    # masked by a leftover artifact from a previous run.
    if (Test-Path $publishDir) {
        Remove-Item -Recurse -Force $publishDir
    }

    Write-Step "Publishing self-contained win-x64 build"
    Invoke-Native -FilePath "dotnet" -ArgumentList @(
        "publish", $projectFile,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-o", $publishDir
    )

    Write-Step "Launching PathHide"
    # GUI app: launch non-blocking via Start-Process (the Windows counterpart to
    # macOS `open`), so the console does not wait on the app's lifetime.
    Start-Process -FilePath $exePath
}
catch {
    Write-Host ""
    Write-Host "pathhide rebuild failed: $($_.Exception.Message)" -ForegroundColor Red
    $scriptExitCode = 1
}
finally {
    Read-Host "Press Enter to close" | Out-Null
}

exit $scriptExitCode
