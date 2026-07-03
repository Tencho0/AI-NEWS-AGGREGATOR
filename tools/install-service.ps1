<#
.SYNOPSIS
    First-time installation of the newsroom worker as a Windows Service.
.DESCRIPTION
    Creates the service with automatic start and restart-on-failure recovery options
    (1 min / 5 min / 15 min, failure counter reset daily), per docs/09-deployment.md.
    Copy the publish output to -BinPath first (tools\deploy.ps1 does that on releases).
    Run elevated. See docs/runbooks/deploy.md for the full first-install checklist.
.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -BinPath C:\apps\newsroom -ServiceAccount ".\svc-newsroom"
#>
[CmdletBinding()]
param(
    [string]$BinPath = "C:\apps\newsroom",
    [string]$ServiceName = "PredelNewsroom",
    [string]$ServiceAccount
)

$ErrorActionPreference = "Stop"

try {
    $exePath = Join-Path $BinPath "Newsroom.Worker.exe"
    if (-not (Test-Path $exePath)) {
        throw "Worker binary not found at '$exePath'. Copy the publish output there first (see docs/runbooks/deploy.md)."
    }

    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        throw "Service '$ServiceName' already exists. Use tools\deploy.ps1 for releases."
    }

    Write-Host "Creating service '$ServiceName' -> $exePath"
    $scArgs = @("create", $ServiceName, "binPath=", $exePath, "start=", "auto")
    if ($ServiceAccount) {
        $scArgs += @("obj=", $ServiceAccount)
        Write-Host "Service account: $ServiceAccount (sc.exe will prompt for the password)"
    }
    & sc.exe @scArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }

    Write-Host "Setting recovery options (restart after 1 min / 5 min / 15 min, reset daily)"
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/300000/restart/900000 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failure (recovery options) failed with exit code $LASTEXITCODE." }

    Write-Host ""
    Write-Host "Service '$ServiceName' installed (not started yet)."
    Write-Host ""
    Write-Host "Next steps (details and the full secret list: docs/runbooks/deploy.md):"
    Write-Host "  1. Set machine-level environment variables (from an elevated prompt)."
    Write-Host "     Configuration keys use double underscores as separators - do NOT use user-secrets in production:"
    Write-Host "       [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Production', 'Machine')"
    Write-Host "       [Environment]::SetEnvironmentVariable('ConnectionStrings__Newsroom', '<connection string>', 'Machine')"
    Write-Host "       [Environment]::SetEnvironmentVariable('Ai__Gemini__ApiKey', '<key>', 'Machine')"
    Write-Host "       [Environment]::SetEnvironmentVariable('Telegram__BotToken', '<token>', 'Machine')"
    Write-Host "       [Environment]::SetEnvironmentVariable('Telegram__ReviewChatId', '<chat id>', 'Machine')"
    Write-Host "       [Environment]::SetEnvironmentVariable('Telegram__AllowedUserIds__0', '<editor user id>', 'Machine')"
    Write-Host "       [Environment]::SetEnvironmentVariable('Umbraco__BaseUrl', '<https://site>', 'Machine')"
    Write-Host "       [Environment]::SetEnvironmentVariable('Umbraco__ClientSecret', '<secret>', 'Machine')"
    Write-Host "       [Environment]::SetEnvironmentVariable('Facebook__PageId', '<page id>', 'Machine')"
    Write-Host "       [Environment]::SetEnvironmentVariable('Facebook__AccessToken', '<page token>', 'Machine')"
    Write-Host "       [Environment]::SetEnvironmentVariable('Images__Pixabay__ApiKey', '<key>', 'Machine')"
    Write-Host "       [Environment]::SetEnvironmentVariable('Images__Pexels__ApiKey', '<key>', 'Machine')"
    Write-Host "  2. Grant the service account modify rights on '$BinPath' (logs folder)."
    Write-Host "  3. Reboot (or restart the Services host) so the service sees the new variables, then:"
    Write-Host "       Start-Service $ServiceName"
    Write-Host "  4. Verify: startup log clean, migrations applied, /status responds in Telegram."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
