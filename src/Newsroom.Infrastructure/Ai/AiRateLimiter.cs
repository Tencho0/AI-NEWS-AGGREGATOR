using System.Threading.RateLimiting;

using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// Process-wide sliding-window RPM throttle shared by every AI stage (ADR-0010): the free-tier
/// RPM cap applies to the API key, not to a stage, so analysis and clustering must draw from
/// the same window. Registered as a singleton; <c>Ai:RequestsPerMinute</c> sets the cap.
/// Throttled callers wait, they are never rejected.
/// </summary>
public sealed class AiRateLimiter(int requestsPerMinute) : IDisposable
{
    private readonly SlidingWindowRateLimiter rateLimiter = new(new SlidingWindowRateLimiterOptions
    {
        PermitLimit = requestsPerMinute,
        Window = TimeSpan.FromMinutes(1),
        SegmentsPerWindow = 6,
        QueueLimit = int.MaxValue, // throttled callers wait, they are never rejected
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    /// <summary>Waits for a request permit; dispose the lease when the request completes.</summary>
    public ValueTask<RateLimitLease> AcquireAsync(CancellationToken ct) =>
        rateLimiter.AcquireAsync(1, ct);

    public void Dispose() => rateLimiter.Dispose();

    public static AiRateLimiter From(IConfiguration configuration) =>
        new(configuration.GetValue("Ai:RequestsPerMinute", 8));
}
