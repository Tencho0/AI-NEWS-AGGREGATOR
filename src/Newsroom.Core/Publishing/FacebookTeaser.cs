using System.Text;

namespace Newsroom.Core.Publishing;

/// <summary>
/// Composes the 1–2 sentence teaser used for the Facebook link post and the copy-paste
/// group-share text (docs/05-integrations/facebook.md): the draft's SEO description when
/// present, otherwise the first ~200 characters of the body stripped to plain text and cut at
/// a word boundary. Pure — no I/O, no configuration.
/// </summary>
public static class FacebookTeaser
{
    /// <summary>Teaser budget when falling back to the body (the SEO description is already
    /// short by construction and is used as-is).</summary>
    public const int MaxBodyChars = 200;

    public static string Compose(string? seoDescription, string bodyMarkdown)
    {
        if (!string.IsNullOrWhiteSpace(seoDescription))
            return seoDescription.Trim();
        return TruncateOnWordBoundary(StripMarkdown(bodyMarkdown), MaxBodyChars);
    }

    /// <summary>Minimal markdown-to-plain-text, enough for a teaser: [text](url) links keep
    /// their text, emphasis (**, *) and heading (#) markers are dropped, and all whitespace
    /// (including newlines) collapses to single spaces.</summary>
    internal static string StripMarkdown(string markdown)
    {
        var text = new StringBuilder(markdown.Length);
        for (var i = 0; i < markdown.Length; i++)
        {
            var c = markdown[i];
            if (c is '*' or '#')
                continue;
            if (c == '[' && TryStripLink(markdown, i, text, out var resumeAt))
            {
                i = resumeAt;
                continue;
            }
            text.Append(c);
        }
        return string.Join(' ',
            text.ToString().Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>When <paramref name="start"/> opens a [text](url) link, appends just the text
    /// and reports where the link ends; malformed brackets are left untouched.</summary>
    private static bool TryStripLink(string markdown, int start, StringBuilder text, out int resumeAt)
    {
        resumeAt = start;
        var close = markdown.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= markdown.Length || markdown[close + 1] != '(')
            return false;
        var end = markdown.IndexOf(')', close + 2);
        if (end < 0)
            return false;

        text.Append(markdown, start + 1, close - start - 1);
        resumeAt = end;
        return true;
    }

    private static string TruncateOnWordBoundary(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;

        var cut = text[..maxChars];
        var lastBreak = cut.LastIndexOf(' ');
        if (lastBreak > 0)
            cut = cut[..lastBreak];
        return cut.TrimEnd() + "…";
    }
}
