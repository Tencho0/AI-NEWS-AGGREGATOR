<#
.SYNOPSIS
    Restarts the Newsroom worker hidden on the dev machine (docs/runbooks/start-the-worker.md).
.DESCRIPTION
    Stops any running Newsroom.Worker instance first (only one may run at a time, and a running
    worker locks the DLL so the build would fail), rebuilds Debug, then launches the built .exe
    detached with no window (Option B in the runbook). Logs go to
    src\Newsroom.Worker\bin\Debug\net10.0\logs\newsroom-<date>.log.
.EXAMPLE
    .\tools\restart-worker.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    Set-Location $repoRoot

    $running = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Stopping running Newsroom.Worker (PID $($running.Id -join ', '))..."
        try {
            $running | Stop-Process -Force -ErrorAction Stop
        }
        catch {
            # An instance started from an elevated prompt can only be killed by an elevated one.
            Write-Host "Access denied - asking for elevation (accept the UAC prompt)..."
            $ids = ($running.Id | ForEach-Object { "/PID $_" }) -join ' '
            Start-Process -FilePath "taskkill.exe" -ArgumentList "/F $ids" -Verb RunAs -Wait -WindowStyle Hidden
        }
        Start-Sleep -Seconds 2
        if (Get-Process Newsroom.Worker -ErrorAction SilentlyContinue) {
            throw "Could not stop the running worker. Stop it from an elevated PowerShell: Get-Process Newsroom.Worker | Stop-Process -Force"
        }
    }
    else {
        Write-Host "No running Newsroom.Worker instance found - starting fresh."
    }

    Write-Host "Building..."
    dotnet build src\Newsroom.Worker\Newsroom.Worker.csproj -c Debug
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

    # Development is what makes the app load dotnet user-secrets (Gemini, Telegram, Facebook).
    $env:DOTNET_ENVIRONMENT = 'Development'
    $dir = Resolve-Path "src\Newsroom.Worker\bin\Debug\net10.0"
    Write-Host "Starting hidden from '$dir'..."
    Start-Process -FilePath "$dir\Newsroom.Worker.exe" -WorkingDirectory $dir -WindowStyle Hidden

    Start-Sleep -Seconds 5
    $proc = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue
    if (-not $proc) { throw "Worker did not stay up - check the newest log under '$dir\logs'." }
    Write-Host "Worker running (PID $($proc.Id -join ', '))."

    $log = Get-ChildItem "$dir\logs\newsroom-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) {
        Write-Host "--- $($log.Name) (last 6 lines) ---"
        Get-Content $log.FullName -Tail 6
    }
    else {
        Write-Host "No log file yet - check '$dir\logs' in a minute."
    }
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
