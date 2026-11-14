// ThreadSafeEgressCell — the Send+Sync-equivalent flavor of EgressCell (spec tag: lzegress).
//
// Same flavor-neutral EgressCore, same four reader kinds. Delivery authority stays in the shared
// core behind its own gate; this shell adds only the reactivity, minted on THIS context's graph.
//
// LOCK DISCIPLINE (mirrors ThreadSafeIngressCell). Two locks, always in this order:
//
//   1. the context — taken by ThreadSafeContext.WithLock / Batch.
//   2. _coreGate   — the delivery algebra.
//
// A reader's compute body runs INSIDE the context lock and takes _coreGate (context -> core). An
// op therefore never holds _coreGate while reaching the context: every op scopes its core lock to
// a block that ends before Apply is called. The attached transport Effect runs inside the context
// lock and calls ops, which take the core gate and release it before re-entering the (reentrant)
// context lock for the batch — still context -> core, never the reverse.
//
// Multi-root invalidation goes through Batch(), so a concurrent reader can never observe the new
// window with the old pending projection.

namespace Lazily;

/// <summary>
/// A lock-serialized reactive egress: pending, in-flight, acknowledgement and retry state as
/// Computeds over one shared delivery authority, with transport I/O confined to one attached
/// Effect per incarnation.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class ThreadSafeEgressCell<T>
{
    private readonly ThreadSafeContext _ctx;
    private readonly EgressCore<T> _core;
    private readonly object _coreGate = new();

    private readonly Source<int> _pendingVersion;
    private readonly Source<int> _inflightVersion;
    private readonly Source<int> _ackedVersion;
    private readonly Source<int> _retryVersion;
    private int _pendingTick;
    private int _inflightTick;
    private int _ackedTick;
    private int _retryTick;

    private readonly Computed<IReadOnlyList<EgressEnvelope<T>>> _pending;
    private readonly Computed<IReadOnlyList<EgressEnvelope<T>>> _inflight;
    private readonly Computed<long?> _ackedThrough;
    private readonly Computed<EgressRetry?> _retry;

    /// <summary>Builds an egress at <paramref name="generation"/> under <paramref name="policy"/>.</summary>
    /// <param name="ctx">The owning lock-serialized reactive scope.</param>
    /// <param name="generation">The initial producer incarnation.</param>
    /// <param name="policy">The bounds in force.</param>
    /// <exception cref="EgressConfigException">The policy is invalid.</exception>
    public ThreadSafeEgressCell(ThreadSafeContext ctx, long generation, EgressPolicy policy)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _ctx = ctx;
        _core = new EgressCore<T>(generation, policy);

        Source<int>? pendingVersion = null;
        Source<int>? inflightVersion = null;
        Source<int>? ackedVersion = null;
        Source<int>? retryVersion = null;
        Computed<IReadOnlyList<EgressEnvelope<T>>>? pending = null;
        Computed<IReadOnlyList<EgressEnvelope<T>>>? inflight = null;
        Computed<long?>? ackedThrough = null;
        Computed<EgressRetry?>? retry = null;
        ctx.WithLock(inner =>
        {
            var pv = pendingVersion = inner.Source(0);
            var iv = inflightVersion = inner.Source(0);
            var av = ackedVersion = inner.Source(0);
            var rv = retryVersion = inner.Source(0);
            pending = inner.Computed(
                cx =>
                {
                    cx.Get(pv);
                    lock (_coreGate) return _core.Pending();
                },
                EgressEnvelopeListComparer<T>.Instance);
            inflight = inner.Computed(
                cx =>
                {
                    cx.Get(iv);
                    lock (_coreGate) return _core.Inflight();
                },
                EgressEnvelopeListComparer<T>.Instance);
            ackedThrough = inner.Computed(cx =>
            {
                cx.Get(av);
                lock (_coreGate) return _core.AckedThrough;
            });
            retry = inner.Computed(cx =>
            {
                cx.Get(rv);
                lock (_coreGate) return _core.Retry();
            });
        });

        _pendingVersion = pendingVersion!;
        _inflightVersion = inflightVersion!;
        _ackedVersion = ackedVersion!;
        _retryVersion = retryVersion!;
        _pending = pending!;
        _inflight = inflight!;
        _ackedThrough = ackedThrough!;
        _retry = retry!;
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

    // --- ops ----------------------------------------------------------------

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

    // --- reactive reads -----------------------------------------------------

    /// <summary>Reactive read: records awaiting a claim.</summary>
    public IReadOnlyList<EgressEnvelope<T>> Pending() => _ctx.WithLock(inner => _pending.Get(inner));

    /// <summary>Reactive read: the unacknowledged window.</summary>
    public IReadOnlyList<EgressEnvelope<T>> Inflight() => _ctx.WithLock(inner => _inflight.Get(inner));

    /// <summary>Reactive read: the cumulative acknowledgement watermark.</summary>
    public long? AckedThrough() => _ctx.WithLock(inner => _ackedThrough.Get(inner));

    /// <summary>Reactive read: the most recent retry decision.</summary>
    public EgressRetry? Retry() => _ctx.WithLock(inner => _retry.Get(inner));

    /// <summary>Handle to the pending reader. Read it only under the context lock.</summary>
    public Computed<IReadOnlyList<EgressEnvelope<T>>> PendingHandle => _pending;

    /// <summary>Handle to the in-flight reader. Read it only under the context lock.</summary>
    public Computed<IReadOnlyList<EgressEnvelope<T>>> InflightHandle => _inflight;

    /// <summary>Handle to the acknowledgement-watermark reader. Read it only under the context lock.</summary>
    public Computed<long?> AckedThroughHandle => _ackedThrough;

    /// <summary>Handle to the retry reader. Read it only under the context lock.</summary>
    public Computed<EgressRetry?> RetryHandle => _retry;

    /// <summary>Attaches ONE transport Effect at the current producer generation.</summary>
    /// <remarks>
    /// The transport is called while the context lock is held, so concurrent attachments and ops
    /// serialize through the graph; the delivery decision itself is always the shared core's.
    /// </remarks>
    /// <param name="transport">The send seam. It must tolerate being called from any thread.</param>
    public Effect AttachTransport(IEgressTransport<T> transport)
    {
        Guard.NotNull(transport, nameof(transport));
        var generation = Generation;
        return _ctx.WithLock(inner => inner.Effect(compute =>
        {
            _inflight.Get(compute);
            if (_pending.Get(compute).Count == 0) return null;
            while (true)
            {
                var claim = Claim(generation);
                if (claim.Envelope is not { } envelope) break;
                if (!transport.Send(envelope))
                {
                    Fail(generation, envelope.Sequence);
                    break;
                }
            }
            return null;
        }));
    }

    private void Apply(EgressChange change)
    {
        if (change.IsEmpty) return;
        _ctx.Batch(() =>
        {
            if (change.Pending) _pendingVersion.Set(++_pendingTick);
            if (change.Inflight) _inflightVersion.Set(++_inflightTick);
            if (change.AckedThrough) _ackedVersion.Set(++_ackedTick);
            if (change.Retry) _retryVersion.Set(++_retryTick);
        });
    }
}
