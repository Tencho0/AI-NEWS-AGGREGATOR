<#
.SYNOPSIS
    Releases and restarts this dev machine's Development-environment Newsroom worker from its own
    folder (docs/runbooks/start-the-worker.md). This is NOT the live pipeline.
.DESCRIPTION
    The live pipeline is the Windows Service "PredelNewsroom" on the Predel-News VPS
    (docs/runbooks/deploy.md, docs/runbooks/release-a-new-version.md), not on this machine. This
    script starts a second, Development-environment worker on this dev machine, which loads this
    machine's own dotnet user-secrets store - the same live Telegram bot token, Gemini key and
    Facebook page token the VPS instance already uses. Starting it therefore contends with the VPS
    for that Telegram bot token: two long-pollers on one token fight over getUpdates, each
    swallowing roughly half the editors' button presses. For day-to-day development use
    tools\restart-sandbox.ps1 instead (docs/runbooks/run-the-sandbox.md) - it is fail-closed
    against the live database, site and Facebook page. Only run this script when you deliberately
    need this machine's live secrets running locally.
    This worker runs from $LiveRoot, NOT from src\Newsroom.Worker\bin\Debug — that folder
    belongs to development builds and the sandbox F5 profile (docs/adr/0014-sandbox-mode.md).
    Keeping them apart means a dotnet build no longer has to stop this worker first.
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

    # Trailing separator turns the prefix test into a directory-boundary test: without it,
    # "C:\apps\newsroom-sandbox" (the sandbox's own path, configured in appsettings.Sandbox.json)
    # would also match a bare "C:\apps\newsroom" prefix, and this script could kill the sandbox.
    $liveRootPrefix = $LiveRoot.TrimEnd('\') + '\'

    # Match on path, never on name alone: a sandbox worker is the same executable name.
    $running = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($liveRootPrefix, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        Write-Host "Stopping the Development worker (PID $($running.Id -join ', '))..."
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
            Where-Object { $_.Path -and $_.Path.StartsWith($liveRootPrefix, [StringComparison]::OrdinalIgnoreCase) }
        if ($still) {
            $stopCommand = "Get-Process Newsroom.Worker | Where-Object { `$_.Path -like '$liveRootPrefix*' } | Stop-Process -Force"
            throw "Could not stop the Development worker. Stop it from an elevated PowerShell: $stopCommand"
        }
    }
    else {
        Write-Host "No Development worker running - starting fresh."
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
        Where-Object { $_.Path -and $_.Path.StartsWith($liveRootPrefix, [StringComparison]::OrdinalIgnoreCase) }
    if (-not $proc) { throw "Worker did not stay up - check the newest log under '$LiveRoot\logs'." }
    Write-Host "Development worker running (PID $($proc.Id -join ', '))."

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
