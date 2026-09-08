Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$scriptExitCode = 0

# run-dev: run the app from source with live reload, in its loosest configuration.
# For active coding and debugging. The strict, production-faithful launchers are
# run-built (launch the existing packaged app bundle without rebuilding) and
# rebuild (build and package a fresh bundle, then launch).

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
$projectFile = Join-Path $repoDir "src/PathHide/PathHide.csproj"
$builtExecutable = Join-Path $repoDir "bin/Release/net10.0/win-x64/publish/PathHide.exe"
$runtimeToken = [guid]::NewGuid().ToString("N")

try {
    Set-Utf8Console
    Import-Module (Join-Path $scriptDir "launcher-runtime.psm1") -Force
    Require-Command dotnet

    Set-Location $repoDir

    Write-Step "Replacing any existing PathHide runtime"
    Claim-LauncherRuntime -Token $runtimeToken -RepoDir $repoDir
    Stop-OwnedRuntime -Kind dotnet -Label "PathHide" -RepoDir $repoDir -ProjectFile $projectFile -ExecutableName "PathHide" -BuiltExecutable $builtExecutable

    Write-Step "Restoring packages required for launch"
    Invoke-Native -FilePath "dotnet" -ArgumentList @("restore", $projectFile)

    Write-Step "Starting PathHide (Debug, from source)"
    $devProcess = Start-Process -FilePath "dotnet" -ArgumentList @("run", "--project", $projectFile) -NoNewWindow -PassThru
    Wait-OwnedRuntime -Kind dotnet -Label "PathHide" -RepoDir $repoDir -ProjectFile "" -ExecutableName "PathHide" -BuiltExecutable $builtExecutable -TimeoutSeconds 120
    Write-Step "PathHide is ready"
    $devProcess.WaitForExit()
    if ($devProcess.ExitCode -notin @(0, 130, -1073741510)) {
        throw "PathHide development runtime failed with exit code $($devProcess.ExitCode)."
    }
}
catch {
    Write-Host ""
    Write-Host "pathhide run-dev failed: $($_.Exception.Message)" -ForegroundColor Red
    $scriptExitCode = 1
}
finally {
    if (Test-LauncherRuntimeOwner -Token $runtimeToken -RepoDir $repoDir) {
        Stop-OwnedRuntime -Kind dotnet -Label "PathHide" -RepoDir $repoDir -ProjectFile $projectFile -ExecutableName "PathHide" -BuiltExecutable $builtExecutable
        Release-LauncherRuntime -Token $runtimeToken -RepoDir $repoDir
        Read-Host "Press Enter to close" | Out-Null
    }
}

exit $scriptExitCode
