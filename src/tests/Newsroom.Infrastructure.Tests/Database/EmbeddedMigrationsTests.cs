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

    /// <summary>
    /// Guards the bug 0015 repairs: a UTF-8 file decoded as Windows-1252 turns Cyrillic into
    /// 'Ð'/'Ñ'-prefixed pairs and Latin accents into 'Ã' pairs. None of those characters occur
    /// in Bulgarian or in SQL, so their presence means a migration was written or re-saved
    /// through the wrong codepage and would write mojibake into the database.
    /// </summary>
    [Fact]
    public void Scripts_carry_no_mis_decoded_text()
    {
        char[] markers = ['Ð', 'Ñ', 'Ã', '�'];

        foreach (var script in Scripts)
        {
            var found = script.Sql.IndexOfAny(markers);

            Assert.True(found < 0,
                $"Migration {script.Version:0000}_{script.Name} contains mojibake marker "
                + $"'{(found < 0 ? ' ' : script.Sql[found])}' at offset {found} — the file was "
                + "decoded with the wrong codepage; re-save it as UTF-8.");
        }
    }

    /// <summary>
    /// The positive half of the guard above: 0015 repairs source names by spelling the correct
    /// Cyrillic out, so its literals must survive the file → embedded resource → StreamReader
    /// round-trip byte for byte. If this fails, the migration would "repair" one mojibake into
    /// another.
    /// </summary>
    [Fact]
    public void Source_name_repair_migration_round_trips_its_Cyrillic_literals()
    {
        var repair = Assert.Single(Scripts, s => s.Version == 15);

        Assert.Equal("fix_source_name_encoding", repair.Name);
        Assert.Contains("N'БТА (свободна лента)'", repair.Sql, StringComparison.Ordinal);
        Assert.Contains("N'Благоевград24'", repair.Sql, StringComparison.Ordinal);
    }
}
