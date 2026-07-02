using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Tests.Database;

public class MigrationScriptTests
{
    [Theory]
    [InlineData("Newsroom.Infrastructure.Database.Migrations.0001_initial.sql", 1, "initial")]
    [InlineData("Newsroom.Infrastructure.Database.Migrations.0042_add_topic_tables.sql", 42, "add_topic_tables")]
    public void ParseResourceName_parses_version_and_name(string resourceName, int expectedVersion, string expectedName)
    {
        var (version, name) = MigrationScript.ParseResourceName(resourceName);

        Assert.Equal(expectedVersion, version);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData("Ns.Database.Migrations.initial.sql")]        // no version prefix
    [InlineData("Ns.Database.Migrations.0001_.sql")]          // empty name
    [InlineData("Ns.Database.Migrations.0001initial.sql")]    // missing underscore
    [InlineData("Ns.Database.Migrations.abcd_initial.sql")]   // non-numeric version
    [InlineData("Ns.Database.Migrations.0000_initial.sql")]   // version must be positive
    [InlineData("Ns.Database.Migrations.0001_initial.txt")]   // wrong extension
    public void ParseResourceName_rejects_malformed_names(string resourceName)
    {
        Assert.Throws<FormatException>(() => MigrationScript.ParseResourceName(resourceName));
    }
}
