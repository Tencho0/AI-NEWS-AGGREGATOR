using Newsroom.Infrastructure.Database;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Applies pending database migrations before any other hosted service starts.
/// Registered first: the default host awaits each StartAsync in registration order,
/// so a failed migration fails startup (fail-fast, docs/07-operations.md).
/// </summary>
public sealed class MigrationStartupService(MigrationRunner runner) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => runner.RunAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
