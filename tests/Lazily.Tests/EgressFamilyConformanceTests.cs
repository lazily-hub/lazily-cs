using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The canonical reactive-egress corpus (<c>#lzegress</c>), replayed against EVERY flavor this
/// binding ships: <c>EgressCell</c>, <c>ThreadSafeEgressCell</c>, and <c>AsyncEgressCell</c>.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures own transition AND invalidation expectations, and the flavor axis lives in the
/// runner: the same JSON drives all three shells through <see cref="IEgressModel"/>. State is read
/// through each shell's REACTIVE readers, never the core, so a shell that forgot to bump a reader
/// serves a stale projection and fails the state comparison as well as the invalidation one.
/// </para>
/// <para>
/// <c>invalidates</c> is asserted per reader kind in BOTH directions through a cache-validity
/// probe (<c>before &amp;&amp; !after</c>), so over-invalidation is as visible as under-;
/// <see cref="TheInvalidationProbeDiscriminatesOnEveryFlavor"/> proves the probe itself can fail.
/// Every flavor asserts executed-steps == loaded-steps exactly (<see cref="CorpusSteps"/>), and an
/// unknown op fails rather than skips. The transport Effect has separate coverage in
/// <see cref="EgressCellTests"/>: the corpus describes the delivery authority beneath any concrete
/// transport.
/// </para>
/// </remarks>
public sealed class EgressFamilyConformanceTests
{
    private const string Corpus = "egress";

    /// <summary>
    /// The egress fixtures this runner owns, named explicitly. The rest of the egress directory
    /// belongs to <see cref="LatestDurableProjectionConformanceTests"/> and
    /// <see cref="EffectCasConformanceTests"/>; <see cref="TheEgressDirectoryIsFullyOwned"/> fails on
    /// any fixture nobody claims.
    /// </summary>
    internal static readonly string[] Fixtures =
    [
        "egress_generation_fence.json",
        "egress_inflight_window.json",
        "egress_ordered_ack.json",
        "egress_retry_budget.json",
    ];

    private static readonly (string Flavor, Func<long, EgressPolicy, IEgressModel> Build)[] Flavors =
    [
        ("single-threaded", static (g, p) => new SyncEgressModel(g, p)),
        ("thread-safe", static (g, p) => new ThreadSafeEgressModel(g, p)),
        ("async", static (g, p) => new AsyncEgressModel(g, p)),
    ];

    private static readonly string[] ReaderKinds = ["pending", "inflight", "acked_through", "retry"];

    /// <summary>Every fixture in the canonical egress directory has exactly one owning runner here.</summary>
    [Fact]
    public void TheEgressDirectoryIsFullyOwned()
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}");
        Assert.Equal(
            Fixtures
                .Append("effect_cas.json")
                .Append("latest_durable_projection.json")
                .Order(StringComparer.Ordinal),
            SpecCorpus.FixtureNames(Corpus));
        Assert.True(
            CorpusSteps.DeclaredAcross(Corpus, Fixtures) > 0,
            "the egress corpus resolved but declares no steps at all");
    }

    /// <summary>Replays the whole corpus against the single-threaded flavor.</summary>
    [Fact]
    public void SingleThreadedFlavorReplaysTheWholeCorpus() => AssertFlavorReplays("single-threaded");

    /// <summary>Replays the whole corpus against the thread-safe flavor.</summary>
    [Fact]
    public void ThreadSafeFlavorReplaysTheWholeCorpus() => AssertFlavorReplays("thread-safe");

    /// <summary>Replays the whole corpus against the async flavor.</summary>
    /// <remarks>
    /// One test per flavor rather than a loop: a loop short-circuits on the first failure, so a
    /// defect in a later flavor would be masked by an earlier one.
    /// </remarks>
    [Fact]
    public void AsyncFlavorReplaysTheWholeCorpus() => AssertFlavorReplays("async");

    /// <summary>
    /// The corpus asserts NEGATIVE invalidation, so the probe must be able to report both answers
    /// on every flavor — otherwise a probe hard-wired to "still valid" satisfies every
    /// <c>invalidates: false</c> expectation.
    /// </summary>
    [Fact]
    public void TheInvalidationProbeDiscriminatesOnEveryFlavor()
    {
        var problems = new List<string>();
        foreach (var (flavor, build) in Flavors)
        {
            using var model = build(1, EgressPolicy.Default);
            Materialize(model);
            if (!model.Validity().All(valid => valid))
                problems.Add($"{flavor}: reading every reader must warm every cache");

            model.Enqueue(1);
            var after = model.Validity();
            if (after[0]) problems.Add($"{flavor}: enqueue must invalidate the pending reader");
            if (!after[1] || !after[2] || !after[3])
                problems.Add($"{flavor}: enqueue must NOT invalidate inflight/acked_through/retry");
        }

        Assert.Empty(problems);
    }

    private static void AssertFlavorReplays(string flavor)
    {
        Assert.NotNull(SpecCorpus.Root);
        var build = Flavors.Single(pair => string.Equals(pair.Flavor, flavor, StringComparison.Ordinal)).Build;
        var loaded = 0;
        var executed = 0;
        var probes = 0;
        foreach (var fixture in Fixtures)
        {
            using var doc = SpecCorpus.Load(Corpus, fixture);
            var root = doc.RootElement;
            Assert.Equal("Egress", root.GetProperty("kind").GetString());
            Assert.Equal("EgressCore", root.GetProperty("model").GetString());
            loaded += CorpusSteps.Declared(root);
            var (steps, checks) = Replay(root, $"{flavor}/{fixture}", build);
            Assert.True(steps > 0, $"{flavor}/{fixture}: replayed zero steps");
            executed += steps;
            probes += checks;
        }

        Assert.True(executed > 0, $"{flavor}: replayed zero steps");
        CorpusSteps.AssertAllExecuted($"{Corpus} ({flavor})", loaded, executed);

        // Four reader kinds per step, both directions — a runner that stopped probing would still
        // report the right step count.
        Assert.True(
            probes == ReaderKinds.Length * executed,
            $"{flavor}: {probes} invalidation probes for {executed} steps");
    }

    private static (int Steps, int Checks) Replay(
        JsonElement root,
        string label,
        Func<long, EgressPolicy, IEgressModel> build)
    {
        var policy = root.GetProperty("policy");
        using var model = build(
            root.GetProperty("generation").GetInt64(),
            new EgressPolicy(
                policy.GetProperty("inflight_limit").GetInt32(),
                policy.GetProperty("retry_budget").GetInt32(),
                policy.GetProperty("retry_base").GetInt64(),
                policy.GetProperty("retry_ceiling").GetInt64()));
        Materialize(model);

        var steps = 0;
        var checks = 0;
        var stepList = root.GetProperty("steps").EnumerateArray().ToList();
        for (var index = 0; index < stepList.Count; index++)
        {
            var step = stepList[index];
            var op = step.GetProperty("op");
            var where = $"{label} step {index} ({op.GetProperty("type").GetString()})";

            var before = model.Validity();
            var returned = RunOp(model, op, where);
            var after = model.Validity();

            var invalidates = new JsonObject();
            for (var kind = 0; kind < ReaderKinds.Length; kind++)
            {
                invalidates[ReaderKinds[kind]] = before[kind] && !after[kind];
                checks++;
            }

            var actual = State(model);
            actual["invalidates"] = invalidates;
            FixtureAssertions.Deep(step.GetProperty("returns"), $"{where} returns", returned);
            FixtureAssertions.Deep(step.GetProperty("expected"), $"{where} expected", actual);
            Materialize(model);
            steps++;
        }

        return (steps, checks);
    }

    private static JsonObject RunOp(IEgressModel model, JsonElement op, string where)
    {
        long Field(string name) => op.GetProperty(name).GetInt64();
        return op.GetProperty("type").GetString() switch
        {
            "enqueue" => new JsonObject { ["sequence"] = model.Enqueue(Field("payload")) },
            "claim" => Claim(model.Claim(Field("generation"))),
            "ack" => Ack(model.Ack(Field("generation"), Field("through"))),
            "fail" => Failure(model.Fail(Field("generation"), Field("sequence"))),
            "retry_now" => RetryAction(model.RetryNow(Field("generation"), Field("sequence"))),
            "reconnect" => Reconnect(model.Reconnect(Field("generation"))),
            // Fail, never skip: a runner that shrugs at an op replays less than the corpus.
            var other => throw new Xunit.Sdk.XunitException($"{where}: unknown egress op `{other}`"),
        };
    }

    /// <summary>Reads every reader kind so the next step's probe measures only that step.</summary>
    private static void Materialize(IEgressModel model)
    {
        _ = model.Pending();
        _ = model.Inflight();
        _ = model.AckedThrough();
        _ = model.Retry();
    }

    private static JsonObject State(IEgressModel model) => new()
    {
        ["generation"] = model.Generation,
        ["next_sequence"] = model.NextSequence,
        ["pending"] = Envelopes(model.Pending()),
        ["inflight"] = Envelopes(model.Inflight()),
        ["acked_through"] = model.AckedThrough(),
        ["retry"] = model.Retry() is { } retry ? Retry(retry) : null,
    };

    private static JsonArray Envelopes(IReadOnlyList<EgressEnvelope<long>> envelopes) =>
        new([.. envelopes.Select(envelope => (JsonNode)Envelope(envelope))]);

    private static JsonObject Envelope(EgressEnvelope<long> envelope) => new()
    {
        ["generation"] = envelope.Generation,
        ["sequence"] = envelope.Sequence,
        ["attempt"] = envelope.Attempt,
        ["payload"] = envelope.Payload,
    };

    private static JsonObject Retry(EgressRetry retry) => new()
    {
        ["sequence"] = retry.Sequence,
        ["attempt"] = retry.Attempt,
        ["backoff"] = retry.Backoff,
        ["exhausted"] = retry.Exhausted,
    };

    private static JsonObject Claim(EgressClaimResult<long> claim) => claim.Kind switch
    {
        EgressClaimKind.Claimed => new() { ["claim"] = "claimed", ["envelope"] = Envelope(claim.Envelope!) },
        EgressClaimKind.Empty => new() { ["claim"] = "empty" },
        EgressClaimKind.WindowFull => new() { ["claim"] = "window_full" },
        EgressClaimKind.StaleGeneration => new() { ["claim"] = "stale_generation", ["current"] = claim.Current },
        _ => throw new ArgumentOutOfRangeException(nameof(claim)),
    };

    private static JsonObject Ack(EgressAckResult ack) => ack.Kind switch
    {
        EgressAckKind.Advanced => new() { ["ack"] = "advanced", ["through"] = ack.Through },
        EgressAckKind.Unchanged => new() { ["ack"] = "unchanged", ["through"] = ack.Through },
        EgressAckKind.StaleGeneration => new() { ["ack"] = "stale_generation", ["current"] = ack.Current },
        _ => throw new ArgumentOutOfRangeException(nameof(ack)),
    };

    private static JsonObject Failure(EgressFailureResult failure) => failure.Kind switch
    {
        EgressFailureKind.Retrying => new() { ["failure"] = "retrying", ["retry"] = Retry(failure.Retry!.Value) },
        EgressFailureKind.Exhausted => new() { ["failure"] = "exhausted", ["retry"] = Retry(failure.Retry!.Value) },
        EgressFailureKind.UnknownSequence => new() { ["failure"] = "unknown_sequence" },
        EgressFailureKind.StaleGeneration => new() { ["failure"] = "stale_generation", ["current"] = failure.Current },
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    private static JsonObject RetryAction(EgressRetryActionResult action) => action.Kind switch
    {
        EgressRetryActionKind.Scheduled => new() { ["retry_action"] = "scheduled", ["sequence"] = action.Sequence },
        EgressRetryActionKind.UnknownSequence => new() { ["retry_action"] = "unknown_sequence" },
        EgressRetryActionKind.StaleGeneration => new() { ["retry_action"] = "stale_generation", ["current"] = action.Current },
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static JsonObject Reconnect(EgressReconnectResult reconnect) => reconnect.Kind switch
    {
        EgressReconnectKind.Advanced => new()
        {
            ["reconnect"] = "advanced",
            ["generation"] = reconnect.Generation,
            ["replayed"] = reconnect.Replayed,
        },
        EgressReconnectKind.Unchanged => new() { ["reconnect"] = "unchanged", ["generation"] = reconnect.Generation },
        EgressReconnectKind.StaleGeneration => new() { ["reconnect"] = "stale_generation", ["current"] = reconnect.Generation },
        _ => throw new ArgumentOutOfRangeException(nameof(reconnect)),
    };
}

/// <summary>One egress flavor behind the op and reader surface the corpus drives.</summary>
/// <remarks>
/// <see cref="Validity"/> is the reason this is an interface: it probes each reader kind's cache
/// (pending, inflight, acked_through, retry) WITHOUT recomputing it, which is the only way to
/// assert that a transition did NOT invalidate a reader.
/// </remarks>
public interface IEgressModel : IDisposable
{
    /// <summary>The current producer incarnation.</summary>
    long Generation { get; }

    /// <summary>The sequence the next enqueue will be assigned.</summary>
    long NextSequence { get; }

    /// <summary>See <see cref="EgressCell{T}.Enqueue"/>.</summary>
    long Enqueue(long payload);

    /// <summary>See <see cref="EgressCell{T}.Claim"/>.</summary>
    EgressClaimResult<long> Claim(long generation);

    /// <summary>See <see cref="EgressCell{T}.Ack"/>.</summary>
    EgressAckResult Ack(long generation, long through);

    /// <summary>See <see cref="EgressCell{T}.Fail"/>.</summary>
    EgressFailureResult Fail(long generation, long sequence);

    /// <summary>See <see cref="EgressCell{T}.RetryNow"/>.</summary>
    EgressRetryActionResult RetryNow(long generation, long sequence);

    /// <summary>See <see cref="EgressCell{T}.Reconnect"/>.</summary>
    EgressReconnectResult Reconnect(long generation);

    /// <summary>Reactive read of the pending projection.</summary>
    IReadOnlyList<EgressEnvelope<long>> Pending();

    /// <summary>Reactive read of the unacknowledged window.</summary>
    IReadOnlyList<EgressEnvelope<long>> Inflight();

    /// <summary>Reactive read of the acknowledgement watermark.</summary>
    long? AckedThrough();

    /// <summary>Reactive read of the retry decision.</summary>
    EgressRetry? Retry();

    /// <summary>Cache validity of [pending, inflight, acked_through, retry], without recomputing.</summary>
    bool[] Validity();
}

/// <summary>The corpus replayed against <see cref="EgressCell{T}"/>.</summary>
public sealed class SyncEgressModel(long generation, EgressPolicy policy) : IEgressModel
{
    private readonly EgressCell<long> _cell = new(new Context(), generation, policy);

    /// <inheritdoc/>
    public long Generation => _cell.Generation;

    /// <inheritdoc/>
    public long NextSequence => _cell.NextSequence;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public long Enqueue(long payload) => _cell.Enqueue(payload);

    /// <inheritdoc/>
    public EgressClaimResult<long> Claim(long generation) => _cell.Claim(generation);

    /// <inheritdoc/>
    public EgressAckResult Ack(long generation, long through) => _cell.Ack(generation, through);

    /// <inheritdoc/>
    public EgressFailureResult Fail(long generation, long sequence) => _cell.Fail(generation, sequence);

    /// <inheritdoc/>
    public EgressRetryActionResult RetryNow(long generation, long sequence) => _cell.RetryNow(generation, sequence);

    /// <inheritdoc/>
    public EgressReconnectResult Reconnect(long generation) => _cell.Reconnect(generation);

    /// <inheritdoc/>
    public IReadOnlyList<EgressEnvelope<long>> Pending() => _cell.Pending();

    /// <inheritdoc/>
    public IReadOnlyList<EgressEnvelope<long>> Inflight() => _cell.Inflight();

    /// <inheritdoc/>
    public long? AckedThrough() => _cell.AckedThrough();

    /// <inheritdoc/>
    public EgressRetry? Retry() => _cell.Retry();

    /// <inheritdoc/>
    public bool[] Validity() =>
    [
        _cell.PendingHandle.Peek(out _),
        _cell.InflightHandle.Peek(out _),
        _cell.AckedThroughHandle.Peek(out _),
        _cell.RetryHandle.Peek(out _),
    ];
}

/// <summary>The corpus replayed against <see cref="ThreadSafeEgressCell{T}"/>.</summary>
public sealed class ThreadSafeEgressModel : IEgressModel
{
    private readonly ThreadSafeContext _ctx = new();
    private readonly ThreadSafeEgressCell<long> _cell;

    /// <summary>Builds the thread-safe flavor.</summary>
    /// <param name="generation">The initial producer incarnation.</param>
    /// <param name="policy">The bounds in force.</param>
    public ThreadSafeEgressModel(long generation, EgressPolicy policy) =>
        _cell = new ThreadSafeEgressCell<long>(_ctx, generation, policy);

    /// <inheritdoc/>
    public long Generation => _cell.Generation;

    /// <inheritdoc/>
    public long NextSequence => _cell.NextSequence;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public long Enqueue(long payload) => _cell.Enqueue(payload);

    /// <inheritdoc/>
    public EgressClaimResult<long> Claim(long generation) => _cell.Claim(generation);

    /// <inheritdoc/>
    public EgressAckResult Ack(long generation, long through) => _cell.Ack(generation, through);

    /// <inheritdoc/>
    public EgressFailureResult Fail(long generation, long sequence) => _cell.Fail(generation, sequence);

    /// <inheritdoc/>
    public EgressRetryActionResult RetryNow(long generation, long sequence) => _cell.RetryNow(generation, sequence);

    /// <inheritdoc/>
    public EgressReconnectResult Reconnect(long generation) => _cell.Reconnect(generation);

    /// <inheritdoc/>
    public IReadOnlyList<EgressEnvelope<long>> Pending() => _cell.Pending();

    /// <inheritdoc/>
    public IReadOnlyList<EgressEnvelope<long>> Inflight() => _cell.Inflight();

    /// <inheritdoc/>
    public long? AckedThrough() => _cell.AckedThrough();

    /// <inheritdoc/>
    public EgressRetry? Retry() => _cell.Retry();

    /// <inheritdoc/>
    public bool[] Validity() => _ctx.WithLock(_ =>
        new[]
        {
            _cell.PendingHandle.Peek(out var _),
            _cell.InflightHandle.Peek(out var _),
            _cell.AckedThroughHandle.Peek(out var _),
            _cell.RetryHandle.Peek(out var _),
        });
}

/// <summary>The corpus replayed against <see cref="AsyncEgressCell{T}"/>.</summary>
/// <remarks>
/// The plane where a reader left resolved after a transition serves its cached value forever: an
/// async slot read short-circuits on <c>Resolved</c>. The reads block exactly as
/// <see cref="AsyncIngressModel"/> does — the delivery algebra awaits nothing.
/// </remarks>
public sealed class AsyncEgressModel : IEgressModel
{
    private readonly AsyncContext _ctx = new();
    private readonly AsyncEgressCell<long> _cell;

    /// <summary>Builds the async flavor.</summary>
    /// <param name="generation">The initial producer incarnation.</param>
    /// <param name="policy">The bounds in force.</param>
    public AsyncEgressModel(long generation, EgressPolicy policy) =>
        _cell = new AsyncEgressCell<long>(_ctx, generation, policy);

    /// <inheritdoc/>
    public long Generation => _cell.Generation;

    /// <inheritdoc/>
    public long NextSequence => _cell.NextSequence;

    /// <inheritdoc/>
    public void Dispose()
    {
        _ctx.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public long Enqueue(long payload) => _cell.Enqueue(payload);

    /// <inheritdoc/>
    public EgressClaimResult<long> Claim(long generation) => _cell.Claim(generation);

    /// <inheritdoc/>
    public EgressAckResult Ack(long generation, long through) => _cell.Ack(generation, through);

    /// <inheritdoc/>
    public EgressFailureResult Fail(long generation, long sequence) => _cell.Fail(generation, sequence);

    /// <inheritdoc/>
    public EgressRetryActionResult RetryNow(long generation, long sequence) => _cell.RetryNow(generation, sequence);

    /// <inheritdoc/>
    public EgressReconnectResult Reconnect(long generation) => _cell.Reconnect(generation);

    /// <inheritdoc/>
    public IReadOnlyList<EgressEnvelope<long>> Pending() => Block(_cell.PendingAsync());

    /// <inheritdoc/>
    public IReadOnlyList<EgressEnvelope<long>> Inflight() => Block(_cell.InflightAsync());

    /// <inheritdoc/>
    public long? AckedThrough() => Block(_cell.AckedThroughAsync());

    /// <inheritdoc/>
    public EgressRetry? Retry() => Block(_cell.RetryAsync());

    /// <inheritdoc/>
    public bool[] Validity() =>
    [
        _cell.PendingHandle.TryGet(out _),
        _cell.InflightHandle.TryGet(out _),
        _cell.AckedThroughHandle.TryGet(out _),
        _cell.RetryHandle.TryGet(out _),
    ];

    private static T Block<T>(Task<T> task) => task.GetAwaiter().GetResult();
}
