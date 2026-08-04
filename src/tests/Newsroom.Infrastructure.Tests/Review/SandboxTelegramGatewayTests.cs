using Newsroom.Core.Review;
using Newsroom.Infrastructure.Review;

namespace Newsroom.Infrastructure.Tests.Review;

public class SandboxTelegramGatewayTests
{
    /// <summary>Records the arguments the decorator forwarded, so tests assert on what the real
    /// gateway would have been asked to send.</summary>
    private sealed class RecordingGateway : ITelegramGateway
    {
        public string? Html { get; private set; }
        public string? Caption { get; private set; }
        public long ChatId { get; private set; }
        public long MessageId { get; private set; }
        public bool WithReviewButtons { get; private set; }
        public long? DraftId { get; private set; }
        public string? ScheduleLabel { get; private set; }
        public bool RemoveButtons { get; private set; }
        public long Offset { get; private set; }
        public string? CallbackId { get; private set; }
        public string? CallbackText { get; private set; }
        public string? FileId { get; private set; }
        public string? Directory { get; private set; }
        public string? PhotoReference { get; private set; }
        public int? Index { get; private set; }
        public int? Total { get; private set; }

        public Task<TgUpdateBatch> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
        {
            Offset = offset;
            // TgUpdateBatch(Callbacks, Texts, Photos, NextOffset) — src/Newsroom.Core/Review/TgUpdate.cs
            return Task.FromResult(new TgUpdateBatch([], [], [], offset));
        }

        public Task<long> SendHtmlAsync(long chatId, string html, bool withReviewButtons,
            long? draftIdForButtons, string? scheduleButtonLabel, CancellationToken ct)
        {
            (ChatId, Html, WithReviewButtons, DraftId, ScheduleLabel) =
                (chatId, html, withReviewButtons, draftIdForButtons, scheduleButtonLabel);
            return Task.FromResult(7L);
        }

        public Task EditHtmlAsync(long chatId, long messageId, string html, bool removeButtons,
            long? approveNowDraftIdForButton, CancellationToken ct)
        {
            (ChatId, MessageId, Html, RemoveButtons, DraftId) = (chatId, messageId, html, removeButtons, approveNowDraftIdForButton);
            return Task.CompletedTask;
        }

        public Task AnswerCallbackAsync(string callbackId, string text, CancellationToken ct)
        {
            (CallbackId, CallbackText) = (callbackId, text);
            return Task.CompletedTask;
        }

        public Task<long> SendPhotoAsync(long chatId, string photoUrlOrFileId, string? caption,
            long? draftIdForCycleButton, int? index, int? total, CancellationToken ct)
        {
            (ChatId, PhotoReference, Caption, DraftId, Index, Total) = (chatId, photoUrlOrFileId, caption, draftIdForCycleButton, index, total);
            return Task.FromResult(8L);
        }

        public Task EditPhotoAsync(long chatId, long messageId, string photoUrlOrFileId,
            string? caption, long? draftIdForCycleButton, CancellationToken ct)
        {
            (ChatId, MessageId, PhotoReference, Caption, DraftId) = (chatId, messageId, photoUrlOrFileId, caption, draftIdForCycleButton);
            return Task.CompletedTask;
        }

        public Task<long> SendPhotoFileAsync(long chatId, string localPath, string? caption,
            long? draftIdForCycleButton, int? index, int? total, CancellationToken ct)
        {
            (ChatId, PhotoReference, Caption, DraftId, Index, Total) = (chatId, localPath, caption, draftIdForCycleButton, index, total);
            return Task.FromResult(9L);
        }

        public Task<string> DownloadFileToAsync(string fileId, string directory, CancellationToken ct)
        {
            (FileId, Directory) = (fileId, directory);
            return Task.FromResult(@"C:\tmp\photo.jpg");
        }
    }

    private static (SandboxTelegramGateway Gateway, RecordingGateway Inner) Subject()
    {
        var inner = new RecordingGateway();
        return (new SandboxTelegramGateway(inner), inner);
    }

    [Fact]
    public async Task Sent_html_is_marked_and_every_other_argument_passes_through()
    {
        var (gateway, inner) = Subject();

        var messageId = await gateway.SendHtmlAsync(
            42, "<b>Заглавие</b>", withReviewButtons: true, draftIdForButtons: 5,
            scheduleButtonLabel: "📅 07:30", CancellationToken.None);

        Assert.Equal(7L, messageId);
        Assert.StartsWith(SandboxTelegramGateway.HtmlMarker, inner.Html);
        Assert.Contains("<b>Заглавие</b>", inner.Html);
        Assert.Equal(42, inner.ChatId);
        Assert.True(inner.WithReviewButtons);
        Assert.Equal(5, inner.DraftId);
        Assert.Equal("📅 07:30", inner.ScheduleLabel);
    }

    [Fact]
    public async Task Edited_html_is_marked_too_so_a_resolved_card_keeps_the_marker()
    {
        var (gateway, inner) = Subject();

        await gateway.EditHtmlAsync(42, 7, "✅ Одобрено", removeButtons: true,
            approveNowDraftIdForButton: null, CancellationToken.None);

        Assert.Equal(42, inner.ChatId);
        Assert.Equal(7, inner.MessageId);
        Assert.StartsWith(SandboxTelegramGateway.HtmlMarker, inner.Html);
        Assert.Contains("✅ Одобрено", inner.Html);
        Assert.True(inner.RemoveButtons);
    }

    [Fact]
    public async Task An_already_marked_message_is_not_marked_twice()
    {
        var (gateway, inner) = Subject();
        var once = $"{SandboxTelegramGateway.HtmlMarker}\n\nвече маркирано";

        await gateway.SendHtmlAsync(42, once, false, null, null, CancellationToken.None);

        Assert.Equal(once, inner.Html);
    }

    [Fact]
    public async Task Photo_captions_are_marked_on_send_edit_and_file_upload()
    {
        var (gateway, inner) = Subject();

        await gateway.SendPhotoAsync(42, "https://img/1.jpg", "снимка", 5, 2, 3, CancellationToken.None);
        Assert.Equal(42, inner.ChatId);
        Assert.Equal("https://img/1.jpg", inner.PhotoReference);
        Assert.StartsWith(SandboxTelegramGateway.CaptionMarker, inner.Caption);
        Assert.Contains("снимка", inner.Caption);
        Assert.Equal(2, inner.Index);
        Assert.Equal(3, inner.Total);

        await gateway.EditPhotoAsync(42, 8, "https://img/2.jpg", "друга", 5, CancellationToken.None);
        Assert.Equal(42, inner.ChatId);
        Assert.Equal(8, inner.MessageId);
        Assert.Equal("https://img/2.jpg", inner.PhotoReference);
        Assert.StartsWith(SandboxTelegramGateway.CaptionMarker, inner.Caption);
        Assert.Contains("друга", inner.Caption);

        await gateway.SendPhotoFileAsync(42, @"C:\img\cover.png", "корица", 5, 1, 4, CancellationToken.None);
        Assert.Equal(42, inner.ChatId);
        Assert.Equal(@"C:\img\cover.png", inner.PhotoReference);
        Assert.StartsWith(SandboxTelegramGateway.CaptionMarker, inner.Caption);
        Assert.Contains("корица", inner.Caption);
        Assert.Equal(1, inner.Index);
        Assert.Equal(4, inner.Total);
    }

    [Fact]
    public async Task A_photo_with_no_caption_stays_without_one()
    {
        var (gateway, inner) = Subject();

        await gateway.SendPhotoAsync(42, "https://img/1.jpg", null, null, null, 1, CancellationToken.None);

        Assert.Null(inner.Caption);
    }

    [Fact]
    public async Task An_already_marked_caption_is_not_marked_twice()
    {
        var (gateway, inner) = Subject();
        var once = $"{SandboxTelegramGateway.CaptionMarker}\n\nвече маркирана";

        await gateway.SendPhotoAsync(42, "https://img/1.jpg", once, null, null, 1, CancellationToken.None);

        Assert.Equal(once, inner.Caption);
    }

    [Fact]
    public async Task Non_sending_members_delegate_unchanged()
    {
        var (gateway, inner) = Subject();

        await gateway.GetUpdatesAsync(99, 25, CancellationToken.None);
        Assert.Equal(99, inner.Offset);

        await gateway.AnswerCallbackAsync("cb-1", "вече обработено", CancellationToken.None);
        Assert.Equal("cb-1", inner.CallbackId);
        Assert.Equal("вече обработено", inner.CallbackText);

        var path = await gateway.DownloadFileToAsync("file-1", @"C:\uploads", CancellationToken.None);
        Assert.Equal(@"C:\tmp\photo.jpg", path);
        Assert.Equal("file-1", inner.FileId);
        Assert.Equal(@"C:\uploads", inner.Directory);
    }
}
