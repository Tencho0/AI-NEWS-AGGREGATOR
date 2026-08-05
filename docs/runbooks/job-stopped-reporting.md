# Runbook — "Задачата X не е отчела активност от N мин"

**Status:** Agreed · **Last updated:** 2026-08-05

The watchdog alert from [07-operations.md](../07-operations.md): a job's heartbeat is older than
3× its own interval (5-minute jobs alert at 15). Alerts repeat at most once per job per hour, so
a climbing minute count in the review chat is one dead job, not many alerts.

## What the alert does and does not mean

The heartbeat is written **after** the cycle returns. So every ordinary early return still beats:
budget exhausted, `/pause`, no API key, nothing to do. A missing beat means the cycle **did not
return** — the job is stuck or gone, not idle.

It also does not mean the worker is down. Each job is an independent `BackgroundService`; the
2026-08-04 outage had Draft and Trend dead for 21 hours and 5 hours while scraping, analysis,
publishing, Telegram and the watchdog itself all ran normally.

## 1. Is it one job or the whole worker?

```powershell
sqlcmd -S .\SQLEXPRESS -E -d Newsroom -b -f 65001 -W -Q "SELECT [Key],[Value],UpdatedAtUtc FROM dbo.nw_Config WHERE [Key] LIKE 'Heartbeat:%' OR [Key]='Worker:LastHeartbeatUtc' ORDER BY [Key];"
```

`Worker:LastHeartbeatUtc` updates every 60 s. Fresh there with stale `Heartbeat:<Job>` rows = the
process is healthy and specific jobs are affected. Everything stale = see
[restore-after-vps-restart.md](restore-after-vps-restart.md) instead.

These timestamps are the ground truth for *when* it stopped. Prefer them to arithmetic on the
alert text — the alert fires on the watchdog's own 5-minute cadence, not at the moment you read it.

## 2. What was the job doing when it stopped?

```powershell
Get-ChildItem C:\apps\newsroom\logs -Filter *.log | Sort-Object LastWriteTime |
    Select-Object Name, LastWriteTime, @{n='KB';e={[int]($_.Length/1KB)}}
```

Then read the window around the frozen timestamp — log stamps are VPS-local (`+03:00`), the
heartbeat values are UTC:

```powershell
Select-String -Path C:\apps\newsroom\logs\newsroom-<yyyyMMdd>.log -Pattern 'DraftJob|TrendJob' -Encoding UTF8 -Context 0,12 |
    Select-Object -Last 5
```

Warning-and-above is also queryable, which survives log rotation:

```sql
SELECT TOP 50 [TimeStamp], [Level], [Message], [Exception]
FROM dbo.nw_Log WHERE [TimeStamp] >= DATEADD(hour, -6, SYSUTCDATETIME()) ORDER BY Id DESC;
```

## 3. Recover

```powershell
Restart-Service PredelNewsroom
```

Queued work is picked up automatically — sources are due by `LastCrawledAtUtc + IntervalMinutes`,
topics stay open, and drafts left `Generating` are swept by the startup recovery.

## 4. Then find out why

A restart is not the fix; a job that stopped reporting is a bug until proven otherwise.

Since 2026-08-05 every periodic job runs through `JobCycle.RunAsync`, which ends the loop **only**
on the host's stopping token — anything else is logged as `<Job> cycle failed; retrying on the
next tick` and the job survives. So the modern failure mode is a *repeating* alert with matching
`cycle failed` errors naming the cause, not silence.

**Silence with no `cycle failed` line means the cycle is genuinely blocked**, which the timeouts
do not cover: check for a stuck outbound call (`Ai:RequestTimeoutSeconds` bounds Gemini; the other
clients carry their own) or SQL blocking (`sys.dm_exec_requests` where `blocking_session_id <> 0`).

Historic cause, fixed and regression-tested in `JobCycleTests`: a bare
`catch (OperationCanceledException)` treated an `HttpClient` timeout — which arrives as
`TaskCanceledException` — as graceful shutdown, so the job returned normally and was never
restarted. If you ever add a cancellation catch, gate it on `stoppingToken.IsCancellationRequested`.
