# Runbook — Move Both Applications to a New Host

**Status:** Agreed · **Last updated:** 2026-08-04

Predel-News (the website) and the newsroom worker share one Windows host. This runbook inventories
every piece of state that does **not** travel with the code, and the order to rebuild it in.

Written because the deploy on 2026-08-03/04 spread that knowledge across a conversation, and
because the current host is Windows Server 2012 R2 whose Extended Security Updates end
**2026-10-13** — a move is a scheduled certainty, not a hypothetical.

## Why state is not all in one folder

The obvious simplification — put logs, images and uploads inside each application's folder so a
move is one copy — was considered and rejected for the worker's images. Two concrete reasons, both
in the release tooling rather than the applications:

- The worker's `tools/rollback.ps1` mirrors a backup over `C:\apps\newsroom` with
  `robocopy /MIR /XD logs`. `/MIR` purges anything in the target that is absent from the backup,
  and only `logs` is excluded — so an `images` folder beside the binaries would be **deleted by the
  first rollback**.
- The worker's `tools/deploy.ps1` backs the target up with a full recursive copy and no exclusions,
  so every release would duplicate the entire image library into a timestamped backup folder.

Hence ADR-0013: images live under a persistent root **outside** the deployment directory, so the
install folder stays disposable. Colocating them would require fixing both scripts first, and would
still leave the databases, IIS configuration and SQL logins outside any app folder — which is the
real reason a move needs a procedure rather than a copy.

The website is different and is *already* colocated: Umbraco keeps `wwwroot\media` and
`umbraco\Data` inside the site folder by design. The site's own `deploy.ps1` and `rollback.ps1`
exclude both from copy, backup and purge — see Predel-News `docs/technical/deployment.md`.

## Inventory — everything that is not in git

Paths are the ones measured on the current host (2026-08-04).

| # | State | Location | Notes |
|---|---|---|---|
| 1 | **Website database** | SQL Server, `PredelNews` | The whole CMS: content, media metadata, users, `pn_*` tables. Back up and restore; do not recreate. |
| 2 | **Worker database** | SQL Server, `Newsroom` | Sources, articles, topics, drafts, publish ledger. The `nw_PublishRecord` idempotency ledger matters — losing it can republish articles. |
| 3 | **Website secrets** | `C:\DATA\SITES\predelnews.com\appsettings.Production.json` | Connection string, admin credentials, `PredelNews:Newsroom:ClientSecret`, unattended-install settings, Serilog path. Not in git, ACL-restricted. |
| 4 | **Worker secrets** | `C:\apps\newsroom\appsettings.Production.json` | Gemini, Telegram, Facebook, Pixabay/Pexels/Cloudflare keys, and `Umbraco:ClientSecret` — which must equal #3's value. |
| 5 | **Uploaded media** | `C:\DATA\SITES\predelnews.com\wwwroot\media` | Every image in published articles. Irreplaceable. |
| 6 | **Worker images** | `%ProgramData%\PredelNewsroom\images` | `generated-images`, `editor-uploads`, `public-figures`, `branding`. Referenced by relative key from `nw_DraftImage`, so it must move with the `Newsroom` database. |
| 7 | **IIS site** | site `predelnews.com` → `C:\DATA\SITES\predelnews.com`, app pool `PredelNews` | Pool must be **No Managed Code**; identity `ApplicationPoolIdentity`. |
| 8 | **Windows service** | `PredelNewsroom` → `C:\apps\newsroom\Newsroom.Worker.exe` | Runs as `NT SERVICE\PredelNewsroom`; recovery options restart after 1/5/15 min. |
| 9 | **SQL logins** | `IIS APPPOOL\PredelNews`, `NT SERVICE\PredelNewsroom` | Windows logins, `db_owner` on their own database only. Both are host-local principals — they **cannot** be migrated, only recreated. |
| 10 | **Folder ACLs** | site `logs`, `C:\apps\newsroom`, the image root, both config files | The app pool and service accounts are not administrators; without these the apps start and then silently fail to log or store images. |
| 11 | **Timezone** | `FLE Standard Time` | The worker schedules on `DateTime.Now` — Facebook windows, the 09:00 digest, retention's "today". A UTC host puts every post hours out. |
| 12 | **DNS / TLS** | Cloudflare — NS `*.ns.cloudflare.com` | SSL mode must be **Full** or **Full (strict)**. On Flexible the site returns `ERR_TOO_MANY_REDIRECTS`, because nothing reads `X-Forwarded-Proto`. |
| 13 | **Facebook token** | in #4 | Long-lived Page token; see [facebook-token-renewal.md](facebook-token-renewal.md). |

Not state, but required on the new host: **.NET 10 Hosting Bundle** (not just the runtime — it
installs the ASP.NET Core Module), **SQL Server 2016 or later** with a **case-insensitive**
collation (Umbraco 17 requires CI), and `sqlcmd` on PATH for the pre-deploy backup.

## Order

Sequence matters in two places: the site must exist before the worker (the worker authenticates as
an Umbraco API user the site creates at startup), and the Windows service must exist before its SQL
login (the `NT SERVICE\…` principal is only registered when the service is created).

1. **Prepare the host** — Hosting Bundle, IIS, SQL Server, timezone (#11), `sqlcmd`.
2. **Restore both databases** (#1, #2) from backup. Do not let the applications create them.
3. **Recreate the IIS site and app pool** (#7), No Managed Code.
4. **Copy the website** publish output, then its config (#3) and media (#5).
5. **Create the site's SQL login** (#9) and grant `db_owner` on `PredelNews`; apply ACLs (#10).
6. **Point DNS at the new host and sort TLS** (#12) before expecting the site to answer publicly.
7. **Verify the site**: homepage renders, `/umbraco/` logs in, log file appears.
8. **Copy the worker** to `C:\apps\newsroom`, then `install-service.ps1 -ServiceAccount "NT SERVICE\PredelNewsroom"`.
9. **Create the worker's SQL login** (#9) — only possible now — and grant `db_owner` on `Newsroom`.
10. **Copy the image root** (#6) and its config (#4); apply ACLs (#10).
11. **Start the worker**, then `/status` in Telegram.
12. **End-to-end check**: approve one draft and confirm the article appears on the site. That is the
    only step exercising the shared secret in #3/#4 — a mismatch shows up nowhere else until a
    publish fails.

## Rehearse the parts that are cheap to rehearse

Steps 2 and 12 are the ones that fail quietly. Restoring a `.bak` into a scratch database and
running one draft through approval both cost minutes and catch most of what goes wrong.

`tools/preflight.ps1` and `tools/sql-check.ps1` in the Predel-News repo verify most of §Inventory
on a new host before anything is copied — run both first.
