namespace Newsroom.Infrastructure.Database;

/// <summary>
/// A single SQL migration, sourced from an embedded resource named
/// <c>...Database.Migrations.NNNN_name.sql</c>. Migrations are forward-only and must be
/// written backward-compatible (see docs/09-deployment.md).
/// </summary>
public sealed record MigrationScript(int Version, string Name, string Sql)
{
    /// <summary>
    /// Parses version and name from an embedded-resource name, e.g.
    /// "Newsroom.Infrastructure.Database.Migrations.0001_initial.sql" → (1, "initial").
    /// </summary>
    public static (int Version, string Name) ParseResourceName(string resourceName)
    {
        const string suffix = ".sql";
        if (!resourceName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Migration resource '{resourceName}' must end with '{suffix}'.");

        // File name is the last dot-segment before ".sql" (dots earlier belong to the namespace).
        var stem = resourceName[..^suffix.Length];
        var fileName = stem[(stem.LastIndexOf('.') + 1)..];

        var separator = fileName.IndexOf('_');
        if (separator <= 0 || separator == fileName.Length - 1)
            throw new FormatException(
                $"Migration '{fileName}' must be named 'NNNN_name.sql' (e.g. 0001_initial.sql).");

        var versionPart = fileName[..separator];
        if (!int.TryParse(versionPart, out var version) || version <= 0)
            throw new FormatException(
                $"Migration '{fileName}' must start with a positive numeric version, got '{versionPart}'.");

        return (version, fileName[(separator + 1)..]);
    }
}
