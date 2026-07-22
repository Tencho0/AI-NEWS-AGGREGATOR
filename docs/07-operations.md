# 07 — Operations: Logging, Monitoring, Error Handling

**Status:** Draft · **Last updated:** 2026-07-02

## Logging (Serilog)

- **Sinks:** rolling files (`logs/newsroom-.log`, 14 days) for everything ≥ Debug;
  SQL table `nw_Log` for ≥ Warning (queryable history).
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

`/status` returns the same data on demand. Windows Service recovery options: restart on failure
(1 min, 5 min, 15 min). If richer monitoring is ever needed (uptime pings, dashboards), that's a
new ADR — deliberately out of v1.

## Error-handling policy (uniform across jobs)

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

## Runbooks (grow in `docs/runbooks/` as incidents happen)

Planned from day 1:
- `facebook-token-renewal.md` — re-auth steps when the Page token dies.
- `add-a-source.md` — source onboarding checklist (feed check, ToS check, parser hint, test poll).
- `restore-after-vps-restart.md` — service auto-start verification, health checklist.
- `cost-cap-hit.md` — how to raise/inspect the cap, find the expensive stage.
