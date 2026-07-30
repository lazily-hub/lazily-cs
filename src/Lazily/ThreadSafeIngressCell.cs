// ThreadSafeIngressCell — the Send+Sync-equivalent flavor of IngressCell
// (spec tag: designimplementtransport).
//
// Same flavor-neutral IngressCore, same four reader kinds per keyed scope, same three receipt
// channels. This shell adds only the reactivity, minted on THIS context's graph — because the
// family's claim is that all three flavors obey ONE contract.
//
// LOCK DISCIPLINE. Three locks exist and are always taken in this order, never any other:
//
//   1. _scopesGate — the per-key reader-handle table.
//   2. the context  — taken by ThreadSafeContext.WithLock / Batch.
//   3. _coreGate    — the admission algebra.
//
// A reader's compute body runs INSIDE the context lock and takes _coreGate, which is
// context -> core. An op therefore must NOT hold _coreGate while reaching the context: every op
// below scopes its core lock to a block that ends before Apply is called. That is why Apply is a
// separate step taking an already-computed IngressChange rather than something an op does inline.
//
// MULTI-ROOT INVALIDATION GOES THROUGH Batch(). One admission can dirty a scope's value,
// readiness, authority, and retry plus a receipt channel; clearing them one at a time is one
// frontier walk each, and a concurrent reader can interleave and see the new value with the old
// authority — precisely the partial fan-out a generation handoff must never expose. Reader handles
// are also minted BEFORE the batch opens, so the batch body never re-enters the table lock in the
// opposite order.

namespace Lazily;

/// <summary>
/// A lock-serialized keyed, lifecycle-scoped reactive ingress: one admission plane per key, with
/// readiness, authority, and retry as derives rather than calls.
/// </summary>
/// <typeparam name="TKey">The scope identity type.</typeparam>
/// <typeparam name="TValue">The payload type folded under the merge policy.</typeparam>
public sealed class ThreadSafeIngressCell<TKey, TValue>
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

    private readonly ThreadSafeContext _ctx;
    private readonly IngressCore<TKey, TValue> _core;
    private readonly object _coreGate = new();
    private readonly object _scopesGate = new();
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
    /// <param name="ctx">The owning lock-serialized reactive scope.</param>
    /// <param name="policy">The bounds in force.</param>
    /// <param name="merge">The associative fold the hot window coalesces under.</param>
    /// <param name="kind">How envelopes reach this ingress.</param>
    /// <param name="pollInterval">The bounded poll period a polling transport would use.</param>
    public ThreadSafeIngressCell(
        ThreadSafeContext ctx,
        IngressPolicy policy,
        MergePolicy<TValue> merge,
        IngressTransportKind kind,
        long pollInterval)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _ctx = ctx;
        _core = new IngressCore<TKey, TValue>(policy, merge);

        Source<int>? acceptedVersion = null;
        Source<int>? droppedVersion = null;
        Source<int>? errorVersion = null;
        Computed<IReadOnlyList<IngressReceipt<TKey>>>? accepted = null;
        Computed<IReadOnlyList<IngressReceipt<TKey>>>? dropped = null;
        Computed<IReadOnlyList<IngressReceipt<TKey>>>? errors = null;
        Source<IngressTransportKind>? transportKind = null;
        Source<long>? poll = null;
        Computed<IngressSchedule>? schedule = null;
        ctx.WithLock(inner =>
        {
            acceptedVersion = inner.Source(0);
            droppedVersion = inner.Source(0);
            errorVersion = inner.Source(0);
            accepted = ReceiptReader(inner, acceptedVersion, IngressReceiptChannel.Accepted);
            dropped = ReceiptReader(inner, droppedVersion, IngressReceiptChannel.Dropped);
            errors = ReceiptReader(inner, errorVersion, IngressReceiptChannel.Error);
            transportKind = inner.Source(kind);
            poll = inner.Source(pollInterval);
            var kindCell = transportKind;
            var pollCell = poll;
            schedule = inner.Computed(cx =>
                IngressSchedule.ForKind(cx.Get(kindCell), cx.Get(pollCell)));
        });

        _acceptedVersion = acceptedVersion!;
        _droppedVersion = droppedVersion!;
        _errorVersion = errorVersion!;
        _accepted = accepted!;
        _dropped = dropped!;
        _errors = errors!;
        _transportKind = transportKind!;
        _pollInterval = poll!;
        _schedule = schedule!;
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

    // --- ops ----------------------------------------------------------------

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
    public Optional<TValue> Value(TKey key)
    {
        var readers = EnsureReaders(key);
        return _ctx.WithLock(inner => readers.Value.Get(inner));
    }

    /// <summary>Tracked read of the coalesced window, from inside the context lock.</summary>
    /// <param name="key">The scope to read.</param>
    /// <param name="ops">The read surface.</param>
    public Optional<TValue> Value(TKey key, IComputeOps ops) => EnsureReaders(key).Value.Get(ops);

    /// <summary>Reactive read: derived readiness.</summary>
    /// <param name="key">The scope to read.</param>
    public IngressReadiness Readiness(TKey key)
    {
        var readers = EnsureReaders(key);
        return _ctx.WithLock(inner => readers.Readiness.Get(inner));
    }

    /// <summary>Tracked read of derived readiness, from inside the context lock.</summary>
    /// <param name="key">The scope to read.</param>
    /// <param name="ops">The read surface.</param>
    public IngressReadiness Readiness(TKey key, IComputeOps ops) =>
        EnsureReaders(key).Readiness.Get(ops);

    /// <summary>Reactive read: derived authority.</summary>
    /// <param name="key">The scope to read.</param>
    public IngressAuthority? Authority(TKey key)
    {
        var readers = EnsureReaders(key);
        return _ctx.WithLock(inner => readers.Authority.Get(inner));
    }

    /// <summary>Tracked read of derived authority, from inside the context lock.</summary>
    /// <param name="key">The scope to read.</param>
    /// <param name="ops">The read surface.</param>
    public IngressAuthority? Authority(TKey key, IComputeOps ops) =>
        EnsureReaders(key).Authority.Get(ops);

    /// <summary>Reactive read: derived retry decision.</summary>
    /// <param name="key">The scope to read.</param>
    public IngressRetry? Retry(TKey key)
    {
        var readers = EnsureReaders(key);
        return _ctx.WithLock(inner => readers.Retry.Get(inner));
    }

    /// <summary>Tracked read of the derived retry decision, from inside the context lock.</summary>
    /// <param name="key">The scope to read.</param>
    /// <param name="ops">The read surface.</param>
    public IngressRetry? Retry(TKey key, IComputeOps ops) => EnsureReaders(key).Retry.Get(ops);

    /// <summary>Reactive read: accepted receipts, oldest first.</summary>
    public IReadOnlyList<IngressReceipt<TKey>> Accepted() =>
        _ctx.WithLock(inner => _accepted.Get(inner));

    /// <summary>Reactive read: dropped receipts, oldest first.</summary>
    public IReadOnlyList<IngressReceipt<TKey>> Dropped() =>
        _ctx.WithLock(inner => _dropped.Get(inner));

    /// <summary>Reactive read: error receipts, oldest first.</summary>
    public IReadOnlyList<IngressReceipt<TKey>> Errors() =>
        _ctx.WithLock(inner => _errors.Get(inner));

    /// <summary>Reactive read: the derived delivery schedule.</summary>
    public IngressSchedule Schedule() => _ctx.WithLock(inner => _schedule.Get(inner));

    /// <summary>Retunes the transport live: every schedule dependent reacts.</summary>
    /// <param name="kind">The new transport kind.</param>
    public void SetTransport(IngressTransportKind kind) => _ctx.Set(_transportKind, kind);

    /// <summary>Retunes the poll bound live.</summary>
    /// <param name="interval">The new poll period.</param>
    public void SetPollInterval(long interval) => _ctx.Set(_pollInterval, interval);

    // --- handles ------------------------------------------------------------

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
        Context inner,
        Source<int> version,
        IngressReceiptChannel channel) =>
        inner.Computed<IReadOnlyList<IngressReceipt<TKey>>>(cx =>
        {
            cx.Get(version);
            lock (_coreGate) return _core.Receipts(channel);
        });

    private ScopeReaders EnsureReaders(TKey key)
    {
        Guard.NotNull(key, nameof(key));
        lock (_scopesGate)
        {
            if (_scopes.TryGetValue(key, out var existing)) return existing;
            var readers = _ctx.WithLock(inner =>
            {
                var valueVersion = inner.Source(0);
                var readinessVersion = inner.Source(0);
                var authorityVersion = inner.Source(0);
                var retryVersion = inner.Source(0);
                return new ScopeReaders
                {
                    ValueVersion = valueVersion,
                    ReadinessVersion = readinessVersion,
                    AuthorityVersion = authorityVersion,
                    RetryVersion = retryVersion,
                    Value = inner.Computed(cx =>
                    {
                        cx.Get(valueVersion);
                        lock (_coreGate) return _core.Peek(key);
                    }),
                    Readiness = inner.Computed(cx =>
                    {
                        cx.Get(readinessVersion);
                        lock (_coreGate) return _core.Readiness(key);
                    }),
                    Authority = inner.Computed(cx =>
                    {
                        cx.Get(authorityVersion);
                        lock (_coreGate) return _core.Authority(key);
                    }),
                    Retry = inner.Computed(cx =>
                    {
                        cx.Get(retryVersion);
                        lock (_coreGate) return _core.Retry(key);
                    }),
                };
            });
            _scopes.Add(key, readers);
            return readers;
        }
    }

    /// <summary>
    /// Applies one core-reported invalidation set. The core lock is already released, and every
    /// affected reader is bumped inside ONE batch so the fan-out is a single frontier walk.
    /// </summary>
    private void Apply(IngressChange<TKey> change)
    {
        if (change.IsEmpty) return;

        // Mint the reader handles BEFORE the batch opens: EnsureReaders takes the table lock and
        // then the context lock, so doing it inside the batch would invert that order.
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
