using Newsroom.Core.Operations;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Startup crash recovery (docs/07-operations.md #4): drafts stuck in 'Generating' with an
/// UpdatedAtUtc older than an hour can only be leftovers of a process that died mid-generation
/// — the drafting cycle is synchronous, so nothing legitimate stays Generating that long
/// untouched. They flip to GenerationFailed with an explanatory error; editor-requested
/// regenerations among them reach the editor through the existing failure-notice path.
/// Registered right after <see cref="MigrationStartupService"/> (schema first), before any
/// job starts. Best-effort: a failed sweep logs an error and lets the host start — the next
/// restart retries it.
/// </summary>
public sealed class StartupRecoveryService(
    IOperationsRepository operations,
    ILogger<StartupRecoveryService> logger) : IHostedService
{
    private static readonly TimeSpan StuckAfter = TimeSpan.FromHours(1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var recovered = await operations.FailStuckGeneratingDraftsAsync(
                DateTime.UtcNow - StuckAfter, "Worker was restarted mid-generation", cancellationToken);
            if (recovered > 0)
                logger.LogWarning(
                    "Startup recovery: {Count} draft(s) stuck in Generating marked GenerationFailed",
                    recovered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Startup crash-recovery sweep failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
