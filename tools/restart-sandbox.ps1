<#
.SYNOPSIS
    Restarts the sandbox Newsroom worker (docs/runbooks/run-the-sandbox.md, ADR-0014).
.DESCRIPTION
    The sandbox runs from its own folder so it never contends with the Development worker in
    C:\apps\newsroom for locked DLLs, and both scripts match processes by executable path with a
    trailing separator on the prefix (so C:\apps\newsroom does not also match
    C:\apps\newsroom-sandbox) - neither can kill the other's instance.
    Configuration comes from appsettings.Sandbox.json plus the 'newsroom-worker-sandbox'
    user-secrets store; the worker refuses to start unless the database, the site URL and the
    image root are all sandbox ones.
    Only one sandbox instance may run at a time (the F5 'Sandbox' profile is the other one) -
    two would fight over the sandbox bot's getUpdates.
    The worker's own startup output is redirected to .sandbox\logs\restart-stdout.log /
    restart-stderr.log (overwritten on every run): the fail-closed guard's violation list is
    thrown before Serilog's file sink is built, so it never reaches sandbox-<date>.log - this is
    the only place it is captured once the console is hidden.
.EXAMPLE
    .\tools\restart-sandbox.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

# The logs are UTF-8 and carry Cyrillic source names plus the banner's emoji. Windows PowerShell
# 5.1 decodes with the ANSI codepage by default and renders mojibake, so every Get-Content below
# passes -Encoding UTF8 and the console is switched to UTF-8 for the tail to display correctly.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    Set-Location $repoRoot
    $sandboxRoot = Join-Path $repoRoot ".sandbox"
    # Trailing separator turns the prefix test into a directory-boundary test: without it,
    # "C:\apps\newsroom-sandbox" (a real, separately-configured path) would also match a bare
    # "C:\apps\newsroom" prefix. $sandboxRoot itself has no such sibling today, but both restart
    # scripts use the same pattern so a future rename doesn't quietly reintroduce the bug.
    $sandboxRootPrefix = $sandboxRoot.TrimEnd('\') + '\'

    $logsDir = Join-Path $sandboxRoot "logs"
    if (-not (Test-Path $logsDir)) {
        New-Item -ItemType Directory -Force -Path $logsDir | Out-Null
    }
    $startupStdout = Join-Path $logsDir "restart-stdout.log"
    $startupStderr = Join-Path $logsDir "restart-stderr.log"

    $running = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($sandboxRootPrefix, [StringComparison]::OrdinalIgnoreCase) }
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
    Start-Process -FilePath "$sandboxRoot\Newsroom.Worker.exe" -WorkingDirectory $sandboxRoot -WindowStyle Hidden `
        -RedirectStandardOutput $startupStdout -RedirectStandardError $startupStderr

    Start-Sleep -Seconds 6
    $proc = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($sandboxRootPrefix, [StringComparison]::OrdinalIgnoreCase) }
    if (-not $proc) {
        Write-Host "Sandbox did not stay up - the guard most likely refused it. Startup output:"
        if (Test-Path $startupStdout) {
            Write-Host "--- $startupStdout ---"
            Get-Content $startupStdout -Tail 40 -Encoding UTF8
        }
        $stderrContent = if (Test-Path $startupStderr) { Get-Content $startupStderr -Tail 40 -Encoding UTF8 } else { $null }
        if ($stderrContent) {
            Write-Host "--- $startupStderr ---"
            $stderrContent
        }
        throw "Sandbox worker exited. Fix the reported violations and run again."
    }
    Write-Host "Sandbox running (PID $($proc.Id -join ', '))."

    $log = Get-ChildItem "$sandboxRoot\logs\sandbox-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) {
        Write-Host "--- $($log.Name) (last 10 lines - look for the SANDBOX MODE banner) ---"
        Get-Content $log.FullName -Tail 10 -Encoding UTF8
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
