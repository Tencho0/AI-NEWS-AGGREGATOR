# Design — Telegram editor commands + force-draft

**Date:** 2026-07-13 · **Status:** Approved (pending spec review) · **Related:** docs/05-integrations/telegram.md, docs/02-functional-spec.md §4–5

## Goal

Add five editor commands to the Telegram review bot and a way to push a non-Hot topic into
drafting. All commands stay inside the existing authorization model (allowlist of editor user
ids + the single review chat) and reply in Bulgarian, matching the current commands.

| Command | Purpose |
|---|---|
| `/help` | List all commands and card actions. |
| `/quota` | AI requests used vs daily cap today, per stage. |
| `/health` | Last heartbeat per background job, with a staleness marker. |
| `/unmute <topicId>` | Lift a topic mute early (reverse of `/mute`). |
| `/draft <topicId>` | Force-draft a topic even if it is not Hot. |

## Non-goals (YAGNI)

- `/draft <url>` (draft from an arbitrary URL) stays a Phase 4b item — out of scope here.
- No `/start`; unknown text stays silently ignored.
- Force-draft does **not** change a topic's `Status` — it is an orthogonal marker (see below).

## Architecture

The existing flow is unchanged in shape: `ReviewUpdateRouter.RouteText` maps text to a
`ReviewCommand`; `TelegramJob` executes it, delegating data/formatting to repository
`Build…Async` methods and toggle methods. Every command below follows that seam.

### New `ReviewCommand` records (`src/Newsroom.Core/Review/ReviewCommand.cs`)

- `ShowHelp`
- `ShowQuota`
- `ShowHealth`
- `UnmuteTopic(int TopicId)`
- `ForceDraftTopic(long TopicId)`

### Routing (`src/Newsroom.Core/Review/ReviewUpdateRouter.cs`)

Extend the `RouteText` command switch:

- `/help` → `ShowHelp`
- `/quota` → `ShowQuota`
- `/health` → `ShowHealth`
- `/unmute` → parse a single positive int topic id (mirror the existing `RouteMute` parser);
  bad/missing arg → `Ignore(ReasonBadArguments)`.
- `/draft` → parse a single positive int topic id → `ForceDraftTopic`. A non-integer arg (a URL)
  → `Ignore(ReasonBadArguments)`; the `TelegramJob` bad-args reply carries the usage hint, and
  the URL form remains explicitly unimplemented.

The `@BotName` suffix stripping and allowlist/chat gating already applied by `RouteText` cover
the new commands unchanged.

## Force-draft (separate-flag approach)

### Data model

Migration `0011_topic_force_draft.sql`:

```sql
ALTER TABLE dbo.nw_Topic ADD ForceDraftAtUtc DATETIME2 NULL;
```

`ForceDraftAtUtc` is a nullable request marker: set = "editor asked to draft this now",
null = no outstanding request.

### Command path (`IDraftRepository.RequestForcedDraftAsync`)

Force-draft touches the draft-activity gate (the "is there already an active draft?" check that
lives in `DraftRepository` via `InactiveDraftStatuses`), so the write method belongs on
`IDraftRepository`, not `IReviewRepository`.

```csharp
Task<ForceDraftResult> RequestForcedDraftAsync(int topicId, CancellationToken ct);

enum ForceDraftResult { TopicNotFound, TopicDone, AlreadyActive, Queued }
```

Behaviour, evaluated in one transaction:

1. Topic id unknown → `TopicNotFound`.
2. Topic `Status = Done` → `TopicDone` (**confirmed decision:** v1 refuses forcing Done topics).
3. An active draft already exists (a `nw_Draft` row whose `Status NOT IN InactiveDraftStatuses`)
   → `AlreadyActive`.
4. Otherwise → set `ForceDraftAtUtc = SYSUTCDATETIME()` **and reset `DraftAttempts = 0`**
   (**confirmed decision:** forcing lets an exhausted topic be retried) → `Queued`.

`TelegramJob` maps the result to a Bulgarian reply:

| Result | Reply |
|---|---|
| `TopicNotFound` | `Няма такава тема (#42).` |
| `TopicDone` | `Тема #42 е приключена.` |
| `AlreadyActive` | `Тема #42 вече има активна чернова.` |
| `Queued` | `⚡ Тема #42 е пусната за чернова.` |

### Pickup (`DraftRepository` + `DraftJob`)

Rename `GetHotTopicsNeedingDraftAsync` → `GetTopicsNeedingDraftAsync` (the name no longer lies:
it returns Hot **and** forced topics). New WHERE:

```sql
WHERE (
        (t.Status = @hotStatus
         AND (t.MutedUntilUtc IS NULL OR t.MutedUntilUtc <= SYSUTCDATETIME()))
        OR t.ForceDraftAtUtc IS NOT NULL
      )
  AND t.DraftAttempts < @maxAttempts
  AND NOT EXISTS (
      SELECT 1 FROM dbo.nw_Draft d
      WHERE d.TopicId = t.Id AND d.Status NOT IN @inactiveStatuses)
ORDER BY CASE WHEN t.ForceDraftAtUtc IS NOT NULL THEN 0 ELSE 1 END, t.Score DESC, t.Id
```

- Forced topics **bypass** the Hot-status and mute gates, but still respect `DraftAttempts` and
  the no-active-draft gate (both are the "don't double-draft / give up eventually" safety net;
  the reset in step 4 gives forcing a fresh budget).
- Forced topics are ordered ahead of Hot ones so an explicit request jumps the queue.
- The `maxPerCycle` limit in `DraftJob.DraftHotTopicsAsync` now naturally covers both kinds; the
  only `DraftJob` change is the renamed call site.

### Clearing the flag

`SaveDraftAsync` nulls `ForceDraftAtUtc` for the drafted topic inside its existing transaction.
Rationale: a saved draft fulfils the request. Without clearing, once that draft is rejected or
expired (both `InactiveDraftStatuses`) the no-active-draft gate would pass again and the topic
would regenerate forever. Clearing on save makes force-draft a genuine one-shot.

## The four read/toggle commands

### `/quota` — `IReviewRepository.BuildQuotaSummaryAsync`

- Used per stage: `SELECT Stage, SUM(RequestCount) FROM dbo.nw_CostLedger WHERE AtUtc >= @todayUtc
  GROUP BY Stage`.
- Cap per stage: `Ai:Stages:{stage}:DailyRequestBudget` (default 1000), same key `AiBudget` reads.
- Stage list: enumerate `configuration.GetSection("Ai:Stages").GetChildren()` so stages with zero
  usage today still appear. Requires injecting `IConfiguration` into `ReviewRepository`
  (the repo already queries `nw_CostLedger` for `/status`, so this is consistent).
- Format (plain text, caller escapes): a title line plus one line per stage `Stage used/cap`,
  with a `⚠️` when `used >= cap`.

### `/health` — `IReviewRepository.BuildHealthSummaryAsync`

- Read `Heartbeat:{job}` from `nw_Config` for each `JobNames` constant
  (Scrape, Analyse, Trend, Draft, Telegram, Publish).
- One line per job: last beat timestamp + age; `⚠️ закъснява` when the last beat is older than a
  staleness threshold — config `Ops:Health:StaleMinutes`, **default 15** (comfortably above the
  largest job interval; Draft polls every 300 s). If planning finds the watchdog already defines
  a threshold, reuse that key instead of adding a new one.
- `няма` for a job that has never beaten.

### `/unmute` — `IReviewRepository.UnmuteTopicAsync`

```csharp
Task<bool> UnmuteTopicAsync(int topicId, CancellationToken ct);
```

`UPDATE dbo.nw_Topic SET MutedUntilUtc = NULL WHERE Id = @topicId`; returns rows-affected > 0.
Replies `🔊 Тема #42 вече не е заглушена.` or `Няма такава тема (#42).` (mirrors `MuteTopicAsync`).

### `/help` — static Bulgarian string

A single source of truth (const in `TelegramJob` or a tiny renderer) listing all slash commands
and the card actions. `TelegramJob` sends it verbatim.

### `/topics` hint

Append one line to `BuildTopicsSummaryAsync` output: `За чернова: /draft <номер>` — makes the
see-topics → force-draft path discoverable, since topic ids are already shown.

## Interfaces summary

**`IReviewRepository` (add):** `BuildQuotaSummaryAsync`, `BuildHealthSummaryAsync`,
`UnmuteTopicAsync`. Inject `IConfiguration` into `ReviewRepository`.

**`IDraftRepository` (add / change):** `RequestForcedDraftAsync`; rename
`GetHotTopicsNeedingDraftAsync` → `GetTopicsNeedingDraftAsync` (+ forced WHERE/ORDER);
clear `ForceDraftAtUtc` in `SaveDraftAsync`.

**`TelegramJob`:** inject `IDraftRepository`; add five switch cases; `/help` text; `/topics` hint.

## Testing (TDD)

1. **Router (pure, unit) — write first.** Extend `ReviewUpdateRouterTests`:
   - `/help`, `/quota`, `/health` → their commands.
   - `/unmute 42` → `UnmuteTopic(42)`; `/unmute`, `/unmute abc`, `/unmute 0` → `Ignore(BadArguments)`.
   - `/draft 42` → `ForceDraftTopic(42)`; `/draft`, `/draft https://…`, `/draft 0` → `Ignore(BadArguments)`.
   - `@BotName` suffix variants still route.
2. **Repository (integration)** mirroring existing mute/status tests:
   - `UnmuteTopicAsync`: unmutes a muted topic; false for unknown id.
   - `RequestForcedDraftAsync`: each of the four results; verifies `ForceDraftAtUtc`/`DraftAttempts`.
   - `GetTopicsNeedingDraftAsync`: returns a forced non-Hot topic; excludes one with an active
     draft; forced ordered ahead of Hot; still excluded once `DraftAttempts >= maxAttempts`.
   - `SaveDraftAsync` clears `ForceDraftAtUtc`.
   - `BuildQuotaSummaryAsync` / `BuildHealthSummaryAsync`: shape of the summary from seeded data.

## Docs

Update the command tables in `docs/05-integrations/telegram.md`: add the five commands, note
`/unmute` and `/draft <topicId>` as implemented, keep `/draft <url>` marked Phase 4b.

## Files touched

- `src/Newsroom.Core/Review/ReviewCommand.cs`
- `src/Newsroom.Core/Review/ReviewUpdateRouter.cs`
- `src/Newsroom.Core/Review/Interfaces.cs` (both interfaces)
- `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs`
- `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs`
- `src/Newsroom.Infrastructure/Database/Migrations/0011_topic_force_draft.sql`
- `src/Newsroom.Worker/Jobs/TelegramJob.cs`
- `src/Newsroom.Worker/Jobs/DraftJob.cs`
- `src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs` (+ repository test project)
- `docs/05-integrations/telegram.md`
