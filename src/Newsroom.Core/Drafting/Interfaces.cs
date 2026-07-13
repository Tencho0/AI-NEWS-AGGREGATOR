using Newsroom.Core.Ai;

namespace Newsroom.Core.Drafting;

/// <summary>Draft content plus what the generation request consumed.</summary>
public sealed record DraftGenerationResult(DraftContent Content, AiUsage Usage);

/// <summary>Claims in a draft the sources do not support, plus what the check consumed.</summary>
public sealed record SelfCheckResult(IReadOnlyList<string> UnsupportedClaims, AiUsage Usage);

/// <summary>
/// AI drafting of Bulgarian articles from a topic's sources (docs/02-functional-spec.md §4,
/// ADR-0010). Implementations sit on the provider-neutral Microsoft.Extensions.AI seam.
/// </summary>
public interface IDraftingAi
{
    /// <summary>Writes one article draft — original synthesis, never a copy — from the topic's
    /// source articles, following the embedded editorial style guide. A non-null
    /// <paramref name="regenContext"/> adds the editor's change instructions and the previous
    /// version to the prompt (✏️ Промени regeneration).</summary>
    Task<DraftGenerationResult> GenerateAsync(
        TopicBundle bundle, RegenerationContext? regenContext, CancellationToken ct);

    /// <summary>Hallucination gate: asks the model which claims in the draft body are not
    /// supported by the sources. The flags are shown to the editor, never auto-acted on.</summary>
    Task<SelfCheckResult> SelfCheckAsync(DraftContent draft, TopicBundle bundle, CancellationToken ct);
}

/// <summary>One stock-image source (docs/05-integrations/images.md tier 2 — Pexels, Pixabay).</summary>
public interface IImageProvider
{
    string Name { get; }

    /// <summary>False while the provider has no API key; unconfigured providers are skipped.</summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<ImageCandidate>> SearchAsync(string query, int count, CancellationToken ct);
}

public interface IDraftRepository
{
    /// <summary>Hot topics that should get a draft: not muted (MutedUntilUtc null or past),
    /// generation attempts below the cap, and no draft in any status other than
    /// Rejected/Expired/GenerationFailed/Superseded (those are history; anything else is active).
    /// Also includes topics with ForceDraftAtUtc set, regardless of Hot status or mute.</summary>
    Task<IReadOnlyList<(long TopicId, string Label)>> GetTopicsNeedingDraftAsync(
        int maxAttempts, int maxCount, CancellationToken ct);

    /// <summary>Editor /draft &lt;topicId&gt;: mark a topic for drafting regardless of Hot status.
    /// Resets DraftAttempts so an exhausted topic can be retried. Refuses Done topics and topics
    /// that already have an active draft.</summary>
    Task<ForceDraftResult> RequestForcedDraftAsync(int topicId, CancellationToken ct);

    /// <summary>/new: creates a Manual topic carrying the editor's text and sets ForceDraftAtUtc,
    /// so DraftJob drafts it next cycle (GetTopicBundleAsync synthesizes the bundle from
    /// EditorInput). Returns the new topic id.</summary>
    Task<int> CreateManualTopicAsync(string editorText, CancellationToken ct);

    /// <summary>/post: creates a Manual topic plus a verbatim PendingReview draft (no AI, zero
    /// cost) — the review dispatch loop posts its card. EditorInput keeps the original text so a
    /// later ✏️ regeneration has source material. Returns the new draft id.</summary>
    Task<long> CreateManualArticleAsync(string headline, string body, CancellationToken ct);

    /// <summary>The topic with its newest <paramref name="maxArticles"/> source articles, each
    /// article's text truncated to <paramref name="maxTextCharsPerArticle"/> characters.
    /// Null when the topic does not exist.</summary>
    Task<TopicBundle?> GetTopicBundleAsync(
        long topicId, int maxArticles, int maxTextCharsPerArticle, CancellationToken ct);

    /// <summary>Stores a validated draft as PendingReview with its image suggestions, in one
    /// transaction. Version is the next per-topic version; SourcesJson comes from the bundle;
    /// <paramref name="flaggedClaims"/> is the merged generation + self-check list.</summary>
    /// <returns>The new draft's id.</returns>
    Task<long> SaveDraftAsync(
        TopicBundle bundle,
        DraftContent content,
        AiUsage usage,
        IReadOnlyList<string> flaggedClaims,
        IReadOnlyList<ImageCandidate> images,
        string promptVersion,
        CancellationToken ct);

    /// <summary>Records a failed generation: inserts a GenerationFailed draft row carrying the
    /// error and increments nw_Topic.DraftAttempts (poison protection — topics at the cap stop
    /// being selected by <see cref="GetTopicsNeedingDraftAsync"/>).</summary>
    /// <returns>True when the topic has now reached <paramref name="maxAttempts"/>.</returns>
    Task<bool> RecordGenerationFailureAsync(
        long topicId, string error, int maxAttempts, CancellationToken ct);

    /// <summary>Generating rows created by ✏️ Промени (RegenInstructions set), each joined to
    /// its topic and the superseded version's body — the regeneration work queue.</summary>
    Task<IReadOnlyList<PendingRegeneration>> GetPendingRegenerationsAsync(
        int maxCount, CancellationToken ct);

    /// <summary>Completes a regeneration by updating the SAME Generating row in place to
    /// PendingReview (content, usage, SourcesJson from the bundle, images — one transaction).
    /// TelegramMessageId stays null, so the review surface re-dispatches the new version as a
    /// fresh message.</summary>
    Task CompleteRegenerationAsync(
        long draftId,
        TopicBundle bundle,
        DraftContent content,
        AiUsage usage,
        IReadOnlyList<string> flaggedClaims,
        IReadOnlyList<ImageCandidate> images,
        string promptVersion,
        CancellationToken ct);

    /// <summary>Marks a regeneration row GenerationFailed with the error. Unlike
    /// <see cref="RecordGenerationFailureAsync"/> this does NOT touch nw_Topic.DraftAttempts —
    /// a failed editor-requested rewrite must not poison the topic.</summary>
    Task FailRegenerationAsync(long draftId, string error, CancellationToken ct);
}
