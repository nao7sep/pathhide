Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$scriptExitCode = 0

# run-dev: run the app from source with dotnet run (Debug, incremental), in its loosest configuration.
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
# The app name as Directory.Build.props states it, read as plain text so launching stays instant.
$appName = ([regex]::Match((Get-Content -LiteralPath (Join-Path $repoDir "Directory.Build.props") -Raw), '<Product>(.*?)</Product>')).Groups[1].Value
$projectFile = Join-Path $repoDir "src/PathHide/PathHide.csproj"
$builtExecutable = Join-Path $repoDir "publish-win/$appName.exe"
$runtimeToken = [guid]::NewGuid().ToString("N")

try {
    Set-Utf8Console
    Import-Module (Join-Path $scriptDir "launcher-runtime.psm1") -Force
    Require-Command dotnet

    Set-Location $repoDir

    Write-Step "Replacing any existing $appName runtime"
    Claim-LauncherRuntime -Token $runtimeToken -RepoDir $repoDir
    Stop-OwnedRuntime -Label $appName -RepoDir $repoDir -ProjectFile $projectFile -ExecutableName $appName -BuiltExecutable $builtExecutable

    Write-Step "Restoring packages required for launch"
    Invoke-Native -FilePath "dotnet" -ArgumentList @("restore", $projectFile)

    Write-Step "Starting $appName (Debug, from source)"
    $devProcess = Start-Process -FilePath "dotnet" -ArgumentList @("run", "--project", "`"$projectFile`"", "--", "--launcher-repo", "`"$repoDir`"", "--launcher-token", $runtimeToken) -NoNewWindow -PassThru
    Wait-OwnedRuntime -Label $appName -RepoDir $repoDir -ProjectFile "" -ExecutableName $appName -BuiltExecutable $builtExecutable -TimeoutSeconds 120
    Write-Step "$appName is ready"
    $devProcess.WaitForExit()
    if ($devProcess.ExitCode -notin @(0, 130, -1073741510)) {
        throw "$appName development runtime failed with exit code $($devProcess.ExitCode)."
    }
}
catch {
    Write-Host ""
    Write-Host "$appName run-dev failed: $($_.Exception.Message)" -ForegroundColor Red
    $scriptExitCode = 1
}
finally {
    if (Test-LauncherRuntimeOwner -Token $runtimeToken -RepoDir $repoDir) {
        Stop-OwnedRuntime -Label $appName -RepoDir $repoDir -ProjectFile $projectFile -ExecutableName $appName -BuiltExecutable $builtExecutable -ExpectedToken $runtimeToken
        Release-LauncherRuntime -Token $runtimeToken -RepoDir $repoDir
        Read-Host "Press Enter to close" | Out-Null
    }
}

exit $scriptExitCode
