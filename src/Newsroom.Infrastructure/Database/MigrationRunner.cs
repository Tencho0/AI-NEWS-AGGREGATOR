using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Newsroom.Infrastructure.Database;

/// <summary>
/// Applies embedded SQL migrations at startup. Forward-only; applied versions are recorded in
/// <c>nw_SchemaVersion</c>. Scripts live in <c>Database/Migrations/NNNN_name.sql</c> as embedded
/// resources and must be single-batch (no <c>GO</c> separators).
/// </summary>
public sealed class MigrationRunner(SqlConnectionFactory connectionFactory, ILogger<MigrationRunner> logger)
{
    private const string EnsureVersionTableSql =
        """
        IF OBJECT_ID(N'dbo.nw_SchemaVersion', N'U') IS NULL
        CREATE TABLE dbo.nw_SchemaVersion (
            Version      int           NOT NULL PRIMARY KEY,
            Name         nvarchar(200) NOT NULL,
            AppliedAtUtc datetime2     NOT NULL DEFAULT SYSUTCDATETIME()
        );
        """;

    public static IReadOnlyList<MigrationScript> LoadEmbeddedScripts(Assembly assembly)
    {
        var scripts = new List<MigrationScript>();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains(".Database.Migrations.", StringComparison.Ordinal))
                continue;

            var (version, name) = MigrationScript.ParseResourceName(resourceName);
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            scripts.Add(new MigrationScript(version, name, reader.ReadToEnd()));
        }

        var duplicates = scripts.GroupBy(s => s.Version).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"Duplicate migration version(s): {string.Join(", ", duplicates.Select(g => g.Key))}.");

        return scripts.OrderBy(s => s.Version).ToList();
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await EnsureDatabaseExistsAsync(ct);

        var scripts = LoadEmbeddedScripts(typeof(MigrationRunner).Assembly);

        using var connection = (SqlConnection)await connectionFactory.OpenAsync(ct);
        await connection.ExecuteAsync(EnsureVersionTableSql);

        var currentVersion = await connection.ExecuteScalarAsync<int?>(
            "SELECT MAX(Version) FROM dbo.nw_SchemaVersion") ?? 0;

        var pending = scripts.Where(s => s.Version > currentVersion).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date at version {Version}", currentVersion);
            return;
        }

        foreach (var script in pending)
        {
            ct.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(script.Sql, transaction: transaction);
            await connection.ExecuteAsync(
                "INSERT INTO dbo.nw_SchemaVersion (Version, Name) VALUES (@Version, @Name)",
                new { script.Version, script.Name }, transaction);
            transaction.Commit();
            logger.LogInformation("Applied migration {Version:0000}_{Name}", script.Version, script.Name);
        }
    }

    /// <summary>Dev convenience: creates the target database on the instance if it is missing.</summary>
    private async Task EnsureDatabaseExistsAsync(CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(connectionFactory.ConnectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("Connection string must specify an Initial Catalog / Database.");

        builder.InitialCatalog = "master";
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync(ct);

        var exists = await master.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM sys.databases WHERE name = @databaseName", new { databaseName });
        if (exists is null)
        {
            // Database names cannot be parameterised; QUOTENAME-style bracket escaping guards the identifier.
            await master.ExecuteAsync($"CREATE DATABASE [{databaseName.Replace("]", "]]")}]");
            logger.LogInformation("Created database {Database}", databaseName);
        }
    }
}
