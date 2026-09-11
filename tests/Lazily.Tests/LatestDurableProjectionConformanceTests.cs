using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lazily.Tests;

public sealed class LatestDurableProjectionConformanceTests
{
    private const string Corpus = "egress";
    private const string Fixture = "latest_durable_projection.json";

    [Fact]
    public void CoreReplaysCanonicalLatestDurableProjectionTrace()
    {
        using var document = SpecCorpus.Load(Corpus, Fixture);
        var root = document.RootElement;
        Assert.Equal("LatestDurableProjection", root.GetProperty("kind").GetString());
        Assert.Equal("LatestDurableProjectionCore", root.GetProperty("model").GetString());

        var loaded = 0;
        var steps = 0;
        foreach (var scenario in SpecCorpus.Scenarios(root, Corpus, Fixture).All())
        {
            loaded += CorpusSteps.Declared(scenario);
            var core = new LatestDurableProjectionCore<string, string>(
                scenario.GetProperty("generation").GetInt64());
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                var operation = step.GetProperty("op");
                var where = $"{Corpus}/{Fixture} step {steps}";
                FixtureAssertions.Deep(
                    step.GetProperty("returns"),
                    $"{where} returns",
                    Apply(core, operation));
                FixtureAssertions.Deep(
                    step.GetProperty("expected"),
                    $"{where} expected",
                    State(core));
                steps++;
            }
        }

        // No step-count constant: see CorpusSteps (#lzcorpusfloorguard). A `>= N` floor here
        // swallowed new corpus rows unexecuted in eight sibling bindings; executed-vs-loaded
        // is exact and cannot drift. Shrinks are caught in lazily-spec's corpus-counts.json.
        CorpusSteps.AssertAllExecuted($"{Corpus}/{Fixture}", loaded, steps);
    }

    [Fact]
    public async Task EveryReactiveFlavorProjectsCoreState()
    {
        var context = new Context();
        var single = new LatestDurableProjection<string, string>(context, 1);
        var singleEntry = single.Entry("doc");
        Assert.Null(context.Get(singleEntry).Desired);
        Assert.Equal(LatestDurableUpsertKind.Accepted, single.UpsertDesired("doc", 1, "A").Kind);
        Assert.Equal(1, context.Get(singleEntry).Desired?.Epoch);

        var threadContext = new ThreadSafeContext();
        var threadSafe = new ThreadSafeLatestDurableProjection<string, string>(threadContext, 1);
        var threadEntry = threadSafe.Entry("doc");
        Assert.Equal(LatestDurableUpsertKind.Accepted, threadSafe.UpsertDesired("doc", 1, "A").Kind);
        Assert.Equal(1, threadContext.WithLock(inner => inner.Get(threadEntry)).Desired?.Epoch);

        await using var asyncContext = new AsyncContext();
        var asynchronous = new AsyncLatestDurableProjection<string, string>(asyncContext, 1);
        var asyncEntry = asynchronous.Entry("doc");
        Assert.Equal(LatestDurableUpsertKind.Accepted, asynchronous.UpsertDesired("doc", 1, "A").Kind);
        Assert.Equal(1, (await asyncEntry.GetAsync()).Desired?.Epoch);
    }

    private static JsonObject Apply(
        LatestDurableProjectionCore<string, string> core,
        JsonElement operation)
    {
        var type = operation.GetProperty("type").GetString();
        var key = operation.TryGetProperty("key", out var keyNode) ? keyNode.GetString()! : string.Empty;
        var epoch = operation.TryGetProperty("epoch", out var epochNode) ? epochNode.GetInt64() : 0;
        var generation = operation.TryGetProperty("generation", out var generationNode)
            ? generationNode.GetInt64()
            : 0;

        return type switch
        {
            "upsert_desired" => Upsert(core.UpsertDesired(
                key,
                epoch,
                operation.GetProperty("value").GetString()!)),
            "claim" => Claim(core.Claim(key, generation)),
            "ack_applied" => Ack(core.AckApplied(key, generation, epoch)),
            "fail_retryable" => Failure(core.FailRetryable(key, generation, epoch)),
            "reconnect" => Reconnect(core.Reconnect(generation)),
            _ => throw new InvalidOperationException($"unknown operation {type}"),
        };
    }

    private static JsonObject Upsert(LatestDurableUpsertResult result)
    {
        var output = new JsonObject { ["upsert"] = Name(result.Kind) };
        if (result.Kind == LatestDurableUpsertKind.AlreadyDurable)
            output["durable_through"] = result.Current;
        else if (result.Kind == LatestDurableUpsertKind.StaleEpoch)
            output["current"] = result.Current;
        return output;
    }

    private static JsonObject Claim(LatestDurableClaimResult<string, string> result)
    {
        var output = new JsonObject { ["claim"] = Name(result.Kind) };
        if (result.Envelope is { } envelope) output["envelope"] = Envelope(envelope);
        if (result.Kind == LatestDurableClaimKind.StaleGeneration)
            output["current"] = result.Current;
        return output;
    }

    private static JsonObject Ack(LatestDurableAckResult result)
    {
        var output = new JsonObject { ["ack"] = Name(result.Kind) };
        if (result.DurableThrough is not null) output["durable_through"] = result.DurableThrough;
        if (result.Kind == LatestDurableAckKind.StaleGeneration)
            output["current"] = result.Current;
        return output;
    }

    private static JsonObject Failure(LatestDurableFailureResult result)
    {
        var output = new JsonObject { ["failure"] = Name(result.Kind) };
        if (result.Kind == LatestDurableFailureKind.StaleGeneration)
            output["current"] = result.Current;
        return output;
    }

    private static JsonObject Reconnect(LatestDurableReconnectResult result)
    {
        var output = new JsonObject { ["reconnect"] = Name(result.Kind) };
        if (result.Kind == LatestDurableReconnectKind.Advanced)
        {
            output["generation"] = result.Generation;
            output["requeued"] = result.Requeued;
            output["superseded"] = result.Superseded;
        }
        else if (result.Kind == LatestDurableReconnectKind.StaleGeneration)
            output["current"] = result.Generation;
        return output;
    }

    private static JsonObject State(LatestDurableProjectionCore<string, string> core) => new()
    {
        ["generation"] = core.Generation,
        ["entries"] = new JsonArray([.. core.Snapshots().Select(Entry)]),
    };

    private static JsonObject Entry(LatestDurableEntry<string, string> entry) => new()
    {
        ["key"] = entry.Key,
        ["desired"] = entry.Desired is null
            ? null
            : new JsonObject { ["epoch"] = entry.Desired.Epoch, ["value"] = entry.Desired.Value },
        ["inflight"] = entry.Inflight is null ? null : Envelope(entry.Inflight),
        ["durable_through"] = entry.DurableThrough,
    };

    private static JsonObject Envelope(LatestDurableEnvelope<string, string> envelope) => new()
    {
        ["generation"] = envelope.Generation,
        ["key"] = envelope.Key,
        ["epoch"] = envelope.Epoch,
        ["value"] = envelope.Value,
    };

    private static string Name<T>(T value) where T : Enum =>
        string.Concat(value.ToString().Select((ch, index) =>
            char.IsUpper(ch) && index > 0 ? $"_{char.ToLowerInvariant(ch)}" : char.ToLowerInvariant(ch).ToString()));
}
