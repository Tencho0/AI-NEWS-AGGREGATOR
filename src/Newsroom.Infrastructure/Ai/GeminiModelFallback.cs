using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// In-memory registry of Gemini models whose free-tier daily quota is exhausted, keyed by
/// model id — not stage — so stages sharing a model (Draft + SelfCheck) flip to the fallback
/// together. An activation expires at the next midnight US-Pacific, Gemini's actual quota
/// reset. Deliberately not persisted: after a worker restart the primary model is re-probed
/// and, at worst, one 429 re-activates the fallback. Registered as a singleton.
/// </summary>
public sealed class GeminiModelFallback(TimeProvider timeProvider, ILogger<GeminiModelFallback> logger)
{
    // Gemini quota days roll over at midnight US-Pacific (~10:00 Europe/Sofia). If the OS has
    // no Pacific timezone data, a fixed UTC-8 approximation is close enough: the worst case is
    // a one-hour-late re-probe (DST), self-healing via one extra 429.
    private static readonly TimeZoneInfo Pacific = ResolvePacificZone();

    private readonly ConcurrentDictionary<string, DateTimeOffset> exhaustedUntil =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while <paramref name="model"/> is inside its fallback window; expired
    /// entries are removed on observation (the first call after the reset restores routing).</summary>
    public bool IsActive(string model)
    {
        if (!exhaustedUntil.TryGetValue(model, out var until))
            return false;
        if (timeProvider.GetUtcNow() < until)
            return true;

        // Remove only the entry we read: a concurrent re-Activate must not be wiped out.
        if (exhaustedUntil.TryRemove(new KeyValuePair<string, DateTimeOffset>(model, until)))
            logger.LogInformation(
                "Gemini daily quota window for {Model} has reset; restoring the primary model", model);
        return false;
    }

    /// <summary>Marks <paramref name="model"/> exhausted until the next Pacific midnight.</summary>
    public void Activate(string model)
    {
        var until = NextPacificMidnight(timeProvider.GetUtcNow());
        exhaustedUntil[model] = until;
        logger.LogWarning(
            "Gemini model {Model} hit its daily quota; using the Analyse fallback model until {UntilUtc:u}",
            model, until.UtcDateTime);
    }

    private static DateTimeOffset NextPacificMidnight(DateTimeOffset nowUtc)
    {
        var pacificNow = TimeZoneInfo.ConvertTime(nowUtc, Pacific);
        var nextMidnight = pacificNow.Date.AddDays(1); // Pacific wall clock; US DST never shifts at midnight
        return new DateTimeOffset(nextMidnight, Pacific.GetUtcOffset(nextMidnight));
    }

    private static TimeZoneInfo ResolvePacificZone()
    {
        foreach (var id in new[] { "Pacific Standard Time", "America/Los_Angeles" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.CreateCustomTimeZone("UTC-8", TimeSpan.FromHours(-8), "UTC-8", "UTC-8");
    }
}
