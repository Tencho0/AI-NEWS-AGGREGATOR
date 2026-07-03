using System.Globalization;

namespace Newsroom.Core.Review;

/// <summary>
/// Pure routing of Telegram updates to <see cref="ReviewCommand"/>s. The allowlist of editor
/// user ids plus the single review chat id are the whole authorization model
/// (docs/05-integrations/telegram.md): everything else is <see cref="Ignore"/>d.
/// </summary>
public static class ReviewUpdateRouter
{
    public const string ReasonNotAllowlisted = "not-allowlisted";
    public const string ReasonWrongChat = "wrong-chat";
    public const string ReasonUnknownData = "unknown-callback-data";
    public const string ReasonUnknownText = "unknown-text";
    public const string ReasonBadArguments = "bad-arguments";

    private const int DefaultMuteHours = 24;

    public static ReviewCommand RouteCallback(
        TgCallback c, IReadOnlySet<long> allowedUsers, long reviewChatId)
    {
        if (c.ChatId != reviewChatId)
            return new Ignore(ReasonWrongChat);
        if (!allowedUsers.Contains(c.UserId))
            return new Ignore(ReasonNotAllowlisted);

        var separator = c.Data.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0
            || !long.TryParse(c.Data[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var draftId))
            return new Ignore(ReasonUnknownData);

        return c.Data[..separator] switch
        {
            "approve" => new ApproveDraft(draftId),
            "reject" => new RejectDraft(draftId),
            "changes" => new RequestChanges(draftId),
            _ => new Ignore(ReasonUnknownData),
        };
    }

    public static ReviewCommand RouteText(
        TgText t, IReadOnlySet<long> allowedUsers, long reviewChatId, long? pendingDraftId)
    {
        if (t.ChatId != reviewChatId)
            return new Ignore(ReasonWrongChat);
        if (!allowedUsers.Contains(t.UserId))
            return new Ignore(ReasonNotAllowlisted);

        var text = t.Text.Trim();
        if (text.Length == 0)
            return new Ignore(ReasonUnknownText);

        // An open ✏️ conversation swallows the next non-command message as instructions.
        if (pendingDraftId is { } draftId && !text.StartsWith('/'))
            return new SubmitChangeInstructions(draftId, text);

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return CommandName(parts[0]) switch
        {
            "/status" => new ShowStatus(),
            "/topics" => new ShowTopics(),
            "/mute" => RouteMute(parts),
            "/pause" => new PauseDrafting(),
            "/resume" => new ResumeDrafting(),
            _ => new Ignore(ReasonUnknownText),
        };
    }

    /// <summary>In group chats commands arrive as "/status@BotName"; the suffix is irrelevant
    /// here because the chat allowlist already scoped the update to our bot's review chat.</summary>
    private static string CommandName(string token)
    {
        var at = token.IndexOf('@', StringComparison.Ordinal);
        return (at < 0 ? token : token[..at]).ToLowerInvariant();
    }

    private static ReviewCommand RouteMute(string[] parts)
    {
        if (parts.Length is < 2 or > 3)
            return new Ignore(ReasonBadArguments);
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var topicId) || topicId <= 0)
            return new Ignore(ReasonBadArguments);

        var hours = DefaultMuteHours;
        if (parts.Length == 3
            && (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours <= 0))
            return new Ignore(ReasonBadArguments);

        return new MuteTopic(topicId, hours);
    }
}
