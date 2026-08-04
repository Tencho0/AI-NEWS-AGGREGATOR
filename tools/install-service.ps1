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
    $account = if ($ServiceAccount) { $ServiceAccount } else { "NT AUTHORITY\SYSTEM" }

    Write-Host "Next steps (details and the full key list: docs/runbooks/deploy.md):"
    Write-Host "  1. Write '$BinPath\appsettings.Production.json' with the secrets."
    Write-Host "     Host.CreateApplicationBuilder loads it automatically and defaults the"
    Write-Host "     environment to Production, so NO environment variable is needed - and no"
    Write-Host "     reboot. Machine variables come from services.exe, which caches its"
    Write-Host "     environment block; picking a new one up needs a reboot or an SCM restart,"
    Write-Host "     which on a shared host is an outage for every service on the box."
    Write-Host "     Keys: Ai:Gemini:ApiKey, Telegram:BotToken, Telegram:ReviewChatId,"
    Write-Host "     Telegram:AllowedUserIds, Umbraco:BaseUrl, Umbraco:ClientSecret,"
    Write-Host "     Facebook:PageId, Facebook:AccessToken, Facebook:DryRun,"
    Write-Host "     Images:Pixabay:ApiKey, Images:Pexels:ApiKey, Images:Cloudflare:*"
    Write-Host "     Umbraco:ClientSecret must equal the SITE's PredelNews:Newsroom:ClientSecret"
    Write-Host "     as deployed - not the dev machine's, which points at another Umbraco."
    Write-Host "     A connection string is only needed when it differs from appsettings.json."
    Write-Host "  2. Grant '$account' access to the paths the worker writes:"
    Write-Host "       icacls `"$BinPath`" /grant `"${account}:(OI)(CI)M`" /T"
    Write-Host "       icacls `"`$env:ProgramData\PredelNewsroom`" /grant `"${account}:(OI)(CI)M`" /T"
    Write-Host "     and read-only on the config: icacls <config> /inheritance:r /grant `"${account}:R`""
    Write-Host "  3. Give '$account' a SQL login and db_owner on the Newsroom database. Pre-create"
    Write-Host "     the database when the account lacks CREATE DATABASE, or startup throws."
    Write-Host "  4. Start-Service $ServiceName   (no reboot required)"
    Write-Host "  5. Verify: startup log clean, migrations applied, /status responds in Telegram."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
