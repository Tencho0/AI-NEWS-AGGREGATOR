<#
.SYNOPSIS
    Restarts the sandbox Newsroom worker (docs/runbooks/run-the-sandbox.md, ADR-0014).
.DESCRIPTION
    The sandbox runs from its own folder so it never contends with the live worker in
    C:\apps\newsroom for locked DLLs, and both scripts match processes by executable PATH so
    neither can kill the other's instance. Configuration comes from appsettings.Sandbox.json plus
    the 'newsroom-worker-sandbox' user-secrets store; the worker refuses to start unless the
    database, the site URL and the image root are all sandbox ones.
    Only one sandbox instance may run at a time (the F5 'Sandbox' profile is the other one) -
    two would fight over the sandbox bot's getUpdates.
.EXAMPLE
    .\tools\restart-sandbox.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    Set-Location $repoRoot
    $sandboxRoot = Join-Path $repoRoot ".sandbox"

    $running = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($sandboxRoot, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        Write-Host "Stopping the sandbox worker (PID $($running.Id -join ', '))..."
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
    }

    # A debugger-launched sandbox (the F5 'Sandbox' profile) shares this bot and database.
    $fromBin = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path -like "*\bin\Debug\*" }
    if ($fromBin) {
        throw "A worker is running from bin\Debug (PID $($fromBin.Id -join ', ')) - that is the F5 sandbox. Stop it first; two sandboxes fight over the same Telegram bot."
    }

    Write-Host "Publishing to '$sandboxRoot'..."
    dotnet publish src\Newsroom.Worker\Newsroom.Worker.csproj -c Debug -o $sandboxRoot
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $env:DOTNET_ENVIRONMENT = 'Sandbox'
    Write-Host "Starting the sandbox hidden from '$sandboxRoot'..."
    Start-Process -FilePath "$sandboxRoot\Newsroom.Worker.exe" -WorkingDirectory $sandboxRoot -WindowStyle Hidden

    Start-Sleep -Seconds 6
    $proc = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($sandboxRoot, [StringComparison]::OrdinalIgnoreCase) }
    if (-not $proc) {
        Write-Host "Sandbox did not stay up - the guard most likely refused it. Newest log:"
        $failed = Get-ChildItem "$sandboxRoot\logs\*.log" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime | Select-Object -Last 1
        if ($failed) { Get-Content $failed.FullName -Tail 20 }
        throw "Sandbox worker exited. Fix the reported violations and run again."
    }
    Write-Host "Sandbox running (PID $($proc.Id -join ', '))."

    $log = Get-ChildItem "$sandboxRoot\logs\sandbox-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) {
        Write-Host "--- $($log.Name) (last 10 lines - look for the SANDBOX MODE banner) ---"
        Get-Content $log.FullName -Tail 10
    }
    else {
        Write-Host "No sandbox log yet - check '$sandboxRoot\logs' in a minute."
    }
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
