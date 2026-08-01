using Newsroom.Core.Review;

namespace Newsroom.Core.Tests.Review;

public class EditorTimeTests
{
    private static readonly DateTime NowLocal = new(2026, 8, 1, 10, 30, 0, DateTimeKind.Local);

    [Fact]
    public void Today_is_just_the_clock_time()
    {
        Assert.Equal("09:15", EditorTime.Format(new DateTime(2026, 8, 1, 9, 15, 0), NowLocal));
    }

    [Fact]
    public void Yesterday_is_labelled()
    {
        Assert.Equal("вчера 18:30", EditorTime.Format(new DateTime(2026, 7, 31, 18, 30, 0), NowLocal));
    }

    [Fact]
    public void Older_carries_the_date()
    {
        Assert.Equal("29.07 18:30", EditorTime.Format(new DateTime(2026, 7, 29, 18, 30, 0), NowLocal));
    }

    [Fact]
    public void Midnight_rolls_the_label_over_by_calendar_day_not_elapsed_hours()
    {
        // 23:59 and 00:01 are two minutes apart but fall on different days: the older one must
        // read "вчера" even though it is younger than plenty of same-day timestamps.
        var justAfterMidnight = new DateTime(2026, 8, 1, 0, 1, 0);
        var justBeforeMidnight = new DateTime(2026, 7, 31, 23, 59, 0);

        Assert.Equal("00:01", EditorTime.Format(justAfterMidnight, NowLocal));
        Assert.Equal("вчера 23:59", EditorTime.Format(justBeforeMidnight, NowLocal));
    }

    [Fact]
    public void Future_timestamps_from_clock_skew_fall_back_to_the_dated_form()
    {
        Assert.Equal("02.08 08:00", EditorTime.Format(new DateTime(2026, 8, 2, 8, 0, 0), NowLocal));
    }
}
