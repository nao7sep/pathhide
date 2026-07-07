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
$exePath = Join-Path $repoDir "bin/Release/net10.0/win-x64/publish/PathHide.exe"

try {
    Set-Utf8Console

    Set-Location $repoDir

    # No publish here: this launcher must start instantly. If there is no usable
    # build yet, stop and point at rebuild rather than launching something stale
    # or empty.
    if (-not (Test-Path $exePath)) {
        throw "No build found — run rebuild first."
    }

    $builtAt = (Get-Item $exePath).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
    Write-Step "Launching the existing build (built: $builtAt)"
    Write-Host "If you changed source since then, run rebuild instead."

    # GUI app: launch non-blocking via Start-Process.
    Start-Process -FilePath $exePath
}
catch {
    Write-Host ""
    Write-Host "pathhide run-built failed: $($_.Exception.Message)" -ForegroundColor Red
    $scriptExitCode = 1
}
finally {
    Read-Host "Press Enter to close" | Out-Null
}

exit $scriptExitCode
