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
        TgCallback c, IReadOnlySet<long> allowedUsers, long reviewChatId,
        IReadOnlyList<string> categories, IReadOnlyList<string> regions)
    {
        if (c.ChatId != reviewChatId)
            return new Ignore(ReasonWrongChat);
        if (!allowedUsers.Contains(c.UserId))
            return new Ignore(ReasonNotAllowlisted);

        var segments = c.Data.Split(':');
        if (segments.Length < 2 || segments[0].Length == 0
            || !long.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var draftId))
            return new Ignore(ReasonUnknownData);

        return (segments[0], segments.Length) switch
        {
            ("approve", 2) => new ApproveDraft(draftId),
            ("reject", 2) => new RejectDraft(draftId),
            ("changes", 2) => new RequestChanges(draftId),
            ("image", 2) => new CycleImage(draftId),
            ("schedule", 2) => new ScheduleDraft(draftId),
            ("meta", 2) => new ShowMetaPicker(draftId),
            ("setcat", 3) when TryResolveIndex(segments[2], categories, out var category) =>
                new SetDraftCategory(draftId, category),
            ("setregion", 3) when TryResolveIndex(segments[2], regions, out var region) =>
                new SetDraftRegion(draftId, region),
            _ => new Ignore(ReasonUnknownData),
        };
    }

    /// <summary>Resolves a callback's trailing index segment against a configured taxonomy list —
    /// the only way a category/region string reaches a <see cref="ReviewCommand"/>, so an invalid
    /// value can never be constructed through this path.</summary>
    private static bool TryResolveIndex(string raw, IReadOnlyList<string> options, out string value)
    {
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < options.Count)
        {
            value = options[index];
            return true;
        }
        value = "";
        return false;
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
        long? draftIdFromReply, long? pendingTagsDraftId = null)
    {
        if (t.ChatId != reviewChatId)
            return new Ignore(ReasonWrongChat);
        if (!allowedUsers.Contains(t.UserId))
            return new Ignore(ReasonNotAllowlisted);

        var text = t.Text.Trim();
        if (text.Length == 0)
            return new Ignore(ReasonUnknownText);

        // A pending 🏷 tags conversation takes priority: the editor just tapped a button that
        // explicitly asked for tags text, so the very next plain reply is virtually certainly
        // meant as tags — and the two conversation kinds share one table slot (opening either
        // replaces the other), so both being non-null in practice does not happen.
        if (pendingTagsDraftId is { } tagsDraftId && !text.StartsWith('/'))
            return new SetDraftTags(tagsDraftId, ParseTags(text));

        // A reply to a specific review card binds the instructions to that card's draft —
        // unambiguous when several drafts await changes; the open ✏️ conversation is the
        // fallback and swallows the next non-command message as instructions.
        if ((draftIdFromReply ?? pendingDraftId) is { } draftId && !text.StartsWith('/'))
            return new SubmitChangeInstructions(draftId, text);

        // Whitespace split (not just spaces): a newline right after the command token is how
        // multi-line /post and /new arrive. Behaviour-preserving for the id-based commands.
        var parts = text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
        // Free-text argument for /post и /new: everything after the command token, line breaks
        // preserved (parts would collapse them). text is trimmed, so it starts with parts[0].
        var argument = text[parts[0].Length..].Trim();
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
            "/post" => RoutePost(argument),
            "/new" => argument.Length == 0 ? new Ignore(ReasonBadArguments) : new CreateAiArticle(NormalizeNewlines(argument)),
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

    /// <summary>/post: the first line of the argument is the headline, the remainder the body
    /// (the argument is trimmed, so the first line is never empty).</summary>
    private static ReviewCommand RoutePost(string argument)
    {
        if (argument.Length == 0)
            return new Ignore(ReasonBadArguments);

        var normalized = NormalizeNewlines(argument);
        var newline = normalized.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0
            ? new CreateArticle(normalized, "")
            : new CreateArticle(normalized[..newline].TrimEnd(), normalized[(newline + 1)..].Trim());
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Comma-separated tags: trimmed, empties dropped.</summary>
    private static IReadOnlyList<string> ParseTags(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
