using Newsroom.Infrastructure.Operations;

namespace Newsroom.Infrastructure.Tests.Operations;

public class SandboxOptionsTests
{
    private const string SandboxDb =
        "Server=.;Database=Newsroom_Sandbox;Integrated Security=True;TrustServerCertificate=True";
    private const string LiveDb =
        "Server=.;Database=Newsroom;Integrated Security=True;TrustServerCertificate=True";
    private const string SandboxRoot = @"C:\apps\newsroom-sandbox\images";
    private const string LiveRoot = @"C:\ProgramData\PredelNewsroom\images";

    [Fact]
    public void A_fully_isolated_configuration_has_no_violations() =>
        Assert.Empty(SandboxOptions.Violations(SandboxDb, "https://localhost:44350", SandboxRoot));

    [Theory]
    [InlineData("https://localhost:44350")]
    [InlineData("https://LOCALHOST:44350")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("https://localhost:44350/")]
    public void Local_site_urls_are_accepted(string url) =>
        Assert.Empty(SandboxOptions.Violations(SandboxDb, url, SandboxRoot));

    [Fact]
    public void The_live_database_is_refused()
    {
        var violations = SandboxOptions.Violations(LiveDb, "https://localhost:44350", SandboxRoot);
        Assert.Single(violations);
        Assert.Contains("Newsroom", violations[0]);
    }

    [Fact]
    public void A_connection_string_without_a_database_is_refused() =>
        Assert.Single(SandboxOptions.Violations(
            "Server=.;Integrated Security=True", "https://localhost:44350", SandboxRoot));

    [Fact]
    public void An_unparseable_connection_string_is_refused() =>
        Assert.Single(SandboxOptions.Violations(
            "this is not a connection string", "https://localhost:44350", SandboxRoot));

    [Theory]
    [InlineData("https://predel.news")]
    [InlineData("https://www.predel.news/umbraco")]
    public void A_public_site_url_is_refused(string url)
    {
        var violations = SandboxOptions.Violations(SandboxDb, url, SandboxRoot);
        Assert.Single(violations);
        Assert.Contains("localhost", violations[0]);
    }

    [Fact]
    public void A_site_url_that_is_not_absolute_is_refused() =>
        Assert.Single(SandboxOptions.Violations(SandboxDb, "localhost:44350", SandboxRoot));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(LiveRoot)]
    public void An_image_root_that_is_not_a_sandbox_root_is_refused(string root)
    {
        var violations = SandboxOptions.Violations(SandboxDb, "https://localhost:44350", root);
        Assert.Single(violations);
        Assert.Contains("Images:StorageRoot", violations[0]);
    }

    [Fact]
    public void The_sandbox_marker_in_the_image_root_is_case_insensitive() =>
        Assert.Empty(SandboxOptions.Violations(
            SandboxDb, "https://localhost:44350", @"D:\Newsroom-SANDBOX\images"));

    [Fact]
    public void Every_violation_is_reported_together_not_just_the_first() =>
        Assert.Equal(3, SandboxOptions.Violations(LiveDb, "https://predel.news", LiveRoot).Count);

    [Fact]
    public void DatabaseName_reads_the_catalog_and_reports_unparseable_as_null()
    {
        Assert.Equal("Newsroom_Sandbox", SandboxOptions.DatabaseName(SandboxDb));
        Assert.Null(SandboxOptions.DatabaseName("this is not a connection string"));
    }
}
