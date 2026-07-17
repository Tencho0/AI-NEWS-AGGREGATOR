using Newsroom.Core.Publishing;

namespace Newsroom.Core.Tests.Publishing;

public class PublishSlotSuggesterTests
{
    private static PublishSlotOptions Options(int minGapMinutes = 90, int maxPerDay = 5, int leadMinutes = 5) => new(
        Windows:
        [
            (new TimeSpan(7, 30, 0), new TimeSpan(9, 30, 0)),
            (new TimeSpan(12, 0, 0), new TimeSpan(13, 30, 0)),
            (new TimeSpan(17, 30, 0), new TimeSpan(21, 30, 0)),
        ],
        MinGap: TimeSpan.FromMinutes(minGapMinutes),
        MaxPerDay: maxPerDay,
        Lead: TimeSpan.FromMinutes(leadMinutes));

    private static readonly DateTime Morning = new(2026, 7, 17, 8, 0, 0); // inside 07:30–09:30

    [Fact]
    public void Inside_a_window_with_no_commitments_suggests_now_plus_lead()
    {
        var slot = PublishSlotSuggester.Suggest(Morning, Options(), []);
        Assert.Equal(new DateTime(2026, 7, 17, 8, 5, 0), slot);
    }

    [Fact]
    public void Between_windows_suggests_the_next_window_start()
    {
        var slot = PublishSlotSuggester.Suggest(new DateTime(2026, 7, 17, 10, 0, 0), Options(), []);
        Assert.Equal(new DateTime(2026, 7, 17, 12, 0, 0), slot);
    }

    [Fact]
    public void A_recent_post_pushes_the_slot_by_the_minimum_gap()
    {
        var lastPost = new DateTime(2026, 7, 17, 7, 45, 0);
        var slot = PublishSlotSuggester.Suggest(Morning, Options(), [lastPost]);
        Assert.Equal(new DateTime(2026, 7, 17, 9, 15, 0), slot); // 07:45 + 90 min
    }

    [Fact]
    public void A_gap_conflict_that_overruns_the_window_falls_to_the_next_window()
    {
        var lastPost = new DateTime(2026, 7, 17, 8, 30, 0);
        var slot = PublishSlotSuggester.Suggest(Morning, Options(), [lastPost]);
        Assert.Equal(new DateTime(2026, 7, 17, 12, 0, 0), slot); // 08:30+90 = 10:00 > 09:30 → lunch
    }

    [Fact]
    public void Future_scheduled_posts_also_repel_the_slot()
    {
        var scheduled = new DateTime(2026, 7, 17, 12, 30, 0);
        var slot = PublishSlotSuggester.Suggest(
            new DateTime(2026, 7, 17, 11, 50, 0), Options(), [scheduled]);
        Assert.Equal(new DateTime(2026, 7, 17, 17, 30, 0), slot); // 12:00→14:00 overruns lunch → evening
    }

    [Fact]
    public void A_full_day_rolls_to_the_next_days_first_window()
    {
        var commitments = Enumerable.Range(0, 5)
            .Select(i => new DateTime(2026, 7, 17, 18, 0, 0).AddMinutes(-i))
            .ToList();
        var slot = PublishSlotSuggester.Suggest(Morning, Options(maxPerDay: 5), commitments);
        Assert.Equal(new DateTime(2026, 7, 18, 7, 30, 0), slot);
    }

    [Fact]
    public void After_the_last_window_suggests_tomorrow_morning()
    {
        var slot = PublishSlotSuggester.Suggest(new DateTime(2026, 7, 17, 22, 0, 0), Options(), []);
        Assert.Equal(new DateTime(2026, 7, 18, 7, 30, 0), slot);
    }

    [Fact]
    public void No_windows_falls_back_to_now_plus_lead()
    {
        var slot = PublishSlotSuggester.Suggest(Morning, Options() with { Windows = [] }, []);
        Assert.Equal(Morning.AddMinutes(5), slot);
    }
}
