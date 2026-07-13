using System.Text.Encodings.Web;
using System.Text.Json;

using Dapper;

using Newsroom.Core.Ai;
using Newsroom.Core.Drafting;
using Newsroom.Core.Trends;
using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Repositories;

public sealed class DraftRepository(IDbConnectionFactory db) : IDraftRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Cyrillic readable in the DB
    };

    /// <summary>A draft in any of these statuses is history, not activity — the topic may be
    /// drafted again. Everything else (Generating..Published) blocks a new draft. A Superseded
    /// version is history too: its replacement row is what keeps the topic busy.</summary>
    private static readonly string[] InactiveDraftStatuses =
    [
        nameof(DraftStatus.Rejected),
        nameof(DraftStatus.Expired),
        nameof(DraftStatus.GenerationFailed),
        nameof(DraftStatus.Superseded),
    ];

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

        // Check-then-set is not lock-guarded (READ COMMITTED): a concurrent SaveDraftAsync could
        // insert an active draft between this count and the UPDATE below. Benign and self-healing —
        // GetTopicsNeedingDraftAsync's NOT EXISTS gate is the real guard, so no duplicate draft is
        // ever generated; the worst case is a momentarily optimistic "Queued" reply and a
        // ForceDraftAtUtc marker that a later SaveDraftAsync clears. Accepted at our volume
        // (mirrors AiBudget's documented reserve/record race).
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
            VALUES (@topicId, 1, @status, @headline, @body, @promptVersion, @model)
            """,
            new
            {
                topicId,
                status = nameof(DraftStatus.PendingReview),
                headline = Truncate(headline, 300),
                body,
                promptVersion = ManualTopic.EditorPromptVersion,
                model = ManualTopic.EditorModelName,
            },
            transaction);

        transaction.Commit();
        return draftId;
    }

    public async Task<TopicBundle?> GetTopicBundleAsync(
        long topicId, int maxArticles, int maxTextCharsPerArticle, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);

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

        var articles = await connection.QueryAsync<TopicSourceArticle>(
            """
            SELECT TOP (@maxArticles)
                   a.Id AS ArticleId, a.Title, s.Name AS SourceName, a.Url, a.PublishedAtUtc,
                   ISNULL(an.Summary, '') AS Summary,
                   LEFT(a.ExtractedText, @maxTextCharsPerArticle) AS Text
            FROM dbo.nw_TopicArticle ta
            JOIN dbo.nw_SourceArticle a ON a.Id = ta.ArticleId
            JOIN dbo.nw_Source s ON s.Id = a.SourceId
            LEFT JOIN dbo.nw_ArticleAnalysis an ON an.ArticleId = a.Id
            WHERE ta.TopicId = @topicId
            ORDER BY ISNULL(a.PublishedAtUtc, a.FirstSeenAtUtc) DESC, a.Id DESC
            """,
            new { topicId, maxArticles, maxTextCharsPerArticle });

        return new TopicBundle(topicId, label, articles.ToList());
    }

    public async Task<long> SaveDraftAsync(
        TopicBundle bundle,
        DraftContent content,
        AiUsage usage,
        IReadOnlyList<string> flaggedClaims,
        IReadOnlyList<ImageCandidate> images,
        string promptVersion,
        CancellationToken ct)
    {
        var sources = bundle.Articles
            .Where(a => !string.IsNullOrEmpty(a.Url)) // synthetic Manual-topic article has no URL
            .Select(a => new SourceRef(a.Url, a.SourceName))
            .ToList();

        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        // Regeneration is a new version of the same topic's draft (02-functional-spec.md);
        // MAX+1 is safe here because only the single-threaded DraftJob inserts drafts in v1.
        var draftId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO dbo.nw_Draft
                (TopicId, Version, Status, Headline, Subtitle, BodyMarkdown, Category, Region,
                 TagsJson, SeoTitle, SeoDescription, SourcesJson, FlaggedClaimsJson, Confidence,
                 ImageAltTextBg, PromptVersion, Provider, Model, TokensIn, TokensOut, Cost)
            OUTPUT INSERTED.Id
            SELECT @topicId, ISNULL(MAX(d.Version), 0) + 1, @status, @headline, @subtitle,
                   @bodyMarkdown, @category, @region, @tagsJson, @seoTitle, @seoDescription,
                   @sourcesJson, @flaggedClaimsJson, @confidence, @imageAltTextBg,
                   @promptVersion, @provider, @model, @tokensIn, @tokensOut, @cost
            FROM dbo.nw_Draft d
            WHERE d.TopicId = @topicId
            """,
            new
            {
                topicId = bundle.TopicId,
                status = nameof(DraftStatus.PendingReview),
                headline = Truncate(content.Headline, 300),
                subtitle = Truncate(content.Subtitle, 500),
                bodyMarkdown = content.BodyMarkdown,
                category = Truncate(content.Category, 100),
                region = Truncate(content.Region, 100),
                tagsJson = JsonSerializer.Serialize(content.Tags, JsonOptions),
                seoTitle = Truncate(content.SeoTitle, 200),
                seoDescription = Truncate(content.SeoDescription, 300),
                sourcesJson = JsonSerializer.Serialize(sources, JsonOptions),
                flaggedClaimsJson = JsonSerializer.Serialize(flaggedClaims, JsonOptions),
                confidence = content.Confidence,
                imageAltTextBg = Truncate(content.ImageAltTextBg, 500),
                promptVersion,
                provider = usage.Provider,
                model = usage.Model,
                tokensIn = usage.TokensIn,
                tokensOut = usage.TokensOut,
                cost = usage.Cost,
            },
            transaction);

        await InsertImagesAsync(connection, transaction, draftId, content, images, ct);

        // A forced draft (ForceDraftAtUtc) is now fulfilled; clear the marker so a later
        // reject/expire of this draft does not silently re-trigger generation.
        await connection.ExecuteAsync(
            "UPDATE dbo.nw_Topic SET ForceDraftAtUtc = NULL WHERE Id = @topicId",
            new { topicId = bundle.TopicId },
            transaction);

        transaction.Commit();
        return draftId;
    }

    public async Task<bool> RecordGenerationFailureAsync(
        long topicId, string error, int maxAttempts, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.nw_Draft (TopicId, Version, Status, Error)
            SELECT @topicId, ISNULL(MAX(d.Version), 0) + 1, @status, @error
            FROM dbo.nw_Draft d
            WHERE d.TopicId = @topicId
            """,
            new { topicId, status = nameof(DraftStatus.GenerationFailed), error },
            transaction);

        var attempts = await connection.ExecuteScalarAsync<int>(
            """
            UPDATE dbo.nw_Topic
            SET DraftAttempts = DraftAttempts + 1
            OUTPUT INSERTED.DraftAttempts
            WHERE Id = @topicId
            """,
            new { topicId },
            transaction);

        transaction.Commit();
        return attempts >= maxAttempts;
    }

    public async Task<IReadOnlyList<PendingRegeneration>> GetPendingRegenerationsAsync(
        int maxCount, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<PendingRegeneration>(
            """
            SELECT TOP (@maxCount)
                   d.Id AS DraftId, CAST(d.TopicId AS bigint) AS TopicId, t.Label AS TopicLabel,
                   d.RegenInstructions AS Instructions, parent.BodyMarkdown AS PreviousBody
            FROM dbo.nw_Draft d
            JOIN dbo.nw_Topic t ON t.Id = d.TopicId
            LEFT JOIN dbo.nw_Draft parent ON parent.Id = d.ParentDraftId
            WHERE d.Status = @generatingStatus AND d.RegenInstructions IS NOT NULL
            ORDER BY d.Id
            """,
            new { maxCount, generatingStatus = nameof(DraftStatus.Generating) });
        return rows.ToList();
    }

    public async Task CompleteRegenerationAsync(
        long draftId,
        TopicBundle bundle,
        DraftContent content,
        AiUsage usage,
        IReadOnlyList<string> flaggedClaims,
        IReadOnlyList<ImageCandidate> images,
        string promptVersion,
        CancellationToken ct)
    {
        var sources = bundle.Articles
            .Where(a => !string.IsNullOrEmpty(a.Url)) // synthetic Manual-topic article has no URL
            .Select(a => new SourceRef(a.Url, a.SourceName))
            .ToList();

        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        // The SAME row flips to PendingReview. TelegramMessageId must be reset explicitly:
        // a failed earlier attempt may have stored the failure-notice message id here
        // (TelegramJob.ReportFailedRegenerationsAsync), which would make the dispatcher treat
        // the fresh version as already posted (bit us live 2026-07-03).
        // PostedAtUtc is reset to NULL for the same reason: the review-TTL clock must restart from
        // when the *new* version is posted, not from the original change-request time — otherwise a
        // slow regeneration (free-tier quota stall) shrinks or zeroes the editor's review window.
        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET Status = @status, Headline = @headline, Subtitle = @subtitle,
                BodyMarkdown = @bodyMarkdown, Category = @category, Region = @region,
                TagsJson = @tagsJson, SeoTitle = @seoTitle, SeoDescription = @seoDescription,
                SourcesJson = @sourcesJson, FlaggedClaimsJson = @flaggedClaimsJson,
                Confidence = @confidence, ImageAltTextBg = @imageAltTextBg,
                PromptVersion = @promptVersion, Provider = @provider, Model = @model,
                TokensIn = @tokensIn, TokensOut = @tokensOut, Cost = @cost, Error = NULL,
                TelegramMessageId = NULL, PostedAtUtc = NULL, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @draftId
            """,
            new
            {
                draftId,
                status = nameof(DraftStatus.PendingReview),
                sourcesJson = JsonSerializer.Serialize(sources, JsonOptions),
                headline = Truncate(content.Headline, 300),
                subtitle = Truncate(content.Subtitle, 500),
                bodyMarkdown = content.BodyMarkdown,
                category = Truncate(content.Category, 100),
                region = Truncate(content.Region, 100),
                tagsJson = JsonSerializer.Serialize(content.Tags, JsonOptions),
                seoTitle = Truncate(content.SeoTitle, 200),
                seoDescription = Truncate(content.SeoDescription, 300),
                flaggedClaimsJson = JsonSerializer.Serialize(flaggedClaims, JsonOptions),
                confidence = content.Confidence,
                imageAltTextBg = Truncate(content.ImageAltTextBg, 500),
                promptVersion,
                provider = usage.Provider,
                model = usage.Model,
                tokensIn = usage.TokensIn,
                tokensOut = usage.TokensOut,
                cost = usage.Cost,
            },
            transaction);

        await InsertImagesAsync(connection, transaction, draftId, content, images, ct);

        transaction.Commit();
    }

    public async Task FailRegenerationAsync(long draftId, string error, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // No DraftAttempts increment: a failed editor-requested rewrite must not poison the
        // topic (the original reviewable version already exists in its history).
        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET Status = @status, Error = @error, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @draftId
            """,
            new { draftId, status = nameof(DraftStatus.GenerationFailed), error });
    }

    private static async Task InsertImagesAsync(
        System.Data.IDbConnection connection, System.Data.IDbTransaction transaction,
        long draftId, DraftContent content, IReadOnlyList<ImageCandidate> images, CancellationToken ct)
    {
        for (var i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var image = images[i];
            await connection.ExecuteAsync(
                """
                INSERT INTO dbo.nw_DraftImage
                    (DraftId, Ordinal, SourceKind, Url, ThumbUrl, ProviderName, Attribution, AltTextBg)
                VALUES
                    (@draftId, @ordinal, @sourceKind, @url, @thumbUrl, @providerName, @attribution, @altTextBg)
                """,
                new
                {
                    draftId,
                    ordinal = i + 1,
                    sourceKind = "stock", // stock suggestions only (ADR-0009 tiers 1/3/4 come later)
                    url = Truncate(image.Url, 2000),
                    thumbUrl = Truncate(image.ThumbUrl, 2000),
                    providerName = Truncate(image.ProviderName, 50),
                    attribution = Truncate(image.Attribution, 500),
                    altTextBg = Truncate(content.ImageAltTextBg, 500),
                },
                transaction);
        }
    }

    /// <summary>Wire shape of one nw_Draft.SourcesJson entry: {"url": ..., "sourceName": ...}.</summary>
    private sealed record SourceRef(string Url, string SourceName);

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
