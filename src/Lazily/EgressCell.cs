// EgressCell — the single-threaded flavor of the reactive egress family (spec tag: lzegress).
//
// The delivery algebra lives in the flavor-neutral EgressCore; this shell adds only the
// reactivity — four memoized Computeds (pending, inflight, acked_through, retry) minted on THIS
// context's graph. Transport I/O lives in exactly ONE Effect per attachment (AttachTransport); the
// core stays pure and is shared verbatim by the thread-safe and async shells.
//
// There are no observers. Each reader kind is a Computed gated by its own version Source, and an
// invalidation is a version bump inside one Batch — the idiom IngressCell, TopicCell and
// WorkQueueCell already use. A transition that changes a reader kind bumps exactly that kind's
// version, and one that changes nothing (a duplicate ack, a full window) bumps nothing.

namespace Lazily;

/// <summary>
/// The only transport seam egress owns. Durability and framing remain separate adapters around the
/// envelope.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public interface IEgressTransport<T>
{
    /// <summary>
    /// Hands one attempt to the transport. <c>true</c> means the transport ACCEPTED the attempt;
    /// domain delivery is proven later by an acknowledgement, never by this return.
    /// </summary>
    /// <param name="envelope">The attempt, carrying its idempotency identity.</param>
    bool Send(EgressEnvelope<T> envelope);
}

/// <summary>
/// A reactive egress: pending, in-flight, acknowledgement and retry state as Computeds over one
/// delivery authority, with transport I/O confined to one attached Effect.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class EgressCell<T>
{
    private readonly Context _ctx;
    private readonly EgressCore<T> _core;

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
    /// <param name="ctx">The owning reactive scope.</param>
    /// <param name="generation">The initial producer incarnation.</param>
    /// <param name="policy">The bounds in force.</param>
    /// <exception cref="EgressConfigException">The policy is invalid.</exception>
    public EgressCell(Context ctx, long generation, EgressPolicy policy)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _ctx = ctx;
        _core = new EgressCore<T>(generation, policy);

        _pendingVersion = ctx.Source(0);
        _inflightVersion = ctx.Source(0);
        _ackedVersion = ctx.Source(0);
        _retryVersion = ctx.Source(0);
        _pending = ctx.Computed(
            cx =>
            {
                cx.Get(_pendingVersion);
                return _core.Pending();
            },
            EgressEnvelopeListComparer<T>.Instance);
        _inflight = ctx.Computed(
            cx =>
            {
                cx.Get(_inflightVersion);
                return _core.Inflight();
            },
            EgressEnvelopeListComparer<T>.Instance);
        _ackedThrough = ctx.Computed(cx =>
        {
            cx.Get(_ackedVersion);
            return _core.AckedThrough;
        });
        _retry = ctx.Computed(cx =>
        {
            cx.Get(_retryVersion);
            return _core.Retry();
        });
    }

    /// <summary>The bounds in force.</summary>
    public EgressPolicy Policy => _core.Policy;

    /// <summary>The current producer incarnation.</summary>
    public long Generation => _core.Generation;

    /// <summary>The sequence the next enqueue will be assigned.</summary>
    public long NextSequence => _core.NextSequence;

    // --- ops ----------------------------------------------------------------

    /// <summary>Assigns the next sequence and places the value in the pending projection.</summary>
    /// <param name="payload">The value to deliver.</param>
    public long Enqueue(T payload)
    {
        var (change, sequence) = _core.Enqueue(payload);
        Apply(change);
        return sequence;
    }

    /// <summary>Moves one pending value into the unacknowledged window.</summary>
    /// <param name="generation">The attachment's captured generation.</param>
    public EgressClaimResult<T> Claim(long generation)
    {
        var (change, claim) = _core.Claim(generation);
        Apply(change);
        return claim;
    }

    /// <summary>Advances the cumulative acknowledgement watermark.</summary>
    /// <param name="generation">The attachment's captured generation.</param>
    /// <param name="through">The highest sequence the receiver has durably accepted.</param>
    public EgressAckResult Ack(long generation, long through)
    {
        var (change, ack) = _core.Ack(generation, through);
        Apply(change);
        return ack;
    }

    /// <summary>Records a failed send attempt.</summary>
    /// <param name="generation">The attachment's captured generation.</param>
    /// <param name="sequence">The sequence whose send failed.</param>
    public EgressFailureResult Fail(long generation, long sequence)
    {
        var (change, failure) = _core.Fail(generation, sequence);
        Apply(change);
        return failure;
    }

    /// <summary>Makes a parked failed record eligible again.</summary>
    /// <param name="generation">The attachment's captured generation.</param>
    /// <param name="sequence">The parked sequence.</param>
    public EgressRetryActionResult RetryNow(long generation, long sequence)
    {
        var (change, action) = _core.RetryNow(generation, sequence);
        Apply(change);
        return action;
    }

    /// <summary>Fences the old attachment and replays unacknowledged records under a new generation.</summary>
    /// <param name="generation">The new producer incarnation.</param>
    public EgressReconnectResult Reconnect(long generation)
    {
        var (change, reconnect) = _core.Reconnect(generation);
        Apply(change);
        return reconnect;
    }

    // --- reactive reads -----------------------------------------------------

    /// <summary>Reactive read: records awaiting a claim.</summary>
    public IReadOnlyList<EgressEnvelope<T>> Pending() => _pending.Get();

    /// <summary>Reactive read: the unacknowledged window.</summary>
    public IReadOnlyList<EgressEnvelope<T>> Inflight() => _inflight.Get();

    /// <summary>Reactive read: the cumulative acknowledgement watermark.</summary>
    public long? AckedThrough() => _ackedThrough.Get();

    /// <summary>Reactive read: the most recent retry decision.</summary>
    public EgressRetry? Retry() => _retry.Get();

    /// <summary>Handle to the pending reader (for composing derives and cache-validity probes).</summary>
    public Computed<IReadOnlyList<EgressEnvelope<T>>> PendingHandle => _pending;

    /// <summary>Handle to the in-flight reader.</summary>
    public Computed<IReadOnlyList<EgressEnvelope<T>>> InflightHandle => _inflight;

    /// <summary>Handle to the acknowledgement-watermark reader.</summary>
    public Computed<long?> AckedThroughHandle => _ackedThrough;

    /// <summary>Handle to the retry reader.</summary>
    public Computed<EgressRetry?> RetryHandle => _retry;

    /// <summary>Attaches ONE transport Effect at the current producer generation.</summary>
    /// <remarks>
    /// A reconnect does not mutate this Effect's captured generation, so the old attachment becomes
    /// inert at the core fence; callers attach one replacement Effect for the new incarnation. The
    /// Effect depends on the in-flight reader as well as the pending one: an acknowledgement
    /// reopens the window and must reactively re-run this same attachment, with no request/ack
    /// handshake.
    /// </remarks>
    /// <param name="transport">The send seam.</param>
    public Effect AttachTransport(IEgressTransport<T> transport)
    {
        Guard.NotNull(transport, nameof(transport));
        var generation = Generation;
        return _ctx.Effect(compute =>
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
        });
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
