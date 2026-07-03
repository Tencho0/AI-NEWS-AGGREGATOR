using Microsoft.Extensions.Logging;

using Newsroom.Core.Drafting;

namespace Newsroom.Infrastructure.Images;

/// <summary>
/// Turns a draft's English image-search queries into up to <see cref="ImagesOptions.MaxSuggestions"/>
/// distinct-URL candidates (docs/05-integrations/images.md): configured providers are spread
/// round-robin over the queries, a failing provider is logged and skipped, and an empty result
/// is fine — the draft just goes to review without image suggestions.
/// </summary>
public sealed class ImageSuggestionService(
    IEnumerable<IImageProvider> providers,
    ImagesOptions options,
    ILogger<ImageSuggestionService> logger)
{
    /// <summary>Named HttpClient the image providers share (15s timeout + standard resilience).</summary>
    public const string HttpClientName = "images";

    public async Task<IReadOnlyList<ImageCandidate>> SuggestAsync(
        IReadOnlyList<string> queries, CancellationToken ct)
    {
        var configured = providers.Where(p => p.IsConfigured).ToList();
        if (configured.Count == 0 || queries.Count == 0)
            return [];

        var candidates = new List<ImageCandidate>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Round-robin: each query starts on a different provider, and later rounds retry the
        // same queries on the providers they have not seen yet, until we have enough.
        for (var round = 0; round < configured.Count && candidates.Count < options.MaxSuggestions; round++)
        {
            for (var i = 0; i < queries.Count && candidates.Count < options.MaxSuggestions; i++)
            {
                ct.ThrowIfCancellationRequested();
                var provider = configured[(i + round) % configured.Count];

                IReadOnlyList<ImageCandidate> found;
                try
                {
                    found = await provider.SearchAsync(queries[i], options.MaxSuggestions, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Image search on {Provider} failed for query '{Query}'; skipped",
                        provider.Name, queries[i]);
                    continue;
                }

                foreach (var candidate in found)
                {
                    if (candidates.Count >= options.MaxSuggestions)
                        break;
                    if (seenUrls.Add(candidate.Url))
                        candidates.Add(candidate);
                }
            }
        }
        return candidates;
    }
}
