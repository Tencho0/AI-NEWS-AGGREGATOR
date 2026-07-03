namespace Newsroom.Core.Review;

/// <summary>An inline-button press (Telegram callback query), reduced to what the review loop
/// needs. Package-free projection of the wire type so routing stays unit-testable.</summary>
public sealed record TgCallback(
    long UpdateId,
    string CallbackId,
    long UserId,
    string? UserName,
    long ChatId,
    long MessageId,
    string Data);

/// <summary>A plain text message (commands and change-instruction replies).
/// <paramref name="ReplyToMessageId"/> binds a reply to a specific review card — instructions
/// sent as a reply target that card's draft instead of the open ✏️ conversation.</summary>
public sealed record TgText(
    long UpdateId,
    long UserId,
    string? UserName,
    long ChatId,
    long MessageId,
    string Text,
    long? ReplyToMessageId);

/// <summary>An editor photo upload (Phase 4b). <paramref name="FileId"/> is the largest
/// PhotoSize's file id; <paramref name="ReplyToMessageId"/> ties the upload to a draft's review
/// card or photo message — without it the photo has no draft context and is ignored.</summary>
public sealed record TgPhoto(
    long UpdateId,
    long UserId,
    string? UserName,
    long ChatId,
    long MessageId,
    string FileId,
    long? ReplyToMessageId);

/// <summary>One long-poll result. <paramref name="NextOffset"/> is max update id + 1, or the
/// input offset when the poll returned nothing — persist it so restarts never lose updates.</summary>
public sealed record TgUpdateBatch(
    IReadOnlyList<TgCallback> Callbacks,
    IReadOnlyList<TgText> Texts,
    IReadOnlyList<TgPhoto> Photos,
    long NextOffset);
