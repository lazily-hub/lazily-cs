using System;

namespace Lazily;

/// <summary>The quantity measured by a relay backpressure bound.</summary>
public enum BoundDimension
{
    /// <summary>Number of ingress operations merged into the current hot window.</summary>
    Count,

    /// <summary>Encoded byte size.</summary>
    Bytes,

    /// <summary>Number of distinct keys.</summary>
    Keys,

    /// <summary>Age under a logical clock.</summary>
    Age,
}

/// <summary>Action applied when a relay reaches its high-water mark.</summary>
public enum RelayOverflow
{
    /// <summary>Refuse ingress so the producer observes backpressure.</summary>
    Block,

    /// <summary>Discard the accumulated window and start a new one from the incoming operation.</summary>
    DropOldest,

    /// <summary>Discard the incoming operation.</summary>
    DropNewest,

    /// <summary>Continue merging because the algebraic summary is the bound.</summary>
    Conflate,

    /// <summary>Page the accumulated window to a durable tail.</summary>
    Spill,
}

/// <summary>Outcome of one relay ingress operation.</summary>
public enum RelayIngressOutcome
{
    /// <summary>The operation opened an empty hot window.</summary>
    Accepted,

    /// <summary>The operation merged into a non-empty hot window.</summary>
    Conflated,

    /// <summary>The selected lossy policy discarded data.</summary>
    Dropped,

    /// <summary>The block policy refused the operation.</summary>
    Blocked,
}

/// <summary>Reactive limits and overflow selection for a relay.</summary>
public sealed class BackpressurePolicy
{
    /// <summary>Creates a live policy whose fields can be retuned through their source cells.</summary>
    public BackpressurePolicy(
        Context ctx,
        BoundDimension dimension,
        ulong highWater,
        ulong lowWater,
        RelayOverflow overflow)
    {
        Guard.NotNull(ctx, nameof(ctx));
        if (highWater == 0)
            throw new ArgumentOutOfRangeException(
                nameof(highWater),
                highWater,
                "high-water mark must be positive");
        if (lowWater >= highWater)
            throw new ArgumentOutOfRangeException(
                nameof(lowWater),
                lowWater,
                "low-water mark must be below the high-water mark");

        Dimension = ctx.Source(dimension);
        HighWater = ctx.Source(highWater);
        LowWater = ctx.Source(lowWater);
        Overflow = ctx.Source(overflow);
    }

    /// <summary>Reactive bound dimension.</summary>
    public Source<BoundDimension> Dimension { get; }

    /// <summary>Reactive ingress gate threshold.</summary>
    public Source<ulong> HighWater { get; }

    /// <summary>Reactive hysteresis re-open threshold.</summary>
    public Source<ulong> LowWater { get; }

    /// <summary>Reactive overflow action.</summary>
    public Source<RelayOverflow> Overflow { get; }
}

/// <summary>
/// Algebra-typed conflating relay: a reactive hot window adapting fast ingress to slow egress.
/// </summary>
public sealed class RelayCell<T>
{
    private readonly record struct Head(T? Value, bool Present);
    private readonly record struct HotWindowMetrics(
        ulong? Bytes,
        object? Key,
        bool HasKey,
        ulong? OpenedAt);

    private readonly Context _ctx;
    private readonly BackpressurePolicy _policy;
    private readonly SpillStore<T>? _spillStore;
    private readonly Func<T, ulong> _spillSize;
    private readonly bool _spillDeduplicatesReplay;
    private readonly RelayMeter<T>? _meter;
    private readonly HashSet<object?> _keys;
    private MergePolicy<T> _merge;
    private readonly Source<Head> _head;
    private readonly Source<ulong> _pending;
    private readonly Source<ulong> _bytes;
    private readonly Source<ulong> _pendingKeys;
    private readonly Source<ulong> _openedAt;
    private readonly Source<bool> _occupied;
    private readonly Source<ulong> _dropped;
    private readonly Source<ulong> _conflated;
    private readonly Source<ulong> _spilled;
    private readonly Computed<ulong> _depth;
    private readonly Computed<ulong> _age;
    private readonly Computed<ulong> _measure;
    private readonly Computed<bool> _isFull;
    private readonly Computed<bool> _isEmpty;
    private readonly Computed<bool> _canReconfigure;

    /// <summary>Creates an in-process, count-metered relay and validates its algebra/policy pair.</summary>
    public RelayCell(
        Context ctx,
        BackpressurePolicy policy,
        MergePolicy<T> merge)
        : this(
            ctx,
            policy,
            merge,
            spillStore: null,
            spillSize: null,
            spillDeduplicatesReplay: false,
            meter: null)
    {
    }

    /// <summary>Creates an in-process relay with configured non-count metering.</summary>
    public static RelayCell<T> WithMetering(
        Context ctx,
        BackpressurePolicy policy,
        MergePolicy<T> merge,
        RelayMeter<T> meter)
    {
        Guard.NotNull(meter, nameof(meter));
        return new RelayCell<T>(
            ctx,
            policy,
            merge,
            spillStore: null,
            spillSize: null,
            spillDeduplicatesReplay: false,
            meter);
    }

    /// <summary>
    /// Creates a relay with an optional durable spill tail. Non-idempotent merge policies require
    /// the caller to assert that the storage/egress path deduplicates replay identities.
    /// </summary>
    public RelayCell(
        Context ctx,
        BackpressurePolicy policy,
        MergePolicy<T> merge,
        SpillStore<T>? spillStore,
        Func<T, ulong>? spillSize = null,
        bool spillDeduplicatesReplay = false,
        RelayMeter<T>? meter = null)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(policy, nameof(policy));
        Guard.NotNull(merge, nameof(merge));
        if (meter?.LogicalClock is { } clock && !ReferenceEquals(clock.Ctx, ctx))
            throw new ArgumentException(
                "the relay logical clock must belong to the relay context",
                nameof(meter));
        ValidatePolicy(
            policy,
            merge,
            spillStore is not null,
            spillDeduplicatesReplay,
            meter,
            construction: true);

        _ctx = ctx;
        _policy = policy;
        _spillStore = spillStore;
        _meter = meter;
        _spillSize = spillSize ?? meter?.ByteSize ?? (static _ => 1);
        _spillDeduplicatesReplay = spillDeduplicatesReplay;
        _keys = new HashSet<object?>(meter?.KeyComparer);
        _merge = merge;
        _head = ctx.Source(new Head(default, Present: false));
        _pending = ctx.Source(0UL);
        _bytes = ctx.Source(0UL);
        _pendingKeys = ctx.Source(0UL);
        _openedAt = ctx.Source(0UL);
        _occupied = ctx.Source(false);
        _dropped = ctx.Source(0UL);
        _conflated = ctx.Source(0UL);
        _spilled = ctx.Source(0UL);
        _depth = ctx.Computed(cx => cx.Get(_pending));
        _age = ctx.Computed(cx =>
        {
            if (!cx.Get(_occupied)) return 0UL;
            var now = cx.Get(_meter!.LogicalClock!);
            var openedAt = cx.Get(_openedAt);
            return now >= openedAt ? now - openedAt : 0;
        });
        _measure = ctx.Computed(cx => ReadMeasure(cx, cx.Get(policy.Dimension)));
        _isFull = ctx.Computed(cx => cx.Get(_measure) >= cx.Get(policy.HighWater));
        _isEmpty = ctx.Computed(cx => !cx.Get(_head).Present);
        _canReconfigure = ctx.Computed(cx => !cx.Get(_head).Present);
    }

    /// <summary>Current hot-window operation count.</summary>
    public ulong Depth() => _depth.Get();

    /// <summary>Tracked hot-window operation count.</summary>
    public ulong Depth(IComputeOps ops) => _depth.Get(ops);

    /// <summary>Encoded size of the current coalesced hot head.</summary>
    public ulong Bytes()
    {
        EnsureMeterConfigured(BoundDimension.Bytes, construction: false);
        return _bytes.Get();
    }

    /// <summary>Tracked encoded size of the current coalesced hot head.</summary>
    public ulong Bytes(IComputeOps ops)
    {
        EnsureMeterConfigured(BoundDimension.Bytes, construction: false);
        return _bytes.Get(ops);
    }

    /// <summary>Number of distinct ingress keys in the current hot window.</summary>
    public ulong PendingKeys()
    {
        EnsureMeterConfigured(BoundDimension.Keys, construction: false);
        return _pendingKeys.Get();
    }

    /// <summary>Tracked distinct-key count for the current hot window.</summary>
    public ulong PendingKeys(IComputeOps ops)
    {
        EnsureMeterConfigured(BoundDimension.Keys, construction: false);
        return _pendingKeys.Get(ops);
    }

    /// <summary>Logical age of the current hot window, or zero while it is empty.</summary>
    public ulong Age()
    {
        EnsureMeterConfigured(BoundDimension.Age, construction: false);
        return _age.Get();
    }

    /// <summary>Tracked logical age of the current hot window.</summary>
    public ulong Age(IComputeOps ops)
    {
        EnsureMeterConfigured(BoundDimension.Age, construction: false);
        return _age.Get(ops);
    }

    /// <summary>Current value of the dimension selected by the live policy.</summary>
    public ulong Measure() => _measure.Get();

    /// <summary>Tracked value of the dimension selected by the live policy.</summary>
    public ulong Measure(IComputeOps ops) => _measure.Get(ops);

    /// <summary>Whether ingress has reached the current high-water mark.</summary>
    public bool IsFull() => _isFull.Get();

    /// <summary>Tracked fullness.</summary>
    public bool IsFull(IComputeOps ops) => _isFull.Get(ops);

    /// <summary>Whether there is no hot value to drain.</summary>
    public bool IsEmpty() => _isEmpty.Get();

    /// <summary>Tracked emptiness.</summary>
    public bool IsEmpty(IComputeOps ops) => _isEmpty.Get(ops);

    /// <summary>Number of lossy overflow actions.</summary>
    public ulong Dropped() => _dropped.Get();

    /// <summary>Tracked lossy overflow count.</summary>
    public ulong Dropped(IComputeOps ops) => _dropped.Get(ops);

    /// <summary>Number of operations merged into an already non-empty window.</summary>
    public ulong Conflated() => _conflated.Get();

    /// <summary>Tracked conflation count.</summary>
    public ulong Conflated(IComputeOps ops) => _conflated.Get(ops);

    /// <summary>Number of full hot windows paged to the durable spill tail.</summary>
    public ulong Spilled() => _spilled.Get();

    /// <summary>Tracked spilled-window count.</summary>
    public ulong Spilled(IComputeOps ops) => _spilled.Get(ops);

    /// <summary>Whether the hot window is empty, allowing its merge policy to change safely.</summary>
    public bool CanReconfigure() => _canReconfigure.Get();

    /// <summary>Tracked merge-reconfiguration readiness.</summary>
    public bool CanReconfigure(IComputeOps ops) => _canReconfigure.Get(ops);

    /// <summary>Whether the current live overflow selection is legal for this relay.</summary>
    public bool OverflowIsLegal()
    {
        var overflow = _policy.Overflow.Peek();
        return overflow switch
        {
            RelayOverflow.Conflate => _merge.Conflates,
            RelayOverflow.Spill =>
                _spillStore is not null &&
                (_merge.Idempotent || _spillDeduplicatesReplay),

            // These three impose no requirement on the merge policy or on a spill store.
            RelayOverflow.Block or RelayOverflow.DropOldest or RelayOverflow.DropNewest => true,

            // This predicate is the ADMISSION GATE for a live policy swap. The old catch-all
            // answered "legal" for anything it did not recognise, which is precisely backwards:
            // an unknown overflow is the one value whose requirements cannot be checked.
            _ => throw new ArgumentOutOfRangeException(
                nameof(overflow), overflow, "Unknown relay overflow policy."),
        };
    }

    /// <summary>Ingests one operation under the current reactive overflow policy.</summary>
    public RelayIngressOutcome Ingress(T operation)
    {
        ValidateLivePolicy();
        var head = _head.Peek();
        var wasEmpty = !head.Present;
        if (IsFullUntracked())
        {
            switch (_policy.Overflow.Peek())
            {
                case RelayOverflow.Block:
                    return RelayIngressOutcome.Blocked;
                case RelayOverflow.DropNewest:
                    _dropped.Set(checked(_dropped.Peek() + 1));
                    return RelayIngressOutcome.Dropped;
                case RelayOverflow.DropOldest:
                    var dropped = checked(_dropped.Peek() + 1);
                    var droppedWindow = CaptureHotWindowMetrics(operation);
                    _ctx.Batch(() =>
                    {
                        ResetHotWindow(operation, droppedWindow);
                        _dropped.Set(dropped);
                    });
                    return RelayIngressOutcome.Dropped;
                case RelayOverflow.Conflate:
                    break;
                case RelayOverflow.Spill:
                    if (!head.Present)
                        throw new InvalidOperationException(
                            "a full relay must have a hot value to spill");
                    var spilled = checked(_spilled.Peek() + 1);
                    var spilledWindow = CaptureHotWindowMetrics(operation);
                    _spillStore!.Spill(head.Value!, _spillSize(head.Value!));
                    _ctx.Batch(() =>
                    {
                        ResetHotWindow(operation, spilledWindow);
                        _spilled.Set(spilled);
                    });
                    return RelayIngressOutcome.Conflated;
                default:
                    throw new InvalidOperationException("unknown relay overflow policy");
            }
        }

        if (wasEmpty)
        {
            var openedWindow = CaptureHotWindowMetrics(operation);
            _ctx.Batch(() => ResetHotWindow(operation, openedWindow));
            return RelayIngressOutcome.Accepted;
        }

        var next = _merge.Merge(head.Value!, operation);
        var nextPending = checked(_pending.Peek() + 1);
        var nextBytes = _meter?.ByteSize?.Invoke(next);
        var key = _meter?.KeySelector?.Invoke(operation);
        var conflated = checked(_conflated.Peek() + 1);
        _ctx.Batch(() =>
        {
            _head.Set(new Head(next, Present: true));
            _pending.Set(nextPending);
            if (nextBytes is { } bytes) _bytes.Set(bytes);
            AddKey(key);
            _conflated.Set(conflated);
        });
        return RelayIngressOutcome.Conflated;
    }

    /// <summary>Drains the coalesced hot window, returning false when it is empty.</summary>
    public bool TryDrain(out T? value)
    {
        var head = _head.Peek();
        value = head.Value;
        if (!head.Present) return false;
        _ctx.Batch(() =>
        {
            _head.Set(new Head(default, Present: false));
            _pending.Set(0);
            _bytes.Set(0);
            _keys.Clear();
            _pendingKeys.Set(0);
            _occupied.Set(false);
        });
        return true;
    }

    /// <summary>Reads the coalesced hot window without draining it.</summary>
    public bool TryPeek(out T? value)
    {
        var head = _head.Peek();
        value = head.Value;
        return head.Present;
    }

    /// <summary>
    /// Swaps the merge policy only while the hot window is empty and the current overflow remains
    /// legal. Returns false when a live window makes reconfiguration unsafe.
    /// </summary>
    public bool TryReconfigure(MergePolicy<T> merge)
    {
        Guard.NotNull(merge, nameof(merge));
        if (_head.Peek().Present) return false;
        ValidatePolicy(
            _policy,
            merge,
            _spillStore is not null,
            _spillDeduplicatesReplay,
            _meter,
            construction: false);
        _merge = merge;
        return true;
    }

    /// <summary>
    /// Reconstructs the cold spill tail followed by the current hot head into an initial state.
    /// </summary>
    public T Reconstruct(T initial)
    {
        var head = _head.Peek();
        if (_spillStore is not null)
            return _spillStore.Reconstruct(initial, head.Value, head.Present);
        return head.Present ? _merge.Merge(initial, head.Value!) : initial;
    }

    private bool IsFullUntracked() =>
        ReadMeasureUntracked(_policy.Dimension.Peek()) >= _policy.HighWater.Peek();

    private void ValidateLivePolicy()
    {
        EnsureMeterConfigured(_policy.Dimension.Peek(), construction: false);
        if (_policy.HighWater.Peek() == 0 ||
            _policy.LowWater.Peek() >= _policy.HighWater.Peek())
            throw new InvalidOperationException(
                "relay watermarks must satisfy 0 <= low-water < high-water");
        if (!OverflowIsLegal())
            throw new InvalidOperationException(
                _policy.Overflow.Peek() == RelayOverflow.Spill
                    ? "Spill overflow requires a SpillStore and idempotent or deduplicated replay"
                    : "Conflate overflow requires a conflating merge policy");
    }

    private static void ValidatePolicy(
        BackpressurePolicy policy,
        MergePolicy<T> merge,
        bool hasSpillStore,
        bool spillDeduplicatesReplay,
        RelayMeter<T>? meter,
        bool construction)
    {
        EnsureMeterConfigured(policy.Dimension.Peek(), meter, construction);
        if (policy.Overflow.Peek() == RelayOverflow.Spill && !hasSpillStore)
            throw new NotSupportedException("Spill overflow requires a SpillStore");
        if (policy.Overflow.Peek() == RelayOverflow.Spill &&
            !merge.Idempotent &&
            !spillDeduplicatesReplay)
            throw new ArgumentException(
                "Spill overflow requires an idempotent merge or deduplicated replay",
                construction ? nameof(merge) : null);
        if (policy.Overflow.Peek() == RelayOverflow.Conflate && !merge.Conflates)
            throw new ArgumentException(
                "Conflate overflow requires a conflating merge policy",
                construction ? nameof(merge) : null);
    }

    private HotWindowMetrics CaptureHotWindowMetrics(T operation) =>
        new(
            _meter?.ByteSize?.Invoke(operation),
            _meter?.KeySelector?.Invoke(operation),
            _meter?.KeySelector is not null,
            _meter?.LogicalClock?.Peek());

    private void ResetHotWindow(T operation, HotWindowMetrics metrics)
    {
        _head.Set(new Head(operation, Present: true));
        _pending.Set(1);
        if (metrics.Bytes is { } encodedBytes) _bytes.Set(encodedBytes);
        _keys.Clear();
        if (metrics.HasKey)
        {
            _keys.Add(metrics.Key);
            _pendingKeys.Set(1);
        }
        if (metrics.OpenedAt is { } now) _openedAt.Set(now);
        _occupied.Set(true);
    }

    private void AddKey(object? key)
    {
        if (_meter?.KeySelector is null || !_keys.Add(key)) return;
        _pendingKeys.Set(checked((ulong)_keys.Count));
    }

    private ulong ReadMeasure(Compute cx, BoundDimension dimension)
    {
        EnsureMeterConfigured(dimension, construction: false);
        return dimension switch
        {
            BoundDimension.Count => cx.Get(_pending),
            BoundDimension.Bytes => cx.Get(_bytes),
            BoundDimension.Keys => cx.Get(_pendingKeys),
            BoundDimension.Age => cx.Get(_age),
            _ => throw new InvalidOperationException("unknown relay bound dimension"),
        };
    }

    private ulong ReadMeasureUntracked(BoundDimension dimension)
    {
        EnsureMeterConfigured(dimension, construction: false);
        return dimension switch
        {
            BoundDimension.Count => _pending.Peek(),
            BoundDimension.Bytes => _bytes.Peek(),
            BoundDimension.Keys => _pendingKeys.Peek(),
            BoundDimension.Age => _age.Get(),
            _ => throw new InvalidOperationException("unknown relay bound dimension"),
        };
    }

    private void EnsureMeterConfigured(BoundDimension dimension, bool construction) =>
        EnsureMeterConfigured(dimension, _meter, construction);

    private static void EnsureMeterConfigured(
        BoundDimension dimension,
        RelayMeter<T>? meter,
        bool construction)
    {
        var configured = dimension switch
        {
            BoundDimension.Count => true,
            BoundDimension.Bytes => meter?.ByteSize is not null,
            BoundDimension.Keys => meter?.KeySelector is not null,
            BoundDimension.Age => meter?.LogicalClock is not null,
            _ => false,
        };
        if (configured) return;

        var message = $"{dimension} relay metering requires a configured RelayMeter";
        if (construction) throw new NotSupportedException(message);
        throw new InvalidOperationException(message);
    }
}
