# Shared by the Windows launchers (run-dev, run-built, rebuild): one PathHide runtime per repository.
# The launcher that starts PathHide records its process ID and executable under .git; the next launcher
# asks that process to close only while it still runs that executable. PathHide started any other way is
# left to its single-instance behavior, which brings its window forward instead of opening a second one.

function Get-RuntimeRecordFile {
    param([Parameter(Mandatory = $true)][string]$RepoDir)
    return Join-Path (Join-Path $RepoDir '.git') 'launcher-runtime-process'
}

function Save-RuntimeRecord {
    param(
        [Parameter(Mandatory = $true)][string]$RepoDir,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process
    )
    [System.IO.File]::WriteAllLines((Get-RuntimeRecordFile $RepoDir), [string[]]@($Process.Id, $Process.Path))
}

# The recorded process, returned only while it still runs the recorded executable.
function Get-RecordedRuntime {
    param([Parameter(Mandatory = $true)][string]$RepoDir)
    $file = Get-RuntimeRecordFile $RepoDir
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    $lines = @(Get-Content -LiteralPath $file)
    $processId = 0
    if ($lines.Count -lt 2 -or -not [int]::TryParse($lines[0], [ref]$processId)) { return $null }
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($process -and $lines[1] -and $process.Path -eq $lines[1]) { return $process }
    return $null
}

# Asks the recorded PathHide to close its window as a user would, so it can still ask about unsaved work,
# and stops with a message rather than force-closing it when it stays open.
function Stop-RecordedRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$RepoDir
    )
    $process = Get-RecordedRuntime $RepoDir
    if (-not $process) { return }
    Write-Host "Asking the running $Label (pid $($process.Id)) to close."
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(5000)) {
        throw "$Label is still open. Finish or cancel its pending changes, then run this launcher again."
    }
}

function Start-RecordedRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$RepoDir,
        [Parameter(Mandatory = $true)][string]$Executable
    )
    $process = Start-Process -FilePath $Executable -PassThru
    Save-RuntimeRecord -RepoDir $RepoDir -Process $process
    Write-Host "If $Label was already running outside these launchers, its window is brought forward instead."
}

# Records the app process that the wrapper (dotnet run) starts, once it appears, and returns it. Returns
# nothing when the wrapper ends first, as it does when the build fails.
function Save-WrappedRuntimeRecord {
    param(
        [Parameter(Mandatory = $true)][string]$RepoDir,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Wrapper,
        [Parameter(Mandatory = $true)][string]$ExecutableName
    )
    while (-not $Wrapper.HasExited) {
        $child = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($Wrapper.Id) AND Name = '$ExecutableName.exe'" |
            Select-Object -First 1
        if ($child) {
            $process = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
            if ($process) {
                Save-RuntimeRecord -RepoDir $RepoDir -Process $process
                return $process
            }
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

Export-ModuleMember -Function Get-RecordedRuntime, Stop-RecordedRuntime, Start-RecordedRuntime, Save-WrappedRuntimeRecord
