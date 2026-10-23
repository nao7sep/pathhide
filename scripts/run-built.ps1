Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$scriptExitCode = 0

# run-built: launch the EXISTING published build without rebuilding, so it starts
# instantly. This is the daily-use launcher. It never publishes — if you changed
# source, run rebuild first.

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

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoDir = Split-Path -Parent $scriptDir
# The app name as Directory.Build.props states it, read as plain text so launching stays instant.
$appName = ([regex]::Match((Get-Content -LiteralPath (Join-Path $repoDir "Directory.Build.props") -Raw), '<Product>(.*?)</Product>')).Groups[1].Value
$exePath = Join-Path $repoDir "publish-win/$appName.exe"
$projectFile = Join-Path $repoDir "src/PathHide/PathHide.csproj"
$runtimeToken = [guid]::NewGuid().ToString("N")

try {
    Set-Utf8Console
    Import-Module (Join-Path $scriptDir "launcher-runtime.psm1") -Force

    Set-Location $repoDir

    # No publish here: this launcher must start instantly. If there is no usable
    # build yet, stop and point at rebuild rather than launching something stale
    # or empty.
    if (-not (Test-Path $exePath)) {
        throw "No build found — run rebuild first."
    }

    $builtAt = (Get-Item $exePath).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
    Claim-LauncherRuntime -Token $runtimeToken -RepoDir $repoDir
    Stop-OwnedRuntime -Label $appName -RepoDir $repoDir -ProjectFile $projectFile -ExecutableName $appName -BuiltExecutable $exePath
    Write-Step "Launching the existing build (built: $builtAt)"
    Write-Host "If you changed source since then, run rebuild instead."

    # GUI app: launch non-blocking via Start-Process.
    Start-Process -FilePath $exePath -ArgumentList @('--launcher-repo', "`"$repoDir`"", '--launcher-token', $runtimeToken) | Out-Null
    Wait-OwnedRuntime -Label $appName -RepoDir $repoDir -ProjectFile "" -ExecutableName $appName -BuiltExecutable $exePath -TimeoutSeconds 30
}
catch {
    Write-Host ""
    Write-Host "$appName run-built failed: $($_.Exception.Message)" -ForegroundColor Red
    $scriptExitCode = 1
}
finally {
    Read-Host "Press Enter to close" | Out-Null
}

exit $scriptExitCode
