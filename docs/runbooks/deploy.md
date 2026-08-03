# Runbook — Deploy a Release

**Status:** Agreed · **Last updated:** 2026-07-03

Release procedure from [09-deployment.md](../09-deployment.md). The worker runs as the Windows
Service `PredelNewsroom` on the Predel-News VPS from `C:\apps\newsroom`. Migrations are applied
by the worker itself at startup (forward-only, backward-compatible), so a deploy is: publish →
copy → `tools\deploy.ps1`.

## 1. Build and publish

On the dev machine (or CI), from the repo root:

```powershell
dotnet build Newsroom.slnx -warnaserror
dotnet test Newsroom.slnx
dotnet publish src/Newsroom.Worker -c Release -r win-x64 --self-contained false -o publish
```

All warnings are errors and all tests must be green before anything leaves the machine.

## 2. Copy to the VPS

Copy the `publish` folder to the VPS (RDP copy/paste or `scp`) — e.g. to
`C:\deploy\publish`. Delivery is manual in v1 (docs/09-deployment.md).

## 3. Release

From an **elevated** PowerShell on the VPS, in the repo's `tools` folder (or with the two
scripts copied next to the publish output):

```powershell
.\deploy.ps1 -PublishSource C:\deploy\publish
```

The script: stops the service → backs up `C:\apps\newsroom` to
`C:\apps\newsroom-backup-<yyyyMMdd-HHmmss>` (keeps the newest 3) → robocopies the publish
output on top, leaving an existing `appsettings.Production.json` untouched → starts the
service → tails the newest log 20 lines. Non-zero exit = failed deploy; fix or run
[`rollback.ps1`](../../tools/rollback.ps1) (see [rollback.md](rollback.md)).

## 4. Post-deploy checklist

- Service `Running`; startup log clean (`Applied migration …` / `Database schema is up to date`).
- `/status` responds in Telegram; no watchdog ⚠️ within ~15 minutes.
- After significant changes: one dry-run draft cycle in the staging chat
  (docs/09-deployment.md, "Staging = dry-run mode on prod VPS").

## First-time install (once per VPS)

1. Copy the publish output to `C:\apps\newsroom` (deploy.ps1 does this too — it just skips
   the service stop/start when the service does not exist yet).
2. Create the service (elevated), running as its **virtual service account**:

   ```powershell
   .\install-service.ps1 -ServiceAccount "NT SERVICE\PredelNewsroom"
   ```

   This sets `start=auto` and recovery options: restart after 1 min / 5 min / 15 min,
   failure counter reset daily (docs/07-operations.md).

   **Decided 2026-08-03: not LocalSystem.** The worker parses untrusted remote content all day
   (RSS, scraped HTML, AI output, downloaded images) and needs almost no privilege to do it —
   one database, one folder, outbound HTTP. `NT SERVICE\PredelNewsroom` is created and managed
   by Windows, has no password to store or rotate, and is the exact analogue of the site's
   `IIS APPPOOL\PredelNews`. The VPS hosts ~25 production sites, so LocalSystem's blast radius
   is every one of them, and granting `NT AUTHORITY\SYSTEM` rights on the newsroom database
   would share that access with every other LocalSystem service on the box.

   The trade is two `icacls` commands in step 4 — LocalSystem would not need them.
3. Set **machine-level environment variables** — production configuration and secrets are
   machine env vars with `__` separators, **not** `dotnet user-secrets` (user-secrets are a
   dev-only mechanism; docs/06-security.md). From an elevated prompt:

   ```powershell
   [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Production', 'Machine')
   ```

   Every secret/setting the worker needs:

   | Variable | What it is |
   |---|---|
   | `ConnectionStrings__Newsroom` | SQL Server connection string. **Not needed on this VPS** — the `appsettings.json` default already targets `.\SQLEXPRESS` with `Integrated Security=True`, which is what the virtual service account needs. |
   | `Ai__Gemini__ApiKey` | Gemini API key (AI analysis / clustering / drafting) |
   | `Telegram__BotToken` | Telegram bot token for the review bot |
   | `Telegram__ReviewChatId` | review-chat id (negative for groups) |
   | `Telegram__AllowedUserIds__0` | first allowlisted editor id (`__1`, `__2`… for more) |
   | `Umbraco__BaseUrl` | Predel-News site base URL (publishing endpoint) |
   | `Umbraco__ClientSecret` | shared secret of the publishing endpoint |
   | `Facebook__PageId` | Facebook page id |
   | `Facebook__AccessToken` | long-lived Page access token (see [facebook-token-renewal.md](facebook-token-renewal.md)) |
   | `Facebook__DryRun` | `false` to actually post (defaults to `true` — dry-run) |
   | `Images__Pixabay__ApiKey` | Pixabay stock-image key |
   | `Images__Pexels__ApiKey` | Pexels stock-image key |

   (Alternatively the same keys can live in an ACL-restricted
   `C:\apps\newsroom\appsettings.Production.json` — deploy.ps1 never overwrites it. Pick one
   mechanism and stick to it; env vars are the default.)
4. Grant the service account write access to the two places the worker writes. It is **not**
   an administrator, so without these it starts and then fails to log or store images:

   ```powershell
   New-Item -ItemType Directory -Path "C:\ProgramData\PredelNewsroom" -Force
   icacls "C:\apps\newsroom"              /grant "NT SERVICE\PredelNewsroom:(OI)(CI)M" /T
   icacls "C:\ProgramData\PredelNewsroom" /grant "NT SERVICE\PredelNewsroom:(OI)(CI)M" /T
   ```

   `C:\apps\newsroom\logs\` is the Serilog sink; `C:\ProgramData\PredelNewsroom\images\` is the
   persistent image storage that deliberately lives outside the deployment folder (ADR-0013),
   holding generated covers, editor uploads and public-figure reference photos.

4b. Give the service account a SQL login and rights on its database. On the shared VPS the
   `Newsroom` database is **pre-created by hand** — `NT SERVICE\PredelNewsroom` has no
   `CREATE DATABASE` permission, so `EnsureDatabaseExistsAsync` would throw at startup:

   ```powershell
   sqlcmd -S .\SQLEXPRESS -E -b -Q "IF DB_ID(N'Newsroom') IS NULL CREATE DATABASE [Newsroom] COLLATE SQL_Latin1_General_CP1_CI_AS;"
   sqlcmd -S .\SQLEXPRESS -E -b -Q "IF SUSER_ID(N'NT SERVICE\PredelNewsroom') IS NULL CREATE LOGIN [NT SERVICE\PredelNewsroom] FROM WINDOWS;"
   sqlcmd -S .\SQLEXPRESS -E -b -d Newsroom -Q "IF DATABASE_PRINCIPAL_ID(N'NT SERVICE\PredelNewsroom') IS NULL CREATE USER [NT SERVICE\PredelNewsroom] FOR LOGIN [NT SERVICE\PredelNewsroom]; ALTER ROLE db_owner ADD MEMBER [NT SERVICE\PredelNewsroom];"
   ```

   With the database already present the worker's existence check passes and it goes straight
   to migrations. `db_owner` on `Newsroom` only — no server-level rights.

   The service must exist before the login is created: Windows only registers the
   `NT SERVICE\PredelNewsroom` principal once `install-service.ps1` has run.
5. Reboot (or restart the Services host) so the Service Control Manager picks up the new
   machine variables, then `Start-Service PredelNewsroom` and run the post-deploy checklist.
