# /post Metadata (Category/Region/Tags) Picker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an editor set Category/Region/Tags on a `/post` draft by tapping buttons — zero AI calls, zero typos — instead of the only path that exists today (✏️ Промени, which sends the whole draft through AI regeneration and can rewrite content it was never asked to touch). ✅ Approve is hidden until Category is set; fixing a category on an already-`PublishFailed` draft automatically reopens it for the next publish cycle.

**Architecture:** Three narrow `DraftRepository` setters (one per column) replace the missing "metadata-only edit" path; each shares a `ReopenIfPublishFailedAsync` step that mirrors `tools/2026-08-04-repair-stale-taxonomy.sql`'s manual-SQL recovery, done in-app. `ReviewUpdateRouter` gains `setcat:{id}:{idx}` / `setregion:{id}:{idx}` (resolved against the same `Ai:Categories`/`Ai:Regions` lists the publish path already validates against — so an offered button can never produce an invalid value) and `meta:{id}` for the 🏷 correction button. Two new `ITelegramGateway` methods (`SendManualCardAsync`/`EditManualCardAsync`) carry a new `ManualCardKeyboard` state (`AwaitingCategory` / `Resolved` / `Expanded`) so the existing `SendHtmlAsync`/`EditHtmlAsync` — used by 8 unrelated call sites — stay untouched. Tags reuse the existing `nw_TelegramPending` table's `Kind` column (a second slot, `"Tags"`, alongside the existing `"ChangeInstructions"` one) rather than new state.

**Tech Stack:** .NET 9 worker (C# primary constructors, collection expressions), Dapper + SQL Server, Telegram.Bot via `ITelegramGateway`, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-05-post-command-metadata-picker-design.md`

## Global Constraints

- All bot replies and button labels are **Bulgarian**; everything interpolated into Telegram HTML goes through `ReviewMessageRenderer.Escape`.
- **No DB-backed test harness exists — do not add one.** Repository SQL methods (`DraftRepository`, `ReviewRepository`) are build-verify + manual UAT; unit-test only pure logic (router, renderer) and the logic-bearing `SandboxTelegramGateway` decorator. `TelegramGateway` itself stays untested by design (it is "intentionally logic-free" — a thin wrapper over `Telegram.Bot`).
- Statuses are stored as enum names (`nameof(...)`); `nw_PublishRecord.Status` values are the string literals `"Succeeded"`/`"Failed"` (no shared enum — matches `PublishRepository`'s existing private constants, which are not visible outside that class).
- Categories/Regions travel by **name**, never by numeric id, once past the button tap — `Categories[index]` is resolved once, at the router, and the resulting string is what every downstream layer stores and compares. Never invent a new taxonomy list; reuse `GeminiDraftingOptions.Categories`/`.Regions` everywhere.
- Commit messages: match repo style (`feat(review): …`, `fix(review): …`, `docs: …`). **Never add a `Co-Authored-By` line.**
- Files contain Cyrillic — edit only with the Edit/Write tools, never PowerShell `Get-Content`/`Set-Content`.
- Run all commands from the repo root. `dotnet build` and `dotnet test` with no args build/test everything.
- `Telegram.Bot`'s `callback_data` is capped at 64 bytes — every callback prefix introduced here (`setcat`, `setregion`, `meta`) comfortably fits with any real draft id and index.

---

### Task 1: Fix the stale C# taxonomy fallback defaults

**Files:**
- Modify: `src/Newsroom.Infrastructure/Ai/GeminiAiOptions.cs`
- Modify: `src/Newsroom.Infrastructure/Ai/GeminiDraftingOptions.cs`
- Test: `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiAiOptionsTests.cs`
- Test: `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiDraftingOptionsTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks — fully standalone.
- Produces: nothing later tasks call directly; this closes a landmine (wrong values used only when `Ai:Categories`/`Ai:Regions` config keys are absent) that is otherwise unrelated to the button-picker work but was found during the investigation that led to this plan.

**Background:** `appsettings.json` already carries the corrected taxonomy (fixed 2026-08-04), but `GeminiAiOptions.DefaultCategories` and `GeminiDraftingOptions.DefaultRegions` — the fallback used when those config keys are missing — are still the pre-fix, wrong values. No existing test references either constant.

- [ ] **Step 1: Write the failing tests**

Create `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiAiOptionsTests.cs`:

```csharp
using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiAiOptionsTests
{
    [Fact]
    public void Default_categories_match_the_sites_real_taxonomy()
    {
        // Predel-News src/Web/PredelNews.Web/Setup/TaxonomySeedSetup.cs is the source of truth —
        // mirrored in appsettings.json's Ai:Categories and here as the code-level fallback used
        // when that config key is absent. Both must agree; "Икономика" is NOT a real node, the
        // site's node is "Икономика / Бизнес" (tools/2026-08-04-repair-stale-taxonomy.sql).
        Assert.Equal(
            ["Общество", "Политика", "Криминално", "Икономика / Бизнес", "Спорт", "Култура", "Любопитно", "Хайлайф"],
            GeminiAiOptions.DefaultCategories);
    }
}
```

Create `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiDraftingOptionsTests.cs`:

```csharp
using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiDraftingOptionsTests
{
    [Fact]
    public void Default_regions_are_the_sites_five_provinces_not_the_old_municipality_list()
    {
        // Pre-2026-08-04 this was the fourteen municipalities of Blagoevgrad Province — wrong,
        // the site's actual region taxonomy is five provinces (tools/2026-08-04-repair-stale-taxonomy.sql).
        Assert.Equal(
            ["Благоевград", "Кюстендил", "Перник", "София", "България"],
            GeminiDraftingOptions.DefaultRegions);
    }

    [Fact]
    public void Default_categories_delegate_to_GeminiAiOptions()
    {
        Assert.Equal(GeminiAiOptions.DefaultCategories, GeminiDraftingOptions.From(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Categories);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GeminiAiOptionsTests|FullyQualifiedName~GeminiDraftingOptionsTests"`
Expected: FAIL — `Default_categories_match_the_sites_real_taxonomy` and `Default_regions_are_the_sites_five_provinces_not_the_old_municipality_list` fail on value mismatch (old lists still in place); `Default_categories_delegate_to_GeminiAiOptions` passes already (unrelated to the fix).

- [ ] **Step 3: Fix the two constants**

In `src/Newsroom.Infrastructure/Ai/GeminiAiOptions.cs`, replace:

```csharp
    public static readonly IReadOnlyList<string> DefaultCategories =
    [
        "Общество", "Политика", "Икономика", "Криминално", "Спорт",
        "Култура", "Здраве", "Образование", "Времето", "Друго",
    ];
```

with:

```csharp
    public static readonly IReadOnlyList<string> DefaultCategories =
    [
        "Общество", "Политика", "Криминално", "Икономика / Бизнес",
        "Спорт", "Култура", "Любопитно", "Хайлайф",
    ];
```

In `src/Newsroom.Infrastructure/Ai/GeminiDraftingOptions.cs`, replace:

```csharp
    /// <summary>The municipalities of the Blagoevgrad district — the site's region taxonomy.</summary>
    public static readonly IReadOnlyList<string> DefaultRegions =
    [
        "Благоевград", "Петрич", "Сандански", "Гоце Делчев", "Разлог", "Банско", "Симитли",
        "Кресна", "Струмяни", "Якоруда", "Белица", "Хаджидимово", "Гърмен", "Сатовча",
    ];
```

with:

```csharp
    /// <summary>The site's five provinces (Predel-News TaxonomySeedSetup.cs is the source of
    /// truth — corrected 2026-08-04, see tools/2026-08-04-repair-stale-taxonomy.sql).</summary>
    public static readonly IReadOnlyList<string> DefaultRegions =
    [
        "Благоевград", "Кюстендил", "Перник", "София", "България",
    ];
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GeminiAiOptionsTests|FullyQualifiedName~GeminiDraftingOptionsTests"`
Expected: PASS (3 tests).

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Newsroom.Infrastructure/Ai/GeminiAiOptions.cs src/Newsroom.Infrastructure/Ai/GeminiDraftingOptions.cs src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiAiOptionsTests.cs src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiDraftingOptionsTests.cs
git commit -m "fix(ai): sync code-level taxonomy fallback defaults with the corrected site taxonomy"
```

---

### Task 2: Core types — `ManualCardKeyboard`, four `ReviewCommand` records, router routing + tests

**Files:**
- Create: `src/Newsroom.Core/Review/ManualCardKeyboard.cs`
- Modify: `src/Newsroom.Core/Review/ReviewCommand.cs`
- Modify: `src/Newsroom.Core/Review/ReviewUpdateRouter.cs`
- Test: `src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `ManualCardKeyboard` enum (`AwaitingCategory`/`Resolved`/`Expanded`) — Task 6's gateway methods and Task 7's `TelegramJob` both switch on this. `SetDraftCategory(long DraftId, string Category)`, `SetDraftRegion(long DraftId, string Region)`, `SetDraftTags(long DraftId, IReadOnlyList<string> Tags)`, `ShowMetaPicker(long DraftId)` records — Task 7's `TelegramJob` matches on these exact names. `ReviewUpdateRouter.RouteCallback` gains two new **required** parameters (`categories`, `regions`) at the end — Task 7 updates its one production call site. `RouteText` gains one new **optional** parameter (`pendingTagsDraftId = null`) — existing call sites (including every test in this file that does not pass it) keep compiling unchanged.

- [ ] **Step 1: Write the failing tests**

Create `src/Newsroom.Core/Review/ManualCardKeyboard.cs`:

```csharp
namespace Newsroom.Core.Review;

/// <summary>
/// Which inline keyboard a manual-topic (/post) review card shows, driven by whether Category is
/// set (docs/superpowers/specs/2026-08-05-post-command-metadata-picker-design.md). Non-manual
/// (AI-drafted, trend-scored) cards never use this — they keep the fixed ✅/✏️/❌(/📅) keyboard.
/// </summary>
public enum ManualCardKeyboard
{
    /// <summary>Category is empty: category-picker buttons + ✏️/❌ only — no ✅/📅, so a draft
    /// with no publishable category cannot be approved.</summary>
    AwaitingCategory,

    /// <summary>Category is set: the normal ✅/✏️/❌(/📅) row plus a single 🏷 correction button.</summary>
    Resolved,

    /// <summary>🏷 pressed: Resolved's buttons stay, category + region picker rows are appended
    /// below so an already-valid category can still be corrected without AI.</summary>
    Expanded,
}
```

In `src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs`, extend the fixture fields (after `Allowed`) and the two routing helpers:

```csharp
    private static readonly IReadOnlySet<long> Allowed = new HashSet<long> { Editor };
    private static readonly IReadOnlyList<string> Categories = ["Общество", "Икономика / Бизнес", "Спорт"];
    private static readonly IReadOnlyList<string> Regions = ["Благоевград", "София"];
```

```csharp
    private static ReviewCommand RouteCallback(TgCallback c) =>
        ReviewUpdateRouter.RouteCallback(c, Allowed, ReviewChat, Categories, Regions);

    private static ReviewCommand RouteText(
        TgText t, long? pendingDraftId = null, long? draftIdFromReply = null, long? pendingTagsDraftId = null) =>
        ReviewUpdateRouter.RouteText(t, Allowed, ReviewChat, pendingDraftId, draftIdFromReply, pendingTagsDraftId);
```

Append these tests at the end of the class, before the closing brace:

```csharp
    [Fact]
    public void Setcat_and_setregion_resolve_the_configured_value_by_index()
    {
        Assert.Equal(new SetDraftCategory(42, "Икономика / Бизнес"), RouteCallback(Callback("setcat:42:1")));
        Assert.Equal(new SetDraftRegion(42, "София"), RouteCallback(Callback("setregion:42:1")));
    }

    [Fact]
    public void Meta_callback_routes_to_ShowMetaPicker()
    {
        Assert.Equal(new ShowMetaPicker(42), RouteCallback(Callback("meta:42")));
    }

    [Theory]
    [InlineData("setcat:42:99")]   // out of range (only 3 configured categories)
    [InlineData("setcat:42:-1")]   // negative
    [InlineData("setcat:42:abc")]  // non-numeric index
    [InlineData("setcat:42")]      // missing index segment
    [InlineData("setregion:42:99")]
    public void Setcat_and_setregion_with_a_bad_index_are_ignored(string data)
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonUnknownData), RouteCallback(Callback(data)));
    }

    [Fact]
    public void Setcat_and_setregion_are_gated_like_every_other_callback()
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonNotAllowlisted),
            RouteCallback(Callback("setcat:42:0", userId: Stranger)));
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonWrongChat),
            RouteCallback(Callback("setcat:42:0", chatId: OtherChat)));
    }

    [Fact]
    public void Pending_tags_reply_becomes_SetDraftTags()
    {
        Assert.Equal(
            new SetDraftTags(42, ["труд", "криза"]),
            RouteText(Text("труд, криза"), pendingTagsDraftId: 42));
    }

    [Fact]
    public void Tags_reply_trims_entries_and_drops_empty_ones()
    {
        Assert.Equal(
            new SetDraftTags(42, ["труд", "криза"]),
            RouteText(Text(" труд ,, криза ,  "), pendingTagsDraftId: 42));
    }

    [Fact]
    public void Command_during_pending_tags_conversation_still_routes_as_command()
    {
        Assert.Equal(new ShowStatus(), RouteText(Text("/status"), pendingTagsDraftId: 42));
    }

    [Fact]
    public void Pending_tags_takes_priority_over_a_pending_changes_conversation()
    {
        // The two slots share one table row (opening either replaces the other), so in practice
        // this never actually happens with both non-null — this pins the intended precedence.
        Assert.Equal(
            new SetDraftTags(42, ["тагове"]),
            RouteText(Text("тагове"), pendingDraftId: 7, pendingTagsDraftId: 42));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ReviewUpdateRouterTests"`
Expected: FAIL — `SetDraftCategory`/`SetDraftRegion`/`SetDraftTags`/`ShowMetaPicker` do not exist and `RouteCallback`/`RouteText` do not accept the new parameters (compile errors).

- [ ] **Step 3: Add the four `ReviewCommand` records**

In `src/Newsroom.Core/Review/ReviewCommand.cs`, insert after the `CreateAiArticle` record (before `Ignore`):

```csharp
/// <summary>🏷 tap or a category button on an AwaitingCategory card: sets Category on a manual
/// (/post) draft with no AI involved. Category is never null here — it always comes from
/// resolving a button index against the configured taxonomy list (docs/superpowers/specs/
/// 2026-08-05-post-command-metadata-picker-design.md).</summary>
public sealed record SetDraftCategory(long DraftId, string Category) : ReviewCommand;

/// <summary>A region button tap: sets Region on a manual draft, same no-AI path as
/// <see cref="SetDraftCategory"/>.</summary>
public sealed record SetDraftRegion(long DraftId, string Region) : ReviewCommand;

/// <summary>A text reply while the 🏷 tags conversation is open: comma-separated tags, no AI.</summary>
public sealed record SetDraftTags(long DraftId, IReadOnlyList<string> Tags) : ReviewCommand;

/// <summary>🏷 pressed: re-render the card with category + region picker rows appended
/// (docs/05-integrations/telegram.md).</summary>
public sealed record ShowMetaPicker(long DraftId) : ReviewCommand;
```

- [ ] **Step 4: Rewrite `RouteCallback`'s parsing to support the 3-part `setcat`/`setregion` payloads**

In `src/Newsroom.Core/Review/ReviewUpdateRouter.cs`, replace the whole `RouteCallback` method body:

```csharp
    public static ReviewCommand RouteCallback(
        TgCallback c, IReadOnlySet<long> allowedUsers, long reviewChatId,
        IReadOnlyList<string> categories, IReadOnlyList<string> regions)
    {
        if (c.ChatId != reviewChatId)
            return new Ignore(ReasonWrongChat);
        if (!allowedUsers.Contains(c.UserId))
            return new Ignore(ReasonNotAllowlisted);

        var segments = c.Data.Split(':');
        if (segments.Length < 2 || segments[0].Length == 0
            || !long.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var draftId))
            return new Ignore(ReasonUnknownData);

        return (segments[0], segments.Length) switch
        {
            ("approve", 2) => new ApproveDraft(draftId),
            ("reject", 2) => new RejectDraft(draftId),
            ("changes", 2) => new RequestChanges(draftId),
            ("image", 2) => new CycleImage(draftId),
            ("schedule", 2) => new ScheduleDraft(draftId),
            ("meta", 2) => new ShowMetaPicker(draftId),
            ("setcat", 3) when TryResolveIndex(segments[2], categories, out var category) =>
                new SetDraftCategory(draftId, category),
            ("setregion", 3) when TryResolveIndex(segments[2], regions, out var region) =>
                new SetDraftRegion(draftId, region),
            _ => new Ignore(ReasonUnknownData),
        };
    }

    /// <summary>Resolves a callback's trailing index segment against a configured taxonomy list —
    /// the only way a category/region string reaches a <see cref="ReviewCommand"/>, so an invalid
    /// value can never be constructed through this path.</summary>
    private static bool TryResolveIndex(string raw, IReadOnlyList<string> options, out string value)
    {
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < options.Count)
        {
            value = options[index];
            return true;
        }
        value = "";
        return false;
    }
```

- [ ] **Step 5: Add the tags-conversation check to `RouteText`**

In `src/Newsroom.Core/Review/ReviewUpdateRouter.cs`, change the `RouteText` signature (add the trailing optional parameter):

```csharp
    public static ReviewCommand RouteText(
        TgText t, IReadOnlySet<long> allowedUsers, long reviewChatId, long? pendingDraftId,
        long? draftIdFromReply, long? pendingTagsDraftId = null)
```

and insert the tags check immediately before the existing reply/pending-conversation check:

```csharp
        // A pending 🏷 tags conversation takes priority: the editor just tapped a button that
        // explicitly asked for tags text, so the very next plain reply is virtually certainly
        // meant as tags — and the two conversation kinds share one table slot (opening either
        // replaces the other), so both being non-null in practice does not happen.
        if (pendingTagsDraftId is { } tagsDraftId && !text.StartsWith('/'))
            return new SetDraftTags(tagsDraftId, ParseTags(text));

        // A reply to a specific review card binds the instructions to that card's draft —
        // unambiguous when several drafts await changes; the open ✏️ conversation is the
        // fallback and swallows the next non-command message as instructions.
        if ((draftIdFromReply ?? pendingDraftId) is { } draftId && !text.StartsWith('/'))
            return new SubmitChangeInstructions(draftId, text);
```

Add the parsing helper near `NormalizeNewlines`:

```csharp
    /// <summary>Comma-separated tags: trimmed, empties dropped.</summary>
    private static IReadOnlyList<string> ParseTags(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
```

- [ ] **Step 6: Run the full router test suite to verify everything passes**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ReviewUpdateRouterTests"`
Expected: PASS — every new test plus all pre-existing router tests (the `RouteCallback`/`RouteText` rewrites must not change behavior for `approve`/`reject`/`changes`/`image`/`schedule`/`/post`/`/new`/etc.).

Run: `dotnet build src/Newsroom.Core/Newsroom.Core.csproj`
Expected: Build succeeded, 0 errors. (A whole-solution `dotnet build` would currently fail — `Newsroom.Worker`'s `TelegramJob.cs` still calls the old 3-arg `RouteCallback` — that is expected until Task 7 and is why this step scopes to the one project this task touches.)

- [ ] **Step 7: Commit**

```bash
git add src/Newsroom.Core/Review/ManualCardKeyboard.cs src/Newsroom.Core/Review/ReviewCommand.cs src/Newsroom.Core/Review/ReviewUpdateRouter.cs src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs
git commit -m "feat(review): route setcat/setregion/meta callbacks and tags-conversation text"
```

---

### Task 3: `IReviewRepository` — a second `nw_TelegramPending` slot for tags

**Files:**
- Modify: `src/Newsroom.Core/Review/Interfaces.cs`
- Modify: `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `IReviewRepository.GetPendingTagsConversationAsync(long chatId, long userId, CancellationToken ct)` returning `Task<long?>`, and `SetPendingTagsConversationAsync(long chatId, long userId, long draftId, CancellationToken ct)` — Task 7's `TelegramJob` calls both.

No unit tests (SQL, no DB harness, matching this file's existing convention) — build-verify here, manual UAT at the end.

- [ ] **Step 1: Add the interface methods**

In `src/Newsroom.Core/Review/Interfaces.cs`, insert after `ClearPendingConversationAsync` (keeping the pending-conversation methods together):

```csharp
    /// <summary>The draft id of the open 🏷 tags conversation for (chat, user), if any — a
    /// separate slot from the ✏️ conversation above (opening either replaces the other; see
    /// <see cref="SetPendingTagsConversationAsync"/>).</summary>
    Task<long?> GetPendingTagsConversationAsync(long chatId, long userId, CancellationToken ct);

    /// <summary>Opens (or replaces) the single pending tags conversation for (chat, user).
    /// <see cref="ClearPendingConversationAsync"/> closes this slot too (unconditional on kind).</summary>
    Task SetPendingTagsConversationAsync(long chatId, long userId, long draftId, CancellationToken ct);
```

- [ ] **Step 2: Implement in `ReviewRepository`**

In `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs`, add the constant next to `ChangeInstructionsKind`:

```csharp
    private const string ChangeInstructionsKind = "ChangeInstructions";
    private const string TagsKind = "Tags";
```

Add the two methods right after `ClearPendingConversationAsync`:

```csharp
    public async Task<long?> GetPendingTagsConversationAsync(long chatId, long userId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long?>(
            """
            SELECT DraftId FROM dbo.nw_TelegramPending
            WHERE ChatId = @chatId AND UserId = @userId AND Kind = @kind
            """,
            new { chatId, userId, kind = TagsKind });
    }

    public async Task SetPendingTagsConversationAsync(
        long chatId, long userId, long draftId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // Same single-slot-per-(chat,user) behavior as SetPendingConversationAsync: opening this
        // replaces any open ✏️ conversation, and vice versa.
        await connection.ExecuteAsync(
            """
            DELETE FROM dbo.nw_TelegramPending WHERE ChatId = @chatId AND UserId = @userId;
            INSERT INTO dbo.nw_TelegramPending (ChatId, UserId, DraftId, Kind)
            VALUES (@chatId, @userId, @draftId, @kind);
            """,
            new { chatId, userId, draftId, kind = TagsKind });
    }
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Newsroom.Infrastructure/Newsroom.Infrastructure.csproj`
Expected: Build succeeded, 0 errors. (Whole-solution `dotnet build` still fails on `Newsroom.Worker`'s pre-existing Task 2 gap — expected until Task 7.)

- [ ] **Step 4: Commit**

```bash
git add src/Newsroom.Core/Review/Interfaces.cs src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs
git commit -m "feat(review): a second pending-conversation slot for tags replies"
```

---

### Task 4: `IDraftRepository` — three narrow metadata setters + in-app publish-failure reopen

**Files:**
- Modify: `src/Newsroom.Core/Drafting/Interfaces.cs`
- Modify: `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `IDraftRepository.SetDraftCategoryAsync(long draftId, string category, CancellationToken ct)`, `SetDraftRegionAsync(long draftId, string region, CancellationToken ct)`, `SetDraftTagsAsync(long draftId, IReadOnlyList<string> tags, CancellationToken ct)` — Task 7's `TelegramJob` calls all three.

No unit tests (SQL, no DB harness) — build-verify here, manual UAT at the end.

- [ ] **Step 1: Add the interface methods**

In `src/Newsroom.Core/Drafting/Interfaces.cs`, append to `IDraftRepository` (after `FailRegenerationAsync`):

```csharp
    /// <summary>Sets Category on a manual (/post) draft — touches only that one column, never
    /// Headline/BodyMarkdown/Version/PromptVersion/Model. If the draft is currently PublishFailed
    /// (Umbraco rejected the old value), clears the burned Umbraco attempt weight and flips it
    /// back to Approved so the next PublishJob cycle retries (docs/superpowers/specs/
    /// 2026-08-05-post-command-metadata-picker-design.md).</summary>
    Task SetDraftCategoryAsync(long draftId, string category, CancellationToken ct);

    /// <summary>As <see cref="SetDraftCategoryAsync"/>, for Region.</summary>
    Task SetDraftRegionAsync(long draftId, string region, CancellationToken ct);

    /// <summary>As <see cref="SetDraftCategoryAsync"/>, for Tags. Tags are never themselves the
    /// reason a publish was rejected, but this runs the same reopen check for consistency across
    /// the three setters rather than special-casing tags out.</summary>
    Task SetDraftTagsAsync(long draftId, IReadOnlyList<string> tags, CancellationToken ct);
```

- [ ] **Step 2: Implement in `DraftRepository`**

Add `using Newsroom.Core.Publishing;` to the file's usings (for `PublishDestinations.Umbraco`).

Add the three methods and the shared helper after `FailRegenerationAsync` (before `InsertImagesAsync`):

```csharp
    public async Task SetDraftCategoryAsync(long draftId, string category, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            "UPDATE dbo.nw_Draft SET Category = @category, UpdatedAtUtc = SYSUTCDATETIME() WHERE Id = @draftId",
            new { draftId, category = Truncate(category, 100) },
            transaction);
        await ReopenIfPublishFailedAsync(connection, transaction, draftId, ct);

        transaction.Commit();
    }

    public async Task SetDraftRegionAsync(long draftId, string region, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            "UPDATE dbo.nw_Draft SET Region = @region, UpdatedAtUtc = SYSUTCDATETIME() WHERE Id = @draftId",
            new { draftId, region = Truncate(region, 100) },
            transaction);
        await ReopenIfPublishFailedAsync(connection, transaction, draftId, ct);

        transaction.Commit();
    }

    public async Task SetDraftTagsAsync(long draftId, IReadOnlyList<string> tags, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            "UPDATE dbo.nw_Draft SET TagsJson = @tagsJson, UpdatedAtUtc = SYSUTCDATETIME() WHERE Id = @draftId",
            new { draftId, tagsJson = JsonSerializer.Serialize(tags, JsonOptions) },
            transaction);
        await ReopenIfPublishFailedAsync(connection, transaction, draftId, ct);

        transaction.Commit();
    }

    /// <summary>Shared by the three metadata setters above: if the draft is PublishFailed, clear
    /// the burned Umbraco attempt weight and flip it back to Approved so the next PublishJob cycle
    /// retries — mirrors tools/2026-08-04-repair-stale-taxonomy.sql steps 3-4 (which zero
    /// Attempts rather than deleting the row, so the error history survives), done in-app for the
    /// single draft the editor just fixed rather than swept for every stale row. A no-op for any
    /// other status.</summary>
    private static async Task ReopenIfPublishFailedAsync(
        System.Data.IDbConnection connection, System.Data.IDbTransaction transaction,
        long draftId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var status = await connection.ExecuteScalarAsync<string?>(
            "SELECT Status FROM dbo.nw_Draft WHERE Id = @draftId", new { draftId }, transaction);
        if (status != nameof(DraftStatus.PublishFailed))
            return;

        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_PublishRecord
            SET Attempts = 0
            WHERE DraftId = @draftId AND Destination = @umbraco AND Status = @failedStatus
            """,
            new { draftId, umbraco = PublishDestinations.Umbraco, failedStatus = "Failed" },
            transaction);

        await connection.ExecuteAsync(
            "UPDATE dbo.nw_Draft SET Status = @approvedStatus WHERE Id = @draftId",
            new { draftId, approvedStatus = nameof(DraftStatus.Approved) },
            transaction);
    }
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Newsroom.Infrastructure/Newsroom.Infrastructure.csproj`
Expected: Build succeeded, 0 errors. (Whole-solution `dotnet build` still fails on `Newsroom.Worker`'s pre-existing Task 2 gap — expected until Task 7.)

- [ ] **Step 4: Commit**

```bash
git add src/Newsroom.Core/Drafting/Interfaces.cs src/Newsroom.Infrastructure/Repositories/DraftRepository.cs
git commit -m "feat(drafting): narrow category/region/tags setters with in-app publish-failure reopen"
```

---

### Task 5: Missing-category warning on the review card

**Files:**
- Modify: `src/Newsroom.Core/Review/ReviewMessageRenderer.cs`
- Test: `src/tests/Newsroom.Core.Tests/Review/ReviewMessageRendererTests.cs`

**Interfaces:**
- Consumes: `DraftReviewView.IsManual`/`.Category` (both already exist — no changes needed to the view record itself).
- Produces: nothing downstream depends on the exact warning text, but Task 7's manual UAT checks for it.

- [ ] **Step 1: Write the failing tests**

Append to `src/tests/Newsroom.Core.Tests/Review/ReviewMessageRendererTests.cs` (the file already has a `View(...)` builder taking `isManual`, `category`, `region`, `tags` — reuse it as-is):

```csharp
    [Fact]
    public void Manual_draft_without_category_shows_the_missing_category_warning()
    {
        var html = ReviewMessageRenderer.RenderHtml(View(isManual: true, category: "", region: null, tags: []));

        Assert.Contains("⚠️ Няма зададена категория", html);
        Assert.DoesNotContain("📎", html);
    }

    [Fact]
    public void Manual_draft_with_category_shows_the_normal_meta_line_not_the_warning()
    {
        var html = ReviewMessageRenderer.RenderHtml(View(isManual: true, category: "Икономика / Бизнес"));

        Assert.DoesNotContain("⚠️", html);
        Assert.Contains("📎 Категория: Икономика / Бизнес", html);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ReviewMessageRendererTests"`
Expected: FAIL — `Manual_draft_without_category_shows_the_missing_category_warning` fails (no warning rendered today; the meta-line block is simply skipped for an all-empty manual draft, matching the pre-existing `Meta_line_is_skipped_when_category_region_and_tags_are_empty` test which uses `isManual: false` and must keep passing unchanged).

- [ ] **Step 3: Add the warning branch**

In `src/Newsroom.Core/Review/ReviewMessageRenderer.cs`, replace:

```csharp
        if (!string.IsNullOrWhiteSpace(v.Category) || !string.IsNullOrWhiteSpace(v.Region) || v.Tags.Count > 0)
        {
            html.Append('\n').Append("📎 Категория: ").Append(Escape(v.Category));
            if (!string.IsNullOrWhiteSpace(v.Region))
                html.Append(" · Регион: ").Append(Escape(v.Region));
            if (v.Tags.Count > 0)
                html.Append(" · Тагове: ").Append(Escape(string.Join(", ", v.Tags)));
            html.Append('\n');
        }
```

with:

```csharp
        if (v.IsManual && string.IsNullOrWhiteSpace(v.Category))
        {
            // /post drafts start with no category — Umbraco requires one to publish. The button
            // picker (docs/superpowers/specs/2026-08-05-post-command-metadata-picker-design.md)
            // is how an editor fills this in; this line is the visual cue that it is still missing.
            html.Append('\n').Append("⚠️ Няма зададена категория").Append('\n');
        }
        else if (!string.IsNullOrWhiteSpace(v.Category) || !string.IsNullOrWhiteSpace(v.Region) || v.Tags.Count > 0)
        {
            html.Append('\n').Append("📎 Категория: ").Append(Escape(v.Category));
            if (!string.IsNullOrWhiteSpace(v.Region))
                html.Append(" · Регион: ").Append(Escape(v.Region));
            if (v.Tags.Count > 0)
                html.Append(" · Тагове: ").Append(Escape(string.Join(", ", v.Tags)));
            html.Append('\n');
        }
```

- [ ] **Step 4: Run the renderer suite and full build**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ReviewMessageRendererTests"`
Expected: PASS — the two new tests plus every pre-existing renderer test unchanged (non-manual empty-metadata drafts still render no meta line at all).

Run: `dotnet build src/Newsroom.Core/Newsroom.Core.csproj`
Expected: Build succeeded, 0 errors. (Whole-solution `dotnet build` still fails on `Newsroom.Worker`'s pre-existing Task 2 gap — expected until Task 7.)

- [ ] **Step 5: Commit**

```bash
git add src/Newsroom.Core/Review/ReviewMessageRenderer.cs src/tests/Newsroom.Core.Tests/Review/ReviewMessageRendererTests.cs
git commit -m "feat(review): missing-category warning on manual-topic cards"
```

---

### Task 6: `ITelegramGateway` — `SendManualCardAsync`/`EditManualCardAsync`, `TelegramGateway`, `SandboxTelegramGateway`, DI wiring

**Files:**
- Modify: `src/Newsroom.Core/Review/Interfaces.cs`
- Modify: `src/Newsroom.Infrastructure/Review/TelegramGateway.cs`
- Modify: `src/Newsroom.Infrastructure/Review/SandboxTelegramGateway.cs`
- Test: `src/tests/Newsroom.Infrastructure.Tests/Review/SandboxTelegramGatewayTests.cs`

**Interfaces:**
- Consumes: `ManualCardKeyboard` (Task 2).
- Produces: `ITelegramGateway.SendManualCardAsync(long chatId, string html, long draftId, ManualCardKeyboard keyboard, string? scheduleButtonLabel, CancellationToken ct)` returning `Task<long>`, and `EditManualCardAsync(long chatId, long messageId, string html, long draftId, ManualCardKeyboard keyboard, string? scheduleButtonLabel, CancellationToken ct)` returning `Task` — Task 7's `TelegramJob` calls both. `TelegramGateway`'s constructor gains two required parameters after `botToken` — Task 7 also updates the one production call site (`Program.cs`), since that file lives in `Newsroom.Worker`, the same project as the rest of Task 7's changes.

**Why two new methods instead of extending `SendHtmlAsync`/`EditHtmlAsync`:** those two are called from 8 files (`TelegramJob`, `PublishJob`, `DailyDigestJob`, `WatchdogJob`, `FacebookTestPostService`, `TelegramOperatorAlerts`, `SandboxTelegramGateway`, and their tests) for plain confirmations and non-manual cards. None of that unrelated call surface should have to learn about manual-picker state.

- [ ] **Step 1: Add the interface methods**

In `src/Newsroom.Core/Review/Interfaces.cs`, add to `ITelegramGateway` after `SendHtmlAsync`'s doc/signature (keeping the "send" methods grouped, before `EditHtmlAsync`):

```csharp
    /// <summary>Posts a manual-topic (/post) review card with a metadata-aware keyboard
    /// (<see cref="ManualCardKeyboard"/>) instead of the fixed ✅/✏️/❌ row — used only for drafts
    /// whose topic is Manual. <paramref name="scheduleButtonLabel"/> behaves as in
    /// <see cref="SendHtmlAsync"/> for Resolved/Expanded (ignored for AwaitingCategory, which has
    /// no ✅/📅 row at all).</summary>
    /// <returns>The Telegram message id.</returns>
    Task<long> SendManualCardAsync(
        long chatId, string html, long draftId, ManualCardKeyboard keyboard,
        string? scheduleButtonLabel, CancellationToken ct);
```

and after `EditHtmlAsync`:

```csharp
    /// <summary>Edits a manual-topic card in place with a metadata-aware keyboard — the category/
    /// region button taps and the 🏷 correction button all re-render through this rather than
    /// <see cref="EditHtmlAsync"/>.</summary>
    Task EditManualCardAsync(
        long chatId, long messageId, string html, long draftId, ManualCardKeyboard keyboard,
        string? scheduleButtonLabel, CancellationToken ct);
```

- [ ] **Step 2: Implement in `TelegramGateway`**

In `src/Newsroom.Infrastructure/Review/TelegramGateway.cs`, change the class declaration:

```csharp
public sealed class TelegramGateway(
    string botToken, IReadOnlyList<string> categories, IReadOnlyList<string> regions) : ITelegramGateway
```

Add the two public methods after `SendHtmlAsync`/`EditHtmlAsync` (or anywhere in the class — placement next to them keeps the "send"/"edit" pairing readable):

```csharp
    public async Task<long> SendManualCardAsync(
        long chatId, string html, long draftId, ManualCardKeyboard keyboard,
        string? scheduleButtonLabel, CancellationToken ct)
    {
        var message = await bot.SendMessage(
            chatId,
            html,
            parseMode: ParseMode.Html,
            replyMarkup: BuildManualKeyboard(draftId, keyboard, scheduleButtonLabel),
            linkPreviewOptions: NoPreview,
            cancellationToken: ct);
        return message.MessageId;
    }

    public async Task EditManualCardAsync(
        long chatId, long messageId, string html, long draftId, ManualCardKeyboard keyboard,
        string? scheduleButtonLabel, CancellationToken ct)
    {
        await bot.EditMessageText(
            chatId,
            (int)messageId,
            html,
            parseMode: ParseMode.Html,
            replyMarkup: BuildManualKeyboard(draftId, keyboard, scheduleButtonLabel),
            linkPreviewOptions: NoPreview,
            cancellationToken: ct);
    }

    /// <summary>AwaitingCategory: category-picker rows + ✏️/❌ (no ✅/📅). Resolved: the normal
    /// ✅/✏️/❌(/📅) row plus 🏷. Expanded: Resolved's rows plus category and region picker rows
    /// appended below.</summary>
    private InlineKeyboardMarkup BuildManualKeyboard(
        long draftId, ManualCardKeyboard keyboard, string? scheduleButtonLabel)
    {
        List<InlineKeyboardButton[]> rows = [];

        if (keyboard == ManualCardKeyboard.AwaitingCategory)
        {
            rows.Add([
                InlineKeyboardButton.WithCallbackData("✏️ Промени", $"changes:{draftId}"),
                InlineKeyboardButton.WithCallbackData("❌ Откажи", $"reject:{draftId}"),
            ]);
        }
        else
        {
            rows.Add([
                InlineKeyboardButton.WithCallbackData("✅ Одобри", $"approve:{draftId}"),
                InlineKeyboardButton.WithCallbackData("✏️ Промени", $"changes:{draftId}"),
                InlineKeyboardButton.WithCallbackData("❌ Откажи", $"reject:{draftId}"),
            ]);
            if (scheduleButtonLabel is not null)
                rows.Add([InlineKeyboardButton.WithCallbackData(scheduleButtonLabel, $"schedule:{draftId}")]);
            rows.Add([InlineKeyboardButton.WithCallbackData("🏷 Категория/Регион/Тагове", $"meta:{draftId}")]);
        }

        if (keyboard is ManualCardKeyboard.AwaitingCategory or ManualCardKeyboard.Expanded)
        {
            rows.AddRange(PickerRows("setcat", draftId, categories));
            if (keyboard == ManualCardKeyboard.Expanded)
                rows.AddRange(PickerRows("setregion", draftId, regions));
        }

        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>Two buttons per row, callback_data "{prefix}:{draftId}:{index}" — index is the
    /// option's position in <paramref name="options"/>, resolved back by
    /// <see cref="Newsroom.Core.Review.ReviewUpdateRouter"/>.</summary>
    private static IEnumerable<InlineKeyboardButton[]> PickerRows(
        string prefix, long draftId, IReadOnlyList<string> options)
    {
        for (var i = 0; i < options.Count; i += 2)
        {
            List<InlineKeyboardButton> row = [InlineKeyboardButton.WithCallbackData(options[i], $"{prefix}:{draftId}:{i}")];
            if (i + 1 < options.Count)
                row.Add(InlineKeyboardButton.WithCallbackData(options[i + 1], $"{prefix}:{draftId}:{i + 1}"));
            yield return row.ToArray();
        }
    }
```

- [ ] **Step 3: Passthrough in `SandboxTelegramGateway`**

In `src/Newsroom.Infrastructure/Review/SandboxTelegramGateway.cs`, add after `EditHtmlAsync`:

```csharp
    public Task<long> SendManualCardAsync(
        long chatId, string html, long draftId, ManualCardKeyboard keyboard,
        string? scheduleButtonLabel, CancellationToken ct) =>
        inner.SendManualCardAsync(chatId, Mark(html, HtmlMarker)!, draftId, keyboard, scheduleButtonLabel, ct);

    public Task EditManualCardAsync(
        long chatId, long messageId, string html, long draftId, ManualCardKeyboard keyboard,
        string? scheduleButtonLabel, CancellationToken ct) =>
        inner.EditManualCardAsync(chatId, messageId, Mark(html, HtmlMarker)!, draftId, keyboard, scheduleButtonLabel, ct);
```

- [ ] **Step 4: Write the failing `SandboxTelegramGateway` tests**

In `src/tests/Newsroom.Infrastructure.Tests/Review/SandboxTelegramGatewayTests.cs`, add to `RecordingGateway` (new fields, alongside the existing ones, plus the two new method implementations):

```csharp
        public long DraftIdArg { get; private set; }
        public ManualCardKeyboard Keyboard { get; private set; }
```

```csharp
        public Task<long> SendManualCardAsync(long chatId, string html, long draftId,
            ManualCardKeyboard keyboard, string? scheduleButtonLabel, CancellationToken ct)
        {
            (ChatId, Html, DraftIdArg, Keyboard, ScheduleLabel) = (chatId, html, draftId, keyboard, scheduleButtonLabel);
            return Task.FromResult(11L);
        }

        public Task EditManualCardAsync(long chatId, long messageId, string html, long draftId,
            ManualCardKeyboard keyboard, string? scheduleButtonLabel, CancellationToken ct)
        {
            (ChatId, MessageId, Html, DraftIdArg, Keyboard, ScheduleLabel) =
                (chatId, messageId, html, draftId, keyboard, scheduleButtonLabel);
            return Task.CompletedTask;
        }
```

Append these test methods to the `SandboxTelegramGatewayTests` class:

```csharp
    [Fact]
    public async Task Sent_manual_card_is_marked_and_every_argument_passes_through()
    {
        var (gateway, inner) = Subject();

        var messageId = await gateway.SendManualCardAsync(
            42, "<b>Заглавие</b>", 5, ManualCardKeyboard.AwaitingCategory, null, CancellationToken.None);

        Assert.Equal(11L, messageId);
        Assert.StartsWith(SandboxTelegramGateway.HtmlMarker, inner.Html);
        Assert.Contains("<b>Заглавие</b>", inner.Html);
        Assert.Equal(42, inner.ChatId);
        Assert.Equal(5, inner.DraftIdArg);
        Assert.Equal(ManualCardKeyboard.AwaitingCategory, inner.Keyboard);
    }

    [Fact]
    public async Task Edited_manual_card_is_marked_and_every_argument_passes_through()
    {
        var (gateway, inner) = Subject();

        await gateway.EditManualCardAsync(
            42, 7, "📎 Категория: Спорт", 5, ManualCardKeyboard.Resolved, "📅 07:30", CancellationToken.None);

        Assert.Equal(42, inner.ChatId);
        Assert.Equal(7, inner.MessageId);
        Assert.StartsWith(SandboxTelegramGateway.HtmlMarker, inner.Html);
        Assert.Equal(5, inner.DraftIdArg);
        Assert.Equal(ManualCardKeyboard.Resolved, inner.Keyboard);
        Assert.Equal("📅 07:30", inner.ScheduleLabel);
    }
```

- [ ] **Step 5: Run tests and build**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SandboxTelegramGatewayTests"`
Expected: PASS (all tests, old and new).

Run: `dotnet build src/Newsroom.Infrastructure/Newsroom.Infrastructure.csproj`
Expected: Build succeeded, 0 errors. `TelegramGateway`'s constructor now requires two extra arguments — `Newsroom.Worker` (the only production caller, in `Program.cs`) will not compile until Task 7 wires them in alongside the rest of that project's changes; that is expected and out of scope for this project-scoped build.

- [ ] **Step 6: Commit**

```bash
git add src/Newsroom.Core/Review/Interfaces.cs src/Newsroom.Infrastructure/Review/TelegramGateway.cs src/Newsroom.Infrastructure/Review/SandboxTelegramGateway.cs src/tests/Newsroom.Infrastructure.Tests/Review/SandboxTelegramGatewayTests.cs
git commit -m "feat(review): manual-card gateway methods with category/region picker keyboards"
```

---

### Task 7: `TelegramJob` — dispatch branching, new command handlers, approve guard, `/help`

**Files:**
- Modify: `src/Newsroom.Worker/Jobs/TelegramJob.cs`
- Modify: `src/Newsroom.Worker/Program.cs`

**Interfaces:**
- Consumes: `ManualCardKeyboard`, `SetDraftCategory`/`SetDraftRegion`/`SetDraftTags`/`ShowMetaPicker` (Task 2); `IReviewRepository.GetPendingTagsConversationAsync`/`SetPendingTagsConversationAsync` (Task 3); `IDraftRepository.SetDraftCategoryAsync`/`SetDraftRegionAsync`/`SetDraftTagsAsync` (Task 4); the missing-category warning (Task 5, no code dependency — renders automatically once `IsManual`/`Category` are what they are); `ITelegramGateway.SendManualCardAsync`/`EditManualCardAsync` (Task 6).
- Produces: end-user behavior; nothing downstream.

This task requires Tasks 2–6 all merged first — it is the only place all five pieces come together, and `dotnet build` will not go green until this task lands.

Build-verify (the job has no unit-test seam; the router, renderer and sandbox-gateway tests carry the tested logic).

- [ ] **Step 1: Branch dispatch between manual and non-manual cards**

In `DispatchPendingAsync`, replace the send call inside the `foreach (var view in pending)` loop:

```csharp
                var html = ReviewMessageRenderer.RenderHtml(view);
                var messageId = await gateway.Value.SendHtmlAsync(
                    options.ReviewChatId, html, withReviewButtons: true, view.DraftId, scheduleLabel, ct);
```

with:

```csharp
                var html = ReviewMessageRenderer.RenderHtml(view);
                var messageId = view.IsManual
                    ? await gateway.Value.SendManualCardAsync(
                        options.ReviewChatId, html, view.DraftId,
                        string.IsNullOrWhiteSpace(view.Category)
                            ? ManualCardKeyboard.AwaitingCategory : ManualCardKeyboard.Resolved,
                        scheduleLabel, ct)
                    : await gateway.Value.SendHtmlAsync(
                        options.ReviewChatId, html, withReviewButtons: true, view.DraftId, scheduleLabel, ct);
```

- [ ] **Step 2: Fix the `RouteCallback` call site and add the approve guard**

In `HandleCallbackAsync`, replace:

```csharp
        var command = ReviewUpdateRouter.RouteCallback(callback, allowedUsers, options.ReviewChatId);
        var editor = callback.UserName ?? callback.UserId.ToString();
        switch (command)
        {
            case ApproveDraft approve:
                // TryApprove: the normal PendingReview → Approved path. TryUnschedule: ✅ on an
                // already-📅-scheduled draft clears the gate — "now" beats the slot by design.
                var transitioned =
                    await reviews.TryApproveAsync(approve.DraftId, callback.UserId, callback.UserName, ct)
                    || await reviews.TryUnscheduleAsync(approve.DraftId, callback.UserId, callback.UserName, ct);
                await ResolveDraftAsync(callback, approve.DraftId, transitioned,
                    toast: "✅ Одобрено", statusLine: $"✅ Одобрено от {editor}", ct);
                break;
```

with:

```csharp
        var draftingOptions = GeminiDraftingOptions.From(configuration);
        var command = ReviewUpdateRouter.RouteCallback(
            callback, allowedUsers, options.ReviewChatId, draftingOptions.Categories, draftingOptions.Regions);
        var editor = callback.UserName ?? callback.UserId.ToString();
        switch (command)
        {
            case ApproveDraft approve:
                var approveView = await reviews.GetReviewViewAsync(approve.DraftId, ct);
                if (approveView is { IsManual: true } && string.IsNullOrWhiteSpace(approveView.Category))
                {
                    // Defense in depth: the AwaitingCategory keyboard has no ✅ button, but
                    // callback_data is not tied to what is currently rendered — a replayed or
                    // crafted press must not approve an unpublishable draft.
                    await gateway.Value.AnswerCallbackAsync(callback.CallbackId, "Първо избери категория", ct);
                    break;
                }
                // TryApprove: the normal PendingReview → Approved path. TryUnschedule: ✅ on an
                // already-📅-scheduled draft clears the gate — "now" beats the slot by design.
                var transitioned =
                    await reviews.TryApproveAsync(approve.DraftId, callback.UserId, callback.UserName, ct)
                    || await reviews.TryUnscheduleAsync(approve.DraftId, callback.UserId, callback.UserName, ct);
                await ResolveDraftAsync(callback, approve.DraftId, transitioned,
                    toast: "✅ Одобрено", statusLine: $"✅ Одобрено от {editor}", ct);
                break;
```

Add `using Newsroom.Infrastructure.Ai;` to the file's usings (for `GeminiDraftingOptions`) if not already present — it is not: `TelegramJob.cs` currently imports `Newsroom.Infrastructure.Images`, `Newsroom.Infrastructure.Publishing`, `Newsroom.Infrastructure.Review` but not `Newsroom.Infrastructure.Ai`.

- [ ] **Step 3: Add the three new callback cases**

In the same `switch (command)` in `HandleCallbackAsync`, insert after the `case ScheduleDraft schedule:` block (before `case Ignore ignore:`):

```csharp
            case SetDraftCategory setCategory:
                await drafts.SetDraftCategoryAsync(setCategory.DraftId, setCategory.Category, ct);
                await RerenderManualCardAsync(callback, setCategory.DraftId, expanded: false, ct);
                await gateway.Value.AnswerCallbackAsync(callback.CallbackId, $"📎 {setCategory.Category}", ct);
                logger.LogInformation("Draft {DraftId}: category set to {Category} by {Editor}",
                    setCategory.DraftId, setCategory.Category, editor);
                break;

            case SetDraftRegion setRegion:
                await drafts.SetDraftRegionAsync(setRegion.DraftId, setRegion.Region, ct);
                await RerenderManualCardAsync(callback, setRegion.DraftId, expanded: false, ct);
                await gateway.Value.AnswerCallbackAsync(callback.CallbackId, $"📍 {setRegion.Region}", ct);
                logger.LogInformation("Draft {DraftId}: region set to {Region} by {Editor}",
                    setRegion.DraftId, setRegion.Region, editor);
                break;

            case ShowMetaPicker meta:
                await reviews.SetPendingTagsConversationAsync(callback.ChatId, callback.UserId, meta.DraftId, ct);
                await RerenderManualCardAsync(callback, meta.DraftId, expanded: true, ct);
                await gateway.Value.AnswerCallbackAsync(
                    callback.CallbackId, "🏷 Избери по-горе или отговори тук с тагове", ct);
                break;
```

Add the shared re-render helper near `EditResolvedAsync`:

```csharp
    /// <summary>Re-renders a manual-topic card in place after a category/region/🏷 action.
    /// AwaitingCategory is picked automatically whenever Category is still empty (the very first
    /// category tap on a fresh /post draft); otherwise <paramref name="expanded"/> chooses between
    /// the collapsed Resolved keyboard and the picker-appended Expanded one (🏷 pressed).</summary>
    private async Task RerenderManualCardAsync(TgCallback callback, long draftId, bool expanded, CancellationToken ct)
    {
        var view = await reviews.GetReviewViewAsync(draftId, ct);
        if (view is null)
            return;

        var keyboard = string.IsNullOrWhiteSpace(view.Category)
            ? ManualCardKeyboard.AwaitingCategory
            : expanded ? ManualCardKeyboard.Expanded : ManualCardKeyboard.Resolved;
        await gateway.Value.EditManualCardAsync(
            callback.ChatId, callback.MessageId, ReviewMessageRenderer.RenderHtml(view), draftId, keyboard,
            await BuildScheduleLabelAsync(ct), ct);
    }
```

- [ ] **Step 4: Wire the pending-tags conversation and the `SetDraftTags` case into `HandleTextAsync`**

In `HandleTextAsync`, replace:

```csharp
        var pendingDraftId = await reviews.GetPendingConversationAsync(text.ChatId, text.UserId, ct);
        var command = ReviewUpdateRouter.RouteText(
            text, allowedUsers, options.ReviewChatId, pendingDraftId, draftIdFromReply);
        switch (command)
        {
            case SubmitChangeInstructions submit:
                await SubmitChangeInstructionsAsync(text, submit, pendingDraftId, ct);
                break;
```

with:

```csharp
        var pendingDraftId = await reviews.GetPendingConversationAsync(text.ChatId, text.UserId, ct);
        var pendingTagsDraftId = await reviews.GetPendingTagsConversationAsync(text.ChatId, text.UserId, ct);
        var command = ReviewUpdateRouter.RouteText(
            text, allowedUsers, options.ReviewChatId, pendingDraftId, draftIdFromReply, pendingTagsDraftId);
        switch (command)
        {
            case SubmitChangeInstructions submit:
                await SubmitChangeInstructionsAsync(text, submit, pendingDraftId, ct);
                break;

            case SetDraftTags setTags:
                await reviews.ClearPendingConversationAsync(text.ChatId, text.UserId, ct);
                await drafts.SetDraftTagsAsync(setTags.DraftId, setTags.Tags, ct);
                await SendTextAsync(text.ChatId, setTags.Tags.Count == 0
                    ? "🏷 Таговете са изчистени."
                    : $"🏷 Тагове: {string.Join(", ", setTags.Tags)}", ct);
                var tagsView = await reviews.GetReviewViewAsync(setTags.DraftId, ct);
                if (tagsView?.TelegramMessageId is { } tagsMessageId)
                    await gateway.Value.EditManualCardAsync(
                        text.ChatId, tagsMessageId, ReviewMessageRenderer.RenderHtml(tagsView), setTags.DraftId,
                        ManualCardKeyboard.Resolved, await BuildScheduleLabelAsync(ct), ct);
                logger.LogInformation("Draft {DraftId}: tags set by {User}",
                    setTags.DraftId, text.UserName ?? text.UserId.ToString());
                break;
```

- [ ] **Step 5: Extend `/help`**

In the `HelpText` constant, replace the interaction-rules paragraph:

```csharp
        "\n" +
        "Върху картичка: ✅ одобри (веднага) · 📅 насрочи за предложения час · ✏️ промени · " +
        "🖼 друга снимка · ❌ откажи. " +
        "Отговор с текст = инструкции за промяна; отговор със снимка = прикачи снимка.";
```

with:

```csharp
        "\n" +
        "Върху картичка: ✅ одобри (веднага) · 📅 насрочи за предложения час · ✏️ промени · " +
        "🖼 друга снимка · ❌ откажи. " +
        "Отговор с текст = инструкции за промяна; отговор със снимка = прикачи снимка.\n" +
        "Редакторска статия (/post) без категория: избери от бутоните под картичката — тя не може " +
        "да се одобри без категория. 🏷 после позволява промяна на категория/регион/тагове по всяко време.";
```

- [ ] **Step 6: Wire `TelegramGateway`'s new constructor parameters in `Program.cs`**

`TelegramGateway` (Task 6) now requires `categories`/`regions` in its constructor; this is the one production call site. In `src/Newsroom.Worker/Program.cs`, replace:

```csharp
    builder.Services.AddSingleton(_ => new Lazy<ITelegramGateway>(() =>
    {
        ITelegramGateway gateway = new TelegramGateway(
            TelegramOptions.From(builder.Configuration).BotToken
                ?? throw new InvalidOperationException("Telegram:BotToken is not configured."));
        // Wrapping the gateway (not the renderer) also marks watchdog alerts and the daily digest.
        return sandbox.Enabled ? new SandboxTelegramGateway(gateway) : gateway;
    }));
```

with:

```csharp
    builder.Services.AddSingleton(_ => new Lazy<ITelegramGateway>(() =>
    {
        var draftingOptions = GeminiDraftingOptions.From(builder.Configuration);
        ITelegramGateway gateway = new TelegramGateway(
            TelegramOptions.From(builder.Configuration).BotToken
                ?? throw new InvalidOperationException("Telegram:BotToken is not configured."),
            draftingOptions.Categories,
            draftingOptions.Regions);
        // Wrapping the gateway (not the renderer) also marks watchdog alerts and the daily digest.
        return sandbox.Enabled ? new SandboxTelegramGateway(gateway) : gateway;
    }));
```

(`using Newsroom.Infrastructure.Ai;` is already present in `Program.cs` — no new using needed.)

- [ ] **Step 7: Build and run the full test suite**

Run: `dotnet build`
Expected: Build succeeded, 0 errors — this is the first point since Task 2 where the whole solution compiles clean.

Run: `dotnet test`
Expected: PASS — everything green across all projects (router, renderer, sandbox-gateway, options tests from every prior task, plus everything pre-existing).

- [ ] **Step 8: Commit**

```bash
git add src/Newsroom.Worker/Jobs/TelegramJob.cs src/Newsroom.Worker/Program.cs
git commit -m "feat(review): /post metadata picker end to end — dispatch, callbacks, tags, approve guard, help"
```

---

### Task 8: Documentation

**Files:**
- Modify: `docs/05-integrations/telegram.md`
- Modify: `docs/11-risks-and-open-questions.md`
- Modify: `docs/decision-log.md`

**Interfaces:** none — docs are the project's source of truth.

- [ ] **Step 1: `docs/05-integrations/telegram.md`**

In the slash-commands table, extend the `/post` row's description (append a sentence) or add a note directly below the table:

```markdown
`/post` drafts start with no Category — Umbraco requires one to publish. The review card shows
category buttons (no ✅ until one is tapped); after that, region buttons and a 🏷 button
(re-opens the category/region pickers, plus a tags text reply) let the editor add the rest, all
without any AI call. Fixing a category on an already-rejected (`PublishFailed`) draft
automatically reopens it for the next publish cycle.
```

- [ ] **Step 2: `docs/11-risks-and-open-questions.md`**

Find the Q-10 row (manual drafts have no Category/SEO fields) and mark it closed, e.g. change its status column to `Closed (2026-08-05)` and add a one-line note: "Resolved by the button-based category/region/tags picker — see docs/superpowers/specs/2026-08-05-post-command-metadata-picker-design.md."

- [ ] **Step 3: `docs/decision-log.md`**

Append a row following the file's existing format:

```markdown
| 2026-08-05 | — | /post drafts get a button-based category/region/tags picker (no AI call); fixing a category on a PublishFailed draft auto-reopens it | Accepted |
```

- [ ] **Step 4: Commit**

```bash
git add docs/05-integrations/telegram.md docs/11-risks-and-open-questions.md docs/decision-log.md
git commit -m "docs: /post metadata picker"
```

---

### Manual UAT (after all tasks; worker running per docs/runbooks/start-the-worker.md or the sandbox)

The repository SQL has no automated harness — verify these flows live:

1. `/post Тестово заглавие` + a body line → confirmation reply, then a review card with the
   `⚠️ Няма зададена категория` warning and category-picker buttons (no ✅, no 📅) plus ✏️/❌.
2. Tap a category button → toast `📎 <category>`, card re-renders: `📎 Категория: <category>` line,
   normal ✅/✏️/❌ row, plus a 🏷 row. Tap ✅ → publishes normally (Umbraco + Facebook).
3. Tap 🏷 on a resolved card → card gains category + region picker rows below the normal buttons;
   tap a region → toast `📍 <region>`, card collapses back to the normal (non-expanded) view with
   the region now shown in the 📎 line.
4. Tap 🏷 again, then reply with plain text `труд, криза, пазар` (not a reply-to, just a message in
   the chat) → confirmation `🏷 Тагове: труд, криза, пазар`, card's 📎 line shows the tags.
5. Craft/replay an `approve:{id}` callback (or use a bot debugging tool) against a still-
   categoryless draft → toast `Първо избери категория`, no status change (guard fires even without
   the button being visible).
6. Take a draft to `PublishFailed` deliberately (e.g. set an invalid category via direct DB edit,
   approve, let `PublishJob` reject it), then tap 🏷 → new category → confirm the draft's status
   flips back to `Approved` in the DB and the next `PublishJob` cycle publishes it successfully,
   with no manual SQL run.
7. `/new` (AI-assisted) still produces a card with a real category from `GeminiDraftingOptions`
   immediately (no picker shown — `DraftValidator` already gates AI output) — confirms non-manual
   and AI-manual-with-category paths are both unaffected.
8. `/help` shows the new paragraph about the category picker.
9. Two editors (or the same editor from two chats, if testable) each open 🏷 on different drafts at
   the same time — confirm each one's tags reply lands on the correct draft (the pending-tags slot
   is per (chat, user), same guarantee as the existing ✏️ conversation).
