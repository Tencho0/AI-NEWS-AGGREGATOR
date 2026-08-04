<#
.SYNOPSIS
    Releases and restarts the live Newsroom worker from its own folder
    (docs/runbooks/start-the-worker.md).
.DESCRIPTION
    The live worker runs from $LiveRoot, NOT from src\Newsroom.Worker\bin\Debug — that folder
    belongs to development builds and the sandbox F5 profile (docs/adr/0014-sandbox-mode.md).
    Keeping them apart means a dotnet build no longer has to kill the live pipeline.
    Stops only processes whose executable lives under $LiveRoot, publishes Debug on top, then
    relaunches detached with no window. Logs go to $LiveRoot\logs\newsroom-<date>.log.
.EXAMPLE
    .\tools\restart-worker.ps1
#>
[CmdletBinding()]
param(
    [string]$LiveRoot = "C:\apps\newsroom"
)

$ErrorActionPreference = "Stop"

try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    Set-Location $repoRoot

    # Match on path, never on name alone: a sandbox worker is the same executable name.
    $running = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($LiveRoot, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        Write-Host "Stopping the live worker (PID $($running.Id -join ', '))..."
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
        $still = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($LiveRoot, [StringComparison]::OrdinalIgnoreCase) }
        if ($still) {
            throw "Could not stop the live worker. Stop it from an elevated PowerShell: Get-Process Newsroom.Worker | Stop-Process -Force"
        }
    }
    else {
        Write-Host "No live worker running - starting fresh."
    }

    Write-Host "Publishing to '$LiveRoot'..."
    dotnet publish src\Newsroom.Worker\Newsroom.Worker.csproj -c Debug -o $LiveRoot
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    # Development is what makes the app load the LIVE dotnet user-secrets (Gemini, Telegram,
    # Facebook). The sandbox uses DOTNET_ENVIRONMENT=Sandbox and a different secrets store.
    $env:DOTNET_ENVIRONMENT = 'Development'
    Write-Host "Starting hidden from '$LiveRoot'..."
    Start-Process -FilePath "$LiveRoot\Newsroom.Worker.exe" -WorkingDirectory $LiveRoot -WindowStyle Hidden

    Start-Sleep -Seconds 5
    $proc = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($LiveRoot, [StringComparison]::OrdinalIgnoreCase) }
    if (-not $proc) { throw "Worker did not stay up - check the newest log under '$LiveRoot\logs'." }
    Write-Host "Live worker running (PID $($proc.Id -join ', '))."

    $log = Get-ChildItem "$LiveRoot\logs\newsroom-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) {
        Write-Host "--- $($log.Name) (last 6 lines) ---"
        Get-Content $log.FullName -Tail 6
    }
    else {
        Write-Host "No log file yet - check '$LiveRoot\logs' in a minute."
    }
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
