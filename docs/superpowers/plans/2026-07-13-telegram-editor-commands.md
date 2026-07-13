# Telegram Editor Commands + Force-Draft Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add five editor commands (`/help`, `/quota`, `/health`, `/unmute`, `/draft <topicId>`) to the Telegram review bot and let an editor force a non-Hot topic into drafting.

**Architecture:** Text → `ReviewUpdateRouter.RouteText` → `ReviewCommand` → `TelegramJob` executes it, delegating data/formatting to repository methods. Force-draft is a new nullable `ForceDraftAtUtc` marker on `nw_Topic` that `DraftJob`'s topic query honours alongside Hot topics; `SaveDraftAsync` clears it. The topic's `Status` is never changed.

**Tech Stack:** C# / .NET (worker), Dapper over SQL Server, xUnit tests, embedded-resource SQL migrations, `Telegram.Bot` (long polling). User-facing strings are Bulgarian.

## Global Constraints

- User-facing bot strings are **Bulgarian** (Cyrillic). Match the tone of the existing command replies.
- Migrations are **embedded-resource `.sql` files**, `NNNN_snake_name.sql`, **single batch, no `GO`**; idempotency comes from the runner's `nw_SchemaVersion` bookkeeping (no `IF NOT EXISTS` guards). The `<EmbeddedResource Include="Database\Migrations\*.sql" />` glob auto-includes new files.
- Topic ids are `int` (`nw_Topic.Id int IDENTITY`). Use `int` for topic-id command parameters (consistent with the existing `MuteTopic(int TopicId)`).
- Authorization is unchanged: `RouteText` already applies the allowlist + review-chat gate before the command switch.
- Repository SQL has **no DB-backed test harness** in this repo — do not add one. Unit-test pure logic (routing, formatters); build-verify + manual-UAT the SQL methods.
- **Commit messages must NOT include a `Co-Authored-By` trailer** (repo convention).
- `ReviewCommand`s are records compared by value in tests (`Assert.Equal(new ShowStatus(), ...)`).

---

### Task 1: Migration 0011 — `ForceDraftAtUtc` column

**Files:**
- Create: `src/Newsroom.Infrastructure/Database/Migrations/0011_topic_force_draft.sql`
- Test (existing, auto-covers): `src/tests/Newsroom.Infrastructure.Tests/Database/EmbeddedMigrationsTests.cs`, `.../MigrationScriptTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `nw_Topic.ForceDraftAtUtc datetime2 NULL` — consumed by Task 5's SQL.

- [ ] **Step 1: Create the migration file**

Create `src/Newsroom.Infrastructure/Database/Migrations/0011_topic_force_draft.sql`:

```sql
-- 0011_topic_force_draft: ForceDraftAtUtc on nw_Topic — the editor's /draft <topic> command sets
-- this marker so DraftJob drafts the topic even when it is not Hot
-- (docs/05-integrations/telegram.md). Orthogonal to Status: the topic keeps its real lifecycle
-- state. DraftRepository.SaveDraftAsync clears the marker once a draft is produced, so a later
-- reject/expire of that draft does not silently re-trigger generation. Single batch, no GO.

ALTER TABLE dbo.nw_Topic ADD ForceDraftAtUtc datetime2 NULL;
```

- [ ] **Step 2: Run the migration metadata tests to confirm the new script is picked up and valid**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~Migration|FullyQualifiedName~EmbeddedMigrations"`
Expected: PASS. `EmbeddedMigrationsTests` asserts only uniqueness, ascending order, no `GO`, and non-empty SQL (there is no hard-coded count), so a well-formed `0011_` after `0010_` satisfies them with no test change.

- [ ] **Step 3: Commit**

```bash
git add "src/Newsroom.Infrastructure/Database/Migrations/0011_topic_force_draft.sql"
git commit -m "feat(db): add nw_Topic.ForceDraftAtUtc for editor force-draft"
```

---

### Task 2: Router — five new commands (TDD)

**Files:**
- Modify: `src/Newsroom.Core/Review/ReviewCommand.cs` (add 5 records)
- Modify: `src/Newsroom.Core/Review/ReviewUpdateRouter.cs` (switch arms + 2 parse helpers)
- Test: `src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs` (add tests; **modify** one existing theory)

**Interfaces:**
- Consumes: existing `ReviewUpdateRouter.RouteText`, `ReasonBadArguments`, `ReasonUnknownText`.
- Produces (record types other tasks consume): `ShowHelp`, `ShowQuota`, `ShowHealth`, `UnmuteTopic(int TopicId)`, `ForceDraftTopic(int TopicId)`.

- [ ] **Step 1: Add the failing tests**

In `ReviewUpdateRouterTests.cs`, **add** these tests (place near the existing status/mute tests):

```csharp
    [Theory]
    [InlineData("/help")]
    [InlineData("/HELP")]
    [InlineData("/help@PredelNewsBot")]
    public void Help_command_routes(string text)
    {
        Assert.Equal(new ShowHelp(), RouteText(Text(text)));
    }

    [Fact]
    public void Quota_and_health_commands_route()
    {
        Assert.Equal(new ShowQuota(), RouteText(Text("/quota")));
        Assert.Equal(new ShowHealth(), RouteText(Text("/health")));
    }

    [Fact]
    public void Unmute_parses_topic_id()
    {
        Assert.Equal(new UnmuteTopic(12), RouteText(Text("/unmute 12")));
        Assert.Equal(new UnmuteTopic(7), RouteText(Text("/unmute   7  "))); // tolerant spacing
    }

    [Theory]
    [InlineData("/unmute")]
    [InlineData("/unmute abc")]
    [InlineData("/unmute 0")]
    [InlineData("/unmute -3")]
    [InlineData("/unmute 12 extra")]
    public void Unmute_with_bad_arguments_is_ignored(string text)
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonBadArguments), RouteText(Text(text)));
    }

    [Fact]
    public void Draft_parses_topic_id()
    {
        Assert.Equal(new ForceDraftTopic(42), RouteText(Text("/draft 42")));
        Assert.Equal(new ForceDraftTopic(5), RouteText(Text("/draft   5  ")));
    }

    [Theory]
    [InlineData("/draft")]
    [InlineData("/draft 0")]
    [InlineData("/draft -1")]
    [InlineData("/draft https://example.com")] // URL form is Phase 4b — a non-numeric arg is bad args
    [InlineData("/draft 42 extra")]
    public void Draft_with_bad_arguments_is_ignored(string text)
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonBadArguments), RouteText(Text(text)));
    }
```

**Modify** the existing `Plain_chatter_and_unknown_commands_are_ignored` theory — remove the `/draft https://example.com` line (it now routes to `/draft` and yields `ReasonBadArguments`, covered above). The theory becomes:

```csharp
    [Theory]
    [InlineData("здравей")]
    [InlineData("/unknown")]
    [InlineData("   ")]
    public void Plain_chatter_and_unknown_commands_are_ignored(string text)
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonUnknownText), RouteText(Text(text)));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ReviewUpdateRouterTests"`
Expected: FAIL — `ShowHelp`, `ShowQuota`, `ShowHealth`, `UnmuteTopic`, `ForceDraftTopic` do not exist (compile error), and once added the new routes are missing.

- [ ] **Step 3: Add the command records**

In `src/Newsroom.Core/Review/ReviewCommand.cs`, add after the existing records (before `Ignore`):

```csharp
public sealed record ShowHelp : ReviewCommand;

public sealed record ShowQuota : ReviewCommand;

public sealed record ShowHealth : ReviewCommand;

/// <summary>/unmute: lift a topic's mute early (reverse of <see cref="MuteTopic"/>).</summary>
public sealed record UnmuteTopic(int TopicId) : ReviewCommand;

/// <summary>/draft &lt;topicId&gt;: force-draft a topic even if it is not Hot (docs/05).</summary>
public sealed record ForceDraftTopic(int TopicId) : ReviewCommand;
```

- [ ] **Step 4: Wire the routes**

In `src/Newsroom.Core/Review/ReviewUpdateRouter.cs`, extend the `RouteText` command switch (the one returning `ShowStatus`/`ShowTopics`/…):

```csharp
        return CommandName(parts[0]) switch
        {
            "/status" => new ShowStatus(),
            "/topics" => new ShowTopics(),
            "/help" => new ShowHelp(),
            "/quota" => new ShowQuota(),
            "/health" => new ShowHealth(),
            "/mute" => RouteMute(parts),
            "/unmute" => RouteUnmute(parts),
            "/draft" => RouteForceDraft(parts),
            "/pause" => new PauseDrafting(),
            "/resume" => new ResumeDrafting(),
            _ => new Ignore(ReasonUnknownText),
        };
```

Add these private helpers next to `RouteMute`:

```csharp
    private static ReviewCommand RouteUnmute(string[] parts)
    {
        if (parts.Length != 2)
            return new Ignore(ReasonBadArguments);
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var topicId) || topicId <= 0)
            return new Ignore(ReasonBadArguments);
        return new UnmuteTopic(topicId);
    }

    /// <summary>Only the numeric topic-id form is routed; a URL argument (Phase 4b) is bad args.</summary>
    private static ReviewCommand RouteForceDraft(string[] parts)
    {
        if (parts.Length != 2)
            return new Ignore(ReasonBadArguments);
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var topicId) || topicId <= 0)
            return new Ignore(ReasonBadArguments);
        return new ForceDraftTopic(topicId);
    }
```

(`NumberStyles.None` rejects signs/whitespace, matching `RouteMute`; the leading token is split off by the existing `parts` split + `CommandName` handling of `@BotName`.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ReviewUpdateRouterTests"`
Expected: PASS (all, including the modified chatter theory).

- [ ] **Step 6: Commit**

```bash
git add src/Newsroom.Core/Review/ReviewCommand.cs src/Newsroom.Core/Review/ReviewUpdateRouter.cs "src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs"
git commit -m "feat(review): route /help /quota /health /unmute /draft commands"
```

---

### Task 3: `/quota` and `/health` pure formatters (TDD)

**Files:**
- Modify: `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs` (add two `public static` formatters)
- Test: `src/tests/Newsroom.Infrastructure.Tests/Repositories/ReviewRepositoryTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `public static string FormatQuotaSummary(IReadOnlyList<(string Stage, int Used, int Cap)> stages)`
  - `public static string FormatHealthSummary(IReadOnlyList<(string Job, DateTime? LastBeatUtc)> jobs, DateTime nowUtc, int staleMinutes)`
  - Both consumed by Task 4's repo methods.

- [ ] **Step 1: Write the failing tests**

Create `src/tests/Newsroom.Infrastructure.Tests/Repositories/ReviewRepositoryTests.cs`:

```csharp
using Newsroom.Infrastructure.Repositories;

namespace Newsroom.Infrastructure.Tests.Repositories;

public class ReviewRepositoryTests
{
    [Fact]
    public void FormatQuotaSummary_lists_each_stage_used_over_cap()
    {
        var text = ReviewRepository.FormatQuotaSummary(
        [
            ("Draft", 6, 20),
            ("SelfCheck", 3, 20),
        ]);

        Assert.Contains("Draft 6/20", text);
        Assert.Contains("SelfCheck 3/20", text);
        Assert.DoesNotContain("⚠️", text);
    }

    [Fact]
    public void FormatQuotaSummary_flags_a_stage_at_or_over_its_cap()
    {
        var text = ReviewRepository.FormatQuotaSummary([("Draft", 20, 20)]);

        Assert.Contains("Draft 20/20 ⚠️", text);
    }

    [Fact]
    public void FormatQuotaSummary_handles_no_stages()
    {
        Assert.Equal("Няма конфигурирани AI етапи.", ReviewRepository.FormatQuotaSummary([]));
    }

    [Fact]
    public void FormatHealthSummary_reports_age_and_flags_stale_jobs()
    {
        var now = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        var text = ReviewRepository.FormatHealthSummary(
        [
            ("Scrape", now.AddMinutes(-2)),   // fresh
            ("Draft", now.AddMinutes(-40)),   // stale (> 15)
            ("Publish", null),                // never beaten
        ], now, staleMinutes: 15);

        Assert.Contains("Scrape: преди 2 мин", text);
        Assert.Contains("Draft: преди 40 мин ⚠️ закъснява", text);
        Assert.Contains("Publish: няма", text);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ReviewRepositoryTests"`
Expected: FAIL — `FormatQuotaSummary`/`FormatHealthSummary` do not exist (compile error).

- [ ] **Step 3: Implement the formatters**

In `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs`, add these `public static` methods (the file already has `using System.Text;` and `using System.Globalization;`):

```csharp
    /// <summary>Bulgarian /quota body: one line per AI stage, "used/cap", ⚠️ when at/over cap.</summary>
    public static string FormatQuotaSummary(IReadOnlyList<(string Stage, int Used, int Cap)> stages)
    {
        if (stages.Count == 0)
            return "Няма конфигурирани AI етапи.";

        var summary = new StringBuilder();
        summary.Append("🎫 AI квота днес");
        foreach (var (stage, used, cap) in stages)
        {
            summary.Append('\n').Append(stage).Append(' ').Append(used).Append('/').Append(cap);
            if (used >= cap)
                summary.Append(" ⚠️");
        }
        return summary.ToString();
    }

    /// <summary>Bulgarian /health body: one line per job, minutes since its last heartbeat,
    /// ⚠️ закъснява when older than <paramref name="staleMinutes"/>, няма when never seen.</summary>
    public static string FormatHealthSummary(
        IReadOnlyList<(string Job, DateTime? LastBeatUtc)> jobs, DateTime nowUtc, int staleMinutes)
    {
        var summary = new StringBuilder();
        summary.Append("🩺 Състояние на задачите");
        foreach (var (job, lastBeat) in jobs)
        {
            summary.Append('\n').Append(job).Append(": ");
            if (lastBeat is not { } beat)
            {
                summary.Append("няма");
                continue;
            }

            var minutes = (int)Math.Max(0, (nowUtc - beat).TotalMinutes);
            summary.Append("преди ").Append(minutes).Append(" мин");
            if (minutes > staleMinutes)
                summary.Append(" ⚠️ закъснява");
        }
        return summary.ToString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ReviewRepositoryTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs "src/tests/Newsroom.Infrastructure.Tests/Repositories/ReviewRepositoryTests.cs"
git commit -m "feat(review): add pure /quota and /health summary formatters"
```

---

### Task 4: `ReviewRepository` data methods (`/quota`, `/health`, `/unmute`) + `/topics` hint

**Files:**
- Modify: `src/Newsroom.Core/Review/Interfaces.cs` (`IReviewRepository`: add 3 methods)
- Modify: `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs` (inject `IConfiguration`; implement 3 methods; append `/topics` hint)

**Interfaces:**
- Consumes: `FormatQuotaSummary`, `FormatHealthSummary` (Task 3); `JobNames` (`Newsroom.Core.Operations`); `JobHeartbeat.KeyPrefix` (same assembly); `nw_Topic.ForceDraftAtUtc` not needed here.
- Produces (on `IReviewRepository`): `Task<string> BuildQuotaSummaryAsync(CancellationToken)`, `Task<string> BuildHealthSummaryAsync(CancellationToken)`, `Task<bool> UnmuteTopicAsync(int topicId, CancellationToken)` — consumed by Task 6.

- [ ] **Step 1: Add the interface methods**

In `src/Newsroom.Core/Review/Interfaces.cs`, add to `IReviewRepository` (near `BuildStatusSummaryAsync`/`MuteTopicAsync`):

```csharp
    /// <summary>Bulgarian /quota summary: AI requests used today vs the per-stage daily cap.</summary>
    Task<string> BuildQuotaSummaryAsync(CancellationToken ct);

    /// <summary>Bulgarian /health summary: last heartbeat per background job with a staleness flag.</summary>
    Task<string> BuildHealthSummaryAsync(CancellationToken ct);

    /// <summary>Lifts a topic's mute early. False when the topic id is unknown.</summary>
    Task<bool> UnmuteTopicAsync(int topicId, CancellationToken ct);
```

- [ ] **Step 2: Inject `IConfiguration` into `ReviewRepository`**

Change the class declaration (DI resolves the new parameter automatically — `ReviewRepository` is registered as `AddSingleton<IReviewRepository, ReviewRepository>()` and `IConfiguration` is container-registered):

```csharp
public sealed class ReviewRepository(IDbConnectionFactory db, IConfiguration configuration) : IReviewRepository
```

Add the required usings at the top of the file (alongside the existing ones):

```csharp
using Microsoft.Extensions.Configuration;

using Newsroom.Core.Operations;
```

- [ ] **Step 3: Implement `UnmuteTopicAsync`**

Add near `MuteTopicAsync`:

```csharp
    public async Task<bool> UnmuteTopicAsync(int topicId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Topic
            SET MutedUntilUtc = NULL
            WHERE Id = @topicId
            """,
            new { topicId });
        return rows > 0;
    }
```

Note: this returns `true` even if the topic was not muted (the id exists). That is fine — the reply ("вече не е заглушена") is true either way; only an unknown id returns `false`.

- [ ] **Step 4: Implement `BuildQuotaSummaryAsync`**

Add:

```csharp
    public async Task<string> BuildQuotaSummaryAsync(CancellationToken ct)
    {
        var todayUtc = DateTime.UtcNow.Date;

        using var connection = await db.OpenAsync(ct);
        var usedRows = await connection.QueryAsync<(string Stage, int Used)>(
            """
            SELECT Stage, SUM(RequestCount) AS Used
            FROM dbo.nw_CostLedger
            WHERE AtUtc >= @todayUtc
            GROUP BY Stage
            """,
            new { todayUtc });
        var used = usedRows.ToDictionary(r => r.Stage, r => r.Used, StringComparer.OrdinalIgnoreCase);

        // Show the union of configured stages (cap known, maybe 0 used) and stages actually seen
        // in today's ledger (so an unconfigured-but-used stage is not hidden). Cap defaults to the
        // same 1000 AiBudget uses when a stage has no explicit DailyRequestBudget.
        var configuredStages = configuration.GetSection("Ai:Stages").GetChildren().Select(c => c.Key);
        var stageNames = configuredStages
            .Concat(used.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stages = stageNames
            .Select(stage => (
                Stage: stage,
                Used: used.GetValueOrDefault(stage, 0),
                Cap: configuration.GetValue($"Ai:Stages:{stage}:DailyRequestBudget", 1000)))
            .ToList();

        return FormatQuotaSummary(stages);
    }
```

- [ ] **Step 5: Implement `BuildHealthSummaryAsync`**

Add:

```csharp
    public async Task<string> BuildHealthSummaryAsync(CancellationToken ct)
    {
        var staleMinutes = configuration.GetValue("Ops:Health:StaleMinutes", 15);
        string[] jobNames =
        [
            JobNames.Scrape, JobNames.Analyse, JobNames.Trend,
            JobNames.Draft, JobNames.Telegram, JobNames.Publish,
        ];
        var keys = jobNames.Select(j => JobHeartbeat.KeyPrefix + j).ToArray();

        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(string Key, string Value)>(
            """
            SELECT [Key], [Value] FROM dbo.nw_Config WHERE [Key] IN @keys
            """,
            new { keys });
        var beats = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

        var jobs = jobNames
            .Select(job => (
                Job: job,
                LastBeatUtc: beats.TryGetValue(JobHeartbeat.KeyPrefix + job, out var v)
                    && DateTime.TryParse(v, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : (DateTime?)null))
            .ToList();

        return FormatHealthSummary(jobs, DateTime.UtcNow, staleMinutes);
    }
```

(`JobHeartbeat.BeatAsync` writes `DateTime.UtcNow.ToString("O")` → round-trip parse yields a UTC `DateTime`; `DateTime.UtcNow` here is also UTC, so the subtraction in the formatter is correct.)

- [ ] **Step 6: Append the `/topics` force-draft hint**

In `BuildTopicsSummaryAsync`, before `return summary.ToString();` (the non-empty branch only — the empty branch returns "Няма отворени теми." earlier), add:

```csharp
        summary.Append("\n\nЗа чернова: /draft <номер>");
```

- [ ] **Step 7: Build to verify it compiles**

Run: `dotnet build src/Newsroom.Infrastructure/Newsroom.Infrastructure.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/Newsroom.Core/Review/Interfaces.cs src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs
git commit -m "feat(review): implement /quota /health /unmute data + /topics draft hint"
```

---

### Task 5: `DraftRepository` force-draft — request, pickup, clear

**Files:**
- Create: `src/Newsroom.Core/Drafting/ForceDraftResult.cs`
- Modify: `src/Newsroom.Core/Drafting/Interfaces.cs` (`IDraftRepository`: add `RequestForcedDraftAsync`; rename `GetHotTopicsNeedingDraftAsync` → `GetTopicsNeedingDraftAsync`)
- Modify: `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs` (add `RequestForcedDraftAsync`; rename `GetHotTopicsNeedingDraftAsync` → `GetTopicsNeedingDraftAsync` + new WHERE; clear `ForceDraftAtUtc` in `SaveDraftAsync`)
- Modify: `src/Newsroom.Worker/Jobs/DraftJob.cs` (update the renamed call site)

**Interfaces:**
- Consumes: `nw_Topic.ForceDraftAtUtc` (Task 1); `InactiveDraftStatuses`, `TopicStatus` (existing).
- Produces (on `IDraftRepository`): `Task<ForceDraftResult> RequestForcedDraftAsync(int topicId, CancellationToken)`; renamed `Task<IReadOnlyList<(long TopicId, string Label)>> GetTopicsNeedingDraftAsync(int maxAttempts, int maxCount, CancellationToken)`. `enum ForceDraftResult { TopicNotFound, TopicDone, AlreadyActive, Queued }` — consumed by Task 6.

- [ ] **Step 1: Confirm every reference to the old method name**

Run: `rg -n "GetHotTopicsNeedingDraftAsync" src`
Expected: three hits — the declaration in `src/Newsroom.Core/Drafting/Interfaces.cs`, the implementation in `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs`, and the call site in `src/Newsroom.Worker/Jobs/DraftJob.cs` (~line 98). All three are renamed in this task; if `rg` shows any other hit (e.g. a test), rename it too.

- [ ] **Step 2: Create the `ForceDraftResult` enum**

Create `src/Newsroom.Core/Drafting/ForceDraftResult.cs`:

```csharp
namespace Newsroom.Core.Drafting;

/// <summary>Outcome of an editor's /draft &lt;topicId&gt; request (docs/05-integrations/telegram.md).</summary>
public enum ForceDraftResult
{
    /// <summary>No topic with that id.</summary>
    TopicNotFound,

    /// <summary>The topic has fallen out of the window (Done); v1 refuses forcing it.</summary>
    TopicDone,

    /// <summary>A draft for this topic already exists in a non-inactive status.</summary>
    AlreadyActive,

    /// <summary>ForceDraftAtUtc set (and DraftAttempts reset); DraftJob will pick it up.</summary>
    Queued,
}
```

- [ ] **Step 3: Add + rename methods on `IDraftRepository`**

In `src/Newsroom.Core/Drafting/Interfaces.cs`, rename `GetHotTopicsNeedingDraftAsync` to `GetTopicsNeedingDraftAsync` (keep the same signature) and add:

```csharp
    /// <summary>Editor /draft &lt;topicId&gt;: mark a topic for drafting regardless of Hot status.
    /// Resets DraftAttempts so an exhausted topic can be retried. Refuses Done topics and topics
    /// that already have an active draft.</summary>
    Task<ForceDraftResult> RequestForcedDraftAsync(int topicId, CancellationToken ct);
```

If the interface file does not already have `using Newsroom.Core.Drafting;` in scope (it defines types in that namespace, so `ForceDraftResult` resolves without a using), no import change is needed.

- [ ] **Step 4: Rename the implementation and widen its WHERE**

In `DraftRepository.cs`, replace the whole `GetHotTopicsNeedingDraftAsync` method with:

```csharp
    public async Task<IReadOnlyList<(long TopicId, string Label)>> GetTopicsNeedingDraftAsync(
        int maxAttempts, int maxCount, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(long TopicId, string Label)>(
            """
            SELECT TOP (@maxCount) CAST(t.Id AS bigint) AS TopicId, t.Label
            FROM dbo.nw_Topic t
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
            """,
            new { maxCount, maxAttempts, hotStatus = nameof(TopicStatus.Hot), inactiveStatuses = InactiveDraftStatuses });
        return rows.ToList();
    }
```

(Forced topics bypass the Hot-status and mute gates but still respect `DraftAttempts` and no-active-draft; the reset in Step 5 gives a forced retry a fresh attempt budget. Forced topics sort ahead of Hot ones.)

- [ ] **Step 5: Add `RequestForcedDraftAsync`**

Add to `DraftRepository.cs` (near `GetTopicsNeedingDraftAsync`):

```csharp
    public async Task<ForceDraftResult> RequestForcedDraftAsync(int topicId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        var status = await connection.ExecuteScalarAsync<string?>(
            "SELECT Status FROM dbo.nw_Topic WHERE Id = @topicId",
            new { topicId }, transaction);
        if (status is null)
            return ForceDraftResult.TopicNotFound;
        if (status == nameof(TopicStatus.Done))
            return ForceDraftResult.TopicDone;

        var activeDrafts = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM dbo.nw_Draft
            WHERE TopicId = @topicId AND Status NOT IN @inactiveStatuses
            """,
            new { topicId, inactiveStatuses = InactiveDraftStatuses }, transaction);
        if (activeDrafts > 0)
            return ForceDraftResult.AlreadyActive;

        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Topic
            SET ForceDraftAtUtc = SYSUTCDATETIME(), DraftAttempts = 0
            WHERE Id = @topicId
            """,
            new { topicId }, transaction);
        transaction.Commit();
        return ForceDraftResult.Queued;
    }
```

(Early returns dispose the uncommitted transaction → rollback; no rows were changed on those paths.)

- [ ] **Step 6: Clear the marker in `SaveDraftAsync`**

In `SaveDraftAsync`, after `await InsertImagesAsync(connection, transaction, draftId, content, images, ct);` and before `transaction.Commit();`, add:

```csharp
        // A forced draft (ForceDraftAtUtc) is now fulfilled; clear the marker so a later
        // reject/expire of this draft does not silently re-trigger generation.
        await connection.ExecuteAsync(
            "UPDATE dbo.nw_Topic SET ForceDraftAtUtc = NULL WHERE Id = @topicId",
            new { topicId = bundle.TopicId },
            transaction);
```

- [ ] **Step 7: Update the `DraftJob` call site**

In `src/Newsroom.Worker/Jobs/DraftJob.cs` (~line 98), rename the call:

```csharp
            topics = await drafts.GetTopicsNeedingDraftAsync(maxAttempts, maxPerCycle, ct);
```

- [ ] **Step 8: Build to verify it compiles**

Run: `dotnet build`
Expected: Build succeeded, 0 errors (confirms the rename touched every reference from Step 1).

- [ ] **Step 9: Commit**

```bash
git add src/Newsroom.Core/Drafting/ForceDraftResult.cs src/Newsroom.Core/Drafting/Interfaces.cs src/Newsroom.Infrastructure/Repositories/DraftRepository.cs src/Newsroom.Worker/Jobs/DraftJob.cs
git commit -m "feat(draft): force-draft request, pickup, and marker clearing"
```

---

### Task 6: `TelegramJob` — execute the five commands

**Files:**
- Modify: `src/Newsroom.Worker/Jobs/TelegramJob.cs` (inject `IDraftRepository`; 5 switch cases; `HelpText`; `ForceDraftReply` helper; usings)

**Interfaces:**
- Consumes: `IReviewRepository.{BuildQuotaSummaryAsync, BuildHealthSummaryAsync, UnmuteTopicAsync}` (Task 4); `IDraftRepository.RequestForcedDraftAsync` + `ForceDraftResult` (Task 5); records `ShowHelp/ShowQuota/ShowHealth/UnmuteTopic/ForceDraftTopic` (Task 2); existing `SendTextAsync`.
- Produces: user-visible behaviour; nothing consumed by later tasks.

- [ ] **Step 1: Inject `IDraftRepository` and add the `Newsroom.Core.Drafting` using**

Add to the top of `TelegramJob.cs`:

```csharp
using Newsroom.Core.Drafting;
```

Add `IDraftRepository drafts` to the primary constructor (after `reviews`):

```csharp
public sealed class TelegramJob(
    IReviewRepository reviews,
    IDraftRepository drafts,
    Lazy<ITelegramGateway> gateway,
    IJobHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<TelegramJob> logger) : BackgroundService
```

- [ ] **Step 2: Add the `HelpText` constant and `ForceDraftReply` helper**

Add near the other class constants (after `TopicsToShow`):

```csharp
    private const string HelpText =
        "🤖 Команди\n" +
        "/status — състояние на конвейера\n" +
        "/topics — отворени теми\n" +
        "/quota — изразходвана AI квота днес\n" +
        "/health — състояние на задачите\n" +
        "/draft <номер> — пусни тема за чернова\n" +
        "/mute <номер> [часове] — заглуши тема (по подразбиране 24 ч.)\n" +
        "/unmute <номер> — отзаглуши тема\n" +
        "/pause — спри генерирането на чернови\n" +
        "/resume — възобнови генерирането\n" +
        "\n" +
        "Върху картичка: ✅ одобри · ✏️ промени · 🖼 друга снимка · ❌ откажи. " +
        "Отговор с текст = инструкции за промяна; отговор със снимка = прикачи снимка.";
```

Add this private static helper (near the other helpers):

```csharp
    private static string ForceDraftReply(int topicId, ForceDraftResult result) => result switch
    {
        ForceDraftResult.Queued => $"⚡ Тема #{topicId} е пусната за чернова.",
        ForceDraftResult.AlreadyActive => $"Тема #{topicId} вече има активна чернова.",
        ForceDraftResult.TopicDone => $"Тема #{topicId} е приключена.",
        _ => $"Няма такава тема (#{topicId}).", // TopicNotFound
    };
```

- [ ] **Step 3: Add the five switch cases**

In `HandleTextAsync`'s `switch (command)`, add these arms (before `case Ignore:`):

```csharp
            case ShowHelp:
                await SendTextAsync(text.ChatId, HelpText, ct);
                break;

            case ShowQuota:
                await SendTextAsync(text.ChatId, await reviews.BuildQuotaSummaryAsync(ct), ct);
                break;

            case ShowHealth:
                await SendTextAsync(text.ChatId, await reviews.BuildHealthSummaryAsync(ct), ct);
                break;

            case UnmuteTopic unmute:
                var unmuted = await reviews.UnmuteTopicAsync(unmute.TopicId, ct);
                await SendTextAsync(text.ChatId, unmuted
                    ? $"🔊 Тема #{unmute.TopicId} вече не е заглушена."
                    : $"Няма такава тема (#{unmute.TopicId}).", ct);
                break;

            case ForceDraftTopic force:
                var forceResult = await drafts.RequestForcedDraftAsync(force.TopicId, ct);
                if (forceResult == ForceDraftResult.Queued)
                    logger.LogInformation("Topic {TopicId} force-drafted by {User} via /draft",
                        force.TopicId, text.UserName ?? text.UserId.ToString());
                await SendTextAsync(text.ChatId, ForceDraftReply(force.TopicId, forceResult), ct);
                break;
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Newsroom.Worker/Jobs/TelegramJob.cs
git commit -m "feat(telegram): execute /help /quota /health /unmute /draft"
```

---

### Task 7: Documentation

**Files:**
- Modify: `docs/05-integrations/telegram.md` (the command tables)

**Interfaces:** none.

- [ ] **Step 1: Update the slash-command table**

In `docs/05-integrations/telegram.md`, add rows to the "Slash commands" table for `/help`, `/quota`, `/health`, and `/unmute <topicId>`, and add `/draft <topicId>` (force-draft a non-Hot topic). Keep the note that `/draft <url>` remains Phase 4b (only the numeric topic form is implemented). Remove the "no `/help`" caveat now that `/help` exists. Bump the "Last updated" date to 2026-07-13.

- [ ] **Step 2: Commit**

```bash
git add docs/05-integrations/telegram.md
git commit -m "docs(telegram): document /help /quota /health /unmute /draft"
```

---

### Task 8: Full build, test, and manual verification

**Files:** none (verification only).

- [ ] **Step 1: Full solution build**

Run: `dotnet build`
Expected: Build succeeded, 0 errors, 0 warnings introduced by these changes.

- [ ] **Step 2: Full test run**

Run: `dotnet test`
Expected: PASS — all existing tests plus the new router and formatter tests.

- [ ] **Step 3: Apply the migration locally**

Start the worker against the dev DB (migrations run at startup via `MigrationStartupService`). Confirm the log line `Applied migration 0011_topic_force_draft` appears and `nw_Topic` now has `ForceDraftAtUtc`.

- [ ] **Step 4: Manual UAT in Telegram** (repository SQL has no automated coverage — verify by hand)

From the review chat, confirm:
- `/help` — prints the command list.
- `/quota` — one line per AI stage, `used/cap`, ⚠️ on any exhausted stage.
- `/health` — one line per job with minutes-since-heartbeat; ⚠️ закъснява on a stale job.
- `/topics` — ends with the `За чернова: /draft <номер>` hint.
- `/mute 12` then `/unmute 12` — mute then "вече не е заглушена"; `/unmute 999999` → "Няма такава тема".
- `/draft <id of a non-Hot, non-Done topic with source articles>` → "⚡ … пусната за чернова", and within a DraftJob cycle a review card appears.
- `/draft <same id again while its draft is pending>` → "вече има активна чернова".
- `/draft <id of a Done topic>` → "приключена"; `/draft 999999` → "Няма такава тема".
- Reject the forced draft, wait a cycle → it must **not** regenerate (marker was cleared on save).

- [ ] **Step 5: Final commit if any doc/verification tweaks were needed**

```bash
git add -A
git commit -m "chore(telegram): verification fixes for editor commands"
```

---

### Task 9: Reconcile `/health` with the WatchdogJob per-job thresholds (post-review fix)

**Why:** The final review found `/health` used a flat 15-min staleness threshold while `WatchdogJob` already pages on per-job allowances (3× each job's interval). `/health` could show a dead fast job (Scrape ~3 min) as healthy for up to 15 min — false reassurance from the diagnostic command. Fix: extract the watchdog's per-job allowance table into a shared helper both consume, and make `/health` flag a job stale when its beat exceeds that job's allowance.

**Files:**
- Create: `src/Newsroom.Infrastructure/Operations/JobStalenessPolicy.cs`
- Modify: `src/Newsroom.Worker/Jobs/WatchdogJob.cs` (use the shared helper; drop its private `BuildExpectations`/`Allowance`)
- Modify: `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs` (`FormatHealthSummary` new signature; `BuildHealthSummaryAsync` uses per-job allowances)
- Test: `src/tests/Newsroom.Infrastructure.Tests/Repositories/ReviewRepositoryTests.cs` (update the `FormatHealthSummary` test)

**Interfaces:**
- Produces: `JobStalenessPolicy.BuildExpectations(IConfiguration) : IReadOnlyList<(string JobName, TimeSpan Allowance)>`; `FormatHealthSummary(IReadOnlyList<(string Job, DateTime? LastBeatUtc, TimeSpan Allowance)>, DateTime nowUtc) : string`.
- Consumes: existing `JobNames`, `TelegramOptions.From`, `UmbracoOptions.From`, `JobHeartbeat.KeyPrefix`.

- [ ] **Step 1: Update the failing test first (TDD)**

Replace the existing `FormatHealthSummary_reports_age_and_flags_stale_jobs` test in `ReviewRepositoryTests.cs` with the new per-job-allowance version:

```csharp
    [Fact]
    public void FormatHealthSummary_reports_age_and_flags_jobs_past_their_allowance()
    {
        var now = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        var text = ReviewRepository.FormatHealthSummary(
        [
            ("Scrape", now.AddMinutes(-2), TimeSpan.FromMinutes(3)),    // fresh (2 < 3)
            ("Analyse", now.AddMinutes(-6), TimeSpan.FromMinutes(6)),   // exactly at allowance → not stale
            ("Draft", now.AddMinutes(-40), TimeSpan.FromMinutes(15)),   // stale (40 > 15)
            ("Publish", null, TimeSpan.FromMinutes(3)),                 // never beaten
        ], now);

        Assert.Contains("Scrape: преди 2 мин", text);
        Assert.DoesNotContain("преди 2 мин ⚠️", text);   // fresh job not flagged
        Assert.Contains("Analyse: преди 6 мин", text);
        Assert.DoesNotContain("преди 6 мин ⚠️", text);   // boundary: age == allowance is not stale
        Assert.Contains("Draft: преди 40 мин ⚠️ закъснява", text);
        Assert.Contains("Publish: няма", text);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FormatHealthSummary"`
Expected: FAIL to compile — `FormatHealthSummary`'s current signature takes `(…, DateTime, int staleMinutes)`, not the 3-tuple + `nowUtc` form.

- [ ] **Step 3: Create the shared policy**

Create `src/Newsroom.Infrastructure/Operations/JobStalenessPolicy.cs`:

```csharp
using Microsoft.Extensions.Configuration;

using Newsroom.Core.Operations;
using Newsroom.Infrastructure.Publishing;
using Newsroom.Infrastructure.Review;

namespace Newsroom.Infrastructure.Operations;

/// <summary>
/// Per-job heartbeat allowance = 3× the interval each job configures itself with
/// (docs/07-operations.md). Shared by <c>WatchdogJob</c> (which pages when a beat exceeds its
/// allowance) and the <c>/health</c> command (which shows the same staleness view), so the
/// diagnostic and the pager can never disagree. Telegram and Publish are listed only when
/// configured — unconfigured jobs stay dormant and must not read as stale.
/// </summary>
public static class JobStalenessPolicy
{
    public static IReadOnlyList<(string JobName, TimeSpan Allowance)> BuildExpectations(
        IConfiguration configuration)
    {
        var expectations = new List<(string, TimeSpan)>
        {
            (JobNames.Scrape, Allowance(configuration, "Scrape:CheckSeconds", 60)),
            (JobNames.Analyse, Allowance(configuration, "Ai:Stages:Analyse:CheckSeconds", 120)),
            (JobNames.Trend, Allowance(configuration, "Ai:Stages:Cluster:CheckSeconds", 300)),
            (JobNames.Draft, Allowance(configuration, "Ai:Stages:Draft:CheckSeconds", 300)),
        };

        var telegram = TelegramOptions.From(configuration);
        if (telegram.IsConfigured)
            expectations.Add((JobNames.Telegram,
                TimeSpan.FromSeconds(3 * telegram.PollTimeoutSeconds + 60)));

        var umbraco = UmbracoOptions.From(configuration);
        if (umbraco.IsConfigured)
            expectations.Add((JobNames.Publish, TimeSpan.FromSeconds(3 * umbraco.CheckSeconds)));

        return expectations;
    }

    private static TimeSpan Allowance(IConfiguration configuration, string intervalKey, int defaultSeconds) =>
        TimeSpan.FromSeconds(3 * configuration.GetValue(intervalKey, defaultSeconds));
}
```

- [ ] **Step 4: Point `WatchdogJob` at the shared helper**

In `WatchdogJob.cs`: add `using Newsroom.Infrastructure.Operations;`. Change the loop header from `foreach (var (jobName, allowedStaleness) in BuildExpectations())` to:

```csharp
        foreach (var (jobName, allowedStaleness) in JobStalenessPolicy.BuildExpectations(configuration))
```

Then delete the now-unused private `BuildExpectations()` method (and its XML doc comment) and the private `Allowance(...)` method — their logic now lives in `JobStalenessPolicy`. Leave the rest of `WatchdogJob` (the `WatchdogPolicy.ShouldAlert` call, alerting, rate limiting) unchanged.

- [ ] **Step 5: Rewrite `FormatHealthSummary` to take a per-job allowance**

In `ReviewRepository.cs`, replace the whole `FormatHealthSummary` method (signature + doc + body) with:

```csharp
    /// <summary>Bulgarian /health body: one line per job, minutes since its last heartbeat,
    /// ⚠️ закъснява when the beat is older than that job's allowance, няма when never seen.</summary>
    public static string FormatHealthSummary(
        IReadOnlyList<(string Job, DateTime? LastBeatUtc, TimeSpan Allowance)> jobs, DateTime nowUtc)
    {
        var summary = new StringBuilder();
        summary.Append("🩺 Състояние на задачите");
        foreach (var (job, lastBeat, allowance) in jobs)
        {
            summary.Append('\n').Append(job).Append(": ");
            if (lastBeat is not { } beat)
            {
                summary.Append("няма");
                continue;
            }

            var age = nowUtc - beat;
            var minutes = (int)Math.Max(0, age.TotalMinutes);
            summary.Append("преди ").Append(minutes).Append(" мин");
            if (age > allowance)
                summary.Append(" ⚠️ закъснява");
        }
        return summary.ToString();
    }
```

- [ ] **Step 6: Rewrite `BuildHealthSummaryAsync` to use the shared expectations**

In `ReviewRepository.cs`, add `using Newsroom.Infrastructure.Operations;` (with the other usings) and replace the whole `BuildHealthSummaryAsync` method body with:

```csharp
    public async Task<string> BuildHealthSummaryAsync(CancellationToken ct)
    {
        var expectations = JobStalenessPolicy.BuildExpectations(configuration);
        var keys = expectations.Select(e => JobHeartbeat.KeyPrefix + e.JobName).ToArray();

        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(string Key, string Value)>(
            """
            SELECT [Key], [Value] FROM dbo.nw_Config WHERE [Key] IN @keys
            """,
            new { keys });
        var beats = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

        var jobs = expectations
            .Select(e => (
                Job: e.JobName,
                LastBeatUtc: beats.TryGetValue(JobHeartbeat.KeyPrefix + e.JobName, out var v)
                    && DateTime.TryParse(v, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : (DateTime?)null,
                e.Allowance))
            .ToList();

        return FormatHealthSummary(jobs, DateTime.UtcNow);
    }
```

This drops the `Ops:Health:StaleMinutes` config read entirely — `/health` now shows exactly the jobs the watchdog watches, each flagged against the watchdog's own allowance.

- [ ] **Step 7: Verify — tests, Infrastructure build, Worker compile**

- `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ReviewRepositoryTests"` → PASS (the updated FormatHealthSummary test + the quota tests).
- `dotnet build src/Newsroom.Infrastructure/Newsroom.Infrastructure.csproj` → 0 errors.
- `dotnet build src/Newsroom.Worker/Newsroom.Worker.csproj` → **0 `CS####` errors** (MSB3027 DLL-copy-lock errors are the expected live-worker condition and are acceptable).
- `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj` → PASS (confirms the WatchdogJob-adjacent `WatchdogPolicy` tests still pass).

- [ ] **Step 8: Commit**

```bash
git add src/Newsroom.Infrastructure/Operations/JobStalenessPolicy.cs src/Newsroom.Worker/Jobs/WatchdogJob.cs src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs "src/tests/Newsroom.Infrastructure.Tests/Repositories/ReviewRepositoryTests.cs"
git commit -m "fix(health): share WatchdogJob per-job thresholds with /health"
```
