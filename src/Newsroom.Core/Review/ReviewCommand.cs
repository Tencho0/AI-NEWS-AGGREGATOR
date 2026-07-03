namespace Newsroom.Core.Review;

/// <summary>
/// Routing decision for one Telegram update (docs/02-functional-spec.md §5 + editor commands):
/// a closed union produced by <see cref="ReviewUpdateRouter"/> and executed by the worker's
/// TelegramJob. Pure data — all authorization has already happened in the router.
/// </summary>
public abstract record ReviewCommand;

public sealed record ApproveDraft(long DraftId) : ReviewCommand;

public sealed record RejectDraft(long DraftId) : ReviewCommand;

/// <summary>✏️ pressed: open a pending conversation; the editor's next message becomes
/// <see cref="SubmitChangeInstructions"/>.</summary>
public sealed record RequestChanges(long DraftId) : ReviewCommand;

public sealed record SubmitChangeInstructions(long DraftId, string Instructions) : ReviewCommand;

public sealed record ShowStatus : ReviewCommand;

public sealed record ShowTopics : ReviewCommand;

public sealed record MuteTopic(int TopicId, int Hours) : ReviewCommand;

public sealed record PauseDrafting : ReviewCommand;

public sealed record ResumeDrafting : ReviewCommand;

/// <summary>Do nothing. <paramref name="Reason"/> is one of the <see cref="ReviewUpdateRouter"/>
/// reason constants; only <see cref="ReviewUpdateRouter.ReasonNotAllowlisted"/> callbacks get a
/// "no rights" toast, everything else is silent (docs/05: unknown chats/users are ignored).</summary>
public sealed record Ignore(string Reason) : ReviewCommand;
