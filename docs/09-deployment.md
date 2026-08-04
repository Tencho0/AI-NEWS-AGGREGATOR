# 09 — Deployment Strategy

**Status:** Draft · **Last updated:** 2026-08-04

## Target environment

Same Windows VPS as the Predel-News site (Windows Server, IIS, SQL Server Express 2022).
The worker is **not** an IIS app — it runs as a **Windows Service** (long-running background
jobs, no inbound HTTP surface).

```
C:\apps\newsroom\            # service binaries (publish output)
C:\apps\newsroom\logs\
C:\apps\newsroom\appsettings.Production.json   # secrets, ACL-restricted, NOT in git
```

## Environments

| Env | Where | Purpose |
|---|---|---|
| Sandbox | This dev machine, and only this dev machine. `DOTNET_ENVIRONMENT=Sandbox`, the `Newsroom_Sandbox` database, the `newsroom-worker-sandbox` user-secrets store (never the live one), a separate Telegram bot, local Umbraco at `https://localhost:44350`, Facebook forced to dry-run in code regardless of config. Started with `tools\restart-sandbox.ps1`. A fail-closed guard refuses to start against anything that looks live. See [ADR-0014](adr/0014-sandbox-mode.md) and [runbooks/run-the-sandbox.md](runbooks/run-the-sandbox.md). | daily development against the full pipeline — real scraping, real AI, real Telegram review — with no path to the live database, site or Facebook page |
| Staging = "dry-run mode on prod VPS" | same binaries, config flags: test chat, Umbraco dev/staging site, `Facebook:DryRun=true` | pre-release smoke |
| Production | Predel-News VPS, Windows Service `PredelNewsroom`, `C:\apps\newsroom` | live |

(A separate staging VPS is deliberately out of scope for v1 — dry-run flags substitute.)

**This dev machine does not run the live pipeline.** The live worker is the VPS Windows Service
above, running from its own `C:\apps\newsroom` on that host — see
[runbooks/release-a-new-version.md](runbooks/release-a-new-version.md) and
[runbooks/deploy.md](runbooks/deploy.md) for how it got there and how a new version ships. Before
the sandbox existed, the only way to develop on this dev machine was a second,
`Development`-environment worker that shares the live Telegram bot token, Gemini key and Facebook
credentials (via this machine's own `dotnet user-secrets`) with the VPS instance; that worker now
runs from its own local `C:\apps\newsroom` rather than `bin\Debug` (so a plain `dotnet build` no
longer needs it stopped first), but it is still a different folder on a different machine from the
VPS's `C:\apps\newsroom`, and it is not where live belongs — see
[runbooks/start-the-worker.md](runbooks/start-the-worker.md) for the full caveat. The Sandbox
environment above is what this dev machine should actually run day to day.

## Install & release process

1. Build: `dotnet publish src/Newsroom.Worker -c Release -r win-x64 --self-contained false`.
2. First install (once, elevated):
   `sc.exe create PredelNewsroom binPath="C:\apps\newsroom\Newsroom.Worker.exe" start=auto obj=<service account>`
   + recovery options (restart on failure), + grant folder ACLs.
3. Release (scripted `tools/deploy.ps1`): stop service → back up current folder → copy publish
   output (config file untouched) → run pending DB migrations (worker also applies at startup) →
   start service → tail log → check `/status` in Telegram.
4. **Rollback:** stop service, restore previous folder backup, start. Migrations are
   forward-only; write them backward-compatible (add columns, don't repurpose) so N-1 binaries
   still run — standard rule recorded here so schema PRs get reviewed against it.

CI (GitHub Actions) produces the publish artifact per tagged release; copying to the VPS is
manual (RDP/`scp`) for v1 — automating delivery is a later phase if release cadence justifies it.

## Versioning & releases

- SemVer tags (`v0.3.0`); CHANGELOG.md maintained per release.
- The Umbraco-side publishing endpoint versions independently in the Predel-News repo; contract
  compatibility rules live in [05-integrations/umbraco.md](05-integrations/umbraco.md).

## Post-deploy checklist (also in runbook)

- Service running; startup log clean; migrations applied.
- `/status` responds; daily digest scheduled.
- One dry-run draft cycle in staging chat after significant changes.
