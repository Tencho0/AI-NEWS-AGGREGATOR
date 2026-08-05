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
meta:{draftId}                     → ShowMetaPicker(draftId)   -- fits the EXISTING 2-part parsing
```

`categoryIndex`/`regionIndex` are positions into the same `Ai:Categories`/`Ai:Regions` lists
`GeminiDraftingOptions` already binds — never free text, so there is no way to construct an invalid
value through this path. Region has no "skip" callback: region is genuinely optional, and simply
never tapping a region button leaves it `null`, which Umbraco already accepts — no extra sentinel
needed. `Telegram.Bot`'s `callback_data` is capped at 64 bytes; `setregion:{long}:{int}` comfortably
fits.

### New `ReviewCommand` records (`ReviewCommand.cs`)

```csharp
public sealed record SetDraftCategory(long DraftId, string Category) : ReviewCommand;
public sealed record SetDraftRegion(long DraftId, string Region) : ReviewCommand;
public sealed record SetDraftTags(long DraftId, IReadOnlyList<string> Tags) : ReviewCommand;
/// <summary>🏷 pressed: re-render the card with category+region picker rows appended.</summary>
public sealed record ShowMetaPicker(long DraftId) : ReviewCommand;
```

### Tags — text reply, a second slot in the existing pending-conversation table

`nw_TelegramPending` already carries a `Kind` discriminator (`ReviewRepository.cs:30`,
`ChangeInstructionsKind = "ChangeInstructions"`) even though today only one kind is ever written —
`SetPendingConversationAsync` unconditionally `DELETE`s the (chat, user) row before inserting, so
at most one pending conversation exists per (chat, user) regardless of kind. This is the existing
extension point: add a second kind (`TagsKind = "Tags"`) via two new, deliberately separate
`IReviewRepository` methods (not a generalized `kind` parameter on the existing ones — smaller diff,
zero risk to the already-working ✏️ flow):

```csharp
Task<long?> GetPendingTagsConversationAsync(long chatId, long userId, CancellationToken ct);
Task SetPendingTagsConversationAsync(long chatId, long userId, long draftId, CancellationToken ct);
```

Opening either kind silently replaces the other (same behavior the ✏️ flow already has for a second
✏️ press) — acceptable, since an editor mid-🏷-tags-flow is not simultaneously mid-✏️-flow in
practice. The existing `ClearPendingConversationAsync` (unconditional on `Kind`) closes either slot,
so no new clear method is needed. `RouteText` gains a `pendingTagsDraftId` parameter, checked
**before** the existing change-instructions check:

```csharp
if (pendingTagsDraftId is { } tagsDraftId && !text.StartsWith('/'))
    return new SetDraftTags(tagsDraftId, ParseTags(text));
if ((draftIdFromReply ?? pendingDraftId) is { } draftId && !text.StartsWith('/'))
    return new SubmitChangeInstructions(draftId, text);
```

`ParseTags`: split on `,`, trim each, drop empties — a pure helper next to `RoutePost`. No "skip"
affordance needed: not replying leaves tags exactly as they were (empty on a fresh draft).

### Repository — `DraftRepository`: three narrow setters, not one combined update

Category, region and tags are set independently, at different times (a category tap; later,
optionally, a region tap; separately, an optional tags reply). A single combined
`UpdateManualMetadataAsync(category, region, tags)` — the original draft of this spec — would force
every call site to pass all three, and an isolated category tap would silently null out a
previously-set region. Three single-column setters avoid that class of bug entirely:

```csharp
Task SetDraftCategoryAsync(long draftId, string category, CancellationToken ct);
Task SetDraftRegionAsync(long draftId, string region, CancellationToken ct);
Task SetDraftTagsAsync(long draftId, IReadOnlyList<string> tags, CancellationToken ct);
```

Each runs a narrow `UPDATE dbo.nw_Draft SET <OneColumn> = @value WHERE Id = @draftId` — never
touches `Headline`/`BodyMarkdown`/`Version`/`PromptVersion`/`Model` — followed by the **same shared
retry step**, in the same transaction (a private `ReopenIfPublishFailedAsync(connection,
transaction, draftId, ct)` helper all three call): read the draft's current `Status`; if
`PublishFailed`, (1) `UPDATE dbo.nw_PublishRecord SET Attempts = 0 WHERE DraftId = @draftId AND
Destination = 'umbraco' AND Status = 'Failed'` (clears the attempt weight
`PublishRepository.RecordFailureAsync` wrote — mirrors `tools/2026-08-04-repair-stale-taxonomy.sql`
step 3, which zeroes `Attempts` rather than deleting the row so the error history survives), then
(2) `UPDATE dbo.nw_Draft SET Status = 'Approved' WHERE Id = @draftId` (mirrors step 4) so the next
`PublishJob` cycle retries. Any other status (`PendingReview`, `Approved`, `Published`, `Rejected`,
`Generating`) leaves `Status` untouched — the helper is a no-op past the column update. Tags can
never be the reason a publish was rejected (Umbraco doesn't require them), so running the same
reopen step after `SetDraftTagsAsync` is harmless idempotence, not a real recovery path — kept only
for consistency across the three setters rather than special-casing tags out.

### Card rendering — new `ManualCardKeyboard` enum + two new gateway methods

`TelegramGateway.SendHtmlAsync`/`EditHtmlAsync` are called from **8 files**
(`TelegramJob`, `PublishJob`, `DailyDigestJob`, `WatchdogJob`, `FacebookTestPostService`,
`TelegramOperatorAlerts`, `SandboxTelegramGateway`, and their tests) for plain confirmations and
non-manual review cards alike. Extending their signatures to carry manual-picker state would ripple
through call sites that have nothing to do with `/post` metadata. Instead, two **new**,
manual-card-only methods on `ITelegramGateway` leave the existing ones untouched:

```csharp
// Newsroom.Core.Review — visible to both TelegramJob (Worker) and the gateway (Infrastructure).
public enum ManualCardKeyboard
{
    /// <summary>Category is null: category-picker buttons + ✏️/❌ only — no ✅/📅.</summary>
    AwaitingCategory,
    /// <summary>Category is set: the normal ✅/✏️/❌(/📅) row plus a single 🏷 correction button.</summary>
    Resolved,
    /// <summary>🏷 pressed: Resolved's buttons kept, category + region picker rows appended.</summary>
    Expanded,
}
```

```csharp
Task<long> SendManualCardAsync(
    long chatId, string html, long draftId, ManualCardKeyboard keyboard,
    string? scheduleButtonLabel, CancellationToken ct);
Task EditManualCardAsync(
    long chatId, long messageId, string html, long draftId, ManualCardKeyboard keyboard,
    string? scheduleButtonLabel, CancellationToken ct);
```

`TelegramGateway`'s constructor gains `IReadOnlyList<string> categories, IReadOnlyList<string>
regions` (bound once at DI registration from `GeminiDraftingOptions.From(configuration)` —
`Program.cs` already has `builder.Configuration` in scope where the gateway is registered) so it can
build category/region button rows with the exact labels and indices the router resolves back. Row
layout: 2 buttons per row for both lists. `scheduleButtonLabel` behaves as in `SendHtmlAsync` for
`Resolved`/`Expanded` (ignored for `AwaitingCategory`, matching "block Approve — and by extension
the 📅 shortcut to it — until Category is set"). `SandboxTelegramGateway` gets straight
passthrough-plus-marking implementations of both, mirroring its existing methods, with matching
`RecordingGateway` additions in `SandboxTelegramGatewayTests`.

`ReviewMessageRenderer.RenderHtml`: when `IsManual && string.IsNullOrEmpty(Category)`, render
`⚠️ Няма зададена категория` instead of omitting the metadata line entirely (current gap:
`ReviewMessageRenderer.cs:47-55` renders nothing when all three fields are empty, giving the editor
no visual cue).

`TelegramJob.DispatchPendingAsync` picks the keyboard per view: non-manual drafts keep calling
`SendHtmlAsync` exactly as today; manual drafts call `SendManualCardAsync` with `AwaitingCategory`
when `Category` is empty, `Resolved` otherwise. The three new callback handlers
(`SetDraftCategory`/`SetDraftRegion`/`ShowMetaPicker`) all re-render via a new private
`RerenderManualCardAsync` helper — fetch the view, render HTML, pick `Resolved` (category tap) or
`Expanded` (🏷 tap), call `EditManualCardAsync`. `SetDraftTags` re-renders the same way after saving
(always `Resolved` — tags are only reachable once Category is already set).

**Approve guard (defense in depth):** the `AwaitingCategory` keyboard has no ✅ button, so a normal
tap cannot approve a categoryless draft — but `callback_data` is just a string, not cryptographically
tied to what is currently rendered, so a replayed/crafted `approve:{draftId}` could still reach
`HandleCallbackAsync`. Before calling `reviews.TryApproveAsync` in the `ApproveDraft` case, fetch the
view and refuse (toast "Първо избери категория", no transition) when `view.IsManual &&
string.IsNullOrWhiteSpace(view.Category)`. One extra `GetReviewViewAsync` call per ✅ press — human-
paced, negligible cost — matching the goal's "structurally impossible" claim rather than relying on
UI-only enforcement.

## Config fix

Sync the two stale C# fallback constants to the corrected `appsettings.json` values so they stop
being a landmine for any environment that omits `Ai:Categories`/`Ai:Regions`:

- `GeminiAiOptions.DefaultCategories` → `["Общество","Политика","Криминално","Икономика / Бизнес","Спорт","Култура","Любопитно","Хайлайф"]`
- `GeminiDraftingOptions.DefaultRegions` → `["Благоевград","Кюстендил","Перник","София","България"]`

String-only change, no behavior change where config is already present (production today).

## Testing (TDD)

1. **Router (pure)** — `ReviewUpdateRouterTests`: `setcat:123:2` / `setregion:123:0` parse
   correctly; malformed payloads (`setcat:abc`, out-of-range index) fall back to
   `Ignore(ReasonUnknownData)`; `meta:123` routes to `ShowMetaPicker(123)`; existing
   `approve:123`-style single-id commands unaffected; a pending-tags reply routes to `SetDraftTags`
   ahead of the existing pending-changes check.
2. **Repository** — build-verify only (no DB test harness, matching every other repository SQL
   method in this codebase): `SetDraftCategoryAsync`/`SetDraftRegionAsync`/`SetDraftTagsAsync`
   compile and match their `IDraftRepository` signatures; manual UAT covers the actual
   `PublishFailed → Approved` transition.
3. **Renderer** — manual-topic card shows the warning line when Category is empty, and the
   real metadata line once set.
4. **Sandbox gateway** — `SandboxTelegramGatewayTests`: `SendManualCardAsync`/`EditManualCardAsync`
   mark the HTML and pass every argument through, matching the existing `SendHtmlAsync`/
   `EditHtmlAsync` test pattern.
5. **Manual UAT** — `/post` → tap category → ✅ appears → tap 🏷 → tap region → tags reply →
   ✅ → publishes; a deliberately-mis-set category on a `PublishFailed` fixture draft → tap 🏷 →
   new category → next `PublishJob` cycle succeeds without any manual SQL; a crafted
   `approve:{id}` callback on a still-categoryless draft is refused with a toast.

## Docs

- `docs/05-integrations/telegram.md`: document the category/region button flow and the 🏷 button.
- `docs/11-risks-and-open-questions.md`: close Q-10 (manual drafts previously had no
  category/SEO path).
- `docs/decision-log.md`: one line for the feature decision.

## Files touched

- `src/Newsroom.Core/Review/ReviewCommand.cs` (four new records)
- `src/Newsroom.Core/Review/ReviewUpdateRouter.cs` (`RouteCallback` 3-part parsing for
  `setcat`/`setregion`, `meta` on the existing 2-part path, `RouteText` tags-reply routing)
- `src/Newsroom.Core/Review/ReviewMessageRenderer.cs` (missing-category warning line)
- `src/Newsroom.Core/Review/ManualCardKeyboard.cs` (new enum)
- `src/Newsroom.Core/Review/Interfaces.cs` (`ITelegramGateway.SendManualCardAsync`/
  `EditManualCardAsync`; `IReviewRepository.GetPendingTagsConversationAsync`/
  `SetPendingTagsConversationAsync`)
- `src/Newsroom.Core/Drafting/Interfaces.cs` (three new `IDraftRepository` setter signatures)
- `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs` (the three setters +
  `ReopenIfPublishFailedAsync` helper)
- `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs` (`TagsKind`, the two new
  pending-tags methods)
- `src/Newsroom.Infrastructure/Review/TelegramGateway.cs` (constructor gains categories/regions;
  `SendManualCardAsync`/`EditManualCardAsync` + keyboard-row builder)
- `src/Newsroom.Infrastructure/Review/SandboxTelegramGateway.cs` (passthrough + marking for the two
  new methods)
- `src/Newsroom.Infrastructure/Ai/GeminiAiOptions.cs` (`DefaultCategories` fix)
- `src/Newsroom.Infrastructure/Ai/GeminiDraftingOptions.cs` (`DefaultRegions` fix)
- `src/Newsroom.Worker/Jobs/TelegramJob.cs` (dispatch branching, new callback/text command
  handling, `RerenderManualCardAsync` helper, approve guard, `/help` text)
- `src/Newsroom.Worker/Program.cs` (`TelegramGateway` construction passes
  `GeminiDraftingOptions.Categories`/`.Regions`)
- Tests: `ReviewUpdateRouterTests`, `SandboxTelegramGatewayTests`, renderer tests, new
  `GeminiAiOptionsTests`/`GeminiDraftingOptionsTests`
- Docs listed above
