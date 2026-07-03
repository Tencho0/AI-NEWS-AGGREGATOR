namespace Newsroom.Core.Operations;

/// <summary>
/// Canonical job names shared by the heartbeat writers (the jobs) and the watchdog reader —
/// they become nw_Config keys 'Heartbeat:{name}', so both sides must agree on the spelling.
/// </summary>
public static class JobNames
{
    public const string Scrape = "Scrape";
    public const string Analyse = "Analyse";
    public const string Trend = "Trend";
    public const string Draft = "Draft";
    public const string Telegram = "Telegram";
    public const string Publish = "Publish";
}
