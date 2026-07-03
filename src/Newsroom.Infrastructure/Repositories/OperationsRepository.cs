using System.Globalization;

using Dapper;

using Newsroom.Core.Drafting;
using Newsroom.Core.Operations;
using Newsroom.Core.Scraping;
using Newsroom.Core.Trends;
using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Repositories;

/// <summary>
/// <see cref="IOperationsRepository"/> over Dapper: heartbeat reads for the watchdog, the
/// daily-digest aggregate, retention pruning and the startup crash-recovery sweep
/// (docs/07-operations.md). All state lives in the DB (nw_Config for the small values), so
/// every ops job survives a restart without double-acting.
/// </summary>
public sealed class OperationsRepository(IDbConnectionFactory db) : IOperationsRepository
{
    public async Task<IReadOnlyDictionary<string, DateTime>> GetHeartbeatsAsync(CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(string Key, string? Value)>(
            """
            SELECT [Key], [Value] FROM dbo.nw_Config WHERE [Key] LIKE @pattern
            """,
            new { pattern = JobHeartbeat.KeyPrefix + "%" });

        var beats = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var (key, value) in rows)
        {
            // Round-trip ("O") values written by JobHeartbeat; anything else is skipped and
            // the job then counts as never having beaten — the safe direction for a watchdog.
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var beatUtc))
            {
                beats[key[JobHeartbeat.KeyPrefix.Length..]] = beatUtc;
            }
        }
        return beats;
    }

    public async Task<string?> GetConfigValueAsync(string key, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<string?>(
            """
            SELECT [Value] FROM dbo.nw_Config WHERE [Key] = @key
            """,
            new { key });
    }

    public async Task SetConfigValueAsync(string key, string value, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            MERGE dbo.nw_Config AS target
            USING (SELECT @key AS [Key]) AS source ON target.[Key] = source.[Key]
            WHEN MATCHED THEN UPDATE SET [Value] = @value, UpdatedAtUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT ([Key], [Value]) VALUES (@key, @value);
            """,
            new { key, value });
    }

    public async Task<DigestStats> GetDigestStatsAsync(DateTime dayUtc, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var multi = await connection.QueryMultipleAsync(
            """
            SELECT s.Name, COUNT(*) AS Cnt
            FROM dbo.nw_SourceArticle a
            JOIN dbo.nw_Source s ON s.Id = a.SourceId
            WHERE a.FirstSeenAtUtc >= @dayUtc
            GROUP BY s.Name
            ORDER BY COUNT(*) DESC, s.Name;

            SELECT a.Status, COUNT(*) AS Cnt
            FROM dbo.nw_SourceArticle a
            WHERE a.FirstSeenAtUtc >= @dayUtc
            GROUP BY a.Status;

            SELECT COUNT(*) FROM dbo.nw_Topic WHERE FirstSeenAtUtc >= @dayUtc;

            SELECT COUNT(*) FROM dbo.nw_Topic WHERE Status = @hotStatus;

            SELECT d.Status, COUNT(*) AS Cnt
            FROM dbo.nw_Draft d
            WHERE d.CreatedAtUtc >= @dayUtc
            GROUP BY d.Status;

            SELECT r.[Action], COUNT(*) AS Cnt
            FROM dbo.nw_ReviewAction r
            WHERE r.AtUtc >= @dayUtc
            GROUP BY r.[Action];

            SELECT ISNULL(SUM(CASE WHEN Status = @succeededStatus THEN 1 ELSE 0 END), 0) AS Succeeded,
                   ISNULL(SUM(CASE WHEN Status = @failedStatus THEN 1 ELSE 0 END), 0) AS Failed
            FROM dbo.nw_PublishRecord
            WHERE AtUtc >= @dayUtc;

            SELECT ISNULL(SUM(RequestCount), 0) AS Requests, ISNULL(SUM(TokensIn), 0) AS TokensIn,
                   ISNULL(SUM(TokensOut), 0) AS TokensOut, ISNULL(SUM(Cost), 0) AS Cost
            FROM dbo.nw_CostLedger
            WHERE AtUtc >= @dayUtc;

            SELECT ISNULL(SUM(CASE WHEN Enabled = 1 THEN 1 ELSE 0 END), 0) AS Enabled,
                   ISNULL(SUM(CASE WHEN Enabled = 0 THEN 1 ELSE 0 END), 0) AS Disabled
            FROM dbo.nw_Source;
            """,
            new
            {
                dayUtc,
                hotStatus = nameof(TopicStatus.Hot),
                succeededStatus = "Succeeded",
                failedStatus = "Failed",
            });

        var perSource = (await multi.ReadAsync<(string SourceName, int Count)>()).ToList();
        var articlesByStatus = (await multi.ReadAsync<(string Status, int Cnt)>()).ToList();
        var topicsCreated = await multi.ReadSingleAsync<int>();
        var hotTopics = await multi.ReadSingleAsync<int>();
        var drafts = (await multi.ReadAsync<(string Status, int Count)>()).ToList();
        var actions = (await multi.ReadAsync<(string Action, int Count)>()).ToList();
        var publishes = await multi.ReadSingleAsync<(int Succeeded, int Failed)>();
        var ai = await multi.ReadSingleAsync<(int Requests, long TokensIn, long TokensOut, decimal Cost)>();
        var sources = await multi.ReadSingleAsync<(int Enabled, int Disabled)>();

        return new DigestStats
        {
            DayUtc = dayUtc,
            ArticlesScraped = articlesByStatus.Sum(a => a.Cnt),
            ArticlesPerSource = perSource,
            ArticlesAnalysed = CountFor(articlesByStatus, nameof(SourceArticleStatus.Analysed)),
            ArticlesIgnored = CountFor(articlesByStatus, nameof(SourceArticleStatus.Ignored)),
            TopicsCreated = topicsCreated,
            HotTopics = hotTopics,
            DraftsCreatedByStatus = drafts.OrderBy(d => d.Status, StringComparer.Ordinal).ToList(),
            ReviewActions = actions.OrderBy(a => a.Action, StringComparer.Ordinal).ToList(),
            PublishSucceeded = publishes.Succeeded,
            PublishFailed = publishes.Failed,
            AiRequests = ai.Requests,
            AiTokensIn = ai.TokensIn,
            AiTokensOut = ai.TokensOut,
            AiCost = ai.Cost,
            SourcesEnabled = sources.Enabled,
            SourcesDisabled = sources.Disabled,
        };
    }

    public async Task<int> ClearExpiredExtractedTextAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // UpdatedAtUtc is deliberately untouched: pruning is not a content edit, and the
        // ContentHash stays so a re-crawl of the unchanged page does not resurrect the text.
        return await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_SourceArticle
            SET ExtractedText = NULL
            WHERE FirstSeenAtUtc < @cutoffUtc AND ExtractedText IS NOT NULL
            """,
            new { cutoffUtc });
    }

    public async Task<int> DeleteExpiredLogsAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        return await connection.ExecuteAsync(
            """
            DELETE FROM dbo.nw_Log WHERE [TimeStamp] < @cutoffUtc
            """,
            new { cutoffUtc });
    }

    public async Task<int> FailStuckGeneratingDraftsAsync(
        DateTime cutoffUtc, string error, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // Recovered regenerations (RegenInstructions set, TelegramMessageId null) are picked
        // up by the existing failure-notice path, so the waiting editor hears about it.
        return await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET Status = @failedStatus, Error = @error, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Status = @generatingStatus AND UpdatedAtUtc < @cutoffUtc
            """,
            new
            {
                cutoffUtc,
                error,
                failedStatus = nameof(DraftStatus.GenerationFailed),
                generatingStatus = nameof(DraftStatus.Generating),
            });
    }

    private static int CountFor(List<(string Status, int Cnt)> rows, string status) =>
        rows.FirstOrDefault(r => string.Equals(r.Status, status, StringComparison.Ordinal)).Cnt;
}
