# Design — Facebook engagement: social captions + suggested-time scheduling

**Date:** 2026-07-17 · **Status:** Approved (pending spec review) · **Related:**
docs/05-integrations/facebook.md, docs/editorial-style-guide.md, docs/decision-log.md (2026-07-08
FacebookOnly), docs/10-roadmap.md

## Problem

Published posts get essentially no reactions or reach. Structural causes in the current
Facebook-only flow:

1. Each post is the **entire article as a photo caption** — ALL-CAPS headline + full 250–450-word
   body (`FacebookPublisher.cs:37` + `FacebookTeaser.ComposeFullBody`), no link. Long all-caps
   wall-of-text posts are down-ranked by Facebook.
2. **No engagement hooks** — the prompt mandates neutral newswire tone; `StripMarkdown` deletes
   every `#`, so even AI-written hashtags are destroyed; no CTA, no question.
3. **No timing** — up to 3 posts fire back-to-back every 60 s whenever the editor approves,
   regardless of hour.
4. No measurement (deferred — see backlog).

Owner decisions (2026-07-17): full social-native style approved for FB captions (site article
keeps house style); editor keeps control of timing via a suggested-slot **Schedule** button;
insights polling deferred to backlog.

## Goal

1. **Social-native FB caption**, generated in the *same* Gemini drafting call (no extra quota),
   reviewed by the editor in the Telegram card, posted instead of the full article body.
2. **Suggested best publish time** per draft + a new 📅 **Насрочи** button on the review card.
   The existing ✅ Одобри button keeps publishing immediately, unchanged.

## Non-goals (YAGNI)

- **Insights/metrics polling** — deferred; design sketch recorded below, tracked in
  docs/10-roadmap.md backlog ("smart insights").
- Feeding engagement data into trend scoring — same backlog item.
- Custom time picker in Telegram — the Schedule button uses the computed slot; ✅ now is the
  escape hatch.
- Group auto-posting (Groups API is dead) and the group-paste block — untouched this round.
- No automatic window gating of ✅-approved posts — "publish now" means now, editor's call.
- No change to the editor `/post` verbatim flow.

## Part 1 — Social-native Facebook caption

### AI output contract (`GeminiDraftingAi`)

`DraftDto` (`GeminiDraftingAi.cs:238`) and `DraftContent` gain:

- `facebookCaption` (string) — Bulgarian, target 400–700 chars: **hook first line** in sentence
  case (≤100 chars — the above-the-fold text), 1–2 short paragraphs with the core facts, closing
  **engagement line** (question or light CTA, e.g. „Усетихте ли труса? Разкажете ни в
  коментарите."). Conversational but never tabloid — „никога жълто" still applies. No ALL CAPS,
  no markdown, no hashtags inside the caption.
- `facebookHashtags` (array, 2–3) — region/brand focus (e.g. `#Благоевград`, `#ПределНюз`),
  without or with leading `#` (normalized later).

`BuildGenerateInstruction` (`:99-114`) gets a caption rules block; the editorial style guide
block stays untouched (it governs the article). `PromptVersion` bumps `draft-v1` → `draft-v2`
for traceability; **all runtime fallbacks key off `FacebookCaption IS NULL`, never off the
prompt version.**

### Validation (`DraftValidator`)

- `Normalize`: trim caption; hashtags — trim, ensure single leading `#`, dedupe, cap at 3.
- `Validate` violations: caption missing/out of bounds (lenient 200–900 chars), first line
  > 120 chars, uppercase-letter ratio > 0.6 (catches ALL-CAPS relapse), contains `*`/`#`
  (markdown/hashtags belong elsewhere); hashtag not matching `^#[\p{L}\p{Nd}]+$`.
- Self-check stage unchanged — the caption contains no claims beyond the body it summarizes.

### Storage

Migration `0013_facebook_caption_scheduling.sql`:

```sql
ALTER TABLE dbo.nw_Draft ADD
    FacebookCaption      NVARCHAR(1200) NULL,
    FacebookHashtagsJson NVARCHAR(400)  NULL,
    ScheduledForUtc      DATETIME2      NULL;   -- Part 2
```

`DraftRepository.SaveDraftAsync` / `CompleteRegenerationAsync` persist the two caption columns.
Editor `/post` drafts (`CreateManualArticleAsync`) leave them NULL.

### Review card

`DraftReviewView` gains caption + hashtags; the card builder appends a visible section:

> 📘 **Facebook:** {caption}
> {#хаштаг #хаштаг}

so the editor reviews exactly what will hit the page. ✏️ Промени / regeneration covers the
caption naturally (same draft, same call).

### Publishing

- New pure helper `FacebookCaption.Compose(caption, hashtags)` (Newsroom.Core.Publishing):
  returns `caption + "\n\n" + string.Join(" ", hashtags)` — posted **verbatim**, no
  `StripMarkdown` (validator already guarantees clean text; `#` must survive).
- `GetApprovedForFacebookAsync` (`PublishRepository.cs:166-171`): when `FacebookCaption` is
  present → `Teaser = FacebookCaption.Compose(...)`, `Headline = ""`; when NULL → current
  behavior unchanged (legacy drafts drain, editor drafts stay verbatim).
- `FacebookPublisher.cs:37` composes
  `message = IsNullOrWhiteSpace(Headline) ? Teaser : $"{Headline}\n\n{Teaser}"` — no leading
  blank lines for caption posts.
- Photo attachment logic unchanged (caption becomes the `/photos` caption or `/feed` message).
- Future site mode: `GetPendingFacebookAsync` applies the same "caption when present" rule, so
  switching `Publishing:FacebookOnly` off later yields link posts with the social caption.

## Part 2 — Suggested publish time + Schedule button

### Config (`FacebookScheduleOptions`, section `Facebook:Schedule`)

Record + static `From(IConfiguration)`, registered in `Program.cs` next to `FacebookOptions`
(`:122-124`):

| Key | Default | Meaning |
|---|---|---|
| `Windows` | `["07:30-09:30","12:00-13:30","17:30-21:30"]` | High-engagement slots, **local time** (same convention as `Digest:LocalTime`) |
| `MinGapMinutes` | `90` | Min spacing from any published or scheduled post |
| `MaxPerDay` | `5` | Cap per calendar day (local) |
| `LeadMinutes` | `5` | Suggested slot must be ≥ now + lead |

### Suggestion algorithm (`PublishSlotSuggester`, Newsroom.Core)

Pure, unit-testable function: `Suggest(nowLocal, options, commitments) → DateTime localSlot`.
Commitments = succeeded FB publish times (today and later, from `nw_PublishRecord`) + future
`ScheduledForUtc` of approved drafts. Earliest `t ≥ now + LeadMinutes` such that: `t` inside a
window, `|t − c| ≥ MinGapMinutes` for every commitment `c`, and commitments on `t`'s day
`< MaxPerDay`. Scans forward day by day (bounded 7 days; every empty day has capacity, so it
always terminates). Stored as UTC.

### Telegram UX

- Review card keyboard (`TelegramGateway.cs:79-87`) gets a second row:
  `📅 Насрочи {HH:mm}` → callback `schedule:{draftId}`. `TelegramJob` computes the suggested
  slot at card-render time and passes it to the gateway (label is advisory).
- `ReviewUpdateRouter.RouteCallback` (`:21-42`): new verb `schedule` → `ScheduleDraft(int DraftId)`.
- `TelegramJob.HandleCallbackAsync`: **recomputes** the slot (cards go stale), then
  `reviews.TryScheduleAsync(draftId, slotUtc, userId, userName)` — guarded UPDATE
  `PendingReview → Approved` + `ScheduledForUtc = @slot` + `nw_ReviewAction` row
  (`Action='Scheduled'`), mirroring `TryResolveAsync` (`ReviewRepository.cs:675-697`).
  Confirmation shows the *actual* slot: „📅 Насрочено за {дд.MM HH:mm}".
- **✅ Одобри unchanged** for pending drafts (immediate publish path). Pressing ✅ on an
  already-**scheduled** draft overrides the schedule: guarded UPDATE
  `Approved + ScheduledForUtc IS NOT NULL → ScheduledForUtc = NULL` (+ audit row) — publishes
  next cycle. Double-taps stay no-ops via the status guards.

### Publish gate

`GetApprovedForFacebookAsync` (and `GetPendingFacebookAsync`, for site mode) WHERE clause adds:

```sql
AND (d.ScheduledForUtc IS NULL OR d.ScheduledForUtc <= SYSUTCDATETIME())
```

Everything else (FIFO order, `MaxPerCycle = 3`, retry budget, idempotency via
`nw_PublishRecord`) is unchanged.

## Testing

- `FacebookCaption.Compose` — hashtag joining, empty-hashtag handling.
- `DraftValidator` — caption bounds, uppercase-ratio, hashtag normalization/violations.
- `GeminiDraftingAi.ParseDraft` — new fields round-trip; missing caption → violation, not crash.
- `PublishSlotSuggester` — inside/outside windows, gap conflicts, `MaxPerDay` rollover to next
  day, `LeadMinutes`, empty-commitments case.
- `ReviewUpdateRouter` — `schedule:{id}` parsing, bad args.
- Repository tests — caption/NULL branches of `GetApprovedForFacebookAsync`, schedule gate,
  `TryScheduleAsync` + ✅-override transitions.
- `FacebookPublisher` — empty-headline message composition.

## Rollout

- Prompt change affects **new** drafts only; everything in flight falls back cleanly
  (`FacebookCaption IS NULL` → old behavior).
- No new secrets; `Facebook:DryRun` still guards real posting; schedule defaults live in
  `appsettings.json` and can be tuned without code changes.
- Docs to touch in implementation: `05-integrations/facebook.md` (post format + scheduling),
  `05-integrations/telegram.md` (new button), decision-log entry, roadmap progress log.

## Deferred — smart insights (backlog sketch, do NOT implement now)

Recorded so the later work has a starting point: nullable metric columns on `nw_PublishRecord`
(`ReactionsCount, CommentsCount, SharesCount, ImpressionsUnique, MetricsCheckedAtUtc`); hourly
sweep in `PublishJob` for posts < 72 h old via
`GET /{postId}?fields=reactions.summary(true),comments.summary(true),shares`
(`pages_read_engagement` already granted; impressions need `read_insights` — optional, NULL if
denied); surface in the 09:00 digest („Вчерашни публикации" + 👍/💬/↗ per post); later:
data-driven `Windows` for the slot suggester and trend-weight feedback.
