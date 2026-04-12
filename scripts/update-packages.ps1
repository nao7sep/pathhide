Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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
        throw "Command failed with exit code $exitCode: $FilePath $($ArgumentList -join ' ')"
    }
}

function Stop-PathHideProcess {
    Get-Process PathHide -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $dotnetHosts = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.CommandLine -like "*PathHide.csproj*" -or
            $_.CommandLine -like "*PathHide.dll*"
        }

    foreach ($processInfo in $dotnetHosts) {
        Stop-Process -Id $processInfo.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoDir = Split-Path -Parent $scriptDir
$projectFile = Join-Path $repoDir "PathHide.csproj"
$binDir = Join-Path $repoDir "bin"
$objDir = Join-Path $repoDir "obj"

try {
    Set-Utf8Console
    Require-Command dotnet

    Set-Location $repoDir

    Write-Step "Stopping running PathHide instances"
    Stop-PathHideProcess

    Write-Step "Restoring current packages"
    Invoke-Native -FilePath "dotnet" -ArgumentList @("restore", $projectFile)

    Write-Step "Updating NuGet package references"
    Invoke-Native -FilePath "dotnet" -ArgumentList @("package", "update", "--project", $projectFile)

    Write-Step "Applying vulnerable package updates"
    Invoke-Native -FilePath "dotnet" -ArgumentList @("package", "update", "--project", $projectFile, "--vulnerable")

    Write-Step "Cleaning previous build outputs"
    if (Test-Path $binDir) {
        Remove-Item -Recurse -Force $binDir
    }
    if (Test-Path $objDir) {
        Remove-Item -Recurse -Force $objDir
    }

    Write-Step "Building PathHide"
    Invoke-Native -FilePath "dotnet" -ArgumentList @("build", $projectFile)
}
catch {
    Write-Host ""
    Write-Host "pathhide update-packages failed: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host "Press Enter to close" | Out-Null
    exit 1
}
