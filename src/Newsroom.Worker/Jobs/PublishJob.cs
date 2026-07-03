using Newsroom.Core.Operations;
using Newsroom.Core.Publishing;
using Newsroom.Core.Review;
using Newsroom.Infrastructure.Publishing;
using Newsroom.Infrastructure.Review;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Publishes Approved drafts per destination (docs/02-functional-spec.md §6, ADR-0007/0008):
/// each cycle first runs the Umbraco leg — Approved drafts without a successful 'umbraco'
/// record go to the site's publishing endpoint (idempotent by externalRef, so crash-replays
/// cannot duplicate articles) — then, when Facebook is configured, the Facebook leg posts a
/// link post for drafts whose site publish succeeded but whose 'facebook' record has not.
/// The draft status reflects the combination: Published when every configured destination
/// succeeded, PartiallyPublished while the site is live but Facebook is still pending or
/// exhausted (the repository recalculates it transactionally on each success). Every outcome
/// lands in nw_PublishRecord and the editor is told in Telegram — the site confirmation always
/// carries a copy-paste teaser block for the ~28 Facebook groups Meta gives no API for
/// (docs/05-integrations/facebook.md, ADR-0008). Transient failures retry next cycles up to
/// the destination's MaxAttempts, then alert once; rejections (described 4xx) are permanent
/// and alert immediately. A daily token health check warns when the page token dies. The
/// transient 'Publishing' status is not persisted — a cycle publishes synchronously and the
/// idempotency key makes replays safe. Without configuration the job logs one warning and
/// stays dormant — like TelegramJob, enabling it requires a restart.
/// </summary>
public sealed class PublishJob(
    IPublishRepository publishes,
    IReviewRepository reviews,
    IUmbracoPublisher publisher,
    IFacebookPublisher facebook,
    Lazy<ITelegramGateway> gateway,
    UmbracoOptions options,
    FacebookOptions facebookOptions,
    IJobHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<PublishJob> logger) : BackgroundService
{
    private const int MaxPerCycle = 3;
    private static readonly TimeSpan TokenCheckInterval = TimeSpan.FromDays(1);

    /// <summary>What "fully Published" means for this process: Facebook joins the required
    /// set only when configured, so a site-only setup keeps today's Approved → Published flow.</summary>
    private readonly string[] requiredDestinations = facebookOptions.IsConfigured
        ? [PublishDestinations.Umbraco, PublishDestinations.Facebook]
        : [PublishDestinations.Umbraco];

    private DateTime lastTokenCheckUtc; // MinValue → the first cycle checks immediately
    private DateTime lastTokenAlertUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured)
        {
            // Once per process by construction: ExecuteAsync runs once and the job ends here.
            logger.LogWarning("Publishing disabled: not configured (Umbraco:BaseUrl / ClientSecret)");
            return;
        }

        if (!facebookOptions.IsConfigured)
            logger.LogWarning(
                "Facebook publishing disabled: not configured (Facebook:PageId / AccessToken)");
        else if (facebookOptions.DryRun)
            logger.LogWarning(
                "Facebook publishing runs in dry-run mode (Facebook:DryRun) — posts are logged, not sent");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.CheckSeconds));
        try
        {
            do
            {
                await RunCycleAsync(stoppingToken);
                await heartbeat.BeatAsync(JobNames.Publish, stoppingToken);
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
        await RunUmbracoLegAsync(ct);
        if (facebookOptions.IsConfigured)
        {
            await RunFacebookLegAsync(ct);
            await CheckTokenHealthAsync(ct);
        }
    }

    // ---- Umbraco leg -------------------------------------------------------------------

    private async Task RunUmbracoLegAsync(CancellationToken ct)
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
                await HandleUmbracoFailureAsync(article, ex.Message, rejected: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Publishing draft {DraftId} failed", article.DraftId);
                await HandleUmbracoFailureAsync(article, ex.Message, rejected: false, ct);
            }
        }
    }

    private async Task PublishOneAsync(ArticleToPublish article, CancellationToken ct)
    {
        var result = await publisher.PublishAsync(article, ct);
        await publishes.RecordSuccessAsync(
            article.DraftId, PublishDestinations.Umbraco, result.ContentKey.ToString(), result.Url,
            requiredDestinations, ct);
        logger.LogInformation("🚀 Published draft {DraftId}: {Url}", article.DraftId, result.Url);
        await NotifyPublishedAsync(article, result.Url, ct);
    }

    /// <summary>Records the failed attempt. A rejection burns the whole attempt budget at once
    /// (see IPublishRepository), so it is terminal — and alerts — immediately; transient
    /// failures keep retrying and alert only when the cap is reached. Either way the draft
    /// flips to PublishFailed inside the repository when exhausted.</summary>
    private async Task HandleUmbracoFailureAsync(
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

    // ---- Facebook leg ------------------------------------------------------------------

    /// <summary>Posts the link post for drafts whose site publish succeeded (they sit in
    /// PartiallyPublished with Facebook budget left — the repository query owns that gate),
    /// with the same per-draft error isolation as the Umbraco leg.</summary>
    private async Task RunFacebookLegAsync(CancellationToken ct)
    {
        IReadOnlyList<FacebookPost> posts;
        try
        {
            posts = await publishes.GetPendingFacebookAsync(
                facebookOptions.MaxAttempts, MaxPerCycle, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not query drafts awaiting their Facebook post");
            return;
        }

        foreach (var post in posts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await PostOneToFacebookAsync(post, ct);
            }
            catch (PublishRejectedException ex)
            {
                logger.LogError("Draft {DraftId} was rejected by the Graph API: {Reason}",
                    post.DraftId, ex.Message);
                if (ex.Message.StartsWith(FacebookPublisher.TokenInvalidPrefix, StringComparison.Ordinal))
                    await TryAlertTokenInvalidAsync(ct); // dead token, not a bad post
                await HandleFacebookFailureAsync(post, ex.Message, rejected: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Posting draft {DraftId} to Facebook failed", post.DraftId);
                await HandleFacebookFailureAsync(post, ex.Message, rejected: false, ct);
            }
        }
    }

    private async Task PostOneToFacebookAsync(FacebookPost post, CancellationToken ct)
    {
        var result = await facebook.PublishAsync(post, ct);
        await publishes.RecordSuccessAsync(
            post.DraftId, PublishDestinations.Facebook, result.PostId, result.PermalinkUrl,
            requiredDestinations, ct);
        logger.LogInformation("📘 Posted draft {DraftId} to Facebook: {PostId}",
            post.DraftId, result.PostId);
        await NotifyFacebookPostedAsync(post, result, ct);
    }

    /// <summary>Same weighting as the Umbraco leg, but exhaustion never demotes the draft —
    /// the site is live, so it stays PartiallyPublished (the repository only flips Approved
    /// drafts to PublishFailed) and the editor is alerted exactly once to post by hand.</summary>
    private async Task HandleFacebookFailureAsync(
        FacebookPost post, string error, bool rejected, CancellationToken ct)
    {
        bool exhausted;
        try
        {
            exhausted = await publishes.RecordFailureAsync(
                post.DraftId, PublishDestinations.Facebook, error,
                attempts: rejected ? facebookOptions.MaxAttempts : 1, facebookOptions.MaxAttempts, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record the failed Facebook attempt for draft {DraftId}",
                post.DraftId);
            return;
        }

        if (!exhausted)
            return; // retried automatically next cycles

        logger.LogWarning("Draft {DraftId} ({Headline}) will not be re-posted to Facebook",
            post.DraftId, post.Headline);
        var reason = Truncate(error, 200);
        await TryAlertAsync(rejected
            ? $"⚠️ Facebook отхвърли поста за „{post.Headline}“: {reason}\nСайтът е публикуван — постни ръчно."
            : $"⚠️ Постът във Facebook за „{post.Headline}“ се провали след {facebookOptions.MaxAttempts} опита: {reason}\nСайтът е публикуван — постни ръчно.",
            ct);
    }

    /// <summary>Once per day, in live mode only: ask Graph whether the page token still works
    /// and alert at most once per day when it does not — expiry is expected and re-auth is a
    /// runbook task (docs/05-integrations/facebook.md).</summary>
    private async Task CheckTokenHealthAsync(CancellationToken ct)
    {
        if (facebookOptions.DryRun || DateTime.UtcNow - lastTokenCheckUtc < TokenCheckInterval)
            return;
        lastTokenCheckUtc = DateTime.UtcNow;

        if (await facebook.CheckTokenAsync(ct))
            return;
        logger.LogWarning("The Facebook page token failed its daily health check");
        await TryAlertTokenInvalidAsync(ct);
    }

    private async Task TryAlertTokenInvalidAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - lastTokenAlertUtc < TokenCheckInterval)
            return; // at most one token alert per day
        lastTokenAlertUtc = DateTime.UtcNow;
        await TryAlertAsync("⚠️ Facebook токенът е невалиден — виж runbook (docs/07-operations.md).", ct);
    }

    // ---- Telegram notifications --------------------------------------------------------

    /// <summary>Telegram confirmation with the live link plus the group-share helper block —
    /// Meta gives no API for groups (ADR-0008), so the editor gets a ready-to-paste text for
    /// the ~28 regional groups — and a "🚀 Публикувано" suffix on the original review card.
    /// Best-effort: the publish already succeeded and must never be redone because a
    /// notification failed.</summary>
    private async Task NotifyPublishedAsync(ArticleToPublish article, string url, CancellationToken ct)
    {
        var telegram = TelegramOptions.From(configuration);
        if (!telegram.IsConfigured)
            return;

        try
        {
            var headline = ReviewMessageRenderer.Escape(article.Headline);
            var link = ReviewMessageRenderer.Escape(url);
            var shareText = ReviewMessageRenderer.Escape(
                $"{article.Headline}\n\n{FacebookTeaser.Compose(article.SeoDescription, article.BodyMarkdown)}\n{url}");
            await gateway.Value.SendHtmlAsync(
                telegram.ReviewChatId,
                $"🚀 Публикувано: <b>{headline}</b>\n<a href=\"{link}\">{link}</a>\n\n"
                    + $"📋 Текст за групите (копирай и постни):\n<pre>{shareText}</pre>",
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

    /// <summary>Facebook confirmation with the permalink (or the raw post id when the
    /// permalink fetch failed); a dry-run "post" says so. Best-effort like the site one.</summary>
    private Task NotifyFacebookPostedAsync(
        FacebookPost post, FacebookPostResult result, CancellationToken ct)
    {
        var text = facebookOptions.DryRun
            ? $"📘 Публикувано във Facebook: „{post.Headline}“ (пробен режим — без реална публикация)"
            : $"📘 Публикувано във Facebook: „{post.Headline}“\n{result.PermalinkUrl ?? result.PostId}";
        return TryAlertAsync(text, ct);
    }

    /// <summary>Alert or confirmation to the review chat (escaped plain text); skipped when
    /// Telegram is not configured, best-effort otherwise — the outcome is already recorded.</summary>
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
            logger.LogWarning(ex, "Could not send the publish alert to Telegram");
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
