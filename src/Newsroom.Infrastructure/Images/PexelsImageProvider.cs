using System.Net.Http.Json;
using System.Text.Json;

using Newsroom.Core.Drafting;

namespace Newsroom.Infrastructure.Images;

/// <summary>
/// Free stock photos from the Pexels API (docs/05-integrations/images.md tier 2, ADR-0009).
/// The Pexels licence asks for a photographer credit, stored in the attribution and shown in
/// the caption. Photos missing a usable image URL are skipped, never thrown on.
/// </summary>
public sealed class PexelsImageProvider(IHttpClientFactory httpClientFactory, ImagesOptions options)
    : IImageProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "Pexels";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.PexelsApiKey);

    public async Task<IReadOnlyList<ImageCandidate>> SearchAsync(string query, int count, CancellationToken ct)
    {
        if (!IsConfigured)
            return [];

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.pexels.com/v1/search?query={Uri.EscapeDataString(query)}&per_page={count}");
        request.Headers.TryAddWithoutValidation("Authorization", options.PexelsApiKey);

        using var client = httpClientFactory.CreateClient(ImageSuggestionService.HttpClientName);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content
            .ReadFromJsonAsync<PexelsResponse>(JsonOptions, ct).ConfigureAwait(false);

        var candidates = new List<ImageCandidate>();
        foreach (var photo in result?.Photos ?? [])
        {
            var imageUrl = FirstNonEmpty(photo.Src?.Large, photo.Src?.Medium);
            if (imageUrl is null)
                continue; // defensive: a photo without a usable image is skipped, not fatal

            candidates.Add(new ImageCandidate(
                imageUrl,
                FirstNonEmpty(photo.Src?.Medium, photo.Src?.Large),
                Name,
                string.IsNullOrWhiteSpace(photo.Photographer) ? Name : $"{Name} / {photo.Photographer}",
                photo.Width ?? 0,
                photo.Height ?? 0));
        }
        return candidates;
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first
        : !string.IsNullOrWhiteSpace(second) ? second
        : null;

    /// <summary>Wire shape of the Pexels search response (fields we use only).</summary>
    private sealed record PexelsResponse(List<PexelsPhoto>? Photos);

    private sealed record PexelsPhoto(PexelsSrc? Src, string? Photographer, int? Width, int? Height);

    private sealed record PexelsSrc(string? Large, string? Medium);
}
