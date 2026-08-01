using System.Globalization;

namespace Newsroom.Core.Review;

/// <summary>
/// Formats a past timestamp for the editor's Telegram messages in LOCAL time (the
/// Digest:LocalTime convention — docs/05-integrations/telegram.md). Pure, so the wording is
/// unit-testable without a database: callers convert to local time and pass their own "now".
/// </summary>
public static class EditorTime
{
    /// <summary>
    /// "14:20" for today, "вчера 18:30" for yesterday, "29.07 18:30" for anything older (and for
    /// future timestamps from clock skew). Both arguments are LOCAL time and the comparison is by
    /// calendar day, not elapsed hours, so 23:59 reads as "вчера" one minute later.
    /// </summary>
    public static string Format(DateTime local, DateTime nowLocal) =>
        (nowLocal.Date - local.Date).Days switch
        {
            0 => local.ToString("HH:mm", CultureInfo.InvariantCulture),
            1 => "вчера " + local.ToString("HH:mm", CultureInfo.InvariantCulture),
            _ => local.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture),
        };
}
