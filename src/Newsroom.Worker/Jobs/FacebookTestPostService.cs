using Newsroom.Core.Publishing;
using Newsroom.Core.Review;
using Newsroom.Infrastructure.Publishing;
using Newsroom.Infrastructure.Review;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Manual one-shot test hook (docs/05-integrations/facebook.md): when <c>Facebook:TestPostDraftId</c>
/// is set, posts that single draft to the page via the real <see cref="IFacebookPublisher"/> — no
/// Umbraco step, no PublishJob orchestration — then idles for the rest of the process. Inert when
/// the id is 0 (the production default), so it never fires by accident. Honours
/// <c>Facebook:DryRun</c> (preview only) and <c>Facebook:IncludeLink</c>.
/// </summary>
public sealed class FacebookTestPostService(
    IPublishRepository publishes,
    IFacebookPublisher facebook,
    FacebookOptions options,
    Lazy<ITelegramGateway> gateway,
    IConfiguration configuration,
    ILogger<FacebookTestPostService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.TestPostDraftId <= 0)
            return; // inert unless explicitly asked to post one draft

        if (!options.IsConfigured)
        {
            logger.LogWarning(
                "Facebook test post requested (draft {DraftId}) but Facebook is not configured (PageId/AccessToken)",
                options.TestPostDraftId);
            return;
        }

        try
        {
            var post = await publishes.GetFacebookPostForDraftAsync(options.TestPostDraftId, stoppingToken);
            if (post is null)
            {
                logger.LogWarning("Facebook test post: draft {DraftId} not found", options.TestPostDraftId);
                return;
            }

            var result = await facebook.PublishAsync(post, stoppingToken);
            if (options.DryRun)
                logger.LogInformation(
                    "📘 Facebook test post for draft {DraftId} ran in dry-run — nothing was sent (see the dry-run message above)",
                    post.DraftId);
            else
                logger.LogInformation("📘 Facebook test post sent for draft {DraftId}: {PostId} {Permalink}",
                    post.DraftId, result.PostId, result.PermalinkUrl);

            await NotifyAsync(post, result, stoppingToken);
        }
        catch (PublishRejectedException ex)
        {
            logger.LogError("Facebook test post for draft {DraftId} was rejected: {Reason}",
                options.TestPostDraftId, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Facebook test post for draft {DraftId} failed", options.TestPostDraftId);
        }
    }

    /// <summary>Best-effort confirmation to the review chat, mirroring PublishJob's Facebook note.</summary>
    private async Task NotifyAsync(FacebookPost post, FacebookPostResult result, CancellationToken ct)
    {
        var telegram = TelegramOptions.From(configuration);
        if (!telegram.IsConfigured)
            return;

        var text = options.DryRun
            ? $"📘 Facebook тест (пробен режим) за „{post.DisplayTitle}“ — нищо не е публикувано."
            : $"📘 Публикувано във Facebook (тест): „{post.DisplayTitle}“\n{result.PermalinkUrl ?? result.PostId}";
        try
        {
            await gateway.Value.SendHtmlAsync(telegram.ReviewChatId, ReviewMessageRenderer.Escape(text),
                withReviewButtons: false, draftIdForButtons: null, scheduleButtonLabel: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the Facebook test-post confirmation to Telegram");
        }
    }
}
