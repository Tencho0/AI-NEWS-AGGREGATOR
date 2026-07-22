# Gemini Daily-Quota Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When Cluster, Draft, or SelfCheck receives a *daily*-quota 429 from Gemini, the stage automatically switches to the Analyse stage's model until the quota resets (midnight US-Pacific), then switches back — Gemini-only, jobs untouched.

**Architecture:** A delegating `IChatClient` wrapper (`GeminiQuotaFallbackChatClient`) around each stage's Gemini client, sharing a per-**model** in-memory fallback registry (`GeminiModelFallback`) so Draft+SelfCheck (same model) flip together. Wired in `Program.cs` via a new `GeminiChatClientFactory` method that only wraps Gemini-provider stages. Spec: `docs/superpowers/specs/2026-07-21-gemini-daily-quota-fallback-design.md`.

**Tech Stack:** .NET 10, `Microsoft.Extensions.AI.Abstractions` (`IChatClient` seam, ADR-0010), xUnit 2.9.3, `TimeProvider` (BCL).

## Global Constraints

- **Never commit.** The owner reviews and commits himself after each work block. End every task with build clean + tests green, then stop. (This overrides any skill default that says to commit.)
- **Never edit files with PowerShell `Get-Content`/`Set-Content`** — PS 5.1 corrupts UTF-8 Cyrillic. Use the Edit/Write file tools only. Several target files contain Cyrillic.
- Trigger is the **real API daily-quota 429 only** — local `DailyRequestBudget` exhaustion keeps today's skip-the-cycle behaviour (owner decision, spec §Problem).
- Per-minute/per-token 429s must NOT trigger fallback — they stay transient exactly as today.
- **Gemini-only:** a stage whose `Ai:Stages:{stage}:Provider` (default `"gemini"` when absent) is not Gemini is never wrapped; same for the Analyse fallback target. No cross-provider fallback.
- No new config keys; fallback model = `Ai:Stages:Analyse:Model`. No new NuGet packages.
- Existing semantics of `AiTransientErrors.IsQuotaExhausted` / `IsTransient` must not change.
- Code style: file-scoped namespaces, primary constructors, `/// <summary>` comments that explain *why* (match `src/Newsroom.Infrastructure/Ai/*.cs`).
- Build: `dotnet build Newsroom.slnx` · Tests: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj`

---

### Task 1: Daily-quota classifier `AiTransientErrors.IsDailyQuotaExhausted`

**Files:**
- Modify: `src/Newsroom.Infrastructure/Ai/AiTransientErrors.cs`
- Test: `src/tests/Newsroom.Infrastructure.Tests/Ai/AiTransientErrorsTests.cs`

**Interfaces:**
- Consumes: existing `AiTransientErrors.IsQuotaExhausted(Exception)`.
- Produces: `public static bool IsDailyQuotaExhausted(Exception ex)` — true only for quota-exhaustion errors whose message names a per-day quota. Task 3 catches on this.

- [ ] **Step 1: Write the failing tests**

Append to the existing `AiTransientErrorsTests` class in `src/tests/Newsroom.Infrastructure.Tests/Ai/AiTransientErrorsTests.cs`:

```csharp
    [Theory]
    [InlineData("Error 429: RESOURCE_EXHAUSTED. Quota exceeded for quota id: GenerateRequestsPerDayPerProjectPerModel-FreeTier.")]
    [InlineData("You exceeded your current quota: 20 requests per day for this model.")]
    public void Daily_quota_wordings_are_daily_exhaustion(string message)
    {
        var ex = new InvalidOperationException(message);
        Assert.True(AiTransientErrors.IsDailyQuotaExhausted(ex));
        Assert.True(AiTransientErrors.IsTransient(ex)); // still transient for job-level catches
    }

    [Theory]
    [InlineData("Error 429: RESOURCE_EXHAUSTED. Quota exceeded for quota id: GenerateRequestsPerMinutePerProjectPerModel-FreeTier.")]
    [InlineData("Error 429: RESOURCE_EXHAUSTED")]
    [InlineData("The model is overloaded. Please try again later.")]
    [InlineData("HTTP 503 Service Unavailable")]
    [InlineData("AI returned malformed JSON for the analysis batch: oops")]
    public void Non_daily_failures_are_not_daily_exhaustion(string message) =>
        Assert.False(AiTransientErrors.IsDailyQuotaExhausted(new InvalidOperationException(message)));
```

Note the second daily case ("per day" prose) and that a bare `429` without per-day wording is NOT daily — ambiguity resolves to "not daily" so behaviour safely stays as today.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AiTransientErrorsTests"`
Expected: build FAILS with `'AiTransientErrors' does not contain a definition for 'IsDailyQuotaExhausted'`.

- [ ] **Step 3: Implement the classifier**

In `src/Newsroom.Infrastructure/Ai/AiTransientErrors.cs`, insert after the existing `IsQuotaExhausted` method:

```csharp
    /// <summary>Daily-quota exhaustion specifically (Gemini free tier, resets at midnight
    /// US-Pacific): a quota 429 whose message names a per-day quota — Google's payload carries
    /// quota ids like "GenerateRequestsPerDayPerProjectPerModel-FreeTier". Per-minute/per-token
    /// 429s deliberately do NOT match (falling back for a whole day over an RPM blip would be
    /// wrong); when the wording is ambiguous this returns false and behaviour stays as today.
    /// Used by GeminiQuotaFallbackChatClient to switch a stage to the Analyse model.</summary>
    public static bool IsDailyQuotaExhausted(Exception ex) =>
        IsQuotaExhausted(ex)
        && (ex.Message.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("per day", StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AiTransientErrorsTests"`
Expected: PASS (all, including the pre-existing tests).

- [ ] **Step 5: Checkpoint — do not commit**

Report the task done; the owner commits.

---

### Task 2: Fallback state registry `GeminiModelFallback`

**Files:**
- Create: `src/Newsroom.Infrastructure/Ai/GeminiModelFallback.cs`
- Create: `src/tests/Newsroom.Infrastructure.Tests/Ai/TestTimeProvider.cs`
- Test: `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiModelFallbackTests.cs`

**Interfaces:**
- Consumes: BCL `TimeProvider`, `ILogger<GeminiModelFallback>`.
- Produces (Task 3 and 4 rely on these exact signatures):
  - `public sealed class GeminiModelFallback(TimeProvider timeProvider, ILogger<GeminiModelFallback> logger)`
  - `public bool IsActive(string model)`
  - `public void Activate(string model)`
  - `internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider` with settable `UtcNow` (test project only).

- [ ] **Step 1: Write the shared test clock**

Create `src/tests/Newsroom.Infrastructure.Tests/Ai/TestTimeProvider.cs`:

```csharp
namespace Newsroom.Infrastructure.Tests.Ai;

/// <summary>Manually advanced clock for fallback-expiry tests (no package dependency).</summary>
internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
```

- [ ] **Step 2: Write the failing tests**

Create `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiModelFallbackTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;

using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiModelFallbackTests
{
    // 2026-07-21 10:00 UTC = 03:00 US-Pacific (PDT, UTC-7). The next Pacific midnight is
    // 2026-07-22 07:00 UTC (or 08:00 UTC under the fixed UTC-8 fallback zone) — the assertions
    // below hold under both, so a missing OS timezone database cannot break the suite.
    private static readonly DateTimeOffset Start = new(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);

    private static (GeminiModelFallback Fallback, TestTimeProvider Clock) Create()
    {
        var clock = new TestTimeProvider(Start);
        return (new GeminiModelFallback(clock, NullLogger<GeminiModelFallback>.Instance), clock);
    }

    [Fact]
    public void Inactive_by_default()
    {
        var (fallback, _) = Create();
        Assert.False(fallback.IsActive("gemini-2.5-flash"));
    }

    [Fact]
    public void Activate_makes_only_that_model_active()
    {
        var (fallback, _) = Create();
        fallback.Activate("gemini-3.5-flash");
        Assert.True(fallback.IsActive("gemini-3.5-flash"));
        Assert.False(fallback.IsActive("gemini-2.5-flash")); // per-model, not global
    }

    [Fact]
    public void Stays_active_past_utc_midnight_until_pacific_midnight()
    {
        var (fallback, clock) = Create();
        fallback.Activate("gemini-2.5-flash");

        // 00:30 UTC next day: UTC midnight has passed, Pacific midnight has not — the Gemini
        // quota resets on Pacific time, so the fallback must still be active.
        clock.UtcNow = new DateTimeOffset(2026, 7, 22, 0, 30, 0, TimeSpan.Zero);
        Assert.True(fallback.IsActive("gemini-2.5-flash"));

        // Just before Pacific midnight (06:59 UTC under PDT; the UTC-8 fallback resets later).
        clock.UtcNow = new DateTimeOffset(2026, 7, 22, 6, 59, 0, TimeSpan.Zero);
        Assert.True(fallback.IsActive("gemini-2.5-flash"));
    }

    [Fact]
    public void Expires_after_pacific_midnight()
    {
        var (fallback, clock) = Create();
        fallback.Activate("gemini-2.5-flash");

        // 08:01 UTC is past Pacific midnight under both PDT (07:00) and fixed UTC-8 (08:00).
        clock.UtcNow = new DateTimeOffset(2026, 7, 22, 8, 1, 0, TimeSpan.Zero);
        Assert.False(fallback.IsActive("gemini-2.5-flash"));
    }

    [Fact]
    public void Can_reactivate_after_expiry()
    {
        var (fallback, clock) = Create();
        fallback.Activate("gemini-2.5-flash");
        clock.UtcNow = new DateTimeOffset(2026, 7, 22, 8, 1, 0, TimeSpan.Zero);
        Assert.False(fallback.IsActive("gemini-2.5-flash"));

        fallback.Activate("gemini-2.5-flash"); // quota still exhausted after a re-probe
        Assert.True(fallback.IsActive("gemini-2.5-flash"));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GeminiModelFallbackTests"`
Expected: build FAILS with `The type or namespace name 'GeminiModelFallback' could not be found`.

- [ ] **Step 4: Implement `GeminiModelFallback`**

Create `src/Newsroom.Infrastructure/Ai/GeminiModelFallback.cs`:

```csharp
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
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GeminiModelFallbackTests"`
Expected: PASS (5 tests).

- [ ] **Step 6: Checkpoint — do not commit**

Report the task done; the owner commits.

---

### Task 3: Delegating wrapper `GeminiQuotaFallbackChatClient`

**Files:**
- Create: `src/Newsroom.Infrastructure/Ai/GeminiQuotaFallbackChatClient.cs`
- Test: `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiQuotaFallbackChatClientTests.cs`

**Interfaces:**
- Consumes: `GeminiModelFallback.IsActive(string)` / `.Activate(string)` (Task 2), `AiTransientErrors.IsDailyQuotaExhausted(Exception)` (Task 1), `Microsoft.Extensions.AI.IChatClient`.
- Produces (Task 4 relies on this exact constructor):
  - `public sealed class GeminiQuotaFallbackChatClient(IChatClient primary, string primaryModel, IChatClient fallback, string fallbackModel, GeminiModelFallback state) : IChatClient`

- [ ] **Step 1: Write the failing tests**

Create `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiQuotaFallbackChatClientTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiQuotaFallbackChatClientTests
{
    private const string PrimaryModel = "gemini-3.5-flash";
    private const string FallbackModel = "gemini-3.1-flash-lite";

    private const string Daily429 =
        "Error 429: RESOURCE_EXHAUSTED. Quota exceeded for quota id: GenerateRequestsPerDayPerProjectPerModel-FreeTier.";
    private const string PerMinute429 =
        "Error 429: RESOURCE_EXHAUSTED. Quota exceeded for quota id: GenerateRequestsPerMinutePerProjectPerModel-FreeTier.";

    private static ChatResponse Response(string? modelId = null) =>
        new(new ChatMessage(ChatRole.Assistant, "ok")) { ModelId = modelId };

    private static GeminiModelFallback State(TimeProvider? clock = null) =>
        new(clock ?? TimeProvider.System, NullLogger<GeminiModelFallback>.Instance);

    private static Task<ChatResponse> Send(IChatClient client) =>
        client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

    [Fact]
    public async Task Passes_through_to_primary_and_stamps_its_model_id()
    {
        var primary = new FakeChatClient(() => Response());
        var fallback = new FakeChatClient(() => Response());
        var client = new GeminiQuotaFallbackChatClient(primary, PrimaryModel, fallback, FallbackModel, State());

        var response = await Send(client);

        Assert.Equal(PrimaryModel, response.ModelId); // null ModelId stamped for the cost ledger
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Server_reported_model_id_is_preserved()
    {
        var primary = new FakeChatClient(() => Response("server-reported"));
        var client = new GeminiQuotaFallbackChatClient(
            primary, PrimaryModel, new FakeChatClient(() => Response()), FallbackModel, State());

        var response = await Send(client);

        Assert.Equal("server-reported", response.ModelId);
    }

    [Fact]
    public async Task Daily_quota_429_activates_fallback_and_retries_the_same_request()
    {
        var state = State();
        var primary = new FakeChatClient(() => throw new InvalidOperationException(Daily429));
        var fallback = new FakeChatClient(() => Response());
        var client = new GeminiQuotaFallbackChatClient(primary, PrimaryModel, fallback, FallbackModel, state);

        var response = await Send(client); // the triggering cycle succeeds, not wasted

        Assert.Equal(FallbackModel, response.ModelId);
        Assert.True(state.IsActive(PrimaryModel));
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);

        await Send(client); // while active, the primary is not probed again

        Assert.Equal(1, primary.Calls);
        Assert.Equal(2, fallback.Calls);
    }

    [Fact]
    public async Task Per_minute_429_propagates_without_activating_fallback()
    {
        var state = State();
        var primary = new FakeChatClient(() => throw new InvalidOperationException(PerMinute429));
        var fallback = new FakeChatClient(() => Response());
        var client = new GeminiQuotaFallbackChatClient(primary, PrimaryModel, fallback, FallbackModel, state);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(client));

        Assert.Contains("PerMinute", ex.Message); // transient path unchanged: retry next cycle
        Assert.False(state.IsActive(PrimaryModel));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Stages_sharing_a_model_share_fallback_fate()
    {
        // Draft and SelfCheck both run gemini-3.5-flash: state is keyed by model, so a daily
        // 429 seen by one wrapper must flip the other without it ever probing its primary.
        var state = State();
        var draftPrimary = new FakeChatClient(() => throw new InvalidOperationException(Daily429));
        var draft = new GeminiQuotaFallbackChatClient(
            draftPrimary, PrimaryModel, new FakeChatClient(() => Response()), FallbackModel, state);
        var selfCheckPrimary = new FakeChatClient(() => Response());
        var selfCheckFallback = new FakeChatClient(() => Response());
        var selfCheck = new GeminiQuotaFallbackChatClient(
            selfCheckPrimary, PrimaryModel, selfCheckFallback, FallbackModel, state);

        await Send(draft);
        var response = await Send(selfCheck);

        Assert.Equal(FallbackModel, response.ModelId);
        Assert.Equal(0, selfCheckPrimary.Calls);
        Assert.Equal(1, selfCheckFallback.Calls);
    }

    [Fact]
    public async Task Primary_is_probed_again_after_the_quota_window_resets()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero));
        var state = State(clock);
        var primary = new FakeChatClient(
            () => throw new InvalidOperationException(Daily429), // first call: quota exhausted
            () => Response());                                   // after reset: healthy again
        var fallback = new FakeChatClient(() => Response());
        var client = new GeminiQuotaFallbackChatClient(primary, PrimaryModel, fallback, FallbackModel, state);

        await Send(client);
        clock.UtcNow = clock.UtcNow.AddHours(26); // safely past the next Pacific midnight

        var response = await Send(client);

        Assert.Equal(PrimaryModel, response.ModelId);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    /// <summary>Scripted IChatClient: call N runs script[N] (last entry repeats). A step either
    /// returns a response or throws, standing in for the Gemini adapter behind the seam.</summary>
    private sealed class FakeChatClient(params Func<ChatResponse>[] script) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var step = script[Math.Min(Calls, script.Length - 1)];
            Calls++;
            return Task.FromResult(step());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GeminiQuotaFallbackChatClientTests"`
Expected: build FAILS with `The type or namespace name 'GeminiQuotaFallbackChatClient' could not be found`.

- [ ] **Step 3: Implement the wrapper**

Create `src/Newsroom.Infrastructure/Ai/GeminiQuotaFallbackChatClient.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// Delegating <see cref="IChatClient"/> that keeps a stage alive when its Gemini model's
/// free-tier daily quota runs out: on a daily-quota 429 (never a per-minute one — see
/// <see cref="AiTransientErrors.IsDailyQuotaExhausted"/>) it activates the shared per-model
/// <see cref="GeminiModelFallback"/> and retries the same request once on the Analyse stage's
/// model, so the triggering cycle succeeds instead of being wasted. While the window is active
/// every request routes to the fallback; after the Pacific-midnight reset the primary is
/// probed again. Gemini-only by construction — non-Gemini stages are never wrapped
/// (<see cref="GeminiChatClientFactory"/>). The response's ModelId is stamped with the model
/// actually used so nw_CostLedger records fallback usage truthfully.
/// </summary>
public sealed class GeminiQuotaFallbackChatClient(
    IChatClient primary,
    string primaryModel,
    IChatClient fallback,
    string fallbackModel,
    GeminiModelFallback state) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Materialise once: on the activate-and-retry path the sequence is enumerated twice.
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        if (state.IsActive(primaryModel))
            return WithModelId(
                await fallback.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false),
                fallbackModel);

        try
        {
            return WithModelId(
                await primary.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false),
                primaryModel);
        }
        catch (Exception ex) when (AiTransientErrors.IsDailyQuotaExhausted(ex))
        {
            state.Activate(primaryModel);
            return WithModelId(
                await fallback.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false),
                fallbackModel);
        }
    }

    /// <summary>Routing only, no catch-and-retry: nothing in this codebase streams today.</summary>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        (state.IsActive(primaryModel) ? fallback : primary)
            .GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        primary.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        primary.Dispose();
        fallback.Dispose();
    }

    private static ChatResponse WithModelId(ChatResponse response, string model)
    {
        response.ModelId ??= model;
        return response;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GeminiQuotaFallbackChatClientTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Checkpoint — do not commit**

Report the task done; the owner commits.

---

### Task 4: Gemini-only factory guard + Worker wiring

**Files:**
- Modify: `src/Newsroom.Infrastructure/Ai/GeminiChatClientFactory.cs`
- Modify: `src/Newsroom.Worker/Program.cs:75-99`
- Test: `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiChatClientFactoryTests.cs` (create)

**Interfaces:**
- Consumes: `GeminiQuotaFallbackChatClient` ctor (Task 3), `GeminiModelFallback` (Task 2), existing `GeminiChatClientFactory.Create(IConfiguration, string)`.
- Produces:
  - `public static bool ShouldUseFallback(IConfiguration configuration, string stage)`
  - `public static IChatClient CreateWithDailyQuotaFallback(IConfiguration configuration, string stage, GeminiModelFallback fallback)`

- [ ] **Step 1: Write the failing guard tests**

Create `src/tests/Newsroom.Infrastructure.Tests/Ai/GeminiChatClientFactoryTests.cs` (config-only: `ShouldUseFallback` never constructs a Google client, so no API key is needed):

```csharp
using Microsoft.Extensions.Configuration;

using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiChatClientFactoryTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> CurrentShape() => new()
    {
        ["Ai:Stages:Analyse:Model"] = "gemini-3.1-flash-lite",
        ["Ai:Stages:Cluster:Model"] = "gemini-2.5-flash",
        ["Ai:Stages:Draft:Model"] = "gemini-3.5-flash",
        ["Ai:Stages:SelfCheck:Model"] = "gemini-3.5-flash",
    };

    [Theory]
    [InlineData("Cluster")]
    [InlineData("Draft")]
    [InlineData("SelfCheck")]
    public void Wraps_gemini_stages_with_no_provider_key(string stage) =>
        // No Ai:Stages:{stage}:Provider keys exist in today's config: absent means Gemini
        // (ADR-0010), so the current production shape must get the fallback.
        Assert.True(GeminiChatClientFactory.ShouldUseFallback(Config(CurrentShape()), stage));

    [Fact]
    public void Never_wraps_a_non_gemini_stage()
    {
        var values = CurrentShape();
        values["Ai:Stages:Draft:Provider"] = "anthropic";
        Assert.False(GeminiChatClientFactory.ShouldUseFallback(Config(values), "Draft"));
        Assert.True(GeminiChatClientFactory.ShouldUseFallback(Config(values), "Cluster")); // others unaffected
    }

    [Fact]
    public void Never_wraps_when_the_analyse_fallback_target_is_not_gemini()
    {
        var values = CurrentShape();
        values["Ai:Stages:Analyse:Provider"] = "openai";
        Assert.False(GeminiChatClientFactory.ShouldUseFallback(Config(values), "Cluster"));
    }

    [Fact]
    public void Provider_match_is_case_insensitive()
    {
        var values = CurrentShape();
        values["Ai:Stages:Cluster:Provider"] = "Gemini";
        Assert.True(GeminiChatClientFactory.ShouldUseFallback(Config(values), "Cluster"));
    }

    [Fact]
    public void Never_wraps_a_stage_already_on_the_analyse_model()
    {
        var values = CurrentShape();
        values["Ai:Stages:Cluster:Model"] = "gemini-3.1-flash-lite";
        Assert.False(GeminiChatClientFactory.ShouldUseFallback(Config(values), "Cluster"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GeminiChatClientFactoryTests"`
Expected: build FAILS with `'GeminiChatClientFactory' does not contain a definition for 'ShouldUseFallback'`.

- [ ] **Step 3: Implement the factory methods**

In `src/Newsroom.Infrastructure/Ai/GeminiChatClientFactory.cs`, insert after the `Create` method:

```csharp
    /// <summary>The Cluster/Draft/SelfCheck client, wrapped with the daily-quota fallback to
    /// the Analyse stage's model when <see cref="ShouldUseFallback"/> allows it; otherwise the
    /// plain client — a non-Gemini stage can never have its model switched.</summary>
    public static IChatClient CreateWithDailyQuotaFallback(
        IConfiguration configuration, string stage, GeminiModelFallback fallback)
    {
        var primary = Create(configuration, stage);
        if (!ShouldUseFallback(configuration, stage))
            return primary;

        return new GeminiQuotaFallbackChatClient(
            primary,
            configuration.GetValue($"Ai:Stages:{stage}:Model", "gemini-2.5-flash")!,
            Create(configuration, "Analyse"),
            configuration.GetValue("Ai:Stages:Analyse:Model", "gemini-2.5-flash")!,
            fallback);
    }

    /// <summary>Gemini-only guard for the daily-quota fallback: both the stage and the Analyse
    /// fallback target must resolve to the Gemini provider (<c>Ai:Stages:{stage}:Provider</c>,
    /// absent = gemini per ADR-0010 — the key does not exist in config today), and the stage
    /// must not already run the Analyse model (wrapping a model with itself is pointless).</summary>
    public static bool ShouldUseFallback(IConfiguration configuration, string stage)
    {
        if (!IsGemini(configuration, stage) || !IsGemini(configuration, "Analyse"))
            return false;

        var primaryModel = configuration.GetValue($"Ai:Stages:{stage}:Model", "gemini-2.5-flash")!;
        var fallbackModel = configuration.GetValue("Ai:Stages:Analyse:Model", "gemini-2.5-flash")!;
        return !string.Equals(primaryModel, fallbackModel, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGemini(IConfiguration configuration, string stage) =>
        string.Equals(
            configuration.GetValue($"Ai:Stages:{stage}:Provider", "gemini"),
            "gemini", StringComparison.OrdinalIgnoreCase);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GeminiChatClientFactoryTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Wire the Worker**

In `src/Newsroom.Worker/Program.cs`, register the clock and the fallback registry. Replace:

```csharp
    builder.Services.AddSingleton(_ => AiRateLimiter.From(builder.Configuration));
    builder.Services.AddSingleton<IAiBudget, AiBudget>();
```

with:

```csharp
    builder.Services.AddSingleton(_ => AiRateLimiter.From(builder.Configuration));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<GeminiModelFallback>(); // daily-quota fallback state, per model
    builder.Services.AddSingleton<IAiBudget, AiBudget>();
```

Then switch Cluster/Draft/SelfCheck to the wrapped clients (Analyse at line ~79 stays on plain `Create` — it *is* the fallback model). Replace:

```csharp
    builder.Services.AddSingleton(provider => new Lazy<IClusteringAi>(() => new GeminiClusteringAi(
        GeminiChatClientFactory.Create(builder.Configuration, "Cluster"),
        GeminiClusteringOptions.From(builder.Configuration),
        provider.GetRequiredService<AiRateLimiter>(),
        provider.GetRequiredService<ILogger<GeminiClusteringAi>>())));
```

with:

```csharp
    builder.Services.AddSingleton(provider => new Lazy<IClusteringAi>(() => new GeminiClusteringAi(
        GeminiChatClientFactory.CreateWithDailyQuotaFallback(builder.Configuration, "Cluster",
            provider.GetRequiredService<GeminiModelFallback>()),
        GeminiClusteringOptions.From(builder.Configuration),
        provider.GetRequiredService<AiRateLimiter>(),
        provider.GetRequiredService<ILogger<GeminiClusteringAi>>())));
```

and replace:

```csharp
    builder.Services.AddSingleton(provider => new Lazy<IDraftingAi>(() => new GeminiDraftingAi(
        GeminiChatClientFactory.Create(builder.Configuration, "Draft"),
        GeminiChatClientFactory.Create(builder.Configuration, "SelfCheck"),
        GeminiDraftingOptions.From(builder.Configuration),
        provider.GetRequiredService<AiRateLimiter>())));
```

with:

```csharp
    builder.Services.AddSingleton(provider => new Lazy<IDraftingAi>(() => new GeminiDraftingAi(
        GeminiChatClientFactory.CreateWithDailyQuotaFallback(builder.Configuration, "Draft",
            provider.GetRequiredService<GeminiModelFallback>()),
        GeminiChatClientFactory.CreateWithDailyQuotaFallback(builder.Configuration, "SelfCheck",
            provider.GetRequiredService<GeminiModelFallback>()),
        GeminiDraftingOptions.From(builder.Configuration),
        provider.GetRequiredService<AiRateLimiter>())));
```

The `Lazy<>` missing-API-key degradation is untouched: `CreateWithDailyQuotaFallback` only runs inside the existing `Lazy` factories.

- [ ] **Step 6: Full build + full test suite**

Run: `dotnet build Newsroom.slnx`
Expected: Build succeeded, 0 errors.

Run: `dotnet test Newsroom.slnx`
Expected: all test projects PASS, no regressions.

- [ ] **Step 7: Checkpoint — do not commit**

Report the task done; the owner commits.

---

### Task 5: Documentation (docs are the project's source of truth)

**Files:**
- Modify: `docs/05-integrations/ai-generation.md:42-79`
- Modify: `docs/07-operations.md:35-40`

Use the Edit tool only (both files sit in a docs tree containing Cyrillic; PowerShell rewrites are banned — Global Constraints).

- [ ] **Step 1: Update the per-stage model table**

In `docs/05-integrations/ai-generation.md`, replace:

```markdown
### Models per stage (current config, 2026-07-10)
```

with:

```markdown
### Models per stage (current config, 2026-07-21)
```

and replace the stale Analyse row (config moved to `gemini-3.1-flash-lite` — see `src/Newsroom.Worker/appsettings.json`):

```markdown
| **Analyse** (summarise/classify) | `gemini-2.5-flash-lite` | 8 articles/request | Highest volume; classification tolerates a lighter model. Own daily bucket. |
```

with:

```markdown
| **Analyse** (summarise/classify) | `gemini-3.1-flash-lite` | 8 articles/request | Highest volume; classification tolerates a lighter model. Own daily bucket (500 RPD). Also the daily-quota fallback model for the other stages (below). |
```

- [ ] **Step 2: Add the fallback subsection**

In the same file, insert a new subsection immediately after the "Why one model per stage" paragraph (the one ending "…re-tune as the Gemini catalog and quotas move."):

```markdown
### Daily-quota fallback (2026-07-21)

When Cluster, Draft, or SelfCheck gets a **daily**-quota 429 from Gemini (a quota id naming
`…PerDay…` — per-minute 429s keep the normal retry-next-cycle path), the stage automatically
switches to the Analyse stage's model until the quota reset (midnight US-Pacific), then switches
back. Implemented as a delegating `IChatClient` (`GeminiQuotaFallbackChatClient` +
`GeminiModelFallback` state) so jobs and adapters are untouched; the triggering request is
retried once on the fallback model, and `nw_CostLedger.Model` records the model actually used.
State is per **model**, so Draft and SelfCheck (same model) flip together; it is in-memory, so a
worker restart merely re-probes the primary (worst case: one extra 429 re-activates).

**Gemini-only:** a stage whose `Ai:Stages:{stage}:Provider` is not `gemini` (absent = `gemini`,
ADR-0010) is never wrapped, and the Analyse fallback target must be Gemini too — the fallback can
never switch a Claude/OpenAI stage's model. Stage `DailyRequestBudget`s still apply, so worst
case on the Analyse model is 450 + 18 + 9 + 9 = 486 requests/day, inside its 500 RPD.
```

- [ ] **Step 3: Amend the quota-exhaustion bullet**

In the same file (Free-tier limitations list), replace:

```markdown
- On quota exhaustion a stage logs `AI temporarily unavailable … will retry later` and resumes
  after the reset; no work is lost (the item's attempt is not burned — see
  `AiTransientErrors.IsQuotaExhausted`).
```

with:

```markdown
- On quota exhaustion a stage logs `AI temporarily unavailable … will retry later` and resumes
  after the reset; no work is lost (the item's attempt is not burned — see
  `AiTransientErrors.IsQuotaExhausted`). On a **daily**-quota 429, Cluster/Draft/SelfCheck do
  not wait: they fall back to the Analyse model automatically ("Daily-quota fallback" above).
```

- [ ] **Step 4: Add the retry-taxonomy entry**

In `docs/07-operations.md`, replace:

```markdown
   - *Transient* (HTTP 5xx/429/timeouts): Polly retry ×3 exponential+jitter, then circuit breaker
     per host; item stays queued for next cycle.
```

with:

```markdown
   - *Transient* (HTTP 5xx/429/timeouts): Polly retry ×3 exponential+jitter, then circuit breaker
     per host; item stays queued for next cycle.
   - *Gemini daily-quota 429:* Cluster/Draft/SelfCheck switch to the Analyse stage's model until
     the quota reset (midnight US-Pacific), then switch back — automatic, in-memory, Gemini-only
     (docs/05-integrations/ai-generation.md § Daily-quota fallback; mitigates risk R-11).
```

- [ ] **Step 5: Verify nothing broke**

Run: `dotnet build Newsroom.slnx`
Expected: Build succeeded (docs-only task; sanity check).

- [ ] **Step 6: Checkpoint — do not commit**

Report the task done; the owner commits.
