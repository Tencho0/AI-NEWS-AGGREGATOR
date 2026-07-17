namespace Newsroom.Core.Publishing;

/// <summary>
/// A pending Facebook page post shaped for the Graph API (docs/05-integrations/facebook.md):
/// the message becomes "{Headline}\n\n{Teaser}" and <see cref="ArticleUrl"/> — the live Umbraco
/// URL — becomes the link, so Facebook renders the OG card the site already composes. When
/// <see cref="Image"/> is set (Facebook-only mode, no site link) the publisher posts a photo with
/// the message as its caption, so the image and the article land in one post; null falls back to
/// a text-only post.
/// </summary>
public sealed record FacebookPost(
    long DraftId,
    string Headline,
    string Teaser,
    string ArticleUrl,
    FacebookImage? Image = null)
{
    /// <summary>Editor-facing title for notifications/alerts: the headline when present,
    /// otherwise the caption post's hook (first line of the message text, word-truncated) —
    /// caption posts ship Headline = "" by design and must still be identifiable in Telegram.</summary>
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Headline))
                return Headline;

            var span = Teaser.AsSpan();
            var newline = span.IndexOf('\n');
            if (newline >= 0)
                span = span[..newline];
            var line = span.Trim().ToString();

            if (line.Length <= 80)
                return line;

            // Cut on a word boundary within the first 80 chars; a spaceless run falls back to a
            // hard cut rather than LastIndexOf returning -1/0 and producing an empty prefix.
            var lastSpace = line.LastIndexOf(' ', 79);
            return (lastSpace > 0 ? line[..lastSpace].TrimEnd() : line[..79]) + "…";
        }
    }
}

/// <summary>The draft's chosen image for a Facebook photo post. Exactly one of <see cref="Url"/>
/// (a stock/library image Facebook fetches server-side) or <see cref="LocalPath"/> (an editor
/// upload on the worker's disk, sent as multipart bytes) is set. <see cref="FileName"/> names the
/// multipart part for uploads.</summary>
public sealed record FacebookImage(string? Url, string? LocalPath, string FileName);
