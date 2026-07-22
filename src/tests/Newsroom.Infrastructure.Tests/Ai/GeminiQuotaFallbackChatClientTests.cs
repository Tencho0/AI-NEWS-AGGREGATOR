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
