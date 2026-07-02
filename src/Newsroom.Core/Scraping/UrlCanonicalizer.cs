namespace Newsroom.Core.Scraping;

/// <summary>
/// Normalises article URLs so the same story maps to the same key regardless of tracking
/// decorations. Conservative on purpose: only removes what is known-safe to remove.
/// </summary>
public static class UrlCanonicalizer
{
    private static readonly string[] TrackingPrefixes = ["utm_"];

    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "yclid", "msclkid", "mc_cid", "mc_eid", "igshid", "wt_mc"
    };

    /// <summary>
    /// Lower-cases scheme and host, strips the fragment, default ports and known tracking
    /// parameters, and sorts the surviving query parameters for byte-stable comparison.
    /// Throws <see cref="UriFormatException"/> for non-absolute or non-http(s) input.
    /// </summary>
    public static string Canonicalize(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new UriFormatException($"Only http/https URLs are supported, got '{uri.Scheme}'.");

        var query = uri.Query.TrimStart('?');
        var keptParams = query.Length == 0
            ? []
            : query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p =>
                {
                    var name = p.Split('=', 2)[0];
                    return !TrackingParams.Contains(name)
                           && !TrackingPrefixes.Any(prefix =>
                               name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                })
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = string.Empty,
            Query = string.Join('&', keptParams),
        };

        if (builder.Port == (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80))
            builder.Port = -1; // omit default port

        return builder.Uri.AbsoluteUri;
    }

    public static bool TryCanonicalize(string url, out string canonical)
    {
        try
        {
            canonical = Canonicalize(url);
            return true;
        }
        catch (UriFormatException)
        {
            canonical = string.Empty;
            return false;
        }
    }
}
