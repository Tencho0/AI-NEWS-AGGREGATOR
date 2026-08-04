using Newsroom.Core.Review;

namespace Newsroom.Infrastructure.Review;

/// <summary>
/// Marks everything the sandbox sends (ADR-0014). The startup guard can check the database, the
/// site URL and the image root, but it cannot recognise the editors' chat id — no value in
/// configuration says which chat is the live one. So the guarantee is made *visible* instead: a
/// sandbox message is unmistakable at a glance, in the review cards and equally in the watchdog
/// alerts and daily digest that <see cref="Operations.TelegramOperatorAlerts"/> sends through the
/// same seam. Edits are marked too, so a resolved card does not silently lose the marker.
/// </summary>
public sealed class SandboxTelegramGateway(ITelegramGateway inner) : ITelegramGateway
{
    public const string HtmlMarker = "🧪 <b>SANDBOX</b>";
    public const string CaptionMarker = "🧪 SANDBOX";

    public Task<TgUpdateBatch> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct) =>
        inner.GetUpdatesAsync(offset, timeoutSeconds, ct);

    public Task<long> SendHtmlAsync(
        long chatId, string html, bool withReviewButtons, long? draftIdForButtons,
        string? scheduleButtonLabel, CancellationToken ct) =>
        inner.SendHtmlAsync(
            chatId, Mark(html, HtmlMarker)!, withReviewButtons, draftIdForButtons,
            scheduleButtonLabel, ct);

    public Task EditHtmlAsync(
        long chatId, long messageId, string html, bool removeButtons,
        long? approveNowDraftIdForButton, CancellationToken ct) =>
        inner.EditHtmlAsync(
            chatId, messageId, Mark(html, HtmlMarker)!, removeButtons,
            approveNowDraftIdForButton, ct);

    public Task AnswerCallbackAsync(string callbackId, string text, CancellationToken ct) =>
        inner.AnswerCallbackAsync(callbackId, text, ct); // a toast is not a message in the chat

    public Task<long> SendPhotoAsync(
        long chatId, string photoUrlOrFileId, string? caption, long? draftIdForCycleButton,
        int? index, int? total, CancellationToken ct) =>
        inner.SendPhotoAsync(
            chatId, photoUrlOrFileId, Mark(caption, CaptionMarker), draftIdForCycleButton,
            index, total, ct);

    public Task EditPhotoAsync(
        long chatId, long messageId, string photoUrlOrFileId, string? caption,
        long? draftIdForCycleButton, CancellationToken ct) =>
        inner.EditPhotoAsync(
            chatId, messageId, photoUrlOrFileId, Mark(caption, CaptionMarker),
            draftIdForCycleButton, ct);

    public Task<long> SendPhotoFileAsync(
        long chatId, string localPath, string? caption, long? draftIdForCycleButton,
        int? index, int? total, CancellationToken ct) =>
        inner.SendPhotoFileAsync(
            chatId, localPath, Mark(caption, CaptionMarker), draftIdForCycleButton,
            index, total, ct);

    public Task<string> DownloadFileToAsync(string fileId, string directory, CancellationToken ct) =>
        inner.DownloadFileToAsync(fileId, directory, ct);

    /// <summary>Prefixes once. A photo with no caption keeps none — the review card that
    /// accompanies it already carries the marker, and inventing a caption would change layout.</summary>
    private static string? Mark(string? text, string marker) =>
        string.IsNullOrEmpty(text) || text.StartsWith(marker, StringComparison.Ordinal)
            ? text
            : $"{marker}\n\n{text}";
}
