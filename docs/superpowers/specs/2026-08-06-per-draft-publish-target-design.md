# Design — Per-draft publish target (сайт / Facebook / и двете)

**Date:** 2026-08-06 · **Status:** Approved · **Related:**
docs/05-integrations/telegram.md, docs/05-integrations/facebook.md, docs/05-integrations/umbraco.md,
docs/superpowers/specs/2026-08-05-post-command-metadata-picker-design.md,
docs/superpowers/specs/2026-07-17-facebook-engagement-design.md, decision-log 2026-07-08

## Problem

✅ Одобри is the only approval on a review card, and it means one fixed thing: publish to every
destination the **process** is configured for. `PublishJob` computes that once, at construction:

```csharp
private readonly string[] requiredDestinations = publishing.FacebookOnly
    ? [PublishDestinations.Facebook]
    : facebookOptions.IsConfigured
        ? [PublishDestinations.Umbraco, PublishDestinations.Facebook]
        : [PublishDestinations.Umbraco];
```

With `Publishing:FacebookOnly = false` (its value in both `appsettings.json` and
`appsettings.Sandbox.json`) every approved draft goes to the website and then to the Facebook page
as a link post. There is no way to say "this one is a Facebook-only quickie" or "this one is a
website piece I don't want on the page" without flipping a config key and restarting the worker,
which changes the routing for *every* draft, not one.

The editor decides this per article, at review time, in Telegram.

## Goal

Three one-tap approvals on the review card:

| Button | Meaning |
|---|---|
| ✅ Одобри | Website, then the link posted to Facebook — **exactly today's flow, unchanged** |
| 🌐 Само сайт | Website only; nothing is posted to the Facebook page |
| 📘 Само ФБ | Facebook only; the article never reaches the site |

## Non-goals (YAGNI)

- **No scheduled variants of the two new targets.** 📅 Насрочи keeps meaning "site + Facebook, at
  the suggested slot". The slot suggester (`PublishSlotSuggester`, `GetFacebookCommitmentsUtcAsync`)
  exists to space out *Facebook page posts*, which is precisely the Both case; a website-only
  article has nothing to space against. 🌐 and 📘 publish on the next cycle.
- **No changing a target after approval.** Approval strips the card's buttons today; that stays.
  A mistap is recovered the way a wrong ✅ is recovered today — by hand.
- **No removal of `Publishing:FacebookOnly`.** See "Interaction with the global flag" below.
- No new destinations (Instagram, X, groups). The group-share text block stays a copy-paste helper.

## Key finding

Both publish shapes this feature needs **already exist**, fully written and tested — the flag just
picks one of them process-wide:

| Existing query | Shape it produces |
|---|---|
| `IPublishRepository.GetPendingFacebookAsync` | FB post **with** the live site link, gated on the draft's `umbraco` record having succeeded (status `PartiallyPublished`) |
| `IPublishRepository.GetApprovedForFacebookAsync` | Standalone FB post — caption + hashtags + the draft's chosen image, **no link**, no site step |

So the work is not "write a Facebook-only publish path". It is: move the choice between these two
from a process-wide boolean to a per-draft column, and run **both** queries every cycle instead of
one.

## Data model

Migration `0016_publish_target.sql`:

```sql
ALTER TABLE dbo.nw_Draft ADD PublishTarget nvarchar(20) NOT NULL DEFAULT 'Both';
```

Backed by a new enum in `Newsroom.Core.Publishing`:

```csharp
public enum PublishTarget { Both, Website, Facebook }
```

Values are persisted with `nameof`, matching how `DraftStatus` is already stored (`nvarchar`
status strings, not ints — see `ReviewRepository`/`PublishRepository`).

`DEFAULT 'Both'` backfills every existing row — including drafts sitting in `Approved` or
`PartiallyPublished` mid-flight during the deploy — to today's exact behaviour. No data migration,
no window where a queued draft changes meaning.

**Rejected alternative:** pre-seed `nw_PublishRecord` rows marking the skipped destination as
already-handled, so the existing attempt-gating `NOT EXISTS`/`SUM(Attempts)` predicates do the
filtering with no schema change. Rejected because it writes a record of an event that never
happened into an audit table, and because `GetFacebookCommitmentsUtcAsync` counts Succeeded
`facebook` rows to space out future posts — synthetic rows would poison the slot suggester.

## Telegram surface

### Callback data

`approve:{draftId}:{target}` where `{target}` is the literal `site` or `fb`.

Bare `approve:{draftId}` (two segments) keeps meaning **Both**. That matters twice:

1. Cards already posted in the chat when the worker restarts keep working — their ✅ button still
   publishes to both, which is what their editor expects.
2. The scheduled card's „✅ Одобри веднага" button emits `approve:{draftId}` and relies on
   `TryApproveAsync` failing (already Approved) so `TryUnscheduleAsync` runs. Untouched.

`ReviewUpdateRouter.RouteCallback` already parses three-segment data for `setcat`/`setregion`, so
this is the established shape:

```csharp
("approve", 2) => new ApproveDraft(draftId),                              // Both
("approve", 3) when TryParseTarget(segments[2], out var t) => new ApproveDraft(draftId, t),
```

An unrecognised third segment falls through to `Ignore(ReasonUnknownData)` — a crafted or
corrupted callback can never construct a target that isn't one of the three.

`ApproveDraft` gains a target:

```csharp
public sealed record ApproveDraft(long DraftId, PublishTarget Target = PublishTarget.Both) : ReviewCommand;
```

The default keeps every existing construction site and test compiling and meaning what it meant.

### Keyboards

| Card | Change to `TelegramGateway` |
|---|---|
| Normal review card (`SendHtmlAsync`) | new row `[🌐 Само сайт] [📘 Само ФБ]` between the ✅/✏️/❌ row and the 📅 row |
| Manual card, `Resolved` / `Expanded` (`BuildManualKeyboard`) | same new row, in the same position |
| Manual card, `AwaitingCategory` | `[📘 Само ФБ]` only, on its own row below ✏️/❌ |

The `AwaitingCategory` case is the one behavioural asymmetry, and it is deliberate: that keyboard
withholds ✅ because **Umbraco** rejects a publish with no category
(`NewsroomPublishingService.ResolveTaxonomyNode` → HTTP 400 → `PublishRejectedException`, which
burns the whole attempt budget at once). A Facebook-only post never calls Umbraco, so the reason
for the gate does not apply and the editor can fire a quick page post without picking taxonomy.
🌐 and ✅ stay hidden until a category is set.

### Approval guard

`TelegramJob`'s existing defence-in-depth check —

```csharp
if (approveView is { IsManual: true } && string.IsNullOrWhiteSpace(approveView.Category))
    → toast "Първо избери категория"
```

— narrows to targets that include the website (`Both`, `Website`). A `Facebook` approval passes it.
The check stays defence-in-depth: `callback_data` is not bound to what is currently rendered, so a
replayed press must not approve a draft the website will refuse.

### Confirmation

The resolved card's status line names the target, so the audit is visible in the chat itself:

| Target | Status line |
|---|---|
| Both | `✅ Одобрено от {editor}` (unchanged) |
| Website | `✅ Одобрено (само сайт) от {editor}` |
| Facebook | `✅ Одобрено (само ФБ) от {editor}` |

The toast follows the same wording.

## Recording the choice

`IReviewRepository.TryApproveAsync` takes the target and sets `Status` **and** `PublishTarget` in
its single guarded UPDATE — one statement, so a draft can never end up Approved with a stale
target. The `nw_ReviewAction` row carries the target in its existing `Comment` column, the same way
`Scheduled` carries its slot.

`TryScheduleAsync` writes `PublishTarget = 'Both'` explicitly rather than leaning on the column
default — 📅 is defined as the both path, and stating it keeps the schedule path readable next to
the approve path.

`TryUnscheduleAsync` does not touch `PublishTarget`. A scheduled draft is Both by construction, and
„✅ Одобри веднага" only changes *when*, not *where*.

## Publishing

`requiredDestinations` stops being a constructor-time field on `PublishJob` and becomes a function
of the draft being published:

| `PublishTarget` | Required destinations | Status on full success |
|---|---|---|
| `Both` | `umbraco` + `facebook` — or `umbraco` alone when Facebook is unconfigured (today's rule, preserved) | `Published` |
| `Website` | `umbraco` | `Published` |
| `Facebook` | `facebook` | `Published` |

A website-only draft therefore lands on `Published`, not `PartiallyPublished`: Facebook is not in
its required set, so `RecordSuccessAsync`'s "every required destination has a Succeeded record"
test is satisfied by the site publish alone.

Three query filters carry the whole routing. The accepted targets are a **parameter** of each
query (`AND d.PublishTarget IN @targets`), not a hard-coded literal — that is what lets the global
flag override the column without a second code path:

| Query | Accepted targets (normal) | Accepted targets (`FacebookOnly`) |
|---|---|---|
| `GetApprovedUnpublishedAsync('umbraco', …)` | `Both`, `Website` | *(leg skipped entirely)* |
| `GetPendingFacebookAsync` | `Both` | *(not run — nothing reaches `PartiallyPublished`)* |
| `GetApprovedForFacebookAsync` | `Facebook` | `Both`, `Website`, `Facebook` |

`RunFacebookLegAsync` stops choosing between the last two by flag and runs both each cycle,
concatenating the results. The per-post error isolation, attempt weighting and exhaustion alerting
are unchanged and apply uniformly.

Failure semantics need no special-casing. `RecordFailureAsync` already flips only *Approved* drafts
to `PublishFailed` on exhaustion and leaves `PartiallyPublished` alone (the site is live; only the
FB leg is spent). A Facebook-only draft is Approved when its FB attempts run out, so it correctly
becomes `PublishFailed`; a Both draft that got its site publish is `PartiallyPublished` and
correctly stays there.

### Interaction with the global flag

`Publishing:FacebookOnly` survives as an ops kill-switch, and it **overrides the column**: while it
is `true` the website leg is skipped entirely and the standalone-FB query accepts every target, so
a draft approved as `Both` (or scheduled with 📅, which always writes `Both`) still reaches the
page as a standalone post instead of stalling forever waiting for a site publish that will never
run. Required destinations collapse to `[facebook]` for every draft.

Each target button is offered only when the worker can actually honour it, so a card can never
promise a destination that publishes nowhere. 🌐 Само сайт is dropped while the flag is on. 📘
Само ФБ is dropped when Facebook is unconfigured — `PublishJob.RunCycleAsync` then skips the
Facebook leg entirely while the Umbraco leg's target filter excludes Facebook-only drafts, so
such a draft would sit `Approved` forever, selected by no leg and never alerted on. (That second
gate was missed in this design's first draft and added during implementation, after Task 5's
review traced the dead end.) With neither destination available the row is omitted rather than
sent empty. ✅ and 📅 always remain and mean "publish everywhere possible", which under the flag
is Facebook: the same thing they mean today.

The flag is `false` in both shipped appsettings files, so this is a dormant path — but it is the
documented lever for "the site is down / being polished, keep posting to the page" (decision-log
2026-07-08), and per-draft targets do not replace it.

Umbraco being unconfigured keeps its current behaviour: `PublishJob` logs one warning and stays
dormant, since without the site there is no pipeline to run.

## Notifications

| Target | What the editor gets |
|---|---|
| Both | Unchanged: 🚀 site confirmation with the live link + 📋 group-share block, then 📘 the Facebook permalink |
| Website | The same 🚀 confirmation **including** the 📋 "Текст за групите" block — the bot skipped the page, but posting to the ~28 regional groups by hand is still wanted, and that block is the only place the ready-to-paste text is produced |
| Facebook | The 📘 confirmation with the permalink (or the raw post id when the permalink fetch failed), exactly as Facebook-only mode produces today |

`OfferManualRepairCardAsync` (the metadata-repair card posted when a manual draft's site publish
exhausts its attempts) is unaffected — it only fires on the Umbraco leg, which a Facebook-only
draft never enters.

## Testing

**Constraint discovered while planning:** this repo has **no database integration-test harness.**
`PublishRepositoryTests` and `ReviewRepositoryTests` are pure unit tests over static helpers
(`FileNameFromUrl`, `FormatQuotaSummary`); the "Integration (DB) / local SQL Express" layer in
docs/08-testing.md is aspirational, not built. Adding one is out of scope for this feature.

The response is to make the routing rules pure rather than to test SQL: every decision — which
targets each leg accepts, what `RequiredDestinations` returns for a given target — lives in one
static class, `PublishTargets`, which `PublishJob` and `PublishRepository` both read instead of
re-deriving. That class is densely unit-tested; the SQL that consumes its output is verified by
the sandbox run.

| Area | Cases |
|---|---|
| `PublishTargetsTests` (new) | callback-token parsing (`site`/`fb`, and that nothing else parses); `Parse` of the persisted column, falling back to `Both` on NULL/garbage; the accepted-target list of each of the three legs; the `FacebookOnly` widening of the standalone leg; `RequiredDestinations` for all three targets × FB configured/not × flag on/off |
| `ReviewUpdateRouterTests` | `approve:{id}` → `ApproveDraft(id, Both)`; `:site` → `Website`; `:fb` → `Facebook`; `:xyz`, `:` and a 4-segment form → `Ignore(ReasonUnknownData)` |
| Sandbox end-to-end (manual, ADR-0014) | migration 0016 applies; the card shows the new row; 🌐 publishes to the local site only and reaches `Published`; 📘 dry-run-posts to Facebook only with no site publish; ✅ behaves exactly as before; 📘 works on an `AwaitingCategory` `/post` card |

## Files touched

- `src/Newsroom.Infrastructure/Database/Migrations/0016_publish_target.sql` (new)
- `src/Newsroom.Core/Publishing/PublishTarget.cs` (new)
- `src/Newsroom.Core/Review/ReviewCommand.cs` — `ApproveDraft.Target`
- `src/Newsroom.Core/Review/ReviewUpdateRouter.cs` — three-segment `approve`
- `src/Newsroom.Core/Review/Interfaces.cs` — `TryApproveAsync` signature
- `src/Newsroom.Core/Publishing/Interfaces.cs` — the three query signatures take accepted targets
- `src/Newsroom.Infrastructure/Review/TelegramGateway.cs` — the two new buttons, both keyboards
- `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs` — persist target + audit comment
- `src/Newsroom.Infrastructure/Repositories/PublishRepository.cs` — three query filters
- `src/Newsroom.Worker/Jobs/PublishJob.cs` — per-draft required destinations, both FB queries
- `src/Newsroom.Worker/Jobs/TelegramJob.cs` — target-aware approve handling, category guard, status lines
- `docs/05-integrations/telegram.md` — card actions table + review message format
- Tests as listed above
