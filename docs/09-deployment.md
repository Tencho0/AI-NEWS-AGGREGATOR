# 09 — Deployment Strategy

**Status:** Draft · **Last updated:** 2026-07-02

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
| Local dev | dev machine + local SQL Express, test Telegram bot/chat, Umbraco dev site, FB dry-run flag | daily development |
| Staging = "dry-run mode on prod VPS" | same binaries, config flags: test chat, Umbraco dev/staging site, `Facebook:DryRun=true` | pre-release smoke |
| Production | VPS service `PredelNewsroom` | live |

(A separate staging VPS is deliberately out of scope for v1 — dry-run flags substitute.)

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
