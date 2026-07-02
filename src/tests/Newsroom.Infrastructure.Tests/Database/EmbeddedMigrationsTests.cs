using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Tests.Database;

/// <summary>
/// Regression guards over the real embedded migration set: these fail at build time in CI when
/// someone adds a migration with a duplicate/out-of-convention name or a GO separator
/// (the runner executes single-batch scripts — see MigrationRunner).
/// </summary>
public class EmbeddedMigrationsTests
{
    private static readonly IReadOnlyList<MigrationScript> Scripts =
        MigrationRunner.LoadEmbeddedScripts(typeof(MigrationRunner).Assembly);

    [Fact]
    public void At_least_the_initial_migration_is_embedded()
    {
        Assert.NotEmpty(Scripts);
        Assert.Equal(1, Scripts[0].Version);
        Assert.Equal("initial", Scripts[0].Name);
    }

    [Fact]
    public void Versions_are_unique_and_returned_in_ascending_order()
    {
        var versions = Scripts.Select(s => s.Version).ToList();

        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(versions.Distinct().Count(), versions.Count);
    }

    [Fact]
    public void Scripts_are_single_batch_without_GO_separators()
    {
        foreach (var script in Scripts)
        {
            var hasGo = script.Sql
                .Split('\n')
                .Any(line => line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase));

            Assert.False(hasGo, $"Migration {script.Version:0000}_{script.Name} contains a GO separator.");
        }
    }

    [Fact]
    public void Scripts_are_not_empty()
    {
        Assert.All(Scripts, s => Assert.False(string.IsNullOrWhiteSpace(s.Sql)));
    }
}
