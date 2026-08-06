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
    /// the review keyboard (✅/✏️/❌) carrying <paramref name="draftIdForButtons"/> is attached;
    /// a non-null <paramref name="scheduleButtonLabel"/> adds a second row with the 📅 button
    /// ("schedule:{draftId}") — the label carries the suggested slot and is advisory only.</summary>
    /// <returns>The Telegram message id.</returns>
    Task<long> SendHtmlAsync(
        long chatId, string html, bool withReviewButtons, long? draftIdForButtons,
        string? scheduleButtonLabel, CancellationToken ct);

    /// <summary>Posts a manual-topic (/post) review card with a metadata-aware keyboard
    /// (<see cref="ManualCardKeyboard"/>) instead of the fixed ✅/✏️/❌ row — used only for drafts
    /// whose topic is Manual. <paramref name="scheduleButtonLabel"/> behaves as in
    /// <see cref="SendHtmlAsync"/> for Resolved/Expanded (ignored for AwaitingCategory, which has
    /// no ✅/📅 row at all).</summary>
    /// <returns>The Telegram message id.</returns>
    Task<long> SendManualCardAsync(
        long chatId, string html, long draftId, ManualCardKeyboard keyboard,
        string? scheduleButtonLabel, CancellationToken ct);

    /// <summary>Edits a previously sent message; <paramref name="removeButtons"/> drops the
    /// inline keyboard (resolved drafts must not keep live buttons). When
    /// <paramref name="approveNowDraftIdForButton"/> is set, the edited message keeps a single
    /// „✅ Одобри веднага" button carrying <c>approve:{draftId}</c> — used by the 📅 flow so a
    /// scheduled draft stays actionable; null = button-less resolved state, the previous
    /// behavior.</summary>
    Task EditHtmlAsync(
        long chatId, long messageId, string html, bool removeButtons,
        long? approveNowDraftIdForButton, CancellationToken ct);

    /// <summary>Edits a manual-topic card in place with a metadata-aware keyboard — the category/
    /// region button taps and the 🏷 correction button all re-render through this rather than
    /// <see cref="EditHtmlAsync"/>.</summary>
    Task EditManualCardAsync(
        long chatId, long messageId, string html, long draftId, ManualCardKeyboard keyboard,
        string? scheduleButtonLabel, CancellationToken ct);

    /// <summary>Short toast answering an inline-button press (must happen within ~10 s).</summary>
    Task AnswerCallbackAsync(string callbackId, string text, CancellationToken ct);

    /// <summary>Sends a photo message — <paramref name="photoUrlOrFileId"/> is a provider URL
    /// (Telegram fetches it) or a Telegram file_id. With <paramref name="draftIdForCycleButton"/>
    /// a single 🖼 "Друга снимка" button carrying "image:{draftId}" is attached; when both
    /// <paramref name="index"/> and <paramref name="total"/> are set (and total &gt; 1),
    /// "{index}/{total}" becomes the caption's last line.</summary>
    /// <returns>The Telegram message id (stored as nw_Draft.TelegramPhotoMessageId).</returns>
    Task<long> SendPhotoAsync(
        long chatId, string photoUrlOrFileId, string? caption, long? draftIdForCycleButton,
        int? index, int? total, CancellationToken ct);

    /// <summary>Replaces a photo message's media + caption in place (editMessageMedia) — the 🖼
    /// cycling and editor-upload flows edit the draft's single photo message rather than posting
    /// new ones. Same optional cycle button as <see cref="SendPhotoAsync"/>.</summary>
    Task EditPhotoAsync(
        long chatId, long messageId, string photoUrlOrFileId, string? caption,
        long? draftIdForCycleButton, CancellationToken ct);

    /// <summary>Sends a photo message from a worker-local file (multipart upload) — used for
    /// AI-generated cover images, which Telegram cannot fetch by URL. Same buttons/caption
    /// behaviour as <see cref="SendPhotoAsync"/>.</summary>
    /// <returns>The Telegram message id (stored as nw_Draft.TelegramPhotoMessageId).</returns>
    Task<long> SendPhotoFileAsync(
        long chatId, string localPath, string? caption, long? draftIdForCycleButton,
        int? index, int? total, CancellationToken ct);

    /// <summary>Downloads a Telegram file (editor photo upload) into
    /// <paramref name="directory"/> (created when missing) under a unique name; the extension
    /// comes from Telegram's file path (default ".jpg").</summary>
    /// <returns>The absolute local path — stored as the 'editor-upload' image's Url.</returns>
    Task<string> DownloadFileToAsync(string fileId, string directory, CancellationToken ct);
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

    /// <summary>PendingReview drafts whose text card is posted (TelegramMessageId set) but whose
    /// photo message is not (TelegramPhotoMessageId null) and that have at least one stock or
    /// AI-generated suggestion. Url/Caption describe the top image (Selected DESC, Ordinal;
    /// caption = attribution + alt text); Total counts the suggestions — the cycle button only
    /// makes sense when it is ≥ 2. IsLocalFile marks an AI-generated image whose Url is a
    /// worker-local path the dispatcher must upload (Telegram cannot fetch it). Drafts without
    /// suggestions never appear (text-only flow).</summary>
    Task<IReadOnlyList<(long DraftId, string Url, string? Caption, int Total, bool IsLocalFile)>> GetPendingPhotoDispatchAsync(
        int max, CancellationToken ct);

    Task SetTelegramPhotoMessageIdAsync(long draftId, long messageId, CancellationToken ct);

    /// <summary>The draft's photo message id, when one was dispatched — null otherwise.</summary>
    Task<long?> GetTelegramPhotoMessageIdAsync(long draftId, CancellationToken ct);

    /// <summary>🖼 pressed: flips Selected to the next stock image by Ordinal (wrapping around;
    /// an editor upload holding the selection cycles back to the first suggestion) and returns
    /// the new selection with its 1-based index among the draft's stock images. Null when the
    /// draft is not PendingReview or has fewer than two stock images — nothing changes.</summary>
    Task<(long DraftId, string Url, string? Caption, int Index, int Total)?> CycleToNextImageAsync(
        long draftId, CancellationToken ct);

    /// <summary>The PendingReview draft whose review card or photo message has this Telegram
    /// message id — how reply-bound uploads and instructions find their draft. Null when the
    /// message belongs to no pending draft.</summary>
    Task<long?> FindDraftByReviewMessageAsync(long messageId, CancellationToken ct);

    /// <summary>As <see cref="FindDraftByReviewMessageAsync"/>, but also matches a
    /// <c>PublishFailed</c> draft — used only by the category/region/tags picker's write guards,
    /// which must be reachable on both the initial /post review and the in-app repair path
    /// (<c>DraftRepository.ReopenIfPublishFailedAsync</c>). Every other caller (photo-attach, ✏️
    /// reply-binding) keeps using the PendingReview-only lookup above.</summary>
    Task<long?> FindEditableDraftByReviewMessageAsync(long messageId, CancellationToken ct);

    /// <summary>Attaches an editor-uploaded photo (docs/05-integrations/images.md tier 4): one
    /// 'editor-upload' nw_DraftImage row (Url = <paramref name="localPath"/>, Ordinal = max+1)
    /// that takes Selected over everything else, plus the 'ImageAttached' nw_ReviewAction —
    /// one transaction. False when the draft is not PendingReview.</summary>
    Task<bool> AttachEditorImageAsync(
        long draftId, string localPath, string fileId, long userId, string? userName,
        CancellationToken ct);

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

    /// <summary>📅: PendingReview → Approved with ScheduledForUtc set — the publish gate holds
    /// the draft until the slot arrives. Writes the 'Scheduled' nw_ReviewAction (comment = the
    /// slot, ISO-8601 UTC) in the same transaction. False when the draft is not PendingReview.</summary>
    Task<bool> TryScheduleAsync(
        long draftId, DateTime scheduledForUtc, long userId, string? userName, CancellationToken ct);

    /// <summary>✅ on an already-scheduled draft: clears ScheduledForUtc so the next publish
    /// cycle picks the draft up immediately. Guarded on Approved + a schedule being present;
    /// false otherwise (not scheduled, already published, unknown). Writes 'ScheduleOverridden'.</summary>
    Task<bool> TryUnscheduleAsync(long draftId, long userId, string? userName, CancellationToken ct);

    /// <summary>Existing Facebook commitments for the slot suggester: UTC times of Succeeded
    /// 'facebook' publish records since <paramref name="fromUtc"/> plus every ScheduledForUtc
    /// (≥ fromUtc) still pending on an Approved draft.</summary>
    Task<IReadOnlyList<DateTime>> GetFacebookCommitmentsUtcAsync(DateTime fromUtc, CancellationToken ct);

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

    /// <summary>The draft id of the open 🏷 tags conversation for (chat, user), if any — a
    /// separate slot from the ✏️ conversation above (opening either replaces the other; see
    /// <see cref="SetPendingTagsConversationAsync"/>).</summary>
    Task<long?> GetPendingTagsConversationAsync(long chatId, long userId, CancellationToken ct);

    /// <summary>Opens (or replaces) the single pending tags conversation for (chat, user).
    /// <see cref="ClearPendingConversationAsync"/> closes this slot too (unconditional on kind).</summary>
    Task SetPendingTagsConversationAsync(long chatId, long userId, long draftId, CancellationToken ct);

    /// <summary>Persisted getUpdates offset (nw_Config 'Telegram:UpdateOffset'; 0 when unset).</summary>
    Task<long> GetUpdateOffsetAsync(CancellationToken ct);

    Task SetUpdateOffsetAsync(long offset, CancellationToken ct);

    /// <summary>Bulgarian /status summary (plain text with newlines — the caller escapes):
    /// today's articles by status, open topics, drafts by status, today's AI usage, last heartbeat.</summary>
    Task<string> BuildStatusSummaryAsync(CancellationToken ct);

    /// <summary>Bulgarian /topics summary: top open topics by score with id, label, score,
    /// article count and when the topic was first seen, in local time (plain text — the caller
    /// escapes).</summary>
    Task<string> BuildTopicsSummaryAsync(int max, CancellationToken ct);

    /// <summary>Mutes the topic for <paramref name="hours"/>. False when the topic id is unknown.</summary>
    Task<bool> MuteTopicAsync(int topicId, int hours, CancellationToken ct);

    /// <summary>Bulgarian /quota summary: AI requests used today vs the per-stage daily cap.</summary>
    Task<string> BuildQuotaSummaryAsync(CancellationToken ct);

    /// <summary>Bulgarian /health summary: last heartbeat per background job with a staleness flag.</summary>
    Task<string> BuildHealthSummaryAsync(CancellationToken ct);

    /// <summary>Lifts a topic's mute early. False when the topic id is unknown.</summary>
    Task<bool> UnmuteTopicAsync(int topicId, CancellationToken ct);

    /// <summary>Editor-controlled runtime switches in nw_Config, e.g. 'Draft:Paused'.</summary>
    Task SetRuntimeFlagAsync(string key, string value, CancellationToken ct);

    Task<bool> GetRuntimeFlagAsync(string key, bool defaultValue, CancellationToken ct);
}
