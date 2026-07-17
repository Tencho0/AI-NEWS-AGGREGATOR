using System.Globalization;

using Microsoft.Extensions.Configuration;

using Newsroom.Core.Publishing;

namespace Newsroom.Infrastructure.Publishing;

/// <summary>
/// Settings for suggested-time Facebook scheduling (Facebook:Schedule,
/// docs/superpowers/specs/2026-07-17-facebook-engagement-design.md): engagement windows as
/// "HH:mm-HH:mm" LOCAL-time ranges (the Digest:LocalTime convention), the minimum gap between
/// page posts, the per-day cap and the minimum lead. Read via <see cref="From"/> by the job that
/// needs it (like TelegramOptions) — no DI registration. Heuristic v1 defaults; data-driven
/// windows arrive with smart insights (docs/10-roadmap.md backlog).
/// </summary>
public sealed record FacebookScheduleOptions
{
    public static readonly string[] DefaultWindows = ["07:30-09:30", "12:00-13:30", "17:30-21:30"];

    public IReadOnlyList<string> Windows { get; init; } = DefaultWindows;
    public int MinGapMinutes { get; init; } = 90;
    public int MaxPerDay { get; init; } = 5;
    public int LeadMinutes { get; init; } = 5;

    public static FacebookScheduleOptions From(IConfiguration configuration) => new()
    {
        Windows = configuration.GetSection("Facebook:Schedule:Windows").Get<string[]>()
            is { Length: > 0 } windows ? windows : DefaultWindows,
        MinGapMinutes = configuration.GetValue("Facebook:Schedule:MinGapMinutes", 90),
        MaxPerDay = configuration.GetValue("Facebook:Schedule:MaxPerDay", 5),
        LeadMinutes = configuration.GetValue("Facebook:Schedule:LeadMinutes", 5),
    };

    /// <summary>Parsed windows for the pure suggester. Malformed entries are skipped and when
    /// none parse the defaults apply — a broken config line must not kill scheduling (mirrors
    /// DailyDigestJob's forgiving Digest:LocalTime parse).</summary>
    public PublishSlotOptions ToSlotOptions()
    {
        var windows = ParseWindows(Windows);
        if (windows.Count == 0)
            windows = ParseWindows(DefaultWindows);
        return new PublishSlotOptions(
            windows,
            TimeSpan.FromMinutes(MinGapMinutes),
            MaxPerDay,
            TimeSpan.FromMinutes(LeadMinutes));
    }

    private static List<(TimeSpan Start, TimeSpan End)> ParseWindows(IReadOnlyList<string> raw)
    {
        var result = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var window in raw)
        {
            var parts = window.Split('-', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && TimeOnly.TryParseExact(
                    parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                && TimeOnly.TryParseExact(
                    parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                && start < end)
            {
                result.Add((start.ToTimeSpan(), end.ToTimeSpan()));
            }
        }
        return result;
    }
}
