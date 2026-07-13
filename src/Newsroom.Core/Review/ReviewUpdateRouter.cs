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
    public const string ReasonNoDraftContext = "photo-without-draft-context";

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
            "image" => new CycleImage(draftId),
            _ => new Ignore(ReasonUnknownData),
        };
    }

    /// <summary>Photo uploads (Phase 4b). <paramref name="draftIdFromReply"/> is the draft whose
    /// review card or photo message the upload replied to (the worker resolves the reply's
    /// message id against nw_Draft) — a photo without that context has no target draft.</summary>
    public static ReviewCommand RoutePhoto(
        TgPhoto p, IReadOnlySet<long> allowedUsers, long reviewChatId, long? draftIdFromReply)
    {
        if (p.ChatId != reviewChatId)
            return new Ignore(ReasonWrongChat);
        if (!allowedUsers.Contains(p.UserId))
            return new Ignore(ReasonNotAllowlisted);
        if (draftIdFromReply is not { } draftId)
            return new Ignore(ReasonNoDraftContext);

        return new AttachEditorPhoto(draftId, p.FileId);
    }

    public static ReviewCommand RouteText(
        TgText t, IReadOnlySet<long> allowedUsers, long reviewChatId, long? pendingDraftId,
        long? draftIdFromReply)
    {
        if (t.ChatId != reviewChatId)
            return new Ignore(ReasonWrongChat);
        if (!allowedUsers.Contains(t.UserId))
            return new Ignore(ReasonNotAllowlisted);

        var text = t.Text.Trim();
        if (text.Length == 0)
            return new Ignore(ReasonUnknownText);

        // A reply to a specific review card binds the instructions to that card's draft —
        // unambiguous when several drafts await changes; the open ✏️ conversation is the
        // fallback and swallows the next non-command message as instructions.
        if ((draftIdFromReply ?? pendingDraftId) is { } draftId && !text.StartsWith('/'))
            return new SubmitChangeInstructions(draftId, text);

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return CommandName(parts[0]) switch
        {
            "/status" => new ShowStatus(),
            "/topics" => new ShowTopics(),
            "/help" => new ShowHelp(),
            "/quota" => new ShowQuota(),
            "/health" => new ShowHealth(),
            "/mute" => RouteMute(parts),
            "/unmute" => RouteUnmute(parts),
            "/draft" => RouteForceDraft(parts),
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

    private static ReviewCommand RouteUnmute(string[] parts)
    {
        if (parts.Length != 2)
            return new Ignore(ReasonBadArguments);
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var topicId) || topicId <= 0)
            return new Ignore(ReasonBadArguments);
        return new UnmuteTopic(topicId);
    }

    /// <summary>Only the numeric topic-id form is routed; a URL argument (Phase 4b) is bad args.</summary>
    private static ReviewCommand RouteForceDraft(string[] parts)
    {
        if (parts.Length != 2)
            return new Ignore(ReasonBadArguments);
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var topicId) || topicId <= 0)
            return new Ignore(ReasonBadArguments);
        return new ForceDraftTopic(topicId);
    }
}
