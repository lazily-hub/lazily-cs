// AsyncEgressCell — the AsyncContext flavor of EgressCell (spec tag: lzegress).
//
// Same flavor-neutral EgressCore, same four reader kinds, same transitions. Only the graph and the
// transport attachment differ.
//
// DELIVERY AUTHORITY IS NOT ASYNC-COLOURED. Sequencing, the window, the watermark, the retry
// budget and the fence are functions of state the graph does not own and nothing awaits, so every
// op (Enqueue / Claim / Ack / Fail / RetryNow / Reconnect) is SYNCHRONOUS and every reader body
// resolves with Task.FromResult. The delivery-state readers are therefore synchronous Computeds in
// everything but the type of their READ: on this binding an AsyncComputed<T> read is Task-typed by
// construction (the same divergence AsyncIngressCell and the async queue family record in
// AGENTS.md). The ONLY async-coloured part is the transport attachment, whose send is awaited.
//
// Multi-root invalidation goes through AsyncContext.Batch, so one transition is one frontier walk
// over the whole dependent cone.

namespace Lazily;

/// <summary>The async transport seam: the only async-coloured part of the egress family.</summary>
/// <typeparam name="T">The payload type.</typeparam>
public interface IAsyncEgressTransport<T>
{
    /// <summary>
    /// Hands one attempt to the transport. <c>true</c> means the transport ACCEPTED the attempt;
    /// domain delivery is proven later by an acknowledgement, never by this return.
    /// </summary>
    /// <remarks>
    /// Deliberately carries no cancellation token: the attempt was already CLAIMED into the
    /// unacknowledged window, and a send abandoned half-way would strand it there until the next
    /// reconnect. A claimed attempt is always either sent or reported failed.
    /// </remarks>
    /// <param name="envelope">The attempt, carrying its idempotency identity.</param>
    ValueTask<bool> SendAsync(EgressEnvelope<T> envelope);
}

/// <summary>
/// A reactive egress on the async graph: pending, in-flight, acknowledgement and retry state as
/// synchronous Computeds over one delivery authority, with transport I/O confined to one
/// async-coloured attached Effect per incarnation.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class AsyncEgressCell<T>
{
    private readonly AsyncContext _ctx;
    private readonly EgressCore<T> _core;
    private readonly object _coreGate = new();

    private readonly AsyncSource<int> _pendingVersion;
    private readonly AsyncSource<int> _inflightVersion;
    private readonly AsyncSource<int> _ackedVersion;
    private readonly AsyncSource<int> _retryVersion;
    private int _pendingTick;
    private int _inflightTick;
    private int _ackedTick;
    private int _retryTick;

    private readonly AsyncComputed<IReadOnlyList<EgressEnvelope<T>>> _pending;
    private readonly AsyncComputed<IReadOnlyList<EgressEnvelope<T>>> _inflight;
    private readonly AsyncComputed<long?> _ackedThrough;
    private readonly AsyncComputed<EgressRetry?> _retry;

    /// <summary>Builds an egress at <paramref name="generation"/> under <paramref name="policy"/>.</summary>
    /// <param name="ctx">The owning async reactive scope.</param>
    /// <param name="generation">The initial producer incarnation.</param>
    /// <param name="policy">The bounds in force.</param>
    /// <exception cref="EgressConfigException">The policy is invalid.</exception>
    public AsyncEgressCell(AsyncContext ctx, long generation, EgressPolicy policy)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _ctx = ctx;
        _core = new EgressCore<T>(generation, policy);

        var pendingVersion = _pendingVersion = ctx.Source(0);
        var inflightVersion = _inflightVersion = ctx.Source(0);
        var ackedVersion = _ackedVersion = ctx.Source(0);
        var retryVersion = _retryVersion = ctx.Source(0);
        _pending = ctx.Computed(
            compute =>
            {
                compute.Track(pendingVersion);
                lock (_coreGate) return Task.FromResult(_core.Pending());
            },
            EgressEnvelopeListComparer<T>.Instance);
        _inflight = ctx.Computed(
            compute =>
            {
                compute.Track(inflightVersion);
                lock (_coreGate) return Task.FromResult(_core.Inflight());
            },
            EgressEnvelopeListComparer<T>.Instance);
        _ackedThrough = ctx.Computed(compute =>
        {
            compute.Track(ackedVersion);
            lock (_coreGate) return Task.FromResult(_core.AckedThrough);
        });
        _retry = ctx.Computed(compute =>
        {
            compute.Track(retryVersion);
            lock (_coreGate) return Task.FromResult(_core.Retry());
        });
    }

    /// <summary>The bounds in force.</summary>
    public EgressPolicy Policy => _core.Policy;

    /// <summary>The current producer incarnation.</summary>
    public long Generation
    {
        get
        {
            lock (_coreGate) return _core.Generation;
        }
    }

    /// <summary>The sequence the next enqueue will be assigned.</summary>
    public long NextSequence
    {
        get
        {
            lock (_coreGate) return _core.NextSequence;
        }
    }

    // --- ops (synchronous: delivery authority awaits nothing) ---------------

    /// <summary>Assigns the next sequence and places the value in the pending projection.</summary>
    /// <param name="payload">The value to deliver.</param>
    public long Enqueue(T payload)
    {
        EgressChange change;
        long sequence;
        lock (_coreGate) (change, sequence) = _core.Enqueue(payload);
        Apply(change);
        return sequence;
    }

    /// <summary>Moves one pending value into the unacknowledged window.</summary>
    /// <param name="generation">The attachment's captured generation.</param>
    public EgressClaimResult<T> Claim(long generation)
    {
        EgressChange change;
        EgressClaimResult<T> claim;
        lock (_coreGate) (change, claim) = _core.Claim(generation);
        Apply(change);
        return claim;
    }

    /// <summary>Advances the cumulative acknowledgement watermark.</summary>
    /// <param name="generation">The attachment's captured generation.</param>
    /// <param name="through">The highest sequence the receiver has durably accepted.</param>
    public EgressAckResult Ack(long generation, long through)
    {
        EgressChange change;
        EgressAckResult ack;
        lock (_coreGate) (change, ack) = _core.Ack(generation, through);
        Apply(change);
        return ack;
    }

    /// <summary>Records a failed send attempt.</summary>
    /// <param name="generation">The attachment's captured generation.</param>
    /// <param name="sequence">The sequence whose send failed.</param>
    public EgressFailureResult Fail(long generation, long sequence)
    {
        EgressChange change;
        EgressFailureResult failure;
        lock (_coreGate) (change, failure) = _core.Fail(generation, sequence);
        Apply(change);
        return failure;
    }

    /// <summary>Makes a parked failed record eligible again.</summary>
    /// <param name="generation">The attachment's captured generation.</param>
    /// <param name="sequence">The parked sequence.</param>
    public EgressRetryActionResult RetryNow(long generation, long sequence)
    {
        EgressChange change;
        EgressRetryActionResult action;
        lock (_coreGate) (change, action) = _core.RetryNow(generation, sequence);
        Apply(change);
        return action;
    }

    /// <summary>Fences the old attachment and replays unacknowledged records under a new generation.</summary>
    /// <param name="generation">The new producer incarnation.</param>
    public EgressReconnectResult Reconnect(long generation)
    {
        EgressChange change;
        EgressReconnectResult reconnect;
        lock (_coreGate) (change, reconnect) = _core.Reconnect(generation);
        Apply(change);
        return reconnect;
    }

    // --- reactive reads (Task-typed only because AsyncComputed reads are) ----

    /// <summary>Reactive read: records awaiting a claim.</summary>
    public Task<IReadOnlyList<EgressEnvelope<T>>> PendingAsync() => _pending.GetAsync();

    /// <summary>Reactive read: the unacknowledged window.</summary>
    public Task<IReadOnlyList<EgressEnvelope<T>>> InflightAsync() => _inflight.GetAsync();

    /// <summary>Reactive read: the cumulative acknowledgement watermark.</summary>
    public Task<long?> AckedThroughAsync() => _ackedThrough.GetAsync();

    /// <summary>Reactive read: the most recent retry decision.</summary>
    public Task<EgressRetry?> RetryAsync() => _retry.GetAsync();

    /// <summary>Handle to the pending reader.</summary>
    public AsyncComputed<IReadOnlyList<EgressEnvelope<T>>> PendingHandle => _pending;

    /// <summary>Handle to the in-flight reader.</summary>
    public AsyncComputed<IReadOnlyList<EgressEnvelope<T>>> InflightHandle => _inflight;

    /// <summary>Handle to the acknowledgement-watermark reader.</summary>
    public AsyncComputed<long?> AckedThroughHandle => _ackedThrough;

    /// <summary>Handle to the retry reader.</summary>
    public AsyncComputed<EgressRetry?> RetryHandle => _retry;

    /// <summary>Attaches ONE async-coloured transport Effect at the current producer generation.</summary>
    /// <remarks>
    /// Like every AsyncContext effect, the first run is SCHEDULED rather than inline. A reconnect
    /// does not mutate the captured generation, so the old attachment becomes inert at the core
    /// fence; callers attach one replacement Effect for the new incarnation.
    /// </remarks>
    /// <param name="transport">The async send seam.</param>
    public AsyncEffectHandle AttachTransport(IAsyncEgressTransport<T> transport)
    {
        Guard.NotNull(transport, nameof(transport));
        var generation = Generation;
        return _ctx.Effect(async compute =>
        {
            await compute.TrackAsync(_inflight).ConfigureAwait(false);
            var pending = await compute.TrackAsync(_pending).ConfigureAwait(false);
            if (pending.Count == 0) return null;
            while (true)
            {
                var claim = Claim(generation);
                if (claim.Envelope is not { } envelope) break;
                if (!await transport.SendAsync(envelope).ConfigureAwait(false))
                {
                    Fail(generation, envelope.Sequence);
                    break;
                }
            }
            return null;
        });
    }

    private void Apply(EgressChange change)
    {
        if (change.IsEmpty) return;
        _ctx.Batch(() =>
        {
            if (change.Pending) _pendingVersion.Set(Interlocked.Increment(ref _pendingTick));
            if (change.Inflight) _inflightVersion.Set(Interlocked.Increment(ref _inflightTick));
            if (change.AckedThrough) _ackedVersion.Set(Interlocked.Increment(ref _ackedTick));
            if (change.Retry) _retryVersion.Set(Interlocked.Increment(ref _retryTick));
        });
    }
}
