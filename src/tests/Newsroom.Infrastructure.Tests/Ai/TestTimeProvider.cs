namespace Newsroom.Infrastructure.Tests.Ai;

/// <summary>Manually advanced clock for fallback-expiry tests (no package dependency).</summary>
internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
