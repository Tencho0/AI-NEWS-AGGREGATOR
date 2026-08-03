<#
.SYNOPSIS
    First-time installation of the newsroom worker as a Windows Service.
.DESCRIPTION
    Creates the service with automatic start and restart-on-failure recovery options
    (1 min / 5 min / 15 min, failure counter reset daily), per docs/09-deployment.md.
    Copy the publish output to -BinPath first (tools\deploy.ps1 does that on releases).
    Run elevated. See docs/runbooks/deploy.md for the full first-install checklist.
.EXAMPLE
    # Default: runs as LocalSystem - the most privileged account on the machine.
    .\install-service.ps1
.EXAMPLE
    # Preferred on a shared host: a per-service virtual account. Windows creates and manages it,
    # there is no password, and it is the direct equivalent of an IIS AppPoolIdentity.
    # Grant it db_owner on Newsroom: CREATE LOGIN [NT SERVICE\PredelNewsroom] FROM WINDOWS;
    .\install-service.ps1 -ServiceAccount "NT SERVICE\PredelNewsroom"
.EXAMPLE
    # A real local account needs its password passed through - sc.exe never prompts.
    .\install-service.ps1 -ServiceAccount ".\svc-newsroom" -ServiceAccountPassword (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
    [string]$BinPath = "C:\apps\newsroom",
    [string]$ServiceName = "PredelNewsroom",
    [string]$ServiceAccount,
    [securestring]$ServiceAccountPassword
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
    $passwordBstr = [IntPtr]::Zero
    if ($ServiceAccount) {
        $scArgs += @("obj=", $ServiceAccount)

        # Built-in and virtual accounts (LocalSystem, NT AUTHORITY\*, NT SERVICE\*) have no
        # password. Everything else does - and sc.exe does NOT prompt for it, so omitting it
        # creates a service with a blank password that fails at first start with 1069.
        $needsPassword = $ServiceAccount -notmatch '^(LocalSystem|NT SERVICE\\|NT AUTHORITY\\)'
        if ($needsPassword) {
            if (-not $ServiceAccountPassword) {
                throw "Account '$ServiceAccount' needs a password. Pass -ServiceAccountPassword (Read-Host -AsSecureString), or use a virtual account such as 'NT SERVICE\$ServiceName' which has none."
            }
            $passwordBstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServiceAccountPassword)
            # NOTE: sc.exe takes the password as an argument, so it is briefly visible in this
            # process's command line. A virtual account avoids the exposure entirely.
            $scArgs += @("password=", [Runtime.InteropServices.Marshal]::PtrToStringUni($passwordBstr))
        }
        Write-Host "Service account: $ServiceAccount$(if (-not $needsPassword) { ' (no password required)' })"
    }
    try {
        & sc.exe @scArgs | Out-Host
    }
    finally {
        if ($passwordBstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordBstr)
        }
    }
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
