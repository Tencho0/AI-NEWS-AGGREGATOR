using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Publishing;

/// <summary>
/// Cross-destination publishing mode, bound from configuration.
/// <para>
/// <c>Publishing:FacebookOnly</c> (default <c>false</c>) is a temporary operational switch
/// (decision-log 2026-07-08): while the Predel website is being polished, Approved drafts are
/// published straight to the Facebook Page and the Umbraco (website) leg is skipped entirely —
/// no site call, no fake publish record. The Umbraco leg stays fully wired in
/// <see cref="PublishJob"/>; flipping this flag back to <c>false</c> (and configuring
/// <c>Umbraco:BaseUrl</c>/<c>ClientSecret</c>) restores the complete site → Facebook pipeline
/// with no code change.
/// </para>
/// </summary>
public sealed record PublishingOptions
{
    public bool FacebookOnly { get; init; }

    public static PublishingOptions From(IConfiguration configuration) => new()
    {
        FacebookOnly = configuration.GetValue("Publishing:FacebookOnly", false),
    };
}
