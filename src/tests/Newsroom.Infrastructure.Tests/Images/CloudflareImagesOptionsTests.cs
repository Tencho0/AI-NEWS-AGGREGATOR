using Microsoft.Extensions.Configuration;

using Newsroom.Infrastructure.Images;

namespace Newsroom.Infrastructure.Tests.Images;

public class CloudflareImagesOptionsTests
{
    private static IConfiguration Config(params KeyValuePair<string, string?>[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Generation_is_disabled_by_default_even_when_credentials_are_configured()
    {
        var options = CloudflareImagesOptions.From(Config(
            new("Images:Cloudflare:AccountId", "acc-123"),
            new("Images:Cloudflare:ApiToken", "cf-token")));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Generation_turns_on_only_with_an_explicit_opt_in()
    {
        var options = CloudflareImagesOptions.From(Config(
            new("Images:Cloudflare:Enabled", "true"),
            new("Images:Cloudflare:AccountId", "acc-123"),
            new("Images:Cloudflare:ApiToken", "cf-token")));

        Assert.True(options.Enabled);
    }
}
