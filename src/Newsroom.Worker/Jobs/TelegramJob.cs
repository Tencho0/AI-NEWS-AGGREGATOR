using System.Globalization;

using Newsroom.Core.Drafting;
using Newsroom.Core.Images;
using Newsroom.Core.Operations;
using Newsroom.Core.Publishing;
using Newsroom.Core.Review;
using Newsroom.Infrastructure.Ai;
using Newsroom.Infrastructure.Images;
using Newsroom.Infrastructure.Publishing;
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
    IDraftRepository drafts,
    Lazy<ITelegramGateway> gateway,
    IJobHeartbeat heartbeat,
    ImageStorage storage,
    IConfiguration configuration,
    ILogger<TelegramJob> logger) : BackgroundService
{
    /// <summary>nw_Config flag flipped by /pause and /resume; DraftJob reads it every cycle.</summary>
    public const string DraftPausedKey = "Draft:Paused";

    private const int TopicsToShow = 10;

    private const string HelpText =
        "🤖 Команди\n" +
        "/status — състояние на конвейера\n" +
        "/topics — отворени теми\n" +
        "/quota — изразходвана AI квота днес\n" +
        "/health — състояние на задачите\n" +
        "/draft <номер> — пусни тема за чернова\n" +
        "/post <заглавие и текст> — публикувай статия точно както е написана\n" +
        "/new <бележки> — AI пише статия от твоя текст\n" +
        "/mute <номер> [часове] — заглуши тема (по подразбиране 24 ч.)\n" +
        "/unmute <номер> — отзаглуши тема\n" +
        "/pause — спри генерирането на чернови\n" +
        "/resume — възобнови генерирането\n" +
        "\n" +
        "Върху картичка: ✅ одобри (сайт, после линк във Facebook) · 🌐 само сайт · 📘 само Facebook · 📅 насрочи за предложения час · " +
        "✏️ промени · 🖼 друга снимка · ❌ откажи. " +
        "Отговор с текст = инструкции за промяна; отговор със снимка = прикачи снимка.\n" +
        "Редакторска статия (/post) без категория: избери от бутоните под картичката — тя не може да се одобри за сайта без категория " +
        "(📘 само Facebook работи и без нея). 🏷 после позволява промяна на категория/регион/тагове по всяко време.";

    /// <summary>Where editor photo uploads land — the editor-upload area of the persistent image
    /// storage root (ADR-0013), not the disposable install directory.</summary>
    private readonly string editorUploadDir = storage.EnsureDirectory(storage.EditorUploadArea);

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
                    await heartbeat.BeatAsync(JobNames.Telegram, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw; // genuine shutdown — end the loop through the handler below
                }
                catch (Exception ex)
                {
                    // Long polling makes an HTTP timeout routine here, and it arrives as
                    // TaskCanceledException — an OperationCanceledException. Catching it as
                    // shutdown would silently retire the review surface (see JobCycle).
                    logger.LogError(ex, "Telegram review cycle failed");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }

    /// <summary>(a) Dispatch: every PendingReview draft not yet posted gets its review card,
    /// then every posted card still missing its photo message gets the top image suggestion.</summary>
    private async Task DispatchPendingAsync(TelegramOptions options, CancellationToken ct)
    {
        await ReportFailedRegenerationsAsync(options, ct);
        var pending = await reviews.GetUnsentPendingReviewsAsync(options.MaxSendPerCycle, ct);
        var scheduleLabel = pending.Count > 0 ? await BuildScheduleLabelAsync(ct) : null;
        foreach (var view in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var html = ReviewMessageRenderer.RenderHtml(view);
                var messageId = view.IsManual
                    ? await gateway.Value.SendManualCardAsync(
                        options.ReviewChatId, html, view.DraftId,
                        string.IsNullOrWhiteSpace(view.Category)
                            ? ManualCardKeyboard.AwaitingCategory : ManualCardKeyboard.Resolved,
                        scheduleLabel, ct)
                    : await gateway.Value.SendHtmlAsync(
                        options.ReviewChatId, html, withReviewButtons: true, view.DraftId, scheduleLabel, ct);
                await reviews.SetTelegramMessageIdAsync(view.DraftId, messageId, ct);
                logger.LogInformation("📨 Draft {DraftId} v{Version} posted for review (message {MessageId})",
                    view.DraftId, view.Version, messageId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Without this, a single failing sendMessage re-serves the same draft first every
                // cycle (ORDER BY DraftId) and wedges card dispatch + the TTL sweep + polling.
                logger.LogWarning(ex,
                    "Draft {DraftId}: review card dispatch failed, will retry next cycle", view.DraftId);
            }
        }

        await DispatchPendingPhotosAsync(options, ct);
    }

    /// <summary>Posts the photo message with the draft's top image suggestion (docs/05
    /// telegram.md: attribution in the caption). Stock suggestions travel by URL (Telegram
    /// fetches them); AI-generated covers are worker-local files and get uploaded. Drafts
    /// without suggestions never show up here and keep the text-only flow; the 🖼 cycle button
    /// only appears when there is something to cycle to.</summary>
    private async Task DispatchPendingPhotosAsync(TelegramOptions options, CancellationToken ct)
    {
        var pendingPhotos = await reviews.GetPendingPhotoDispatchAsync(options.MaxSendPerCycle, ct);
        foreach (var (draftId, url, caption, total, isLocalFile) in pendingPhotos)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var messageId = isLocalFile
                    ? await gateway.Value.SendPhotoFileAsync(
                        options.ReviewChatId, url, caption,
                        draftIdForCycleButton: total >= 2 ? draftId : null, index: null, total, ct)
                    : await gateway.Value.SendPhotoAsync(
                        options.ReviewChatId, url, caption,
                        draftIdForCycleButton: total >= 2 ? draftId : null, index: null, total, ct);
                await reviews.SetTelegramPhotoMessageIdAsync(draftId, messageId, ct);
                logger.LogInformation(
                    "🖼 Draft {DraftId}: image suggestion posted (message {MessageId}, {Total} total)",
                    draftId, messageId, total);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "🖼 Draft {DraftId}: photo dispatch failed, will retry next cycle", draftId);
            }
        }
    }

    /// <summary>Editor-requested regenerations that failed must be reported, not swallowed —
    /// the editor is actively waiting (found live 2026-07-03: quota failure left the editor
    /// staring at "Правя нова версия…" forever).</summary>
    private async Task ReportFailedRegenerationsAsync(TelegramOptions options, CancellationToken ct)
    {
        var failures = await reviews.GetUnreportedRegenFailuresAsync(options.MaxSendPerCycle, ct);
        foreach (var (draftId, topicLabel, error) in failures)
        {
            ct.ThrowIfCancellationRequested();
            var reason = ReviewMessageRenderer.Escape(Truncate(error, 200));
            var label = ReviewMessageRenderer.Escape(topicLabel);
            var messageId = await gateway.Value.SendHtmlAsync(
                options.ReviewChatId,
                $"⚠️ Новата версия за „{label}“ не можа да бъде създадена: {reason}",
                withReviewButtons: false, null, scheduleButtonLabel: null, ct);
            await reviews.SetTelegramMessageIdAsync(draftId, messageId, ct);
            logger.LogInformation("Reported failed regeneration for draft {DraftId}", draftId);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string ForceDraftReply(int topicId, ForceDraftResult result) => result switch
    {
        ForceDraftResult.Queued => $"⚡ Тема #{topicId} е пусната за чернова.",
        ForceDraftResult.AlreadyActive => $"Тема #{topicId} вече има активна чернова.",
        ForceDraftResult.TopicDone => $"Тема #{topicId} е приключена.",
        _ => $"Няма такава тема (#{topicId}).", // TopicNotFound
    };

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
            .Select(c => (c.UpdateId, Update: (object)c))
            .Concat(batch.Texts.Select(t => (t.UpdateId, Update: (object)t)))
            .Concat(batch.Photos.Select(p => (p.UpdateId, Update: (object)p)))
            .OrderBy(u => u.UpdateId);
        foreach (var (updateId, update) in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (update)
                {
                    case TgCallback callback:
                        await HandleCallbackAsync(callback, options, allowedUsers, ct);
                        break;
                    case TgText text:
                        await HandleTextAsync(text, options, allowedUsers, ct);
                        break;
                    case TgPhoto photo:
                        await HandlePhotoAsync(photo, options, allowedUsers, ct);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to process Telegram update {UpdateId}, skipping", updateId);
            }
        }

        if (batch.NextOffset != offset)
            await reviews.SetUpdateOffsetAsync(batch.NextOffset, ct);
        return batch.NextOffset;
    }

    private async Task HandleCallbackAsync(
        TgCallback callback, TelegramOptions options, IReadOnlySet<long> allowedUsers, CancellationToken ct)
    {
        var draftingOptions = GeminiDraftingOptions.From(configuration);
        var command = ReviewUpdateRouter.RouteCallback(
            callback, allowedUsers, options.ReviewChatId, draftingOptions.Categories, draftingOptions.Regions);
        var editor = callback.UserName ?? callback.UserId.ToString();
        switch (command)
        {
            case ApproveDraft approve:
                var approveView = await reviews.GetReviewViewAsync(approve.DraftId, ct);
                // The category gate exists only because Umbraco rejects a publish without one, so
                // it applies to every target EXCEPT Facebook-only, which never calls the site.
                // Still defense in depth: callback_data is not tied to what is currently
                // rendered — a replayed or crafted press must not approve an unpublishable draft.
                if (approve.Target is not PublishTarget.Facebook
                    && approveView is { IsManual: true } && string.IsNullOrWhiteSpace(approveView.Category))
                {
                    await gateway.Value.AnswerCallbackAsync(callback.CallbackId, "Първо избери категория", ct);
                    break;
                }

                // TryApprove: the normal PendingReview → Approved path, now carrying the target.
                // TryUnschedule: ✅ on an already-📅-scheduled draft clears the gate — "now" beats
                // the slot by design, and that draft is already Both. Gated on Both because
                // TryUnscheduleAsync does not (and must not) touch PublishTarget: a scheduled
                // draft is Both by construction, so clearing the schedule needs no target write.
                // But 🌐/📘 pressed on that same scheduled draft would otherwise take this branch
                // too (TryApproveAsync fails — already Approved) and edit the card to claim a
                // narrower target while the row stays Both, publishing the opposite of what the
                // editor was just told. Restricting the fallthrough to Both makes 🌐/📘 on a
                // scheduled draft fall through to the "Вече обработено" toast instead — honest,
                // if unhelpful; narrowing an already-scheduled draft is not supported.
                var transitioned =
                    await reviews.TryApproveAsync(approve.DraftId, approve.Target, callback.UserId, callback.UserName, ct)
                    || (approve.Target is PublishTarget.Both
                        && await reviews.TryUnscheduleAsync(approve.DraftId, callback.UserId, callback.UserName, ct));
                var targetSuffix = TargetSuffix(approve.Target);
                await ResolveDraftAsync(callback, approve.DraftId, transitioned,
                    toast: $"✅ Одобрено{targetSuffix}", statusLine: $"✅ Одобрено{targetSuffix} от {editor}", ct);
                break;

            case RejectDraft reject:
                await ResolveDraftAsync(callback, reject.DraftId,
                    await reviews.TryRejectAsync(reject.DraftId, callback.UserId, callback.UserName, ct),
                    toast: "❌ Отказано", statusLine: $"❌ Отказано от {editor}", ct);
                break;

            case RequestChanges changes:
                await StartChangeConversationAsync(callback, changes.DraftId, ct);
                break;

            case CycleImage cycle:
                await CycleImageAsync(callback, cycle.DraftId, ct);
                break;

            case ScheduleDraft schedule:
                await ScheduleDraftAsync(callback, schedule.DraftId, editor, ct);
                break;

            case SetDraftCategory setCategory:
                if (await reviews.FindEditableDraftByReviewMessageAsync(callback.MessageId, ct) != setCategory.DraftId)
                {
                    await AnswerBestEffortAsync(callback.CallbackId, "Вече обработено", ct);
                    break;
                }
                await drafts.SetDraftCategoryAsync(setCategory.DraftId, setCategory.Category, ct);
                await RerenderManualCardAsync(callback, setCategory.DraftId, expanded: false, ct);
                await gateway.Value.AnswerCallbackAsync(callback.CallbackId, $"📎 {setCategory.Category}", ct);
                logger.LogInformation("Draft {DraftId}: category set to {Category} by {Editor}",
                    setCategory.DraftId, setCategory.Category, editor);
                break;

            case SetDraftRegion setRegion:
                if (await reviews.FindEditableDraftByReviewMessageAsync(callback.MessageId, ct) != setRegion.DraftId)
                {
                    await AnswerBestEffortAsync(callback.CallbackId, "Вече обработено", ct);
                    break;
                }
                await drafts.SetDraftRegionAsync(setRegion.DraftId, setRegion.Region, ct);
                await RerenderManualCardAsync(callback, setRegion.DraftId, expanded: false, ct);
                await gateway.Value.AnswerCallbackAsync(callback.CallbackId, $"📍 {setRegion.Region}", ct);
                logger.LogInformation("Draft {DraftId}: region set to {Region} by {Editor}",
                    setRegion.DraftId, setRegion.Region, editor);
                break;

            case ShowMetaPicker meta:
                if (await reviews.FindEditableDraftByReviewMessageAsync(callback.MessageId, ct) != meta.DraftId)
                {
                    await AnswerBestEffortAsync(callback.CallbackId, "Вече обработено", ct);
                    break;
                }
                await reviews.SetPendingTagsConversationAsync(callback.ChatId, callback.UserId, meta.DraftId, ct);
                await RerenderManualCardAsync(callback, meta.DraftId, expanded: true, ct);
                await gateway.Value.AnswerCallbackAsync(
                    callback.CallbackId, "🏷 Избери по-долу или отговори тук с тагове", ct);
                break;

            case Ignore ignore:
                // Only a not-allowlisted press in the review chat gets a toast; everything
                // else (wrong chat, unknown data) is silently dropped per docs/05.
                if (ignore.Reason == ReviewUpdateRouter.ReasonNotAllowlisted)
                    await gateway.Value.AnswerCallbackAsync(callback.CallbackId, "Нямате права", ct);
                break;
        }
    }

    /// <summary>Names the chosen destination on the resolved card and its toast. Both — what ✅
    /// has always meant — stays unmarked, so the common case reads exactly as it did before
    /// targets existed.</summary>
    private static string TargetSuffix(PublishTarget target) => target switch
    {
        PublishTarget.Website => " (само сайт)",
        PublishTarget.Facebook => " (само ФБ)",
        _ => "",
    };

    private async Task ResolveDraftAsync(
        TgCallback callback, long draftId, bool transitioned, string toast, string statusLine,
        CancellationToken ct)
    {
        if (!transitioned)
        {
            // Double-tap or stale button (docs/05 interaction rules): toast, do nothing.
            await AnswerBestEffortAsync(callback.CallbackId, "Вече обработено", ct);
            return;
        }

        // The toast is cosmetic and fails on stale callback queries ("query is too old"); it must
        // never abort the button-removal edit, which is what the editor actually sees.
        await AnswerBestEffortAsync(callback.CallbackId, toast, ct);
        await EditResolvedAsync(callback.ChatId, callback.MessageId, draftId, statusLine, ct);
        logger.LogInformation("Draft {DraftId}: {Status}", draftId, statusLine);
    }

    /// <summary>answerCallbackQuery only dismisses the button's spinner + shows a toast; it expires
    /// with the callback query (~minutes) and its failure must never block the functional
    /// follow-up (message edit / draft transition already committed). Best-effort by design.</summary>
    private async Task AnswerBestEffortAsync(string callbackId, string text, CancellationToken ct)
    {
        try
        {
            await gateway.Value.AnswerCallbackAsync(callbackId, text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "answerCallbackQuery failed (likely stale query), ignoring");
        }
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

    /// <summary>📅 pressed: recompute the slot (card labels go stale) and approve the draft
    /// gated on it. The guarded transition keeps double-taps and resolved drafts harmless.</summary>
    private async Task ScheduleDraftAsync(
        TgCallback callback, long draftId, string editor, CancellationToken ct)
    {
        DateTime slotLocal;
        try
        {
            slotLocal = await SuggestSlotAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not compute the publish slot for draft {DraftId}; schedule press ignored", draftId);
            await AnswerBestEffortAsync(callback.CallbackId, "Грешка — опитай пак", ct);
            return;
        }

        var scheduled = await reviews.TryScheduleAsync(
            draftId, slotLocal.ToUniversalTime(), callback.UserId, callback.UserName, ct);
        if (!scheduled)
        {
            await AnswerBestEffortAsync(callback.CallbackId, "Вече обработено", ct);
            return;
        }

        var slotText = FormatSlot(slotLocal);
        await AnswerBestEffortAsync(callback.CallbackId, $"📅 Насрочено за {slotText}", ct);
        // Keep a single "approve now" button so ✅ on this card can still override the schedule
        // (TryApproveAsync fails — already Approved — and falls through to TryUnscheduleAsync).
        await EditResolvedAsync(callback.ChatId, callback.MessageId, draftId,
            $"📅 Насрочено за {slotText} от {editor}", ct, approveNowDraftId: draftId);
        logger.LogInformation("Draft {DraftId}: scheduled for {SlotLocal} by {Editor}",
            draftId, slotLocal, editor);
    }

    /// <summary>🖼 pressed on the photo message the button lives on: the repository flips
    /// Selected to the next stock suggestion and the message is edited in place. A null result
    /// (resolved draft or nothing to cycle to) only toasts — stale buttons stay harmless.</summary>
    private async Task CycleImageAsync(TgCallback callback, long draftId, CancellationToken ct)
    {
        var next = await reviews.CycleToNextImageAsync(draftId, ct);
        if (next is not { } selection)
        {
            await gateway.Value.AnswerCallbackAsync(callback.CallbackId, "Няма други снимки", ct);
            return;
        }

        var caption = selection.Caption is null
            ? $"{selection.Index}/{selection.Total}"
            : $"{selection.Caption}\n{selection.Index}/{selection.Total}";
        await gateway.Value.EditPhotoAsync(
            callback.ChatId, callback.MessageId, selection.Url, caption,
            draftIdForCycleButton: draftId, ct);
        await gateway.Value.AnswerCallbackAsync(
            callback.CallbackId, $"Снимка {selection.Index}/{selection.Total}", ct);
        logger.LogInformation("Draft {DraftId}: image cycled to {Index}/{Total}",
            draftId, selection.Index, selection.Total);
    }

    /// <summary>An editor photo upload becomes the draft's chosen image when it replies to a
    /// review card or photo message (docs/05 interaction rules) — the reply's message id
    /// resolves the draft. Photos without draft context are ignored by the router.</summary>
    private async Task HandlePhotoAsync(
        TgPhoto photo, TelegramOptions options, IReadOnlySet<long> allowedUsers, CancellationToken ct)
    {
        var draftIdFromReply = photo.ReplyToMessageId is { } replyTo
            ? await reviews.FindDraftByReviewMessageAsync(replyTo, ct)
            : null;
        var command = ReviewUpdateRouter.RoutePhoto(
            photo, allowedUsers, options.ReviewChatId, draftIdFromReply);
        if (command is AttachEditorPhoto attach)
            await AttachEditorPhotoAsync(photo, attach, ct);
    }

    private async Task AttachEditorPhotoAsync(TgPhoto photo, AttachEditorPhoto attach, CancellationToken ct)
    {
        var localPath = await gateway.Value.DownloadFileToAsync(attach.FileId, editorUploadDir, ct);
        var attached = await reviews.AttachEditorImageAsync(
            attach.DraftId, localPath, attach.FileId, photo.UserId, photo.UserName, ct);
        if (!attached)
        {
            // Resolved while the photo travelled (docs/05 idempotency rules).
            await SendTextAsync(photo.ChatId, "Вече обработено.", ct);
            return;
        }

        var draft = await reviews.GetDraftHeadlineAsync(attach.DraftId, ct);
        await SendTextAsync(photo.ChatId,
            $"📎 Снимката е прикачена и избрана за „{draft?.Headline}“", ct);

        // Show the upload on the draft's photo message (by file_id — no re-upload). The cycle
        // button stays: pressing it moves the selection back to the stock suggestions.
        if (await reviews.GetTelegramPhotoMessageIdAsync(attach.DraftId, ct) is { } photoMessageId)
            await gateway.Value.EditPhotoAsync(
                photo.ChatId, photoMessageId, attach.FileId, "📷 редакторска снимка",
                draftIdForCycleButton: attach.DraftId, ct);
        logger.LogInformation("📎 Draft {DraftId}: editor photo attached by {User} ({Path})",
            attach.DraftId, photo.UserName ?? photo.UserId.ToString(), localPath);
    }

    private async Task HandleTextAsync(
        TgText text, TelegramOptions options, IReadOnlySet<long> allowedUsers, CancellationToken ct)
    {
        // A reply to a specific review card beats the open ✏️ conversation (unambiguous when
        // two drafts await changes); the pending conversation remains the fallback.
        var draftIdFromReply = text.ReplyToMessageId is { } replyTo
            ? await reviews.FindDraftByReviewMessageAsync(replyTo, ct)
            : null;
        var pendingDraftId = await reviews.GetPendingConversationAsync(text.ChatId, text.UserId, ct);
        var pendingTagsDraftId = await reviews.GetPendingTagsConversationAsync(text.ChatId, text.UserId, ct);
        var command = ReviewUpdateRouter.RouteText(
            text, allowedUsers, options.ReviewChatId, pendingDraftId, draftIdFromReply, pendingTagsDraftId);
        switch (command)
        {
            case SubmitChangeInstructions submit:
                await SubmitChangeInstructionsAsync(text, submit, pendingDraftId, ct);
                break;

            case SetDraftTags setTags:
                // Clear unconditionally, like the ✏️ conversation does: a dead conversation must
                // not keep swallowing the editor's next unrelated message.
                await reviews.ClearPendingConversationAsync(text.ChatId, text.UserId, ct);
                var tagsView = await reviews.GetReviewViewAsync(setTags.DraftId, ct);
                if (tagsView?.TelegramMessageId is not { } tagsMessageId
                    || await reviews.FindEditableDraftByReviewMessageAsync(tagsMessageId, ct) != setTags.DraftId)
                {
                    await SendTextAsync(text.ChatId, "Вече обработено.", ct);
                    break;
                }
                await drafts.SetDraftTagsAsync(setTags.DraftId, setTags.Tags, ct);
                await SendTextAsync(text.ChatId, setTags.Tags.Count == 0
                    ? "🏷 Таговете са изчистени."
                    : $"🏷 Тагове: {string.Join(", ", setTags.Tags)}", ct);
                var updatedTagsView = await reviews.GetReviewViewAsync(setTags.DraftId, ct);
                if (updatedTagsView is not null)
                {
                    var tagsKeyboard = string.IsNullOrWhiteSpace(updatedTagsView.Category)
                        ? ManualCardKeyboard.AwaitingCategory
                        : ManualCardKeyboard.Resolved;
                    await gateway.Value.EditManualCardAsync(
                        text.ChatId, tagsMessageId, ReviewMessageRenderer.RenderHtml(updatedTagsView), setTags.DraftId,
                        tagsKeyboard, await BuildScheduleLabelAsync(ct), ct);
                }
                logger.LogInformation("Draft {DraftId}: tags set by {User}",
                    setTags.DraftId, text.UserName ?? text.UserId.ToString());
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

            case ShowHelp:
                await SendTextAsync(text.ChatId, HelpText, ct);
                break;

            case ShowQuota:
                await SendTextAsync(text.ChatId, await reviews.BuildQuotaSummaryAsync(ct), ct);
                break;

            case ShowHealth:
                await SendTextAsync(text.ChatId, await reviews.BuildHealthSummaryAsync(ct), ct);
                break;

            case UnmuteTopic unmute:
                var unmuted = await reviews.UnmuteTopicAsync(unmute.TopicId, ct);
                await SendTextAsync(text.ChatId, unmuted
                    ? $"🔊 Тема #{unmute.TopicId} вече не е заглушена."
                    : $"Няма такава тема (#{unmute.TopicId}).", ct);
                break;

            case ForceDraftTopic force:
                var forceResult = await drafts.RequestForcedDraftAsync(force.TopicId, ct);
                if (forceResult == ForceDraftResult.Queued)
                    logger.LogInformation("Topic {TopicId} force-drafted by {User} via /draft",
                        force.TopicId, text.UserName ?? text.UserId.ToString());
                await SendTextAsync(text.ChatId, ForceDraftReply(force.TopicId, forceResult), ct);
                break;

            case CreateArticle create:
                var manualDraftId = await drafts.CreateManualArticleAsync(create.Headline, create.Body, ct);
                logger.LogInformation("Editor article draft {DraftId} created by {User} via /post",
                    manualDraftId, text.UserName ?? text.UserId.ToString());
                await SendTextAsync(text.ChatId, "📝 Статията е приета — картичката за преглед идва.", ct);
                break;

            case CreateAiArticle createAi:
                var manualTopicId = await drafts.CreateManualTopicAsync(createAi.Text, ct);
                logger.LogInformation("Manual topic {TopicId} queued for AI drafting by {User} via /new",
                    manualTopicId, text.UserName ?? text.UserId.ToString());
                await SendTextAsync(text.ChatId,
                    $"✍️ Статията се пише (тема #{manualTopicId}) — ще я получиш за преглед.", ct);
                break;

            case Ignore:
                break; // plain chatter, unknown commands, foreign chats/users: silently skipped
        }
    }

    private async Task SubmitChangeInstructionsAsync(
        TgText text, SubmitChangeInstructions submit, long? pendingDraftId, CancellationToken ct)
    {
        var started = await reviews.TryStartRegenerationAsync(
            submit.DraftId, submit.Instructions, text.UserId, text.UserName, ct);
        // Clear on both outcomes: a dead conversation must not keep swallowing messages. But a
        // reply bound to a DIFFERENT draft leaves the open ✏️ conversation intact — the editor
        // was promised their next plain message goes to that draft.
        if (pendingDraftId is null || pendingDraftId == submit.DraftId)
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
            await gateway.Value.EditHtmlAsync(text.ChatId, messageId, html, removeButtons: true,
                approveNowDraftIdForButton: null, ct);
        }
        logger.LogInformation("Draft {DraftId}: changes requested by {User}",
            submit.DraftId, text.UserName ?? text.UserId.ToString());
    }

    /// <summary>Re-renders a manual-topic card in place after a category/region/🏷 action.
    /// AwaitingCategory is picked automatically whenever Category is still empty (the very first
    /// category tap on a fresh /post draft); otherwise <paramref name="expanded"/> chooses between
    /// the collapsed Resolved keyboard and the picker-appended Expanded one (🏷 pressed).</summary>
    private async Task RerenderManualCardAsync(TgCallback callback, long draftId, bool expanded, CancellationToken ct)
    {
        var view = await reviews.GetReviewViewAsync(draftId, ct);
        if (view is null)
            return;

        var keyboard = string.IsNullOrWhiteSpace(view.Category)
            ? ManualCardKeyboard.AwaitingCategory
            : expanded ? ManualCardKeyboard.Expanded : ManualCardKeyboard.Resolved;
        await gateway.Value.EditManualCardAsync(
            callback.ChatId, callback.MessageId, ReviewMessageRenderer.RenderHtml(view), draftId, keyboard,
            await BuildScheduleLabelAsync(ct), ct);
    }

    /// <summary>Re-renders the draft's card with a final-status suffix and drops the buttons —
    /// unless <paramref name="approveNowDraftId"/> is set (the 📅 confirmation edit), in which
    /// case a single "✅ Одобри веднага" button stays so the schedule can still be overridden.</summary>
    private async Task EditResolvedAsync(
        long chatId, long messageId, long draftId, string statusLine, CancellationToken ct,
        long? approveNowDraftId = null)
    {
        var view = await reviews.GetReviewViewAsync(draftId, ct);
        if (view is null)
            return;
        var html = ReviewMessageRenderer.RenderHtml(view) + ReviewMessageRenderer.RenderResolvedSuffix(statusLine);
        await gateway.Value.EditHtmlAsync(chatId, messageId, html, removeButtons: true,
            approveNowDraftIdForButton: approveNowDraftId, ct);
    }

    /// <summary>Plain Bulgarian text (repository summaries, confirmations) sent as escaped HTML.</summary>
    private Task SendTextAsync(long chatId, string text, CancellationToken ct) =>
        gateway.Value.SendHtmlAsync(
            chatId, ReviewMessageRenderer.Escape(text), withReviewButtons: false, draftIdForButtons: null,
            scheduleButtonLabel: null, ct);

    /// <summary>Label for the 📅 button ("📅 Насрочи 17:30" / "…утре 08:15"). Advisory — the
    /// slot is recomputed at press time. Best-effort: a failure falls back to a bare label.</summary>
    private async Task<string> BuildScheduleLabelAsync(CancellationToken ct)
    {
        try
        {
            return $"📅 Насрочи {FormatSlot(await SuggestSlotAsync(ct))}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not compute the suggested publish slot for the card button");
            return "📅 Насрочи";
        }
    }

    /// <summary>The suggested publish slot in LOCAL time (Digest:LocalTime convention):
    /// commitments = Facebook posts since yesterday (gap + per-day caps) plus every pending
    /// scheduled draft, fed to the pure suggester.</summary>
    private async Task<DateTime> SuggestSlotAsync(CancellationToken ct)
    {
        var slotOptions = FacebookScheduleOptions.From(configuration).ToSlotOptions();
        var fromUtc = DateTime.UtcNow.Date.AddDays(-1);
        var commitments = (await reviews.GetFacebookCommitmentsUtcAsync(fromUtc, ct))
            .Select(c => c.ToLocalTime())
            .ToList();
        return PublishSlotSuggester.Suggest(DateTime.Now, slotOptions, commitments);
    }

    private static string FormatSlot(DateTime slotLocal) =>
        slotLocal.Date == DateTime.Now.Date
            ? slotLocal.ToString("HH:mm", CultureInfo.InvariantCulture)
            : slotLocal.Date == DateTime.Now.Date.AddDays(1)
                ? "утре " + slotLocal.ToString("HH:mm", CultureInfo.InvariantCulture)
                : slotLocal.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
}
