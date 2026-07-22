# Design — Gemini daily-quota fallback: Cluster/Draft/SelfCheck borrow the Analyse model

**Date:** 2026-07-21 · **Status:** Approved (pending spec review) · **Related:**
docs/adr/0010-provider-agnostic-ai-gemini-default.md, docs/05-integrations/ai-generation.md,
docs/07-operations.md (retry taxonomy, risk R-11)

## Problem

Each pipeline stage is pinned to one Gemini model at startup (`Ai:Stages:{stage}:Model`,
`Program.cs:78-99`). When the **real** Gemini per-model daily quota is exhausted (HTTP 429,
`RESOURCE_EXHAUSTED`, per-day quota id), the stage's requests keep failing until the quota resets
at midnight Pacific. Jobs classify this as transient (`AiTransientErrors.IsQuotaExhausted`) and
retry every cycle — each retry burns nothing but achieves nothing, and Cluster/Draft/SelfCheck
stall for up to a day.

The real 429 can arrive **before** the worker's own `DailyRequestBudget` is spent, because the
production ledger does not see all consumers of the API key (e.g. a local dev worker run against
the same key writes to a different database's ledger).

Meanwhile the Analyse stage's model (`gemini-3.1-flash-lite`, 500 RPD) usually has plenty of
quota left — a degraded-but-alive fallback for the stalled stages.

Owner decisions (2026-07-21):

- Trigger is the **real API daily-quota 429 only** — local `DailyRequestBudget` exhaustion keeps
  today's skip-the-cycle behaviour and does *not* trigger the fallback.
- SelfCheck falls back too (it shares Draft's model; a fallback-generated draft must not stall
  on an exhausted self-check model).
- Fallback logic lives in a delegating `IChatClient` wrapper — jobs and AI adapters unchanged.
- **Gemini-only**: if a stage's provider is ever switched to another provider (ADR-0010 keeps
  that door open), the fallback machinery must not touch it.

## Goal

When Cluster, Draft, or SelfCheck receives a *daily*-quota 429 from Gemini, the stage
automatically and temporarily switches to the Analyse stage's model, and switches back after the
quota reset (midnight Pacific). No manual intervention, no config change, no stalled pipeline.

## Non-goals (YAGNI)

- No fallback triggered by the worker's own `DailyRequestBudget` (owner decision above).
- No fallback for the Analyse stage itself (it *is* the fallback model; if flash-lite is
  exhausted, today's behaviour — skip and retry next cycle — stands).
- No cross-provider fallback (Gemini → Claude/OpenAI) and no fallback at all for non-Gemini
  stages.
- No fallback-model quality gating (a lite-model draft still passes the normal DraftValidator /
  self-check / editor review gates).
- No new config keys — the fallback model is whatever `Ai:Stages:Analyse:Model` says.
- No persistence of fallback state across worker restarts (a restart re-probes the primary
  model; worst case one extra 429 re-activates the fallback).

## Design

### 1. Daily-quota classifier (`AiTransientErrors`)

New `IsDailyQuotaExhausted(Exception)`: `IsQuotaExhausted(ex)` **and** the message names a
per-day quota. Google's 429 payload carries quota ids like
`GenerateRequestsPerDayPerProjectPerModel-FreeTier`, so the discriminator is an
ordinal-case-insensitive `"PerDay"` match (plus `"per day"` for prose wordings).

Per-minute / per-token 429s deliberately do **not** match — they stay transient
(retry next cycle, no fallback), because flipping a stage to the lite model for a whole day over
an RPM blip would be wrong. The existing `IsQuotaExhausted` / `IsTransient` semantics are
untouched, so job-level catch blocks behave exactly as today.

### 2. Fallback state — `GeminiModelFallback` (singleton)

Thread-safe registry keyed by **model id** (not stage):

- `bool IsActive(string model)` — true while an activation has not expired.
- `void Activate(string model)` — records the model as exhausted until the next quota reset.

Keying by model makes Draft and SelfCheck (both `gemini-3.5-flash`) share fate automatically:
one 429 flips both, and Cluster (`gemini-2.5-flash`) is tracked independently.

**Expiry:** next midnight **Pacific** — Gemini's actual free-tier reset. Computed via
`TimeZoneInfo` (`"Pacific Standard Time"`, falling back to `"America/Los_Angeles"`, falling back
to a fixed UTC-8 offset). Takes a `TimeProvider` (ctor-injected, `TimeProvider.System` default)
so tests can fake the clock. After expiry the next request probes the primary model again; if
the quota is somehow still exhausted the 429 simply re-activates the fallback (costs one
request).

Logs: warning on activation (`model`, `until`), information on expiry (first request after the
window restores the primary).

### 3. `GeminiQuotaFallbackChatClient : IChatClient` (delegating wrapper)

Wraps a stage's primary client. Holds: primary `IChatClient` + model id, fallback `IChatClient`
+ model id, the `GeminiModelFallback` singleton, an `ILogger`.

`GetResponseAsync`:

1. If `IsActive(primaryModel)` → route the request to the fallback client.
2. Otherwise call the primary; on `IsDailyQuotaExhausted` → `Activate(primaryModel)`, log, and
   **retry the same request once** on the fallback client, so the triggering cycle succeeds
   instead of being wasted.
3. Sets `response.ModelId ??= <model actually used>` so `nw_CostLedger` (which records
   `usage.Model` from `response.ModelId`) shows fallback usage truthfully.
4. If the **fallback** call itself throws (including quota), the exception propagates unchanged —
   jobs already treat it as transient and the stage skips cycles exactly as today.

`GetStreamingResponseAsync` routes by `IsActive` only (no catch-retry) — nothing in the codebase
streams today. `GetService`/`Dispose` delegate to the primary (and dispose both inner clients).

### 4. Wiring (`Program.cs` + `GeminiChatClientFactory`)

`GeminiChatClientFactory` gains `CreateWithDailyQuotaFallback(configuration, stage, fallback,
loggerFactory)` used for the **Cluster**, **Draft**, and **SelfCheck** registrations. It wraps
only when *all* hold:

- the stage's provider resolves to Gemini — `Ai:Stages:{stage}:Provider`, default `"gemini"`
  (the key does not exist in config yet; per ADR-0010 absent = Gemini). Non-Gemini stage → the
  plain unwrapped client, fallback machinery structurally unreachable;
- the Analyse stage's provider is also Gemini (the fallback target must be a Gemini model — no
  cross-provider swaps);
- the stage's model differs from the Analyse model (wrapping a model with itself is pointless).

The Analyse registration stays unwrapped. The `Lazy<>` degradation pattern for a missing API key
is unchanged.

### 5. Budgets, rate limiting — unchanged

- Jobs keep reserving against their own stage's `DailyRequestBudget`, so fallback traffic is
  capped by the existing per-stage budgets. Worst case on the flash-lite bucket:
  450 (Analyse) + 18 (Cluster) + 9 (Draft) + 9 (SelfCheck) = 486 ≤ 500 RPD — safe headroom.
- The shared `AiRateLimiter` lease is acquired by the caller as today; the one-time
  activate-and-retry makes two API calls under one lease — acceptable at 8 RPM configured vs 10
  allowed.
- `nw_CostLedger` needs no schema change; the `Model` column now truthfully records the
  fallback model for fallback requests (via §3 point 3).

## Error handling summary

| Failure | Behaviour |
| --- | --- |
| Daily-quota 429 on primary (Cluster/Draft/SelfCheck) | Activate fallback for that model, retry once on Analyse model, succeed |
| Per-minute 429 / 503 / overload on primary | Unchanged — transient, retry next cycle, no fallback |
| Daily-quota 429 on the fallback model | Propagates; job treats as transient; stage skips cycles (both models exhausted) |
| Content block / safety refusal | Unchanged — deterministic, burns the attempt (not the wrapper's business) |
| Worker restart while in fallback | State is in-memory; primary re-probed, one extra 429 re-activates |

## Testing

`Newsroom.Infrastructure.Tests/Ai`, following the existing fake-`IChatClient` style:

- **AiTransientErrorsTests** — `IsDailyQuotaExhausted`: matches a real per-day 429 wording;
  rejects per-minute 429, 503, and non-quota errors.
- **GeminiModelFallbackTests** — activation is active immediately; expires at next Pacific
  midnight (fake `TimeProvider`); models are tracked independently.
- **GeminiQuotaFallbackChatClientTests** —
  - normal pass-through to primary (no state change);
  - daily-quota 429 → activates, retries on fallback, returns fallback response with
    `ModelId` set;
  - subsequent requests route straight to fallback while active;
  - per-minute 429 propagates, no activation;
  - two wrappers sharing one model id (Draft+SelfCheck) share fate;
  - expiry restores routing to primary.

## Documentation

- `docs/05-integrations/ai-generation.md` — model table gains a fallback note + a short
  "Daily-quota fallback" subsection (trigger, reset, Gemini-only guard).
- `docs/07-operations.md` — retry taxonomy row for the daily-quota 429 → fallback path (R-11).
