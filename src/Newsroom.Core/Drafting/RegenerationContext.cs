namespace Newsroom.Core.Drafting;

/// <summary>What the drafting AI gets on top of the bundle when the editor requested changes
/// (docs/02-functional-spec.md §5 ✏️): their instructions plus the version being replaced.</summary>
public sealed record RegenerationContext(string Instructions, string? PreviousBody);

/// <summary>A Generating draft row created by ✏️ Промени, waiting for the DraftJob to produce
/// the new version. <paramref name="PreviousBody"/> is the superseded version's body.</summary>
public sealed record PendingRegeneration(
    long DraftId,
    long TopicId,
    string TopicLabel,
    string Instructions,
    string? PreviousBody);
