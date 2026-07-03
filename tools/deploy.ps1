<#
.SYNOPSIS
    Releases a new worker build to the service folder (docs/09-deployment.md).
.DESCRIPTION
    Stops the service (when it exists), takes a timestamped backup of the target folder
    (keeping the newest 3), copies the publish output on top - leaving an existing
    appsettings.Production.json untouched - starts the service again and tails the newest
    log. Migrations run inside the worker at startup. Run elevated on the VPS.
.EXAMPLE
    dotnet publish src/Newsroom.Worker -c Release -r win-x64 --self-contained false -o publish
    .\tools\deploy.ps1 -PublishSource .\publish
#>
[CmdletBinding()]
param(
    [string]$PublishSource = ".\publish",
    [string]$Target = "C:\apps\newsroom",
    [string]$ServiceName = "PredelNewsroom"
)

$ErrorActionPreference = "Stop"

try {
    if (-not (Test-Path (Join-Path $PublishSource "Newsroom.Worker.exe"))) {
        throw "No publish output at '$PublishSource' (expected Newsroom.Worker.exe). Run dotnet publish first (see docs/runbooks/deploy.md)."
    }

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne "Stopped") {
            Write-Host "Stopping service '$ServiceName'..."
            Stop-Service -Name $ServiceName -Force
            $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
        }
    }
    else {
        Write-Host "Service '$ServiceName' does not exist yet - copying files only (run tools\install-service.ps1 afterwards)."
    }

    if (Test-Path $Target) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $backupPath = "$Target-backup-$stamp"
        Write-Host "Backing up '$Target' -> '$backupPath'"
        Copy-Item -Path $Target -Destination $backupPath -Recurse -Force

        # Keep only the newest 3 backups (the timestamp format sorts lexicographically).
        $parent = Split-Path -Parent $Target
        $leaf = Split-Path -Leaf $Target
        $backups = @(Get-ChildItem -Path $parent -Directory -Filter "$leaf-backup-*" |
            Sort-Object Name -Descending)
        if ($backups.Count -gt 3) {
            $backups | Select-Object -Skip 3 | ForEach-Object {
                Write-Host "Removing old backup '$($_.FullName)'"
                Remove-Item -Path $_.FullName -Recurse -Force -Confirm:$false
            }
        }
    }
    else {
        New-Item -ItemType Directory -Path $Target -Force | Out-Null
    }

    Write-Host "Copying '$PublishSource' -> '$Target'"
    $robocopyArgs = @($PublishSource, $Target, "/E", "/R:2", "/W:2", "/NP", "/NFL", "/NDL")
    if (Test-Path (Join-Path $Target "appsettings.Production.json")) {
        # The production config (secrets, ACL-restricted, not in git) must survive releases.
        $robocopyArgs += @("/XF", "appsettings.Production.json")
    }
    & robocopy @robocopyArgs | Out-Host
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
        else {
            Write-Host "No log file found under '$Target\logs' yet - check the service manually."
        }
    }

    Write-Host "Deploy finished. Verify /status in Telegram (docs/09-deployment.md post-deploy checklist)."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
