using System.Globalization;
using System.Text;

namespace Newsroom.Core.Review;

/// <summary>
/// Renders the one-message-per-draft-version review card for Telegram (HTML parse mode),
/// following docs/05-integrations/telegram.md "Review message format". Every interpolated value
/// is HTML-escaped — headlines and source names are model/scraper output and must never break
/// the markup. The body is truncated to <see cref="MaxBodyChars"/> on a word boundary; the full
/// text arrives as an attachment in Phase 4b.
/// </summary>
public static class ReviewMessageRenderer
{
    public const int MaxBodyChars = 1500;

    public static string RenderHtml(DraftReviewView v)
    {
        var html = new StringBuilder();

        html.Append("🔥 ").Append(Escape(v.TopicLabel))
            .Append(" (score ").Append(v.TopicScore.ToString("0.0", CultureInfo.InvariantCulture))
            .Append(", ").Append(v.SourceCount)
            .Append(v.SourceCount == 1 ? " източник)" : " източника)").Append('\n');
        html.Append("━━━━━━━━━━━━━━━").Append('\n');

        html.Append("<b>").Append(Escape(v.Headline)).Append("</b>").Append('\n');
        if (!string.IsNullOrWhiteSpace(v.Subtitle))
            html.Append("<i>").Append(Escape(v.Subtitle)).Append("</i>").Append('\n');

        html.Append('\n').Append(Escape(TruncateOnWordBoundary(v.BodyMarkdown, MaxBodyChars))).Append('\n');

        html.Append('\n').Append("📎 Категория: ").Append(Escape(v.Category));
        if (!string.IsNullOrWhiteSpace(v.Region))
            html.Append(" · Регион: ").Append(Escape(v.Region));
        if (v.Tags.Count > 0)
            html.Append(" · Тагове: ").Append(Escape(string.Join(", ", v.Tags)));
        html.Append('\n');

        if (v.Sources.Count > 0)
        {
            html.Append("🔗 Източници:").Append('\n');
            for (var i = 0; i < v.Sources.Count; i++)
            {
                var (name, url) = v.Sources[i];
                html.Append(i + 1).Append(". <a href=\"").Append(EscapeAttribute(url)).Append("\">")
                    .Append(Escape(name)).Append("</a>").Append('\n');
            }
        }

        if (v.FlaggedClaims.Count > 0)
        {
            html.Append("⚠️ За проверка:").Append('\n');
            foreach (var claim in v.FlaggedClaims)
                html.Append("• ").Append(Escape(claim)).Append('\n');
        }

        html.Append("💰 $").Append(v.Cost.ToString("0.####", CultureInfo.InvariantCulture))
            .Append(" · v").Append(v.Version)
            .Append(" · модел ").Append(Escape(v.Model ?? "—"));
        if (v.Confidence is { } confidence)
            html.Append(" · увереност ").Append(Math.Round(confidence * 100)).Append('%');
        if (v.ImageCount > 0)
            html.Append(" · 🖼 ").Append(v.ImageCount);

        return html.ToString();
    }

    /// <summary>Suffix appended when a review message reaches a final state, e.g.
    /// "✅ Одобрено от Иван" — the caller re-renders the card and adds this.</summary>
    public static string RenderResolvedSuffix(string statusLineBg) => $"\n\n{Escape(statusLineBg)}";

    /// <summary>Escapes the three characters Telegram HTML parse mode reserves. Apply to ALL
    /// interpolated content (also used for plain-text summaries sent as HTML).</summary>
    public static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string EscapeAttribute(string text) =>
        Escape(text).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string TruncateOnWordBoundary(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;

        var cut = text[..maxChars];
        var lastBreak = -1;
        for (var i = cut.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(cut[i]))
            {
                lastBreak = i;
                break;
            }
        }
        if (lastBreak > 0)
            cut = cut[..lastBreak];

        return cut.TrimEnd() + " …";
    }
}
