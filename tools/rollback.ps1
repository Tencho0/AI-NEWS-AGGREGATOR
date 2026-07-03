<#
.SYNOPSIS
    Restores the newest folder backup taken by deploy.ps1 and restarts the service.
.DESCRIPTION
    Rollback per docs/09-deployment.md: stop service, mirror the newest
    <Target>-backup-<timestamp> folder back over the target (current logs are kept),
    start the service. Migrations are forward-only and written backward-compatible,
    so N-1 binaries run against the current schema. Run elevated on the VPS.
.EXAMPLE
    .\tools\rollback.ps1
#>
[CmdletBinding()]
param(
    [string]$Target = "C:\apps\newsroom",
    [string]$ServiceName = "PredelNewsroom"
)

$ErrorActionPreference = "Stop"

try {
    $parent = Split-Path -Parent $Target
    $leaf = Split-Path -Leaf $Target
    $backup = Get-ChildItem -Path $parent -Directory -Filter "$leaf-backup-*" |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $backup) {
        throw "No backup folder matching '$leaf-backup-*' found under '$parent'."
    }

    Write-Host "Rolling back '$Target' to '$($backup.FullName)'"

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne "Stopped") {
        Write-Host "Stopping service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
    }

    # /MIR restores the exact backed-up state; /XD logs keeps the current log files.
    & robocopy $backup.FullName $Target /MIR /R:2 /W:2 /NP /NFL /NDL /XD logs | Out-Host
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }

    if ($service) {
        Write-Host "Starting service '$ServiceName'..."
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
        Start-Sleep -Seconds 5

        $log = Get-ChildItem -Path (Join-Path $Target "logs") -Filter "*.log" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($log) {
            Write-Host "--- $($log.Name) (last 20 lines) ---"
            Get-Content -Path $log.FullName -Tail 20
        }
    }

    Write-Host "Rollback finished. Verify /status in Telegram."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
