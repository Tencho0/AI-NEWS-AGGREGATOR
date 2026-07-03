using Newsroom.Core.Trends;

namespace Newsroom.Core.Tests.Trends;

public class TrendScorerTests
{
    private static readonly DateTime Now = new(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);

    private static TopicArticleFact Fact(long articleId, int sourceId, double ageHours, double regionScore = 0.5) =>
        new(1, articleId, sourceId, Now.AddHours(-ageHours), regionScore);

    [Fact]
    public void No_facts_scores_zero()
    {
        Assert.Equal(0, TrendScorer.Compute(Now, [], new TrendScorerOptions()));
    }

    [Fact]
    public void Multi_source_recent_cluster_beats_single_source_stale_one()
    {
        var options = new TrendScorerOptions();
        var multiSourceRecent = TrendScorer.Compute(Now,
            [Fact(1, 1, 1, 0.9), Fact(2, 2, 2, 0.9), Fact(3, 3, 3, 0.9)], options);
        var singleSourceStale = TrendScorer.Compute(Now,
            [Fact(4, 1, 30, 0.9), Fact(5, 1, 32, 0.9), Fact(6, 1, 34, 0.9)], options);

        Assert.True(multiSourceRecent > singleSourceStale,
            $"expected {multiSourceRecent} > {singleSourceStale}");
    }

    [Fact]
    public void Score_halves_after_one_half_life()
    {
        // Velocity zeroed: shifting facts by 12h would otherwise also change the velocity
        // count and hide the pure decay effect.
        var options = new TrendScorerOptions { VelocityWeight = 0 };
        var fresh = TrendScorer.Compute(Now, [Fact(1, 1, 0), Fact(2, 2, 1)], options);
        var aged = TrendScorer.Compute(Now,
            [Fact(1, 1, options.HalfLifeHours), Fact(2, 2, options.HalfLifeHours + 1)], options);

        Assert.Equal(fresh / 2, aged, 6);
    }

    [Fact]
    public void Velocity_counts_only_facts_inside_the_velocity_window()
    {
        // Everything but velocity zeroed, decay switched off via a huge half-life, and the
        // newest fact at age 0 — the score is exactly the recent-fact count.
        var options = new TrendScorerOptions
        {
            ArticlesWeight = 0,
            SourcesWeight = 0,
            RegionWeight = 0,
            VelocityWeight = 1,
            HalfLifeHours = double.MaxValue,
        };
        var score = TrendScorer.Compute(Now,
            [Fact(1, 1, 0), Fact(2, 1, 5.9), Fact(3, 1, 6.1), Fact(4, 1, 20)], options);

        Assert.Equal(2, score, 6);
    }

    [Fact]
    public void Region_average_contributes_weighted()
    {
        var options = new TrendScorerOptions
        {
            ArticlesWeight = 0,
            SourcesWeight = 0,
            VelocityWeight = 0,
            RegionWeight = 3,
            HalfLifeHours = double.MaxValue,
        };
        var score = TrendScorer.Compute(Now,
            [Fact(1, 1, 0, regionScore: 0.2), Fact(2, 2, 1, regionScore: 0.8)], options);

        Assert.Equal(3 * 0.5, score, 6); // avg(0.2, 0.8) = 0.5
    }

    [Fact]
    public void Future_facts_clamp_age_to_zero_instead_of_amplifying()
    {
        var options = new TrendScorerOptions { VelocityWeight = 0 };
        var atNow = TrendScorer.Compute(Now, [Fact(1, 1, 0)], options);
        var inFuture = TrendScorer.Compute(Now, [Fact(1, 1, -2)], options); // clock skew

        Assert.Equal(atNow, inFuture, 6);
    }
}
