# ADR-0004 — In-process hosted-service scheduling (no Quartz/Hangfire in v1)

**Status:** Proposed · **Date:** 2026-07-02

## Context

Six recurring jobs (scrape, analyse, trend, draft, telegram loop, publish) on one machine, with
intervals from minutes to hours. Retries, idempotency and crash recovery are already handled at
the data layer (statuses in the DB).

## Options considered

1. **Plain `BackgroundService` + `PeriodicTimer` per job** — zero dependencies, trivially
   debuggable, intervals from config; no dashboard, no cron expressions, single-node only.
2. **Quartz.NET** — cron precision, persistence, clustering — none currently needed.
3. **Hangfire** — dashboard + retries, but brings its own storage schema and a web dashboard the
   worker (non-IIS) would have to host; overlaps with retry logic we need at the domain level anyway.

## Decision

Option 1 for v1. Because all queueing/retry state lives in the database, the scheduler is just a
heartbeat — the simplest thing that works. Revisit (checkpoint written into the roadmap, end of
Phase 3) if we ever need cron-exact schedules, multi-node, or an ops dashboard.

## Consequences

No new dependencies; job visibility comes from our own heartbeat/watchdog alerts (07-operations).
If requirements grow, swapping the trigger mechanism is cheap because jobs are already isolated
services reading DB state.
