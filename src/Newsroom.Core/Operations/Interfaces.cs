namespace Newsroom.Core.Operations;

/// <summary>
/// Per-job liveness signal (docs/07-operations.md): each pipeline job records a heartbeat at
/// the end of a completed cycle so the watchdog can alert when a job has been silent for
/// longer than 3× its interval. Implementations are best-effort by contract — a failed beat
/// is logged at debug and swallowed, because a heartbeat must never break the cycle it
/// reports on.
/// </summary>
public interface IJobHeartbeat
{
    /// <summary>Records that <paramref name="jobName"/> (a <see cref="JobNames"/> constant)
    /// just completed a cycle. Never throws except on cancellation.</summary>
    Task BeatAsync(string jobName, CancellationToken ct);
}

/// <summary>
/// Persistence for the ops jobs (watchdog, daily digest, retention, startup crash recovery —
/// docs/07-operations.md). Everything is keyed on nw_Config or plain aggregate queries; no
/// job state lives in memory, so restarts are safe by design.
/// </summary>
public interface IOperationsRepository
{
    /// <summary>All per-job heartbeats (nw_Config keys 'Heartbeat:{job}'), job name → last
    /// beat (UTC). Keys with unparseable values are skipped.</summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetHeartbeatsAsync(CancellationToken ct);

    /// <summary>Raw nw_Config value, or null when the key does not exist.</summary>
    Task<string?> GetConfigValueAsync(string key, CancellationToken ct);

    /// <summary>Upserts a nw_Config value (MERGE — same contract as runtime flags).</summary>
    Task SetConfigValueAsync(string key, string value, CancellationToken ct);

    /// <summary>Aggregates one day of pipeline activity (everything at/after
    /// <paramref name="dayUtc"/>) for the daily digest.</summary>
    Task<DigestStats> GetDigestStatsAsync(DateTime dayUtc, CancellationToken ct);

    /// <summary>Retention: NULLs nw_SourceArticle.ExtractedText for articles first seen before
    /// <paramref name="cutoffUtc"/> (the text is an internal working copy, docs/06-security.md).
    /// Returns the number of pruned rows.</summary>
    Task<int> ClearExpiredExtractedTextAsync(DateTime cutoffUtc, CancellationToken ct);

    /// <summary>Retention: deletes nw_Log rows older than <paramref name="cutoffUtc"/>.
    /// Returns the number of deleted rows.</summary>
    Task<int> DeleteExpiredLogsAsync(DateTime cutoffUtc, CancellationToken ct);

    /// <summary>Crash recovery (docs/07-operations.md #4): drafts stuck in 'Generating' whose
    /// UpdatedAtUtc is older than <paramref name="cutoffUtc"/> flip to 'GenerationFailed' with
    /// <paramref name="error"/>. Returns the number of recovered drafts.</summary>
    Task<int> FailStuckGeneratingDraftsAsync(DateTime cutoffUtc, string error, CancellationToken ct);
}
