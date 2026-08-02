// The transport-agnostic reactive ingress admission algebra (spec tag: designimplementtransport).
//
// The same core/shell split KeyedOrder makes for the map family and TopicCell/WorkQueueCell make
// for the queue family, and for the same reason: deciding whether an inbound envelope is
// ADMISSIBLE touches no reactive node and awaits nothing, so the single-threaded, thread-safe, and
// async shells share this file verbatim — while reactivity deliberately stays OUT.
//
// Invalidation is a graph write, so the core performs none. Every mutator returns an
// IngressChange: WHICH reader kinds the transition dirtied. That return value is the whole
// contract between the core and a shell, and it is a pure function of the transition.
//
// Transport-agnostic by construction: an envelope is a VALUE carrying its own provenance
// (generation, sequence, stamped_at), so a WebSocket frame, an RPC response, and a polled page are
// the same input once decoded. IngressTransportKind exists only to derive a SCHEDULE.

namespace Lazily;

/// <summary>How envelopes reach a scope.</summary>
/// <remarks>
/// Event delivery is the default and needs no schedule; the other two exist so a caller without an
/// event channel still has a BOUNDED fallback rather than an unbounded refresh loop.
/// </remarks>
public enum IngressTransportKind
{
    /// <summary>Server-initiated delivery (WebSocket, SSE, in-proc channel). Preferred.</summary>
    EventChannel,

    /// <summary>Client-initiated, but triggered by an out-of-band event rather than a timer.</summary>
    RpcTriggered,

    /// <summary>Client-initiated on a bounded interval. The fallback of last resort.</summary>
    BoundedPolling,
}

/// <summary>When, if ever, a scope should ask the transport for more data.</summary>
/// <remarks>
/// <see cref="PollInterval"/> is non-null only for <see cref="IngressTransportKind.BoundedPolling"/>,
/// which makes "we polled a transport that pushes" unrepresentable rather than merely discouraged.
/// </remarks>
/// <param name="Kind">The transport this schedule was derived from.</param>
/// <param name="PollInterval">Bounded poll period, or null when delivery is event-driven.</param>
public readonly record struct IngressSchedule(IngressTransportKind Kind, long? PollInterval)
{
    /// <summary>
    /// Derives the schedule for <paramref name="kind"/>. A poll interval is offered only where
    /// event delivery is unavailable, and never zero.
    /// </summary>
    /// <param name="kind">The transport kind.</param>
    /// <param name="pollInterval">The requested poll period.</param>
    public static IngressSchedule ForKind(IngressTransportKind kind, long pollInterval) =>
        new(
            kind,
            kind == IngressTransportKind.BoundedPolling ? Math.Max(1, pollInterval) : null);
}

/// <summary>One decoded inbound message, with the provenance admission needs.</summary>
/// <remarks>
/// <paramref name="Generation"/> fences a producer incarnation (a reconnect, a redeploy, a build
/// skew); <paramref name="Sequence"/> orders within a generation; <paramref name="StampedAt"/> is
/// the producer's logical time, which is what freshness is measured against — never arrival time.
/// </remarks>
/// <typeparam name="TKey">The lifecycle-scoped identity type.</typeparam>
/// <typeparam name="TValue">The decoded payload type.</typeparam>
/// <param name="Key">Lifecycle-scoped identity this envelope belongs to.</param>
/// <param name="Generation">Producer incarnation. A higher value fences lower ones.</param>
/// <param name="Sequence">Position within <paramref name="Generation"/>, from 0.</param>
/// <param name="StampedAt">Producer logical timestamp.</param>
/// <param name="Payload">The decoded payload.</param>
public sealed record IngressEnvelope<TKey, TValue>(
    TKey Key,
    long Generation,
    long Sequence,
    long StampedAt,
    TValue Payload);

/// <summary>Why an envelope was refused.</summary>
/// <remarks>
/// Every member is a DECISION, not a failure — dropping a superseded envelope is correct behaviour
/// and is receipted as such.
/// </remarks>
public enum IngressDropReason
{
    /// <summary>The generation is below the scope's fence: a zombie producer.</summary>
    StaleGeneration,

    /// <summary>The sequence was already delivered in this generation.</summary>
    DuplicateSequence,

    /// <summary>The sequence is already sitting in the reorder buffer.</summary>
    DuplicateBuffered,

    /// <summary>The reorder buffer is full and this envelope does not fill the gap.</summary>
    ReorderWindowOverflow,

    /// <summary>The producer stamp is older than the freshness horizon.</summary>
    Expired,

    /// <summary>The hot window is at the high-water mark under a bounding overflow policy.</summary>
    Backpressure,

    /// <summary>The scope is closed; it admits nothing until reopened.</summary>
    ScopeClosed,
}

/// <summary>A transport- or decode-level failure attributed to a scope.</summary>
/// <remarks>Distinct from a drop: an error means we could not DECIDE, so it drives retry.</remarks>
public enum IngressError
{
    /// <summary>The transport closed or reset under us.</summary>
    TransportClosed,

    /// <summary>The frame could not be decoded into an envelope.</summary>
    DecodeFailed,

    /// <summary>The producer reported that our generation is no longer authoritative.</summary>
    AuthorityLost,
}

/// <summary>Which shape an <see cref="IngressAdmission"/> carries.</summary>
public enum IngressAdmissionKind
{
    /// <summary>Delivered in order, and the window holds exactly this one op.</summary>
    Accepted,

    /// <summary>Delivered in order and coalesced with at least one other op.</summary>
    Conflated,

    /// <summary>Held pending an earlier sequence. Nothing is visible yet.</summary>
    Buffered,

    /// <summary>A newer producer incarnation took over: sequence expectations reset.</summary>
    GenerationHandoff,

    /// <summary>Refused, with the reason receipted.</summary>
    Dropped,

    /// <summary>Refused by <see cref="RelayOverflow.Block"/>; the producer must retry.</summary>
    Blocked,
}

/// <summary>The outcome of admitting one envelope.</summary>
/// <remarks>
/// C# has no discriminated union, so the variants share one value type: <see cref="Kind"/> selects
/// which of the remaining members carry meaning, and the static factories are the only intended
/// constructors. Value equality makes a fixture's expected outcome directly comparable.
/// </remarks>
/// <param name="Kind">Which variant this is.</param>
/// <param name="DeliveredThrough">Watermark after delivery (Accepted/Conflated).</param>
/// <param name="GapFrom">The first sequence still missing (Buffered).</param>
/// <param name="FromGeneration">The fence we held (GenerationHandoff).</param>
/// <param name="ToGeneration">The fence we now hold (GenerationHandoff).</param>
/// <param name="Reason">Why the envelope was refused (Dropped).</param>
public readonly record struct IngressAdmission(
    IngressAdmissionKind Kind,
    long DeliveredThrough,
    long GapFrom,
    long FromGeneration,
    long ToGeneration,
    IngressDropReason? Reason)
{
    /// <summary>An in-order delivery into an empty window.</summary>
    /// <param name="deliveredThrough">The resulting watermark.</param>
    public static IngressAdmission Accepted(long deliveredThrough) =>
        new(IngressAdmissionKind.Accepted, deliveredThrough, 0, 0, 0, null);

    /// <summary>An in-order delivery that coalesced with at least one other op.</summary>
    /// <param name="deliveredThrough">The resulting watermark.</param>
    public static IngressAdmission Conflated(long deliveredThrough) =>
        new(IngressAdmissionKind.Conflated, deliveredThrough, 0, 0, 0, null);

    /// <summary>An out-of-order envelope parked pending its predecessors.</summary>
    /// <param name="gapFrom">The first sequence still missing.</param>
    public static IngressAdmission Buffered(long gapFrom) =>
        new(IngressAdmissionKind.Buffered, 0, gapFrom, 0, 0, null);

    /// <summary>A newer producer incarnation taking over.</summary>
    /// <param name="from">The fence we held.</param>
    /// <param name="to">The fence we now hold.</param>
    public static IngressAdmission GenerationHandoff(long from, long to) =>
        new(IngressAdmissionKind.GenerationHandoff, 0, 0, from, to, null);

    /// <summary>A refusal, receipted on the dropped channel.</summary>
    /// <param name="reason">Why the envelope was refused.</param>
    public static IngressAdmission Dropped(IngressDropReason reason) =>
        new(IngressAdmissionKind.Dropped, 0, 0, 0, 0, reason);

    /// <summary>The lossless refusal: the producer retries after a drain.</summary>
    public static IngressAdmission Blocked =>
        new(IngressAdmissionKind.Blocked, 0, 0, 0, 0, null);

    /// <summary>Whether the envelope became visible to readers.</summary>
    public bool IsDelivered =>
        Kind is IngressAdmissionKind.Accepted
            or IngressAdmissionKind.Conflated
            or IngressAdmissionKind.GenerationHandoff;
}

/// <summary>Where a scope is in its lifecycle.</summary>
/// <remarks>Scopes are keyed and independent: closing one never touches another.</remarks>
public enum IngressLifecycle
{
    /// <summary>Opened, nothing delivered yet.</summary>
    Opening,

    /// <summary>Delivering.</summary>
    Live,

    /// <summary>Disconnected but retained: state and cursors survive for replay.</summary>
    Suspended,

    /// <summary>Terminal until reopened. Admits nothing.</summary>
    Closed,
}

/// <summary>The derived answer to "can a consumer trust this scope right now?".</summary>
public enum IngressReadiness
{
    /// <summary>No such scope.</summary>
    Unknown,

    /// <summary>Open, nothing delivered yet.</summary>
    Warming,

    /// <summary>Delivered and inside the freshness horizon.</summary>
    Ready,

    /// <summary>Delivered, but the newest accepted stamp is older than the horizon.</summary>
    Stale,

    /// <summary>Disconnected; retained state may be replayed.</summary>
    Suspended,

    /// <summary>Terminal.</summary>
    Closed,
}

/// <summary>What the scope currently claims authority over.</summary>
/// <param name="Generation">The generation fence currently held.</param>
/// <param name="DeliveredThrough">Highest in-order sequence delivered, or null before first delivery.</param>
/// <param name="StampedAt">Producer stamp of the newest delivered envelope.</param>
public readonly record struct IngressAuthority(
    long Generation,
    long? DeliveredThrough,
    long StampedAt);

/// <summary>The derived retry decision for a scope that has errored.</summary>
/// <param name="Attempt">Consecutive errors since the last delivery.</param>
/// <param name="Backoff">Exponential backoff, clamped to the policy ceiling.</param>
/// <param name="ResumeFrom">Sequence a replay should resume from.</param>
public readonly record struct IngressRetry(int Attempt, long Backoff, long ResumeFrom);

/// <summary>What a reconnect needs from the transport to close its gap.</summary>
/// <param name="Generation">The generation being resumed.</param>
/// <param name="FromSequence">First sequence the consumer has not delivered.</param>
public readonly record struct ReplayRequest(long Generation, long FromSequence);

/// <summary>Bounds and taxes, all flavor-neutral.</summary>
/// <remarks>
/// <see cref="Overflow"/> is the relay algebra's own <see cref="RelayOverflow"/>: backpressure is
/// not re-invented here, and construction validates the choice against the merge policy's
/// <c>Conflates</c> flag exactly as <see cref="RelayCell{T}"/> does.
/// </remarks>
public sealed record IngressPolicy
{
    /// <summary>
    /// How many out-of-order envelopes may be held per scope. Zero disables reordering: a gap
    /// drops immediately.
    /// </summary>
    public int ReorderWindow { get; init; } = 8;

    /// <summary>
    /// An age above this marks a scope <see cref="IngressReadiness.Stale"/>; an ARRIVING envelope
    /// that old is dropped as <see cref="IngressDropReason.Expired"/>.
    /// </summary>
    public long FreshnessHorizon { get; init; } = 1_000;

    /// <summary>Merged-op count at which <see cref="Overflow"/> engages.</summary>
    public long HighWater { get; init; } = 64;

    /// <summary>What to do at <see cref="HighWater"/>.</summary>
    public RelayOverflow Overflow { get; init; } = RelayOverflow.Conflate;

    /// <summary>Retained receipts, oldest evicted first.</summary>
    public int ReceiptCapacity { get; init; } = 256;

    /// <summary>First retry backoff; doubles per consecutive error.</summary>
    public long RetryBase { get; init; } = 10;

    /// <summary>Backoff clamp.</summary>
    public long RetryCeiling { get; init; } = 10_000;
}

/// <summary>Which receipt channel a receipt belongs to.</summary>
/// <remarks>
/// The three are separate READER KINDS because they have separate consumers: a projection wants
/// accepts, a dashboard wants drops, a supervisor wants errors. A dropped envelope must not
/// invalidate a projection that only reads accepts.
/// </remarks>
public enum IngressReceiptChannel
{
    /// <summary>Delivered.</summary>
    Accepted,

    /// <summary>Refused by a decision.</summary>
    Dropped,

    /// <summary>Could not be decided.</summary>
    Error,
}

/// <summary>The decision a receipt records.</summary>
/// <param name="Channel">Which channel this outcome is read from.</param>
/// <param name="DeliveredThrough">The resulting watermark (Accepted).</param>
/// <param name="Conflated">Whether the payload coalesced into a non-empty window (Accepted).</param>
/// <param name="DropReason">Why the envelope was refused (Dropped).</param>
/// <param name="Error">The failure that prevented a decision (Error).</param>
public readonly record struct IngressReceiptOutcome(
    IngressReceiptChannel Channel,
    long DeliveredThrough,
    bool Conflated,
    IngressDropReason? DropReason,
    IngressError? Error)
{
    /// <summary>An accepted-channel outcome.</summary>
    /// <param name="deliveredThrough">The resulting watermark.</param>
    /// <param name="conflated">Whether the payload coalesced.</param>
    public static IngressReceiptOutcome ForAccepted(long deliveredThrough, bool conflated) =>
        new(IngressReceiptChannel.Accepted, deliveredThrough, conflated, null, null);

    /// <summary>A dropped-channel outcome.</summary>
    /// <param name="reason">Why the envelope was refused.</param>
    public static IngressReceiptOutcome ForDropped(IngressDropReason reason) =>
        new(IngressReceiptChannel.Dropped, 0, false, reason, null);

    /// <summary>An error-channel outcome.</summary>
    /// <param name="error">The failure that prevented a decision.</param>
    public static IngressReceiptOutcome ForError(IngressError error) =>
        new(IngressReceiptChannel.Error, 0, false, null, error);
}

/// <summary>One durable record of an admission decision.</summary>
/// <typeparam name="TKey">The scope identity type.</typeparam>
/// <param name="Offset">Monotone receipt offset, stable across eviction.</param>
/// <param name="Key">Scope the decision was made for.</param>
/// <param name="Generation">Generation the decision was made under.</param>
/// <param name="Sequence">Sequence the decision was made for, when there was one.</param>
/// <param name="Outcome">The decision.</param>
public sealed record IngressReceipt<TKey>(
    long Offset,
    TKey Key,
    long Generation,
    long? Sequence,
    IngressReceiptOutcome Outcome)
{
    /// <summary>The channel this receipt is read from.</summary>
    public IngressReceiptChannel Channel => Outcome.Channel;
}

/// <summary>Which of a scope's four reader kinds a transition dirtied.</summary>
/// <remarks>
/// Four kinds exist because they have four different invalidation boundaries: a buffered envelope
/// moves nothing but its own gap, a tick across the horizon moves only readiness, and an error
/// moves only retry. Collapsing them would make an error deepen a backoff AND re-render a value
/// that did not change.
/// </remarks>
/// <param name="Value">The coalesced window changed.</param>
/// <param name="Readiness">The derived readiness changed.</param>
/// <param name="Authority">The derived authority changed.</param>
/// <param name="Retry">The derived retry decision changed.</param>
public readonly record struct IngressScopeChange(
    bool Value,
    bool Readiness,
    bool Authority,
    bool Retry)
{
    /// <summary>Nothing changed — the shell must not clear a reader.</summary>
    public bool IsEmpty => !(Value || Readiness || Authority || Retry);

    /// <summary>Every reader kind moved (an in-order delivery).</summary>
    public static IngressScopeChange All => new(true, true, true, true);

    /// <summary>Only readiness moved (a suspend, or a freshness-horizon crossing).</summary>
    public static IngressScopeChange ReadinessOnly => new(false, true, false, false);

    /// <summary>Only the coalesced window moved (a drain).</summary>
    public static IngressScopeChange ValueOnly => new(true, false, false, false);

    /// <summary>Only the retry decision moved (an error).</summary>
    public static IngressScopeChange RetryOnly => new(false, false, false, true);

    /// <summary>
    /// What materializing a previously-unknown scope changes.
    /// </summary>
    /// <remarks>
    /// An unknown scope reads <see cref="IngressReadiness.Unknown"/> and a null authority, so its
    /// first appearance moves readiness and authority — and nothing else. A reader that observed a
    /// key before it opened must learn that it did.
    /// </remarks>
    public static IngressScopeChange Creation => new(false, true, true, false);

    /// <summary>The pointwise union with <paramref name="other"/>.</summary>
    /// <param name="other">The change to fold in.</param>
    public IngressScopeChange Union(IngressScopeChange other) =>
        new(
            Value || other.Value,
            Readiness || other.Readiness,
            Authority || other.Authority,
            Retry || other.Retry);
}

/// <summary>The pure invalidation set of one transition.</summary>
/// <remarks>
/// The whole contract between the core and a flavor shell: the core decides WHICH readers moved,
/// and each shell clears exactly that set on its own graph, in one frontier walk.
/// </remarks>
/// <typeparam name="TKey">The scope identity type.</typeparam>
public sealed class IngressChange<TKey>
    where TKey : notnull
{
    private readonly List<KeyValuePair<TKey, IngressScopeChange>> _scopes = [];

    /// <summary>Per-scope dirty reader kinds, in transition order.</summary>
    public IReadOnlyList<KeyValuePair<TKey, IngressScopeChange>> Scopes => _scopes;

    /// <summary>The accepted-receipt reader grew.</summary>
    public bool AcceptedReceipts { get; private set; }

    /// <summary>The dropped-receipt reader grew.</summary>
    public bool DroppedReceipts { get; private set; }

    /// <summary>The error-receipt reader grew.</summary>
    public bool ErrorReceipts { get; private set; }

    /// <summary>Whether this transition dirtied nothing at all.</summary>
    public bool IsEmpty =>
        _scopes.Count == 0 && !AcceptedReceipts && !DroppedReceipts && !ErrorReceipts;

    internal void Mark(TKey key, IngressScopeChange change)
    {
        if (change.IsEmpty) return;
        _scopes.Add(new KeyValuePair<TKey, IngressScopeChange>(key, change));
    }

    internal void MarkChannel(IngressReceiptChannel channel)
    {
        switch (channel)
        {
            case IngressReceiptChannel.Accepted: AcceptedReceipts = true; break;
            case IngressReceiptChannel.Dropped: DroppedReceipts = true; break;
            case IngressReceiptChannel.Error: ErrorReceipts = true; break;
            default:
                // A channel is which READER the shell must invalidate. Folding an unrecognised
                // channel into `Error` would dirty the wrong reader: a receipt would be appended
                // to a channel whose version source never bumped, so its reader would serve a
                // cached list that is missing it, permanently.
                throw new ArgumentOutOfRangeException(
                    nameof(channel), channel, "Unknown ingress receipt channel.");
        }
    }
}

/// <summary>Read-only projection of one scope, from which every derive is computed.</summary>
/// <remarks>
/// A shell's reader bodies call these and nothing else, which is why the three flavors cannot
/// disagree about readiness, authority, or retry.
/// </remarks>
/// <param name="Lifecycle">Lifecycle position.</param>
/// <param name="Generation">Generation fence.</param>
/// <param name="DeliveredThrough">In-order watermark.</param>
/// <param name="StampedAt">Producer stamp of the newest delivered envelope.</param>
/// <param name="Buffered">Buffered out-of-order envelopes.</param>
/// <param name="WindowDepth">Merged ops in the hot window.</param>
/// <param name="ConsecutiveErrors">Consecutive errors since the last delivery.</param>
/// <param name="ObservedNow">Logical now, as of the last tick.</param>
/// <param name="Policy">Bounds in force.</param>
public readonly record struct IngressScopeView(
    IngressLifecycle Lifecycle,
    long Generation,
    long? DeliveredThrough,
    long StampedAt,
    int Buffered,
    long WindowDepth,
    int ConsecutiveErrors,
    long ObservedNow,
    IngressPolicy Policy)
{
    /// <summary>Whether the newest delivered stamp is inside the freshness horizon.</summary>
    public bool IsFresh =>
        (ObservedNow > StampedAt ? ObservedNow - StampedAt : 0) <= Policy.FreshnessHorizon;

    /// <summary>Derived readiness.</summary>
    /// <remarks>
    /// A scope that has never delivered is <see cref="IngressReadiness.Warming"/>, not
    /// <see cref="IngressReadiness.Stale"/>: there is no stamp to be old.
    /// </remarks>
    public IngressReadiness Readiness => Lifecycle switch
    {
        IngressLifecycle.Closed => IngressReadiness.Closed,
        IngressLifecycle.Suspended => IngressReadiness.Suspended,
        IngressLifecycle.Opening => IngressReadiness.Warming,
        IngressLifecycle.Live => DeliveredThrough is null
            ? IngressReadiness.Warming
            : IsFresh ? IngressReadiness.Ready : IngressReadiness.Stale,

        // Readiness is the answer to "can a consumer trust this scope right now?". A lifecycle
        // this build does not know must not be answered `Ready` on the strength of a watermark,
        // which is what the old catch-all did for every future non-Live state.
        _ => throw new ArgumentOutOfRangeException(
            nameof(Lifecycle), Lifecycle, "Unknown ingress lifecycle has no readiness."),
    };

    /// <summary>Derived authority. A closed scope claims none.</summary>
    public IngressAuthority? Authority =>
        Lifecycle == IngressLifecycle.Closed
            ? null
            : new IngressAuthority(Generation, DeliveredThrough, StampedAt);

    /// <summary>The first sequence not yet delivered in order.</summary>
    public long ResumeFrom => DeliveredThrough is { } seq ? seq + 1 : 0;

    /// <summary>
    /// Whether the scope is holding a gap open — an out-of-order buffer that a replay, not a
    /// retry, is the fix for.
    /// </summary>
    public bool HasGap => Buffered > 0;

    /// <summary>Derived retry, or null while no error is outstanding.</summary>
    /// <remarks>A healthy scope has NO backoff, rather than a zero one.</remarks>
    public IngressRetry? Retry
    {
        get
        {
            if (ConsecutiveErrors == 0) return null;
            var shift = Math.Min(ConsecutiveErrors - 1, 62);
            var headroom = long.MaxValue >> shift;
            var scaled = Policy.RetryBase > headroom ? long.MaxValue : Policy.RetryBase << shift;
            return new IngressRetry(
                ConsecutiveErrors,
                Math.Min(scaled, Policy.RetryCeiling),
                ResumeFrom);
        }
    }
}

/// <summary>A decoded source of envelopes.</summary>
/// <remarks>
/// The core never calls this — a shell's <c>Pump</c> does — which is exactly what keeps admission
/// independent of delivery. Implementations decode; they do not decide.
/// </remarks>
/// <typeparam name="TKey">The scope identity type.</typeparam>
/// <typeparam name="TValue">The decoded payload type.</typeparam>
public interface IIngressTransport<TKey, TValue>
{
    /// <summary>How this transport delivers. Drives the schedule and nothing else.</summary>
    IngressTransportKind Kind { get; }

    /// <summary>Takes everything decoded since the last call. Never blocks.</summary>
    IReadOnlyList<IngressEnvelope<TKey, TValue>> Drain();

    /// <summary>
    /// Asks the producer to resend from the request's sequence, and reports whether the transport
    /// could carry the request.
    /// </summary>
    /// <remarks>
    /// A polling transport that cannot address history answers false, which is what makes "this
    /// gap will never close" observable rather than silent.
    /// </remarks>
    /// <param name="key">The scope to replay.</param>
    /// <param name="request">The gap to close.</param>
    bool RequestReplay(TKey key, ReplayRequest request);
}

/// <summary>An in-process event channel: the reference transport.</summary>
/// <remarks>
/// <see cref="Kind"/> is configurable so one implementation exercises all three delivery modes,
/// including the bounded-polling case that cannot serve a replay.
/// </remarks>
/// <typeparam name="TKey">The scope identity type.</typeparam>
/// <typeparam name="TValue">The decoded payload type.</typeparam>
public sealed class InProcIngress<TKey, TValue> : IIngressTransport<TKey, TValue>
{
    private readonly Queue<IngressEnvelope<TKey, TValue>> _inbound = new();
    private readonly List<KeyValuePair<TKey, ReplayRequest>> _replays = [];

    /// <summary>Creates an empty channel delivering as <paramref name="kind"/>.</summary>
    /// <param name="kind">How this channel delivers.</param>
    public InProcIngress(IngressTransportKind kind) => Kind = kind;

    /// <inheritdoc/>
    public IngressTransportKind Kind { get; }

    /// <summary>Replay requests observed so far, oldest first.</summary>
    public IReadOnlyList<KeyValuePair<TKey, ReplayRequest>> Replays => _replays;

    /// <summary>Queues one envelope for the next <see cref="Drain"/>.</summary>
    /// <param name="envelope">The envelope to queue.</param>
    public void Push(IngressEnvelope<TKey, TValue> envelope)
    {
        Guard.NotNull(envelope, nameof(envelope));
        _inbound.Enqueue(envelope);
    }

    /// <inheritdoc/>
    public IReadOnlyList<IngressEnvelope<TKey, TValue>> Drain()
    {
        var batch = _inbound.ToArray();
        _inbound.Clear();
        return batch;
    }

    /// <inheritdoc/>
    public bool RequestReplay(TKey key, ReplayRequest request)
    {
        // A bounded poll has no addressable history: it can only wait for the next page, so it
        // cannot honour a replay.
        if (Kind == IngressTransportKind.BoundedPolling) return false;
        _replays.Add(new KeyValuePair<TKey, ReplayRequest>(key, request));
        return true;
    }
}

/// <summary>
/// Keyed lifecycle scopes, the admission algebra, and a bounded three-channel receipt log.
/// </summary>
/// <remarks>
/// No reactive node, no context, nothing awaited: each flavor shell wraps this and owns its own
/// reactivity. Every mutator returns an <see cref="IngressChange{TKey}"/> instead of invalidating,
/// because invalidation is a graph write.
/// <para>
/// The admission order is NORMATIVE: lifecycle, generation fence, freshness, generation handoff,
/// dedupe, ordering, backpressure, merge. Two orderings are load-bearing. The FENCE OUTRANKS
/// DEDUPE — otherwise a zombie producer replaying old sequences reads as a legitimate retry. And
/// FRESHNESS OUTRANKS ORDERING — otherwise an expired envelope occupies a reorder slot and a slow
/// zombie can starve live data.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The scope identity type.</typeparam>
/// <typeparam name="TValue">The payload type folded under the merge policy.</typeparam>
public sealed class IngressCore<TKey, TValue>
    where TKey : notnull
{
    private sealed class Scope
    {
        internal Scope(long generation) => Generation = generation;

        internal IngressLifecycle Lifecycle = IngressLifecycle.Opening;
        internal long Generation;
        internal long? DeliveredThrough;
        internal long StampedAt;
        internal readonly SortedDictionary<long, KeyValuePair<TValue, long>> Pending = new();
        internal bool HasWindow;
        internal TValue Window = default!;
        internal long WindowDepth;
        internal int ConsecutiveErrors;

        internal long NextExpected => DeliveredThrough is { } seq ? seq + 1 : 0;

        internal IngressLifecycle LiveOrOpening =>
            DeliveredThrough is null ? IngressLifecycle.Opening : IngressLifecycle.Live;

        internal IngressScopeView View(long observedNow, IngressPolicy policy) => new(
            Lifecycle,
            Generation,
            DeliveredThrough,
            StampedAt,
            Pending.Count,
            WindowDepth,
            ConsecutiveErrors,
            observedNow,
            policy);

        /// <summary>
        /// Everything a reader can observe ABOUT SHAPE rather than payload.
        /// </summary>
        /// <remarks>
        /// The buffered path diffs these to DERIVE its invalidation set, so "a buffered envelope
        /// invalidates nothing" is a computed fact rather than a claim — and the
        /// handoff-that-buffers case (which clears the window) cannot slip through.
        /// </remarks>
        internal (IngressLifecycle Lifecycle, long Generation, long? Watermark, bool HasWindow) Stamp =>
            (Lifecycle, Generation, DeliveredThrough, HasWindow);
    }

    private enum DecisionKind { Refuse, Block, Buffered, Delivered }

    private readonly record struct Decision(
        DecisionKind Kind,
        IngressDropReason Reason,
        long GapFrom,
        long DeliveredThrough,
        bool Conflated,
        bool Handoff,
        long HandoffFrom);

    private readonly IngressPolicy _policy;
    private readonly MergePolicy<TValue> _merge;
    private readonly Dictionary<TKey, Scope> _scopes = [];
    private readonly LinkedList<IngressReceipt<TKey>> _receipts = new();
    private long _nextReceiptOffset;
    private long _observedNow;

    /// <summary>
    /// Builds a core over <paramref name="policy"/>, validating the overflow choice against the
    /// merge algebra.
    /// </summary>
    /// <remarks>
    /// The same validation <see cref="RelayCell{T}"/> performs, for the same reason:
    /// <see cref="RelayOverflow.Conflate"/> bounds nothing for a non-conflating fold.
    /// </remarks>
    /// <param name="policy">The bounds in force.</param>
    /// <param name="merge">The associative fold the hot window coalesces under.</param>
    /// <exception cref="ArgumentException">Conflating overflow with a non-conflating fold.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A zero receipt capacity.</exception>
    public IngressCore(IngressPolicy policy, MergePolicy<TValue> merge)
    {
        Guard.NotNull(policy, nameof(policy));
        Guard.NotNull(merge, nameof(merge));
        if (policy.Overflow == RelayOverflow.Conflate && !merge.Conflates)
            throw new ArgumentException(
                $"conflating overflow requires a conflating merge policy; '{merge.Name}' cannot bound",
                nameof(policy));
        if (policy.ReceiptCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.ReceiptCapacity,
                "receipt capacity must be positive; zero would discard every receipt it just minted");
        if (policy.ReorderWindow < 0)
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.ReorderWindow,
                "reorder window must be non-negative");

        _policy = policy;
        _merge = merge;
    }

    /// <summary>The bounds in force.</summary>
    public IngressPolicy Policy => _policy;

    /// <summary>The merge algebra the hot window coalesces under.</summary>
    public MergePolicy<TValue> Merge => _merge;

    /// <summary>Logical now, as of the last <see cref="Tick"/>.</summary>
    public long ObservedNow => _observedNow;

    /// <summary>Every known scope key, for a shell rebuilding its reader table.</summary>
    public IReadOnlyList<TKey> ScopeKeys() => [.. _scopes.Keys];

    /// <summary>Read-only projection of one scope, or null when unknown.</summary>
    /// <param name="key">The scope to project.</param>
    public IngressScopeView? View(TKey key) =>
        _scopes.TryGetValue(key, out var scope) ? scope.View(_observedNow, _policy) : null;

    /// <summary>Readiness of a scope.</summary>
    /// <remarks>
    /// An unknown scope is <see cref="IngressReadiness.Unknown"/> rather than an error: a reader
    /// may legitimately observe a key before it opens.
    /// </remarks>
    /// <param name="key">The scope to read.</param>
    public IngressReadiness Readiness(TKey key) =>
        View(key) is { } view ? view.Readiness : IngressReadiness.Unknown;

    /// <summary>Authority claimed by a scope.</summary>
    /// <param name="key">The scope to read.</param>
    public IngressAuthority? Authority(TKey key) => View(key)?.Authority;

    /// <summary>Retry decision for a scope.</summary>
    /// <param name="key">The scope to read.</param>
    public IngressRetry? Retry(TKey key) => View(key)?.Retry;

    /// <summary>The coalesced window awaiting drain.</summary>
    /// <param name="key">The scope to read.</param>
    public Optional<TValue> Peek(TKey key) =>
        _scopes.TryGetValue(key, out var scope) && scope.HasWindow
            ? Optional<TValue>.Some(scope.Window)
            : Optional<TValue>.None;

    /// <summary>Receipts on one channel, oldest first.</summary>
    /// <param name="channel">The channel to read.</param>
    public IReadOnlyList<IngressReceipt<TKey>> Receipts(IngressReceiptChannel channel) =>
        [.. _receipts.Where(receipt => receipt.Channel == channel)];

    /// <summary>Opens (or reopens) a scope at <paramref name="generation"/>.</summary>
    /// <remarks>
    /// Reopening a SUSPENDED scope preserves its watermark so a replay can resume from the gap;
    /// reopening a CLOSED scope resets it, because a closed scope's producer is gone and its
    /// sequence space is not resumable.
    /// </remarks>
    /// <param name="key">The scope to open.</param>
    /// <param name="generation">The producer incarnation to open at.</param>
    public IngressChange<TKey> Open(TKey key, long generation)
    {
        Guard.NotNull(key, nameof(key));
        var change = new IngressChange<TKey>();
        if (!_scopes.TryGetValue(key, out var scope))
        {
            _scopes.Add(key, new Scope(generation));
            change.Mark(key, IngressScopeChange.Creation);
            return change;
        }

        var before = (scope.Lifecycle, scope.Generation, scope.DeliveredThrough);
        if (scope.Lifecycle == IngressLifecycle.Closed)
        {
            scope = new Scope(generation);
            _scopes[key] = scope;
        }
        else
        {
            scope.Lifecycle = scope.LiveOrOpening;
            if (generation > scope.Generation)
            {
                scope.Generation = generation;
                scope.DeliveredThrough = null;
                scope.Pending.Clear();
            }
        }

        var after = (scope.Lifecycle, scope.Generation, scope.DeliveredThrough);
        if (before != after)
        {
            change.Mark(
                key,
                new IngressScopeChange(
                    Value: false,
                    Readiness: before.Lifecycle != after.Lifecycle,
                    Authority: true,
                    Retry: false));
        }

        return change;
    }

    /// <summary>Suspends a scope: retains state and cursors, stops delivering.</summary>
    /// <param name="key">The scope to suspend.</param>
    /// <returns>The change, and the replay request a reconnect will need (null when idempotent).</returns>
    public (IngressChange<TKey> Change, ReplayRequest? Replay) Suspend(TKey key)
    {
        Guard.NotNull(key, nameof(key));
        var change = new IngressChange<TKey>();
        if (!_scopes.TryGetValue(key, out var scope)) return (change, null);
        if (scope.Lifecycle is IngressLifecycle.Suspended or IngressLifecycle.Closed)
            return (change, null);

        scope.Lifecycle = IngressLifecycle.Suspended;
        change.Mark(key, IngressScopeChange.ReadinessOnly);
        return (change, new ReplayRequest(scope.Generation, scope.NextExpected));
    }

    /// <summary>Reconnects a scope at <paramref name="generation"/>, clearing the error streak.</summary>
    /// <remarks>
    /// A higher generation is a producer handoff: the sequence space restarts, so the buffered
    /// reorder window AND the coalesced value are discarded rather than replayed against a fence
    /// they no longer belong to. One rule, two entry points — this is the same reset
    /// <see cref="Admit"/> performs.
    /// </remarks>
    /// <param name="key">The scope to reconnect.</param>
    /// <param name="generation">The producer incarnation to resume under.</param>
    public (IngressChange<TKey> Change, ReplayRequest Replay) Reconnect(TKey key, long generation)
    {
        Guard.NotNull(key, nameof(key));
        var change = new IngressChange<TKey>();
        var created = !_scopes.TryGetValue(key, out var scope);
        if (created)
        {
            scope = new Scope(generation);
            _scopes.Add(key, scope);
        }

        var handoff = generation > scope!.Generation;
        var hadWindow = scope.HasWindow;
        if (handoff)
        {
            scope.Generation = generation;
            scope.DeliveredThrough = null;
            scope.Pending.Clear();
            scope.HasWindow = false;
            scope.Window = default!;
            scope.WindowDepth = 0;
        }

        var beforeLifecycle = scope.Lifecycle;
        scope.Lifecycle = scope.LiveOrOpening;
        var hadErrors = scope.ConsecutiveErrors > 0;
        scope.ConsecutiveErrors = 0;

        var basis = new IngressScopeChange(
            Value: handoff && hadWindow,
            Readiness: beforeLifecycle != scope.Lifecycle,
            Authority: handoff,
            Retry: hadErrors);
        change.Mark(key, created ? basis.Union(IngressScopeChange.Creation) : basis);
        return (change, new ReplayRequest(scope.Generation, scope.NextExpected));
    }

    /// <summary>Closes a scope. It admits nothing and claims no authority until reopened.</summary>
    /// <param name="key">The scope to close.</param>
    public IngressChange<TKey> Close(TKey key)
    {
        Guard.NotNull(key, nameof(key));
        var change = new IngressChange<TKey>();
        if (!_scopes.TryGetValue(key, out var scope)) return change;
        if (scope.Lifecycle == IngressLifecycle.Closed) return change;

        var hadWindow = scope.HasWindow;
        var hadErrors = scope.ConsecutiveErrors > 0;
        scope.Lifecycle = IngressLifecycle.Closed;
        scope.Pending.Clear();
        scope.HasWindow = false;
        scope.Window = default!;
        scope.WindowDepth = 0;
        scope.ConsecutiveErrors = 0;
        change.Mark(
            key,
            new IngressScopeChange(
                Value: hadWindow,
                Readiness: true,
                Authority: true,
                Retry: hadErrors));
        return change;
    }

    /// <summary>Advances logical time.</summary>
    /// <remarks>
    /// Only scopes that CROSSED the freshness horizon are dirtied — a tick inside the horizon
    /// invalidates nothing, which is what keeps a polling shell from re-rendering on every tick.
    /// </remarks>
    /// <param name="now">The new logical now.</param>
    public IngressChange<TKey> Tick(long now)
    {
        var change = new IngressChange<TKey>();
        if (now == _observedNow) return change;

        var before = _observedNow;
        _observedNow = now;
        foreach (var (key, scope) in _scopes)
        {
            if (scope.View(before, _policy).Readiness != scope.View(now, _policy).Readiness)
                change.Mark(key, IngressScopeChange.ReadinessOnly);
        }

        return change;
    }

    /// <summary>Records a transport/decode failure against a scope, deepening its backoff.</summary>
    /// <param name="key">The scope the failure is attributed to.</param>
    /// <param name="error">What went wrong.</param>
    public IngressChange<TKey> Fail(TKey key, IngressError error)
    {
        Guard.NotNull(key, nameof(key));
        var change = new IngressChange<TKey>();
        var created = !_scopes.TryGetValue(key, out var scope);
        if (created)
        {
            scope = new Scope(0);
            _scopes.Add(key, scope);
        }

        scope!.ConsecutiveErrors = scope.ConsecutiveErrors == int.MaxValue
            ? int.MaxValue
            : scope.ConsecutiveErrors + 1;
        var basis = IngressScopeChange.RetryOnly;
        change.Mark(key, created ? basis.Union(IngressScopeChange.Creation) : basis);
        change.MarkChannel(PushReceipt(
            key,
            scope.Generation,
            sequence: null,
            IngressReceiptOutcome.ForError(error)));
        return change;
    }

    /// <summary>Drains a scope's coalesced window, resetting its depth.</summary>
    /// <remarks>
    /// A drain is an EGRESS, not an ack: it never moves the watermark, so a replay after a drain
    /// still resumes from the same sequence. An empty drain dirties nothing.
    /// </remarks>
    /// <param name="key">The scope to drain.</param>
    public (IngressChange<TKey> Change, Optional<TValue> Drained) Drain(TKey key)
    {
        Guard.NotNull(key, nameof(key));
        var change = new IngressChange<TKey>();
        if (!_scopes.TryGetValue(key, out var scope) || !scope.HasWindow)
            return (change, Optional<TValue>.None);

        var value = Optional<TValue>.Some(scope.Window);
        scope.HasWindow = false;
        scope.Window = default!;
        scope.WindowDepth = 0;
        change.Mark(key, IngressScopeChange.ValueOnly);
        return (change, value);
    }

    /// <summary>Admits one envelope under the normative admission order.</summary>
    /// <param name="envelope">The decoded envelope.</param>
    public (IngressChange<TKey> Change, IngressAdmission Admission) Admit(
        IngressEnvelope<TKey, TValue> envelope)
    {
        Guard.NotNull(envelope, nameof(envelope));
        var key = envelope.Key;
        var created = !_scopes.TryGetValue(key, out var scope);
        (IngressLifecycle Lifecycle, long Generation, long? Watermark, bool HasWindow)? before = null;
        if (created)
        {
            scope = new Scope(envelope.Generation);
            _scopes.Add(key, scope);
        }
        else
        {
            before = scope!.Stamp;
        }

        var decision = Decide(scope!, envelope);

        // A refused envelope must not leave a scope behind: an expired or blocked message for a
        // key we do not track is not an admission plane, and materializing one would report a
        // readiness change that never happened.
        var admitted = decision.Kind is DecisionKind.Buffered or DecisionKind.Delivered;
        if (created && !admitted) _scopes.Remove(key);

        var change = new IngressChange<TKey>();
        var fence = _scopes.TryGetValue(key, out var live) ? live.Generation : envelope.Generation;

        switch (decision.Kind)
        {
            case DecisionKind.Refuse:
                change.MarkChannel(PushReceipt(
                    key,
                    fence,
                    envelope.Sequence,
                    IngressReceiptOutcome.ForDropped(decision.Reason)));
                return (change, IngressAdmission.Dropped(decision.Reason));

            case DecisionKind.Block:
                change.MarkChannel(PushReceipt(
                    key,
                    fence,
                    envelope.Sequence,
                    IngressReceiptOutcome.ForDropped(IngressDropReason.Backpressure)));
                return (change, IngressAdmission.Blocked);

            case DecisionKind.Buffered:
                {
                    // A buffered envelope mints no receipt, and for an already-current scope it
                    // dirties no reader, because nothing a reader can observe moved. Two cases are
                    // NOT invisible and are DERIVED rather than assumed: the scope's own first
                    // appearance (it moves off Unknown), and a generation handoff that buffers —
                    // which resets the fence, the watermark, and the window before parking the
                    // envelope.
                    var scopeChange = created ? IngressScopeChange.Creation : default;
                    if (before is { } was)
                    {
                        var now = scope!.Stamp;
                        scopeChange = scopeChange.Union(new IngressScopeChange(
                            Value: was.HasWindow != now.HasWindow,
                            Readiness: was.Lifecycle != now.Lifecycle
                                || (was.Watermark is null) != (now.Watermark is null),
                            Authority: was.Generation != now.Generation
                                || was.Watermark != now.Watermark,
                            Retry: false));
                    }

                    change.Mark(key, scopeChange);
                    return (change, IngressAdmission.Buffered(decision.GapFrom));
                }

            case DecisionKind.Delivered:
                change.Mark(key, IngressScopeChange.All);
                change.MarkChannel(PushReceipt(
                    key,
                    fence,
                    envelope.Sequence,
                    IngressReceiptOutcome.ForAccepted(decision.DeliveredThrough, decision.Conflated)));
                var admission = decision.Handoff
                    ? IngressAdmission.GenerationHandoff(decision.HandoffFrom, fence)
                    : decision.Conflated
                        ? IngressAdmission.Conflated(decision.DeliveredThrough)
                        : IngressAdmission.Accepted(decision.DeliveredThrough);
                return (change, admission);

            default:
                // The old catch-all made Delivered the ASSUMED LAST VARIANT: any decision kind
                // added to the algebra without a case here would mint an ACCEPTED receipt and
                // advance the watermark for an envelope that was never merged.
                throw new ArgumentOutOfRangeException(
                    nameof(envelope), decision.Kind, "Unknown ingress decision kind.");
        }
    }

    /// <summary>
    /// The admission algebra proper: pure over one scope, mutating only that scope, minting
    /// nothing.
    /// </summary>
    private Decision Decide(Scope scope, IngressEnvelope<TKey, TValue> envelope)
    {
        // 1. Lifecycle.
        if (scope.Lifecycle == IngressLifecycle.Closed)
            return Refuse(IngressDropReason.ScopeClosed);

        // 2. Generation fence — BEFORE dedupe, so a zombie producer replaying old sequences under
        //    an old generation stays distinguishable from a legitimate retry.
        if (envelope.Generation < scope.Generation)
            return Refuse(IngressDropReason.StaleGeneration);

        // 3. Freshness — BEFORE ordering, so an expired envelope never occupies a reorder slot and
        //    a slow zombie cannot exhaust the buffer and starve live data.
        var age = _observedNow > envelope.StampedAt ? _observedNow - envelope.StampedAt : 0;
        if (age > _policy.FreshnessHorizon) return Refuse(IngressDropReason.Expired);

        // 4. Generation handoff — a baseline RESET, not a continuation. The new incarnation's
        //    first envelope is authoritative, so the old incarnation's undrained window and
        //    buffered successors are discarded rather than folded into it. Merging a superseded
        //    delta into a fresh baseline is exactly the build-skew corruption the fence exists to
        //    prevent.
        var handoff = false;
        var handoffFrom = scope.Generation;
        if (envelope.Generation > scope.Generation)
        {
            handoff = true;
            scope.Generation = envelope.Generation;
            scope.DeliveredThrough = null;
            scope.Pending.Clear();
            scope.HasWindow = false;
            scope.Window = default!;
            scope.WindowDepth = 0;
        }

        // 5. Dedupe.
        var expected = scope.NextExpected;
        if (envelope.Sequence < expected) return Refuse(IngressDropReason.DuplicateSequence);

        // 6. Ordering.
        if (envelope.Sequence > expected)
        {
            if (scope.Pending.ContainsKey(envelope.Sequence))
                return Refuse(IngressDropReason.DuplicateBuffered);
            if (scope.Pending.Count >= _policy.ReorderWindow)
                return Refuse(IngressDropReason.ReorderWindowOverflow);
            scope.Pending.Add(
                envelope.Sequence,
                new KeyValuePair<TValue, long>(envelope.Payload, envelope.StampedAt));
            return new Decision(DecisionKind.Buffered, default, expected, 0, false, false, 0);
        }

        // 7. Backpressure. Checked HERE and not earlier: refusing an in-order envelope leaves a
        //    gap the reorder buffer cannot close, so Block must be observable by the producer as
        //    its own outcome.
        if (scope.WindowDepth >= _policy.HighWater)
        {
            switch (_policy.Overflow)
            {
                case RelayOverflow.Block:
                    return new Decision(DecisionKind.Block, default, 0, 0, false, false, 0);
                case RelayOverflow.DropNewest:
                    return Refuse(IngressDropReason.Backpressure);
                case RelayOverflow.DropOldest:
                    scope.HasWindow = false;
                    scope.Window = default!;
                    scope.WindowDepth = 0;
                    break;
                case RelayOverflow.Conflate:
                case RelayOverflow.Spill:
                    // Conflate IS the bound; Spill degrades to it until a durable tail is wired,
                    // exactly as RelayCell does. Both are now named rather than absorbed.
                    break;
                default:
                    // `Overflow` is a caller-supplied policy value that survives an unchecked cast
                    // from int. Absorbing an unknown one into "conflate" silently disables the
                    // bound the caller asked for — the scope grows without limit and reports no
                    // backpressure at all.
                    throw new ArgumentOutOfRangeException(
                        nameof(envelope),
                        _policy.Overflow,
                        "Unknown ingress overflow policy.");
            }
        }

        // 8. Merge, then flush every buffered successor this delivery unblocked. ONE invalidation
        //    covers the whole flush: readers observe the coalesced window, never a partial replay.
        var conflated = MergeInto(scope, envelope.Payload, envelope.StampedAt);
        scope.DeliveredThrough = envelope.Sequence;
        scope.Lifecycle = IngressLifecycle.Live;
        scope.ConsecutiveErrors = 0;
        var deliveredThrough = envelope.Sequence;

        while (true)
        {
            var next = scope.NextExpected;
            if (!scope.Pending.TryGetValue(next, out var buffered)) break;
            scope.Pending.Remove(next);
            conflated |= MergeInto(scope, buffered.Key, buffered.Value);
            scope.DeliveredThrough = next;
            deliveredThrough = next;
        }

        return new Decision(
            DecisionKind.Delivered,
            default,
            0,
            deliveredThrough,
            conflated,
            handoff,
            handoffFrom);

        static Decision Refuse(IngressDropReason reason) =>
            new(DecisionKind.Refuse, reason, 0, 0, false, false, 0);
    }

    /// <summary>
    /// Merges one payload into a scope's hot head and reports whether it coalesced with an
    /// existing window.
    /// </summary>
    private bool MergeInto(Scope scope, TValue payload, long stampedAt)
    {
        bool conflated;
        if (scope.HasWindow)
        {
            scope.Window = _merge.Merge(scope.Window, payload);
            conflated = true;
        }
        else
        {
            scope.Window = payload;
            scope.HasWindow = true;
            conflated = false;
        }

        scope.WindowDepth++;
        scope.StampedAt = Math.Max(scope.StampedAt, stampedAt);
        return conflated;
    }

    private IngressReceiptChannel PushReceipt(
        TKey key,
        long generation,
        long? sequence,
        IngressReceiptOutcome outcome)
    {
        var receipt = new IngressReceipt<TKey>(
            _nextReceiptOffset++,
            key,
            generation,
            sequence,
            outcome);
        _receipts.AddLast(receipt);
        while (_receipts.Count > _policy.ReceiptCapacity) _receipts.RemoveFirst();
        return receipt.Channel;
    }
}
