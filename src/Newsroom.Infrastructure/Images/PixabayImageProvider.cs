using System.Net.Http.Json;
using System.Text.Json;

using Newsroom.Core.Drafting;

namespace Newsroom.Infrastructure.Images;

/// <summary>
/// Free stock photos from the Pixabay API (docs/05-integrations/images.md tier 2, ADR-0009).
/// The Pixabay licence allows editorial use without attribution, but the credit is stored and
/// shown anyway. Hits missing a usable image URL are skipped, never thrown on.
/// </summary>
public sealed class PixabayImageProvider(IHttpClientFactory httpClientFactory, ImagesOptions options)
    : IImageProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "Pixabay";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.PixabayApiKey);

    public async Task<IReadOnlyList<ImageCandidate>> SearchAsync(string query, int count, CancellationToken ct)
    {
        if (!IsConfigured)
            return [];

        var url = "https://pixabay.com/api/" +
            $"?key={Uri.EscapeDataString(options.PixabayApiKey)}" +
            $"&q={Uri.EscapeDataString(query)}" +
            $"&image_type=photo&safesearch=true&per_page={count}";

        using var client = httpClientFactory.CreateClient(ImageSuggestionService.HttpClientName);
        var response = await client.GetFromJsonAsync<PixabayResponse>(url, JsonOptions, ct).ConfigureAwait(false);

        var candidates = new List<ImageCandidate>();
        foreach (var hit in response?.Hits ?? [])
        {
            var imageUrl = FirstNonEmpty(hit.LargeImageUrl, hit.WebformatUrl);
            if (imageUrl is null)
                continue; // defensive: a hit without a usable image is skipped, not fatal

            candidates.Add(new ImageCandidate(
                imageUrl,
                FirstNonEmpty(hit.PreviewUrl, hit.WebformatUrl),
                Name,
                string.IsNullOrWhiteSpace(hit.User) ? Name : $"{Name} / {hit.User}",
                hit.ImageWidth ?? 0,
                hit.ImageHeight ?? 0));
        }
        return candidates;
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first
        : !string.IsNullOrWhiteSpace(second) ? second
        : null;

    /// <summary>Wire shape of the Pixabay search response (fields we use only).</summary>
    private sealed record PixabayResponse(List<PixabayHit>? Hits);

    private sealed record PixabayHit(
        string? LargeImageUrl,
        string? WebformatUrl,
        string? PreviewUrl,
        string? User,
        int? ImageWidth,
        int? ImageHeight);
}
