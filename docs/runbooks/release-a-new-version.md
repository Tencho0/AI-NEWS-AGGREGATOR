# Runbook — Release a New Version

**Status:** Agreed · **Last updated:** 2026-08-04

How the live deployment is laid out, and the exact steps to ship a newer build of either
application. Written from the first production deploy (2026-08-03/04), so every path and command
here is one that actually ran rather than one that ought to work.

Deeper detail lives in Predel-News `docs/technical/deployment.md` (site) and
[deploy.md](deploy.md) (worker). Moving to a different host is
[move-to-a-new-host.md](move-to-a-new-host.md).

---

## 1. The deployed environment

Measured 2026-08-04. **A shared host** — it runs ~25 production sites, so nothing here may restart
IIS or reboot the machine.

| | |
|---|---|
| Host OS | Windows Server 2012 R2 Standard (6.3.9600), PowerShell **4.0** |
| Runtime | .NET 10 Hosting Bundle (`Microsoft.NETCore.App` + `Microsoft.AspNetCore.App`) |
| SQL Server | `.\SQLEXPRESS` — 2017 Express (14.0.1000.169), collation `SQL_Latin1_General_CP1_CI_AS` |
| Timezone | `FLE Standard Time` — the worker schedules on local time |
| DNS / TLS | Cloudflare (proxied), SSL mode **Full** |
| Admin account | `Administrator` — used to run every script below |

### Website

| | |
|---|---|
| IIS site | `predelnews.com` |
| Web root | `C:\DATA\SITES\predelnews.com` |
| App pool | `PredelNews` — **No Managed Code**, `ApplicationPoolIdentity` |
| SQL login | `IIS APPPOOL\PredelNews`, `db_owner` on `PredelNews` |
| Database | `PredelNews` |
| Config (secrets) | `C:\DATA\SITES\predelnews.com\appsettings.Production.json` |
| Logs | `C:\DATA\SITES\predelnews.com\logs\predelnews-<date>.log` |
| Media | `C:\DATA\SITES\predelnews.com\wwwroot\media` |
| Examine / NuCache | `C:\DATA\SITES\predelnews.com\umbraco\Data` |
| Release backups | `C:\DATA\SITES\predelnews.com-backup-<yyyyMMdd-HHmmss>` (keep **1**) |
| DB backups | `C:\backups\predelnews` (writable by `NT Service\MSSQL$SQLEXPRESS`) |

### Worker

| | |
|---|---|
| Service | `PredelNewsroom`, auto-start, restart after 1/5/15 min |
| Runs as | `NT SERVICE\PredelNewsroom` — virtual account, no password |
| Install folder | `C:\apps\newsroom` |
| SQL login | `NT SERVICE\PredelNewsroom`, `db_owner` on `Newsroom` |
| Database | `Newsroom` |
| Config (secrets) | `C:\apps\newsroom\appsettings.Production.json` |
| Logs | `C:\apps\newsroom\logs\newsroom-<date>.log` |
| Images | `C:\ProgramData\PredelNewsroom\images` — `generated-images`, `editor-uploads`, `public-figures`, `branding`. **Outside** the install folder (ADR-0013) |

### Where the release scripts live on the host

The scripts are **not** deployed with either application — they are copied to the host separately
and kept there. Canonical locations:

| Folder | Contents | From |
|---|---|---|
| `C:\deploy\site-tools\` | `deploy.ps1`, `rollback.ps1`, `preflight.ps1`, `sql-check.ps1`, `README.txt` | Predel-News `tools/` |
| `C:\deploy\worker-tools\` | `deploy.ps1`, `rollback.ps1`, `install-service.ps1`, `seed-sources.sql`, `check-dotnet-runtime.ps1`, `list-dotnet-versions.ps1`, `README.txt` | AI-NEWS-AGGREGATOR `tools/` |
| `C:\deploy\` | staging: the publish zip and the folder it is extracted to | transient, delete after a release |

**Both folders contain a `deploy.ps1` and a `rollback.ps1` with identical names and different
jobs** — one drives an IIS app pool, the other a Windows service. Keep them in separate folders and
never flatten them together. Each refuses to run unless it finds its own binary in the publish
source (`PredelNews.Web.dll` / `Newsroom.Worker.exe`), so a mix-up fails with a clear error rather
than deploying the wrong application — but the folder separation is what stops the confusion in the
first place. Each folder carries a `README.txt` naming which application it belongs to, so an
unzipped copy is self-describing.

Identify a loose script with:

```powershell
Select-String -Path .\deploy.ps1 -Pattern "Newsroom.Worker.exe|PredelNews.Web.dll" |
  Select-Object -First 1 -ExpandProperty Line
```

`restart-worker.ps1` from the worker repo is **deliberately not on the host**: it forces
`DOTNET_ENVIRONMENT=Development`, which loads `dotnet user-secrets` instead of
`appsettings.Production.json` — on this host that starts the worker with no configuration at all.

Refresh the scripts from the repos whenever they change; they are versioned there, and the copies
on the host are just copies. The defaults baked into them match this host, so a normal release needs
only `-PublishSource`.

### Configuration is by file, not environment variable

Both applications read `appsettings.Production.json` from their own folder. **No machine
environment variables are set, and none should be.** IIS inherits its environment from WAS and a
Windows service from `services.exe`; both cache the block, so a new variable needs a WAS restart or
a reboot — an outage for all ~25 sites. `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` are unset
because both default to `Production`, which is what loads the file.

Both `deploy.ps1` scripts exclude `appsettings.Production.json` from every release copy, so it
survives upgrades. Both files are ACL-restricted to their app account (read), Administrators and
SYSTEM.

**`Umbraco:ClientSecret` (worker) must equal `PredelNews:Newsroom:ClientSecret` (site).** The site
re-registers the credential on every boot, so rotating means: change both files, recycle the app
pool, restart the service.

---

## 2. Releasing a new website version

### On the dev machine

```powershell
cd "<repo>\Predel-News"
git pull
dotnet build PredelNews.slnx
dotnet test PredelNews.slnx
dotnet publish src/Web/PredelNews.Web -c Release -o C:\pub
```

Then zip it — 5,000+ files over RDP is slow and can drop files silently:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path ([Environment]::GetFolderPath('Desktop')) "predelnews-publish.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory("C:\pub", $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
```

~393 MB of output compresses to ~140 MB.

### On the VPS

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
Remove-Item C:\deploy\publish -Recurse -Force -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::ExtractToDirectory("C:\deploy\predelnews-publish.zip", "C:\deploy\publish")

cd <site tools folder>
.\deploy.ps1 -PublishSource C:\deploy\publish -SkipSmokeTest
```

Defaults already target the right web root, app pool and site URL — no other parameters needed.
`-SkipSmokeTest` because PowerShell 4.0 negotiates TLS 1.0 by default and Cloudflare requires
1.2+, so the HTTPS check fails on a healthy site.

The script stops the app pool, backs up the web root, copies the output on top (leaving config,
media, indexes and logs), and restarts the pool. **Only this site is affected.**

### Verify

```powershell
Invoke-WebRequest -Uri "http://localhost/" -Headers @{Host="predelnews.com"} -UseBasicParsing -MaximumRedirection 0 -ErrorAction SilentlyContinue | Out-Null
Get-Content "C:\DATA\SITES\predelnews.com\logs\predelnews-*.log" -Tail 40 -Encoding UTF8
```

`HTTP 307` from that request is success — it is the HTTPS redirect, proving the app is running.
Then open `https://predelnews.com` and `/umbraco/` **from a modern browser**, not the VPS: Server
2012 R2's Internet Explorer cannot render the Umbraco 17 backoffice.

---

## 3. Releasing a new worker version

### On the dev machine

```powershell
cd "<repo>\AI-NEWS-AGGREGATOR"
git pull
dotnet build Newsroom.slnx -warnaserror
dotnet test Newsroom.slnx
dotnet publish src/Newsroom.Worker -c Release -r win-x64 --self-contained false -o C:\pubw
```

Zip as above (~119 MB → ~33 MB).

### On the VPS

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
Remove-Item C:\deploy\workerpub -Recurse -Force -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::ExtractToDirectory("C:\deploy\newsroom-worker-publish.zip", "C:\deploy\workerpub")

cd <worker tools folder>
.\deploy.ps1 -PublishSource C:\deploy\workerpub
```

Stops the service, backs up `C:\apps\newsroom`, copies on top (config preserved), restarts, tails
the log. Migrations run inside the worker at startup.

### Verify

```powershell
Get-Service PredelNewsroom
Get-Content "C:\apps\newsroom\logs\newsroom-*.log" -Tail 40
```

Then `/status` in Telegram.

---

## 4. Gotchas this host will hand you

Each of these cost time on the first deploy.

| Symptom | Cause |
|---|---|
| `MSB3021 … exceeds the OS max path limit` on publish | Umbraco's backoffice assets nest ~146 chars below the output root. Publish to a **short** path such as `C:\pub`. |
| `MSB3021 … file is locked by "PredelNews.Web"` on build | A local `dotnet run`/`dotnet watch` is still running. Stop it. |
| `Expand-Archive is not recognized` | PowerShell 4.0. Use `[System.IO.Compression.ZipFile]::ExtractToDirectory`. |
| `ExtractToDirectory … already exists` | Delete the destination folder first. |
| `Get-Content … parameter cannot be found … 'Encoding'` | The wildcard matched no files. The log does not exist yet — not a parameter problem. |
| Cyrillic shows as `???` in sqlcmd, or `â€”` in a log | Console codepage, not corrupted data. Read logs with `-Encoding UTF8`; for sqlcmd verify with `SELECT UNICODE(LEFT(Name,1))` — Cyrillic is 1040–1103. |
| Site returns endless redirects | Cloudflare SSL mode set to **Flexible**. Nothing reads `X-Forwarded-Proto`; it must be Full or Full (strict). |
| App runs but writes no log | The `logs` folder must be writable by the app account. Serilog swallows write failures silently. |
| Two scripts named `deploy.ps1` | Both repos have one, and they are different. Keep them in separate folders. Each refuses to run without its own binary in the publish source, so a mix-up fails safely. |

---

## 5. Rollback

```powershell
.\rollback.ps1          # site, or the worker's own copy
```

Mirrors the newest backup back, protecting media, indexes and logs. The site's version refuses a
backup that lacks `PredelNews.Web.dll` or the parent of a preserved folder — `/MIR` would otherwise
delete the media library.

Only **one** site backup is retained (`-KeepBackups 1`), because free space is tight. That means one
release of rollback depth.

Databases are not rolled back. Umbraco migrations are forward-only, so N-1 binaries normally run
against the current schema. `-RestoreDatabase` exists for an Umbraco version bump and discards
every content change since the backup.

---

## 6. Watch the disk

**2.7 GB free on C: as of 2026-08-04**, on the volume shared by ~25 sites and all their databases.
A site release needs ~800 MB transient (copy plus backup). Filling C: takes every site on the host
down, not just this one.

```powershell
Get-PSDrive C | Select-Object @{n='FreeGB';e={[math]::Round($_.Free/1GB,1)}}
```

Check it **before** every release. Delete the extracted publish folder and the zip afterwards —
that is ~570 MB of reclaimable staging. There is no second volume: `D:` and `E:` report 0 bytes.
