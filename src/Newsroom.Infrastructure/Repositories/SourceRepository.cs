using Dapper;
using Newsroom.Core.Scraping;
using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Repositories;

public sealed class SourceRepository(IDbConnectionFactory db) : ISourceRepository
{
    public async Task<IReadOnlyList<Source>> GetDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<SourceRow>(
            """
            SELECT Id, Name, Kind, Url, ParserHint, IntervalMinutes, Enabled,
                   PolitenessDelaySeconds, LastCrawledAtUtc, LastSuccessAtUtc, LastError,
                   ConsecutiveFailures, Etag, LastModifiedHeader
            FROM dbo.nw_Source
            WHERE Enabled = 1
              AND (LastCrawledAtUtc IS NULL
                   OR DATEADD(minute, IntervalMinutes, LastCrawledAtUtc) <= @nowUtc)
            ORDER BY LastCrawledAtUtc
            """,
            new { nowUtc });
        return rows.Select(r => r.ToSource()).ToList();
    }

    public async Task RecordSuccessAsync(
        int sourceId, DateTime nowUtc, string? etag, string? lastModifiedHeader, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Source
            SET LastCrawledAtUtc = @nowUtc, LastSuccessAtUtc = @nowUtc, LastError = NULL,
                ConsecutiveFailures = 0, Etag = @etag, LastModifiedHeader = @lastModifiedHeader
            WHERE Id = @sourceId
            """,
            new { sourceId, nowUtc, etag, lastModifiedHeader });
    }

    public async Task RecordFailureAsync(int sourceId, DateTime nowUtc, string error, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Source
            SET LastCrawledAtUtc = @nowUtc, LastError = @error,
                ConsecutiveFailures = ConsecutiveFailures + 1
            WHERE Id = @sourceId
            """,
            new { sourceId, nowUtc, error = Truncate(error, 4000) });
    }

    public async Task<IReadOnlyList<Source>> DisableDeadSourcesAsync(
        DateTime failingSinceUtc, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<SourceRow>(
            """
            UPDATE dbo.nw_Source
            SET Enabled = 0
            OUTPUT inserted.Id, inserted.Name, inserted.Kind, inserted.Url, inserted.ParserHint,
                   inserted.IntervalMinutes, inserted.Enabled, inserted.PolitenessDelaySeconds,
                   inserted.LastCrawledAtUtc, inserted.LastSuccessAtUtc, inserted.LastError,
                   inserted.ConsecutiveFailures, inserted.Etag, inserted.LastModifiedHeader
            WHERE Enabled = 1
              AND ConsecutiveFailures >= 3
              AND COALESCE(LastSuccessAtUtc, '0001-01-01') < @failingSinceUtc
              AND LastCrawledAtUtc IS NOT NULL
            """,
            new { failingSinceUtc });
        return rows.Select(r => r.ToSource()).ToList();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>Dapper row: Kind arrives as nvarchar, mapped to the enum here.</summary>
    private sealed record SourceRow(
        int Id, string Name, string Kind, string Url, string? ParserHint, int IntervalMinutes,
        bool Enabled, int PolitenessDelaySeconds, DateTime? LastCrawledAtUtc,
        DateTime? LastSuccessAtUtc, string? LastError, int ConsecutiveFailures,
        string? Etag, string? LastModifiedHeader)
    {
        public Source ToSource() => new()
        {
            Id = Id,
            Name = Name,
            Kind = Enum.Parse<SourceKind>(Kind, ignoreCase: true),
            Url = Url,
            ParserHint = ParserHint,
            IntervalMinutes = IntervalMinutes,
            Enabled = Enabled,
            PolitenessDelaySeconds = PolitenessDelaySeconds,
            LastCrawledAtUtc = LastCrawledAtUtc,
            LastSuccessAtUtc = LastSuccessAtUtc,
            LastError = LastError,
            ConsecutiveFailures = ConsecutiveFailures,
            Etag = Etag,
            LastModifiedHeader = LastModifiedHeader,
        };
    }
}
