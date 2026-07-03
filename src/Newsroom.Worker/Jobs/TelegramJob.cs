using Newsroom.Core.Review;
using Newsroom.Infrastructure.Review;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// The Telegram editorial review loop (docs/02-functional-spec.md §5, ADR-0006): posts
/// PendingReview drafts to the review chat with ✅/✏️/❌ buttons, expires stale ones, and long
/// polls getUpdates for editor actions — the poll timeout is the pacing, so this is a continuous
/// loop rather than a PeriodicTimer job. All routing decisions live in
/// <see cref="ReviewUpdateRouter"/>; all state transitions are guarded in the repository, which
/// makes reprocessing after a crash (offset not yet persisted) harmless. Without configuration
/// (token/chat/allowlist) the job logs one warning and stays dormant — like the AI stages,
/// enabling it requires a restart.
/// </summary>
public sealed class TelegramJob(
    IReviewRepository reviews,
    Lazy<ITelegramGateway> gateway,
    IConfiguration configuration,
    ILogger<TelegramJob> logger) : BackgroundService
{
    /// <summary>nw_Config flag flipped by /pause and /resume; DraftJob reads it every cycle.</summary>
    public const string DraftPausedKey = "Draft:Paused";

    private const int TopicsToShow = 10;

    private DateTime lastSweepUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = TelegramOptions.From(configuration);
        if (!options.IsConfigured)
        {
            // Once per process by construction: ExecuteAsync runs once and the job ends here.
            logger.LogWarning(
                "Telegram review disabled: not configured (Telegram:BotToken / ReviewChatId / AllowedUserIds)");
            return;
        }

        var allowedUsers = options.AllowedUserIds.ToHashSet();
        long? offset = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    offset ??= await reviews.GetUpdateOffsetAsync(stoppingToken);
                    await DispatchPendingAsync(options, stoppingToken);
                    await SweepExpiredAsync(options, stoppingToken);
                    offset = await PollAsync(options, allowedUsers, offset.Value, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Telegram review cycle failed");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    /// <summary>(a) Dispatch: every PendingReview draft not yet posted gets its review card.</summary>
    private async Task DispatchPendingAsync(TelegramOptions options, CancellationToken ct)
    {
        var pending = await reviews.GetUnsentPendingReviewsAsync(options.MaxSendPerCycle, ct);
        foreach (var view in pending)
        {
            ct.ThrowIfCancellationRequested();
            var html = ReviewMessageRenderer.RenderHtml(view);
            var messageId = await gateway.Value.SendHtmlAsync(
                options.ReviewChatId, html, withReviewButtons: true, view.DraftId, ct);
            await reviews.SetTelegramMessageIdAsync(view.DraftId, messageId, ct);
            logger.LogInformation("📨 Draft {DraftId} v{Version} posted for review (message {MessageId})",
                view.DraftId, view.Version, messageId);
        }
    }

    /// <summary>(b) TTL sweep, at most once per minute: unactioned drafts expire
    /// (docs/02-functional-spec.md §5 — news goes stale).</summary>
    private async Task SweepExpiredAsync(TelegramOptions options, CancellationToken ct)
    {
        if (DateTime.UtcNow - lastSweepUtc < TimeSpan.FromMinutes(1))
            return;
        lastSweepUtc = DateTime.UtcNow;

        var cutoffUtc = DateTime.UtcNow.AddHours(-options.ReviewTtlHours);
        var expired = await reviews.ExpireStaleAsync(cutoffUtc, ct);
        foreach (var (draftId, messageId) in expired)
        {
            ct.ThrowIfCancellationRequested();
            if (messageId is { } sentMessageId)
                await EditResolvedAsync(options.ReviewChatId, sentMessageId, draftId,
                    "⌛ Изтекло — не е прегледано навреме", ct);
            logger.LogInformation("Draft {DraftId} expired unreviewed (TTL {TtlHours}h)",
                draftId, options.ReviewTtlHours);
        }
    }

    /// <summary>(c) Long poll + process in update-id order, then persist the offset. Telegram
    /// re-delivers everything after the last persisted offset, so each action is guarded
    /// (idempotent) in the repository.</summary>
    private async Task<long> PollAsync(
        TelegramOptions options, IReadOnlySet<long> allowedUsers, long offset, CancellationToken ct)
    {
        var batch = await gateway.Value.GetUpdatesAsync(offset, options.PollTimeoutSeconds, ct);

        var ordered = batch.Callbacks
            .Select(c => (c.UpdateId, Callback: (TgCallback?)c, Text: (TgText?)null))
            .Concat(batch.Texts.Select(t => (t.UpdateId, Callback: (TgCallback?)null, Text: (TgText?)t)))
            .OrderBy(u => u.UpdateId);
        foreach (var (_, callback, text) in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (callback is not null)
                await HandleCallbackAsync(callback, options, allowedUsers, ct);
            else if (text is not null)
                await HandleTextAsync(text, options, allowedUsers, ct);
        }

        if (batch.NextOffset != offset)
            await reviews.SetUpdateOffsetAsync(batch.NextOffset, ct);
        return batch.NextOffset;
    }

    private async Task HandleCallbackAsync(
        TgCallback callback, TelegramOptions options, IReadOnlySet<long> allowedUsers, CancellationToken ct)
    {
        var command = ReviewUpdateRouter.RouteCallback(callback, allowedUsers, options.ReviewChatId);
        var editor = callback.UserName ?? callback.UserId.ToString();
        switch (command)
        {
            case ApproveDraft approve:
                await ResolveDraftAsync(callback, approve.DraftId,
                    await reviews.TryApproveAsync(approve.DraftId, callback.UserId, callback.UserName, ct),
                    toast: "✅ Одобрено", statusLine: $"✅ Одобрено от {editor}", ct);
                break;

            case RejectDraft reject:
                await ResolveDraftAsync(callback, reject.DraftId,
                    await reviews.TryRejectAsync(reject.DraftId, callback.UserId, callback.UserName, ct),
                    toast: "❌ Отказано", statusLine: $"❌ Отказано от {editor}", ct);
                break;

            case RequestChanges changes:
                await StartChangeConversationAsync(callback, changes.DraftId, ct);
                break;

            case Ignore ignore:
                // Only a not-allowlisted press in the review chat gets a toast; everything
                // else (wrong chat, unknown data) is silently dropped per docs/05.
                if (ignore.Reason == ReviewUpdateRouter.ReasonNotAllowlisted)
                    await gateway.Value.AnswerCallbackAsync(callback.CallbackId, "Нямате права", ct);
                break;
        }
    }

    private async Task ResolveDraftAsync(
        TgCallback callback, long draftId, bool transitioned, string toast, string statusLine,
        CancellationToken ct)
    {
        if (!transitioned)
        {
            // Double-tap or stale button (docs/05 interaction rules): toast, do nothing.
            await gateway.Value.AnswerCallbackAsync(callback.CallbackId, "Вече обработено", ct);
            return;
        }

        await gateway.Value.AnswerCallbackAsync(callback.CallbackId, toast, ct);
        await EditResolvedAsync(callback.ChatId, callback.MessageId, draftId, statusLine, ct);
        logger.LogInformation("Draft {DraftId}: {Status}", draftId, statusLine);
    }

    private async Task StartChangeConversationAsync(TgCallback callback, long draftId, CancellationToken ct)
    {
        var draft = await reviews.GetDraftHeadlineAsync(draftId, ct);
        if (draft is null)
        {
            await gateway.Value.AnswerCallbackAsync(callback.CallbackId, "Вече обработено", ct);
            return;
        }

        await reviews.SetPendingConversationAsync(callback.ChatId, callback.UserId, draftId, ct);
        await gateway.Value.AnswerCallbackAsync(callback.CallbackId, "✏️ Очаквам инструкции", ct);
        await SendTextAsync(callback.ChatId,
            $"✏️ Опиши промените за „{draft.Value.Headline}“ с отговор на това съобщение.", ct);
    }

    private async Task HandleTextAsync(
        TgText text, TelegramOptions options, IReadOnlySet<long> allowedUsers, CancellationToken ct)
    {
        var pendingDraftId = await reviews.GetPendingConversationAsync(text.ChatId, text.UserId, ct);
        var command = ReviewUpdateRouter.RouteText(text, allowedUsers, options.ReviewChatId, pendingDraftId);
        switch (command)
        {
            case SubmitChangeInstructions submit:
                await SubmitChangeInstructionsAsync(text, submit, ct);
                break;

            case ShowStatus:
                await SendTextAsync(text.ChatId, await reviews.BuildStatusSummaryAsync(ct), ct);
                break;

            case ShowTopics:
                await SendTextAsync(text.ChatId, await reviews.BuildTopicsSummaryAsync(TopicsToShow, ct), ct);
                break;

            case MuteTopic mute:
                var muted = await reviews.MuteTopicAsync(mute.TopicId, mute.Hours, ct);
                await SendTextAsync(text.ChatId, muted
                    ? $"🔇 Тема #{mute.TopicId} е заглушена за {mute.Hours} ч."
                    : $"Няма такава тема (#{mute.TopicId}).", ct);
                break;

            case PauseDrafting:
                await reviews.SetRuntimeFlagAsync(DraftPausedKey, "true", ct);
                await SendTextAsync(text.ChatId,
                    "⏸ Генерирането на чернови е спряно (скрейпването продължава). /resume за пускане.", ct);
                logger.LogWarning("Draft generation paused by {User} via /pause", text.UserName ?? text.UserId.ToString());
                break;

            case ResumeDrafting:
                await reviews.SetRuntimeFlagAsync(DraftPausedKey, "false", ct);
                await SendTextAsync(text.ChatId, "▶️ Генерирането на чернови е възобновено.", ct);
                logger.LogWarning("Draft generation resumed by {User} via /resume", text.UserName ?? text.UserId.ToString());
                break;

            case Ignore:
                break; // plain chatter, unknown commands, foreign chats/users: silently skipped
        }
    }

    private async Task SubmitChangeInstructionsAsync(
        TgText text, SubmitChangeInstructions submit, CancellationToken ct)
    {
        var started = await reviews.TryStartRegenerationAsync(
            submit.DraftId, submit.Instructions, text.UserId, text.UserName, ct);
        // Clear on both outcomes: a dead conversation must not keep swallowing messages.
        await reviews.ClearPendingConversationAsync(text.ChatId, text.UserId, ct);
        if (!started)
        {
            await SendTextAsync(text.ChatId, "Вече обработено.", ct);
            return;
        }

        await SendTextAsync(text.ChatId, "🔄 Правя нова версия…", ct);
        // The superseded version's card loses its buttons and shows why it is closed.
        var view = await reviews.GetReviewViewAsync(submit.DraftId, ct);
        if (view?.TelegramMessageId is { } messageId)
        {
            var html = ReviewMessageRenderer.RenderHtml(view)
                + ReviewMessageRenderer.RenderResolvedSuffix("✏️ Заявени промени — нова версия се изготвя");
            await gateway.Value.EditHtmlAsync(text.ChatId, messageId, html, removeButtons: true, ct);
        }
        logger.LogInformation("Draft {DraftId}: changes requested by {User}",
            submit.DraftId, text.UserName ?? text.UserId.ToString());
    }

    /// <summary>Re-renders the draft's card with a final-status suffix and drops the buttons.</summary>
    private async Task EditResolvedAsync(
        long chatId, long messageId, long draftId, string statusLine, CancellationToken ct)
    {
        var view = await reviews.GetReviewViewAsync(draftId, ct);
        if (view is null)
            return;
        var html = ReviewMessageRenderer.RenderHtml(view) + ReviewMessageRenderer.RenderResolvedSuffix(statusLine);
        await gateway.Value.EditHtmlAsync(chatId, messageId, html, removeButtons: true, ct);
    }

    /// <summary>Plain Bulgarian text (repository summaries, confirmations) sent as escaped HTML.</summary>
    private Task SendTextAsync(long chatId, string text, CancellationToken ct) =>
        gateway.Value.SendHtmlAsync(
            chatId, ReviewMessageRenderer.Escape(text), withReviewButtons: false, draftIdForButtons: null, ct);
}
