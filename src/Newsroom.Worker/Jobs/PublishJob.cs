using Newsroom.Core.Publishing;
using Newsroom.Core.Review;
using Newsroom.Infrastructure.Publishing;
using Newsroom.Infrastructure.Review;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Publishes Approved drafts to the Umbraco site (docs/02-functional-spec.md §6, ADR-0007):
/// each cycle picks Approved drafts without a successful 'umbraco' record, posts them to the
/// site's publishing endpoint (idempotent by externalRef, so crash-replays cannot duplicate
/// articles), records the outcome in nw_PublishRecord and confirms to the editor in Telegram
/// with the live link. Transient failures retry next cycles up to Umbraco:MaxAttempts, then
/// alert once; endpoint rejections (HTTP 400) are permanent and alert immediately. The
/// transient 'Publishing' status is not persisted — a cycle publishes synchronously and the
/// idempotency key makes replays safe. Facebook is Phase 6; nw_PublishRecord is already
/// per-destination. Without configuration the job logs one warning and stays dormant — like
/// TelegramJob, enabling it requires a restart.
/// </summary>
public sealed class PublishJob(
    IPublishRepository publishes,
    IReviewRepository reviews,
    IUmbracoPublisher publisher,
    Lazy<ITelegramGateway> gateway,
    UmbracoOptions options,
    IConfiguration configuration,
    ILogger<PublishJob> logger) : BackgroundService
{
    private const int MaxPerCycle = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured)
        {
            // Once per process by construction: ExecuteAsync runs once and the job ends here.
            logger.LogWarning("Publishing disabled: not configured (Umbraco:BaseUrl / ClientSecret)");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.CheckSeconds));
        try
        {
            do
            {
                await RunCycleAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        IReadOnlyList<ArticleToPublish> articles;
        try
        {
            articles = await publishes.GetApprovedUnpublishedAsync(
                PublishDestinations.Umbraco, options.MaxAttempts, MaxPerCycle, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not query Approved drafts awaiting publication");
            return; // the DB is unwell; publishing now would fail too
        }

        foreach (var article in articles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await PublishOneAsync(article, ct);
            }
            catch (PublishRejectedException ex)
            {
                // 400-class: the payload itself is refused — retrying cannot help.
                logger.LogError("Draft {DraftId} was rejected by the publishing endpoint: {Reason}",
                    article.DraftId, ex.Message);
                await HandleFailureAsync(article, ex.Message, rejected: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Publishing draft {DraftId} failed", article.DraftId);
                await HandleFailureAsync(article, ex.Message, rejected: false, ct);
            }
        }
    }

    private async Task PublishOneAsync(ArticleToPublish article, CancellationToken ct)
    {
        var result = await publisher.PublishAsync(article, ct);
        await publishes.RecordSuccessAsync(
            article.DraftId, PublishDestinations.Umbraco, result.ContentKey.ToString(), result.Url, ct);
        logger.LogInformation("🚀 Published draft {DraftId}: {Url}", article.DraftId, result.Url);
        await NotifyPublishedAsync(article, result.Url, ct);
    }

    /// <summary>Records the failed attempt. A rejection burns the whole attempt budget at once
    /// (see IPublishRepository), so it is terminal — and alerts — immediately; transient
    /// failures keep retrying and alert only when the cap is reached. Either way the draft
    /// flips to PublishFailed inside the repository when exhausted.</summary>
    private async Task HandleFailureAsync(
        ArticleToPublish article, string error, bool rejected, CancellationToken ct)
    {
        bool exhausted;
        try
        {
            exhausted = await publishes.RecordFailureAsync(
                article.DraftId, PublishDestinations.Umbraco, error,
                attempts: rejected ? options.MaxAttempts : 1, options.MaxAttempts, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record the failed publish attempt for draft {DraftId}",
                article.DraftId);
            return;
        }

        if (!exhausted)
            return; // retried automatically next cycles

        logger.LogWarning("Draft {DraftId} ({Headline}) will not be retried", article.DraftId,
            article.Headline);
        var reason = Truncate(error, 200);
        await TryAlertAsync(rejected
            ? $"⚠️ Публикуването на „{article.Headline}“ е отхвърлено: {reason}"
            : $"⚠️ Публикуването на „{article.Headline}“ се провали след {options.MaxAttempts} опита: {reason}",
            ct);
    }

    /// <summary>Telegram confirmation with the live link, plus a "🚀 Публикувано" suffix on
    /// the original review card. Best-effort: the publish already succeeded and must never be
    /// redone because a notification failed.</summary>
    private async Task NotifyPublishedAsync(ArticleToPublish article, string url, CancellationToken ct)
    {
        var telegram = TelegramOptions.From(configuration);
        if (!telegram.IsConfigured)
            return;

        try
        {
            var headline = ReviewMessageRenderer.Escape(article.Headline);
            var link = ReviewMessageRenderer.Escape(url);
            await gateway.Value.SendHtmlAsync(
                telegram.ReviewChatId,
                $"🚀 Публикувано: <b>{headline}</b>\n<a href=\"{link}\">{link}</a>",
                withReviewButtons: false, draftIdForButtons: null, ct);

            var view = await reviews.GetReviewViewAsync(article.DraftId, ct);
            if (view?.TelegramMessageId is { } messageId)
            {
                var html = ReviewMessageRenderer.RenderHtml(view)
                    + ReviewMessageRenderer.RenderResolvedSuffix("🚀 Публикувано");
                await gateway.Value.EditHtmlAsync(
                    telegram.ReviewChatId, messageId, html, removeButtons: true, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the publish confirmation for draft {DraftId}",
                article.DraftId);
        }
    }

    /// <summary>Failure alert to the review chat (escaped plain text); skipped when Telegram
    /// is not configured, best-effort otherwise — the failure is already recorded.</summary>
    private async Task TryAlertAsync(string text, CancellationToken ct)
    {
        var telegram = TelegramOptions.From(configuration);
        if (!telegram.IsConfigured)
            return;

        try
        {
            await gateway.Value.SendHtmlAsync(
                telegram.ReviewChatId, ReviewMessageRenderer.Escape(text),
                withReviewButtons: false, draftIdForButtons: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the publish-failure alert to Telegram");
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
