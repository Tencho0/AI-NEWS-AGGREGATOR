using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Operations;

/// <summary>
/// Sandbox mode (ADR-0014): a second worker instance that develops against the full pipeline
/// while the live one keeps running. Isolation is not left to configuration discipline —
/// <see cref="Violations"/> is checked at startup and the worker refuses to run while any of
/// them holds, so a copied connection string or a forgotten base URL fails fast instead of
/// consuming the live review queue or publishing to the real site.
/// <para>
/// The checks are deliberately fails-closed (a *positive* assertion that each destination looks
/// like a sandbox one) rather than a blocklist of live values: an unset
/// <c>Images:StorageRoot</c> resolves to the shared <c>%ProgramData%</c> default, and a blocklist
/// would wave it through.
/// </para>
/// </summary>
public sealed record SandboxOptions
{
    /// <summary>The <c>DOTNET_ENVIRONMENT</c> value that selects appsettings.Sandbox.json.</summary>
    public const string EnvironmentName = "Sandbox";

    /// <summary>The sandbox's own dotnet user-secrets store. Deliberately a readable string
    /// rather than a GUID so the runbook's <c>dotnet user-secrets --id</c> commands are typeable.
    /// The live store (the csproj's UserSecretsId) is only auto-loaded in the Development
    /// environment, so it is unreachable from here.</summary>
    public const string UserSecretsId = "newsroom-worker-sandbox";

    public const string RequiredDatabaseSuffix = "_Sandbox";
    public const string RequiredStorageRootMarker = "sandbox";

    public bool Enabled { get; init; }

    public static SandboxOptions From(IConfiguration configuration) => new()
    {
        Enabled = configuration.GetValue("Sandbox:Enabled", false),
    };

    /// <summary>Every way the configuration still points at something live. Empty = safe to run.
    /// All violations are reported together so a misconfigured sandbox is fixed in one pass.</summary>
    /// <param name="imageStorageRoot">The *resolved* root (ImageStorageOptions.Root), not the raw
    /// config value — an unset value must be judged by what it actually resolves to.</param>
    public static IReadOnlyList<string> Violations(
        string connectionString, string umbracoBaseUrl, string imageStorageRoot)
    {
        var violations = new List<string>();

        if (DatabaseName(connectionString) is not { } database)
        {
            violations.Add("ConnectionStrings:Newsroom is not a valid SQL Server connection string.");
        }
        else if (!database.EndsWith(RequiredDatabaseSuffix, StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(
                $"ConnectionStrings:Newsroom points at database '{database}' — a sandbox database "
                + $"name must end with '{RequiredDatabaseSuffix}'. Running against the live database "
                + "would consume the live review queue and publish live drafts.");
        }

        if (!Uri.TryCreate(umbracoBaseUrl, UriKind.Absolute, out var site))
        {
            violations.Add($"Umbraco:BaseUrl ('{umbracoBaseUrl}') is not an absolute URL.");
        }
        else if (!IsLoopback(site.Host))
        {
            violations.Add(
                $"Umbraco:BaseUrl points at '{site.Host}' — a sandbox may only publish to "
                + "localhost or 127.0.0.1.");
        }

        if (string.IsNullOrWhiteSpace(imageStorageRoot)
            || !imageStorageRoot.Contains(RequiredStorageRootMarker, StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(
                $"Images:StorageRoot ('{imageStorageRoot}') must contain "
                + $"'{RequiredStorageRootMarker}' — otherwise the sandbox shares the live image "
                + "folder and its retention sweep deletes live files.");
        }

        return violations;
    }

    /// <summary>The connection string's database, or null when it will not parse — used by the
    /// guard above and by the startup banner.</summary>
    public static string? DatabaseName(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1";
}
