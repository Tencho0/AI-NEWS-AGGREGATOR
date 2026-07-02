using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Newsroom.Core.Scraping;

namespace Newsroom.Infrastructure.Scraping;

/// <summary>
/// Downloads an article page and extracts the main text with a readability-style heuristic:
/// per-source CSS selector (ParserHint "selector:...") → semantic containers → densest
/// paragraph cluster. Downloads are size-capped (docs/06-security.md).
/// </summary>
public sealed class HtmlTextExtractor(HttpClient httpClient, ILogger<HtmlTextExtractor> logger)
    : IArticleTextExtractor
{
    public const int MaxDownloadBytes = 2 * 1024 * 1024;
    private const string SelectorHintPrefix = "selector:";

    private static readonly string[] SemanticSelectors =
    [
        "[itemprop='articleBody']", "article", "main",
    ];

    private static readonly HtmlParser Parser = new();

    public async Task<string?> ExtractAsync(string url, string? parserHint, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                                  && !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Skipping non-HTML content type {MediaType} at {Url}", mediaType, url);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new MemoryStream();
        await CopyBoundedAsync(stream, limited, MaxDownloadBytes, ct);
        limited.Position = 0;

        var document = await Parser.ParseDocumentAsync(limited, ct);
        return ExtractFromDocument(document, parserHint);
    }

    internal static string? ExtractFromDocument(IDocument document, string? parserHint)
    {
        // Boilerplate never counts as article text.
        foreach (var node in document.QuerySelectorAll("script, style, nav, header, footer, aside, form, noscript"))
            node.Remove();

        if (parserHint?.StartsWith(SelectorHintPrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            var custom = document.QuerySelector(parserHint[SelectorHintPrefix.Length..].Trim());
            if (custom is not null)
                return NormalizeBlockText(custom);
        }

        foreach (var selector in SemanticSelectors)
        {
            var candidate = document.QuerySelector(selector);
            var text = candidate is null ? null : NormalizeBlockText(candidate);
            if (text is { Length: >= 200 })
                return text;
        }

        // Fallback: the element containing the largest total <p> text.
        var best = document.QuerySelectorAll("p")
            .GroupBy(p => p.ParentElement)
            .Where(g => g.Key is not null)
            .Select(g => new { Parent = g.Key!, Length = g.Sum(p => p.TextContent.Length) })
            .OrderByDescending(x => x.Length)
            .FirstOrDefault();

        return best is null || best.Length < 200 ? null : NormalizeBlockText(best.Parent);
    }

    /// <summary>Strips markup from an HTML fragment (used for feed summaries/content:encoded).</summary>
    public static string HtmlToPlainText(string html)
    {
        var document = Parser.ParseDocument($"<body>{html}</body>");
        return NormalizeBlockText(document.Body!) ?? string.Empty;
    }

    private static string? NormalizeBlockText(IElement element)
    {
        var blocks = element.QuerySelectorAll("p, h1, h2, h3, li")
            .Select(b => CollapseWhitespace(b.TextContent))
            .Where(t => t.Length > 0)
            .ToList();

        var text = blocks.Count > 0
            ? string.Join("\n\n", blocks)
            : CollapseWhitespace(element.TextContent);

        return text.Length == 0 ? null : text;
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task CopyBoundedAsync(Stream source, Stream target, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        var total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException($"Download exceeded the {maxBytes / 1024 / 1024} MB cap.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
