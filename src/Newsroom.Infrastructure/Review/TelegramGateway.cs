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
        foreach (var update in updates)
        {
            // Phase 4a handles button presses and plain text only; photos etc. come with 4b.
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
                    text));
            }
        }

        var nextOffset = updates.Length == 0 ? offset : updates.Max(u => u.Id) + 1L;
        return new TgUpdateBatch(callbacks, texts, nextOffset);
    }

    public async Task<long> SendHtmlAsync(
        long chatId, string html, bool withReviewButtons, long? draftIdForButtons, CancellationToken ct)
    {
        InlineKeyboardMarkup? keyboard = withReviewButtons && draftIdForButtons is { } draftId
            ? new InlineKeyboardMarkup([
                [
                    InlineKeyboardButton.WithCallbackData("✅ Одобри", $"approve:{draftId}"),
                    InlineKeyboardButton.WithCallbackData("✏️ Промени", $"changes:{draftId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Откажи", $"reject:{draftId}"),
                ],
            ])
            : null;

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
        long chatId, long messageId, string html, bool removeButtons, CancellationToken ct)
    {
        // Telegram drops the inline keyboard on editMessageText unless reply_markup is re-sent;
        // Phase 4a only ever edits messages into their resolved (button-less) state.
        _ = removeButtons;
        await bot.EditMessageText(
            chatId,
            (int)messageId,
            html,
            parseMode: ParseMode.Html,
            linkPreviewOptions: NoPreview,
            cancellationToken: ct);
    }

    public Task AnswerCallbackAsync(string callbackId, string text, CancellationToken ct) =>
        bot.AnswerCallbackQuery(callbackId, text, cancellationToken: ct);
}
