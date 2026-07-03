using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// Settings for <see cref="GeminiClusteringAi"/>, bound from configuration:
/// <c>Ai:Stages:Cluster:Model</c> (model ids are config, not code — ADR-0010),
/// <c>Ai:Prices:InputPerMTok</c> / <c>Ai:Prices:OutputPerMTok</c> (USD per million tokens; 0 = free tier).
/// Batch size, budget and cadence are read by TrendJob/AiBudget from <c>Ai:Stages:Cluster:*</c>;
/// the RPM throttle lives in the shared <see cref="AiRateLimiter"/>.
/// </summary>
public sealed record GeminiClusteringOptions
{
    public string Model { get; init; } = "gemini-2.5-flash";
    public decimal InputPricePerMTok { get; init; }
    public decimal OutputPricePerMTok { get; init; }

    public static GeminiClusteringOptions From(IConfiguration configuration) => new()
    {
        Model = configuration.GetValue("Ai:Stages:Cluster:Model", "gemini-2.5-flash")!,
        InputPricePerMTok = configuration.GetValue("Ai:Prices:InputPerMTok", 0m),
        OutputPricePerMTok = configuration.GetValue("Ai:Prices:OutputPerMTok", 0m),
    };
}
