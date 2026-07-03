# Runbook — AI Cost / Request Cap Hit

**Status:** Agreed · **Last updated:** 2026-07-03

The binding constraint is the free-tier **request quota**, not dollars (ADR-0010), so budgets
are daily request caps per AI stage. When a stage's cap is exhausted its job logs
`Daily AI request budget for stage <Stage> exhausted; skipping cycle` and skips work until the
UTC day rolls over — nothing is lost, items stay queued.

## Where the budgets live

Configuration keys `Ai:Stages:<Stage>:DailyRequestBudget` (defaults in
[appsettings.json](../../src/Newsroom.Worker/appsettings.json)):

| Stage | Default | Consumed by |
|---|---|---|
| `Analyse` | 1000 | AnalyseJob — one request per batch of articles |
| `Cluster` | 300 | TrendJob — one request per clustering batch |
| `Draft` | 100 | DraftJob — one request per draft/regeneration |
| `SelfCheck` | 100 | DraftJob — one request per draft self-check |

Enforcement counts **today's (UTC) rows in `nw_CostLedger` per stage** — no separate counter
to reset.

## 1. Find what ate the budget

```sql
-- Today's usage per stage
SELECT Stage, SUM(RequestCount) AS Requests, SUM(TokensIn) AS TokensIn,
       SUM(TokensOut) AS TokensOut, SUM(Cost) AS Cost
FROM dbo.nw_CostLedger
WHERE AtUtc >= CAST(SYSUTCDATETIME() AS date)
GROUP BY Stage ORDER BY Requests DESC;

-- Hourly shape (did something loop?)
SELECT Stage, DATEPART(HOUR, AtUtc) AS Hr, COUNT(*) AS Requests
FROM dbo.nw_CostLedger
WHERE AtUtc >= CAST(SYSUTCDATETIME() AS date)
GROUP BY Stage, DATEPART(HOUR, AtUtc) ORDER BY Stage, Hr;
```

A single stage burning its cap early usually means a retry loop (check `nw_Log` around the
spike) or a scrape flood (a new source dumping its archive). Fix the cause before raising caps.

## 2. Raise (or lower) a cap

Production override — machine environment variable with `__` separators, then restart:

```powershell
[Environment]::SetEnvironmentVariable('Ai__Stages__Draft__DailyRequestBudget', '200', 'Machine')
Restart-Service PredelNewsroom
```

(Or edit `C:\apps\newsroom\appsettings.Production.json` and restart — whichever mechanism this
install uses; see [deploy.md](deploy.md).) Keep the caps under the provider's real free-tier
limits — the per-minute limiter (`Ai:RequestsPerMinute`) is separate and shared by all stages.

## 3. If you need headroom today

The budget check reads configuration live but counts today's ledger rows — raising the cap
takes effect on the next cycle after restart, no ledger surgery needed. **Do not delete
`nw_CostLedger` rows** to free budget: the ledger is the audit trail for spend and quota
(docs/07-operations.md). If the provider quota itself is exhausted (HTTP 429 /
RESOURCE_EXHAUSTED in the logs), no local setting helps — wait for the provider's daily reset
or move the affected stage to a paid tier/model (`Ai:Stages:<Stage>:Model`).
