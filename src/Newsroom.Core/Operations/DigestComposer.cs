using System.Globalization;
using System.Text;

using Newsroom.Core.Review;

namespace Newsroom.Core.Operations;

/// <summary>
/// Renders the daily digest (docs/07-operations.md) as a Telegram HTML message in Bulgarian.
/// Pure — DailyDigestJob owns the schedule and the send. Every interpolated string that comes
/// from the database (source names, status/action names) is HTML-escaped via
/// <see cref="ReviewMessageRenderer.Escape"/> so scraped or editor-entered text can never
/// break the markup.
/// </summary>
public static class DigestComposer
{
    public static string Compose(DigestStats stats)
    {
        var text = new StringBuilder();

        text.Append("ℹ️ <b>Дневен отчет — ")
            .Append(stats.DayUtc.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))
            .Append(" (UTC)</b>").Append('\n');

        text.Append("📰 Статии: ").Append(stats.ArticlesScraped);
        if (stats.ArticlesScraped > 0)
            text.Append(" (анализирани ").Append(stats.ArticlesAnalysed)
                .Append(" · игнорирани ").Append(stats.ArticlesIgnored).Append(')');
        text.Append('\n');
        foreach (var (sourceName, count) in stats.ArticlesPerSource)
            text.Append("  • ").Append(ReviewMessageRenderer.Escape(sourceName))
                .Append(": ").Append(count).Append('\n');

        text.Append("🔥 Теми: ").Append(stats.TopicsCreated).Append(" нови · ")
            .Append(stats.HotTopics).Append(" горещи в момента").Append('\n');

        text.Append("📝 Чернови днес: ").Append(stats.DraftsCreatedByStatus.Count == 0
            ? "няма"
            : string.Join(" · ", stats.DraftsCreatedByStatus
                .Select(d => $"{ReviewMessageRenderer.Escape(d.Status)} {d.Count}")));
        text.Append('\n');

        text.Append("👤 Редакторски действия: ").Append(stats.ReviewActions.Count == 0
            ? "няма"
            : string.Join(" · ", stats.ReviewActions
                .Select(a => $"{ReviewMessageRenderer.Escape(a.Action)} {a.Count}")));
        text.Append('\n');

        text.Append("🚀 Публикации: ").Append(stats.PublishSucceeded).Append(" успешни · ")
            .Append(stats.PublishFailed).Append(" неуспешни").Append('\n');

        text.Append("🤖 AI: ").Append(stats.AiRequests).Append(" заявки · ")
            .Append(stats.AiTokensIn).Append('/').Append(stats.AiTokensOut).Append(" токена · $")
            .Append(stats.AiCost.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');

        text.Append("📡 Източници: ").Append(stats.SourcesEnabled).Append(" активни · ")
            .Append(stats.SourcesDisabled).Append(" изключени");

        return text.ToString();
    }
}
