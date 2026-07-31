namespace Newsroom.Core.Operations;

/// <summary>
/// Pure window decision for the daily digest (docs/07-operations.md). Kept free of I/O next to
/// <see cref="WatchdogPolicy"/>; DailyDigestJob owns the schedule and the send.
/// </summary>
public static class DigestPolicy
{
    /// <summary>
    /// The UTC day a digest composed at <paramref name="nowUtc"/> reports on: the last day that
    /// is already over. The send time is VPS-local (default 09:00), so reporting "today" would
    /// cover only the hours since UTC midnight — at UTC+3 that is a "Дневен отчет" built from
    /// six hours of activity, under-reporting the day in its own header. Reporting the previous
    /// day makes the header and the figures agree, and guarantees nothing can still land inside
    /// the window after the message goes out.
    /// </summary>
    public static DateTime DayToReport(DateTime nowUtc) => nowUtc.Date.AddDays(-1);
}
