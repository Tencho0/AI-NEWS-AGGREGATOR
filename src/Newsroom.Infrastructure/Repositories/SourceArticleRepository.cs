using Dapper;
using Newsroom.Core.Scraping;
using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Repositories;

public sealed class SourceArticleRepository(IDbConnectionFactory db) : ISourceArticleRepository
{
    public async Task<bool> UpsertAsync(SourceArticle article, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // Insert when the canonical URL is new; refresh title/text when the source edited the
        // article (hash changed). Re-crawls of unchanged content touch nothing.
        var inserted = await connection.ExecuteScalarAsync<int>(
            """
            MERGE dbo.nw_SourceArticle AS target
            USING (SELECT @UrlHash AS UrlHash) AS source ON target.UrlHash = source.UrlHash
            WHEN MATCHED AND target.ContentHash <> @ContentHash THEN
                UPDATE SET Title = @Title, Author = @Author, PublishedAtUtc = @PublishedAtUtc,
                           ExtractedText = @ExtractedText, ContentHash = @ContentHash,
                           UpdatedAtUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (SourceId, Url, UrlHash, Title, Author, PublishedAtUtc, ExtractedText,
                        ContentHash, Status)
                VALUES (@SourceId, @Url, @UrlHash, @Title, @Author, @PublishedAtUtc,
                        @ExtractedText, @ContentHash, @Status)
            OUTPUT CASE WHEN $action = 'INSERT' THEN 1 ELSE 0 END;
            """,
            new
            {
                article.SourceId,
                article.Url,
                article.UrlHash,
                Title = Truncate(article.Title, 500),
                Author = article.Author is null ? null : Truncate(article.Author, 200),
                article.PublishedAtUtc,
                article.ExtractedText,
                article.ContentHash,
                Status = article.Status.ToString(),
            });
        return inserted == 1;
    }

    public async Task<IReadOnlySet<string>> GetExistingUrlHashesAsync(
        IReadOnlyCollection<string> urlHashes, CancellationToken ct)
    {
        if (urlHashes.Count == 0)
            return new HashSet<string>();

        using var connection = await db.OpenAsync(ct);
        var existing = await connection.QueryAsync<string>(
            "SELECT UrlHash FROM dbo.nw_SourceArticle WHERE UrlHash IN @urlHashes",
            new { urlHashes });
        return existing.ToHashSet(StringComparer.Ordinal);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
