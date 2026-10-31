# Deploys the latest origin/main desktop build to artifacts\app and restarts the
# tray app when it was running. Intended for the Windows Scheduled Task
# "WattLedger auto-deploy" (every 30 minutes) but safe to run manually:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\update-local.ps1 [-Force]
#
# Design rules:
# - Builds happen in the dedicated ".deploy" git worktree pinned to origin/main,
#   so the developer's working tree (any branch, any dirt) is never touched.
# - A .deployed-commit marker skips work when nothing moved; -Force redeploys.
# - Resource usage only ever touches this machine's own checkout — no downloads,
#   no signature-less installs.

param([switch]$Force)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$deployDir = Join-Path $repo '.deploy'
$appDir = Join-Path $repo 'artifacts\app'
$stagingDir = Join-Path $repo 'artifacts\app-staging'
$marker = Join-Path $appDir '.deployed-commit'
$log = Join-Path $repo 'artifacts\update-local.log'

function Write-Log([string]$message) {
    Add-Content -Path $log -Value ('{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message)
}

# git writes progress to stderr; with $ErrorActionPreference='Stop' PowerShell 5.1 turns
# those lines into terminating errors. Route every git call through this helper, which
# judges success by exit code instead.
function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git @GitArguments 2>&1
        $text = ($output | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "git $($GitArguments -join ' ') failed: $text"
        }

        return $text
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

try {
    if (-not (Test-Path (Join-Path $deployDir '.git'))) {
        Invoke-Git worktree add --detach $deployDir origin/main | Out-Null
    }

    Invoke-Git -C $deployDir fetch origin main | Out-Null
    Invoke-Git -C $deployDir checkout --detach --quiet origin/main | Out-Null
    $sha = (Invoke-Git -C $deployDir rev-parse HEAD).Trim()
    if (-not $Force -and (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $sha)) {
        Write-Log "up to date at $sha"
        exit 0
    }

    Write-Log "deploying $sha"
    if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }

    dotnet publish (Join-Path $deployDir 'src\WattLedger.App\WattLedger.App.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $stagingDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    # Keep the deployed file name stable: autostart and shortcuts point at WattLedgerApp.exe.
    $builtExe = Join-Path $stagingDir 'WattLedger.exe'
    if ((Test-Path $builtExe) -and -not (Test-Path (Join-Path $stagingDir 'WattLedgerApp.exe'))) {
        Move-Item $builtExe (Join-Path $stagingDir 'WattLedgerApp.exe') -Force
    }

    $wasRunning = $false
    Get-Process -Name WattLedgerApp -ErrorAction SilentlyContinue | ForEach-Object {
        $wasRunning = $true
        Stop-Process -Id $_.Id -Force
    }
    if ($wasRunning) { Start-Sleep -Seconds 2 }

    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    Copy-Item (Join-Path $stagingDir '*') $appDir -Force
    Remove-Item $stagingDir -Recurse -Force
    Set-Content -Path $marker -Value $sha

    if ($wasRunning) {
        Start-Process (Join-Path $appDir 'WattLedgerApp.exe') -ArgumentList '--tray'
        Write-Log "deployed $sha and restarted the tray app"
    }
    else {
        Write-Log "deployed $sha (app was not running; left stopped)"
    }
}
catch {
    Write-Log "ERROR: $_"
    exit 1
}
