namespace Newsroom.Core.Review;

/// <summary>
/// Thin seam over the Telegram Bot API (docs/05-integrations/telegram.md, ADR-0006: long
/// polling, no webhook). Implementations hold no review logic — everything decidable lives in
/// <see cref="ReviewUpdateRouter"/>/<see cref="ReviewMessageRenderer"/> so it stays testable.
/// </summary>
public interface ITelegramGateway
{
    /// <summary>Long-polls for updates (messages + callback queries only in Phase 4a).</summary>
    Task<TgUpdateBatch> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct);

    /// <summary>Sends an HTML message (link previews off). With <paramref name="withReviewButtons"/>
    /// the review keyboard (✅/✏️/❌) carrying <paramref name="draftIdForButtons"/> is attached.</summary>
    /// <returns>The Telegram message id.</returns>
    Task<long> SendHtmlAsync(
        long chatId, string html, bool withReviewButtons, long? draftIdForButtons, CancellationToken ct);

    /// <summary>Edits a previously sent message; <paramref name="removeButtons"/> drops the
    /// inline keyboard (resolved drafts must not keep live buttons).</summary>
    Task EditHtmlAsync(long chatId, long messageId, string html, bool removeButtons, CancellationToken ct);

    /// <summary>Short toast answering an inline-button press (must happen within ~10 s).</summary>
    Task AnswerCallbackAsync(string callbackId, string text, CancellationToken ct);
}

/// <summary>
/// Persistence for the review loop (docs/02-functional-spec.md §5). All draft transitions are
/// guarded: only PendingReview drafts accept editor actions, and a failed guard returns false —
/// that is the idempotency contract behind "вече обработено" toasts on double-taps. Every
/// successful transition writes its nw_ReviewAction row in the same transaction.
/// </summary>
public interface IReviewRepository
{
    /// <summary>PendingReview drafts not yet posted to Telegram (TelegramMessageId is null).</summary>
    Task<IReadOnlyList<DraftReviewView>> GetUnsentPendingReviewsAsync(int max, CancellationToken ct);

    /// <summary>The review view for one draft in any status — used to re-render a posted
    /// message when its draft resolves. Null when the draft does not exist.</summary>
    Task<DraftReviewView?> GetReviewViewAsync(long draftId, CancellationToken ct);

    Task SetTelegramMessageIdAsync(long draftId, long messageId, CancellationToken ct);

    /// <summary>Failed editor-requested regenerations not yet reported to the chat
    /// (GenerationFailed + RegenInstructions set + TelegramMessageId null).</summary>
    Task<IReadOnlyList<(long DraftId, string TopicLabel, string Error)>> GetUnreportedRegenFailuresAsync(
        int max, CancellationToken ct);

    /// <summary>PendingReview → Approved. False when the draft is not PendingReview.</summary>
    Task<bool> TryApproveAsync(long draftId, long userId, string? userName, CancellationToken ct);

    /// <summary>PendingReview → Rejected. False when the draft is not PendingReview.</summary>
    Task<bool> TryRejectAsync(long draftId, long userId, string? userName, CancellationToken ct);

    /// <summary>PendingReview → Superseded, plus a new Generating row (Version+1, same topic)
    /// carrying <paramref name="instructions"/> and ParentDraftId, plus the 'ChangesRequested'
    /// nw_ReviewAction — one transaction. False when the draft is not PendingReview.</summary>
    Task<bool> TryStartRegenerationAsync(
        long draftId, string instructions, long userId, string? userName, CancellationToken ct);

    /// <summary>PendingReview drafts created before <paramref name="cutoffUtc"/> → Expired.
    /// MessageId is null for drafts that never reached Telegram (delivery failure).</summary>
    Task<IReadOnlyList<(long DraftId, long? MessageId)>> ExpireStaleAsync(
        DateTime cutoffUtc, CancellationToken ct);

    /// <summary>Null when the draft does not exist.</summary>
    Task<(long DraftId, string Headline)?> GetDraftHeadlineAsync(long draftId, CancellationToken ct);

    /// <summary>The draft id of the open ✏️ conversation for (chat, user), if any.</summary>
    Task<long?> GetPendingConversationAsync(long chatId, long userId, CancellationToken ct);

    /// <summary>Opens (or replaces) the single pending conversation for (chat, user).</summary>
    Task SetPendingConversationAsync(long chatId, long userId, long draftId, CancellationToken ct);

    Task ClearPendingConversationAsync(long chatId, long userId, CancellationToken ct);

    /// <summary>Persisted getUpdates offset (nw_Config 'Telegram:UpdateOffset'; 0 when unset).</summary>
    Task<long> GetUpdateOffsetAsync(CancellationToken ct);

    Task SetUpdateOffsetAsync(long offset, CancellationToken ct);

    /// <summary>Bulgarian /status summary (plain text with newlines — the caller escapes):
    /// today's articles by status, open topics, drafts by status, today's AI usage, last heartbeat.</summary>
    Task<string> BuildStatusSummaryAsync(CancellationToken ct);

    /// <summary>Bulgarian /topics summary: top open topics by score with id, label, score and
    /// article count (plain text — the caller escapes).</summary>
    Task<string> BuildTopicsSummaryAsync(int max, CancellationToken ct);

    /// <summary>Mutes the topic for <paramref name="hours"/>. False when the topic id is unknown.</summary>
    Task<bool> MuteTopicAsync(int topicId, int hours, CancellationToken ct);

    /// <summary>Editor-controlled runtime switches in nw_Config, e.g. 'Draft:Paused'.</summary>
    Task SetRuntimeFlagAsync(string key, string value, CancellationToken ct);

    Task<bool> GetRuntimeFlagAsync(string key, bool defaultValue, CancellationToken ct);
}
