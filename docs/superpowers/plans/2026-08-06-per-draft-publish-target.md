# Per-draft publish target — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the editor choose, per draft, whether ✅ approval publishes to the website, to the Facebook page, or to both — replacing the process-wide `Publishing:FacebookOnly` decision with a per-draft `nw_Draft.PublishTarget` column.

**Architecture:** A new `PublishTarget` enum (`Both` | `Website` | `Facebook`) is written to the draft at approval time by two new callback buttons (`approve:{id}:site`, `approve:{id}:fb`); bare `approve:{id}` keeps meaning `Both`. All routing rules — which drafts each publishing leg selects, and what "fully Published" means for a draft — live in one pure static class, `PublishTargets`, which is the only unit-testable seam this feature has (see Global Constraints). `PublishJob` stops computing `requiredDestinations` once at construction and asks `PublishTargets` per draft; the two existing Facebook queries (link post vs. standalone post) stop being selected by a global flag and run every cycle, each filtered by target.

**Tech Stack:** .NET 10, C# 14, Dapper over SQL Server, `Telegram.Bot` 22.10.1, xUnit.

## Global Constraints

- **There is no database integration-test harness in this repo.** `PublishRepositoryTests` and `ReviewRepositoryTests` are pure unit tests over static helpers (`FileNameFromUrl`, `FormatQuotaSummary`). `docs/08-testing.md` lists an "Integration (DB) / local SQL Express" layer, but no such test exists. **Do not attempt to write DB integration tests.** Every rule that could otherwise only be checked against a live database is extracted into `PublishTargets` and unit-tested there; the SQL itself is verified by the manual sandbox run in Task 9.
- **Dapper matches row-record constructor parameters to SELECT columns *positionally*, not by name.** A record whose parameters are merely a permutation of the columns fails to materialise at all, throwing "a parameterless default constructor or one matching signature … is required". This has already broken the Facebook leg in production once (2026-08-04) and `ReviewRepository` once (2026-07-30); the warning is written on `PublishRepository.FacebookRow`. When adding `PublishTarget` to a query, add it as the **last** SELECT column *and* the **last** record parameter.
- Migrations are forward-only, embedded resources, and **must be single batch — no `GO` separators** (`MigrationRunner`). Naming is `NNNN_name.sql`.
- Enum values persist as their **name** (`nvarchar`), matching how `DraftStatus` is stored.
- Bulgarian is the editor-facing language. Button labels and toasts in this plan are exact — copy them verbatim.
- **Commits in this repo carry no `Co-Authored-By: Claude` trailer and no AI-attribution footer.** Conventional-commit style with an optional scope (`fix(publish): …`, `docs: …`).
- Build/test from the repo root: `dotnet test Newsroom.slnx`. The solution file is `.slnx`.
- The live pipeline runs as the Windows Service `PredelNewsroom` on the VPS. **This dev machine runs the sandbox only** — never `dotnet run` a `Development`-environment worker here (it collides with the VPS poller: `409 Conflict: terminated by other getUpdates request`, ADR-0014).

---

### Task 1: `PublishTarget` enum and the `PublishTargets` routing table

The pure core of the feature. Everything else reads its decisions from here, so this is where the rules get tested.

**Files:**
- Create: `src/Newsroom.Core/Publishing/PublishTarget.cs`
- Create: `src/Newsroom.Core/Publishing/PublishTargets.cs`
- Test: `src/tests/Newsroom.Core.Tests/Publishing/PublishTargetsTests.cs`

**Interfaces:**
- Consumes: `PublishDestinations.Umbraco` / `.Facebook` (existing, in `src/Newsroom.Core/Publishing/Interfaces.cs`).
- Produces, all used by Tasks 3–7:
  - `enum PublishTarget { Both, Website, Facebook }`
  - `const string PublishTargets.WebsiteToken = "site"`, `PublishTargets.FacebookToken = "fb"`
  - `bool PublishTargets.TryParseCallbackToken(string token, out PublishTarget target)`
  - `PublishTarget PublishTargets.Parse(string? persisted)`
  - `string PublishTargets.Name(PublishTarget target)`
  - `IReadOnlyList<string> PublishTargets.UmbracoLeg`
  - `IReadOnlyList<string> PublishTargets.FacebookLinkLeg`
  - `IReadOnlyList<string> PublishTargets.FacebookStandaloneLeg(bool facebookOnly)`
  - `IReadOnlyList<string> PublishTargets.RequiredDestinations(PublishTarget target, bool facebookConfigured, bool facebookOnly)`

- [ ] **Step 1: Write the failing tests**

Create `src/tests/Newsroom.Core.Tests/Publishing/PublishTargetsTests.cs`:

```csharp
using Newsroom.Core.Publishing;

namespace Newsroom.Core.Tests.Publishing;

public class PublishTargetsTests
{
    [Theory]
    [InlineData("site", PublishTarget.Website)]
    [InlineData("fb", PublishTarget.Facebook)]
    public void TryParseCallbackToken_resolves_the_two_button_tokens(string token, PublishTarget expected)
    {
        Assert.True(PublishTargets.TryParseCallbackToken(token, out var target));
        Assert.Equal(expected, target);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("SITE")]
    [InlineData("website")]
    [InlineData("")]
    public void TryParseCallbackToken_rejects_anything_else(string token)
    {
        Assert.False(PublishTargets.TryParseCallbackToken(token, out _));
    }

    [Theory]
    [InlineData("Both", PublishTarget.Both)]
    [InlineData("Website", PublishTarget.Website)]
    [InlineData("Facebook", PublishTarget.Facebook)]
    public void Parse_reads_the_persisted_column_value(string persisted, PublishTarget expected)
    {
        Assert.Equal(expected, PublishTargets.Parse(persisted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("website")] // wrong case: persisted values are written with Name(), never lowercased
    public void Parse_falls_back_to_Both_so_a_bad_row_can_never_strand_a_draft(string? persisted)
    {
        Assert.Equal(PublishTarget.Both, PublishTargets.Parse(persisted));
    }

    [Fact]
    public void Umbraco_leg_serves_Both_and_Website_but_never_Facebook()
    {
        Assert.Equal(new[] { "Both", "Website" }, PublishTargets.UmbracoLeg);
    }

    [Fact]
    public void Facebook_link_leg_serves_only_Both()
    {
        Assert.Equal(new[] { "Both" }, PublishTargets.FacebookLinkLeg);
    }

    [Fact]
    public void Facebook_standalone_leg_serves_only_Facebook_normally()
    {
        Assert.Equal(new[] { "Facebook" }, PublishTargets.FacebookStandaloneLeg(facebookOnly: false));
    }

    [Fact]
    public void FacebookOnly_makes_the_standalone_leg_serve_every_target()
    {
        // Without this, a draft approved as Both — or scheduled with 📅, which always writes
        // Both — would wait forever under the flag for a site publish that never runs.
        Assert.Equal(
            new[] { "Both", "Website", "Facebook" },
            PublishTargets.FacebookStandaloneLeg(facebookOnly: true));
    }

    [Fact]
    public void Both_requires_the_site_and_the_page_when_Facebook_is_configured()
    {
        Assert.Equal(
            new[] { "umbraco", "facebook" },
            PublishTargets.RequiredDestinations(
                PublishTarget.Both, facebookConfigured: true, facebookOnly: false));
    }

    [Fact]
    public void Both_requires_only_the_site_when_Facebook_is_not_configured()
    {
        Assert.Equal(
            new[] { "umbraco" },
            PublishTargets.RequiredDestinations(
                PublishTarget.Both, facebookConfigured: false, facebookOnly: false));
    }

    [Fact]
    public void Website_reaches_Published_on_the_site_publish_alone()
    {
        // Not PartiallyPublished: Facebook is not in a website-only draft's required set.
        Assert.Equal(
            new[] { "umbraco" },
            PublishTargets.RequiredDestinations(
                PublishTarget.Website, facebookConfigured: true, facebookOnly: false));
    }

    [Fact]
    public void Facebook_requires_only_the_page()
    {
        Assert.Equal(
            new[] { "facebook" },
            PublishTargets.RequiredDestinations(
                PublishTarget.Facebook, facebookConfigured: true, facebookOnly: false));
    }

    [Theory]
    [InlineData(PublishTarget.Both)]
    [InlineData(PublishTarget.Website)]
    [InlineData(PublishTarget.Facebook)]
    public void FacebookOnly_collapses_every_target_to_the_page(PublishTarget target)
    {
        Assert.Equal(
            new[] { "facebook" },
            PublishTargets.RequiredDestinations(target, facebookConfigured: true, facebookOnly: true));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Newsroom.slnx --filter "FullyQualifiedName~PublishTargetsTests"`
Expected: FAIL — compile errors, `PublishTarget` and `PublishTargets` do not exist.

- [ ] **Step 3: Write the enum**

Create `src/Newsroom.Core/Publishing/PublishTarget.cs`:

```csharp
namespace Newsroom.Core.Publishing;

/// <summary>
/// Where an approved draft goes, chosen per draft by the editor at ✅ time
/// (docs/superpowers/specs/2026-08-06-per-draft-publish-target-design.md). Persisted as its name
/// in nw_Draft.PublishTarget, the same way DraftStatus is stored.
/// </summary>
public enum PublishTarget
{
    /// <summary>The website, then the live link posted to the Facebook page — the flow that
    /// existed before targets, and still what ✅ Одобри and 📅 Насрочи mean.</summary>
    Both,

    /// <summary>The website only; nothing reaches the Facebook page.</summary>
    Website,

    /// <summary>The Facebook page only, as a standalone post (caption + image, no link) — the
    /// article never reaches the site.</summary>
    Facebook,
}
```

- [ ] **Step 4: Write the routing table**

Create `src/Newsroom.Core/Publishing/PublishTargets.cs`:

```csharp
namespace Newsroom.Core.Publishing;

/// <summary>
/// The whole publish-routing table in one pure place: which drafts each publishing leg selects,
/// and what "fully Published" means for a given draft. PublishJob and PublishRepository read it
/// instead of each re-deriving the rules, and because it is pure it is also the only part of the
/// routing that can be unit-tested — the repository queries have no DB test harness
/// (docs/superpowers/specs/2026-08-06-per-draft-publish-target-design.md).
/// </summary>
public static class PublishTargets
{
    /// <summary>Trailing segment of the target buttons' callback data,
    /// "approve:{draftId}:{token}". Bare "approve:{draftId}" carries no token and means
    /// <see cref="PublishTarget.Both"/> — that is what keeps cards posted before this feature,
    /// and the scheduled card's „✅ Одобри веднага" button, working unchanged.</summary>
    public const string WebsiteToken = "site";

    public const string FacebookToken = "fb";

    public static bool TryParseCallbackToken(string token, out PublishTarget target)
    {
        switch (token)
        {
            case WebsiteToken:
                target = PublishTarget.Website;
                return true;
            case FacebookToken:
                target = PublishTarget.Facebook;
                return true;
            default:
                target = PublishTarget.Both;
                return false;
        }
    }

    /// <summary>The value written to nw_Draft.PublishTarget.</summary>
    public static string Name(PublishTarget target) => target.ToString();

    /// <summary>Reads a persisted nw_Draft.PublishTarget value. Anything unrecognised — NULL from
    /// a pre-migration row read by an old query, a hand-edited value — reads as
    /// <see cref="PublishTarget.Both"/>, the pre-feature behaviour, so a bad row degrades to the
    /// old flow rather than stranding a draft in a leg that never selects it.</summary>
    public static PublishTarget Parse(string? persisted) =>
        Enum.TryParse<PublishTarget>(persisted, ignoreCase: false, out var target)
            ? target
            : PublishTarget.Both;

    /// <summary>Targets the Umbraco leg publishes; Facebook-only drafts never touch the site.</summary>
    public static IReadOnlyList<string> UmbracoLeg { get; } =
        [nameof(PublishTarget.Both), nameof(PublishTarget.Website)];

    /// <summary>Targets served by the "link post after the site publish" Facebook leg
    /// (IPublishRepository.GetPendingFacebookAsync) — the unchanged normal pipeline.</summary>
    public static IReadOnlyList<string> FacebookLinkLeg { get; } = [nameof(PublishTarget.Both)];

    /// <summary>Targets served by the standalone (no link) Facebook leg
    /// (IPublishRepository.GetApprovedForFacebookAsync). Under <c>Publishing:FacebookOnly</c> the
    /// flag overrides the column and every draft comes this way — otherwise a draft approved as
    /// Both, or scheduled with 📅 (which always writes Both), would wait forever for a site
    /// publish the flag has disabled.</summary>
    public static IReadOnlyList<string> FacebookStandaloneLeg(bool facebookOnly) => facebookOnly
        ? [nameof(PublishTarget.Both), nameof(PublishTarget.Website), nameof(PublishTarget.Facebook)]
        : [nameof(PublishTarget.Facebook)];

    /// <summary>What must succeed before THIS draft counts as Published — the per-draft
    /// replacement for PublishJob's old process-wide field. Facebook joins a Both draft's set
    /// only when it is configured, so a site-only deployment keeps reaching Published exactly as
    /// it does today.</summary>
    public static IReadOnlyList<string> RequiredDestinations(
        PublishTarget target, bool facebookConfigured, bool facebookOnly)
    {
        if (facebookOnly)
            return [PublishDestinations.Facebook];

        return target switch
        {
            PublishTarget.Website => [PublishDestinations.Umbraco],
            PublishTarget.Facebook => [PublishDestinations.Facebook],
            _ => facebookConfigured
                ? [PublishDestinations.Umbraco, PublishDestinations.Facebook]
                : [PublishDestinations.Umbraco],
        };
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Newsroom.slnx --filter "FullyQualifiedName~PublishTargetsTests"`
Expected: PASS, 24 tests (the `[Theory]` rows count individually).

- [ ] **Step 6: Commit**

```bash
git add src/Newsroom.Core/Publishing/PublishTarget.cs src/Newsroom.Core/Publishing/PublishTargets.cs src/tests/Newsroom.Core.Tests/Publishing/PublishTargetsTests.cs
git commit -m "feat(publish): PublishTarget enum and the publish-routing table"
```

---

### Task 2: Migration 0016 — the `PublishTarget` column

**Files:**
- Create: `src/Newsroom.Infrastructure/Database/Migrations/0016_publish_target.sql`
- Test: `src/tests/Newsroom.Infrastructure.Tests/Database/EmbeddedMigrationsTests.cs` (existing — it loads every embedded script and asserts the conventions; no edit needed)

**Interfaces:**
- Consumes: nothing.
- Produces: `nw_Draft.PublishTarget nvarchar(20) NOT NULL DEFAULT 'Both'`, read by Task 5's queries.

`src/Newsroom.Infrastructure/Newsroom.Infrastructure.csproj` already embeds the whole folder (`<EmbeddedResource Include="Database\Migrations\*.sql" />`), so the new file needs no csproj edit.

- [ ] **Step 1: Write the migration**

Create `src/Newsroom.Infrastructure/Database/Migrations/0016_publish_target.sql`:

```sql
-- 0016_publish_target: per-draft publish destination
-- (docs/superpowers/specs/2026-08-06-per-draft-publish-target-design.md).
-- 'Both' (website, then the link to Facebook) | 'Website' | 'Facebook', written at ✅ time by the
-- editor's chosen button. NOT NULL with a DEFAULT so every existing row — including drafts
-- sitting Approved or PartiallyPublished mid-deploy — backfills to the pre-feature behaviour and
-- nothing in flight changes meaning. Single batch, no GO.

ALTER TABLE dbo.nw_Draft ADD PublishTarget nvarchar(20) NOT NULL
    CONSTRAINT DF_nw_Draft_PublishTarget DEFAULT 'Both';
```

- [ ] **Step 2: Run the migration-convention tests**

Run: `dotnet test Newsroom.slnx --filter "FullyQualifiedName~EmbeddedMigrationsTests"`
Expected: PASS. These load the real embedded set, so they now cover 0016 — they fail if the version duplicates an existing one, the file name is off-convention, or the script contains a `GO` separator.

- [ ] **Step 3: Commit**

```bash
git add src/Newsroom.Infrastructure/Database/Migrations/0016_publish_target.sql
git commit -m "feat(publish): add nw_Draft.PublishTarget (migration 0016)"
```

---

### Task 3: Route `approve:{draftId}:{target}`

**Files:**
- Modify: `src/Newsroom.Core/Review/ReviewCommand.cs:10` (the `ApproveDraft` record)
- Modify: `src/Newsroom.Core/Review/ReviewUpdateRouter.cs:35-48` (the callback switch)
- Test: `src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs`

**Interfaces:**
- Consumes: `PublishTarget`, `PublishTargets.TryParseCallbackToken` (Task 1).
- Produces: `ApproveDraft(long DraftId, PublishTarget Target = PublishTarget.Both)` — consumed by Task 4 (`TelegramJob`'s approve case).

- [ ] **Step 1: Write the failing tests**

Add to `src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs`. Add `using Newsroom.Core.Publishing;` at the top of the file if it is not already there.

```csharp
    [Fact]
    public void Approve_without_a_target_token_means_both_destinations()
    {
        // Cards posted before this feature, and the scheduled card's „✅ Одобри веднага" button,
        // both emit the bare two-segment form — it must keep its original meaning.
        Assert.Equal(new ApproveDraft(42, PublishTarget.Both), RouteCallback(Callback("approve:42")));
    }

    [Fact]
    public void Approve_with_a_target_token_routes_to_that_target()
    {
        Assert.Equal(
            new ApproveDraft(42, PublishTarget.Website), RouteCallback(Callback("approve:42:site")));
        Assert.Equal(
            new ApproveDraft(42, PublishTarget.Facebook), RouteCallback(Callback("approve:42:fb")));
    }

    [Theory]
    [InlineData("approve:42:both")]
    [InlineData("approve:42:")]
    [InlineData("approve:42:xyz")]
    [InlineData("approve:42:site:extra")]
    public void Approve_with_an_unusable_target_token_is_ignored(string data)
    {
        // A crafted or corrupted callback must never construct a target outside the enum.
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonUnknownData), RouteCallback(Callback(data)));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Newsroom.slnx --filter "FullyQualifiedName~ReviewUpdateRouterTests"`
Expected: FAIL — compile error, `ApproveDraft` takes one argument.

- [ ] **Step 3: Add the target to `ApproveDraft`**

In `src/Newsroom.Core/Review/ReviewCommand.cs`, add `using Newsroom.Core.Publishing;` above the namespace declaration, then replace line 10:

```csharp
/// <summary>✅/🌐/📘 pressed: approve the draft for a specific destination set. A bare
/// "approve:{draftId}" callback (no target token) is <see cref="PublishTarget.Both"/> — the
/// pre-feature meaning, kept so cards already in the chat and the scheduled card's
/// „✅ Одобри веднага" button keep working across a deploy.</summary>
public sealed record ApproveDraft(long DraftId, PublishTarget Target = PublishTarget.Both) : ReviewCommand;
```

- [ ] **Step 4: Route the three-segment form**

In `src/Newsroom.Core/Review/ReviewUpdateRouter.cs`, add `using Newsroom.Core.Publishing;` below the existing `using System.Globalization;`, then replace the `("approve", 2)` arm of the switch at line 37 with these two arms (leave every other arm untouched):

```csharp
            ("approve", 2) => new ApproveDraft(draftId),
            ("approve", 3) when PublishTargets.TryParseCallbackToken(segments[2], out var target) =>
                new ApproveDraft(draftId, target),
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Newsroom.slnx --filter "FullyQualifiedName~ReviewUpdateRouterTests"`
Expected: PASS — including the pre-existing `Callback_approve_reject_changes_route_to_typed_commands`, which compares against `new ApproveDraft(42)` and still holds because the default is `Both`.

- [ ] **Step 6: Commit**

```bash
git add src/Newsroom.Core/Review/ReviewCommand.cs src/Newsroom.Core/Review/ReviewUpdateRouter.cs src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs
git commit -m "feat(review): route approve:{id}:{target} callbacks"
```

---

### Task 4: Persist the chosen target on approval

`TryApproveAsync` stops sharing `TryResolveAsync` with reject — it needs a second column in the same guarded UPDATE. `TryResolveAsync` stays for `TryRejectAsync`.

This task also updates `TelegramJob`'s call site, so the solution compiles at the commit.

**Files:**
- Modify: `src/Newsroom.Core/Review/Interfaces.cs:148-149` (`TryApproveAsync` signature)
- Modify: `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs:338-339` (`TryApproveAsync`) and `:386-415` (`TryScheduleAsync`)
- Modify: `src/Newsroom.Worker/Jobs/TelegramJob.cs:297-299` (the approve call site)

**Interfaces:**
- Consumes: `PublishTarget`, `PublishTargets.Name` (Task 1); `ApproveDraft.Target` (Task 3).
- Produces: `Task<bool> IReviewRepository.TryApproveAsync(long draftId, PublishTarget target, long userId, string? userName, CancellationToken ct)` — consumed by Task 7.

No unit test: this is SQL against a database the test suite cannot reach (see Global Constraints). The behaviour is verified by the sandbox run in Task 9. The build itself is the gate here — the signature change breaks every stale call site.

- [ ] **Step 1: Change the interface**

In `src/Newsroom.Core/Review/Interfaces.cs`, add `using Newsroom.Core.Publishing;` above the namespace declaration, then replace lines 148-149:

```csharp
    /// <summary>PendingReview → Approved, recording the editor's chosen destination set in
    /// nw_Draft.PublishTarget in the same statement — a draft can never be Approved carrying a
    /// stale target. False when the draft is not PendingReview.</summary>
    Task<bool> TryApproveAsync(
        long draftId, PublishTarget target, long userId, string? userName, CancellationToken ct);
```

- [ ] **Step 2: Implement it in the repository**

In `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs`, ensure `using Newsroom.Core.Publishing;` is present, then replace the two-line `TryApproveAsync` at lines 338-339 with:

```csharp
    public async Task<bool> TryApproveAsync(
        long draftId, PublishTarget target, long userId, string? userName, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        // Status and PublishTarget move together: one statement, so the publish queries can never
        // see an Approved draft carrying the previous target. The PendingReview guard is the same
        // idempotency contract as TryResolveAsync — double-taps still return false.
        var rows = await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET Status = @approvedStatus, PublishTarget = @target, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @draftId AND Status = @pendingStatus
            """,
            new
            {
                draftId,
                target = PublishTargets.Name(target),
                approvedStatus = nameof(DraftStatus.Approved),
                pendingStatus = nameof(DraftStatus.PendingReview),
            },
            transaction);
        if (rows == 0)
            return false; // not PendingReview (double-tap or stale button); transaction rolls back

        await InsertReviewActionAsync(connection, transaction, draftId, userId, userName,
            "Approved", PublishTargets.Name(target));

        transaction.Commit();
        return true;
    }
```

- [ ] **Step 3: Make 📅 write its target explicitly**

In the same file, in `TryScheduleAsync` (line 386), replace the UPDATE and its parameter object:

```csharp
        var rows = await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET Status = @approvedStatus, ScheduledForUtc = @scheduledForUtc,
                PublishTarget = @target, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @draftId AND Status = @pendingStatus
            """,
            new
            {
                draftId,
                scheduledForUtc,
                // 📅 is defined as the both-destinations path; stating it beats leaning on the
                // column default, and it re-asserts Both if the row somehow carried anything else.
                target = PublishTargets.Name(PublishTarget.Both),
                approvedStatus = nameof(DraftStatus.Approved),
                pendingStatus = nameof(DraftStatus.PendingReview),
            },
            transaction);
```

Leave `TryUnscheduleAsync` untouched — „✅ Одобри веднага" changes *when*, not *where*, and the draft is already `Both`.

- [ ] **Step 4: Update the call site**

In `src/Newsroom.Worker/Jobs/TelegramJob.cs`, in the `ApproveDraft` case (lines 297-299), pass the command's target:

```csharp
                var transitioned =
                    await reviews.TryApproveAsync(approve.DraftId, approve.Target, callback.UserId, callback.UserName, ct)
                    || await reviews.TryUnscheduleAsync(approve.DraftId, callback.UserId, callback.UserName, ct);
```

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet test Newsroom.slnx`
Expected: build succeeds, all tests PASS. A build error naming `TryApproveAsync` means a call site was missed — search for it and pass the target.

- [ ] **Step 6: Commit**

```bash
git add src/Newsroom.Core/Review/Interfaces.cs src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs src/Newsroom.Worker/Jobs/TelegramJob.cs
git commit -m "feat(review): persist the chosen publish target on approval"
```

---

### Task 5: Publishing honours the draft's target

The repository queries and `PublishJob` change together — the signatures and the caller are one deliverable, and splitting them would leave the build broken at a commit.

**Files:**
- Modify: `src/Newsroom.Core/Publishing/ArticleToPublish.cs:12-23`
- Modify: `src/Newsroom.Core/Publishing/FacebookPost.cs:11-16`
- Modify: `src/Newsroom.Core/Publishing/Interfaces.cs:64-87` (the three query signatures)
- Modify: `src/Newsroom.Infrastructure/Repositories/PublishRepository.cs:40-195` (queries, row records, mappers)
- Modify: `src/Newsroom.Worker/Jobs/PublishJob.cs:46-50, 99-108, 148-156, 232-281`

**Interfaces:**
- Consumes: `PublishTarget`, `PublishTargets.Parse`, `.UmbracoLeg`, `.FacebookLinkLeg`, `.FacebookStandaloneLeg`, `.RequiredDestinations` (Task 1); the `nw_Draft.PublishTarget` column (Task 2).
- Produces: `ArticleToPublish.Target`, `FacebookPost.Target`; the three query signatures below.

⚠️ **Re-read the Dapper positional-matching constraint at the top of this plan before editing `PublishRepository`.** The new column goes last in the SELECT list *and* last in the row record.

Note the column is selected as `d.PublishTarget AS PublishTargetName` and the row-record parameter is named `PublishTargetName`, not `PublishTarget` — a record parameter named `PublishTarget` would shadow the *type* `PublishTarget` inside that record's declaration, which compiles but reads as a trap for the next editor.

- [ ] **Step 1: Carry the target on the two publish DTOs**

In `src/Newsroom.Core/Publishing/ArticleToPublish.cs`, add a trailing parameter to the record (defaulted, so existing positional construction sites and tests keep compiling):

```csharp
public sealed record ArticleToPublish(
    long DraftId,
    Guid PublishRef,
    string Headline,
    string? Subtitle,
    string BodyMarkdown,
    string Category,
    string? Region,
    IReadOnlyList<string> Tags,
    string? SeoTitle,
    string? SeoDescription,
    PublishImage? Image,
    /// <summary>The draft's own destination set, chosen by the editor at ✅ time — PublishJob
    /// asks PublishTargets.RequiredDestinations with it instead of a process-wide field.</summary>
    PublishTarget Target = PublishTarget.Both);
```

In `src/Newsroom.Core/Publishing/FacebookPost.cs`, add the same trailing parameter after `Image`:

```csharp
public sealed record FacebookPost(
    long DraftId,
    string Headline,
    string Teaser,
    string ArticleUrl,
    FacebookImage? Image = null,
    PublishTarget Target = PublishTarget.Both)
{
```

- [ ] **Step 2: Change the three query signatures**

In `src/Newsroom.Core/Publishing/Interfaces.cs`, each of the three queries takes the targets it accepts. Replace their signatures (keep the existing doc comments, appending the noted sentence):

```csharp
    /// <summary>… (existing text) …
    /// <para><paramref name="targets"/> filters on nw_Draft.PublishTarget — the caller passes
    /// <see cref="PublishTargets.UmbracoLeg"/>.</para></summary>
    Task<IReadOnlyList<ArticleToPublish>> GetApprovedUnpublishedAsync(
        string destination, IReadOnlyList<string> targets, int maxAttempts, int maxCount,
        CancellationToken ct);

    /// <summary>… (existing text) …
    /// <para><paramref name="targets"/> is <see cref="PublishTargets.FacebookLinkLeg"/>.</para></summary>
    Task<IReadOnlyList<FacebookPost>> GetPendingFacebookAsync(
        IReadOnlyList<string> targets, int maxAttempts, int maxCount, CancellationToken ct);

    /// <summary>… (existing text) …
    /// <para><paramref name="targets"/> is <see cref="PublishTargets.FacebookStandaloneLeg"/>,
    /// which widens to every target under Publishing:FacebookOnly.</para></summary>
    Task<IReadOnlyList<FacebookPost>> GetApprovedForFacebookAsync(
        IReadOnlyList<string> targets, int maxAttempts, int maxCount, CancellationToken ct);
```

- [ ] **Step 3: Filter the three queries**

In `src/Newsroom.Infrastructure/Repositories/PublishRepository.cs`:

**a.** `GetApprovedUnpublishedAsync` (line 40) — new parameter, new last SELECT column, new predicate, new anonymous-object member:

```csharp
    public async Task<IReadOnlyList<ArticleToPublish>> GetApprovedUnpublishedAsync(
        string destination, IReadOnlyList<string> targets, int maxAttempts, int maxCount,
        CancellationToken ct)
```

Append `d.PublishTarget AS PublishTargetName` as the **last** column of the SELECT list (after `img.Attribution AS ImageAttribution`), add the predicate immediately before `ORDER BY d.Id`:

```sql
              AND d.PublishTarget IN @targets
```

and add `targets,` to the anonymous parameter object.

**b.** `GetPendingFacebookAsync` (line 83) — same three edits: the `targets` parameter, `d.PublishTarget AS PublishTargetName` as the last SELECT column (after `site.ExternalUrl AS ArticleUrl`), the `AND d.PublishTarget IN @targets` predicate before `ORDER BY d.Id`, and `targets,` in the parameter object.

**c.** `GetApprovedForFacebookAsync` (line 135) — same three edits: the `targets` parameter, `d.PublishTarget AS PublishTargetName` as the last SELECT column (after `img.Url AS ImageUrl`), the predicate, and `targets,` in the parameter object.

- [ ] **Step 4: Extend the row records and mappers**

In the same file, add `string PublishTargetName` as the **last** parameter of all three row records:

```csharp
    private sealed record FacebookRow(
        long DraftId,
        string Headline,
        string? SeoDescription,
        string? FacebookCaption,
        string? FacebookHashtagsJson,
        string BodyMarkdown,
        string ArticleUrl,
        string PublishTargetName);

    private sealed record FacebookApprovedRow(
        long DraftId,
        string Headline,
        string BodyMarkdown,
        string? PromptVersion,
        string? FacebookCaption,
        string? FacebookHashtagsJson,
        string? ImageKind,
        string? ImageUrl,
        string PublishTargetName);

    private sealed record PublishRow(
        long DraftId,
        Guid PublishRef,
        string Headline,
        string? Subtitle,
        string BodyMarkdown,
        string Category,
        string? Region,
        string? TagsJson,
        string? SeoTitle,
        string? SeoDescription,
        string? DraftAltTextBg,
        string? ImageKind,
        string? ImageUrl,
        string? ImageAltTextBg,
        string? ImageAttribution,
        string PublishTargetName);
```

Then set `Target` in each mapper. In `ToArticle` (the `PublishRow` → `ArticleToPublish` mapper), pass `Target: PublishTargets.Parse(r.PublishTargetName)` as a named argument on the `ArticleToPublish` construction. In the `Select(...)` projections of `GetPendingFacebookAsync` and `GetApprovedForFacebookAsync`, add `Target: PublishTargets.Parse(r.PublishTargetName)` as a named argument to every `new FacebookPost(...)` in those two methods (each has more than one branch — caption vs. legacy composition; **every** branch needs it).

Leave `GetFacebookPostForDraftAsync` (the `Facebook:TestPostDraftId` hook) alone — it constructs a `FacebookPost` that is never routed by target, and the default `Both` is fine.

- [ ] **Step 5: Make PublishJob ask per draft**

In `src/Newsroom.Worker/Jobs/PublishJob.cs`:

**a.** Delete the `requiredDestinations` field (lines 43-50) entirely, including its doc comment.

**b.** In `RunUmbracoLegAsync`, pass the leg's targets:

```csharp
            articles = await publishes.GetApprovedUnpublishedAsync(
                PublishDestinations.Umbraco, PublishTargets.UmbracoLeg, options.MaxAttempts,
                MaxPerCycle, ct);
```

**c.** In `PublishOneAsync`, compute the required set from the draft:

```csharp
        await publishes.RecordSuccessAsync(
            article.DraftId, PublishDestinations.Umbraco, result.ContentKey.ToString(), result.Url,
            PublishTargets.RequiredDestinations(
                article.Target, facebookOptions.IsConfigured, publishing.FacebookOnly),
            ct);
```

**d.** Replace the query block at the top of `RunFacebookLegAsync` (lines 237-241) so both shapes run each cycle:

```csharp
        IReadOnlyList<FacebookPost> posts;
        try
        {
            // Both Facebook shapes run every cycle now; the draft's own target decides which
            // query selects it. Link posts (site already live) come first — the normal pipeline.
            // Under Publishing:FacebookOnly nothing ever reaches PartiallyPublished, so the link
            // query has nothing to find and is skipped outright.
            IReadOnlyList<FacebookPost> linkPosts = [];
            if (!publishing.FacebookOnly)
                linkPosts = await publishes.GetPendingFacebookAsync(
                    PublishTargets.FacebookLinkLeg, facebookOptions.MaxAttempts, MaxPerCycle, ct);

            var standalonePosts = await publishes.GetApprovedForFacebookAsync(
                PublishTargets.FacebookStandaloneLeg(publishing.FacebookOnly),
                facebookOptions.MaxAttempts, MaxPerCycle, ct);

            posts = [.. linkPosts, .. standalonePosts];
        }
```

Note this raises the per-cycle Facebook ceiling from `MaxPerCycle` to `2 × MaxPerCycle` (3 → 6) when both shapes have work waiting. That is intentional: the two shapes are independent queues and neither should starve the other.

**e.** In `PostOneToFacebookAsync`, same per-draft required set:

```csharp
        await publishes.RecordSuccessAsync(
            post.DraftId, PublishDestinations.Facebook, result.PostId, result.PermalinkUrl,
            PublishTargets.RequiredDestinations(
                post.Target, facebookOptions.IsConfigured, publishing.FacebookOnly),
            ct);
```

**Leave the rest of the file alone.** Specifically:

- `ExecuteAsync`'s configuration guards (lines 55-97) — the `FacebookOnly`-without-Facebook refusal, the Umbraco-not-configured dormancy, the dry-run warnings — are unchanged.
- `RunCycleAsync`'s `if (!publishing.FacebookOnly) await RunUmbracoLegAsync(ct);` is unchanged.
- `HandleUmbracoFailureAsync` / `HandleFacebookFailureAsync` are unchanged: `RecordFailureAsync` already demotes only `Approved` drafts to `PublishFailed` and leaves `PartiallyPublished` alone, which is correct for every target.
- `OfferManualRepairCardAsync` is unchanged — it fires only on the Umbraco leg, which a Facebook-only draft never enters.
- **The notification methods need no edit.** `NotifyPublishedAsync` runs from the Umbraco leg, so a `Website` draft gets the 🚀 confirmation *with* the 📋 group-share block, as the spec requires; `NotifyFacebookPostedAsync` runs from the Facebook leg for both post shapes. The spec's notification table falls out of the existing wiring.

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet test Newsroom.slnx`
Expected: build succeeds, all tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Newsroom.Core/Publishing src/Newsroom.Infrastructure/Repositories/PublishRepository.cs src/Newsroom.Worker/Jobs/PublishJob.cs
git commit -m "feat(publish): route each draft by its own publish target"
```

---

### Task 6: The two new buttons

**Files:**
- Modify: `src/Newsroom.Infrastructure/Review/TelegramGateway.cs:16-17` (ctor), `:77-95` (`SendHtmlAsync`), `:153-188` (`BuildManualKeyboard`)
- Modify: `src/Newsroom.Worker/Program.cs:165-175` (gateway construction)

**Interfaces:**
- Consumes: `PublishTargets.WebsiteToken` / `.FacebookToken` (Task 1); the router's three-segment form (Task 3).
- Produces: `TelegramGateway(string botToken, IReadOnlyList<string> categories, IReadOnlyList<string> regions, bool websiteEnabled)`.

`TelegramGateway` is deliberately logic-free and untested (its own class comment says so) — verification is the sandbox run in Task 9.

- [ ] **Step 1: Add the `websiteEnabled` constructor parameter**

In `src/Newsroom.Infrastructure/Review/TelegramGateway.cs`, add `using Newsroom.Core.Publishing;` next to the existing `using Newsroom.Core.Review;`, then extend the primary constructor:

```csharp
public sealed class TelegramGateway(
    string botToken, IReadOnlyList<string> categories, IReadOnlyList<string> regions,
    bool websiteEnabled) : ITelegramGateway
```

- [ ] **Step 2: Add the shared target-row helper**

In the same file, next to `PickerRows` (around line 193), add:

```csharp
    /// <summary>The per-draft target row: 🌐 publishes to the site only, 📘 to the page only.
    /// The ✅ button above them keeps meaning "site, then the link to Facebook". 🌐 is omitted
    /// while Publishing:FacebookOnly is on — it is the one button that would promise something
    /// the worker will not do (docs/superpowers/specs/2026-08-06-per-draft-publish-target-design.md).</summary>
    private InlineKeyboardButton[] TargetRow(long draftId)
    {
        List<InlineKeyboardButton> row = [];
        if (websiteEnabled)
            row.Add(InlineKeyboardButton.WithCallbackData(
                "🌐 Само сайт", $"approve:{draftId}:{PublishTargets.WebsiteToken}"));
        row.Add(InlineKeyboardButton.WithCallbackData(
            "📘 Само ФБ", $"approve:{draftId}:{PublishTargets.FacebookToken}"));
        return row.ToArray();
    }
```

- [ ] **Step 3: Add the row to the normal review card**

In `SendHtmlAsync`, insert the target row between the main row and the schedule row (i.e. immediately after the `rows` list initialiser at line 91 and before the `if (scheduleButtonLabel is not null)` check):

```csharp
            rows.Add(TargetRow(draftId));
            if (scheduleButtonLabel is not null)
                rows.Add([InlineKeyboardButton.WithCallbackData(scheduleButtonLabel, $"schedule:{draftId}")]);
```

- [ ] **Step 4: Add the buttons to the manual card**

In `BuildManualKeyboard`, the `AwaitingCategory` branch gets 📘 only — a Facebook-only post never calls Umbraco, so the category gate does not apply to it:

```csharp
        if (keyboard == ManualCardKeyboard.AwaitingCategory)
        {
            rows.Add([
                InlineKeyboardButton.WithCallbackData("✏️ Промени", $"changes:{draftId}"),
                InlineKeyboardButton.WithCallbackData("❌ Откажи", $"reject:{draftId}"),
            ]);
            // No ✅ and no 🌐 — those publish to the site, which rejects a draft with no
            // category. 📘 has no such requirement, so a quick page post stays one tap away.
            rows.Add([InlineKeyboardButton.WithCallbackData(
                "📘 Само ФБ", $"approve:{draftId}:{PublishTargets.FacebookToken}")]);
        }
        else
        {
            rows.Add([
                InlineKeyboardButton.WithCallbackData("✅ Одобри", $"approve:{draftId}"),
                InlineKeyboardButton.WithCallbackData("✏️ Промени", $"changes:{draftId}"),
                InlineKeyboardButton.WithCallbackData("❌ Откажи", $"reject:{draftId}"),
            ]);
            rows.Add(TargetRow(draftId));
            if (scheduleButtonLabel is not null)
                rows.Add([InlineKeyboardButton.WithCallbackData(scheduleButtonLabel, $"schedule:{draftId}")]);
            rows.Add([InlineKeyboardButton.WithCallbackData("🏷 Категория/Регион/Тагове", $"meta:{draftId}")]);
        }
```

Also update the method's doc comment (line 153) to mention the target row.

- [ ] **Step 5: Wire it in Program.cs**

In `src/Newsroom.Worker/Program.cs`, inside the `Lazy<ITelegramGateway>` factory (line 165), compute the flag and pass it:

```csharp
    builder.Services.AddSingleton(_ => new Lazy<ITelegramGateway>(() =>
    {
        var draftingOptions = GeminiDraftingOptions.From(builder.Configuration);
        // Mirrors the publishing wiring below: the sandbox forces FacebookOnly off (ADR-0014),
        // so the site button is always live there. Hiding 🌐 is the only card change the flag
        // makes — ✅ and 📅 keep meaning "publish everywhere possible".
        var websiteEnabled = sandbox.Enabled
            || !PublishingOptions.From(builder.Configuration).FacebookOnly;
        ITelegramGateway gateway = new TelegramGateway(
            TelegramOptions.From(builder.Configuration).BotToken
                ?? throw new InvalidOperationException("Telegram:BotToken is not configured."),
            draftingOptions.Categories,
            draftingOptions.Regions,
            websiteEnabled);
        // Wrapping the gateway (not the renderer) also marks watchdog alerts and the daily digest.
        return sandbox.Enabled ? new SandboxTelegramGateway(gateway) : gateway;
    }));
```

Add `using Newsroom.Infrastructure.Publishing;` to `Program.cs` if `PublishingOptions` is not already in scope there.

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet test Newsroom.slnx`
Expected: build succeeds, all tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Newsroom.Infrastructure/Review/TelegramGateway.cs src/Newsroom.Worker/Program.cs
git commit -m "feat(review): 🌐 Само сайт and 📘 Само ФБ buttons on review cards"
```

---

### Task 7: Target-aware approval handling

**Files:**
- Modify: `src/Newsroom.Worker/Jobs/TelegramJob.cs:285-302` (the `ApproveDraft` case) and a new private helper near `ResolveDraftAsync` (line 369)

**Interfaces:**
- Consumes: `ApproveDraft.Target` (Task 3), `IReviewRepository.TryApproveAsync` (Task 4), `PublishTarget` (Task 1).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Narrow the category guard and name the target**

In `src/Newsroom.Worker/Jobs/TelegramJob.cs`, add `using Newsroom.Core.Publishing;` if absent, then replace the whole `case ApproveDraft approve:` block (lines 285-302):

```csharp
            case ApproveDraft approve:
                var approveView = await reviews.GetReviewViewAsync(approve.DraftId, ct);
                // The category gate exists only because Umbraco rejects a publish without one, so
                // it applies to every target EXCEPT Facebook-only, which never calls the site.
                // Still defense in depth: callback_data is not tied to what is currently
                // rendered — a replayed or crafted press must not approve an unpublishable draft.
                if (approve.Target is not PublishTarget.Facebook
                    && approveView is { IsManual: true } && string.IsNullOrWhiteSpace(approveView.Category))
                {
                    await gateway.Value.AnswerCallbackAsync(callback.CallbackId, "Първо избери категория", ct);
                    break;
                }

                // TryApprove: the normal PendingReview → Approved path, now carrying the target.
                // TryUnschedule: ✅ on an already-📅-scheduled draft clears the gate — "now" beats
                // the slot by design, and that draft is already Both.
                var transitioned =
                    await reviews.TryApproveAsync(approve.DraftId, approve.Target, callback.UserId, callback.UserName, ct)
                    || await reviews.TryUnscheduleAsync(approve.DraftId, callback.UserId, callback.UserName, ct);
                var targetSuffix = TargetSuffix(approve.Target);
                await ResolveDraftAsync(callback, approve.DraftId, transitioned,
                    toast: $"✅ Одобрено{targetSuffix}", statusLine: $"✅ Одобрено{targetSuffix} от {editor}", ct);
                break;
```

- [ ] **Step 2: Add the suffix helper**

In the same file, immediately above `ResolveDraftAsync` (line 369):

```csharp
    /// <summary>Names the chosen destination on the resolved card and its toast. Both — what ✅
    /// has always meant — stays unmarked, so the common case reads exactly as it did before
    /// targets existed.</summary>
    private static string TargetSuffix(PublishTarget target) => target switch
    {
        PublishTarget.Website => " (само сайт)",
        PublishTarget.Facebook => " (само ФБ)",
        _ => "",
    };
```

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet test Newsroom.slnx`
Expected: build succeeds, all tests PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Newsroom.Worker/Jobs/TelegramJob.cs
git commit -m "feat(review): approve per target, with the target named on the resolved card"
```

---

### Task 8: Documentation

**Files:**
- Modify: `docs/05-integrations/telegram.md` (review message format ~line 44, card actions table ~line 112)
- Modify: `docs/decision-log.md` (append a dated row)

**Interfaces:** none.

- [ ] **Step 1: Update the review message format**

In `docs/05-integrations/telegram.md`, replace the keyboard bullets under "Review message format" (the `+ inline keyboard:` and `+ second keyboard row:` lines):

```markdown
+ inline keyboard: ✅ Одобри · ✏️ Промени · ❌ Откажи
+ target row: 🌐 Само сайт · 📘 Само ФБ — the per-draft publish target
  (docs/superpowers/specs/2026-08-06-per-draft-publish-target-design.md). ✅ above them means
  both destinations, the pre-existing flow. 🌐 is omitted while `Publishing:FacebookOnly` is on.
+ photo message keyboard: 🖼 Друга снимка
+ last keyboard row: 📅 Насрочи {HH:mm} (always present; the label degrades to a bare
  „📅 Насрочи" when the suggested slot could not be computed). 📅 always means both destinations.
```

- [ ] **Step 2: Update the card actions table**

In the same file, replace the `Approve` row of the "Card actions" table and add two rows below it:

```markdown
| Approve (both) | ✅ button | Publishes to the website, then posts the link to the Facebook page; on an already-scheduled draft, clears the schedule and publishes instead. |
| Approve (site only) | 🌐 Само сайт | Publishes to the website only. The draft reaches `Published` on the site publish alone — Facebook is not in its required set. |
| Approve (Facebook only) | 📘 Само ФБ | Posts to the Facebook page only, as a standalone post (caption + image, no link) — the article never reaches the site. Available even on an `AwaitingCategory` `/post` card, since only Umbraco requires a category. |
```

- [ ] **Step 3: Note the manual-card exception**

In the same file, in the `/post` row of the slash-command table, after "The review card shows category buttons (no ✅ until one is tapped)", insert:

```markdown
— 📘 Само ФБ is the exception and stays tappable, because a Facebook-only post never calls Umbraco and so has no category requirement —
```

- [ ] **Step 4: Add the decision-log row**

Append to the table in `docs/decision-log.md`:

```markdown
| 2026-08-06 | — | **Publish target is per draft, not per process:** the review card gains 🌐 Само сайт and 📘 Само ФБ next to ✅ Одобри, writing `nw_Draft.PublishTarget` (`Both` \| `Website` \| `Facebook`, migration 0016). `PublishJob` asks `PublishTargets.RequiredDestinations` per draft instead of computing one process-wide array at construction, and the two Facebook queries (link post after the site publish vs. standalone post) now both run each cycle, filtered by target, instead of being selected by `Publishing:FacebookOnly`. That flag survives as an ops kill-switch and **overrides** the column: while it is on, the standalone query accepts every target — otherwise a draft approved as Both, or scheduled with 📅 (which always writes Both), would wait forever for a site publish the flag has disabled | Accepted |
```

- [ ] **Step 5: Commit**

```bash
git add docs/05-integrations/telegram.md docs/decision-log.md
git commit -m "docs: per-draft publish target on the review card"
```

---

### Task 9: Sandbox end-to-end verification

The repo has no DB test harness, so the SQL, the migration and the keyboards are proven here — this is the documented end-to-end harness (`docs/08-testing.md`, ADR-0014). **Do not skip it and do not report the feature as working before it passes.**

**Files:** none (verification only).

- [ ] **Step 1: Start the sandbox**

Run: `tools\restart-sandbox.ps1`, with the local Umbraco site running.
Confirm in the log: `Applied migration 0016_publish_target`.

- [ ] **Step 2: Check the card**

Wait for a review card in the sandbox chat (prefixed `🧪 SANDBOX`). Confirm the keyboard shows, in order: the ✅/✏️/❌ row, then `🌐 Само сайт` + `📘 Само ФБ`, then `📅 Насрочи {HH:mm}`. The sandbox forces `FacebookOnly` off, so 🌐 must be present.

- [ ] **Step 3: Verify website-only**

Tap `🌐 Само сайт` on one card. Expected:
- toast `✅ Одобрено (само сайт)`, card resolves to `✅ Одобрено (само сайт) от {editor}`
- the log shows the Umbraco publish success line; the returned URL opens on `https://localhost:44350`
- the 🚀 confirmation arrives **with** the 📋 „Текст за групите" block
- **no** `Facebook dry run for draft {id}` line appears for this draft
- in the database: `SELECT Id, Status, PublishTarget FROM dbo.nw_Draft WHERE Id = <id>` returns `Published` / `Website`

- [ ] **Step 4: Verify Facebook-only**

Tap `📘 Само ФБ` on another card. Expected:
- toast `✅ Одобрено (само ФБ)`, card resolves to `✅ Одобрено (само ФБ) от {editor}`
- the log shows `Facebook dry run for draft {id}` and **no** Umbraco publish for this draft
- nothing new appears on the local site
- in the database: status `Published`, target `Facebook`

- [ ] **Step 5: Verify the unchanged both-path**

Tap `✅ Одобри` on a third card. Expected: the site publish, then the dry-run Facebook line carrying the site URL, exactly as before this feature. Database: `Published` / `Both`.

- [ ] **Step 6: Verify the AwaitingCategory exception**

Send `/post Тест заглавие` + a body line to the sandbox chat. On the resulting card confirm there is **no** ✅ and **no** 🌐, but 📘 Само ФБ **is** present, and tapping it approves the draft (target `Facebook`) without ever picking a category.

- [ ] **Step 7: Record the result**

Append a dated line to the "Sandbox end-to-end harness" section of `docs/08-testing.md` recording that the per-draft target paths were exercised, matching the existing "Status: executed successfully on …" convention.

```bash
git add docs/08-testing.md
git commit -m "docs(testing): record the per-draft publish target sandbox run"
```

---

## Notes for the implementer

- **The bare `approve:{id}` form must keep working.** Two things depend on it: cards already sitting in the chat when the worker restarts, and the scheduled card's „✅ Одобри веднага" button (`TelegramGateway.ApproveNowKeyboard`). Do not "tidy" it into a three-segment form.
- **Do not touch `TryUnscheduleAsync`.** It changes *when* a draft publishes, never *where*.
- **`Publishing:FacebookOnly` is not dead code.** It is `false` in both shipped appsettings, but it is the documented lever for "the site is down, keep posting to the page" (decision-log 2026-07-08), and Task 5 gives it a defined interaction with the new column. Do not remove it.
- If a Dapper query starts throwing "a parameterless default constructor or one matching signature … is required", the row record's parameters no longer line up positionally with the SELECT columns. That is the constraint at the top of this plan, not a Dapper bug.
