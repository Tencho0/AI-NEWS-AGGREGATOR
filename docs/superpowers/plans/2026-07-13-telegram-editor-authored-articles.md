# Editor-Authored Articles via Telegram (`/post`, `/new`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an editor create an article by sending text in the Telegram review chat — `/post <text>` publishes verbatim (no AI), `/new <text>` has the AI write the article from the editor's notes; both flow through the existing review card → approve → publish path.

**Architecture:** Editor commands create a synthetic `nw_Topic` row with a new `Manual` status carrying the editor's text in a new `EditorInput` column. `/new` also sets the existing `ForceDraftAtUtc` marker so `DraftJob` picks the topic up unchanged; `GetTopicBundleAsync` synthesizes a one-article bundle from `EditorInput` for Manual topics, which makes generation, validation, self-check and ✏️ regeneration all work with no other `DraftJob` changes. `/post` inserts a `PendingReview` draft directly; the existing dispatch loop posts its card.

**Tech Stack:** .NET 9 worker (C# primary constructors, collection expressions), Dapper + SQL Server, Telegram.Bot via `ITelegramGateway`, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-13-telegram-editor-authored-articles-design.md`

## Global Constraints

- All bot replies are **Bulgarian**; everything interpolated into Telegram HTML goes through `ReviewMessageRenderer.Escape` (the shared `SendTextAsync` helper already escapes).
- **No DB-backed test harness exists — do not add one.** Repository SQL methods are build-verify + manual UAT; unit-test only pure logic (router, renderer, helpers).
- Bad-argument commands are **silently ignored** (`Ignore(ReasonBadArguments)`, no reply) — the established behaviour; do not add usage-hint replies.
- Statuses are stored as enum names (`nameof(...)`); migrations are embedded resources named `NNNN_name.sql`, single batch, no `GO`.
- Commit messages: match repo style (`feat(review): …`, `docs: …`). **Never add a `Co-Authored-By` line.**
- Files contain Cyrillic — edit only with the Edit/Write tools, never PowerShell `Get-Content`/`Set-Content`.
- Run all commands from the repo root. `dotnet build` and `dotnet test` with no args build/test everything.

---

### Task 1: Manual-topic foundations — `TopicStatus.Manual`, `ManualTopic` helper, migration 0012

**Files:**
- Modify: `src/Newsroom.Core/Trends/TopicStatus.cs`
- Create: `src/Newsroom.Core/Drafting/ManualTopic.cs`
- Create: `src/Newsroom.Infrastructure/Database/Migrations/0012_manual_topics.sql`
- Test: `src/tests/Newsroom.Core.Tests/Drafting/ManualTopicTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `TopicStatus.Manual` enum member; `ManualTopic.SourceName` (`string` const, value `"Редакция"`); `static string ManualTopic.LabelFrom(string text)` — first non-empty line, truncated to 60 chars on a word boundary with `…`. Column `dbo.nw_Topic.EditorInput NVARCHAR(MAX) NULL`. Tasks 3–6 rely on all of these.

- [ ] **Step 1: Write the failing tests**

Create `src/tests/Newsroom.Core.Tests/Drafting/ManualTopicTests.cs`:

```csharp
using Newsroom.Core.Drafting;

namespace Newsroom.Core.Tests.Drafting;

public class ManualTopicTests
{
    [Fact]
    public void Short_text_is_the_label()
    {
        Assert.Equal("Кметът откри новата зала", ManualTopic.LabelFrom("Кметът откри новата зала"));
    }

    [Fact]
    public void Only_the_first_nonempty_line_is_used()
    {
        Assert.Equal("Заглавие", ManualTopic.LabelFrom("\n  Заглавие  \nВтори ред с още текст."));
    }

    [Fact]
    public void Long_lines_truncate_on_a_word_boundary_with_ellipsis()
    {
        const string source =
            "Общинската администрация в Благоевград съобщи за нови мерки срещу задръстванията в центъра на града";

        var label = ManualTopic.LabelFrom(source);

        Assert.True(label.Length <= 60, $"label too long: {label.Length}");
        Assert.EndsWith("…", label);
        var prefix = label[..^1];
        Assert.StartsWith(prefix, source);        // no characters invented
        Assert.Equal(' ', source[prefix.Length]); // cut exactly at a word boundary, not mid-word
    }

    [Fact]
    public void Windows_line_endings_are_handled()
    {
        Assert.Equal("Заглавие", ManualTopic.LabelFrom("Заглавие\r\nТяло."));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ManualTopicTests"`
Expected: FAIL — `ManualTopic` does not exist (compile error).

- [ ] **Step 3: Implement `ManualTopic`, add the enum member, add the migration**

Create `src/Newsroom.Core/Drafting/ManualTopic.cs`:

```csharp
namespace Newsroom.Core.Drafting;

/// <summary>
/// Helpers for editor-authored articles (/post, /new — docs/05-integrations/telegram.md):
/// synthetic nw_Topic rows with Status=Manual whose EditorInput column carries the editor's
/// text. Pure — no I/O.
/// </summary>
public static class ManualTopic
{
    /// <summary>Source name shown for the synthetic bundle article built from EditorInput.</summary>
    public const string SourceName = "Редакция";

    private const int MaxLabelChars = 60;

    /// <summary>Topic label for an editor-authored article: the first non-empty line,
    /// truncated to 60 chars on a word boundary with an ellipsis.</summary>
    public static string LabelFrom(string text)
    {
        var firstLine = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        if (firstLine.Length <= MaxLabelChars)
            return firstLine;

        var cut = firstLine[..MaxLabelChars];
        var lastBreak = cut.LastIndexOf(' ');
        if (lastBreak > 0)
            cut = cut[..lastBreak];
        return cut.TrimEnd() + "…";
    }
}
```

In `src/Newsroom.Core/Trends/TopicStatus.cs`, extend the enum (keep the existing doc comment, add the member and one doc line):

```csharp
public enum TopicStatus
{
    Emerging,
    Hot,
    Muted,
    Done,
    /// <summary>Editor-authored (/post, /new): synthetic topic, invisible to trend scoring
    /// and /topics; nw_Topic.EditorInput carries the editor's text.</summary>
    Manual
}
```

Create `src/Newsroom.Infrastructure/Database/Migrations/0012_manual_topics.sql`:

```sql
-- 0012_manual_topics: EditorInput on nw_Topic — editor-authored articles (/post, /new;
-- docs/05-integrations/telegram.md). Topics with Status='Manual' are synthetic: created by the
-- Telegram commands, never by trend detection; EditorInput is the editor's original text and is
-- the "source article" the drafting AI (and self-check) works from. Single batch, no GO.

ALTER TABLE dbo.nw_Topic ADD EditorInput nvarchar(max) NULL;
```

- [ ] **Step 4: Run the tests and the migration test to verify they pass**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ManualTopicTests"`
Expected: PASS (4 tests).

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~Migration|FullyQualifiedName~EmbeddedMigrations"`
Expected: PASS — the embedded-resource glob (`Database\Migrations\*.sql`) picks 0012 up automatically; this test suite verifies naming/ordering.

- [ ] **Step 5: Commit**

```bash
git add src/Newsroom.Core/Drafting/ManualTopic.cs src/Newsroom.Core/Trends/TopicStatus.cs src/Newsroom.Infrastructure/Database/Migrations/0012_manual_topics.sql src/tests/Newsroom.Core.Tests/Drafting/ManualTopicTests.cs
git commit -m "feat(drafting): manual topic status, editor-input column and label helper"
```

---

### Task 2: Route `/post` and `/new` in `ReviewUpdateRouter`

**Files:**
- Modify: `src/Newsroom.Core/Review/ReviewCommand.cs`
- Modify: `src/Newsroom.Core/Review/ReviewUpdateRouter.cs`
- Test: `src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `public sealed record CreateArticle(string Headline, string Body) : ReviewCommand;` and `public sealed record CreateAiArticle(string Text) : ReviewCommand;` — Task 6's `TelegramJob` switch matches on these exact names.

**Background:** `RouteText` currently splits on `' '` only, so `"/post\nЗаглавие"` (command, then newline) would not even be recognised as a command token. Switch the split to *whitespace* (spaces, tabs, newlines) — this is behaviour-preserving for every existing command and test — and take the free-text argument from the original trimmed string so line breaks inside it survive.

- [ ] **Step 1: Write the failing tests**

Append to `src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs` (inside the class; the `Text(...)` helper and `RouteText` shim already exist there):

```csharp
    [Fact]
    public void Post_splits_headline_and_body()
    {
        Assert.Equal(
            new CreateArticle("Заглавие", "Първи ред.\nВтори ред."),
            RouteText(Text("/post Заглавие\nПърви ред.\nВтори ред.")));
    }

    [Fact]
    public void Post_headline_may_start_on_the_next_line()
    {
        Assert.Equal(
            new CreateArticle("Заглавие", "Тялото на статията."),
            RouteText(Text("/post\nЗаглавие\nТялото на статията.")));
    }

    [Fact]
    public void Post_single_line_is_headline_only()
    {
        Assert.Equal(new CreateArticle("Само заглавие", ""), RouteText(Text("/post Само заглавие")));
    }

    [Fact]
    public void Post_normalizes_windows_line_endings()
    {
        Assert.Equal(
            new CreateArticle("Заглавие", "Тяло."),
            RouteText(Text("/post Заглавие\r\nТяло.")));
    }

    [Fact]
    public void New_keeps_line_breaks_in_the_editor_text()
    {
        Assert.Equal(
            new CreateAiArticle("бележка ред 1\nбележка ред 2"),
            RouteText(Text("/new бележка ред 1\nбележка ред 2")));
    }

    [Fact]
    public void Post_and_new_route_with_botname_suffix()
    {
        Assert.Equal(new CreateArticle("Заглавие", ""), RouteText(Text("/post@MyBot Заглавие")));
        Assert.Equal(new CreateAiArticle("бележки"), RouteText(Text("/new@MyBot бележки")));
    }

    [Theory]
    [InlineData("/post")]
    [InlineData("/post   ")]
    [InlineData("/new")]
    [InlineData("/new \n ")]
    public void Post_and_new_without_text_are_ignored(string text)
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonBadArguments), RouteText(Text(text)));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ReviewUpdateRouterTests"`
Expected: FAIL — `CreateArticle` / `CreateAiArticle` do not exist (compile error).

- [ ] **Step 3: Implement the records and routing**

Append to `src/Newsroom.Core/Review/ReviewCommand.cs` (before the `Ignore` record, matching the file's ordering of commands-then-Ignore):

```csharp
/// <summary>/post: verbatim editor article — first line is the headline, the rest the body.
/// Publishes exactly as sent, no AI (docs/05-integrations/telegram.md).</summary>
public sealed record CreateArticle(string Headline, string Body) : ReviewCommand;

/// <summary>/new: the editor's notes become raw material for an AI-written article
/// (Manual topic + ForceDraft pickup — docs/05-integrations/telegram.md).</summary>
public sealed record CreateAiArticle(string Text) : ReviewCommand;
```

In `src/Newsroom.Core/Review/ReviewUpdateRouter.cs`, replace the `parts` split + switch inside `RouteText` (currently `text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)`) with:

```csharp
        // Whitespace split (not just spaces): a newline right after the command token is how
        // multi-line /post and /new arrive. Behaviour-preserving for the id-based commands.
        var parts = text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
        // Free-text argument for /post и /new: everything after the command token, line breaks
        // preserved (parts would collapse them). text is trimmed, so it starts with parts[0].
        var argument = text[parts[0].Length..].Trim();
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
            "/post" => RoutePost(argument),
            "/new" => argument.Length == 0 ? new Ignore(ReasonBadArguments) : new CreateAiArticle(NormalizeNewlines(argument)),
            "/pause" => new PauseDrafting(),
            "/resume" => new ResumeDrafting(),
            _ => new Ignore(ReasonUnknownText),
        };
```

Add the two private helpers at the bottom of the class (next to `RouteForceDraft`):

```csharp
    /// <summary>/post: the first line of the argument is the headline, the remainder the body
    /// (the argument is trimmed, so the first line is never empty).</summary>
    private static ReviewCommand RoutePost(string argument)
    {
        if (argument.Length == 0)
            return new Ignore(ReasonBadArguments);

        var normalized = NormalizeNewlines(argument);
        var newline = normalized.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0
            ? new CreateArticle(normalized, "")
            : new CreateArticle(normalized[..newline].TrimEnd(), normalized[(newline + 1)..].Trim());
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');
```

- [ ] **Step 4: Run the full router test suite to verify everything passes**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ReviewUpdateRouterTests"`
Expected: PASS — all new tests plus every pre-existing router test (the whitespace-split change must not break `/mute`, `/draft`, chatter-ignore, botname-suffix tests).

- [ ] **Step 5: Commit**

```bash
git add src/Newsroom.Core/Review/ReviewCommand.cs src/Newsroom.Core/Review/ReviewUpdateRouter.cs src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs
git commit -m "feat(review): route /post and /new editor-article commands"
```

---

### Task 3: Repository writes — `CreateManualTopicAsync` and `CreateManualArticleAsync`

**Files:**
- Modify: `src/Newsroom.Core/Drafting/Interfaces.cs` (`IDraftRepository`)
- Modify: `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs`

**Interfaces:**
- Consumes: `ManualTopic.LabelFrom` / `TopicStatus.Manual` (Task 1).
- Produces: `Task<int> CreateManualTopicAsync(string editorText, CancellationToken ct)` and `Task<long> CreateManualArticleAsync(string headline, string body, CancellationToken ct)` on `IDraftRepository` — Task 6's `TelegramJob` calls both.

No unit tests (SQL, no DB harness) — build-verify here, manual UAT at the end.

- [ ] **Step 1: Add the interface methods**

In `src/Newsroom.Core/Drafting/Interfaces.cs`, append to `IDraftRepository` (after `RequestForcedDraftAsync`, keeping the command methods together):

```csharp
    /// <summary>/new: creates a Manual topic carrying the editor's text and sets ForceDraftAtUtc,
    /// so DraftJob drafts it next cycle (GetTopicBundleAsync synthesizes the bundle from
    /// EditorInput). Returns the new topic id.</summary>
    Task<int> CreateManualTopicAsync(string editorText, CancellationToken ct);

    /// <summary>/post: creates a Manual topic plus a verbatim PendingReview draft (no AI, zero
    /// cost) — the review dispatch loop posts its card. EditorInput keeps the original text so a
    /// later ✏️ regeneration has source material. Returns the new draft id.</summary>
    Task<long> CreateManualArticleAsync(string headline, string body, CancellationToken ct);
```

- [ ] **Step 2: Implement in `DraftRepository`**

Add to `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs` (after `RequestForcedDraftAsync`; reuse the file's existing private `Truncate` helper):

```csharp
    public async Task<int> CreateManualTopicAsync(string editorText, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dbo.nw_Topic (Label, Status, EditorInput, ForceDraftAtUtc)
            OUTPUT INSERTED.Id
            VALUES (@label, @status, @editorText, SYSUTCDATETIME())
            """,
            new
            {
                label = ManualTopic.LabelFrom(editorText),
                status = nameof(TopicStatus.Manual),
                editorText,
            });
    }

    public async Task<long> CreateManualArticleAsync(string headline, string body, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        var topicId = await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dbo.nw_Topic (Label, Status, EditorInput)
            OUTPUT INSERTED.Id
            VALUES (@label, @status, @editorInput)
            """,
            new
            {
                label = ManualTopic.LabelFrom(headline),
                status = nameof(TopicStatus.Manual),
                editorInput = body.Length == 0 ? headline : headline + "\n\n" + body,
            },
            transaction);

        // Verbatim: what the editor sent is the article. PromptVersion marks the row as
        // editor-authored; Model shows as "модел editor" on the card; cost columns default to 0.
        var draftId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO dbo.nw_Draft (TopicId, Version, Status, Headline, BodyMarkdown, PromptVersion, Model)
            OUTPUT INSERTED.Id
            VALUES (@topicId, 1, @status, @headline, @body, N'editor-v1', N'editor')
            """,
            new
            {
                topicId,
                status = nameof(DraftStatus.PendingReview),
                headline = Truncate(headline, 300),
                body,
            },
            transaction);

        transaction.Commit();
        return draftId;
    }
```

Add `using Newsroom.Core.Trends;` to the file's usings if not already present (for `TopicStatus`).

- [ ] **Step 3: Build to verify**

Run: `dotnet build`
Expected: Build succeeded, 0 errors. (Warnings unrelated to these files are pre-existing.)

- [ ] **Step 4: Commit**

```bash
git add src/Newsroom.Core/Drafting/Interfaces.cs src/Newsroom.Infrastructure/Repositories/DraftRepository.cs
git commit -m "feat(drafting): repository writes for editor-authored topics and verbatim drafts"
```

---

### Task 4: Make the AI pipeline handle Manual topics (synthetic bundle, source filter, failure notice)

**Files:**
- Modify: `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs` (`GetTopicBundleAsync`, `SaveDraftAsync`)
- Modify: `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs` (`GetUnreportedRegenFailuresAsync`)

**Interfaces:**
- Consumes: `TopicStatus.Manual`, `ManualTopic.SourceName` (Task 1); `EditorInput` column (Task 1).
- Produces: no signature changes — `DraftJob` works on Manual topics with zero changes to `DraftJob.cs` itself.

Three surgical changes; build-verify only (SQL + projection, no DB harness).

- [ ] **Step 1: Synthesize the bundle for Manual topics in `GetTopicBundleAsync`**

In `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs`, replace the start of `GetTopicBundleAsync` — the current label-only lookup:

```csharp
        var label = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT Label FROM dbo.nw_Topic WHERE Id = @topicId
            """,
            new { topicId });
        if (label is null)
            return null;
```

with a lookup that also reads status + editor input, and short-circuits Manual topics:

```csharp
        var topic = (await connection.QueryAsync<(string Label, string Status, string? EditorInput)>(
            """
            SELECT Label, Status, EditorInput FROM dbo.nw_Topic WHERE Id = @topicId
            """,
            new { topicId })).FirstOrDefault();
        if (topic.Label is null)
            return null;

        // Manual topics (/post, /new) have no scraped sources — the editor's text IS the source.
        // One synthetic article makes generation, self-check and ✏️ regeneration work unchanged.
        if (topic.Status == nameof(TopicStatus.Manual) && !string.IsNullOrWhiteSpace(topic.EditorInput))
        {
            var editorText = topic.EditorInput.Length <= maxTextCharsPerArticle
                ? topic.EditorInput
                : topic.EditorInput[..maxTextCharsPerArticle];
            return new TopicBundle(topicId, topic.Label,
            [
                new TopicSourceArticle(
                    ArticleId: 0, Title: topic.Label, SourceName: ManualTopic.SourceName,
                    Url: "", PublishedAtUtc: null, Summary: "", Text: editorText),
            ]);
        }

        var label = topic.Label;
```

(The existing articles query below continues to use `label` unchanged.)

- [ ] **Step 2: Skip URL-less sources in `SaveDraftAsync`**

The synthetic article has an empty `Url`; without this filter the review card would render a dead `<a href="">Редакция</a>` source link. In `SaveDraftAsync`, change:

```csharp
        var sources = bundle.Articles
            .Select(a => new SourceRef(a.Url, a.SourceName))
            .ToList();
```

to:

```csharp
        var sources = bundle.Articles
            .Where(a => !string.IsNullOrEmpty(a.Url)) // synthetic Manual-topic article has no URL
            .Select(a => new SourceRef(a.Url, a.SourceName))
            .ToList();
```

- [ ] **Step 3: Report generation failures for Manual topics**

An editor is actively waiting on `/new`, so its failures must not be silent (normal Hot-topic failures stay silent). `RecordGenerationFailureAsync` already inserts a `GenerationFailed` draft row; the notice query just filters it out today. In `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs`, `GetUnreportedRegenFailuresAsync`, change the WHERE clause:

```sql
            WHERE d.Status = @failedStatus
              AND d.RegenInstructions IS NOT NULL
              AND d.TelegramMessageId IS NULL
```

to:

```sql
            WHERE d.Status = @failedStatus
              AND (d.RegenInstructions IS NOT NULL OR t.Status = @manualStatus)
              AND d.TelegramMessageId IS NULL
```

and the parameters object from `new { max, failedStatus = nameof(DraftStatus.GenerationFailed) }` to:

```csharp
            new { max, failedStatus = nameof(DraftStatus.GenerationFailed), manualStatus = nameof(TopicStatus.Manual) });
```

`TelegramJob.ReportFailedRegenerationsAsync` consumes this unchanged — its existing „⚠️ Новата версия за „…“ не можа да бъде създадена“ message and the message-id stamping (which marks the notice reported without starting a review clock) both apply as-is. Add `using Newsroom.Core.Trends;` to `ReviewRepository.cs` if missing.

- [ ] **Step 4: Build to verify**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Newsroom.Infrastructure/Repositories/DraftRepository.cs src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs
git commit -m "feat(drafting): draft pipeline support for manual topics"
```

---

### Task 5: Review card for Manual topics — `IsManual` through the view + renderer

**Files:**
- Modify: `src/Newsroom.Core/Review/DraftReviewView.cs`
- Modify: `src/Newsroom.Core/Review/ReviewMessageRenderer.cs`
- Modify: `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs` (`ViewSelectSql`, `ReviewRow`, `ToView`)
- Test: `src/tests/Newsroom.Core.Tests/Review/ReviewMessageRendererTests.cs`

**Interfaces:**
- Consumes: `Manual` status literal in SQL (Task 1).
- Produces: `DraftReviewView` gains a **final** positional parameter `bool IsManual = false` — existing construction sites compile unchanged.

- [ ] **Step 1: Write the failing renderer tests**

In `src/tests/Newsroom.Core.Tests/Review/ReviewMessageRendererTests.cs`, extend the `View(...)` builder's parameter list with four optional parameters (add to the existing signature; pass them through in the `new(...)` below it):

```csharp
    private static DraftReviewView View(
        string headline = "МОЩЕН ТРУС РАЗТЪРСИ ЮГОЗАПАДА",
        string? subtitle = "Усетен и в Благоевград",
        string body = "Земетресение разтърси региона, съобщи БТА.",
        int sourceCount = 5,
        IReadOnlyList<(string Name, string Url)>? sources = null,
        IReadOnlyList<string>? flaggedClaims = null,
        string? model = "gemini-2.5-flash",
        string category = "Общество",
        string? region = "Благоевград",
        IReadOnlyList<string>? tags = null,
        bool isManual = false) => new(
```

and in the record construction change `Category: "Общество",` → `Category: category,`, `Region: "Благоевград",` → `Region: region,`, `Tags: ["земетресение", "Благоевград"],` → `Tags: tags ?? ["земетресение", "Благоевград"],` and append `IsManual: isManual` after `TelegramMessageId: null`.

Then add the tests:

```csharp
    [Fact]
    public void Manual_topics_render_the_editorial_header()
    {
        var html = ReviewMessageRenderer.RenderHtml(View(isManual: true));

        Assert.StartsWith("✍️ Земетресение в Югозапада (редакторска)\n", html);
        Assert.DoesNotContain("score", html);
        Assert.DoesNotContain("източника", html);
    }

    [Fact]
    public void Meta_line_is_skipped_when_category_region_and_tags_are_empty()
    {
        var html = ReviewMessageRenderer.RenderHtml(View(category: "", region: null, tags: []));

        Assert.DoesNotContain("📎", html);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ReviewMessageRendererTests"`
Expected: FAIL — `DraftReviewView` has no `IsManual` (compile error).

- [ ] **Step 3: Implement view + renderer + projection**

`src/Newsroom.Core/Review/DraftReviewView.cs` — add a final positional parameter with a default (existing callers stay valid):

```csharp
    long? TelegramMessageId,
    bool IsManual = false);
```

`src/Newsroom.Core/Review/ReviewMessageRenderer.cs` — replace the header block in `RenderHtml`:

```csharp
        html.Append("🔥 ").Append(Escape(v.TopicLabel))
            .Append(" (score ").Append(v.TopicScore.ToString("0.0", CultureInfo.InvariantCulture))
            .Append(", ").Append(v.SourceCount)
            .Append(v.SourceCount == 1 ? " източник)" : " източника)").Append('\n');
```

with:

```csharp
        if (v.IsManual)
        {
            // Editor-authored (/post, /new): no trend score and no scraped sources to count.
            html.Append("✍️ ").Append(Escape(v.TopicLabel)).Append(" (редакторска)").Append('\n');
        }
        else
        {
            html.Append("🔥 ").Append(Escape(v.TopicLabel))
                .Append(" (score ").Append(v.TopicScore.ToString("0.0", CultureInfo.InvariantCulture))
                .Append(", ").Append(v.SourceCount)
                .Append(v.SourceCount == 1 ? " източник)" : " източника)").Append('\n');
        }
```

and wrap the meta line (verbatim drafts have no category/region/tags) — replace:

```csharp
        html.Append('\n').Append("📎 Категория: ").Append(Escape(v.Category));
        if (!string.IsNullOrWhiteSpace(v.Region))
            html.Append(" · Регион: ").Append(Escape(v.Region));
        if (v.Tags.Count > 0)
            html.Append(" · Тагове: ").Append(Escape(string.Join(", ", v.Tags)));
        html.Append('\n');
```

with:

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

`src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs`:
- In `ViewSelectSql`, after the `d.TelegramMessageId` line (before `FROM`), add:

```sql
               CASE WHEN t.Status = N'Manual' THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS IsManual
```

  (add a trailing comma to the `d.TelegramMessageId` line).
- In the `ReviewRow` record, append `bool IsManual` as the last parameter.
- In `ToView`, append `r.IsManual` as the last argument (after `r.TelegramMessageId`).

- [ ] **Step 4: Run the renderer suite and full build**

Run: `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj --filter "FullyQualifiedName~ReviewMessageRendererTests"`
Expected: PASS — the two new tests plus all pre-existing renderer tests (default `isManual: false` keeps their expected output identical).

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Newsroom.Core/Review/DraftReviewView.cs src/Newsroom.Core/Review/ReviewMessageRenderer.cs src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs src/tests/Newsroom.Core.Tests/Review/ReviewMessageRendererTests.cs
git commit -m "feat(review): editorial review-card header for manual topics"
```

---

### Task 6: `TelegramJob` handlers, `/help` entries, `/topics` filter

**Files:**
- Modify: `src/Newsroom.Worker/Jobs/TelegramJob.cs`
- Modify: `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs` (`BuildTopicsSummaryAsync`)

**Interfaces:**
- Consumes: `CreateArticle` / `CreateAiArticle` (Task 2); `IDraftRepository.CreateManualTopicAsync` / `CreateManualArticleAsync` (Task 3).
- Produces: end-user behaviour; nothing downstream.

Build-verify (the job has no unit-test seam; the router and renderer carry the tested logic).

- [ ] **Step 1: Add the two switch cases**

In `src/Newsroom.Worker/Jobs/TelegramJob.cs`, in the `HandleTextAsync` switch, after the `case ForceDraftTopic force:` block:

```csharp
            case CreateArticle create:
                var manualDraftId = await drafts.CreateManualArticleAsync(create.Headline, create.Body, ct);
                logger.LogInformation("Editor article draft {DraftId} created by {User} via /post",
                    manualDraftId, text.UserName ?? text.UserId.ToString());
                await SendTextAsync(text.ChatId, "📝 Статията е приета — картичката за преглед идва.", ct);
                break;

            case CreateAiArticle createAi:
                var manualTopicId = await drafts.CreateManualTopicAsync(createAi.Text, ct);
                logger.LogInformation("Manual topic {TopicId} queued for AI drafting by {User} via /new",
                    manualTopicId, text.UserName ?? text.UserId.ToString());
                await SendTextAsync(text.ChatId,
                    $"✍️ Статията се пише (тема #{manualTopicId}) — ще я получиш за преглед.", ct);
                break;
```

- [ ] **Step 2: Extend `/help`**

In the `HelpText` constant, after the `/draft <номер>` line, add:

```csharp
        "/post <заглавие и текст> — публикувай статия точно както е написана\n" +
        "/new <бележки> — AI пише статия от твоя текст\n" +
```

- [ ] **Step 3: Hide Manual topics from `/topics`**

In `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs`, `BuildTopicsSummaryAsync`, change:

```sql
            WHERE t.Status <> @doneStatus
```

to:

```sql
            WHERE t.Status NOT IN (@doneStatus, @manualStatus)
```

and the parameters from `new { max, doneStatus = nameof(TopicStatus.Done) }` to:

```csharp
            new { max, doneStatus = nameof(TopicStatus.Done), manualStatus = nameof(TopicStatus.Manual) })).ToList();
```

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

Run: `dotnet test`
Expected: PASS — everything green across all projects.

- [ ] **Step 5: Commit**

```bash
git add src/Newsroom.Worker/Jobs/TelegramJob.cs src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs
git commit -m "feat(review): /post and /new handlers, help entries, manual topics hidden from /topics"
```

---

### Task 7: Documentation

**Files:**
- Modify: `docs/05-integrations/telegram.md`
- Modify: `docs/02-functional-spec.md`
- Modify: `docs/11-risks-and-open-questions.md`
- Modify: `docs/decision-log.md`

**Interfaces:** none — docs are the project's source of truth (docs/README).

- [ ] **Step 1: `docs/05-integrations/telegram.md`**

In the slash-commands table, add after the `/draft` row:

```markdown
| `/post <заглавие и текст>` | Editor-authored **verbatim** article: first line = headline, rest = body. Creates a `Manual` topic + a `PendingReview` draft with no AI involved (zero quota); the normal review card follows — ✅ publishes exactly the sent text, a photo reply attaches an image, ✏️ regenerates **via AI** (costs one Draft request). Empty text is silently ignored. |
| `/new <бележки>` | Editor-authored **AI** article: the text (notes, press release) becomes the source material; a `Manual` topic with `ForceDraftAtUtc` rides the normal DraftJob pipeline (style guide, validation, self-check against the editor's text, image suggestions). Costs one Draft (+ one SelfCheck) request. Generation failures are reported to the chat (the editor is waiting). Empty text is silently ignored. |
```

Also update the review-message-format section: note that editor-authored drafts render the header `✍️ <label> (редакторска)` instead of `🔥 … (score …, N източника)`, and that the `📎` meta line is omitted when the draft has no category/region/tags (verbatim `/post` drafts).

- [ ] **Step 2: `docs/02-functional-spec.md`**

In §5 (review flow / editor commands area, near the `/draft <url>` command table entry), add a short paragraph:

```markdown
Editor-authored articles: `/post <text>` creates a verbatim draft (first line = headline) that
publishes exactly as sent after ✅, with no AI cost; `/new <text>` feeds the editor's notes
through the normal AI drafting pipeline as a single-source topic. Both create a synthetic
`Manual` topic (`nw_Topic.EditorInput` carries the text) and end in the standard review card.
```

- [ ] **Step 3: `docs/11-risks-and-open-questions.md`**

Add an open question (follow the existing Q-N numbering in the file):

```markdown
| Q-N | Manual (`/post`, `/new`-verbatim) drafts have no Category/SEO fields — Umbraco publishing requires them. Needs a default-category decision before `Publishing:FacebookOnly` is turned off. | Open |
```

(Replace `Q-N` with the next free number in the table.)

- [ ] **Step 4: `docs/decision-log.md`**

Append a row following the file's format:

```markdown
| 2026-07-13 | — | Editor-authored articles via Telegram: /post (verbatim, no AI) and /new (AI from editor notes); synthetic Manual topics reuse the force-draft pipeline | Accepted |
```

- [ ] **Step 5: Commit**

```bash
git add docs/05-integrations/telegram.md docs/02-functional-spec.md docs/11-risks-and-open-questions.md docs/decision-log.md
git commit -m "docs: editor-authored articles via /post and /new"
```

---

### Manual UAT (after all tasks; worker running per docs/runbooks/start-the-worker.md)

The repository SQL has no automated harness — verify these flows live (DryRun for Facebook first):

1. `/post Тестово заглавие` + a body line → confirmation reply, then a review card with the `✍️ … (редакторска)` header, no `📎` line, `модел editor` — ✅ publishes the exact text (check line breaks on the FB post), ❌ discards.
2. Photo reply to a `/post` card → image attaches (editor-upload) and rides the FB photo post.
3. `/new` with a couple of sentences of notes → "✍️ Статията се пише…" reply; a normal-looking review card arrives after the next Draft cycle (≤ ~5 min); `/quota` shows the Draft request; sources section is absent (no URL-less link).
4. ✏️ on the `/new` card with an instruction → new version arrives (regeneration works from `EditorInput`).
5. `/new` when the Draft budget is exhausted (or force an error) → the ⚠️ failure notice appears instead of silence.
6. `/topics` does not list the Manual topics; `/help` shows the two new commands.
7. `/post` with no text and `/new` with no text → no reply (silent ignore, existing bad-args behaviour).
