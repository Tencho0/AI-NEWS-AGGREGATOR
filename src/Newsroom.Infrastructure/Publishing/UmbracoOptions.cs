using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Publishing;

/// <summary>
/// Settings for Umbraco publishing (docs/05-integrations/umbraco.md, ADR-0007), bound from
/// configuration: <c>Umbraco:BaseUrl</c>, <c>Umbraco:ClientId</c> (the dedicated API user),
/// <c>Umbraco:ClientSecret</c> (fallback: the <c>NEWSROOM_UMBRACO_SECRET</c> environment
/// variable — secrets are not configuration, docs/06-security.md), plus the attempt cap and
/// the publish-cycle interval.
/// </summary>
public sealed record UmbracoOptions
{
    public string BaseUrl { get; init; } = "";
    public string ClientId { get; init; } = "newsroom-bot";
    public string ClientSecret { get; init; } = "";
    public int MaxAttempts { get; init; } = 3;
    public int CheckSeconds { get; init; } = 60;

    /// <summary>Publishing only runs with an endpoint and a credential to reach it.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ClientSecret);

    public static UmbracoOptions From(IConfiguration configuration) => new()
    {
        BaseUrl = configuration.GetValue("Umbraco:BaseUrl", "")!,
        ClientId = configuration.GetValue("Umbraco:ClientId", "newsroom-bot")!,
        ClientSecret = ResolveClientSecret(configuration),
        MaxAttempts = configuration.GetValue("Umbraco:MaxAttempts", 3),
        CheckSeconds = configuration.GetValue("Umbraco:CheckSeconds", 60),
    };

    private static string ResolveClientSecret(IConfiguration configuration)
    {
        var configured = configuration.GetValue("Umbraco:ClientSecret", "");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured!;
        return Environment.GetEnvironmentVariable("NEWSROOM_UMBRACO_SECRET") ?? "";
    }
}
