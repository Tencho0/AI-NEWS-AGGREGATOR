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
