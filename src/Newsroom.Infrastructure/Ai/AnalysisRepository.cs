using System.Text.Encodings.Web;
using System.Text.Json;

using Dapper;

using Newsroom.Core.Ai;
using Newsroom.Core.Scraping;
using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Ai;

public sealed class AnalysisRepository(IDbConnectionFactory db) : IAnalysisRepository
{
    /// <summary>Below this many characters there is nothing worth summarising.</summary>
    internal const int MinAnalysableTextLength = 200;

    private static readonly JsonSerializerOptions EntitiesJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Cyrillic readable in the DB
    };

    public async Task<IReadOnlyList<ArticleForAnalysis>> GetBatchAsync(
        int maxCount, int maxAttempts, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<ArticleForAnalysis>(
            """
            SELECT TOP (@maxCount)
                   a.Id AS ArticleId, a.Title, a.ExtractedText AS Text,
                   s.Name AS SourceName, a.PublishedAtUtc
            FROM dbo.nw_SourceArticle a
            JOIN dbo.nw_Source s ON s.Id = a.SourceId
            WHERE a.Status = @newStatus
              AND a.AnalysisAttempts < @maxAttempts
              AND a.ExtractedText IS NOT NULL
              AND LEN(a.ExtractedText) >= @minTextLength
            ORDER BY a.Id
            """,
            new
            {
                maxCount,
                maxAttempts,
                newStatus = nameof(SourceArticleStatus.New),
                minTextLength = MinAnalysableTextLength,
            });
        return rows.ToList();
    }

    public async Task SaveAsync(
        IReadOnlyList<ArticleAnalysisResult> results, AiUsage usage, CancellationToken ct)
    {
        if (results.Count == 0)
            return;

        // Batch usage split evenly across the batch's rows; nw_CostLedger keeps the exact totals.
        var tokensInPerArticle = usage.TokensIn / results.Count;
        var tokensOutPerArticle = usage.TokensOut / results.Count;
        var costPerArticle = usage.Cost / results.Count;

        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        foreach (var result in results)
        {
            ct.ThrowIfCancellationRequested();

            await connection.ExecuteAsync(
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.nw_ArticleAnalysis WHERE ArticleId = @ArticleId)
                INSERT INTO dbo.nw_ArticleAnalysis
                    (ArticleId, Summary, Category, RegionScore, EntitiesJson, Language, Relevant,
                     Provider, Model, TokensIn, TokensOut, Cost)
                VALUES
                    (@ArticleId, @Summary, @Category, @RegionScore, @EntitiesJson, @Language, @Relevant,
                     @Provider, @Model, @TokensIn, @TokensOut, @Cost)
                """,
                new
                {
                    result.ArticleId,
                    result.Summary,
                    result.Category,
                    result.RegionScore,
                    EntitiesJson = JsonSerializer.Serialize(result.Entities, EntitiesJsonOptions),
                    result.Language,
                    result.Relevant,
                    usage.Provider,
                    usage.Model,
                    TokensIn = tokensInPerArticle,
                    TokensOut = tokensOutPerArticle,
                    Cost = costPerArticle,
                },
                transaction);

            // Only relevant Bulgarian articles continue down the pipeline; everything else is
            // Ignored but keeps its analysis row for the audit trail.
            var status = result.Relevant && result.Language == "bg"
                ? SourceArticleStatus.Analysed
                : SourceArticleStatus.Ignored;
            await connection.ExecuteAsync(
                """
                UPDATE dbo.nw_SourceArticle
                SET Status = @status, UpdatedAtUtc = SYSUTCDATETIME()
                WHERE Id = @ArticleId
                """,
                new { status = status.ToString(), result.ArticleId },
                transaction);
        }

        transaction.Commit();
    }

    public async Task MarkFailedAttemptAsync(
        IReadOnlyList<long> articleIds, int maxAttempts, CancellationToken ct)
    {
        if (articleIds.Count == 0)
            return;

        using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_SourceArticle
            SET AnalysisAttempts = AnalysisAttempts + 1,
                Status = CASE WHEN AnalysisAttempts + 1 >= @maxAttempts
                              THEN @ignoredStatus ELSE Status END, -- poison protection
                UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id IN @articleIds
            """,
            new { articleIds, maxAttempts, ignoredStatus = nameof(SourceArticleStatus.Ignored) });
    }

    public async Task MarkIgnoredAsync(long articleId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_SourceArticle
            SET Status = @ignoredStatus, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @articleId
            """,
            new { articleId, ignoredStatus = nameof(SourceArticleStatus.Ignored) });
    }

    public async Task<int> MarkUnanalysableAsync(CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        return await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_SourceArticle
            SET Status = @ignoredStatus, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Status = @newStatus
              AND (ExtractedText IS NULL OR LEN(ExtractedText) < @minTextLength)
            """,
            new
            {
                ignoredStatus = nameof(SourceArticleStatus.Ignored),
                newStatus = nameof(SourceArticleStatus.New),
                minTextLength = MinAnalysableTextLength,
            });
    }
}
