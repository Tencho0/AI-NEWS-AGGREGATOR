# Runbook — Restore After VPS Restart

**Status:** Agreed · **Last updated:** 2026-07-03

Health checklist after the VPS reboots (planned or not) — promised by
[07-operations.md](../07-operations.md). The service is installed with `start=auto` plus
restart-on-failure recovery, so normally everything below is already green; this runbook is
the verification, and what to do when it is not.

## 1. Service auto-start check

```powershell
Get-Service PredelNewsroom
```

Expected: `Running`. If `Stopped`:

```powershell
Start-Service PredelNewsroom
Get-Service PredelNewsroom
```

If it will not start, check the Windows Event Log (`Get-EventLog -LogName Application -Newest 20`)
and the newest worker log (below). Common causes: SQL Server service not running yet
(`Get-Service 'MSSQL*'`), missing machine environment variables after an OS change.

## 2. Worker health

- **Logs:** newest file under `C:\apps\newsroom\logs\newsroom-<date>.log`. A clean start shows
  the Serilog banner, `Database schema is up to date at version N` and `Heartbeat OK` within
  ~1 minute. Warnings+ also land in the `nw_Log` table:

  ```sql
  SELECT TOP 20 [TimeStamp], [Level], [Message] FROM dbo.nw_Log ORDER BY Id DESC;
  ```

- **`/status` in Telegram:** send `/status` in the review chat — the reply includes today's
  pipeline counts and `Последен пулс` (last heartbeat). A response at all proves the Telegram
  loop and the DB round trip.
- **Watchdog:** if any job stayed dead, the review chat gets
  `⚠️ Задачата <Job> не е отчела активност от N мин` within ~15 minutes of the restart
  (3× the job's interval + the watchdog's 5-minute cadence). No alert = all jobs cycling.

## 3. Pipeline sanity (optional, after longer downtime)

- Sources resume automatically — polls are due when `LastCrawledAtUtc + IntervalMinutes <= now`,
  so a backlog drains on the first cycles.
- Drafts that were mid-generation when the machine died are recovered at startup
  (`Generating` older than 1 h → `GenerationFailed`, logged as
  `Startup recovery: N draft(s) …`); editor-requested regenerations among them are reported
  in the review chat.
- The daily digest catches up by itself: if 09:00 passed while the VPS was down, it sends on
  the first check after startup (persisted `Digest:LastSentDate` prevents doubles).
