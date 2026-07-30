using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Images;

/// <summary>
/// Settings for the Cloudflare Workers AI cover-image generator (ADR-0011), bound from
/// configuration: <c>Images:Cloudflare:AccountId</c> / <c>Images:Cloudflare:ApiToken</c>
/// (either empty = generation disabled, stock providers only; real values live in
/// user-secrets / service environment variables, see docs/06-security.md),
/// <c>Images:Cloudflare:Model</c>, <c>Steps</c> (FLUX.1 Schnell caps at 8),
/// <c>Width</c>/<c>Height</c> (default 1280×720 — the site warns on covers under 1200 px,
/// Google Discover's large-image minimum) and <c>GeneratedImageDir</c> — where generations
/// land (a relative value is resolved against the worker's base directory by the consumer).
/// </summary>
public sealed record CloudflareImagesOptions
{
    public string AccountId { get; init; } = "";
    public string ApiToken { get; init; } = "";
    public string Model { get; init; } = "@cf/black-forest-labs/flux-1-schnell";
    public int Steps { get; init; } = 4;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public string GeneratedImageDir { get; init; } = "generated-images";

    public static CloudflareImagesOptions From(IConfiguration configuration) => new()
    {
        AccountId = configuration.GetValue("Images:Cloudflare:AccountId", "")!,
        ApiToken = configuration.GetValue("Images:Cloudflare:ApiToken", "")!,
        Model = configuration.GetValue("Images:Cloudflare:Model", "@cf/black-forest-labs/flux-1-schnell")!,
        Steps = configuration.GetValue("Images:Cloudflare:Steps", 4),
        Width = configuration.GetValue("Images:Cloudflare:Width", 1280),
        Height = configuration.GetValue("Images:Cloudflare:Height", 720),
        GeneratedImageDir = configuration.GetValue("Images:Cloudflare:GeneratedImageDir", "generated-images")!,
    };
}
