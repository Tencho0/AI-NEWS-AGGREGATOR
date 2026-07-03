using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Images;

/// <summary>
/// Settings for the stock-image providers (docs/05-integrations/images.md, ADR-0009), bound
/// from configuration: <c>Images:Pixabay:ApiKey</c> / <c>Images:Pexels:ApiKey</c> (empty =
/// provider disabled; real keys live in user-secrets, see docs/06) and
/// <c>Images:MaxSuggestions</c> — how many candidates a draft gets.
/// </summary>
public sealed record ImagesOptions
{
    public string PixabayApiKey { get; init; } = "";
    public string PexelsApiKey { get; init; } = "";
    public int MaxSuggestions { get; init; } = 3;

    public static ImagesOptions From(IConfiguration configuration) => new()
    {
        PixabayApiKey = configuration.GetValue("Images:Pixabay:ApiKey", "")!,
        PexelsApiKey = configuration.GetValue("Images:Pexels:ApiKey", "")!,
        MaxSuggestions = configuration.GetValue("Images:MaxSuggestions", 3),
    };
}
