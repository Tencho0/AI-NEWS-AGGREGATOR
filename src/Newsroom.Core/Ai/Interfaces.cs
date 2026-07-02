namespace Newsroom.Core.Ai;

/// <summary>Results plus what the batch request consumed.</summary>
public sealed record AiBatchResult(IReadOnlyList<ArticleAnalysisResult> Results, AiUsage Usage);

/// <summary>
/// Domain AI operations (ADR-0010). Implementations sit on the provider-neutral
/// Microsoft.Extensions.AI seam, so switching providers is a config change, not a rewrite.
/// </summary>
public interface IAiClient
{
    /// <summary>Summarises and classifies a batch of articles in one request (free-tier quotas
    /// are per request, so batching = packing). Articles missing from the result stay unanalysed.</summary>
    Task<AiBatchResult> SummariseAndClassifyAsync(IReadOnlyList<ArticleForAnalysis> articles, CancellationToken ct);
}

/// <summary>Daily request budget per pipeline stage, backed by nw_CostLedger (ADR-0010).</summary>
public interface IAiBudget
{
    /// <summary>Whether the stage still has request budget for today (UTC). The actual
    /// reservation is the ledger row written by <see cref="RecordAsync"/> after the call.</summary>
    Task<bool> TryReserveAsync(string stage, CancellationToken ct);

    /// <summary>Writes the ledger row for one completed request.</summary>
    Task RecordAsync(string stage, AiUsage usage, CancellationToken ct);
}

public interface IAnalysisRepository
{
    /// <summary>Oldest analysable articles: Status=New, attempts below the cap, enough text.</summary>
    Task<IReadOnlyList<ArticleForAnalysis>> GetBatchAsync(int maxCount, int maxAttempts, CancellationToken ct);

    /// <summary>Stores analyses and moves each article to Analysed (relevant, Bulgarian)
    /// or Ignored, atomically. Idempotent per article.</summary>
    Task SaveAsync(IReadOnlyList<ArticleAnalysisResult> results, AiUsage usage, CancellationToken ct);

    /// <summary>Bumps AnalysisAttempts; articles reaching the cap go to Ignored (poison protection).</summary>
    Task MarkFailedAttemptAsync(IReadOnlyList<long> articleIds, int maxAttempts, CancellationToken ct);

    Task MarkIgnoredAsync(long articleId, CancellationToken ct);

    /// <summary>Sweep: Ignores New articles whose text is missing or too short to analyse.</summary>
    /// <returns>How many articles were ignored.</returns>
    Task<int> MarkUnanalysableAsync(CancellationToken ct);
}
