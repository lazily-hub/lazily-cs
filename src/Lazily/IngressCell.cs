// IngressCell — the single-threaded flavor of the transport-agnostic reactive ingress family
// (spec tag: designimplementtransport).
//
// The admission algebra lives in the flavor-neutral IngressCore; this shell adds only the
// reactivity — four memoized Computeds per keyed scope plus three receipt readers and a derived
// schedule, minted on THIS context's graph.
//
// Readiness, authority, and retry are DERIVES, not refresh calls. Nothing here polls a connection
// to find out whether it is healthy: a consumer that reads readiness is a graph dependent of
// exactly the transitions that can change it, and a transition that cannot (a buffered
// out-of-order envelope, a tick inside the freshness horizon) invalidates nothing.
//
// There are no observers. Each reader kind is a Computed gated by its own version Source, and an
// invalidation is a version bump — the same idiom TopicCell and WorkQueueCell use. Anything that
// survived an invalidation would not be a graph edge, so no listener list exists to keep one.

namespace Lazily;

/// <summary>
/// A keyed, lifecycle-scoped reactive ingress: one admission plane per key, with readiness,
/// authority, and retry as derives rather than calls.
/// </summary>
/// <typeparam name="TKey">The scope identity type.</typeparam>
/// <typeparam name="TValue">The payload type folded under the merge policy.</typeparam>
public sealed class IngressCell<TKey, TValue>
    where TKey : notnull
{
    private sealed class ScopeReaders
    {
        internal required Source<int> ValueVersion { get; init; }
        internal required Source<int> ReadinessVersion { get; init; }
        internal required Source<int> AuthorityVersion { get; init; }
        internal required Source<int> RetryVersion { get; init; }
        internal required Computed<Optional<TValue>> Value { get; init; }
        internal required Computed<IngressReadiness> Readiness { get; init; }
        internal required Computed<IngressAuthority?> Authority { get; init; }
        internal required Computed<IngressRetry?> Retry { get; init; }
        internal int ValueTick;
        internal int ReadinessTick;
        internal int AuthorityTick;
        internal int RetryTick;
    }

    private readonly Context _ctx;
    private readonly IngressCore<TKey, TValue> _core;
    private readonly Dictionary<TKey, ScopeReaders> _scopes = [];

    private readonly Source<int> _acceptedVersion;
    private readonly Source<int> _droppedVersion;
    private readonly Source<int> _errorVersion;
    private int _acceptedTick;
    private int _droppedTick;
    private int _errorTick;

    private readonly Computed<IReadOnlyList<IngressReceipt<TKey>>> _accepted;
    private readonly Computed<IReadOnlyList<IngressReceipt<TKey>>> _dropped;
    private readonly Computed<IReadOnlyList<IngressReceipt<TKey>>> _errors;

    private readonly Source<IngressTransportKind> _transportKind;
    private readonly Source<long> _pollInterval;
    private readonly Computed<IngressSchedule> _schedule;

    /// <summary>Builds an ingress over <paramref name="policy"/>, delivering as <paramref name="kind"/>.</summary>
    /// <remarks>
    /// <paramref name="pollInterval"/> is retained even for an event channel so a later
    /// <see cref="SetTransport"/> to bounded polling has a bound to fall back to rather than
    /// inventing one.
    /// </remarks>
    /// <param name="ctx">The owning reactive scope.</param>
    /// <param name="policy">The bounds in force.</param>
    /// <param name="merge">The associative fold the hot window coalesces under.</param>
    /// <param name="kind">How envelopes reach this ingress.</param>
    /// <param name="pollInterval">The bounded poll period a polling transport would use.</param>
    public IngressCell(
        Context ctx,
        IngressPolicy policy,
        MergePolicy<TValue> merge,
        IngressTransportKind kind,
        long pollInterval)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _ctx = ctx;
        _core = new IngressCore<TKey, TValue>(policy, merge);

        _acceptedVersion = ctx.Source(0);
        _droppedVersion = ctx.Source(0);
        _errorVersion = ctx.Source(0);
        _accepted = ReceiptReader(_acceptedVersion, IngressReceiptChannel.Accepted);
        _dropped = ReceiptReader(_droppedVersion, IngressReceiptChannel.Dropped);
        _errors = ReceiptReader(_errorVersion, IngressReceiptChannel.Error);

        _transportKind = ctx.Source(kind);
        _pollInterval = ctx.Source(pollInterval);
        _schedule = ctx.Computed(cx =>
            IngressSchedule.ForKind(cx.Get(_transportKind), cx.Get(_pollInterval)));
    }

    /// <summary>The bounds in force.</summary>
    public IngressPolicy Policy => _core.Policy;

    /// <summary>Every known scope key.</summary>
    public IReadOnlyList<TKey> ScopeKeys() => _core.ScopeKeys();

    /// <summary>Non-reactive projection of a scope, for assertions and diagnostics.</summary>
    /// <param name="key">The scope to project.</param>
    public IngressScopeView? View(TKey key) => _core.View(key);

    // --- ops ----------------------------------------------------------------

    /// <summary>Opens (or reopens) a keyed scope at <paramref name="generation"/>.</summary>
    /// <param name="key">The scope to open.</param>
    /// <param name="generation">The producer incarnation to open at.</param>
    public void Open(TKey key, long generation) => Apply(_core.Open(key, generation));

    /// <summary>Admits one decoded envelope.</summary>
    /// <param name="envelope">The envelope to admit.</param>
    public IngressAdmission Admit(IngressEnvelope<TKey, TValue> envelope)
    {
        var (change, admission) = _core.Admit(envelope);
        Apply(change);
        return admission;
    }

    /// <summary>Suspends a scope, retaining its watermark.</summary>
    /// <param name="key">The scope to suspend.</param>
    /// <returns>The replay request a reconnect will need, or null when already suspended.</returns>
    public ReplayRequest? Suspend(TKey key)
    {
        var (change, replay) = _core.Suspend(key);
        Apply(change);
        return replay;
    }

    /// <summary>Reconnects a scope at <paramref name="generation"/>, clearing its error streak.</summary>
    /// <param name="key">The scope to reconnect.</param>
    /// <param name="generation">The producer incarnation to resume under.</param>
    public ReplayRequest Reconnect(TKey key, long generation)
    {
        var (change, replay) = _core.Reconnect(key, generation);
        Apply(change);
        return replay;
    }

    /// <summary>Closes a scope. It admits nothing and claims no authority until reopened.</summary>
    /// <param name="key">The scope to close.</param>
    public void Close(TKey key) => Apply(_core.Close(key));

    /// <summary>Records a transport/decode failure, deepening the scope's backoff.</summary>
    /// <param name="key">The scope the failure is attributed to.</param>
    /// <param name="error">What went wrong.</param>
    public void Fail(TKey key, IngressError error) => Apply(_core.Fail(key, error));

    /// <summary>Advances logical time. Only scopes that crossed the horizon are invalidated.</summary>
    /// <param name="now">The new logical now.</param>
    public void Tick(long now) => Apply(_core.Tick(now));

    /// <summary>Drains a scope's coalesced window.</summary>
    /// <param name="key">The scope to drain.</param>
    public Optional<TValue> Drain(TKey key)
    {
        var (change, drained) = _core.Drain(key);
        Apply(change);
        return drained;
    }

    /// <summary>
    /// Admits everything <paramref name="transport"/> has decoded, then asks it to replay any gap
    /// still open.
    /// </summary>
    /// <remarks>
    /// The only method that touches a transport, and it makes no decision of its own: the gap it
    /// replays is the one the algebra reports.
    /// </remarks>
    /// <param name="transport">The decoded envelope source.</param>
    /// <returns>The admission outcomes, in arrival order.</returns>
    public IReadOnlyList<IngressAdmission> Pump(IIngressTransport<TKey, TValue> transport)
    {
        Guard.NotNull(transport, nameof(transport));
        var batch = transport.Drain();
        var outcomes = new List<IngressAdmission>(batch.Count);
        var touched = new List<TKey>();
        foreach (var envelope in batch)
        {
            outcomes.Add(Admit(envelope));
            if (!touched.Contains(envelope.Key)) touched.Add(envelope.Key);
        }

        foreach (var key in touched)
        {
            if (_core.View(key) is { HasGap: true } view)
                transport.RequestReplay(key, new ReplayRequest(view.Generation, view.ResumeFrom));
        }

        return outcomes;
    }

    // --- reactive reads -----------------------------------------------------

    /// <summary>Reactive read: the coalesced window awaiting drain.</summary>
    /// <param name="key">The scope to read.</param>
    public Optional<TValue> Value(TKey key) => EnsureReaders(key).Value.Get();

    /// <summary>Tracked read of the coalesced window.</summary>
    /// <param name="key">The scope to read.</param>
    /// <param name="ops">The read surface.</param>
    public Optional<TValue> Value(TKey key, IComputeOps ops) => EnsureReaders(key).Value.Get(ops);

    /// <summary>Reactive read: derived readiness.</summary>
    /// <param name="key">The scope to read.</param>
    public IngressReadiness Readiness(TKey key) => EnsureReaders(key).Readiness.Get();

    /// <summary>Tracked read of derived readiness.</summary>
    /// <param name="key">The scope to read.</param>
    /// <param name="ops">The read surface.</param>
    public IngressReadiness Readiness(TKey key, IComputeOps ops) =>
        EnsureReaders(key).Readiness.Get(ops);

    /// <summary>Reactive read: derived authority.</summary>
    /// <param name="key">The scope to read.</param>
    public IngressAuthority? Authority(TKey key) => EnsureReaders(key).Authority.Get();

    /// <summary>Tracked read of derived authority.</summary>
    /// <param name="key">The scope to read.</param>
    /// <param name="ops">The read surface.</param>
    public IngressAuthority? Authority(TKey key, IComputeOps ops) =>
        EnsureReaders(key).Authority.Get(ops);

    /// <summary>Reactive read: derived retry decision.</summary>
    /// <param name="key">The scope to read.</param>
    public IngressRetry? Retry(TKey key) => EnsureReaders(key).Retry.Get();

    /// <summary>Tracked read of the derived retry decision.</summary>
    /// <param name="key">The scope to read.</param>
    /// <param name="ops">The read surface.</param>
    public IngressRetry? Retry(TKey key, IComputeOps ops) => EnsureReaders(key).Retry.Get(ops);

    /// <summary>Reactive read: accepted receipts, oldest first.</summary>
    public IReadOnlyList<IngressReceipt<TKey>> Accepted() => _accepted.Get();

    /// <summary>Reactive read: dropped receipts, oldest first.</summary>
    public IReadOnlyList<IngressReceipt<TKey>> Dropped() => _dropped.Get();

    /// <summary>Reactive read: error receipts, oldest first.</summary>
    public IReadOnlyList<IngressReceipt<TKey>> Errors() => _errors.Get();

    /// <summary>Reactive read: the derived delivery schedule.</summary>
    public IngressSchedule Schedule() => _schedule.Get();

    /// <summary>Retunes the transport live: every schedule dependent reacts.</summary>
    /// <param name="kind">The new transport kind.</param>
    public void SetTransport(IngressTransportKind kind) => _transportKind.Set(kind);

    /// <summary>Retunes the poll bound live.</summary>
    /// <param name="interval">The new poll period.</param>
    public void SetPollInterval(long interval) => _pollInterval.Set(interval);

    // --- handles (for composing further derives, and for cache-validity probes) ----

    /// <summary>Handle to a scope's coalesced-window reader.</summary>
    /// <param name="key">The scope to read.</param>
    public Computed<Optional<TValue>> ValueHandle(TKey key) => EnsureReaders(key).Value;

    /// <summary>Handle to a scope's readiness reader.</summary>
    /// <param name="key">The scope to read.</param>
    public Computed<IngressReadiness> ReadinessHandle(TKey key) => EnsureReaders(key).Readiness;

    /// <summary>Handle to a scope's authority reader.</summary>
    /// <param name="key">The scope to read.</param>
    public Computed<IngressAuthority?> AuthorityHandle(TKey key) => EnsureReaders(key).Authority;

    /// <summary>Handle to a scope's retry reader.</summary>
    /// <param name="key">The scope to read.</param>
    public Computed<IngressRetry?> RetryHandle(TKey key) => EnsureReaders(key).Retry;

    /// <summary>Handle to the accepted-receipt reader.</summary>
    public Computed<IReadOnlyList<IngressReceipt<TKey>>> AcceptedHandle => _accepted;

    /// <summary>Handle to the dropped-receipt reader.</summary>
    public Computed<IReadOnlyList<IngressReceipt<TKey>>> DroppedHandle => _dropped;

    /// <summary>Handle to the error-receipt reader.</summary>
    public Computed<IReadOnlyList<IngressReceipt<TKey>>> ErrorsHandle => _errors;

    /// <summary>Handle to the schedule reader.</summary>
    public Computed<IngressSchedule> ScheduleHandle => _schedule;

    // --- internals ----------------------------------------------------------

    private Computed<IReadOnlyList<IngressReceipt<TKey>>> ReceiptReader(
        Source<int> version,
        IngressReceiptChannel channel) =>
        _ctx.Computed<IReadOnlyList<IngressReceipt<TKey>>>(cx =>
        {
            cx.Get(version);
            return _core.Receipts(channel);
        });

    /// <summary>
    /// Mints (or returns) one scope's four readers. Idempotent, so a consumer may hold a handle for
    /// a key that has not opened yet.
    /// </summary>
    private ScopeReaders EnsureReaders(TKey key)
    {
        Guard.NotNull(key, nameof(key));
        if (_scopes.TryGetValue(key, out var existing)) return existing;

        var valueVersion = _ctx.Source(0);
        var readinessVersion = _ctx.Source(0);
        var authorityVersion = _ctx.Source(0);
        var retryVersion = _ctx.Source(0);
        var readers = new ScopeReaders
        {
            ValueVersion = valueVersion,
            ReadinessVersion = readinessVersion,
            AuthorityVersion = authorityVersion,
            RetryVersion = retryVersion,
            Value = _ctx.Computed(cx =>
            {
                cx.Get(valueVersion);
                return _core.Peek(key);
            }),
            Readiness = _ctx.Computed(cx =>
            {
                cx.Get(readinessVersion);
                return _core.Readiness(key);
            }),
            Authority = _ctx.Computed(cx =>
            {
                cx.Get(authorityVersion);
                return _core.Authority(key);
            }),
            Retry = _ctx.Computed(cx =>
            {
                cx.Get(retryVersion);
                return _core.Retry(key);
            }),
        };
        _scopes.Add(key, readers);
        return readers;
    }

    /// <summary>
    /// Applies one core-reported invalidation set inside a single batch.
    /// </summary>
    /// <remarks>
    /// One frontier walk, so no reader observes a partial fan-out — a generation handoff must never
    /// be visible as "new value, old authority".
    /// </remarks>
    private void Apply(IngressChange<TKey> change)
    {
        if (change.IsEmpty) return;
        _ctx.Batch(() =>
        {
            foreach (var entry in change.Scopes)
            {
                var readers = EnsureReaders(entry.Key);
                var scopeChange = entry.Value;
                if (scopeChange.Value) readers.ValueVersion.Set(++readers.ValueTick);
                if (scopeChange.Readiness) readers.ReadinessVersion.Set(++readers.ReadinessTick);
                if (scopeChange.Authority) readers.AuthorityVersion.Set(++readers.AuthorityTick);
                if (scopeChange.Retry) readers.RetryVersion.Set(++readers.RetryTick);
            }

            if (change.AcceptedReceipts) _acceptedVersion.Set(++_acceptedTick);
            if (change.DroppedReceipts) _droppedVersion.Set(++_droppedTick);
            if (change.ErrorReceipts) _errorVersion.Set(++_errorTick);
        });
    }
}
