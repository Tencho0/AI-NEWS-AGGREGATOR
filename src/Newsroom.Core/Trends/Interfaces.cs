using Newsroom.Core.Ai;

namespace Newsroom.Core.Trends;

/// <summary>Assignments plus what the batch request consumed.</summary>
public sealed record ClusterBatchResult(IReadOnlyList<ClusterAssignment> Assignments, AiUsage Usage);

/// <summary>
/// AI-assisted grouping of articles into stories (docs/02-functional-spec.md §3, ADR-0010).
/// Implementations sit on the provider-neutral Microsoft.Extensions.AI seam.
/// </summary>
public interface IClusteringAi
{
    /// <summary>Assigns every candidate either to an existing topic (same concrete story) or to
    /// a proposed new one, in a single request (free-tier quotas are per request). Candidates
    /// missing from the result stay unassigned and are retried next cycle.</summary>
    Task<ClusterBatchResult> AssignAsync(
        IReadOnlyList<ExistingTopicSnapshot> existingTopics,
        IReadOnlyList<ClusterCandidate> candidates,
        CancellationToken ct);
}

public interface ITopicRepository
{
    /// <summary>Deterministic pre-pass, no AI cost: unassigned Analysed articles in the window
    /// whose ContentHash matches an already-assigned article (agency wire copy) join that
    /// article's topic.</summary>
    /// <returns>How many articles were assigned.</returns>
    Task<int> AssignWireCopyDuplicatesAsync(DateTime windowStartUtc, CancellationToken ct);

    /// <summary>All non-Done topics (muted topics still collect articles) with the newest
    /// <paramref name="recentTitlesPerTopic"/> article titles each, for the clustering prompt.</summary>
    Task<IReadOnlyList<ExistingTopicSnapshot>> GetOpenTopicSnapshotsAsync(
        int recentTitlesPerTopic, CancellationToken ct);

    /// <summary>Oldest Analysed articles inside the sliding window (by FirstSeenAtUtc) that are
    /// not in any topic yet, with their analysis summary and entities.</summary>
    Task<IReadOnlyList<ClusterCandidate>> GetUnassignedCandidatesAsync(
        DateTime windowStartUtc, int maxCount, CancellationToken ct);

    /// <summary>Stores assignments atomically. New-topic labels are deduped case-insensitively
    /// within the batch and reuse an existing non-Done topic with the same label; already
    /// assigned articles and nonexistent existing-topic ids are skipped safely.</summary>
    Task ApplyAssignmentsAsync(IReadOnlyList<ClusterAssignment> assignments, CancellationToken ct);

    /// <summary>The non-Done topics themselves — the metadata the scoring transitions need.</summary>
    Task<IReadOnlyList<OpenTopic>> GetOpenTopicsAsync(CancellationToken ct);

    /// <summary>Scoring facts for every non-Done topic, keyed by topic id.</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<TopicArticleFact>>> GetOpenTopicFactsAsync(CancellationToken ct);

    /// <summary>Sets Score, Status and LastScoredAtUtc after a scoring pass.</summary>
    Task UpdateTopicAsync(long topicId, double score, TopicStatus status, DateTime nowUtc, CancellationToken ct);
}
