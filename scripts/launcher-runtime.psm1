# Shared by the Windows launchers (run-dev, run-built, rebuild): one PathHide runtime per repository.
# A launcher claims ownership, stops any PathHide it or another launcher started, waits for the new one
# to appear, and releases ownership only if no later launcher has claimed it since.

function Get-LauncherOwnerFile {
    param([Parameter(Mandatory = $true)][string]$RepoDir)
    return Join-Path (Join-Path $RepoDir '.git') 'launcher-runtime-owner'
}

function Claim-LauncherRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$Token,
        [Parameter(Mandatory = $true)][string]$RepoDir
    )
    $ownerFile = Get-LauncherOwnerFile -RepoDir $RepoDir
    $temporaryFile = "$ownerFile.$PID.$([guid]::NewGuid().ToString('N'))"
    [System.IO.File]::WriteAllText($temporaryFile, "$Token`n", [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryFile -Destination $ownerFile -Force
}

function Test-LauncherRuntimeOwner {
    param(
        [Parameter(Mandatory = $true)][string]$Token,
        [Parameter(Mandatory = $true)][string]$RepoDir
    )
    $ownerFile = Get-LauncherOwnerFile -RepoDir $RepoDir
    return (Test-Path -LiteralPath $ownerFile) -and
        ((Get-Content -LiteralPath $ownerFile -Raw).Trim() -ceq $Token)
}

function Release-LauncherRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$Token,
        [Parameter(Mandatory = $true)][string]$RepoDir
    )
    if (Test-LauncherRuntimeOwner -Token $Token -RepoDir $RepoDir) {
        Remove-Item -LiteralPath (Get-LauncherOwnerFile -RepoDir $RepoDir) -Force -ErrorAction SilentlyContinue
    }
}

function Get-RuntimeRecord {
    param([string]$RepoDir)
    $file = Join-Path (Join-Path $RepoDir '.git') 'launcher-runtime-child'
    if (Test-Path -LiteralPath $file) {
        try { return Get-Content -LiteralPath $file -Raw | ConvertFrom-Json } catch { return $null }
    }
    return $null
}

function Get-OwnedRuntimeProcess {
    param([string]$RepoDir, [string]$ProjectFile, [string]$ExecutableName, [string]$BuiltExecutable)
    $record = Get-RuntimeRecord $RepoDir
    if (-not $record -or $record.state -ne 'running') { return }
    $process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
    if ($process -and $process.StartTime.ToUniversalTime().Ticks -eq $record.startTicks) { return $process }
}

function Stop-OwnedRuntime {
    param([string]$Label, [string]$RepoDir, [string]$ProjectFile, [string]$ExecutableName, [string]$BuiltExecutable, [string]$ExpectedToken)
    if ($ExpectedToken) {
        $record = Get-RuntimeRecord $RepoDir
        if (-not $record -or $record.token -cne $ExpectedToken) { return }
    }
    $process = Get-OwnedRuntimeProcess -RepoDir $RepoDir
    if (-not $process) { return }
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(5000)) {
        throw "$Label is still open. Finish or cancel its pending changes before replacing it."
    }
}

function Wait-OwnedRuntime {
    param([string]$Label, [string]$RepoDir, [string]$ProjectFile, [string]$ExecutableName,
        [string]$BuiltExecutable, [int]$TimeoutSeconds = 30)
    $token = (Get-Content -LiteralPath (Get-LauncherOwnerFile $RepoDir) -Raw).Trim()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $record = Get-RuntimeRecord $RepoDir
        if ($record -and $record.token -ceq $token) {
            if ($record.state -eq 'activated') {
                Write-Host "$Label activated an independently started instance; the selected launch mode did not replace it."
                return
            }
            if (Get-OwnedRuntimeProcess -RepoDir $RepoDir) { return }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$Label did not start within ${TimeoutSeconds}s."
}

Export-ModuleMember -Function Claim-LauncherRuntime, Test-LauncherRuntimeOwner, Release-LauncherRuntime, Stop-OwnedRuntime, Wait-OwnedRuntime
