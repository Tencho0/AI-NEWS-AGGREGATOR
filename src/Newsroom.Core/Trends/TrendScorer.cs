namespace Newsroom.Core.Trends;

/// <summary>
/// Pure trend-score function (docs/02-functional-spec.md §3: score = f(source count, source
/// diversity, velocity, recency, region match)). Deterministic and dependency-free so the
/// formula is unit-testable and tunable purely through <see cref="TrendScorerOptions"/>.
/// </summary>
public static class TrendScorer
{
    /// <summary>
    /// Computes the trend score for one topic:
    /// <code>
    /// raw   = ArticlesWeight  * log2(1 + articleCount)
    ///       + SourcesWeight   * (distinctSourceCount - 1)
    ///       + VelocityWeight  * count(facts newer than now - VelocityWindowHours)
    ///       + RegionWeight    * avg(RegionScore)
    /// score = raw * exp(-ln(2) * ageHours / HalfLifeHours)
    /// </code>
    /// where <c>ageHours</c> is the hours since the NEWEST fact (a story is only as stale as its
    /// latest coverage; negative ages from clock skew clamp to 0). No facts scores 0.
    /// </summary>
    public static double Compute(DateTime nowUtc, IReadOnlyList<TopicArticleFact> facts, TrendScorerOptions options)
    {
        if (facts.Count == 0)
            return 0;

        var distinctSources = facts.Select(f => f.SourceId).Distinct().Count();
        var velocityCutoff = nowUtc.AddHours(-options.VelocityWindowHours);
        var recentCount = facts.Count(f => f.SeenAtUtc >= velocityCutoff);
        var averageRegionScore = facts.Average(f => f.RegionScore);

        var raw = (options.ArticlesWeight * Math.Log2(1 + facts.Count))
            + (options.SourcesWeight * (distinctSources - 1))
            + (options.VelocityWeight * recentCount)
            + (options.RegionWeight * averageRegionScore);

        var ageHours = Math.Max(0, (nowUtc - facts.Max(f => f.SeenAtUtc)).TotalHours);
        return raw * Math.Exp(-Math.Log(2) * ageHours / options.HalfLifeHours);
    }
}
