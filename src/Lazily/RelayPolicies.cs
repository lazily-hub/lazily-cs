namespace Lazily;

/// <summary>
/// App-to-transport relay role that propagates fullness directly to the local producer.
/// </summary>
/// <typeparam name="T">The relayed operation type.</typeparam>
public sealed class Outbox<T>
{
    private readonly RelayCell<T> _relay;

    /// <summary>Creates a producer-facing relay with direction-appropriate defaults.</summary>
    /// <param name="ctx">The reactive context.</param>
    /// <param name="highWater">The ingress high-water mark.</param>
    /// <param name="merge">The hot-window merge algebra.</param>
    /// <param name="dimension">The quantity that applies the bound.</param>
    /// <param name="overflow">The full-window action; state outboxes default to conflation.</param>
    /// <param name="meter">Value-specific observations for non-count dimensions.</param>
    /// <param name="spillStore">Optional durable tail required by spill overflow.</param>
    /// <param name="spillSize">Optional durable-page size observation.</param>
    /// <param name="spillDeduplicatesReplay">
    /// Whether a non-idempotent spill path deduplicates replay identities.
    /// </param>
    public Outbox(
        Context ctx,
        ulong highWater,
        MergePolicy<T> merge,
        BoundDimension dimension = BoundDimension.Count,
        RelayOverflow overflow = RelayOverflow.Conflate,
        RelayMeter<T>? meter = null,
        SpillStore<T>? spillStore = null,
        Func<T, ulong>? spillSize = null,
        bool spillDeduplicatesReplay = false)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(merge, nameof(merge));
        var policy = new BackpressurePolicy(
            ctx,
            dimension,
            highWater,
            highWater / 2,
            overflow);
        _relay = new RelayCell<T>(
            ctx,
            policy,
            merge,
            spillStore,
            spillSize,
            spillDeduplicatesReplay,
            meter);
    }

    /// <summary>Sends one local operation, returning the producer's backpressure outcome.</summary>
    public RelayIngressOutcome Send(T operation) => _relay.Ingress(operation);

    /// <summary>Drains the coalesced hot window for transport egress.</summary>
    public bool TryDrain(out T? value) => _relay.TryDrain(out value);

    /// <summary>Whether the local producer has reached the active bound.</summary>
    public bool IsFull() => _relay.IsFull();

    /// <summary>Tracked producer-facing fullness.</summary>
    public bool IsFull(IComputeOps ops) => _relay.IsFull(ops);

    /// <summary>The shared direction-neutral relay core.</summary>
    public RelayCell<T> Relay => _relay;
}

/// <summary>
/// Transport-to-app relay role that meters the remote peer with replenishable credits.
/// </summary>
/// <typeparam name="T">The received operation type.</typeparam>
public sealed class Inbox<T>
{
    private readonly Context _ctx;
    private readonly RelayCell<T> _relay;
    private readonly Source<ulong> _credits;
    private readonly Computed<bool> _ready;
    private readonly ulong _maxCredits;

    /// <summary>Creates a receive relay with a count bound and remote credit budget.</summary>
    public Inbox(
        Context ctx,
        ulong highWater,
        ulong maxCredits,
        MergePolicy<T> merge,
        RelayOverflow overflow = RelayOverflow.Conflate)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(merge, nameof(merge));
        _ctx = ctx;
        _maxCredits = maxCredits;
        _credits = ctx.Source(maxCredits);
        _ready = ctx.Computed(cx => cx.Get(_credits) > 0);
        _relay = new RelayCell<T>(
            ctx,
            new BackpressurePolicy(
                ctx,
                BoundDimension.Count,
                highWater,
                highWater / 2,
                overflow),
            merge);
    }

    /// <summary>
    /// Whether the transport may deliver another message. False means it must withhold flow.
    /// </summary>
    public bool Ready() => _ready.Get();

    /// <summary>Tracked remote-flow readiness.</summary>
    public bool Ready(IComputeOps ops) => _ready.Get(ops);

    /// <summary>Credits currently available to the remote.</summary>
    public ulong Credits() => _credits.Get();

    /// <summary>Tracked remote credit count.</summary>
    public ulong Credits(IComputeOps ops) => _credits.Get(ops);

    /// <summary>
    /// Delivers one received operation and consumes one credit, saturating at zero.
    /// </summary>
    public RelayIngressOutcome Receive(T operation)
    {
        RelayIngressOutcome outcome = default;
        _ctx.Batch(() =>
        {
            _credits.Set(_credits.Peek() == 0 ? 0 : _credits.Peek() - 1);
            outcome = _relay.Ingress(operation);
        });
        return outcome;
    }

    /// <summary>
    /// Drains the app-facing window and replenishes credits up to the configured budget.
    /// </summary>
    public bool TryConsume(ulong replenish, out T? value)
    {
        var drained = false;
        T? consumed = default;
        _ctx.Batch(() =>
        {
            drained = _relay.TryDrain(out consumed);
            var credits = _credits.Peek();
            var available = _maxCredits - credits;
            _credits.Set(credits + Math.Min(replenish, available));
        });
        value = consumed;
        return drained;
    }

    /// <summary>The shared direction-neutral relay core.</summary>
    public RelayCell<T> Relay => _relay;
}

/// <summary>Token-bucket egress policy driven by explicit logical ticks.</summary>
public sealed class RatePolicy
{
    private ulong _tokens;

    /// <summary>Creates a full token bucket.</summary>
    public RatePolicy(ulong capacity, ulong refillPerTick)
    {
        Capacity = capacity;
        RefillPerTick = refillPerTick;
        _tokens = capacity;
    }

    /// <summary>Maximum token count.</summary>
    public ulong Capacity { get; }

    /// <summary>Tokens added by each logical tick.</summary>
    public ulong RefillPerTick { get; }

    /// <summary>Tokens currently available.</summary>
    public ulong Tokens => _tokens;

    /// <summary>Consumes one token when egress is permitted.</summary>
    public bool TryEgress()
    {
        if (_tokens == 0) return false;
        _tokens--;
        return true;
    }

    /// <summary>Refills the bucket, saturating at capacity without integer wraparound.</summary>
    public void Tick()
    {
        var available = Capacity - _tokens;
        _tokens += Math.Min(RefillPerTick, available);
    }
}

/// <summary>Operation-count flush window with an explicit logical-tick boundary.</summary>
public sealed class WindowPolicy
{
    private ulong _pending;

    /// <summary>Creates a window, clamping a zero size to one operation.</summary>
    public WindowPolicy(ulong windowOperations)
    {
        WindowOperations = Math.Max(1UL, windowOperations);
    }

    /// <summary>Number of operations that fills one window.</summary>
    public ulong WindowOperations { get; }

    /// <summary>Operations held in the current window.</summary>
    public ulong Pending => _pending;

    /// <summary>Records ingress and reports whether a full window should flush.</summary>
    public bool OnIngress()
    {
        _pending++;
        if (_pending < WindowOperations) return false;
        _pending = 0;
        return true;
    }

    /// <summary>Flushes a non-empty partial window at a logical interval boundary.</summary>
    public bool Tick()
    {
        if (_pending == 0) return false;
        _pending = 0;
        return true;
    }
}

/// <summary>Logical-clock TTL policy that explicitly drops values older than its bound.</summary>
public sealed class ExpiryPolicy
{
    private ulong _now;

    /// <summary>Creates an expiry policy at logical time zero.</summary>
    public ExpiryPolicy(ulong timeToLive) => TimeToLive = timeToLive;

    /// <summary>Maximum live age, inclusive.</summary>
    public ulong TimeToLive { get; }

    /// <summary>Current logical time.</summary>
    public ulong Now => _now;

    /// <summary>Advances the logical clock.</summary>
    public void Advance(ulong by) => _now = checked(_now + by);

    /// <summary>Whether an item stamped at the given time is still live.</summary>
    public bool IsLive(ulong stampedAt) =>
        (_now >= stampedAt ? _now - stampedAt : 0) <= TimeToLive;

    /// <summary>Returns the values from a timestamped batch that have not expired.</summary>
    public IReadOnlyList<T> RetainLive<T>(IEnumerable<(ulong Timestamp, T Value)> batch)
    {
        Guard.NotNull(batch, nameof(batch));
        return batch
            .Where(item => IsLive(item.Timestamp))
            .Select(item => item.Value)
            .ToArray();
    }
}

/// <summary>Highest-priority-first storage with FIFO order inside each priority.</summary>
/// <typeparam name="T">The stored value type.</typeparam>
public sealed class PriorityStorage<T>
{
    private readonly record struct Entry(ulong Priority, ulong Sequence, T Value);

    private readonly List<Entry> _items = [];
    private ulong _sequence;

    /// <summary>Stored item count.</summary>
    public int Count => _items.Count;

    /// <summary>Whether the storage has no pending items.</summary>
    public bool IsEmpty => _items.Count == 0;

    /// <summary>Adds one value at the given priority.</summary>
    public void Push(ulong priority, T value)
    {
        var sequence = _sequence;
        _sequence = checked(sequence + 1);
        _items.Add(new Entry(priority, sequence, value));
    }

    /// <summary>Removes the highest-priority item, FIFO inside an equal-priority group.</summary>
    public bool TryPop(out T? value)
    {
        if (_items.Count == 0)
        {
            value = default;
            return false;
        }

        var best = 0;
        for (var index = 1; index < _items.Count; index++)
        {
            var candidate = _items[index];
            var current = _items[best];
            if (candidate.Priority > current.Priority ||
                candidate.Priority == current.Priority &&
                candidate.Sequence < current.Sequence)
                best = index;
        }

        value = _items[best].Value;
        _items.RemoveAt(best);
        return true;
    }
}

/// <summary>Per-key relay sharding under one associative, commutative merge policy.</summary>
/// <typeparam name="TKey">The shard key type.</typeparam>
/// <typeparam name="T">The relayed operation type.</typeparam>
public sealed class KeyedRelay<TKey, T>
    where TKey : notnull
{
    private readonly Context _ctx;
    private readonly ulong _highWater;
    private readonly RelayOverflow _overflow;
    private readonly MergePolicy<T> _merge;
    private readonly Dictionary<TKey, RelayCell<T>> _shards;
    private readonly List<TKey> _keys = [];

    /// <summary>Creates lazily materialized, count-bounded relay shards.</summary>
    public KeyedRelay(
        Context ctx,
        ulong highWater,
        RelayOverflow overflow,
        MergePolicy<T> merge,
        IEqualityComparer<TKey>? comparer = null)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(merge, nameof(merge));
        if (highWater == 0)
            throw new ArgumentOutOfRangeException(
                nameof(highWater),
                highWater,
                "high-water mark must be positive");
        if (!merge.Commutative)
            throw new ArgumentException(
                "keyed relay sharding requires a commutative merge policy",
                nameof(merge));
        if (overflow == RelayOverflow.Conflate && !merge.Conflates)
            throw new ArgumentException(
                "Conflate overflow requires a conflating merge policy",
                nameof(merge));
        if (overflow == RelayOverflow.Spill)
            throw new NotSupportedException(
                "keyed spill requires an explicit durable store per shard");

        _ctx = ctx;
        _highWater = highWater;
        _overflow = overflow;
        _merge = merge;
        _shards = new Dictionary<TKey, RelayCell<T>>(comparer);
    }

    /// <summary>Routes one operation to its key's shard, creating that shard on first use.</summary>
    public RelayIngressOutcome Ingress(TKey key, T operation)
    {
        if (!_shards.TryGetValue(key, out var relay))
        {
            relay = new RelayCell<T>(
                _ctx,
                new BackpressurePolicy(
                    _ctx,
                    BoundDimension.Count,
                    _highWater,
                    _highWater / 2,
                    _overflow),
                _merge);
            _shards.Add(key, relay);
            _keys.Add(key);
        }
        return relay.Ingress(operation);
    }

    /// <summary>Drains one key's coalesced hot window.</summary>
    public bool TryDrain(TKey key, out T? value)
    {
        if (_shards.TryGetValue(key, out var relay)) return relay.TryDrain(out value);
        value = default;
        return false;
    }

    /// <summary>Keys in deterministic shard-creation order.</summary>
    public IReadOnlyList<TKey> Keys => _keys.ToArray();
}
