using Microsoft.Extensions.Configuration;

using Newsroom.Infrastructure.Publishing;

namespace Newsroom.Infrastructure.Tests.Publishing;

public class FacebookScheduleOptionsTests
{
    private static IConfiguration Config(params KeyValuePair<string, string?>[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Defaults_apply_without_configuration()
    {
        var options = FacebookScheduleOptions.From(Config());

        Assert.Equal(90, options.MinGapMinutes);
        Assert.Equal(5, options.MaxPerDay);
        Assert.Equal(5, options.LeadMinutes);

        var slots = options.ToSlotOptions();
        Assert.Equal(3, slots.Windows.Count);
        Assert.Equal(new TimeSpan(7, 30, 0), slots.Windows[0].Start);
        Assert.Equal(new TimeSpan(21, 30, 0), slots.Windows[2].End);
        Assert.Equal(TimeSpan.FromMinutes(90), slots.MinGap);
    }

    [Fact]
    public void Configured_windows_and_numbers_bind()
    {
        var options = FacebookScheduleOptions.From(Config(
            new("Facebook:Schedule:Windows:0", "10:00-11:00"),
            new("Facebook:Schedule:MinGapMinutes", "45"),
            new("Facebook:Schedule:MaxPerDay", "2")));

        Assert.Equal(45, options.MinGapMinutes);
        Assert.Equal(2, options.MaxPerDay);
        var window = Assert.Single(options.ToSlotOptions().Windows);
        Assert.Equal(new TimeSpan(10, 0, 0), window.Start);
        Assert.Equal(new TimeSpan(11, 0, 0), window.End);
    }

    [Fact]
    public void Malformed_windows_fall_back_to_the_defaults()
    {
        var options = FacebookScheduleOptions.From(Config(
            new("Facebook:Schedule:Windows:0", "banana"),
            new("Facebook:Schedule:Windows:1", "25:00-26:00"),
            new("Facebook:Schedule:Windows:2", "13:00-12:00"))); // start after end

        Assert.Equal(3, options.ToSlotOptions().Windows.Count); // the defaults
        Assert.Equal(new TimeSpan(7, 30, 0), options.ToSlotOptions().Windows[0].Start);
    }
}
