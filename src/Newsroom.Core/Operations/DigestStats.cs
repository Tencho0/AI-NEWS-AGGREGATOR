namespace Newsroom.Core.Operations;

/// <summary>
/// One day of pipeline activity for the daily digest (docs/07-operations.md: "Daily digest
/// (09:00): articles scraped, topics, drafts, approvals, publishes, cost"). All "today"
/// figures cover the UTC day starting at <see cref="DayUtc"/>; <see cref="HotTopics"/>,
/// <see cref="SourcesEnabled"/> and <see cref="SourcesDisabled"/> are current snapshots.
/// </summary>
public sealed record DigestStats
{
    public required DateTime DayUtc { get; init; }

    /// <summary>nw_SourceArticle rows first seen today.</summary>
    public int ArticlesScraped { get; init; }

    /// <summary>Today's scraped articles per source, largest first.</summary>
    public IReadOnlyList<(string SourceName, int Count)> ArticlesPerSource { get; init; } = [];

    /// <summary>Of today's scraped articles: how many are already Analysed / Ignored.</summary>
    public int ArticlesAnalysed { get; init; }
    public int ArticlesIgnored { get; init; }

    /// <summary>nw_Topic rows first seen today; Hot count is the current snapshot.</summary>
    public int TopicsCreated { get; init; }
    public int HotTopics { get; init; }

    /// <summary>Drafts created today, by their current status.</summary>
    public IReadOnlyList<(string Status, int Count)> DraftsCreatedByStatus { get; init; } = [];

    /// <summary>Editor actions today (Approved / Rejected / ChangesRequested).</summary>
    public IReadOnlyList<(string Action, int Count)> ReviewActions { get; init; } = [];

    /// <summary>nw_PublishRecord rows written today.</summary>
    public int PublishSucceeded { get; init; }
    public int PublishFailed { get; init; }

    /// <summary>Today's nw_CostLedger totals.</summary>
    public int AiRequests { get; init; }
    public long AiTokensIn { get; init; }
    public long AiTokensOut { get; init; }
    public decimal AiCost { get; init; }

    /// <summary>Current nw_Source snapshot (a disabled source means a dead feed to fix).</summary>
    public int SourcesEnabled { get; init; }
    public int SourcesDisabled { get; init; }
}
