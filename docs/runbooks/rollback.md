# Runbook — Rollback a Release

**Status:** Agreed · **Last updated:** 2026-07-03

When a release misbehaves, restore the previous binaries from the folder backup that
[`deploy.ps1`](../../tools/deploy.ps1) took (docs/09-deployment.md).

## Steps

From an **elevated** PowerShell on the VPS:

```powershell
.\rollback.ps1        # defaults: C:\apps\newsroom, PredelNewsroom
```

The script stops the service, mirrors the newest `C:\apps\newsroom-backup-<timestamp>` back
over `C:\apps\newsroom` (current logs are kept), starts the service and tails the newest log.

## Verify

- Service `Running`, startup log clean.
- `/status` responds in Telegram.

## Notes

- **Migrations are not rolled back.** They are forward-only and written backward-compatible
  (add columns, don't repurpose — docs/09-deployment.md), so N-1 binaries run against the
  newer schema. If a migration itself is the problem, that is an incident, not a rollback:
  fix forward with a new migration.
- To roll back to an older backup than the newest, rename/remove the newer
  `C:\apps\newsroom-backup-*` folders first (the script always picks the newest by name).
- After a rollback, the bad release's backup still counts against the keep-3 limit — clean up
  manually if you need the older restore points preserved.
