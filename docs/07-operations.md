# 07 — Operations: Logging, Monitoring, Error Handling

**Status:** Draft · **Last updated:** 2026-07-02

## Logging (Serilog)

- **Sinks:** rolling files (`logs/newsroom-.log`, 14 days) for everything ≥ Debug;
  SQL table `nw_Log` for ≥ Warning (queryable history). The rolling suffix is `yyyyMMdd`, so
  today's file is `newsroom-20260805.log` — not `newsroom-2026-08-05.log`.
- **Gemini call timeout:** `Ai:RequestTimeoutSeconds` (default 100). The Google SDK builds those
  clients itself, so unlike every other outbound client they get no `AddHttpClient` timeout and no
  Polly handler; before 2026-08-05 the value was inherited silently from `HttpClient`'s default.
- **Structured properties everywhere:** `Job`, `SourceId`, `TopicId`, `DraftId`, `Destination`,
  `CorrelationId` (one id per pipeline item flowing through all stages).
- **Never logged:** secrets, full article bodies (log ids + lengths), Telegram tokens in URLs.
- AI calls log: model, prompt version, token counts, cost, duration (mirrors `nw_CostLedger`).

## Monitoring & alerting

The **Telegram admin thread is the ops console** for v1 — no extra infra:

| Signal | Alert |
|---|---|
| Job hasn't completed a cycle within 3× its interval (watchdog `nw_AuditEvent` heartbeats) | ⚠️ immediately |
| Source failing > 3 consecutive polls / auto-disabled | ⚠️ |
| GenerationFailed / PublishFailed / PartiallyPublished | 🔴 with retry button |
| Daily AI cost > 80 % of cap / cap reached | ⚠️ / 🔴 |
| FB token invalid (daily health check) | 🔴 with re-auth runbook link |
| Daily digest (09:00): articles scraped, topics, drafts, approvals, publishes, cost | ℹ️ |

The digest covers the **last complete UTC day** — the one named in its header. The send time is
VPS-local, so reporting "today" would have counted only the hours since UTC midnight (six, at
UTC+3) under a header claiming the whole day. Hot topics and enabled/disabled source counts are
deliberately current snapshots, not day figures.

`/status` returns the same data on demand. Windows Service recovery options: restart on failure
(1 min, 5 min, 15 min). If richer monitoring is ever needed (uptime pings, dashboards), that's a
new ADR — deliberately out of v1.

## Error-handling policy (uniform across jobs)

0. **A job may never retire itself.** Every periodic job runs through `JobCycle.RunAsync`, whose
   one rule is that *only the host's stopping token ends the loop*. Any other failure costs the
   current cycle, is logged, and the job takes the next tick.

   This is rule zero because breaking it caused the 2026-08-04 outage. Each job used to end in a
   bare `catch (OperationCanceledException)` commented "graceful shutdown" — but an `HttpClient`
   timeout throws `TaskCanceledException`, which *is* an `OperationCanceledException`. One stalled
   Gemini call therefore read as shutdown, `ExecuteAsync` returned normally, and since a completed
   `BackgroundService` neither faults the host nor logs anything, DraftJob and TrendJob were
   silently retired for the life of the process while every other job kept running. Draft was dead
   21 hours before anyone noticed. When catching cancellation, always ask whether the *stopping
   token* is cancelled — never infer shutdown from the exception type.

1. **Item-level isolation:** one bad article/draft never stops a batch — catch per item, mark the
   item failed, continue.
2. **Retry taxonomy:**
   - *Transient* (HTTP 5xx/429/timeouts): Polly retry ×3 exponential+jitter, then circuit breaker
     per host; item stays queued for next cycle.
   - *Gemini daily-quota 429:* Cluster/Draft/SelfCheck switch to the Analyse stage's model until
     the quota reset (midnight US-Pacific), then switch back — automatic, in-memory, Gemini-only
     (docs/05-integrations/ai-generation.md § Daily-quota fallback; mitigates risk R-11).
   - *Permanent* (4xx validation, schema failures): mark failed immediately + alert; no retry
     without human action.
   - *Poison items:* 3 failed cycles → status `*Failed`, excluded from queues, alerted.
3. **Idempotency everywhere:** re-running any stage on the same item is safe by design
   (status checks + unique keys + external idempotency refs).
4. **Crash recovery:** all state in DB; on service start, `Publishing`/`Generating` items older
   than a threshold are reset to their previous status for reprocessing.
5. **Human escalation is a feature:** every failure that stops an item is visible in Telegram
   with the minimal action needed (retry button / instruction).

## Persistent storage (ADR-0013)

**The worker's install directory is disposable. Image files are not.**

Generated covers, editor uploads and the approved public-figure reference portraits all live under
`Images:StorageRoot`, deliberately outside the deployment directory so a redeploy, a service
reinstall or a `bin` wipe cannot destroy a pending draft's cover.

- **Production must point `Images:StorageRoot` at a persistent mounted volume**, configured *before*
  drafts are generated. Left unset it defaults to `%ProgramData%\PredelNewsroom\images` on whichever
  host runs the service — fine for a single-box dev install, not for anything that gets
  redeployed or moved.
- `nw_DraftImage.Url` stores a relative key into that root, so the root can move without rewriting
  rows. Rows written before ADR-0013 hold absolute paths under the old install directory; they still
  resolve, but only while that directory exists. Migrating the root means copying the three area
  folders across, not editing the database.
- The volume needs room for roughly 14–30 days of covers at ~200 KB each (the daily retention pass
  prunes on that schedule — see docs/05-integrations/images.md). `public-figures/` and the logo asset
  are never pruned automatically.
- Back up `public-figures/` and `branding/`: they are hand-curated inputs that cannot be
  regenerated. Adding to them is [runbooks/add-a-public-figure.md](runbooks/add-a-public-figure.md).

## Runbooks (grow in `docs/runbooks/` as incidents happen)

Planned from day 1:
- `facebook-token-renewal.md` — re-auth steps when the Page token dies.
- `add-a-source.md` — source onboarding checklist (feed check, ToS check, parser hint, test poll).
- `add-a-public-figure.md` — reference portrait + allow-list entry before a real face can be drawn.
- `restore-after-vps-restart.md` — service auto-start verification, health checklist.
- `cost-cap-hit.md` — how to raise/inspect the cap, find the expensive stage.
