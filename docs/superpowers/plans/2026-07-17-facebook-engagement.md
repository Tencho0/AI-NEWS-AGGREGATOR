# Facebook Engagement (Social Captions + Suggested-Time Scheduling) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** AI drafts get a social-native Facebook caption (hook + CTA + hashtags, generated in the same Gemini call, reviewed in Telegram) that replaces the ALL-CAPS full-body post, plus a 📅 Насрочи review-card button that schedules publication at a system-suggested high-engagement slot while ✅ Одобри keeps publishing immediately.

**Architecture:** The caption rides the existing draft pipeline end-to-end: new JSON fields in the drafting prompt → `DraftContent` → `DraftValidator` gates → `nw_Draft` columns → review card → `PublishRepository` prefers the caption over `FacebookTeaser.ComposeFullBody`. Scheduling is one nullable `nw_Draft.ScheduledForUtc` gate in the publish queries, a pure `PublishSlotSuggester` in Core, and one new guarded repository transition wired to a new `schedule:{draftId}` Telegram callback.

**Tech Stack:** .NET 10 worker, Dapper over SQL Express, Telegram.Bot, Gemini via `IChatClient` (ADR-0010), xUnit.

**Spec:** `docs/superpowers/specs/2026-07-17-facebook-engagement-design.md`

## Global Constraints

- **Never** add `Co-Authored-By` (or any AI attribution) to commits — owner rule.
- **Never** rewrite source files via PowerShell `Get-Content`/`Set-Content` — PS 5.1 corrupts UTF-8 Cyrillic (incident 2026-07-03). Use IDE/agent Edit/Write tooling only. Files contain Bulgarian text almost everywhere.
- The repo path contains spaces — always quote paths in shell commands.
- Editor/user-facing strings are Bulgarian; code, comments, and log messages are English.
- Migrations: sequential numbering (next is `0013`), single batch, **no `GO`**, header comment explaining what/why (see `0012_manual_topics.sql`).
- Runtime fallbacks key off `FacebookCaption IS NULL`, never off `PromptVersion` (except the existing editor-verbatim check, which stays).
- Test names are `Sentence_case_with_underscores` xUnit facts, matching the existing suites.
- Full suite must be green at the end of every task: `dotnet test Newsroom.slnx` from the repo root (use targeted `--filter` runs mid-task).
- `Facebook:DryRun` defaults ON — nothing in this plan posts to the real page.

---

### Task 1: Migration 0013 — caption + scheduling columns

**Files:**
- Create: `src/Newsroom.Infrastructure/Database/Migrations/0013_facebook_caption_scheduling.sql`

**Interfaces:**
- Consumes: nothing.
- Produces: `dbo.nw_Draft.FacebookCaption NVARCHAR(1200) NULL`, `dbo.nw_Draft.FacebookHashtagsJson NVARCHAR(400) NULL`, `dbo.nw_Draft.ScheduledForUtc DATETIME2 NULL` — used by Tasks 5, 6, 7, 10.

- [ ] **Step 1: Write the migration**

```sql
-- 0013_facebook_caption_scheduling: social-native Facebook posting
-- (docs/superpowers/specs/2026-07-17-facebook-engagement-design.md).
-- FacebookCaption + FacebookHashtagsJson: the AI-written social caption (hook / CTA / hashtags)
-- posted to the page instead of the ALL-CAPS headline + full body; NULL = legacy draft, the old
-- composition applies. ScheduledForUtc: 📅 Насрочи gate — an Approved draft with a future value
-- waits; NULL or past = publish on the next cycle. Single batch, no GO.

ALTER TABLE dbo.nw_Draft ADD
    FacebookCaption      nvarchar(1200) NULL,
    FacebookHashtagsJson nvarchar(400)  NULL,
    ScheduledForUtc      datetime2      NULL;
```

- [ ] **Step 2: Run the migration convention tests**

Run: `dotnet test "src/tests/Newsroom.Infrastructure.Tests" --filter "FullyQualifiedName~Migration" -v minimal`
Expected: PASS (the migration tests validate embedding/naming/single-batch conventions; the file is picked up by the csproj wildcard).

- [ ] **Step 3: Commit**

```bash
git add "src/Newsroom.Infrastructure/Database/Migrations/0013_facebook_caption_scheduling.sql"
git commit -m "feat(db): nw_Draft caption + scheduling columns (migration 0013)"
```

---

### Task 2: Caption fields through the AI parse layer

**Files:**
- Modify: `src/Newsroom.Core/Drafting/DraftContent.cs`
- Modify: `src/Newsroom.Infrastructure/Ai/GeminiDraftingAi.cs` (DraftDto + ParseDraft only — the prompt is Task 4)
- Test: `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiDraftingAiTests.cs`
- Test (compile fix only): `src/tests/Newsroom.Core.Tests/Drafting/DraftValidatorTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `DraftContent` gains two trailing positional parameters: `string FacebookCaption, IReadOnlyList<string> FacebookHashtags` (after `double Confidence`). Missing model output maps to `""` / `[]`, never null. Tasks 3–7 rely on these exact names/types.

- [ ] **Step 1: Extend the test fixture and add failing assertions**

In `GeminiDraftingAiTests.cs`:

(a) Add two fields to `ValidDraftJson` (after `"confidence": 0.85` add a comma, then):

```json
  "facebookCaption": "Земетресение разлюля Югозапада тази сутрин.\n\nТрусът е усетен в Благоевград и района, съобщи БТА. Няма данни за пострадали и щети по сградите.\n\nВие усетихте ли труса? Разкажете ни в коментарите.",
  "facebookHashtags": ["#Благоевград", "#Земетресение"]
```

(b) In `Generate_parses_draft_and_usage_from_clean_json`, after the `Confidence` assertion add:

```csharp
        Assert.StartsWith("Земетресение разлюля Югозапада", content.FacebookCaption);
        Assert.Equal(["#Благоевград", "#Земетресение"], content.FacebookHashtags);
```

(c) In `Generate_tolerates_markdown_fenced_json_and_missing_fields`, after the `Confidence` assertion add:

```csharp
        Assert.Equal("", result.Content.FacebookCaption);
        Assert.Empty(result.Content.FacebookHashtags);
```

(d) Update the positional `Draft()` helper (used by the self-check tests) — append two arguments:

```csharp
    private static DraftContent Draft() => new(
        "МОЩЕН ТРУС РАЗТЪРСИ ЮГОЗАПАДА", null, "Земетресение разтърси региона.", "Общество",
        "Благоевград", ["земетресение"], "Трус в Югозапада", "Земетресение в региона.",
        ["earthquake"], null, [], 0.85, "", []);
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test "src/tests/Newsroom.Infrastructure.Tests" --filter "FullyQualifiedName~GeminiDraftingAiTests" -v minimal`
Expected: FAIL — compile error (`DraftContent` does not take 14 arguments / has no `FacebookCaption`).

- [ ] **Step 3: Extend DraftContent**

`src/Newsroom.Core/Drafting/DraftContent.cs` — append two positional parameters:

```csharp
public sealed record DraftContent(
    string Headline,
    string? Subtitle,
    string BodyMarkdown,
    string Category,
    string? Region,
    IReadOnlyList<string> Tags,
    string SeoTitle,
    string SeoDescription,
    IReadOnlyList<string> ImageSearchQueries,
    string? ImageAltTextBg,
    IReadOnlyList<string> FlaggedClaims,
    double Confidence,
    string FacebookCaption,
    IReadOnlyList<string> FacebookHashtags);
```

- [ ] **Step 4: Extend DraftDto + ParseDraft in GeminiDraftingAi.cs**

`DraftDto` — append two parameters:

```csharp
    private sealed record DraftDto(
        string? Headline,
        string? Subtitle,
        string? BodyMarkdown,
        string? Category,
        string? Region,
        List<string?>? Tags,
        string? SeoTitle,
        string? SeoDescription,
        List<string?>? ImageSearchQueries,
        string? ImageAltTextBg,
        List<string?>? FlaggedClaims,
        double? Confidence,
        string? FacebookCaption,
        List<string?>? FacebookHashtags);
```

`ParseDraft` — the `return new DraftContent(...)` gains two trailing arguments:

```csharp
        return new DraftContent(
            dto.Headline?.Trim() ?? "",
            NullIfWhiteSpace(dto.Subtitle),
            dto.BodyMarkdown?.Trim() ?? "",
            dto.Category?.Trim() ?? "",
            NullIfWhiteSpace(dto.Region),
            CleanList(dto.Tags),
            dto.SeoTitle?.Trim() ?? "",
            dto.SeoDescription?.Trim() ?? "",
            CleanList(dto.ImageSearchQueries),
            NullIfWhiteSpace(dto.ImageAltTextBg),
            CleanList(dto.FlaggedClaims),
            dto.Confidence ?? 0,
            dto.FacebookCaption?.Trim() ?? "",
            CleanList(dto.FacebookHashtags));
```

- [ ] **Step 5: Fix the other DraftContent construction site (compile only)**

`DraftValidatorTests.ValidDraft()` uses named arguments — append (real caption values come in Task 3; a placeholder that long is fine here because validator caption rules don't exist yet):

```csharp
        FlaggedClaims: [],
        Confidence: 0.8,
        FacebookCaption: "",
        FacebookHashtags: []);
```

- [ ] **Step 6: Run to verify green**

Run: `dotnet test "src/tests/Newsroom.Infrastructure.Tests" --filter "FullyQualifiedName~GeminiDraftingAiTests" -v minimal` then `dotnet test Newsroom.slnx -v minimal`
Expected: PASS (all).

- [ ] **Step 7: Commit**

```bash
git add -A src/
git commit -m "feat(ai): facebookCaption + facebookHashtags in the draft output contract"
```

---

### Task 3: DraftValidator caption + hashtag gates

**Files:**
- Modify: `src/Newsroom.Core/Drafting/DraftValidator.cs`
- Test: `src/tests/Newsroom.Core.Tests/Drafting/DraftValidatorTests.cs`

**Interfaces:**
- Consumes: `DraftContent.FacebookCaption` / `.FacebookHashtags` (Task 2).
- Produces: `DraftValidator.Normalize` trims the caption and normalizes hashtags (leading `#`, dedupe, cap 3); `DraftValidator.Validate` adds caption violations. Bounds (spec): caption 200–900 chars, first line ≤ 120, uppercase ratio ≤ 0.6, no `*`/`#` inside the caption; hashtags must match `#дума` (letters/digits only).

- [ ] **Step 1: Write the failing tests**

In `DraftValidatorTests.cs`, first make `ValidDraft()` carry a valid caption. Add a fixture next to `ValidBody`:

```csharp
    /// <summary>~330 chars: short sentence-case hook line, facts paragraph, closing question —
    /// comfortably inside the 200–900 caption bounds.</summary>
    private static readonly string ValidCaption =
        "Общината обяви нови мерки за контрол.\n\n"
        + string.Concat(Enumerable.Repeat(
            "Промените влизат в сила от понеделник и засягат центъра на града. ", 4))
        + "\nКакво мислите за промените?";
```

and change the `ValidDraft()` trailing arguments (from Task 2's placeholders) to:

```csharp
        FlaggedClaims: [],
        Confidence: 0.8,
        FacebookCaption: ValidCaption,
        FacebookHashtags: ["#Благоевград"]);
```

Then add the new facts:

```csharp
    [Fact]
    public void Empty_facebook_caption_is_a_violation()
    {
        var violations = Validate(ValidDraft() with { FacebookCaption = "" });
        Assert.Contains(violations, v => v.Contains("Facebook caption"));
    }

    [Fact]
    public void Overlong_facebook_caption_is_a_violation()
    {
        var violations = Validate(ValidDraft() with { FacebookCaption = new string('а', 950) });
        Assert.Contains(violations, v => v.Contains("Facebook caption"));
    }

    [Fact]
    public void All_caps_facebook_caption_is_a_violation()
    {
        var violations = Validate(ValidDraft() with
        {
            FacebookCaption = ValidCaption.ToUpperInvariant(),
        });
        Assert.Contains(violations, v => v.Contains("uppercase"));
    }

    [Fact]
    public void Overlong_facebook_caption_first_line_is_a_violation()
    {
        var caption = new string('а', 130) + "\n" + new string('б', 200);
        var violations = Validate(ValidDraft() with { FacebookCaption = caption });
        Assert.Contains(violations, v => v.Contains("first line"));
    }

    [Fact]
    public void Markdown_or_hashtag_markers_inside_the_caption_are_a_violation()
    {
        var violations = Validate(ValidDraft() with
        {
            FacebookCaption = ValidCaption + " **важно** #Пирин",
        });
        Assert.Contains(violations, v => v.Contains("marker"));
    }

    [Fact]
    public void Malformed_facebook_hashtag_is_a_violation()
    {
        var violations = Validate(ValidDraft() with { FacebookHashtags = ["#за дръжка"] });
        Assert.Contains(violations, v => v.Contains("hashtag"));
    }

    [Fact]
    public void Normalize_repairs_hashtags_prefix_dedupes_and_caps_at_three()
    {
        var draft = ValidDraft() with
        {
            FacebookCaption = "  " + ValidCaption + "  ",
            FacebookHashtags = ["Благоевград", "#Благоевград", "#Пирин", "", "#Струма", "#Четвърти"],
        };

        var normalized = DraftValidator.Normalize(draft);

        Assert.Equal(ValidCaption, normalized.FacebookCaption);
        Assert.Equal(["#Благоевград", "#Пирин", "#Струма"], normalized.FacebookHashtags);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test "src/tests/Newsroom.Core.Tests" --filter "FullyQualifiedName~DraftValidatorTests" -v minimal`
Expected: FAIL — the new facts fail (no caption rules yet); `Normalize` returns hashtags untouched.

- [ ] **Step 3: Implement in DraftValidator.cs**

Add constants next to the existing ones:

```csharp
    private const int MinFacebookCaptionChars = 200;
    private const int MaxFacebookCaptionChars = 900;
    private const int MaxFacebookCaptionFirstLineChars = 120;
    private const double MaxFacebookCaptionUppercaseRatio = 0.6;
    private const int MaxFacebookHashtags = 3;
```

Extend `Normalize` (caption trim + hashtag repair are cosmetic, same philosophy as the SEO truncation):

```csharp
    public static DraftContent Normalize(DraftContent draft) => draft with
    {
        SeoTitle = TruncateAtWordBoundary(draft.SeoTitle.Trim(), MaxSeoTitleChars),
        SeoDescription = TruncateAtWordBoundary(draft.SeoDescription.Trim(), MaxSeoDescriptionChars),
        FacebookCaption = draft.FacebookCaption.Trim(),
        FacebookHashtags = NormalizeHashtags(draft.FacebookHashtags),
    };

    /// <summary>Repairs safely-fixable hashtag issues: missing leading '#', duplicates (case-
    /// insensitive), blanks, overflow beyond the cap. Malformed characters are NOT repaired —
    /// Validate flags those (a mangled hashtag is a content problem, not a cosmetic one).</summary>
    private static IReadOnlyList<string> NormalizeHashtags(IReadOnlyList<string> hashtags) =>
        hashtags
            .Select(h => h.Trim().TrimStart('#'))
            .Where(h => h.Length > 0)
            .Select(h => "#" + h)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFacebookHashtags)
            .ToList();
```

Append to `Validate` (before `return violations;`):

```csharp
        if (draft.FacebookCaption.Length < MinFacebookCaptionChars)
            violations.Add(
                $"Facebook caption is {draft.FacebookCaption.Length} chars (min {MinFacebookCaptionChars}).");
        else if (draft.FacebookCaption.Length > MaxFacebookCaptionChars)
            violations.Add(
                $"Facebook caption is {draft.FacebookCaption.Length} chars (max {MaxFacebookCaptionChars}).");
        else
        {
            var firstLine = draft.FacebookCaption.AsSpan(0, FirstLineLength(draft.FacebookCaption));
            if (firstLine.Length > MaxFacebookCaptionFirstLineChars)
                violations.Add(
                    $"Facebook caption first line is {firstLine.Length} chars (max {MaxFacebookCaptionFirstLineChars}) — the hook must fit above the fold.");

            if (draft.FacebookCaption.AsSpan().IndexOfAny('*', '#') >= 0)
                violations.Add("Facebook caption contains markdown/hashtag markers (* or #) — hashtags belong in facebookHashtags.");

            var uppercaseRatio = UppercaseLetterRatio(draft.FacebookCaption);
            if (uppercaseRatio > MaxFacebookCaptionUppercaseRatio)
                violations.Add(
                    $"Facebook caption is {uppercaseRatio:P0} uppercase (max {MaxFacebookCaptionUppercaseRatio:P0}) — no ALL CAPS on Facebook.");
        }

        if (draft.FacebookHashtags.Count > MaxFacebookHashtags)
            violations.Add(
                $"Draft has {draft.FacebookHashtags.Count} Facebook hashtags (max {MaxFacebookHashtags}).");
        foreach (var hashtag in draft.FacebookHashtags)
        {
            if (hashtag.Length < 2 || hashtag[0] != '#' || !hashtag[1..].All(char.IsLetterOrDigit))
                violations.Add($"Facebook hashtag '{hashtag}' is malformed (expected #дума, letters/digits only).");
        }
```

Add the two helpers next to `CyrillicLetterRatio`:

```csharp
    private static int FirstLineLength(string text)
    {
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? text.Length : newline;
    }

    /// <summary>Share of uppercase among the cased letters of <paramref name="text"/>
    /// (0 when there are none) — the ALL-CAPS detector for the Facebook caption.</summary>
    private static double UppercaseLetterRatio(string text)
    {
        var cased = 0;
        var upper = 0;
        foreach (var ch in text)
        {
            if (char.IsUpper(ch))
            {
                cased++;
                upper++;
            }
            else if (char.IsLower(ch))
            {
                cased++;
            }
        }
        return cased == 0 ? 0 : (double)upper / cased;
    }
```

(If `IndexOfAny('*', '#')` on a span does not compile on the project's LangVersion, use `draft.FacebookCaption.IndexOfAny(['*', '#']) >= 0`.)

- [ ] **Step 4: Run to verify green**

Run: `dotnet test "src/tests/Newsroom.Core.Tests" --filter "FullyQualifiedName~DraftValidatorTests" -v minimal`
Expected: PASS (all facts, including the pre-existing ones — `ValidDraft()` now passes the caption gates).

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "feat(drafting): validator gates for the Facebook caption and hashtags"
```

---

### Task 4: Prompt asks for the caption; PromptVersion → draft-v2

**Files:**
- Modify: `src/Newsroom.Infrastructure/Ai/GeminiDraftingAi.cs` (`BuildGenerateInstruction`)
- Modify: `src/Newsroom.Worker/Jobs/DraftJob.cs` (`PromptVersion` constant)
- Test: `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiDraftingAiTests.cs`

**Interfaces:**
- Consumes: Task 2's JSON field names (`facebookCaption`, `facebookHashtags`).
- Produces: every new AI draft (including `/new` and ✏️ regenerations) carries a caption; `nw_Draft.PromptVersion` = `"draft-v2"` for traceability.

- [ ] **Step 1: Write the failing test**

Add to `GeminiDraftingAiTests`:

```csharp
    [Fact]
    public async Task Generate_prompt_asks_for_the_facebook_caption_and_hashtags()
    {
        var (client, fake, _) = CreateClient(ValidDraftJson);

        await client.GenerateAsync(Bundle(), null, CancellationToken.None);

        var systemPrompt = fake.LastMessages!.Single(m => m.Role == ChatRole.System).Text;
        Assert.Contains("facebookCaption", systemPrompt);
        Assert.Contains("facebookHashtags", systemPrompt);
        Assert.Contains("БЕЗ главни букви", systemPrompt); // the hook line must not be ALL CAPS
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test "src/tests/Newsroom.Infrastructure.Tests" --filter "FullyQualifiedName~GeminiDraftingAiTests" -v minimal`
Expected: FAIL — `facebookCaption` not in the system prompt.

- [ ] **Step 3: Extend BuildGenerateInstruction**

In the JSON field list of `BuildGenerateInstruction` (after the `"confidence"` bullet) add:

```
        - "facebookCaption": текст за Facebook пост на български, 400-700 знака. Структура:
          първият ред е кука до 100 знака — нормално изречение, БЕЗ главни букви навсякъде;
          после 1-2 кратки абзаца с основните факти; накрая въпрос или лека подкана към
          читателите (напр. „Разкажете ни в коментарите."). Разговорен, но никога жълт тон.
          Без markdown, без хаштагове и без символите * и # в текста.
        - "facebookHashtags": 2-3 хаштага на български за региона и темата
          (напр. "#Благоевград") — само букви и цифри след #
```

- [ ] **Step 4: Bump the prompt version**

In `DraftJob.cs`:

```csharp
    private const string PromptVersion = "draft-v2";
```

- [ ] **Step 5: Run to verify green**

Run: `dotnet test "src/tests/Newsroom.Infrastructure.Tests" --filter "FullyQualifiedName~GeminiDraftingAiTests" -v minimal`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A src/
git commit -m "feat(ai): prompt writes a social-native Facebook caption (draft-v2)"
```

---

### Task 5: Persist the caption (DraftRepository)

**Files:**
- Modify: `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs` (`SaveDraftAsync`, `CompleteRegenerationAsync`)

**Interfaces:**
- Consumes: migration 0013 columns; `DraftContent.FacebookCaption`/`.FacebookHashtags`.
- Produces: `nw_Draft.FacebookCaption` (NULL when the AI produced none — the publish fallback keys off this) and `nw_Draft.FacebookHashtagsJson` (camelCase JSON array, NULL when empty). `CreateManualArticleAsync` (/post) intentionally untouched → columns stay NULL → verbatim path.

- [ ] **Step 1: Extend SaveDraftAsync**

Column list gains `FacebookCaption, FacebookHashtagsJson`, the SELECT gains `@facebookCaption, @facebookHashtagsJson`:

```csharp
            INSERT INTO dbo.nw_Draft
                (TopicId, Version, Status, Headline, Subtitle, BodyMarkdown, Category, Region,
                 TagsJson, SeoTitle, SeoDescription, SourcesJson, FlaggedClaimsJson, Confidence,
                 ImageAltTextBg, PromptVersion, Provider, Model, TokensIn, TokensOut, Cost,
                 FacebookCaption, FacebookHashtagsJson)
            OUTPUT INSERTED.Id
            SELECT @topicId, ISNULL(MAX(d.Version), 0) + 1, @status, @headline, @subtitle,
                   @bodyMarkdown, @category, @region, @tagsJson, @seoTitle, @seoDescription,
                   @sourcesJson, @flaggedClaimsJson, @confidence, @imageAltTextBg,
                   @promptVersion, @provider, @model, @tokensIn, @tokensOut, @cost,
                   @facebookCaption, @facebookHashtagsJson
            FROM dbo.nw_Draft d
            WHERE d.TopicId = @topicId
```

and the anonymous parameter object gains (after `cost = usage.Cost,`):

```csharp
                facebookCaption = string.IsNullOrWhiteSpace(content.FacebookCaption)
                    ? null
                    : Truncate(content.FacebookCaption, 1200),
                facebookHashtagsJson = content.FacebookHashtags.Count == 0
                    ? null
                    : JsonSerializer.Serialize(content.FacebookHashtags, JsonOptions),
```

- [ ] **Step 2: Extend CompleteRegenerationAsync the same way**

The UPDATE's SET list gains (before `Error = NULL`):

```csharp
                FacebookCaption = @facebookCaption, FacebookHashtagsJson = @facebookHashtagsJson,
```

and its parameter object gains the identical `facebookCaption` / `facebookHashtagsJson` entries from Step 1.

- [ ] **Step 3: Build + run the suite**

Run: `dotnet test Newsroom.slnx -v minimal`
Expected: PASS (repositories are live-verified in this repo; no new unit tests for SQL).

- [ ] **Step 4: Commit**

```bash
git add -A src/
git commit -m "feat(drafting): persist the Facebook caption and hashtags with the draft"
```

---

### Task 6: Review card shows the caption

**Files:**
- Modify: `src/Newsroom.Core/Review/DraftReviewView.cs`
- Modify: `src/Newsroom.Core/Review/ReviewMessageRenderer.cs`
- Modify: `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs` (`ViewSelectSql`, `ReviewRow`, `ToView`)
- Test: `src/tests/Newsroom.Core.Tests/Review/ReviewMessageRendererTests.cs`

**Interfaces:**
- Consumes: `nw_Draft.FacebookCaption` / `FacebookHashtagsJson` (Tasks 1, 5).
- Produces: `DraftReviewView` gains two trailing **defaulted** parameters: `string? FacebookCaption = null, IReadOnlyList<string>? FacebookHashtags = null` (defaults keep all existing construction sites compiling). The card renders a `📘 Facebook:` block so the editor reviews exactly what will be posted.

- [ ] **Step 1: Write the failing renderer test**

Add to `ReviewMessageRendererTests.cs` (adapt the view construction to the file's existing helper if one exists — the named-argument form below always works):

```csharp
    [Fact]
    public void Renders_the_facebook_caption_block_when_present()
    {
        var view = new DraftReviewView(
            DraftId: 7, Version: 1, TopicLabel: "Тема", TopicScore: 6.5, SourceCount: 2,
            Headline: "ЗАГЛАВИЕ", Subtitle: null, BodyMarkdown: "Текст на статията.",
            Category: "Общество", Region: "Благоевград", Tags: [],
            Sources: [], FlaggedClaims: [], Confidence: 0.8, Cost: 0.001m, Model: "gemini",
            ImageCount: 0, TelegramMessageId: null, IsManual: false,
            FacebookCaption: "Кука на поста.\n\nОще факти за събитието.\n\nКакво мислите?",
            FacebookHashtags: ["#Благоевград", "#ПределНюз"]);

        var html = ReviewMessageRenderer.RenderHtml(view);

        Assert.Contains("📘 Facebook:", html);
        Assert.Contains("Кука на поста.", html);
        Assert.Contains("#Благоевград #ПределНюз", html);
    }

    [Fact]
    public void Skips_the_facebook_block_when_the_draft_has_no_caption()
    {
        var view = new DraftReviewView(
            DraftId: 7, Version: 1, TopicLabel: "Тема", TopicScore: 6.5, SourceCount: 2,
            Headline: "ЗАГЛАВИЕ", Subtitle: null, BodyMarkdown: "Текст.", Category: "Общество",
            Region: null, Tags: [], Sources: [], FlaggedClaims: [], Confidence: null,
            Cost: 0m, Model: null, ImageCount: 0, TelegramMessageId: null);

        Assert.DoesNotContain("📘 Facebook:", ReviewMessageRenderer.RenderHtml(view));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test "src/tests/Newsroom.Core.Tests" --filter "FullyQualifiedName~ReviewMessageRendererTests" -v minimal`
Expected: FAIL — compile error (`DraftReviewView` has no `FacebookCaption`).

- [ ] **Step 3: Extend DraftReviewView**

```csharp
public sealed record DraftReviewView(
    long DraftId,
    int Version,
    string TopicLabel,
    double TopicScore,
    int SourceCount,
    string Headline,
    string? Subtitle,
    string BodyMarkdown,
    string Category,
    string? Region,
    IReadOnlyList<string> Tags,
    IReadOnlyList<(string Name, string Url)> Sources,
    IReadOnlyList<string> FlaggedClaims,
    double? Confidence,
    decimal Cost,
    string? Model,
    int ImageCount,
    long? TelegramMessageId,
    bool IsManual = false,
    string? FacebookCaption = null,
    IReadOnlyList<string>? FacebookHashtags = null);
```

- [ ] **Step 4: Render the block**

In `ReviewMessageRenderer.RenderHtml`, insert after the `📎 Категория` block (before the sources block):

```csharp
        if (!string.IsNullOrWhiteSpace(v.FacebookCaption))
        {
            html.Append('\n').Append("📘 Facebook:").Append('\n')
                .Append(Escape(v.FacebookCaption)).Append('\n');
            if (v.FacebookHashtags is { Count: > 0 } hashtags)
                html.Append(Escape(string.Join(" ", hashtags))).Append('\n');
        }
```

- [ ] **Step 5: Feed the view from the repository**

In `ReviewRepository.cs`:

(a) `ViewSelectSql` — after `d.TelegramMessageId,` add:

```sql
               d.FacebookCaption, d.FacebookHashtagsJson,
```

(b) `ReviewRow` — append two parameters after `bool IsManual`:

```csharp
        bool IsManual,
        string? FacebookCaption,
        string? FacebookHashtagsJson);
```

(c) `ToView` — append two arguments after `r.IsManual`:

```csharp
        r.IsManual,
        r.FacebookCaption,
        ParseStringList(r.FacebookHashtagsJson));
```

- [ ] **Step 6: Run to verify green**

Run: `dotnet test "src/tests/Newsroom.Core.Tests" --filter "FullyQualifiedName~ReviewMessageRendererTests" -v minimal` then `dotnet test Newsroom.slnx -v minimal`
Expected: PASS (all).

- [ ] **Step 7: Commit**

```bash
git add -A src/
git commit -m "feat(review): review card shows the Facebook caption block"
```

---

### Task 7: FacebookCaption.Compose + publish path uses the caption

**Files:**
- Create: `src/Newsroom.Core/Publishing/FacebookCaption.cs`
- Modify: `src/Newsroom.Infrastructure/Publishing/FacebookPublisher.cs` (message composition)
- Modify: `src/Newsroom.Infrastructure/Repositories/PublishRepository.cs` (`GetApprovedForFacebookAsync`, `GetPendingFacebookAsync`, `GetFacebookPostForDraftAsync` + their row records)
- Test: Create `src/tests/Newsroom.Core.Tests/Publishing/FacebookCaptionTests.cs`
- Test: `src/tests/Newsroom.Infrastructure.Tests/Publishing/FacebookPublisherTests.cs`

**Interfaces:**
- Consumes: `nw_Draft.FacebookCaption`/`FacebookHashtagsJson`; `ManualTopic.EditorPromptVersion`; `FacebookTeaser`.
- Produces: `public static class FacebookCaption { public static string Compose(string caption, IReadOnlyList<string> hashtags); }` in `Newsroom.Core.Publishing`. `FacebookPost` composition rule: caption drafts ship `Headline = ""` and `Teaser = FacebookCaption.Compose(...)`, posted **verbatim** (no `StripMarkdown` — `#` must survive); `FacebookPublisher` skips the headline line when it is blank.

- [ ] **Step 1: Write the failing Compose tests**

`src/tests/Newsroom.Core.Tests/Publishing/FacebookCaptionTests.cs`:

```csharp
using Newsroom.Core.Publishing;

namespace Newsroom.Core.Tests.Publishing;

public class FacebookCaptionTests
{
    [Fact]
    public void Compose_appends_hashtags_after_a_blank_line() =>
        Assert.Equal("Текст на поста.\n\n#Благоевград #ПределНюз",
            FacebookCaption.Compose("Текст на поста.", ["#Благоевград", "#ПределНюз"]));

    [Fact]
    public void Compose_without_hashtags_returns_the_trimmed_caption_alone() =>
        Assert.Equal("Текст на поста.", FacebookCaption.Compose("Текст на поста.\n", []));

    [Fact]
    public void Compose_skips_blank_hashtags() =>
        Assert.Equal("Текст.\n\n#Пирин", FacebookCaption.Compose("Текст.", ["", "#Пирин", " "]));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test "src/tests/Newsroom.Core.Tests" --filter "FullyQualifiedName~FacebookCaptionTests" -v minimal`
Expected: FAIL — `FacebookCaption` does not exist.

- [ ] **Step 3: Implement FacebookCaption.Compose**

`src/Newsroom.Core/Publishing/FacebookCaption.cs`:

```csharp
namespace Newsroom.Core.Publishing;

/// <summary>
/// Composes the final Facebook message for a caption-carrying draft
/// (docs/superpowers/specs/2026-07-17-facebook-engagement-design.md): the AI-written social
/// caption followed by a blank line and the hashtags. Posted verbatim — deliberately NOT run
/// through <see cref="FacebookTeaser.StripMarkdown"/>, which deletes '#' (the validator already
/// guarantees the caption itself carries no markdown). Pure — no I/O, no configuration.
/// </summary>
public static class FacebookCaption
{
    public static string Compose(string caption, IReadOnlyList<string> hashtags)
    {
        var text = caption.Trim();
        var tags = string.Join(' ', hashtags.Where(h => !string.IsNullOrWhiteSpace(h)));
        return tags.Length == 0 ? text : $"{text}\n\n{tags}";
    }
}
```

Run: `dotnet test "src/tests/Newsroom.Core.Tests" --filter "FullyQualifiedName~FacebookCaptionTests" -v minimal`
Expected: PASS.

- [ ] **Step 4: Write the failing publisher test (blank headline)**

Add to `FacebookPublisherTests.cs`:

```csharp
    [Fact]
    public async Task Blank_headline_posts_the_teaser_without_leading_blank_lines()
    {
        var (publisher, handler) = CreatePublisher(request => request.Path == FeedPath
            ? Json(HttpStatusCode.OK, PostedJson)
            : Json(HttpStatusCode.OK, PermalinkJson));

        await publisher.PublishAsync(
            Post() with { Headline = "", Teaser = "Кука на поста.\n\n#Пирин", ArticleUrl = "" },
            CancellationToken.None);

        var feed = Assert.Single(handler.Requests, r => r.Path == FeedPath);
        Assert.Equal("Кука на поста.\n\n#Пирин", ParseForm(feed.Body)["message"]);
    }
```

Run: `dotnet test "src/tests/Newsroom.Infrastructure.Tests" --filter "FullyQualifiedName~FacebookPublisherTests" -v minimal`
Expected: FAIL — message arrives as `"\n\nКука на поста.\n\n#Пирин"`.

- [ ] **Step 5: Fix the message composition in FacebookPublisher.PublishAsync**

Replace line `var message = $"{post.Headline}\n\n{post.Teaser}";` with:

```csharp
        // Caption-carrying drafts ship Headline = "" — the caption's first line is the hook and
        // must open the post; legacy drafts keep the headline + body layout.
        var message = string.IsNullOrWhiteSpace(post.Headline)
            ? post.Teaser
            : $"{post.Headline}\n\n{post.Teaser}";
```

Run the publisher tests again. Expected: PASS (including the pre-existing ones).

- [ ] **Step 6: Prefer the caption in PublishRepository**

(a) `GetApprovedForFacebookAsync` — SELECT gains the two columns (after `d.PromptVersion,`):

```sql
                   d.FacebookCaption, d.FacebookHashtagsJson,
```

`FacebookApprovedRow` gains matching members:

```csharp
    private sealed record FacebookApprovedRow(
        long DraftId,
        string Headline,
        string BodyMarkdown,
        string? PromptVersion,
        string? FacebookCaption,
        string? FacebookHashtagsJson,
        string? ImageKind,
        string? ImageUrl);
```

Replace the final `return rows.Select(...)` (keep the existing explanatory comment above it and extend it):

```csharp
        // Facebook-only: the post IS the article (no site link to carry the full read).
        // Priority: (1) editor-authored drafts (/post — PromptVersion "editor-v1") publish the
        // body verbatim (owner decision 2026-07-13); (2) caption-carrying AI drafts (draft-v2+)
        // post the social caption + hashtags verbatim with no headline — the caption's first
        // line is the hook; (3) legacy AI drafts without a caption keep the old
        // headline + ComposeFullBody layout.
        return rows.Select(r =>
        {
            var image = ToFacebookImage(r.ImageKind, r.ImageUrl);
            if (r.PromptVersion == ManualTopic.EditorPromptVersion)
                return new FacebookPost(r.DraftId, r.Headline, r.BodyMarkdown, ArticleUrl: "", image);
            if (!string.IsNullOrWhiteSpace(r.FacebookCaption))
                return new FacebookPost(
                    r.DraftId, Headline: "",
                    FacebookCaption.Compose(r.FacebookCaption, ParseStringList(r.FacebookHashtagsJson)),
                    ArticleUrl: "", image);
            return new FacebookPost(
                r.DraftId, r.Headline, FacebookTeaser.ComposeFullBody(r.BodyMarkdown),
                ArticleUrl: "", image);
        }).ToList();
```

(b) `GetPendingFacebookAsync` (site mode — same caption preference so flipping `Publishing:FacebookOnly` off keeps the social caption as the link-post message) — SELECT gains (after `d.SeoDescription,` on its line):

```sql
                   d.FacebookCaption, d.FacebookHashtagsJson,
```

`FacebookRow` gains matching members:

```csharp
    private sealed record FacebookRow(
        long DraftId,
        string Headline,
        string? SeoDescription,
        string BodyMarkdown,
        string? FacebookCaption,
        string? FacebookHashtagsJson,
        string ArticleUrl);
```

and its projection becomes:

```csharp
        return rows.Select(r => string.IsNullOrWhiteSpace(r.FacebookCaption)
            ? new FacebookPost(
                r.DraftId, r.Headline, FacebookTeaser.Compose(r.SeoDescription, r.BodyMarkdown),
                r.ArticleUrl)
            : new FacebookPost(
                r.DraftId, Headline: "",
                FacebookCaption.Compose(r.FacebookCaption, ParseStringList(r.FacebookHashtagsJson)),
                r.ArticleUrl)).ToList();
```

(c) `GetFacebookPostForDraftAsync` (manual test hook — keep it representative): SELECT gains `d.FacebookCaption, d.FacebookHashtagsJson,` after `d.SeoDescription,`, and the projection becomes:

```csharp
        return row is null
            ? null
            : string.IsNullOrWhiteSpace(row.FacebookCaption)
                ? new FacebookPost(row.DraftId, row.Headline,
                    FacebookTeaser.Compose(row.SeoDescription, row.BodyMarkdown), row.ArticleUrl)
                : new FacebookPost(row.DraftId, Headline: "",
                    FacebookCaption.Compose(row.FacebookCaption, ParseStringList(row.FacebookHashtagsJson)),
                    row.ArticleUrl);
```

- [ ] **Step 7: Run the full suite**

Run: `dotnet test Newsroom.slnx -v minimal`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add -A src/
git commit -m "feat(publishing): Facebook posts use the social caption when present"
```

---

### Task 8: PublishSlotSuggester (pure Core)

**Files:**
- Create: `src/Newsroom.Core/Publishing/PublishSlotSuggester.cs`
- Test: Create `src/tests/Newsroom.Core.Tests/Publishing/PublishSlotSuggesterTests.cs`

**Interfaces:**
- Consumes: nothing (pure).
- Produces:

```csharp
public sealed record PublishSlotOptions(
    IReadOnlyList<(TimeSpan Start, TimeSpan End)> Windows,
    TimeSpan MinGap,
    int MaxPerDay,
    TimeSpan Lead);

public static class PublishSlotSuggester
{
    public static DateTime Suggest(
        DateTime nowLocal, PublishSlotOptions options, IReadOnlyList<DateTime> commitmentsLocal);
}
```

All times are **local** (the caller converts; matches the `Digest:LocalTime` convention). Task 11 calls `Suggest`.

- [ ] **Step 1: Write the failing tests**

`src/tests/Newsroom.Core.Tests/Publishing/PublishSlotSuggesterTests.cs`:

```csharp
using Newsroom.Core.Publishing;

namespace Newsroom.Core.Tests.Publishing;

public class PublishSlotSuggesterTests
{
    private static PublishSlotOptions Options(int minGapMinutes = 90, int maxPerDay = 5, int leadMinutes = 5) => new(
        Windows:
        [
            (new TimeSpan(7, 30, 0), new TimeSpan(9, 30, 0)),
            (new TimeSpan(12, 0, 0), new TimeSpan(13, 30, 0)),
            (new TimeSpan(17, 30, 0), new TimeSpan(21, 30, 0)),
        ],
        MinGap: TimeSpan.FromMinutes(minGapMinutes),
        MaxPerDay: maxPerDay,
        Lead: TimeSpan.FromMinutes(leadMinutes));

    private static readonly DateTime Morning = new(2026, 7, 17, 8, 0, 0); // inside 07:30–09:30

    [Fact]
    public void Inside_a_window_with_no_commitments_suggests_now_plus_lead()
    {
        var slot = PublishSlotSuggester.Suggest(Morning, Options(), []);
        Assert.Equal(new DateTime(2026, 7, 17, 8, 5, 0), slot);
    }

    [Fact]
    public void Between_windows_suggests_the_next_window_start()
    {
        var slot = PublishSlotSuggester.Suggest(new DateTime(2026, 7, 17, 10, 0, 0), Options(), []);
        Assert.Equal(new DateTime(2026, 7, 17, 12, 0, 0), slot);
    }

    [Fact]
    public void A_recent_post_pushes_the_slot_by_the_minimum_gap()
    {
        var lastPost = new DateTime(2026, 7, 17, 7, 45, 0);
        var slot = PublishSlotSuggester.Suggest(Morning, Options(), [lastPost]);
        Assert.Equal(new DateTime(2026, 7, 17, 9, 15, 0), slot); // 07:45 + 90 min
    }

    [Fact]
    public void A_gap_conflict_that_overruns_the_window_falls_to_the_next_window()
    {
        var lastPost = new DateTime(2026, 7, 17, 8, 30, 0);
        var slot = PublishSlotSuggester.Suggest(Morning, Options(), [lastPost]);
        Assert.Equal(new DateTime(2026, 7, 17, 12, 0, 0), slot); // 08:30+90 = 10:00 > 09:30 → lunch
    }

    [Fact]
    public void Future_scheduled_posts_also_repel_the_slot()
    {
        var scheduled = new DateTime(2026, 7, 17, 12, 30, 0);
        var slot = PublishSlotSuggester.Suggest(
            new DateTime(2026, 7, 17, 11, 50, 0), Options(), [scheduled]);
        Assert.Equal(new DateTime(2026, 7, 17, 17, 30, 0), slot); // 12:00→14:00 overruns lunch → evening
    }

    [Fact]
    public void A_full_day_rolls_to_the_next_days_first_window()
    {
        var commitments = Enumerable.Range(0, 5)
            .Select(i => new DateTime(2026, 7, 17, 18, 0, 0).AddMinutes(-i))
            .ToList();
        var slot = PublishSlotSuggester.Suggest(Morning, Options(maxPerDay: 5), commitments);
        Assert.Equal(new DateTime(2026, 7, 18, 7, 30, 0), slot);
    }

    [Fact]
    public void After_the_last_window_suggests_tomorrow_morning()
    {
        var slot = PublishSlotSuggester.Suggest(new DateTime(2026, 7, 17, 22, 0, 0), Options(), []);
        Assert.Equal(new DateTime(2026, 7, 18, 7, 30, 0), slot);
    }

    [Fact]
    public void No_windows_falls_back_to_now_plus_lead()
    {
        var slot = PublishSlotSuggester.Suggest(Morning, Options() with { Windows = [] }, []);
        Assert.Equal(Morning.AddMinutes(5), slot);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test "src/tests/Newsroom.Core.Tests" --filter "FullyQualifiedName~PublishSlotSuggesterTests" -v minimal`
Expected: FAIL — compile error, `PublishSlotSuggester` does not exist.

- [ ] **Step 3: Implement**

`src/Newsroom.Core/Publishing/PublishSlotSuggester.cs`:

```csharp
namespace Newsroom.Core.Publishing;

/// <summary>Inputs for <see cref="PublishSlotSuggester"/>: engagement windows as local
/// time-of-day ranges, the minimum spacing between page posts, the per-local-day cap and the
/// minimum lead from "now". Parsed from Facebook:Schedule by FacebookScheduleOptions.</summary>
public sealed record PublishSlotOptions(
    IReadOnlyList<(TimeSpan Start, TimeSpan End)> Windows,
    TimeSpan MinGap,
    int MaxPerDay,
    TimeSpan Lead);

/// <summary>
/// Suggests the best next Facebook publish slot
/// (docs/superpowers/specs/2026-07-17-facebook-engagement-design.md): the earliest time
/// ≥ now + Lead that falls inside an engagement window, keeps MinGap from every existing
/// commitment (published or scheduled posts) and lands on a local day with fewer than MaxPerDay
/// commitments. Everything is LOCAL time (the caller converts, matching Digest:LocalTime);
/// heuristic v1 — the windows become data-driven when smart insights land (roadmap). Pure.
/// </summary>
public static class PublishSlotSuggester
{
    private const int MaxDaysAhead = 7;

    public static DateTime Suggest(
        DateTime nowLocal, PublishSlotOptions options, IReadOnlyList<DateTime> commitmentsLocal)
    {
        var earliest = nowLocal + options.Lead;
        var windows = options.Windows.OrderBy(w => w.Start).ToList();

        for (var day = 0; day <= MaxDaysAhead; day++)
        {
            var date = nowLocal.Date.AddDays(day);
            if (commitmentsLocal.Count(c => c.Date == date) >= options.MaxPerDay)
                continue;

            foreach (var (start, end) in windows)
            {
                var candidate = Max(date + start, earliest);
                // Push past every commitment closer than MinGap; forward-only, so this terminates.
                bool moved;
                do
                {
                    moved = false;
                    foreach (var commitment in commitmentsLocal)
                    {
                        if ((candidate - commitment).Duration() < options.MinGap)
                        {
                            candidate = commitment + options.MinGap;
                            moved = true;
                        }
                    }
                }
                while (moved);

                if (candidate <= date + end)
                    return candidate;
            }
        }

        // Pathological config (no windows / a week fully booked): the editor still gets a slot.
        return earliest;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
```

- [ ] **Step 4: Run to verify green**

Run: `dotnet test "src/tests/Newsroom.Core.Tests" --filter "FullyQualifiedName~PublishSlotSuggesterTests" -v minimal`
Expected: PASS (8 facts).

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "feat(publishing): pure best-slot suggester for scheduled Facebook posts"
```

---

### Task 9: FacebookScheduleOptions (config binding + window parsing)

**Files:**
- Create: `src/Newsroom.Infrastructure/Publishing/FacebookScheduleOptions.cs`
- Modify: `src/Newsroom.Worker/appsettings.json` (`Facebook` section)
- Test: Create `src/tests/Newsroom.Infrastructure.Tests/Publishing/FacebookScheduleOptionsTests.cs`

**Interfaces:**
- Consumes: `PublishSlotOptions` (Task 8).
- Produces: `FacebookScheduleOptions.From(IConfiguration)` binding `Facebook:Schedule:*` with defaults Windows `["07:30-09:30","12:00-13:30","17:30-21:30"]`, MinGapMinutes 90, MaxPerDay 5, LeadMinutes 5; `.ToSlotOptions()` → parsed `PublishSlotOptions`. No `Program.cs` registration — TelegramJob reads it via `From(configuration)`, the same pattern as `TelegramOptions` (Task 11).

- [ ] **Step 1: Write the failing tests**

`src/tests/Newsroom.Infrastructure.Tests/Publishing/FacebookScheduleOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;

using Newsroom.Infrastructure.Publishing;

namespace Newsroom.Infrastructure.Tests.Publishing;

public class FacebookScheduleOptionsTests
{
    private static IConfiguration Config(params KeyValuePair<string, string?>[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Defaults_apply_without_configuration()
    {
        var options = FacebookScheduleOptions.From(Config());

        Assert.Equal(90, options.MinGapMinutes);
        Assert.Equal(5, options.MaxPerDay);
        Assert.Equal(5, options.LeadMinutes);

        var slots = options.ToSlotOptions();
        Assert.Equal(3, slots.Windows.Count);
        Assert.Equal(new TimeSpan(7, 30, 0), slots.Windows[0].Start);
        Assert.Equal(new TimeSpan(21, 30, 0), slots.Windows[2].End);
        Assert.Equal(TimeSpan.FromMinutes(90), slots.MinGap);
    }

    [Fact]
    public void Configured_windows_and_numbers_bind()
    {
        var options = FacebookScheduleOptions.From(Config(
            new("Facebook:Schedule:Windows:0", "10:00-11:00"),
            new("Facebook:Schedule:MinGapMinutes", "45"),
            new("Facebook:Schedule:MaxPerDay", "2")));

        Assert.Equal(45, options.MinGapMinutes);
        Assert.Equal(2, options.MaxPerDay);
        var window = Assert.Single(options.ToSlotOptions().Windows);
        Assert.Equal(new TimeSpan(10, 0, 0), window.Start);
        Assert.Equal(new TimeSpan(11, 0, 0), window.End);
    }

    [Fact]
    public void Malformed_windows_fall_back_to_the_defaults()
    {
        var options = FacebookScheduleOptions.From(Config(
            new("Facebook:Schedule:Windows:0", "banana"),
            new("Facebook:Schedule:Windows:1", "25:00-26:00"),
            new("Facebook:Schedule:Windows:2", "13:00-12:00"))); // start after end

        Assert.Equal(3, options.ToSlotOptions().Windows.Count); // the defaults
        Assert.Equal(new TimeSpan(7, 30, 0), options.ToSlotOptions().Windows[0].Start);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test "src/tests/Newsroom.Infrastructure.Tests" --filter "FullyQualifiedName~FacebookScheduleOptionsTests" -v minimal`
Expected: FAIL — compile error, `FacebookScheduleOptions` does not exist.

- [ ] **Step 3: Implement**

`src/Newsroom.Infrastructure/Publishing/FacebookScheduleOptions.cs`:

```csharp
using System.Globalization;

using Microsoft.Extensions.Configuration;

using Newsroom.Core.Publishing;

namespace Newsroom.Infrastructure.Publishing;

/// <summary>
/// Settings for suggested-time Facebook scheduling (Facebook:Schedule,
/// docs/superpowers/specs/2026-07-17-facebook-engagement-design.md): engagement windows as
/// "HH:mm-HH:mm" LOCAL-time ranges (the Digest:LocalTime convention), the minimum gap between
/// page posts, the per-day cap and the minimum lead. Read via <see cref="From"/> by the job that
/// needs it (like TelegramOptions) — no DI registration. Heuristic v1 defaults; data-driven
/// windows arrive with smart insights (docs/10-roadmap.md backlog).
/// </summary>
public sealed record FacebookScheduleOptions
{
    public static readonly string[] DefaultWindows = ["07:30-09:30", "12:00-13:30", "17:30-21:30"];

    public IReadOnlyList<string> Windows { get; init; } = DefaultWindows;
    public int MinGapMinutes { get; init; } = 90;
    public int MaxPerDay { get; init; } = 5;
    public int LeadMinutes { get; init; } = 5;

    public static FacebookScheduleOptions From(IConfiguration configuration) => new()
    {
        Windows = configuration.GetSection("Facebook:Schedule:Windows").Get<string[]>()
            is { Length: > 0 } windows ? windows : DefaultWindows,
        MinGapMinutes = configuration.GetValue("Facebook:Schedule:MinGapMinutes", 90),
        MaxPerDay = configuration.GetValue("Facebook:Schedule:MaxPerDay", 5),
        LeadMinutes = configuration.GetValue("Facebook:Schedule:LeadMinutes", 5),
    };

    /// <summary>Parsed windows for the pure suggester. Malformed entries are skipped and when
    /// none parse the defaults apply — a broken config line must not kill scheduling (mirrors
    /// DailyDigestJob's forgiving Digest:LocalTime parse).</summary>
    public PublishSlotOptions ToSlotOptions()
    {
        var windows = ParseWindows(Windows);
        if (windows.Count == 0)
            windows = ParseWindows(DefaultWindows);
        return new PublishSlotOptions(
            windows,
            TimeSpan.FromMinutes(MinGapMinutes),
            MaxPerDay,
            TimeSpan.FromMinutes(LeadMinutes));
    }

    private static List<(TimeSpan Start, TimeSpan End)> ParseWindows(IReadOnlyList<string> raw)
    {
        var result = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var window in raw)
        {
            var parts = window.Split('-', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && TimeOnly.TryParseExact(
                    parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                && TimeOnly.TryParseExact(
                    parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                && start < end)
            {
                result.Add((start.ToTimeSpan(), end.ToTimeSpan()));
            }
        }
        return result;
    }
}
```

- [ ] **Step 4: Add the defaults to appsettings.json**

Inside the existing `"Facebook"` object (after `"MaxAttempts": 3` add a comma, then):

```json
    "Schedule": {
      "Windows": ["07:30-09:30", "12:00-13:30", "17:30-21:30"],
      "MinGapMinutes": 90,
      "MaxPerDay": 5,
      "LeadMinutes": 5
    }
```

- [ ] **Step 5: Run to verify green**

Run: `dotnet test "src/tests/Newsroom.Infrastructure.Tests" --filter "FullyQualifiedName~FacebookScheduleOptionsTests" -v minimal`
Expected: PASS (3 facts).

- [ ] **Step 6: Commit**

```bash
git add -A src/
git commit -m "feat(publishing): Facebook:Schedule options with parsed engagement windows"
```

---

### Task 10: Scheduling repository methods + publish gate

**Files:**
- Modify: `src/Newsroom.Core/Review/Interfaces.cs` (`IReviewRepository`)
- Modify: `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs`
- Modify: `src/Newsroom.Infrastructure/Repositories/PublishRepository.cs` (WHERE clauses of both FB queries)

**Interfaces:**
- Consumes: `nw_Draft.ScheduledForUtc` (Task 1); `nw_PublishRecord.AtUtc`; `PublishDestinations.Facebook`.
- Produces (on `IReviewRepository`; Task 11 calls all three):

```csharp
Task<bool> TryScheduleAsync(long draftId, DateTime scheduledForUtc, long userId, string? userName, CancellationToken ct);
Task<bool> TryUnscheduleAsync(long draftId, long userId, string? userName, CancellationToken ct);
Task<IReadOnlyList<DateTime>> GetFacebookCommitmentsUtcAsync(DateTime fromUtc, CancellationToken ct);
```

`nw_ReviewAction.Action` values: `'Scheduled'` (comment = slot, ISO-8601 UTC) and `'ScheduleOverridden'`.

- [ ] **Step 1: Add the interface members**

In `IReviewRepository` (after `TryStartRegenerationAsync`):

```csharp
    /// <summary>📅: PendingReview → Approved with ScheduledForUtc set — the publish gate holds
    /// the draft until the slot arrives. Writes the 'Scheduled' nw_ReviewAction (comment = the
    /// slot, ISO-8601 UTC) in the same transaction. False when the draft is not PendingReview.</summary>
    Task<bool> TryScheduleAsync(
        long draftId, DateTime scheduledForUtc, long userId, string? userName, CancellationToken ct);

    /// <summary>✅ on an already-scheduled draft: clears ScheduledForUtc so the next publish
    /// cycle picks the draft up immediately. Guarded on Approved + a schedule being present;
    /// false otherwise (not scheduled, already published, unknown). Writes 'ScheduleOverridden'.</summary>
    Task<bool> TryUnscheduleAsync(long draftId, long userId, string? userName, CancellationToken ct);

    /// <summary>Existing Facebook commitments for the slot suggester: UTC times of Succeeded
    /// 'facebook' publish records since <paramref name="fromUtc"/> plus every ScheduledForUtc
    /// (≥ fromUtc) still pending on an Approved draft.</summary>
    Task<IReadOnlyList<DateTime>> GetFacebookCommitmentsUtcAsync(DateTime fromUtc, CancellationToken ct);
```

- [ ] **Step 2: Implement in ReviewRepository**

Add `using Newsroom.Core.Publishing;` to the file's usings, and a constant next to the existing ones:

```csharp
    /// <summary>Mirrors PublishRepository's nw_PublishRecord.Status value.</summary>
    private const string PublishSucceededStatus = "Succeeded";
```

Add the three methods (next to `TryStartRegenerationAsync`):

```csharp
    public async Task<bool> TryScheduleAsync(
        long draftId, DateTime scheduledForUtc, long userId, string? userName, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        var rows = await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET Status = @approvedStatus, ScheduledForUtc = @scheduledForUtc,
                UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @draftId AND Status = @pendingStatus
            """,
            new
            {
                draftId,
                scheduledForUtc,
                approvedStatus = nameof(DraftStatus.Approved),
                pendingStatus = nameof(DraftStatus.PendingReview),
            },
            transaction);
        if (rows == 0)
            return false; // not PendingReview (double-tap or stale button); transaction rolls back

        await InsertReviewActionAsync(connection, transaction, draftId, userId, userName,
            "Scheduled", scheduledForUtc.ToString("O", CultureInfo.InvariantCulture));

        transaction.Commit();
        return true;
    }

    public async Task<bool> TryUnscheduleAsync(
        long draftId, long userId, string? userName, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        var rows = await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET ScheduledForUtc = NULL, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @draftId AND Status = @approvedStatus AND ScheduledForUtc IS NOT NULL
            """,
            new { draftId, approvedStatus = nameof(DraftStatus.Approved) },
            transaction);
        if (rows == 0)
            return false; // not scheduled (or already published); transaction rolls back

        await InsertReviewActionAsync(connection, transaction, draftId, userId, userName,
            "ScheduleOverridden", comment: null);

        transaction.Commit();
        return true;
    }

    public async Task<IReadOnlyList<DateTime>> GetFacebookCommitmentsUtcAsync(
        DateTime fromUtc, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<DateTime>(
            """
            SELECT p.AtUtc FROM dbo.nw_PublishRecord p
            WHERE p.Destination = @facebook AND p.Status = @succeededStatus AND p.AtUtc >= @fromUtc
            UNION ALL
            SELECT d.ScheduledForUtc FROM dbo.nw_Draft d
            WHERE d.Status = @approvedStatus AND d.ScheduledForUtc IS NOT NULL
              AND d.ScheduledForUtc >= @fromUtc
            """,
            new
            {
                fromUtc,
                facebook = PublishDestinations.Facebook,
                succeededStatus = PublishSucceededStatus,
                approvedStatus = nameof(DraftStatus.Approved),
            });
        return rows.ToList();
    }
```

- [ ] **Step 3: Add the publish gate to both Facebook queries**

In `PublishRepository.cs`, in **both** `GetApprovedForFacebookAsync` and `GetPendingFacebookAsync`, extend the WHERE clause (after the failed-attempts condition, before `ORDER BY d.Id`):

```sql
              AND (d.ScheduledForUtc IS NULL OR d.ScheduledForUtc <= SYSUTCDATETIME())
```

(Editor `/post` drafts never get a schedule unless the editor presses 📅, so they stay instant; the Umbraco leg — `GetApprovedUnpublishedAsync` — is untouched: scheduling is a Facebook-mode feature per the spec.)

- [ ] **Step 4: Build + run the suite**

Run: `dotnet test Newsroom.slnx -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "feat(publishing): ScheduledForUtc gate + schedule/unschedule transitions"
```

---

### Task 11: Telegram — 📅 Насрочи button, callback, approve override

**Files:**
- Modify: `src/Newsroom.Core/Review/ReviewCommand.cs`
- Modify: `src/Newsroom.Core/Review/ReviewUpdateRouter.cs`
- Modify: `src/Newsroom.Core/Review/Interfaces.cs` (`ITelegramGateway.SendHtmlAsync` signature)
- Modify: `src/Newsroom.Infrastructure/Review/TelegramGateway.cs`
- Modify: `src/Newsroom.Worker/Jobs/TelegramJob.cs`
- Modify (call sites of `SendHtmlAsync`, pass `scheduleButtonLabel: null`): `src/Newsroom.Worker/Jobs/PublishJob.cs` (2×), `src/Newsroom.Worker/Jobs/DailyDigestJob.cs` (1×), `src/Newsroom.Worker/Jobs/WatchdogJob.cs` (1×), `src/Newsroom.Worker/Jobs/FacebookTestPostService.cs` (1×)
- Test: `src/tests/Newsroom.Core.Tests/Review/ReviewUpdateRouterTests.cs`

**Interfaces:**
- Consumes: `TryScheduleAsync`/`TryUnscheduleAsync`/`GetFacebookCommitmentsUtcAsync` (Task 10), `PublishSlotSuggester.Suggest` (Task 8), `FacebookScheduleOptions` (Task 9).
- Produces: `ScheduleDraft(long DraftId) : ReviewCommand`; callback verb `schedule`; `ITelegramGateway.SendHtmlAsync(long chatId, string html, bool withReviewButtons, long? draftIdForButtons, string? scheduleButtonLabel, CancellationToken ct)` — a non-null label adds a second keyboard row `[{label}]` → `schedule:{draftId}`.

- [ ] **Step 1: Write the failing router test**

Add to `ReviewUpdateRouterTests.cs` (mirror the file's existing approve-callback test helpers; the raw construction below works regardless — `TgCallback(UpdateId, CallbackId, UserId, UserName, ChatId, MessageId, Data)`):

```csharp
    [Fact]
    public void Schedule_callback_routes_to_ScheduleDraft()
    {
        var callback = new TgCallback(1, "cb-1", 100, "editor", 555, 42, "schedule:17");

        var command = ReviewUpdateRouter.RouteCallback(callback, new HashSet<long> { 100 }, 555);

        var schedule = Assert.IsType<ScheduleDraft>(command);
        Assert.Equal(17, schedule.DraftId);
    }
```

(If the test class already has `AllowedUsers`/chat-id constants and a callback factory, use those instead of the literals.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test "src/tests/Newsroom.Core.Tests" --filter "FullyQualifiedName~ReviewUpdateRouterTests" -v minimal`
Expected: FAIL — compile error, `ScheduleDraft` does not exist.

- [ ] **Step 3: Add the command + route**

`ReviewCommand.cs` (after `CycleImage`):

```csharp
/// <summary>📅 pressed on a review card: approve the draft gated on the suggested publish slot
/// (recomputed at press time — card labels go stale). ✅ stays the immediate path.</summary>
public sealed record ScheduleDraft(long DraftId) : ReviewCommand;
```

`ReviewUpdateRouter.RouteCallback` switch — add before the discard arm:

```csharp
            "schedule" => new ScheduleDraft(draftId),
```

Run the router tests again. Expected: PASS.

- [ ] **Step 4: Extend the gateway seam**

`ITelegramGateway.SendHtmlAsync` in `Interfaces.cs` becomes:

```csharp
    /// <summary>Sends an HTML message (link previews off). With <paramref name="withReviewButtons"/>
    /// the review keyboard (✅/✏️/❌) carrying <paramref name="draftIdForButtons"/> is attached;
    /// a non-null <paramref name="scheduleButtonLabel"/> adds a second row with the 📅 button
    /// ("schedule:{draftId}") — the label carries the suggested slot and is advisory only.</summary>
    /// <returns>The Telegram message id.</returns>
    Task<long> SendHtmlAsync(
        long chatId, string html, bool withReviewButtons, long? draftIdForButtons,
        string? scheduleButtonLabel, CancellationToken ct);
```

`TelegramGateway.SendHtmlAsync` becomes:

```csharp
    public async Task<long> SendHtmlAsync(
        long chatId, string html, bool withReviewButtons, long? draftIdForButtons,
        string? scheduleButtonLabel, CancellationToken ct)
    {
        InlineKeyboardMarkup? keyboard = null;
        if (withReviewButtons && draftIdForButtons is { } draftId)
        {
            var rows = new List<InlineKeyboardButton[]>
            {
                [
                    InlineKeyboardButton.WithCallbackData("✅ Одобри", $"approve:{draftId}"),
                    InlineKeyboardButton.WithCallbackData("✏️ Промени", $"changes:{draftId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Откажи", $"reject:{draftId}"),
                ],
            };
            if (scheduleButtonLabel is not null)
                rows.Add([InlineKeyboardButton.WithCallbackData(scheduleButtonLabel, $"schedule:{draftId}")]);
            keyboard = new InlineKeyboardMarkup(rows);
        }

        var message = await bot.SendMessage(
            chatId,
            html,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            linkPreviewOptions: NoPreview,
            cancellationToken: ct);
        return message.MessageId;
    }
```

- [ ] **Step 5: Update every non-card call site mechanically**

Pass `scheduleButtonLabel: null` as the new fifth argument at: `TelegramJob.cs:153` (regen-failure notice), `TelegramJob.SendTextAsync` (line ~520), `PublishJob.cs:327` and `:370`, `DailyDigestJob.cs:69`, `WatchdogJob.cs:93`, `FacebookTestPostService.cs:79`. Verify none is missed:

Run: `dotnet build Newsroom.slnx -v minimal` — expected: only `TelegramJob.DispatchPendingAsync` (line ~104) still fails; fix it in the next step.

- [ ] **Step 6: Wire the suggestion + schedule flow in TelegramJob**

(a) Add usings: `using System.Globalization;` and `using Newsroom.Core.Publishing;` and `using Newsroom.Infrastructure.Publishing;`.

(b) In `DispatchPendingAsync`, compute the label once per cycle and pass it to the card send:

```csharp
        var pending = await reviews.GetUnsentPendingReviewsAsync(options.MaxSendPerCycle, ct);
        var scheduleLabel = pending.Count > 0 ? await BuildScheduleLabelAsync(ct) : null;
        foreach (var view in pending)
        {
            ct.ThrowIfCancellationRequested();
            var html = ReviewMessageRenderer.RenderHtml(view);
            var messageId = await gateway.Value.SendHtmlAsync(
                options.ReviewChatId, html, withReviewButtons: true, view.DraftId, scheduleLabel, ct);
            await reviews.SetTelegramMessageIdAsync(view.DraftId, messageId, ct);
            logger.LogInformation("📨 Draft {DraftId} v{Version} posted for review (message {MessageId})",
                view.DraftId, view.Version, messageId);
        }
```

(c) Add the suggestion helpers (near `ResolveEditorUploadDir`):

```csharp
    /// <summary>Label for the 📅 button ("📅 Насрочи 17:30" / "…утре 08:15"). Advisory — the
    /// slot is recomputed at press time. Best-effort: a failure falls back to a bare label.</summary>
    private async Task<string> BuildScheduleLabelAsync(CancellationToken ct)
    {
        try
        {
            return $"📅 Насрочи {FormatSlot(await SuggestSlotAsync(ct))}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not compute the suggested publish slot for the card button");
            return "📅 Насрочи";
        }
    }

    /// <summary>The suggested publish slot in LOCAL time (Digest:LocalTime convention):
    /// commitments = Facebook posts since yesterday (gap + per-day caps) plus every pending
    /// scheduled draft, fed to the pure suggester.</summary>
    private async Task<DateTime> SuggestSlotAsync(CancellationToken ct)
    {
        var slotOptions = FacebookScheduleOptions.From(configuration).ToSlotOptions();
        var fromUtc = DateTime.UtcNow.Date.AddDays(-1);
        var commitments = (await reviews.GetFacebookCommitmentsUtcAsync(fromUtc, ct))
            .Select(c => c.ToLocalTime())
            .ToList();
        return PublishSlotSuggester.Suggest(DateTime.Now, slotOptions, commitments);
    }

    private static string FormatSlot(DateTime slotLocal) =>
        slotLocal.Date == DateTime.Now.Date
            ? slotLocal.ToString("HH:mm", CultureInfo.InvariantCulture)
            : slotLocal.Date == DateTime.Now.Date.AddDays(1)
                ? "утре " + slotLocal.ToString("HH:mm", CultureInfo.InvariantCulture)
                : slotLocal.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
```

(d) In `HandleCallbackAsync`, replace the `ApproveDraft` case (✅ on a scheduled draft now overrides the schedule) and add the `ScheduleDraft` case after `CycleImage`:

```csharp
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

```csharp
            case ScheduleDraft schedule:
                await ScheduleDraftAsync(callback, schedule.DraftId, editor, ct);
                break;
```

(e) Add the handler (near `StartChangeConversationAsync`):

```csharp
    /// <summary>📅 pressed: recompute the slot (card labels go stale) and approve the draft
    /// gated on it. The guarded transition keeps double-taps and resolved drafts harmless.</summary>
    private async Task ScheduleDraftAsync(
        TgCallback callback, long draftId, string editor, CancellationToken ct)
    {
        var slotLocal = await SuggestSlotAsync(ct);
        var scheduled = await reviews.TryScheduleAsync(
            draftId, slotLocal.ToUniversalTime(), callback.UserId, callback.UserName, ct);
        if (!scheduled)
        {
            await AnswerBestEffortAsync(callback.CallbackId, "Вече обработено", ct);
            return;
        }

        var slotText = FormatSlot(slotLocal);
        await AnswerBestEffortAsync(callback.CallbackId, $"📅 Насрочено за {slotText}", ct);
        await EditResolvedAsync(callback.ChatId, callback.MessageId, draftId,
            $"📅 Насрочено за {slotText} от {editor}", ct);
        logger.LogInformation("Draft {DraftId}: scheduled for {SlotLocal} by {Editor}",
            draftId, slotLocal, editor);
    }
```

(f) Update `HelpText`'s card line:

```csharp
        "Върху картичка: ✅ одобри (веднага) · 📅 насрочи за предложения час · ✏️ промени · " +
        "🖼 друга снимка · ❌ откажи. " +
```

- [ ] **Step 7: Build + full suite**

Run: `dotnet test Newsroom.slnx -v minimal`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add -A src/
git commit -m "feat(review): 📅 Насрочи button schedules publishing at the suggested slot"
```

---

### Task 12: Docs + final verification

**Files:**
- Modify: `docs/05-integrations/facebook.md`
- Modify: `docs/05-integrations/telegram.md`
- Modify: `docs/10-roadmap.md` (progress log)

**Interfaces:** documentation only.

- [ ] **Step 1: facebook.md — post composition + scheduling**

In the section describing the Facebook-only post format, document the new priority order and add a scheduling section (adapt placement to the document's existing structure):

```markdown
### Post composition (Facebook-only mode)

Priority per draft:
1. **Editor `/post` drafts** (`PromptVersion = editor-v1`) — body published verbatim (unchanged).
2. **Caption drafts** (`nw_Draft.FacebookCaption` set; `draft-v2`+) — the post is
   `FacebookCaption.Compose(caption, hashtags)`: the social caption (sentence-case hook line,
   1–2 short paragraphs, closing question/CTA), a blank line, then 2–3 hashtags. Posted
   **verbatim** — no markdown stripping, no headline (the hook opens the post).
3. **Legacy drafts** (`FacebookCaption` NULL) — old behavior: ALL-CAPS headline + full stripped
   body (`FacebookTeaser.ComposeFullBody`).

When the site leg returns (`Publishing:FacebookOnly=false`), the same caption becomes the
link-post message.

### Scheduling (`Facebook:Schedule`)

- The review card's 📅 **Насрочи HH:mm** button approves the draft gated on
  `nw_Draft.ScheduledForUtc`; the publish queries skip a scheduled draft until the slot passes.
- The suggested slot (`PublishSlotSuggester`, recomputed at press time) is the earliest local
  time ≥ now + `LeadMinutes` inside a `Windows` range, ≥ `MinGapMinutes` from every published or
  scheduled post, on a day with < `MaxPerDay` posts. Defaults: 07:30–09:30 / 12:00–13:30 /
  17:30–21:30, 90 min gap, 5/day, 5 min lead — heuristic v1; data-driven windows arrive with
  smart insights (roadmap).
- ✅ **Одобри** stays immediate; pressed on a scheduled draft it clears the schedule
  (`nw_ReviewAction` = `ScheduleOverridden`).
```

- [ ] **Step 2: telegram.md — the new button and action values**

In the review-card / interaction-rules section add:

```markdown
- 📅 **Насрочи {HH:mm}** (second keyboard row): approves the draft scheduled for the suggested
  slot (label is advisory; the slot is recomputed at press time). Confirmation edits the card to
  „📅 Насрочено за {дд.MM HH:mm} от {editor}“; `nw_ReviewAction.Action = 'Scheduled'` (comment =
  the UTC slot). ✅ on a scheduled draft publishes immediately instead
  (`Action = 'ScheduleOverridden'`).
- The card shows a „📘 Facebook:“ block — the exact caption + hashtags the page post will carry.
```

- [ ] **Step 3: Roadmap progress log**

Add a row at the top of the `## Progress log` table in `docs/10-roadmap.md`:

```markdown
| 2026-07-17 | **Facebook engagement round implemented** (spec superpowers/specs/2026-07-17-facebook-engagement-design.md): social-native FB caption generated in the same drafting call (`facebookCaption`/`facebookHashtags`, prompt `draft-v2`, validator gates, 📘 block on the review card, posted verbatim with hashtags intact — replaces the ALL-CAPS full-body post); 📅 Насрочи button schedules publishing at a suggested slot (`PublishSlotSuggester` + `Facebook:Schedule` windows/gap/cap, `ScheduledForUtc` gate in both FB queries, ✅ override); migration 0013. Insights polling deferred to backlog ("smart insights"). |
```

- [ ] **Step 4: Full verification**

Run: `dotnet build Newsroom.slnx -v minimal` then `dotnet test Newsroom.slnx -v minimal`
Expected: build clean, all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add docs/
git commit -m "docs: Facebook caption composition + suggested-time scheduling"
```

---

## Post-implementation notes (for the session lead, not a task)

- **Live UAT checklist** (worker restarted with the new build, `Facebook:DryRun` still ON):
  1. `/new` or an organic Hot topic → the review card shows the 📘 Facebook block and the 📅 button with a plausible slot.
  2. Press 📅 → card edits to „Насрочено за …“; the draft does NOT publish before the slot; the dry-run log then shows the caption-only message (no ALL-CAPS headline, hashtags intact).
  3. Press ✅ on another scheduled draft → publishes on the next cycle.
  4. `/post` still publishes verbatim and immediately.
- Old approved drafts drain with the legacy composition (caption NULL) — expected.
- `ScheduledForUtc` on a draft that later gets ✏️ regenerated: the regeneration path resets the row to PendingReview via `CompleteRegenerationAsync` — it does not touch `ScheduledForUtc`, but a PendingReview draft is not selected by the publish queries, and the schedule gate re-applies only after a new 📅/✅. Acceptable; revisit if editors report confusion.
