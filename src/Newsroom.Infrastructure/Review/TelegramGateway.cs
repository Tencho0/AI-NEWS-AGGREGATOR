using Newsroom.Core.Review;

using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Newsroom.Infrastructure.Review;

/// <summary>
/// <see cref="ITelegramGateway"/> over Telegram.Bot's <see cref="TelegramBotClient"/> (ADR-0006:
/// long polling via getUpdates — no webhook). Intentionally logic-free and untested: it only
/// translates between the wire types and the Core DTOs; everything decidable lives in
/// <see cref="ReviewUpdateRouter"/>/<see cref="ReviewMessageRenderer"/> plus the TelegramJob.
/// </summary>
public sealed class TelegramGateway(string botToken) : ITelegramGateway
{
    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    private readonly TelegramBotClient bot = new(botToken);

    public async Task<TgUpdateBatch> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        var updates = await bot.GetUpdates(
            offset: (int)offset,
            timeout: timeoutSeconds,
            allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],
            cancellationToken: ct);

        var callbacks = new List<TgCallback>();
        var texts = new List<TgText>();
        var photos = new List<TgPhoto>();
        foreach (var update in updates)
        {
            if (update.CallbackQuery is { Data: { } data, Message: { } cbMessage } callback)
            {
                callbacks.Add(new TgCallback(
                    update.Id,
                    callback.Id,
                    callback.From.Id,
                    callback.From.Username ?? callback.From.FirstName,
                    cbMessage.Chat.Id,
                    cbMessage.MessageId,
                    data));
            }
            else if (update.Message is { Text: { } text, From: { } from } message)
            {
                texts.Add(new TgText(
                    update.Id,
                    from.Id,
                    from.Username ?? from.FirstName,
                    message.Chat.Id,
                    message.MessageId,
                    text,
                    message.ReplyToMessage?.MessageId));
            }
            else if (update.Message is { Photo.Length: > 0, From: { } sender } photoMessage)
            {
                // Telegram sends several PhotoSize variants; the largest is the editor's upload.
                var largest = photoMessage.Photo.MaxBy(p => (long)p.Width * p.Height)!;
                photos.Add(new TgPhoto(
                    update.Id,
                    sender.Id,
                    sender.Username ?? sender.FirstName,
                    photoMessage.Chat.Id,
                    photoMessage.MessageId,
                    largest.FileId,
                    photoMessage.ReplyToMessage?.MessageId));
            }
        }

        var nextOffset = updates.Length == 0 ? offset : updates.Max(u => u.Id) + 1L;
        return new TgUpdateBatch(callbacks, texts, photos, nextOffset);
    }

    public async Task<long> SendHtmlAsync(
        long chatId, string html, bool withReviewButtons, long? draftIdForButtons,
        string? scheduleButtonLabel, CancellationToken ct)
    {
        InlineKeyboardMarkup? keyboard = null;
        if (withReviewButtons && draftIdForButtons is { } draftId)
        {
            List<InlineKeyboardButton[]> rows =
            [
                [
                    InlineKeyboardButton.WithCallbackData("✅ Одобри", $"approve:{draftId}"),
                    InlineKeyboardButton.WithCallbackData("✏️ Промени", $"changes:{draftId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Откажи", $"reject:{draftId}"),
                ],
            ];
            if (scheduleButtonLabel is not null)
                rows.Add([InlineKeyboardButton.WithCallbackData(scheduleButtonLabel, $"schedule:{draftId}")]);
            keyboard = new InlineKeyboardMarkup(rows);
        }

        var message = await bot.SendMessage(
            chatId,
            html,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            linkPreviewOptions: NoPreview,
            cancellationToken: ct);
        return message.MessageId;
    }

    public async Task EditHtmlAsync(
        long chatId, long messageId, string html, bool removeButtons,
        long? approveNowDraftIdForButton, CancellationToken ct)
    {
        // Telegram drops the inline keyboard on editMessageText unless reply_markup is re-sent.
        // A scheduled draft's confirmation edit re-attaches a single "approve now" button (below)
        // so TryUnscheduleAsync stays reachable; every other resolved state stays button-less.
        _ = removeButtons;
        await bot.EditMessageText(
            chatId,
            (int)messageId,
            html,
            parseMode: ParseMode.Html,
            replyMarkup: ApproveNowKeyboard(approveNowDraftIdForButton),
            linkPreviewOptions: NoPreview,
            cancellationToken: ct);
    }

    public Task AnswerCallbackAsync(string callbackId, string text, CancellationToken ct) =>
        bot.AnswerCallbackQuery(callbackId, text, cancellationToken: ct);

    public async Task<long> SendPhotoAsync(
        long chatId, string photoUrlOrFileId, string? caption, long? draftIdForCycleButton,
        int? index, int? total, CancellationToken ct)
    {
        // Captions stay plain text (no parse mode): attribution/alt text need no markup.
        var message = await bot.SendPhoto(
            chatId,
            InputFile.FromString(photoUrlOrFileId),
            caption: WithIndexLine(caption, index, total),
            replyMarkup: CycleKeyboard(draftIdForCycleButton),
            cancellationToken: ct);
        return message.MessageId;
    }

    public async Task EditPhotoAsync(
        long chatId, long messageId, string photoUrlOrFileId, string? caption,
        long? draftIdForCycleButton, CancellationToken ct)
    {
        // editMessageMedia accepts URLs and file_ids alike (editor uploads re-show by file_id).
        var media = new InputMediaPhoto(InputFile.FromString(photoUrlOrFileId))
        {
            Caption = caption,
        };
        await bot.EditMessageMedia(
            chatId,
            (int)messageId,
            media,
            replyMarkup: CycleKeyboard(draftIdForCycleButton),
            cancellationToken: ct);
    }

    public async Task<string> DownloadFileToAsync(string fileId, string directory, CancellationToken ct)
    {
        var file = await bot.GetFile(fileId, ct);
        var extension = Path.GetExtension(file.FilePath ?? "");
        if (string.IsNullOrEmpty(extension))
            extension = ".jpg";

        Directory.CreateDirectory(directory);
        // FileUniqueId is stable per file content, so a re-sent photo overwrites its own copy.
        var path = Path.GetFullPath(Path.Combine(directory, file.FileUniqueId + extension));
        await using (var stream = File.Create(path))
        {
            await bot.DownloadFile(file, stream, ct);
        }
        return path;
    }

    /// <summary>The photo message's single-button keyboard: 🖼 → "image:{draftId}".</summary>
    private static InlineKeyboardMarkup? CycleKeyboard(long? draftId) =>
        draftId is { } id
            ? new InlineKeyboardMarkup([
                [InlineKeyboardButton.WithCallbackData("🖼 Друга снимка", $"image:{id}")],
            ])
            : null;

    /// <summary>The scheduled-card's single-button keyboard: ✅ Одобри веднага → "approve:{draftId}"
    /// — the same callback data the initial review card's ✅ button uses, so TryApproveAsync
    /// (fails: already Approved) falling through to TryUnscheduleAsync is the only wiring the
    /// button needs.</summary>
    private static InlineKeyboardMarkup? ApproveNowKeyboard(long? draftId) =>
        draftId is { } id
            ? new InlineKeyboardMarkup([
                [InlineKeyboardButton.WithCallbackData("✅ Одобри веднага", $"approve:{id}")],
            ])
            : null;

    /// <summary>Appends "{index}/{total}" as the caption's last line — shown instead of a button
    /// label suffix so the keyboard stays stable while cycling.</summary>
    private static string? WithIndexLine(string? caption, int? index, int? total) =>
        index is { } i && total is > 1 and { } t
            ? string.IsNullOrWhiteSpace(caption) ? $"{i}/{t}" : $"{caption}\n{i}/{t}"
            : caption;
}
