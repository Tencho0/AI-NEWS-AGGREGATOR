using Google.GenAI;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// Builds the default-provider <see cref="IChatClient"/> from the official Google SDK (ADR-0010).
/// The API key comes from <c>Ai:Gemini:ApiKey</c>, falling back to the <c>GOOGLE_API_KEY</c> /
/// <c>GEMINI_API_KEY</c> environment variables the Google client also reads natively;
/// the model id comes from <c>Ai:Stages:{stage}:Model</c> (config, not code).
/// </summary>
public static class GeminiChatClientFactory
{
    public static bool HasApiKey(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(ResolveApiKey(configuration));

    public static IChatClient Create(IConfiguration configuration, string stage = "Analyse")
    {
        var model = configuration.GetValue($"Ai:Stages:{stage}:Model", "gemini-2.5-flash")!;
        return new Client(apiKey: ResolveApiKey(configuration)).AsIChatClient(model);
    }

    /// <summary>The Cluster/Draft/SelfCheck client, wrapped with the daily-quota fallback to
    /// the Analyse stage's model when <see cref="ShouldUseFallback"/> allows it; otherwise the
    /// plain client — a non-Gemini stage can never have its model switched.</summary>
    public static IChatClient CreateWithDailyQuotaFallback(
        IConfiguration configuration, string stage, GeminiModelFallback fallback)
    {
        var primary = Create(configuration, stage);
        if (!ShouldUseFallback(configuration, stage))
            return primary;

        return new GeminiQuotaFallbackChatClient(
            primary,
            configuration.GetValue($"Ai:Stages:{stage}:Model", "gemini-2.5-flash")!,
            Create(configuration, "Analyse"),
            configuration.GetValue("Ai:Stages:Analyse:Model", "gemini-2.5-flash")!,
            fallback);
    }

    /// <summary>Gemini-only guard for the daily-quota fallback: both the stage and the Analyse
    /// fallback target must resolve to the Gemini provider (<c>Ai:Stages:{stage}:Provider</c>,
    /// absent = gemini per ADR-0010 — the key does not exist in config today), and the stage
    /// must not already run the Analyse model (wrapping a model with itself is pointless).</summary>
    public static bool ShouldUseFallback(IConfiguration configuration, string stage)
    {
        if (!IsGemini(configuration, stage) || !IsGemini(configuration, "Analyse"))
            return false;

        var primaryModel = configuration.GetValue($"Ai:Stages:{stage}:Model", "gemini-2.5-flash")!;
        var fallbackModel = configuration.GetValue("Ai:Stages:Analyse:Model", "gemini-2.5-flash")!;
        return !string.Equals(primaryModel, fallbackModel, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGemini(IConfiguration configuration, string stage) =>
        string.Equals(
            configuration.GetValue($"Ai:Stages:{stage}:Provider", "gemini"),
            "gemini", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveApiKey(IConfiguration configuration)
    {
        var configured = configuration.GetValue<string?>("Ai:Gemini:ApiKey", null);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var environment = Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        return string.IsNullOrWhiteSpace(environment) ? null : environment;
    }
}
