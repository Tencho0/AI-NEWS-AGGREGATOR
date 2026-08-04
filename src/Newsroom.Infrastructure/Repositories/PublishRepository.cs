using System.Text.Json;

using Dapper;

using Newsroom.Core.Drafting;
using Newsroom.Core.Images;
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
public sealed class PublishRepository(IDbConnectionFactory db, ImageStorage storage) : IPublishRepository
{
    /// <summary>nw_PublishRecord.Status values.</summary>
    private const string SucceededStatus = "Succeeded";
    private const string FailedStatus = "Failed";

    /// <summary>Editor uploads and AI-generated covers store a relative storage key in Url
    /// (downloaded from Telegram at attach time / saved at generation time); the publisher inlines
    /// the file as base64 since the site cannot fetch it. Pre-ADR-0013 rows hold absolute paths —
    /// <see cref="ImageStorage.TryResolve"/> accepts both.</summary>
    private static bool IsLocalFileKind(string? kind) =>
        kind is ImageSourceKinds.EditorUpload or ImageSourceKinds.Ai;

    /// <summary>Resolves a local storage key to an absolute path, or null when it does not resolve
    /// inside the storage root (a traversal attempt or an orphaned legacy row) — the publisher then
    /// treats the draft as having no image rather than reading an arbitrary file.</summary>
    private string? LocalPathFor(string key) =>
        storage.TryResolve(key, out var path) ? path : null;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ArticleToPublish>> GetApprovedUnpublishedAsync(
        string destination, int maxAttempts, int maxCount, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<PublishRow>(
            """
            SELECT TOP (@maxCount)
                   d.Id AS DraftId, d.PublishRef, ISNULL(d.Headline, '') AS Headline, d.Subtitle,
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
                   d.SeoDescription,
                   d.FacebookCaption, d.FacebookHashtagsJson,
                   ISNULL(d.BodyMarkdown, '') AS BodyMarkdown,
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
              AND (d.ScheduledForUtc IS NULL OR d.ScheduledForUtc <= SYSUTCDATETIME())
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
        return rows.Select(r => string.IsNullOrWhiteSpace(r.FacebookCaption)
            ? new FacebookPost(
                r.DraftId, r.Headline, FacebookTeaser.Compose(r.SeoDescription, r.BodyMarkdown),
                r.ArticleUrl)
            : new FacebookPost(
                r.DraftId, Headline: "",
                FacebookCaption.Compose(r.FacebookCaption, ParseStringList(r.FacebookHashtagsJson)),
                r.ArticleUrl)).ToList();
    }

    public async Task<IReadOnlyList<FacebookPost>> GetApprovedForFacebookAsync(
        int maxAttempts, int maxCount, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // Facebook-only mode: no Umbraco join and no site URL. Selection mirrors
        // GetPendingFacebookAsync's Facebook gate (no Succeeded 'facebook' record, failed attempts
        // under the cap) but keys off Approved drafts directly. The draft's chosen image (Selected
        // first, else lowest Ordinal — the same pick as the Umbraco leg) rides along so the page
        // post carries the picture, not just text.
        var rows = await connection.QueryAsync<FacebookApprovedRow>(
            """
            SELECT TOP (@maxCount)
                   d.Id AS DraftId, ISNULL(d.Headline, '') AS Headline,
                   ISNULL(d.BodyMarkdown, '') AS BodyMarkdown, d.PromptVersion,
                   d.FacebookCaption, d.FacebookHashtagsJson,
                   img.SourceKind AS ImageKind, img.Url AS ImageUrl
            FROM dbo.nw_Draft d
            OUTER APPLY (
                SELECT TOP 1 di.SourceKind, di.Url
                FROM dbo.nw_DraftImage di
                WHERE di.DraftId = d.Id
                ORDER BY di.Selected DESC, di.Ordinal
            ) img
            WHERE d.Status = @approvedStatus
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.nw_PublishRecord p
                  WHERE p.DraftId = d.Id AND p.Destination = @facebook
                    AND p.Status = @succeededStatus)
              AND ISNULL((
                  SELECT SUM(p.Attempts) FROM dbo.nw_PublishRecord p
                  WHERE p.DraftId = d.Id AND p.Destination = @facebook
                    AND p.Status = @failedStatus), 0) < @maxAttempts
              AND (d.ScheduledForUtc IS NULL OR d.ScheduledForUtc <= SYSUTCDATETIME())
            ORDER BY d.Id
            """,
            new
            {
                maxCount,
                maxAttempts,
                approvedStatus = nameof(DraftStatus.Approved),
                facebook = PublishDestinations.Facebook,
                succeededStatus = SucceededStatus,
                failedStatus = FailedStatus,
            });
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
    }

    /// <summary>The chosen draft image as a Facebook photo source: editor uploads and AI
    /// illustrations live on the worker's disk (sent as multipart bytes), everything else is a
    /// hosted URL Facebook fetches server-side. Null when the draft has no image — the
    /// publisher then posts text only.</summary>
    private FacebookImage? ToFacebookImage(string? kind, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!IsLocalFileKind(kind))
            return new FacebookImage(Url: url, LocalPath: null, FileName: FileNameFromUrl(url));

        return LocalPathFor(url) is { } path
            ? new FacebookImage(Url: null, LocalPath: path, FileName: Path.GetFileName(path))
            : null; // unresolvable key — post text only rather than reach outside the storage root
    }

    public async Task<FacebookPost?> GetFacebookPostForDraftAsync(long draftId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<FacebookRow>(
            """
            SELECT d.Id AS DraftId, ISNULL(d.Headline, '') AS Headline,
                   d.SeoDescription,
                   d.FacebookCaption, d.FacebookHashtagsJson,
                   ISNULL(d.BodyMarkdown, '') AS BodyMarkdown,
                   '' AS ArticleUrl
            FROM dbo.nw_Draft d
            WHERE d.Id = @draftId
            """,
            new { draftId });
        return row is null
            ? null
            : string.IsNullOrWhiteSpace(row.FacebookCaption)
                ? new FacebookPost(row.DraftId, row.Headline,
                    FacebookTeaser.Compose(row.SeoDescription, row.BodyMarkdown), row.ArticleUrl)
                : new FacebookPost(row.DraftId, Headline: "",
                    FacebookCaption.Compose(row.FacebookCaption, ParseStringList(row.FacebookHashtagsJson)),
                    row.ArticleUrl);
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

    private ArticleToPublish ToArticle(PublishRow r) => new(
        r.DraftId,
        r.PublishRef,
        r.Headline,
        r.Subtitle,
        r.BodyMarkdown,
        r.Category,
        r.Region,
        ParseStringList(r.TagsJson),
        r.SeoTitle,
        r.SeoDescription,
        ToImage(r));

    private PublishImage? ToImage(PublishRow r)
    {
        if (r.ImageUrl is null)
            return null;
        var altText = r.ImageAltTextBg ?? r.DraftAltTextBg ?? r.Headline;
        if (!IsLocalFileKind(r.ImageKind))
            return new PublishImage(
                FileNameFromUrl(r.ImageUrl), r.ImageUrl, altText, r.ImageAttribution, LocalPath: null);

        return LocalPathFor(r.ImageUrl) is { } path
            ? new PublishImage(
                Path.GetFileName(path), SourceUrl: null, altText, r.ImageAttribution, LocalPath: path)
            : null; // unresolvable key — publish without a cover rather than reach outside the root
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

    /// <summary>Dapper row shape of <see cref="GetPendingFacebookAsync"/> and
    /// <see cref="GetFacebookPostForDraftAsync"/> — the Facebook caption/hashtags are preferred
    /// over the SeoDescription-based teaser when the draft carries one.
    ///
    /// **Parameter order must match the SELECT column order in both queries.** Dapper compares
    /// constructor parameter names against the returned columns *positionally*, so a record whose
    /// parameters are merely a permutation of the columns fails to materialise at all — the whole
    /// query throws "a parameterless default constructor or one matching signature … is required".
    /// BodyMarkdown sat at position 4 here while both SELECTs return it at 6, so the Facebook leg
    /// of publishing threw on every cycle (found on the production host 2026-08-04). Same defect
    /// as ViewSelectSql/ReviewRow in ReviewRepository, fixed 2026-07-30.</summary>
    private sealed record FacebookRow(
        long DraftId,
        string Headline,
        string? SeoDescription,
        string? FacebookCaption,
        string? FacebookHashtagsJson,
        string BodyMarkdown,
        string ArticleUrl);

    /// <summary>Dapper row shape of <see cref="GetApprovedForFacebookAsync"/> — adds the chosen
    /// image (source kind + URL/local path), PromptVersion (to detect editor-authored drafts
    /// that must publish verbatim), and the Facebook caption/hashtags (preferred over the body
    /// for AI drafts that carry one) to the Facebook-only post.</summary>
    private sealed record FacebookApprovedRow(
        long DraftId,
        string Headline,
        string BodyMarkdown,
        string? PromptVersion,
        string? FacebookCaption,
        string? FacebookHashtagsJson,
        string? ImageKind,
        string? ImageUrl);

    /// <summary>Dapper row shape of <see cref="GetApprovedUnpublishedAsync"/>.</summary>
    private sealed record PublishRow(
        long DraftId,
        Guid PublishRef,
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
