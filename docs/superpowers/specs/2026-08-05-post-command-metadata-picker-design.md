# Design — Metadata (Category/Region/Tags) picker for `/post` drafts

**Date:** 2026-08-05 · **Status:** Approved · **Related:**
docs/superpowers/specs/2026-07-13-telegram-editor-authored-articles-design.md,
docs/11-risks-and-open-questions.md (Q-10), tools/2026-08-04-repair-stale-taxonomy.sql

## Problem

`/post <headline>\n<body>` (`DraftRepository.CreateManualArticleAsync`) inserts a draft with
`Category`/`Region`/`TagsJson` all `NULL` and jumps straight to `PendingReview` — unlike AI-drafted
articles, it never passes through `DraftValidator.Validate` (only called from `DraftJob.cs` at the
fresh-generation and ✏️-regeneration seams). Umbraco requires a category to publish; the rejection
is caught only server-side as an HTTP 400 (`UmbracoPublisher.cs:41-43` →
`PublishRejectedException`), which `PublishJob.HandleUmbracoFailureAsync` treats as a **hard**
failure — it burns the entire attempt budget in one shot (`attempts: rejected ? MaxAttempts : 1`)
and flips the draft to `PublishFailed` with no built-in retry.

Today the only way an editor can add a category is ✏️ Промени, which sends the whole draft through
`GeminiDraftingAi.GenerateAsync` for a full regeneration — no code path exists for a metadata-only
edit. Two compounding problems make this worse:

1. The regeneration system prompt has standing "never copy source text, always synthesize" rules
   that fight against an editor instruction to preserve the body verbatim — unreliable in
   practice (confirmed: one attempt rewrote the headline/subtitle/body; a later attempt with a
   more forceful instruction did preserve it, but nothing guarantees this).
2. Even when regeneration is used only to add a category, the AI picks from `Ai:Categories`
   (config), which is a **hand-maintained duplicate** of Umbraco's real taxonomy — verified via
   `tools/2026-08-04-repair-stale-taxonomy.sql`, which documents this exact class of bug (AI wrote
   `"Икономика"`, Umbraco's node is named `"Икономика / Бизнес"`) already happening once for
   AI-generated drafts before the 2026-08-04 `appsettings.json` fix.

A second latent bug found during investigation: `appsettings.json` carries the corrected taxonomy,
but the **C# fallback defaults** used when those config keys are absent are still the pre-fix,
wrong values (`GeminiAiOptions.DefaultCategories`, `GeminiDraftingOptions.DefaultRegions`) — masked
today only because config is present, but a landmine for any environment/test that omits it.

## Goal

Let an editor set Category/Region/Tags on a `/post` draft by **tapping buttons**, with:

- Zero AI calls, zero typing for category/region (buttons are built from the same
  `Ai:Categories`/`Ai:Regions` config list the publish path already validates against, so a
  mismatch between what's offered and what's accepted becomes structurally impossible).
- ✅ Approve hidden until Category is set — closes the bug at the source instead of relying on
  Umbraco's 400 as the backstop.
- A permanent **🏷 Категория/Регион** button on every manual-topic card (not just while Category is
  null) so a wrong category can be corrected later the same way, without ✏️ Промени / AI at all.
- Fixing a category on an already-`PublishFailed` draft automatically reopens it for the next
  publish cycle — no more manual SQL (`tools/2026-08-04-repair-stale-taxonomy.sql`'s pattern,
  built into the app).

## Non-goals (YAGNI)

- Tags stay free-text (a reply, like ✏️ instructions today) — no curated tag-button list to
  maintain in sync with a third taxonomy.
- No change to `/new` (AI-assisted) — it already rides `DraftJob`'s normal
  `DraftValidator`-gated path and picks a valid category today (once the fallback-default fix
  below lands).
- No change to how ✏️ Промени/AI regeneration works — it remains available on manual cards for
  editors who *do* want AI to touch the body; this feature is an additional, AI-free path, not a
  replacement.
- No admin UI for editing the `Ai:Categories`/`Ai:Regions` list itself — still a config value,
  hand-synced against the Umbraco repo's `TaxonomySeedSetup.cs` as today (out of scope to build
  cross-repo sync tooling).

## Architecture

### Callback routing — `ReviewUpdateRouter.RouteCallback`

Current parsing (`ReviewUpdateRouter.cs:29-32`) finds the **first** `:` and parses everything after
it as a bare `long draftId` — it cannot carry a third field. Extend it: for the two new prefixes,
split the remainder on `:` again into `draftId` and `index`; existing prefixes (`approve`,
`reject`, `changes`, `image`, `schedule`) keep their current single-id parsing unchanged.

```
setcat:{draftId}:{categoryIndex}   → SetDraftCategory(draftId, Categories[categoryIndex])
setregion:{draftId}:{regionIndex}  → SetDraftRegion(draftId, Regions[regionIndex])
setregion:{draftId}:skip           → SetDraftRegion(draftId, null)
```

`categoryIndex`/`regionIndex` are positions into the router's injected `Ai:Categories`/`Ai:Regions`
lists (already available where `TelegramGateway` builds cards) — never free text, so there is no
way to construct an invalid value through this path. `Telegram.Bot`'s `callback_data` is capped at
64 bytes; `setregion:{long}:{int}` comfortably fits.

### New `ReviewCommand` records (`ReviewCommand.cs`)

```csharp
public sealed record SetDraftCategory(long DraftId, string Category) : ReviewCommand;
public sealed record SetDraftRegion(long DraftId, string? Region) : ReviewCommand;
public sealed record SetDraftTags(long DraftId, IReadOnlyList<string> Tags) : ReviewCommand;
```

### Tags — text reply, new pending-conversation kind

Reuses the existing "reply binds to a draft" mechanism (`RouteText`'s `draftIdFromReply ??
pendingDraftId` check, `ReviewUpdateRouter.cs:77-78`) but must **not** collide with the ✏️
AI-conversation state — a tags reply must never be swallowed as `SubmitChangeInstructions` and
sent to the AI. `RouteText` gains a `pendingTagsDraftId` parameter (mirrors `pendingDraftId`,
tracked separately by `TelegramJob`); when set and the reply isn't a `/command`, it routes to
`SetDraftTags(draftId, ParseTags(text))` instead. A "🏷 Пропусни" inline button
(`skiptags:{draftId}`) lets the editor finish without typing anything.

### Repository — `DraftRepository`

New method, the piece that doesn't exist today (the only current writers of Category/Region/
TagsJson are `SaveDraftAsync` — new AI draft — and `CompleteRegenerationAsync` — full AI rewrite):

```csharp
Task UpdateManualMetadataAsync(
    long draftId, string? category, string? region, IReadOnlyList<string>? tags, CancellationToken ct);
```

A narrow `UPDATE dbo.nw_Draft SET Category = @category, Region = @region, TagsJson = @tags WHERE
Id = @draftId` — never touches `Headline`/`BodyMarkdown`/`Version`/`PromptVersion`/`Model`.

**Retry semantics**, inside the same method/transaction: read the draft's current `Status` first.

- `PendingReview` → just update the columns; card re-render now shows ✅.
- `PublishFailed` → update the columns, **and**:
  1. `UPDATE dbo.nw_PublishRecord SET Attempts = 0 WHERE DraftId = @draftId AND Destination =
     'umbraco'` (clears the burned attempt weight `HandleUmbracoFailureAsync` wrote).
  2. `UPDATE dbo.nw_Draft SET Status = 'Approved' WHERE Id = @draftId` (so the next `PublishJob`
     cycle picks it up — mirrors `tools/2026-08-04-repair-stale-taxonomy.sql` steps 3–4, done
     in-app instead of by hand).
- Any other status (`Approved`, `Published`, `Rejected`, `Generating`) → update the columns only;
  a category fix after `Published` has no publish-side effect (Umbraco's endpoint is create-only
  and won't be re-called), so the 🏷 button on an already-published card is cosmetic-only for that
  case — acceptable, since the common case this whole feature targets is fixing metadata **before**
  the first successful publish.

### Card rendering — `ReviewMessageRenderer` / `TelegramGateway`

- `ReviewMessageRenderer.RenderHtml`: when `IsManual && string.IsNullOrEmpty(Category)`, render
  `⚠️ Няма зададена категория` instead of omitting the metadata line
  (current gap: `ReviewMessageRenderer.cs:47-55` renders nothing when all three fields are empty,
  giving the editor no visual cue).
- `TelegramGateway`'s keyboard builder (`TelegramGateway.cs:81-94`):
  - `IsManual && Category is null` → rows of category buttons (one per configured category,
    2-per-row), then (after category is picked and the card re-renders) region buttons + "🏷
    Пропусни" for region, **no** ✅ row yet. ✏️ Промени and ❌ Откажи stay available throughout —
    an editor can still discard a bad `/post` or fall back to AI without being blocked.
  - `IsManual && Category is not null` → today's `✅ / ✏️ / ❌` row, **plus** a new
    `🏷 Категория/Регион` button (`meta:{draftId}`) that redisplays the category/region picker rows
    on demand, for correcting an already-set value later. Unlike the first-time flow, ✅ stays
    visible while correcting — the draft already has a valid category, so nothing is blocked
    mid-edit; picking a new category simply overwrites it in place.

## Config fix

Sync the two stale C# fallback constants to the corrected `appsettings.json` values so they stop
being a landmine for any environment that omits `Ai:Categories`/`Ai:Regions`:

- `GeminiAiOptions.DefaultCategories` → `["Общество","Политика","Криминално","Икономика / Бизнес","Спорт","Култура","Любопитно","Хайлайф"]`
- `GeminiDraftingOptions.DefaultRegions` → `["Благоевград","Кюстендил","Перник","София","България"]`

String-only change, no behavior change where config is already present (production today).

## Testing (TDD)

1. **Router (pure)** — `ReviewUpdateRouterTests`: `setcat:123:2` / `setregion:123:0` /
   `setregion:123:skip` parse correctly; malformed payloads (`setcat:abc`, out-of-range index)
   fall back to `Ignore(ReasonUnknownData)`; existing `approve:123`-style single-id commands
   unaffected.
2. **Repository** — new test fixture for `UpdateManualMetadataAsync`: confirms
   Headline/BodyMarkdown/Version/Model are untouched; confirms the `PublishFailed → Approved` +
   `Attempts = 0` transition fires only when the pre-update status was `PublishFailed`; confirms a
   `PendingReview` draft is left in `PendingReview`.
3. **Renderer** — manual-topic card shows the warning line when Category is null, and the
   real metadata line once set.
4. **Manual UAT** — `/post` → tap category → tap region → skip tags → ✅ → publishes; a
   deliberately-mis-set category on a `PublishFailed` fixture draft → tap 🏷 → new category → next
   `PublishJob` cycle succeeds without any manual SQL.

## Docs

- `docs/05-integrations/telegram.md`: document the category/region button flow and the 🏷 button.
- `docs/11-risks-and-open-questions.md`: close Q-10 (manual drafts previously had no
  category/SEO path).
- `docs/decision-log.md`: one line for the feature decision.

## Files touched

- `src/Newsroom.Core/Review/ReviewCommand.cs` (three new records)
- `src/Newsroom.Core/Review/ReviewUpdateRouter.cs` (`RouteCallback` 3-part parsing, `RouteText`
  tags-reply routing)
- `src/Newsroom.Core/Review/ReviewMessageRenderer.cs` (missing-category warning line)
- `src/Newsroom.Core/Drafting/Interfaces.cs` (`UpdateManualMetadataAsync` signature)
- `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs` (`UpdateManualMetadataAsync` +
  retry semantics)
- `src/Newsroom.Infrastructure/Review/TelegramGateway.cs` (category/region/🏷/skip-tags keyboards)
- `src/Newsroom.Infrastructure/Ai/GeminiAiOptions.cs` (`DefaultCategories` fix)
- `src/Newsroom.Infrastructure/Ai/GeminiDraftingOptions.cs` (`DefaultRegions` fix)
- `src/Newsroom.Worker/Jobs/TelegramJob.cs` (new command handling, `pendingTagsDraftId` state,
  `/help` text)
- Tests: `ReviewUpdateRouterTests`, new `DraftRepository` metadata tests, renderer tests
- Docs listed above
