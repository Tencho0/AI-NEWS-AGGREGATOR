using System.Text.Json;

using Dapper;

using Newsroom.Core.Scraping;
using Newsroom.Core.Trends;
using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Repositories;

public sealed class TopicRepository(IDbConnectionFactory db) : ITopicRepository
{
    public async Task<int> AssignWireCopyDuplicatesAsync(DateTime windowStartUtc, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // Agency wire copy shares the ContentHash across sources (0002_source_articles), so a
        // twin of an already-clustered article joins that topic deterministically — no AI cost.
        return await connection.ExecuteAsync(
            """
            INSERT INTO dbo.nw_TopicArticle (TopicId, ArticleId)
            SELECT twin.TopicId, a.Id
            FROM dbo.nw_SourceArticle a
            CROSS APPLY (
                SELECT TOP (1) ta.TopicId
                FROM dbo.nw_SourceArticle assigned
                JOIN dbo.nw_TopicArticle ta ON ta.ArticleId = assigned.Id
                WHERE assigned.ContentHash = a.ContentHash AND assigned.Id <> a.Id
                ORDER BY ta.AddedAtUtc, ta.TopicId
            ) twin
            WHERE a.Status = @analysedStatus
              AND a.FirstSeenAtUtc >= @windowStartUtc
              AND NOT EXISTS (SELECT 1 FROM dbo.nw_TopicArticle x WHERE x.ArticleId = a.Id)
            """,
            new { analysedStatus = nameof(SourceArticleStatus.Analysed), windowStartUtc });
    }

    public async Task<IReadOnlyList<ExistingTopicSnapshot>> GetOpenTopicSnapshotsAsync(
        int recentTitlesPerTopic, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(long TopicId, string Label, string? Title)>(
            """
            SELECT CAST(t.Id AS bigint) AS TopicId, t.Label, recent.Title
            FROM dbo.nw_Topic t
            LEFT JOIN (
                SELECT ta.TopicId, a.Title,
                       ROW_NUMBER() OVER (PARTITION BY ta.TopicId
                                          ORDER BY ta.AddedAtUtc DESC, ta.ArticleId DESC) AS Recency
                FROM dbo.nw_TopicArticle ta
                JOIN dbo.nw_SourceArticle a ON a.Id = ta.ArticleId
            ) recent ON recent.TopicId = t.Id AND recent.Recency <= @recentTitlesPerTopic
            WHERE t.Status <> @doneStatus
            ORDER BY t.Id, recent.Recency
            """,
            new { recentTitlesPerTopic, doneStatus = nameof(TopicStatus.Done) });

        return rows
            .GroupBy(r => (r.TopicId, r.Label))
            .Select(g => new ExistingTopicSnapshot(
                g.Key.TopicId,
                g.Key.Label,
                g.Where(r => r.Title is not null).Select(r => r.Title!).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<ClusterCandidate>> GetUnassignedCandidatesAsync(
        DateTime windowStartUtc, int maxCount, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(long ArticleId, string Title, string? Summary, string? EntitiesJson)>(
            """
            SELECT TOP (@maxCount)
                   a.Id AS ArticleId, a.Title, an.Summary, an.EntitiesJson
            FROM dbo.nw_SourceArticle a
            JOIN dbo.nw_ArticleAnalysis an ON an.ArticleId = a.Id
            WHERE a.Status = @analysedStatus
              AND a.FirstSeenAtUtc >= @windowStartUtc
              AND NOT EXISTS (SELECT 1 FROM dbo.nw_TopicArticle ta WHERE ta.ArticleId = a.Id)
            ORDER BY a.Id
            """,
            new { maxCount, analysedStatus = nameof(SourceArticleStatus.Analysed), windowStartUtc });

        return rows
            .Select(r => new ClusterCandidate(r.ArticleId, r.Title, r.Summary ?? "", ParseEntities(r.EntitiesJson)))
            .ToList();
    }

    public async Task ApplyAssignmentsAsync(IReadOnlyList<ClusterAssignment> assignments, CancellationToken ct)
    {
        if (assignments.Count == 0)
            return;

        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        // One topic per distinct proposed label (case-insensitive), so several articles proposing
        // the same new story land together; an open topic with the same label is reused outright.
        var topicIdByLabel = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in assignments
                     .Select(a => a.NewTopicLabel?.Trim())
                     .Where(l => !string.IsNullOrEmpty(l))
                     .Select(l => Truncate(l!, 300))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var topicId = await connection.ExecuteScalarAsync<long?>(
                """
                SELECT TOP (1) Id FROM dbo.nw_Topic
                WHERE Label = @label AND Status <> @doneStatus
                ORDER BY Id
                """,
                new { label, doneStatus = nameof(TopicStatus.Done) },
                transaction);
            topicId ??= await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO dbo.nw_Topic (Label, Status)
                OUTPUT INSERTED.Id
                VALUES (@label, @emergingStatus)
                """,
                new { label, emergingStatus = nameof(TopicStatus.Emerging) },
                transaction);
            topicIdByLabel[label] = topicId.Value;
        }

        foreach (var assignment in assignments)
        {
            ct.ThrowIfCancellationRequested();

            var topicId = assignment.ExistingTopicId
                ?? (assignment.NewTopicLabel is { } label
                    && topicIdByLabel.TryGetValue(Truncate(label.Trim(), 300), out var created)
                    ? created
                    : (long?)null);
            if (topicId is null)
                continue; // breaks the exactly-one-of contract; nothing sane to store

            // Nonexistent topic ids (model hallucination) and already-assigned articles
            // (v1: one topic per article) are safe skips, not errors.
            await connection.ExecuteAsync(
                """
                IF EXISTS (SELECT 1 FROM dbo.nw_Topic WHERE Id = @topicId)
                   AND NOT EXISTS (SELECT 1 FROM dbo.nw_TopicArticle WHERE ArticleId = @articleId)
                INSERT INTO dbo.nw_TopicArticle (TopicId, ArticleId)
                VALUES (@topicId, @articleId)
                """,
                new { topicId, articleId = assignment.ArticleId },
                transaction);
        }

        transaction.Commit();
    }

    public async Task<IReadOnlyList<OpenTopic>> GetOpenTopicsAsync(CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<OpenTopic>(
            """
            SELECT CAST(Id AS bigint) AS TopicId, Label, Status, MutedUntilUtc
            FROM dbo.nw_Topic
            WHERE Status <> @doneStatus
            ORDER BY Id
            """,
            new { doneStatus = nameof(TopicStatus.Done) });
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<TopicArticleFact>>> GetOpenTopicFactsAsync(
        CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<TopicArticleFact>(
            """
            SELECT CAST(ta.TopicId AS bigint) AS TopicId, ta.ArticleId, a.SourceId,
                   a.FirstSeenAtUtc AS SeenAtUtc, ISNULL(an.RegionScore, 0) AS RegionScore
            FROM dbo.nw_TopicArticle ta
            JOIN dbo.nw_Topic t ON t.Id = ta.TopicId
            JOIN dbo.nw_SourceArticle a ON a.Id = ta.ArticleId
            LEFT JOIN dbo.nw_ArticleAnalysis an ON an.ArticleId = a.Id
            WHERE t.Status <> @doneStatus
            """,
            new { doneStatus = nameof(TopicStatus.Done) });

        return rows
            .GroupBy(f => f.TopicId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TopicArticleFact>)g.ToList());
    }

    public async Task UpdateTopicAsync(
        long topicId, double score, TopicStatus status, DateTime nowUtc, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Topic
            SET Score = @score, Status = @status, LastScoredAtUtc = @nowUtc
            WHERE Id = @topicId
            """,
            new { topicId, score, status = status.ToString(), nowUtc });
    }

    /// <summary>EntitiesJson is model output stored as-is; null or malformed reads as empty.</summary>
    private static IReadOnlyList<string> ParseEntities(string? entitiesJson)
    {
        if (string.IsNullOrWhiteSpace(entitiesJson))
            return [];

        try
        {
            var entities = JsonSerializer.Deserialize<List<string?>>(entitiesJson);
            return entities?.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
