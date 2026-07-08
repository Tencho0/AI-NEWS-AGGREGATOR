using System.Globalization;
using System.Text;
using System.Text.Json;

using Dapper;

using Newsroom.Core.Drafting;
using Newsroom.Core.Review;
using Newsroom.Core.Scraping;
using Newsroom.Core.Trends;
using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Repositories;

/// <summary>
/// <see cref="IReviewRepository"/> over Dapper. Editor transitions are guarded UPDATEs
/// (WHERE Status = 'PendingReview') so double-taps and stale buttons are no-ops returning
/// false, and every successful transition writes its nw_ReviewAction row in the same
/// transaction (docs/05-integrations/telegram.md interaction rules).
/// </summary>
public sealed class ReviewRepository(IDbConnectionFactory db) : IReviewRepository
{
    private const string UpdateOffsetKey = "Telegram:UpdateOffset";
    private const string ChangeInstructionsKind = "ChangeInstructions";
    /// <summary>nw_DraftImage.SourceKind values (ADR-0009; mirrors PublishRepository).</summary>
    private const string StockKind = "stock";
    private const string EditorUploadKind = "editor-upload";
    private const string EditorUploadAttribution = "редакторска снимка";
    /// <summary>Mirrors HeartbeatService.ConfigKey (the Worker project is not referenced here).</summary>
    private const string HeartbeatKey = "Worker:LastHeartbeatUtc";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Shared projection for <see cref="DraftReviewView"/> (see <see cref="ReviewRow"/>).</summary>
    private const string ViewSelectSql =
        """
        SELECT d.Id AS DraftId, d.Version, t.Label AS TopicLabel, t.Score AS TopicScore,
               (SELECT COUNT(*) FROM dbo.nw_TopicArticle ta WHERE ta.TopicId = d.TopicId) AS SourceCount,
               ISNULL(d.Headline, '') AS Headline, d.Subtitle, ISNULL(d.BodyMarkdown, '') AS BodyMarkdown,
               ISNULL(d.Category, '') AS Category, d.Region, d.TagsJson, d.SourcesJson,
               d.FlaggedClaimsJson, d.Confidence, d.Cost, d.Model,
               (SELECT COUNT(*) FROM dbo.nw_DraftImage di WHERE di.DraftId = d.Id) AS ImageCount,
               d.TelegramMessageId
        FROM dbo.nw_Draft d
        JOIN dbo.nw_Topic t ON t.Id = d.TopicId
        """;

    public async Task<IReadOnlyList<DraftReviewView>> GetUnsentPendingReviewsAsync(
        int max, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<ReviewRow>(
            $"""
            SELECT TOP (@max) * FROM (
            {ViewSelectSql}
            WHERE d.Status = @pendingStatus AND d.TelegramMessageId IS NULL
            ) unsent
            ORDER BY unsent.DraftId
            """,
            new { max, pendingStatus = nameof(DraftStatus.PendingReview) });
        return rows.Select(ToView).ToList();
    }

    public async Task<DraftReviewView?> GetReviewViewAsync(long draftId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<ReviewRow>(
            $"""
            {ViewSelectSql}
            WHERE d.Id = @draftId
            """,
            new { draftId });
        var row = rows.FirstOrDefault();
        return row is null ? null : ToView(row);
    }

    public async Task<IReadOnlyList<(long DraftId, string TopicLabel, string Error)>> GetUnreportedRegenFailuresAsync(
        int max, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(long DraftId, string TopicLabel, string Error)>(
            """
            SELECT TOP (@max) d.Id, t.Label, COALESCE(d.Error, N'неизвестна грешка')
            FROM dbo.nw_Draft d
            JOIN dbo.nw_Topic t ON t.Id = d.TopicId
            WHERE d.Status = @failedStatus
              AND d.RegenInstructions IS NOT NULL
              AND d.TelegramMessageId IS NULL
            ORDER BY d.Id
            """,
            new { max, failedStatus = nameof(DraftStatus.GenerationFailed) });
        return rows.ToList();
    }

    public async Task SetTelegramMessageIdAsync(long draftId, long messageId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // PostedAtUtc anchors the review-TTL sweep (ExpireStaleAsync) to when the card was actually
        // shown to the editor. Stamp it only for a genuine review post (Status = PendingReview):
        // this method is reused to record the message id of a regeneration-failure notice on a
        // GenerationFailed draft, which must not start a review clock.
        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET TelegramMessageId = @messageId,
                PostedAtUtc = CASE WHEN Status = @pendingStatus THEN SYSUTCDATETIME() ELSE PostedAtUtc END,
                UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @draftId
            """,
            new { draftId, messageId, pendingStatus = nameof(DraftStatus.PendingReview) });
    }

    public async Task<IReadOnlyList<(long DraftId, string Url, string? Caption, int Total)>> GetPendingPhotoDispatchAsync(
        int max, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // CROSS APPLY = "has a dispatchable stock image"; the top image mirrors the publish
        // path's pick (Selected DESC, Ordinal), so the editor reviews what would be published.
        var rows = await connection.QueryAsync<(long DraftId, string Url, string? Attribution, string? AltTextBg, int Total)>(
            """
            SELECT TOP (@max)
                   d.Id AS DraftId, img.Url, img.Attribution, img.AltTextBg,
                   (SELECT COUNT(*) FROM dbo.nw_DraftImage dc
                    WHERE dc.DraftId = d.Id AND dc.SourceKind = @stockKind) AS Total
            FROM dbo.nw_Draft d
            CROSS APPLY (
                SELECT TOP 1 di.Url, di.Attribution, di.AltTextBg
                FROM dbo.nw_DraftImage di
                WHERE di.DraftId = d.Id AND di.SourceKind = @stockKind
                ORDER BY di.Selected DESC, di.Ordinal
            ) img
            WHERE d.Status = @pendingStatus
              AND d.TelegramMessageId IS NOT NULL
              AND d.TelegramPhotoMessageId IS NULL
            ORDER BY d.Id
            """,
            new { max, stockKind = StockKind, pendingStatus = nameof(DraftStatus.PendingReview) });
        return rows
            .Select(r => (r.DraftId, r.Url, ComposeImageCaption(r.Attribution, r.AltTextBg), r.Total))
            .ToList();
    }

    public async Task SetTelegramPhotoMessageIdAsync(long draftId, long messageId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET TelegramPhotoMessageId = @messageId, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @draftId
            """,
            new { draftId, messageId });
    }

    public async Task<long?> GetTelegramPhotoMessageIdAsync(long draftId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long?>(
            """
            SELECT TelegramPhotoMessageId FROM dbo.nw_Draft WHERE Id = @draftId
            """,
            new { draftId });
    }

    public async Task<(long DraftId, string Url, string? Caption, int Index, int Total)?> CycleToNextImageAsync(
        long draftId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        var status = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT Status FROM dbo.nw_Draft WHERE Id = @draftId
            """,
            new { draftId },
            transaction);
        if (status != nameof(DraftStatus.PendingReview))
            return null; // stale 🖼 press on a resolved draft; transaction rolls back

        var images = (await connection.QueryAsync<(long Id, string SourceKind, string Url, string? Attribution, string? AltTextBg, bool Selected)>(
            """
            SELECT di.Id, di.SourceKind, di.Url, di.Attribution, di.AltTextBg, di.Selected
            FROM dbo.nw_DraftImage di
            WHERE di.DraftId = @draftId
            ORDER BY di.Ordinal
            """,
            new { draftId },
            transaction)).ToList();

        var stock = images.Where(i => i.SourceKind == StockKind).ToList();
        if (stock.Count < 2)
            return null; // nothing to cycle to

        // Current position: the selected stock image; without one the card shows the lowest
        // ordinal (advance to the second), unless an editor upload holds the selection — then
        // cycling returns to the first stock suggestion.
        var currentIndex = stock.FindIndex(i => i.Selected);
        var nextIndex = currentIndex >= 0
            ? (currentIndex + 1) % stock.Count
            : images.Any(i => i.Selected) ? 0 : 1;
        var next = stock[nextIndex];

        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_DraftImage SET Selected = 0 WHERE DraftId = @draftId AND Selected = 1;
            UPDATE dbo.nw_DraftImage SET Selected = 1 WHERE Id = @imageId;
            """,
            new { draftId, imageId = next.Id },
            transaction);

        transaction.Commit();
        return (draftId, next.Url, ComposeImageCaption(next.Attribution, next.AltTextBg),
            nextIndex + 1, stock.Count);
    }

    public async Task<long?> FindDraftByReviewMessageAsync(long messageId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // Message ids are unique within a chat and there is a single review chat (docs/05).
        return await connection.ExecuteScalarAsync<long?>(
            """
            SELECT TOP 1 Id FROM dbo.nw_Draft
            WHERE Status = @pendingStatus
              AND (TelegramMessageId = @messageId OR TelegramPhotoMessageId = @messageId)
            ORDER BY Id DESC
            """,
            new { messageId, pendingStatus = nameof(DraftStatus.PendingReview) });
    }

    public async Task<bool> AttachEditorImageAsync(
        long draftId, string localPath, string fileId, long userId, string? userName,
        CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        var isPending = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM dbo.nw_Draft WHERE Id = @draftId AND Status = @pendingStatus
            """,
            new { draftId, pendingStatus = nameof(DraftStatus.PendingReview) },
            transaction);
        if (isPending == 0)
            return false; // resolved while the photo travelled; transaction rolls back

        // The upload wins selection over every suggestion (docs/05 interaction rules). Url is
        // the worker-local file the publisher inlines; ThumbUrl keeps the Telegram file_id so
        // the chat can re-show the photo without re-uploading.
        await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_DraftImage SET Selected = 0 WHERE DraftId = @draftId AND Selected = 1;
            INSERT INTO dbo.nw_DraftImage (DraftId, Ordinal, SourceKind, Url, ThumbUrl, Attribution, Selected)
            SELECT @draftId, ISNULL(MAX(di.Ordinal), 0) + 1, @kind, @url, @thumbUrl, @attribution, 1
            FROM dbo.nw_DraftImage di
            WHERE di.DraftId = @draftId;
            """,
            new
            {
                draftId,
                kind = EditorUploadKind,
                url = Truncate(localPath, 2000),
                thumbUrl = Truncate(fileId, 2000),
                attribution = EditorUploadAttribution,
            },
            transaction);

        await InsertReviewActionAsync(
            connection, transaction, draftId, userId, userName, "ImageAttached", localPath);

        transaction.Commit();
        return true;
    }

    /// <summary>The photo message caption: attribution (credit per licence, docs/05
    /// images.md) plus the Bulgarian alt text; null when the image carries neither.</summary>
    private static string? ComposeImageCaption(string? attribution, string? altText)
    {
        var hasAttribution = !string.IsNullOrWhiteSpace(attribution);
        var hasAltText = !string.IsNullOrWhiteSpace(altText);
        return (hasAttribution, hasAltText) switch
        {
            (true, true) => $"📷 {attribution}\n{altText}",
            (true, false) => $"📷 {attribution}",
            (false, true) => altText,
            _ => null,
        };
    }

    public Task<bool> TryApproveAsync(long draftId, long userId, string? userName, CancellationToken ct) =>
        TryResolveAsync(draftId, DraftStatus.Approved, "Approved", userId, userName, ct);

    public Task<bool> TryRejectAsync(long draftId, long userId, string? userName, CancellationToken ct) =>
        TryResolveAsync(draftId, DraftStatus.Rejected, "Rejected", userId, userName, ct);

    public async Task<bool> TryStartRegenerationAsync(
        long draftId, string instructions, long userId, string? userName, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        var superseded = await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET Status = @supersededStatus, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @draftId AND Status = @pendingStatus
            """,
            new
            {
                draftId,
                supersededStatus = nameof(DraftStatus.Superseded),
                pendingStatus = nameof(DraftStatus.PendingReview),
            },
            transaction);
        if (superseded == 0)
            return false; // not PendingReview (double-tap or stale button); transaction rolls back

        // MAX+1 mirrors SaveDraftAsync — only the single-threaded review loop inserts here.
        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.nw_Draft (TopicId, Version, Status, RegenInstructions, ParentDraftId)
            SELECT d.TopicId,
                   (SELECT ISNULL(MAX(x.Version), 0) + 1 FROM dbo.nw_Draft x WHERE x.TopicId = d.TopicId),
                   @generatingStatus, @instructions, d.Id
            FROM dbo.nw_Draft d
            WHERE d.Id = @draftId
            """,
            new { draftId, generatingStatus = nameof(DraftStatus.Generating), instructions },
            transaction);

        await InsertReviewActionAsync(
            connection, transaction, draftId, userId, userName, "ChangesRequested", instructions);

        transaction.Commit();
        return true;
    }

    public async Task<IReadOnlyList<(long DraftId, long? MessageId)>> ExpireStaleAsync(
        DateTime cutoffUtc, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // Expire on PostedAtUtc (when the card was shown), not CreatedAtUtc (when the row was
        // made) — see 0010_review_posted_at.sql. A PendingReview draft not yet dispatched has
        // PostedAtUtc NULL; the `< @cutoffUtc` comparison is UNKNOWN for NULL, so it is correctly
        // excluded and never expires before the editor has actually seen it.
        var rows = await connection.QueryAsync<(long DraftId, long? MessageId)>(
            """
            UPDATE dbo.nw_Draft
            SET Status = @expiredStatus, UpdatedAtUtc = SYSUTCDATETIME()
            OUTPUT INSERTED.Id AS DraftId, INSERTED.TelegramMessageId AS MessageId
            WHERE Status = @pendingStatus AND PostedAtUtc < @cutoffUtc
            """,
            new
            {
                cutoffUtc,
                expiredStatus = nameof(DraftStatus.Expired),
                pendingStatus = nameof(DraftStatus.PendingReview),
            });
        return rows.ToList();
    }

    public async Task<(long DraftId, string Headline)?> GetDraftHeadlineAsync(
        long draftId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(long DraftId, string? Headline)>(
            """
            SELECT Id AS DraftId, Headline FROM dbo.nw_Draft WHERE Id = @draftId
            """,
            new { draftId });
        var row = rows.ToList();
        return row.Count == 0 ? null : (row[0].DraftId, row[0].Headline ?? "");
    }

    public async Task<long?> GetPendingConversationAsync(long chatId, long userId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long?>(
            """
            SELECT DraftId FROM dbo.nw_TelegramPending
            WHERE ChatId = @chatId AND UserId = @userId AND Kind = @kind
            """,
            new { chatId, userId, kind = ChangeInstructionsKind });
    }

    public async Task SetPendingConversationAsync(
        long chatId, long userId, long draftId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        // One conversation per (chat, user) — a second ✏️ replaces the first (unique index).
        await connection.ExecuteAsync(
            """
            DELETE FROM dbo.nw_TelegramPending WHERE ChatId = @chatId AND UserId = @userId;
            INSERT INTO dbo.nw_TelegramPending (ChatId, UserId, DraftId, Kind)
            VALUES (@chatId, @userId, @draftId, @kind);
            """,
            new { chatId, userId, draftId, kind = ChangeInstructionsKind });
    }

    public async Task ClearPendingConversationAsync(long chatId, long userId, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            DELETE FROM dbo.nw_TelegramPending WHERE ChatId = @chatId AND UserId = @userId
            """,
            new { chatId, userId });
    }

    public async Task<long> GetUpdateOffsetAsync(CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var value = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT [Value] FROM dbo.nw_Config WHERE [Key] = @key
            """,
            new { key = UpdateOffsetKey });
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            ? offset
            : 0;
    }

    public Task SetUpdateOffsetAsync(long offset, CancellationToken ct) =>
        SetRuntimeFlagAsync(UpdateOffsetKey, offset.ToString(CultureInfo.InvariantCulture), ct);

    public async Task<string> BuildStatusSummaryAsync(CancellationToken ct)
    {
        var todayUtc = DateTime.UtcNow.Date;

        using var connection = await db.OpenAsync(ct);
        using var multi = await connection.QueryMultipleAsync(
            """
            SELECT Status, COUNT(*) AS Cnt
            FROM dbo.nw_SourceArticle
            WHERE FirstSeenAtUtc >= @todayUtc
            GROUP BY Status;

            SELECT COUNT(*) FROM dbo.nw_Topic WHERE Status <> @doneStatus;

            SELECT COUNT(*) FROM dbo.nw_Topic WHERE Status = @hotStatus;

            SELECT Status, COUNT(*) AS Cnt FROM dbo.nw_Draft GROUP BY Status;

            SELECT ISNULL(SUM(RequestCount), 0) AS Requests, ISNULL(SUM(TokensIn), 0) AS TokensIn,
                   ISNULL(SUM(TokensOut), 0) AS TokensOut, ISNULL(SUM(Cost), 0) AS Cost
            FROM dbo.nw_CostLedger
            WHERE AtUtc >= @todayUtc;

            SELECT [Value] FROM dbo.nw_Config WHERE [Key] = @heartbeatKey;
            """,
            new
            {
                todayUtc,
                doneStatus = nameof(TopicStatus.Done),
                hotStatus = nameof(TopicStatus.Hot),
                heartbeatKey = HeartbeatKey,
            });

        var articles = (await multi.ReadAsync<(string Status, int Cnt)>()).ToList();
        var openTopics = await multi.ReadSingleAsync<int>();
        var hotTopics = await multi.ReadSingleAsync<int>();
        var drafts = (await multi.ReadAsync<(string Status, int Cnt)>()).ToList();
        var ai = await multi.ReadSingleAsync<(int Requests, int TokensIn, int TokensOut, decimal Cost)>();
        var heartbeat = (await multi.ReadAsync<string?>()).FirstOrDefault();

        var summary = new StringBuilder();
        summary.Append("📊 Състояние на конвейера").Append('\n');
        summary.Append("Статии днес: ").Append(articles.Sum(a => a.Cnt));
        if (articles.Count > 0)
            summary.Append(" (")
                .Append(string.Join(" · ", articles.OrderBy(a => a.Status).Select(a => $"{a.Status} {a.Cnt}")))
                .Append(')');
        summary.Append('\n');
        summary.Append("Отворени теми: ").Append(openTopics)
            .Append(" (").Append(hotTopics).Append(" горещи)").Append('\n');
        summary.Append("Чернови: ").Append(drafts.Count == 0
            ? "няма"
            : string.Join(" · ", drafts.OrderBy(d => d.Status).Select(d => $"{d.Status} {d.Cnt}")));
        summary.Append('\n');
        summary.Append("AI днес: ").Append(ai.Requests).Append(" заявки · ")
            .Append(ai.TokensIn).Append('/').Append(ai.TokensOut).Append(" токена · $")
            .Append(ai.Cost.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');
        summary.Append("Последен пулс: ").Append(string.IsNullOrWhiteSpace(heartbeat) ? "няма" : heartbeat);
        return summary.ToString();
    }

    public async Task<string> BuildTopicsSummaryAsync(int max, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var topics = (await connection.QueryAsync<(int Id, string Label, double Score, string Status, int Articles)>(
            """
            SELECT TOP (@max) t.Id, t.Label, t.Score, t.Status,
                   (SELECT COUNT(*) FROM dbo.nw_TopicArticle ta WHERE ta.TopicId = t.Id) AS Articles
            FROM dbo.nw_Topic t
            WHERE t.Status <> @doneStatus
            ORDER BY t.Score DESC, t.Id
            """,
            new { max, doneStatus = nameof(TopicStatus.Done) })).ToList();

        if (topics.Count == 0)
            return "Няма отворени теми.";

        var summary = new StringBuilder();
        summary.Append("🔥 Отворени теми (топ ").Append(topics.Count).Append("):");
        foreach (var topic in topics)
        {
            summary.Append('\n').Append('#').Append(topic.Id).Append(' ').Append(topic.Label)
                .Append(" — ").Append(topic.Score.ToString("0.0", CultureInfo.InvariantCulture))
                .Append(" (").Append(topic.Status).Append(", ").Append(topic.Articles)
                .Append(topic.Articles == 1 ? " статия)" : " статии)");
        }
        return summary.ToString();
    }

    public async Task<bool> MuteTopicAsync(int topicId, int hours, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var rows = await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Topic
            SET MutedUntilUtc = DATEADD(HOUR, @hours, SYSUTCDATETIME())
            WHERE Id = @topicId
            """,
            new { topicId, hours });
        return rows > 0;
    }

    public async Task SetRuntimeFlagAsync(string key, string value, CancellationToken ct)
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

    public async Task<bool> GetRuntimeFlagAsync(string key, bool defaultValue, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        var value = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT [Value] FROM dbo.nw_Config WHERE [Key] = @key
            """,
            new { key });
        return bool.TryParse(value, out var flag) ? flag : defaultValue;
    }

    private async Task<bool> TryResolveAsync(
        long draftId, DraftStatus newStatus, string action, long userId, string? userName,
        CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        var rows = await connection.ExecuteAsync(
            """
            UPDATE dbo.nw_Draft
            SET Status = @newStatus, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @draftId AND Status = @pendingStatus
            """,
            new { draftId, newStatus = newStatus.ToString(), pendingStatus = nameof(DraftStatus.PendingReview) },
            transaction);
        if (rows == 0)
            return false; // not PendingReview (double-tap or stale button); transaction rolls back

        await InsertReviewActionAsync(connection, transaction, draftId, userId, userName, action, comment: null);

        transaction.Commit();
        return true;
    }

    private static Task InsertReviewActionAsync(
        System.Data.IDbConnection connection, System.Data.IDbTransaction transaction,
        long draftId, long userId, string? userName, string action, string? comment) =>
        connection.ExecuteAsync(
            """
            INSERT INTO dbo.nw_ReviewAction (DraftId, TelegramUserId, UserName, [Action], Comment)
            VALUES (@draftId, @userId, @userName, @action, @comment)
            """,
            new { draftId, userId, userName = Truncate(userName, 200), action, comment },
            transaction);

    private static DraftReviewView ToView(ReviewRow r) => new(
        r.DraftId,
        r.Version,
        r.TopicLabel,
        r.TopicScore,
        r.SourceCount,
        r.Headline,
        r.Subtitle,
        r.BodyMarkdown,
        r.Category,
        r.Region,
        ParseStringList(r.TagsJson),
        ParseSources(r.SourcesJson),
        ParseStringList(r.FlaggedClaimsJson),
        r.Confidence,
        r.Cost,
        r.Model,
        r.ImageCount,
        r.TelegramMessageId);

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

    private static IReadOnlyList<(string Name, string Url)> ParseSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var sources = JsonSerializer.Deserialize<List<SourceRef?>>(json, JsonOptions);
            return sources?
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Url))
                .Select(s => (s!.SourceName ?? s.Url!, s.Url!))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    /// <summary>Dapper row shape of <see cref="ViewSelectSql"/>.</summary>
    private sealed record ReviewRow(
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
        string? TagsJson,
        string? SourcesJson,
        string? FlaggedClaimsJson,
        double? Confidence,
        decimal Cost,
        string? Model,
        int ImageCount,
        long? TelegramMessageId);

    /// <summary>Wire shape of one nw_Draft.SourcesJson entry (see DraftRepository.SourceRef).</summary>
    private sealed record SourceRef(string? Url, string? SourceName);
}
