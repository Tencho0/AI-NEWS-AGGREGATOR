using Newsroom.Core.Publishing;

namespace Newsroom.Core.Review;

/// <summary>
/// Routing decision for one Telegram update (docs/02-functional-spec.md §5 + editor commands):
/// a closed union produced by <see cref="ReviewUpdateRouter"/> and executed by the worker's
/// TelegramJob. Pure data — all authorization has already happened in the router.
/// </summary>
public abstract record ReviewCommand;

/// <summary>✅/🌐/📘 pressed: approve the draft for a specific destination set. A bare
/// "approve:{draftId}" callback (no target token) is <see cref="PublishTarget.Both"/> — the
/// pre-feature meaning, kept so cards already in the chat and the scheduled card's
/// „✅ Одобри веднага" button keep working across a deploy.</summary>
public sealed record ApproveDraft(long DraftId, PublishTarget Target = PublishTarget.Both) : ReviewCommand;

public sealed record RejectDraft(long DraftId) : ReviewCommand;

/// <summary>✏️ pressed: open a pending conversation; the editor's next message becomes
/// <see cref="SubmitChangeInstructions"/>.</summary>
public sealed record RequestChanges(long DraftId) : ReviewCommand;

public sealed record SubmitChangeInstructions(long DraftId, string Instructions) : ReviewCommand;

/// <summary>🖼 pressed on the draft's photo message: select and show the next stock suggestion.</summary>
public sealed record CycleImage(long DraftId) : ReviewCommand;

/// <summary>📅 pressed on a review card: approve the draft gated on the suggested publish slot
/// (recomputed at press time — card labels go stale). ✅ stays the immediate path.</summary>
public sealed record ScheduleDraft(long DraftId) : ReviewCommand;

/// <summary>An editor photo uploaded as a reply to the draft's review card or photo message —
/// it becomes an 'editor-upload' image and wins selection (docs/05-integrations/images.md).</summary>
public sealed record AttachEditorPhoto(long DraftId, string FileId) : ReviewCommand;

public sealed record ShowStatus : ReviewCommand;

public sealed record ShowTopics : ReviewCommand;

public sealed record MuteTopic(int TopicId, int Hours) : ReviewCommand;

public sealed record PauseDrafting : ReviewCommand;

public sealed record ResumeDrafting : ReviewCommand;

public sealed record ShowHelp : ReviewCommand;

public sealed record ShowQuota : ReviewCommand;

public sealed record ShowHealth : ReviewCommand;

/// <summary>/unmute: lift a topic's mute early (reverse of <see cref="MuteTopic"/>).</summary>
public sealed record UnmuteTopic(int TopicId) : ReviewCommand;

/// <summary>/draft &lt;topicId&gt;: force-draft a topic even if it is not Hot (docs/05).</summary>
public sealed record ForceDraftTopic(int TopicId) : ReviewCommand;

/// <summary>/post: verbatim editor article — first line is the headline, the rest the body.
/// Publishes exactly as sent, no AI (docs/05-integrations/telegram.md).</summary>
public sealed record CreateArticle(string Headline, string Body) : ReviewCommand;

/// <summary>/new: the editor's notes become raw material for an AI-written article
/// (Manual topic + ForceDraft pickup — docs/05-integrations/telegram.md).</summary>
public sealed record CreateAiArticle(string Text) : ReviewCommand;

/// <summary>🏷 tap or a category button on an AwaitingCategory card: sets Category on a manual
/// (/post) draft with no AI involved. Category is never null here — it always comes from
/// resolving a button index against the configured taxonomy list (docs/superpowers/specs/
/// 2026-08-05-post-command-metadata-picker-design.md).</summary>
public sealed record SetDraftCategory(long DraftId, string Category) : ReviewCommand;

/// <summary>A region button tap: sets Region on a manual draft, same no-AI path as
/// <see cref="SetDraftCategory"/>.</summary>
public sealed record SetDraftRegion(long DraftId, string Region) : ReviewCommand;

/// <summary>A text reply while the 🏷 tags conversation is open: comma-separated tags, no AI.
/// Equals/GetHashCode are overridden for structural equality on <see cref="Tags"/> — compiler-
/// synthesized record equality compares list-typed properties by reference, which would make two
/// functionally identical commands compare unequal (same gotcha documented on
/// <c>CoverTextPlan.Normalized</c>).</summary>
public sealed record SetDraftTags(long DraftId, IReadOnlyList<string> Tags) : ReviewCommand
{
    public bool Equals(SetDraftTags? other) =>
        other is not null && DraftId == other.DraftId && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(DraftId, Tags.Count);
}

/// <summary>🏷 pressed: re-render the card with category + region picker rows appended
/// (docs/05-integrations/telegram.md).</summary>
public sealed record ShowMetaPicker(long DraftId) : ReviewCommand;

/// <summary>Do nothing. <paramref name="Reason"/> is one of the <see cref="ReviewUpdateRouter"/>
/// reason constants; only <see cref="ReviewUpdateRouter.ReasonNotAllowlisted"/> callbacks get a
/// "no rights" toast, everything else is silent (docs/05: unknown chats/users are ignored).</summary>
public sealed record Ignore(string Reason) : ReviewCommand;
