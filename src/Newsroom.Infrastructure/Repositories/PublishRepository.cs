using System.Text.Json;

using Dapper;

using Newsroom.Core.Drafting;
using Newsroom.Core.Publishing;
using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Repositories;

/// <summary>
/// <see cref="IPublishRepository"/> over Dapper. Attempt gating sums nw_PublishRecord.Attempts
/// per (draft, destination): transient failures weigh 1 and retry next cycles, terminal
/// rejections are written weighing the whole cap so they never come back. Reaching the cap
/// flips an Approved draft to PublishFailed in the same transaction; a PartiallyPublished
/// draft (site live, Facebook exhausted) keeps its status (docs/02-functional-spec.md §6).
/// </summary>
public sealed class PublishRepository(IDbConnectionFactory db) : IPublishRepository
{
    /// <summary>nw_PublishRecord.Status values.</summary>
    private const string SucceededStatus = "Succeeded";
    private const string FailedStatus = "Failed";

    /// <summary>Editor uploads carry Telegram file_ids the site cannot fetch, so a draft whose
    /// chosen image is one publishes without an image in v1 (the endpoint's placeholder kicks in).</summary>
    private const string EditorUploadKind = "editor-upload";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ArticleToPublish>> GetApprovedUnpublishedAsync(
        string destination, int maxAttempts, int maxCount, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<PublishRow>(
            """
            SELECT TOP (@maxCount)
                   d.Id AS DraftId, ISNULL(d.Headline, '') AS Headline, d.Subtitle,
                   ISNULL(d.BodyMarkdown, '') AS BodyMarkdown, ISNULL(d.Category, '') AS Category,
                   d.Region, d.TagsJson, d.SeoTitle, d.SeoDescription,
                   d.ImageAltTextBg AS DraftAltTextBg,
                   img.SourceKind AS ImageKind, img.Url AS ImageUrl,
                   img.AltTextBg AS ImageAltTextBg, img.Attribution AS ImageAttribution
            FROM dbo.nw_Draft d
            OUTER APPLY (
                SELECT TOP 1 di.SourceKind, di.Url, di.AltTextBg, di.Attribution
                FROM dbo.nw_DraftImage di
                WHERE di.DraftId = d.Id
                ORDER BY di.Selected DESC, di.Ordinal
            ) img
            WHERE d.Status = @approvedStatus
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.nw_PublishRecord p
                  WHERE p.DraftId = d.Id AND p.Destination = @destination
                    AND p.Status = @succeededStatus)
              AND ISNULL((
                  SELECT SUM(p.Attempts) FROM dbo.nw_PublishRecord p
                  WHERE p.DraftId = d.Id AND p.Destination = @destination
                    AND p.Status = @failedStatus), 0) < @maxAttempts
            ORDER BY d.Id
            """,
            new
            {
                maxCount,
                destination,
                maxAttempts,
                approvedStatus = nameof(DraftStatus.Approved),
                succeededStatus = SucceededStatus,
                failedStatus = FailedStatus,
            });
        return rows.Select(ToArticle).ToList();
    }

    public async Task<IReadOnlyList<FacebookPost>> GetPendingFacebookAsync(
        int maxAttempts, int maxCount, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<FacebookRow>(
            """
            SELECT TOP (@maxCount)
                   d.Id AS DraftId, ISNULL(d.Headline, '') AS Headline,
                   d.SeoDescription, ISNULL(d.BodyMarkdown, '') AS BodyMarkdown,
                   site.ExternalUrl AS ArticleUrl
            FROM dbo.nw_Draft d
            CROSS APPLY (
                SELECT TOP 1 p.ExternalUrl
                FROM dbo.nw_PublishRecord p
                WHERE p.DraftId = d.Id AND p.Destination = @umbraco
                  AND p.Status = @succeededStatus AND p.ExternalUrl IS NOT NULL
                ORDER BY p.Id DESC
            ) site
            WHERE d.Status = @partiallyPublishedStatus
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.nw_PublishRecord p
                  WHERE p.DraftId = d.Id AND p.Destination = @facebook
                    AND p.Status = @succeededStatus)
              AND ISNULL((
                  SELECT SUM(p.Attempts) FROM dbo.nw_PublishRecord p
                  WHERE p.DraftId = d.Id AND p.Destination = @facebook
                    AND p.Status = @failedStatus), 0) < @maxAttempts
            ORDER BY d.Id
            """,
            new
            {
                maxCount,
                maxAttempts,
                umbraco = PublishDestinations.Umbraco,
                facebook = PublishDestinations.Facebook,
                partiallyPublishedStatus = nameof(DraftStatus.PartiallyPublished),
                succeededStatus = SucceededStatus,
                failedStatus = FailedStatus,
            });
        return rows.Select(r => new FacebookPost(
            r.DraftId, r.Headline, FacebookTeaser.Compose(r.SeoDescription, r.BodyMarkdown),
            r.ArticleUrl)).ToList();
    }

    public async Task RecordSuccessAsync(
        long draftId, string destination, string externalId, string? url,
        IReadOnlyCollection<string> requiredDestinations, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.nw_PublishRecord (DraftId, Destination, ExternalId, ExternalUrl, Status)
            VALUES (@draftId, @destination, @externalId, @url, @succeededStatus)
            """,
            new
            {
                draftId,
                destination,
                externalId = Truncate(externalId, 100),
                url = Truncate(url, 2000),
                succeededStatus = SucceededStatus,
            },
            transaction);

        // Published only when every required destination has succeeded; otherwise the site is
        // live while Facebook is still pending → PartiallyPublished (docs/02-functional-spec §6).
        // Computed inside the transaction so a concurrent record cannot skew the count.
        var succeededCount = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(DISTINCT p.Destination) FROM dbo.nw_PublishRecord p
            WHERE p.DraftId = @draftId AND p.Status = @succeededStatus
              AND p.Destination IN @requiredDestinations
            """,
            new { draftId, succeededStatus = SucceededStatus, requiredDestinations },
            transaction);

        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET Status = @status, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @draftId
            """,
            new
            {
                draftId,
                status = succeededCount >= requiredDestinations.Count
                    ? nameof(DraftStatus.Published)
                    : nameof(DraftStatus.PartiallyPublished),
            },
            transaction);

        transaction.Commit();
    }

    public async Task<bool> RecordFailureAsync(
        long draftId, string destination, string error, int attempts, int maxAttempts,
        CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.nw_PublishRecord (DraftId, Destination, Status, Error, Attempts)
            VALUES (@draftId, @destination, @failedStatus, @error, @attempts)
            """,
            new { draftId, destination, failedStatus = FailedStatus, error, attempts },
            transaction);

        var totalAttempts = await connection.ExecuteScalarAsync<int>(
            """
            SELECT ISNULL(SUM(p.Attempts), 0) FROM dbo.nw_PublishRecord p
            WHERE p.DraftId = @draftId AND p.Destination = @destination AND p.Status = @failedStatus
            """,
            new { draftId, destination, failedStatus = FailedStatus },
            transaction);

        var exhausted = totalAttempts >= maxAttempts;
        if (exhausted)
            await connection.ExecuteAsync(
                """
                UPDATE dbo.nw_Draft
                SET Status = @publishFailedStatus, UpdatedAtUtc = SYSUTCDATETIME()
                WHERE Id = @draftId AND Status = @approvedStatus
                """,
                new
                {
                    draftId,
                    publishFailedStatus = nameof(DraftStatus.PublishFailed),
                    approvedStatus = nameof(DraftStatus.Approved),
                },
                transaction);

        transaction.Commit();
        return exhausted;
    }

    private static ArticleToPublish ToArticle(PublishRow r) => new(
        r.DraftId,
        r.Headline,
        r.Subtitle,
        r.BodyMarkdown,
        r.Category,
        r.Region,
        ParseStringList(r.TagsJson),
        r.SeoTitle,
        r.SeoDescription,
        ToImage(r));

    private static PublishImage? ToImage(PublishRow r)
    {
        if (r.ImageUrl is null || r.ImageKind == EditorUploadKind)
            return null;
        return new PublishImage(
            FileNameFromUrl(r.ImageUrl),
            r.ImageUrl,
            r.ImageAltTextBg ?? r.DraftAltTextBg ?? r.Headline,
            r.ImageAttribution);
    }

    /// <summary>Derives a media file name from the image URL's last path segment;
    /// "image.jpg" when the URL yields none.</summary>
    internal static string FileNameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segment = uri.Segments.Length > 0 ? uri.Segments[^1].Trim('/') : "";
            if (!string.IsNullOrWhiteSpace(segment))
                return Uri.UnescapeDataString(segment);
        }
        return "image.jpg";
    }

    /// <summary>JSON columns are model output stored as-is; null or malformed reads as empty.</summary>
    private static IReadOnlyList<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var values = JsonSerializer.Deserialize<List<string?>>(json, JsonOptions);
            return values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    /// <summary>Dapper row shape of <see cref="GetPendingFacebookAsync"/>.</summary>
    private sealed record FacebookRow(
        long DraftId,
        string Headline,
        string? SeoDescription,
        string BodyMarkdown,
        string ArticleUrl);

    /// <summary>Dapper row shape of <see cref="GetApprovedUnpublishedAsync"/>.</summary>
    private sealed record PublishRow(
        long DraftId,
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
        string? ImageAttribution);
}
