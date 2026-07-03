namespace Newsroom.Core.Trends;

/// <summary>
/// Weights and horizons for <see cref="TrendScorer.Compute"/>, bound from the <c>Trend:</c>
/// configuration section (docs/02-functional-spec.md: trend parameters are configuration,
/// not code).
/// </summary>
public sealed record TrendScorerOptions
{
    /// <summary>Weight of article volume (log-dampened so wire-copy floods don't dominate).</summary>
    public double ArticlesWeight { get; init; } = 2.0;

    /// <summary>Weight of source diversity — independent confirmation beats repetition.</summary>
    public double SourcesWeight { get; init; } = 2.0;

    /// <summary>Weight of recent velocity (articles within <see cref="VelocityWindowHours"/>).</summary>
    public double VelocityWeight { get; init; } = 1.0;

    /// <summary>Weight of the average region relevance — local stories are the whole point.</summary>
    public double RegionWeight { get; init; } = 3.0;

    /// <summary>Hours for the score to halve once coverage stops.</summary>
    public double HalfLifeHours { get; init; } = 12.0;

    /// <summary>How far back an article still counts as "velocity".</summary>
    public double VelocityWindowHours { get; init; } = 6.0;
}
