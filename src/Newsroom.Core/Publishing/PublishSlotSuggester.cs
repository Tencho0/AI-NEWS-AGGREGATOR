namespace Newsroom.Core.Publishing;

/// <summary>Inputs for <see cref="PublishSlotSuggester"/>: engagement windows as local
/// time-of-day ranges, the minimum spacing between page posts, the per-local-day cap and the
/// minimum lead from "now". Parsed from Facebook:Schedule by FacebookScheduleOptions.</summary>
public sealed record PublishSlotOptions(
    IReadOnlyList<(TimeSpan Start, TimeSpan End)> Windows,
    TimeSpan MinGap,
    int MaxPerDay,
    TimeSpan Lead);

/// <summary>
/// Suggests the best next Facebook publish slot
/// (docs/superpowers/specs/2026-07-17-facebook-engagement-design.md): the earliest time
/// ≥ now + Lead that falls inside an engagement window, keeps MinGap from every existing
/// commitment (published or scheduled posts) and lands on a local day with fewer than MaxPerDay
/// commitments. Everything is LOCAL time (the caller converts, matching Digest:LocalTime);
/// heuristic v1 — the windows become data-driven when smart insights land (roadmap). Pure.
/// </summary>
public static class PublishSlotSuggester
{
    private const int MaxDaysAhead = 7;

    public static DateTime Suggest(
        DateTime nowLocal, PublishSlotOptions options, IReadOnlyList<DateTime> commitmentsLocal)
    {
        var earliest = nowLocal + options.Lead;
        var windows = options.Windows.OrderBy(w => w.Start).ToList();

        for (var day = 0; day <= MaxDaysAhead; day++)
        {
            var date = nowLocal.Date.AddDays(day);
            if (commitmentsLocal.Count(c => c.Date == date) >= options.MaxPerDay)
                continue;

            foreach (var (start, end) in windows)
            {
                var candidate = Max(date + start, earliest);
                // Push past every commitment closer than MinGap; forward-only, so this terminates.
                bool moved;
                do
                {
                    moved = false;
                    foreach (var commitment in commitmentsLocal)
                    {
                        if ((candidate - commitment).Duration() < options.MinGap)
                        {
                            candidate = commitment + options.MinGap;
                            moved = true;
                        }
                    }
                }
                while (moved);

                if (candidate <= date + end)
                    return candidate;
            }
        }

        // Pathological config (no windows / a week fully booked): the editor still gets a slot.
        return earliest;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
