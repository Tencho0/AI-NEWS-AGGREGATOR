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

/// <summary>A plain text message (commands and change-instruction replies).</summary>
public sealed record TgText(
    long UpdateId,
    long UserId,
    string? UserName,
    long ChatId,
    long MessageId,
    string Text);

/// <summary>One long-poll result. <paramref name="NextOffset"/> is max update id + 1, or the
/// input offset when the poll returned nothing — persist it so restarts never lose updates.</summary>
public sealed record TgUpdateBatch(
    IReadOnlyList<TgCallback> Callbacks,
    IReadOnlyList<TgText> Texts,
    long NextOffset);
