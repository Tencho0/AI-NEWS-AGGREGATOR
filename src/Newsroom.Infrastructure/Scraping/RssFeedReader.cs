using System.Net;
using System.ServiceModel.Syndication;
using System.Xml;
using Microsoft.Extensions.Logging;
using Newsroom.Core.Scraping;

namespace Newsroom.Infrastructure.Scraping;

/// <summary>
/// Fetches and parses RSS 2.0 / Atom feeds. Conditional GET via ETag / If-Modified-Since;
/// encoding (incl. windows-1251, common on Bulgarian sites) is resolved by the XML reader
/// from the byte stream, so the body is never read as a string first.
/// </summary>
public sealed class RssFeedReader(HttpClient httpClient, ILogger<RssFeedReader> logger) : IFeedReader
{
    public async Task<FeedFetchResult> FetchAsync(Source source, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (!string.IsNullOrEmpty(source.Etag))
            request.Headers.TryAddWithoutValidation("If-None-Match", source.Etag);
        if (!string.IsNullOrEmpty(source.LastModifiedHeader))
            request.Headers.TryAddWithoutValidation("If-Modified-Since", source.LastModifiedHeader);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new FeedFetchResult { NotModified = true, Etag = source.Etag, LastModifiedHeader = source.LastModifiedHeader };

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var xmlReader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, // untrusted input: no DTD, no external entities
            XmlResolver = null,
            Async = false,
        });

        var feed = SyndicationFeed.Load(xmlReader);
        var baseUri = feed.BaseUri ?? new Uri(source.Url);

        var items = new List<FeedItem>();
        foreach (var entry in feed.Items)
        {
            var link = ResolveLink(entry, baseUri);
            if (link is null)
            {
                logger.LogDebug("Feed item without usable link skipped in {Source}", source.Name);
                continue;
            }

            items.Add(new FeedItem
            {
                Link = link,
                Title = entry.Title?.Text?.Trim() ?? "(без заглавие)",
                Author = entry.Authors.FirstOrDefault()?.Name,
                PublishedAtUtc = ToUtc(entry.PublishDate) ?? ToUtc(entry.LastUpdatedTime),
                Text = ExtractText(entry),
            });
        }

        return new FeedFetchResult
        {
            Items = items,
            Etag = response.Headers.ETag?.Tag,
            LastModifiedHeader = response.Content.Headers.LastModified?.ToString("R"),
        };
    }

    private static string? ResolveLink(SyndicationItem entry, Uri baseUri)
    {
        var link = entry.Links.FirstOrDefault(l => l.RelationshipType is null or "alternate")?.Uri
                   ?? entry.Links.FirstOrDefault()?.Uri;
        if (link is null)
            return Uri.TryCreate(entry.Id, UriKind.Absolute, out var fromId) ? fromId.AbsoluteUri : null;

        return link.IsAbsoluteUri ? link.AbsoluteUri : new Uri(baseUri, link).AbsoluteUri;
    }

    /// <summary>Prefers content:encoded / atom content over the summary; strips markup to text.</summary>
    private static string? ExtractText(SyndicationItem entry)
    {
        string? html = null;

        foreach (var extension in entry.ElementExtensions)
        {
            if (extension.OuterName == "encoded" &&
                extension.OuterNamespace == "http://purl.org/rss/1.0/modules/content/")
            {
                html = extension.GetObject<string>();
                break;
            }
        }

        html ??= (entry.Content as TextSyndicationContent)?.Text ?? entry.Summary?.Text;
        return html is null ? null : HtmlTextExtractor.HtmlToPlainText(html);
    }

    private static DateTime? ToUtc(DateTimeOffset value) =>
        value == default ? null : value.UtcDateTime;
}
