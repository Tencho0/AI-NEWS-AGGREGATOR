using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// Settings for <see cref="GeminiAiClient"/>, bound from configuration:
/// <c>Ai:Stages:Analyse:Model</c> (model ids are config, not code — ADR-0010),
/// <c>Ai:Categories</c> (the site's category taxonomy),
/// <c>Ai:Prices:InputPerMTok</c> / <c>Ai:Prices:OutputPerMTok</c> (USD per million tokens; 0 = free tier).
/// The RPM throttle lives in the shared <see cref="AiRateLimiter"/>.
/// </summary>
public sealed record GeminiAiOptions
{
    public string Model { get; init; } = "gemini-2.5-flash";
    public IReadOnlyList<string> Categories { get; init; } = DefaultCategories;
    public decimal InputPricePerMTok { get; init; }
    public decimal OutputPricePerMTok { get; init; }

    public static readonly IReadOnlyList<string> DefaultCategories =
    [
        "Общество", "Политика", "Икономика", "Криминално", "Спорт",
        "Култура", "Здраве", "Образование", "Времето", "Друго",
    ];

    public static GeminiAiOptions From(IConfiguration configuration) => new()
    {
        Model = configuration.GetValue("Ai:Stages:Analyse:Model", "gemini-2.5-flash")!,
        Categories = configuration.GetSection("Ai:Categories").Get<string[]>() is { Length: > 0 } configured
            ? configured
            : DefaultCategories,
        InputPricePerMTok = configuration.GetValue("Ai:Prices:InputPerMTok", 0m),
        OutputPricePerMTok = configuration.GetValue("Ai:Prices:OutputPerMTok", 0m),
    };
}
