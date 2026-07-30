// AsyncIngressCell — the AsyncContext flavor of IngressCell (spec tag: designimplementtransport).
//
// Same flavor-neutral IngressCore, same four reader kinds per keyed scope, same three receipt
// channels, same normative admission order. Only the graph differs.
//
// ADMISSION IS NOT ASYNC-COLOURED. Whether an envelope is admissible is a function of the fence,
// the watermark, the reorder buffer, and the observed clock — state the graph does not own and
// nothing has to await. Every op below (Admit / Drain / Suspend / Reconnect / Close / Fail / Tick)
// is therefore SYNCHRONOUS and returns a plain value, and every reader body resolves with
// Task.FromResult: nothing in this primitive awaits. Awaiting belongs to the transport, and the
// transport is outside the primitive by construction.
//
// The one thing that is Task-typed is a reader READ, because on this binding an AsyncContext slot
// read is Task-typed by construction (AsyncComputed<T>.GetAsync). That is a property of the async
// graph, not of the ingress algebra; the divergence is recorded in AGENTS.md.
//
// Multi-root invalidation goes through AsyncContext.Batch, whose boundary is synchronous: the
// version writes inside queue their roots and the queued roots propagate once at the outermost
// exit, so one admission is one frontier walk over the whole dependent cone.

namespace Lazily;

/// <summary>
/// A keyed, lifecycle-scoped reactive ingress on the async graph: one admission plane per key, with
/// readiness, authority, and retry as derives rather than calls.
/// </summary>
/// <typeparam name="TKey">The scope identity type.</typeparam>
/// <typeparam name="TValue">The payload type folded under the merge policy.</typeparam>
public sealed class AsyncIngressCell<TKey, TValue>
    where TKey : notnull
{
    private sealed class ScopeReaders
    {
        internal required AsyncSource<int> ValueVersion { get; init; }
        internal required AsyncSource<int> ReadinessVersion { get; init; }
        internal required AsyncSource<int> AuthorityVersion { get; init; }
        internal required AsyncSource<int> RetryVersion { get; init; }
        internal required AsyncComputed<Optional<TValue>> Value { get; init; }
        internal required AsyncComputed<IngressReadiness> Readiness { get; init; }
        internal required AsyncComputed<IngressAuthority?> Authority { get; init; }
        internal required AsyncComputed<IngressRetry?> Retry { get; init; }
        internal int ValueTick;
        internal int ReadinessTick;
        internal int AuthorityTick;
        internal int RetryTick;
    }

    private readonly AsyncContext _ctx;
    private readonly IngressCore<TKey, TValue> _core;
    private readonly object _coreGate = new();
    private readonly object _scopesGate = new();
    private readonly Dictionary<TKey, ScopeReaders> _scopes = [];

    private readonly AsyncSource<int> _acceptedVersion;
    private readonly AsyncSource<int> _droppedVersion;
    private readonly AsyncSource<int> _errorVersion;
    private int _acceptedTick;
    private int _droppedTick;
    private int _errorTick;

    private readonly AsyncComputed<IReadOnlyList<IngressReceipt<TKey>>> _accepted;
    private readonly AsyncComputed<IReadOnlyList<IngressReceipt<TKey>>> _dropped;
    private readonly AsyncComputed<IReadOnlyList<IngressReceipt<TKey>>> _errors;

    private readonly AsyncSource<IngressTransportKind> _transportKind;
    private readonly AsyncSource<long> _pollInterval;
    private readonly AsyncComputed<IngressSchedule> _schedule;

    /// <summary>Builds an ingress over <paramref name="policy"/>, delivering as <paramref name="kind"/>.</summary>
    /// <param name="ctx">The owning async reactive scope.</param>
    /// <param name="policy">The bounds in force.</param>
    /// <param name="merge">The associative fold the hot window coalesces under.</param>
    /// <param name="kind">How envelopes reach this ingress.</param>
    /// <param name="pollInterval">The bounded poll period a polling transport would use.</param>
    public AsyncIngressCell(
        AsyncContext ctx,
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
        var kindCell = _transportKind;
        var pollCell = _pollInterval;
        _schedule = ctx.Computed(compute => Task.FromResult(
            IngressSchedule.ForKind(compute.Track(kindCell), compute.Track(pollCell))));
    }

    /// <summary>The bounds in force.</summary>
    public IngressPolicy Policy => _core.Policy;

    /// <summary>Every known scope key.</summary>
    public IReadOnlyList<TKey> ScopeKeys()
    {
        lock (_coreGate) return _core.ScopeKeys();
    }

    /// <summary>Non-reactive projection of a scope, for assertions and diagnostics.</summary>
    /// <param name="key">The scope to project.</param>
    public IngressScopeView? View(TKey key)
    {
        lock (_coreGate) return _core.View(key);
    }

    // --- ops (synchronous: admission is not async-coloured) ------------------

    /// <summary>Opens (or reopens) a keyed scope at <paramref name="generation"/>.</summary>
    /// <param name="key">The scope to open.</param>
    /// <param name="generation">The producer incarnation to open at.</param>
    public void Open(TKey key, long generation)
    {
        IngressChange<TKey> change;
        lock (_coreGate) change = _core.Open(key, generation);
        Apply(change);
    }

    /// <summary>Admits one decoded envelope.</summary>
    /// <param name="envelope">The envelope to admit.</param>
    public IngressAdmission Admit(IngressEnvelope<TKey, TValue> envelope)
    {
        IngressChange<TKey> change;
        IngressAdmission admission;
        lock (_coreGate) (change, admission) = _core.Admit(envelope);
        Apply(change);
        return admission;
    }

    /// <summary>Suspends a scope, retaining its watermark.</summary>
    /// <param name="key">The scope to suspend.</param>
    /// <returns>The replay request a reconnect will need, or null when already suspended.</returns>
    public ReplayRequest? Suspend(TKey key)
    {
        IngressChange<TKey> change;
        ReplayRequest? replay;
        lock (_coreGate) (change, replay) = _core.Suspend(key);
        Apply(change);
        return replay;
    }

    /// <summary>Reconnects a scope at <paramref name="generation"/>, clearing its error streak.</summary>
    /// <param name="key">The scope to reconnect.</param>
    /// <param name="generation">The producer incarnation to resume under.</param>
    public ReplayRequest Reconnect(TKey key, long generation)
    {
        IngressChange<TKey> change;
        ReplayRequest replay;
        lock (_coreGate) (change, replay) = _core.Reconnect(key, generation);
        Apply(change);
        return replay;
    }

    /// <summary>Closes a scope. It admits nothing and claims no authority until reopened.</summary>
    /// <param name="key">The scope to close.</param>
    public void Close(TKey key)
    {
        IngressChange<TKey> change;
        lock (_coreGate) change = _core.Close(key);
        Apply(change);
    }

    /// <summary>Records a transport/decode failure, deepening the scope's backoff.</summary>
    /// <param name="key">The scope the failure is attributed to.</param>
    /// <param name="error">What went wrong.</param>
    public void Fail(TKey key, IngressError error)
    {
        IngressChange<TKey> change;
        lock (_coreGate) change = _core.Fail(key, error);
        Apply(change);
    }

    /// <summary>Advances logical time. Only scopes that crossed the horizon are invalidated.</summary>
    /// <param name="now">The new logical now.</param>
    public void Tick(long now)
    {
        IngressChange<TKey> change;
        lock (_coreGate) change = _core.Tick(now);
        Apply(change);
    }

    /// <summary>Drains a scope's coalesced window.</summary>
    /// <param name="key">The scope to drain.</param>
    public Optional<TValue> Drain(TKey key)
    {
        IngressChange<TKey> change;
        Optional<TValue> drained;
        lock (_coreGate) (change, drained) = _core.Drain(key);
        Apply(change);
        return drained;
    }

    /// <summary>
    /// Admits everything <paramref name="transport"/> has decoded, then asks it to replay any gap
    /// still open.
    /// </summary>
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
            if (View(key) is { HasGap: true } view)
                transport.RequestReplay(key, new ReplayRequest(view.Generation, view.ResumeFrom));
        }

        return outcomes;
    }

    // --- reactive reads -----------------------------------------------------

    /// <summary>Reactive read: the coalesced window awaiting drain.</summary>
    /// <param name="key">The scope to read.</param>
    public Task<Optional<TValue>> ValueAsync(TKey key) => EnsureReaders(key).Value.GetAsync();

    /// <summary>Reactive read: derived readiness.</summary>
    /// <param name="key">The scope to read.</param>
    public Task<IngressReadiness> ReadinessAsync(TKey key) => EnsureReaders(key).Readiness.GetAsync();

    /// <summary>Reactive read: derived authority.</summary>
    /// <param name="key">The scope to read.</param>
    public Task<IngressAuthority?> AuthorityAsync(TKey key) => EnsureReaders(key).Authority.GetAsync();

    /// <summary>Reactive read: derived retry decision.</summary>
    /// <param name="key">The scope to read.</param>
    public Task<IngressRetry?> RetryAsync(TKey key) => EnsureReaders(key).Retry.GetAsync();

    /// <summary>Reactive read: accepted receipts, oldest first.</summary>
    public Task<IReadOnlyList<IngressReceipt<TKey>>> AcceptedAsync() => _accepted.GetAsync();

    /// <summary>Reactive read: dropped receipts, oldest first.</summary>
    public Task<IReadOnlyList<IngressReceipt<TKey>>> DroppedAsync() => _dropped.GetAsync();

    /// <summary>Reactive read: error receipts, oldest first.</summary>
    public Task<IReadOnlyList<IngressReceipt<TKey>>> ErrorsAsync() => _errors.GetAsync();

    /// <summary>Reactive read: the derived delivery schedule.</summary>
    public Task<IngressSchedule> ScheduleAsync() => _schedule.GetAsync();

    /// <summary>Retunes the transport live: every schedule dependent reacts.</summary>
    /// <param name="kind">The new transport kind.</param>
    public void SetTransport(IngressTransportKind kind) => _transportKind.Set(kind);

    /// <summary>Retunes the poll bound live.</summary>
    /// <param name="interval">The new poll period.</param>
    public void SetPollInterval(long interval) => _pollInterval.Set(interval);

    // --- handles ------------------------------------------------------------

    /// <summary>Handle to a scope's coalesced-window reader.</summary>
    /// <param name="key">The scope to read.</param>
    public AsyncComputed<Optional<TValue>> ValueHandle(TKey key) => EnsureReaders(key).Value;

    /// <summary>Handle to a scope's readiness reader.</summary>
    /// <param name="key">The scope to read.</param>
    public AsyncComputed<IngressReadiness> ReadinessHandle(TKey key) => EnsureReaders(key).Readiness;

    /// <summary>Handle to a scope's authority reader.</summary>
    /// <param name="key">The scope to read.</param>
    public AsyncComputed<IngressAuthority?> AuthorityHandle(TKey key) => EnsureReaders(key).Authority;

    /// <summary>Handle to a scope's retry reader.</summary>
    /// <param name="key">The scope to read.</param>
    public AsyncComputed<IngressRetry?> RetryHandle(TKey key) => EnsureReaders(key).Retry;

    /// <summary>Handle to the accepted-receipt reader.</summary>
    public AsyncComputed<IReadOnlyList<IngressReceipt<TKey>>> AcceptedHandle => _accepted;

    /// <summary>Handle to the dropped-receipt reader.</summary>
    public AsyncComputed<IReadOnlyList<IngressReceipt<TKey>>> DroppedHandle => _dropped;

    /// <summary>Handle to the error-receipt reader.</summary>
    public AsyncComputed<IReadOnlyList<IngressReceipt<TKey>>> ErrorsHandle => _errors;

    /// <summary>Handle to the schedule reader.</summary>
    public AsyncComputed<IngressSchedule> ScheduleHandle => _schedule;

    // --- internals ----------------------------------------------------------

    private AsyncComputed<IReadOnlyList<IngressReceipt<TKey>>> ReceiptReader(
        AsyncSource<int> version,
        IngressReceiptChannel channel) =>
        _ctx.Computed<IReadOnlyList<IngressReceipt<TKey>>>(compute =>
        {
            compute.Track(version);
            lock (_coreGate) return Task.FromResult(_core.Receipts(channel));
        });

    private ScopeReaders EnsureReaders(TKey key)
    {
        Guard.NotNull(key, nameof(key));
        lock (_scopesGate)
        {
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
                Value = _ctx.Computed(compute =>
                {
                    compute.Track(valueVersion);
                    lock (_coreGate) return Task.FromResult(_core.Peek(key));
                }),
                Readiness = _ctx.Computed(compute =>
                {
                    compute.Track(readinessVersion);
                    lock (_coreGate) return Task.FromResult(_core.Readiness(key));
                }),
                Authority = _ctx.Computed<IngressAuthority?>(compute =>
                {
                    compute.Track(authorityVersion);
                    lock (_coreGate) return Task.FromResult(_core.Authority(key));
                }),
                Retry = _ctx.Computed<IngressRetry?>(compute =>
                {
                    compute.Track(retryVersion);
                    lock (_coreGate) return Task.FromResult(_core.Retry(key));
                }),
            };
            _scopes.Add(key, readers);
            return readers;
        }
    }

    /// <summary>
    /// Applies one core-reported invalidation set. The core lock is already released, and every
    /// affected reader is bumped inside ONE batch so the fan-out is a single frontier walk over the
    /// whole dependent cone.
    /// </summary>
    private void Apply(IngressChange<TKey> change)
    {
        if (change.IsEmpty) return;

        var targets = new List<KeyValuePair<ScopeReaders, IngressScopeChange>>(change.Scopes.Count);
        foreach (var entry in change.Scopes)
        {
            targets.Add(new KeyValuePair<ScopeReaders, IngressScopeChange>(
                EnsureReaders(entry.Key),
                entry.Value));
        }

        _ctx.Batch(() =>
        {
            foreach (var target in targets)
            {
                var readers = target.Key;
                var scopeChange = target.Value;
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
