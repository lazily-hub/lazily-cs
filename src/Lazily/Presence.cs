namespace Lazily;

/// <summary>The persistence plane carried by a value.</summary>
public enum DataPlane
{
    /// <summary>Short-lived state that must not enter durable storage.</summary>
    Ephemeral,

    /// <summary>State intended for durable storage and replay.</summary>
    Durable,
}

/// <summary>A single typed value that clears at its logical deadline.</summary>
public sealed class EphemeralCell<T>
{
    private Optional<T> _value;
    private long _expiry;

    /// <summary>Creates an empty ephemeral value.</summary>
    public EphemeralCell(Context context)
    {
        Guard.NotNull(context, nameof(context));
        ValueCell = context.Source(Optional<T>.None);
    }

    /// <summary>The value's non-durable plane.</summary>
    public DataPlane Plane => DataPlane.Ephemeral;

    /// <summary>The reactive live value.</summary>
    public Source<Optional<T>> ValueCell { get; }

    /// <summary>The current live value.</summary>
    public Optional<T> Value => ValueCell.Get();

    /// <summary>Sets a value with a time-to-live.</summary>
    public void Set(T value, long now, long ttl)
    {
        Guard.NotNull(value, nameof(value));
        LogicalTime.Require(now, nameof(now));
        LogicalTime.Require(ttl, nameof(ttl));
        _value = Optional<T>.Some(value);
        _expiry = LogicalTime.Add(now, ttl);
        ValueCell.Set(_value);
    }

    /// <summary>Clears an expired value.</summary>
    public void Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (_value.HasValue && now >= _expiry) _value = Optional<T>.None;
        ValueCell.Set(_value);
    }
}

/// <summary>Shared reactive storage for typed per-peer ephemeral maps.</summary>
public abstract class EphemeralMapCell<T>
{
    private sealed record Entry(T Value, long Expiry);

    private readonly Dictionary<long, Entry> _entries = [];

    /// <summary>Creates an empty per-peer map with a fixed time-to-live.</summary>
    protected EphemeralMapCell(Context context, long ttl)
    {
        Guard.NotNull(context, nameof(context));
        TimeToLive = LogicalTime.Require(ttl, nameof(ttl));
        PresentCell = context.Source<IReadOnlyDictionary<long, T>>(
            new SortedDictionary<long, T>(),
            DictionaryEqualityComparer<long, T>.Instance);
    }

    /// <summary>The fixed time-to-live for each write.</summary>
    protected long TimeToLive { get; }

    /// <summary>The reactive map of live peer values.</summary>
    public Source<IReadOnlyDictionary<long, T>> PresentCell { get; }

    /// <summary>The map of live peer values.</summary>
    public IReadOnlyDictionary<long, T> Present => PresentCell.Get();

    /// <summary>Writes one peer value and refreshes the live projection.</summary>
    protected void SetValue(long peer, T value, long now)
    {
        Guard.NotNull(value, nameof(value));
        LogicalTime.Require(now, nameof(now));
        _entries[peer] = new Entry(value, LogicalTime.Add(now, TimeToLive));
        Refresh(now);
    }

    /// <summary>Evicts one peer value and refreshes the live projection.</summary>
    protected void EvictValue(long peer, long now)
    {
        LogicalTime.Require(now, nameof(now));
        _entries.Remove(peer);
        Refresh(now);
    }

    /// <summary>Evicts all expired values and refreshes the live projection.</summary>
    protected void TickValues(long now)
    {
        LogicalTime.Require(now, nameof(now));
        foreach (var peer in _entries
                     .Where(pair => now >= pair.Value.Expiry)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.Remove(peer);
        }
        Refresh(now);
    }

    /// <summary>Returns one live peer value at logical time <paramref name="now"/>.</summary>
    public Optional<T> Get(long peer, long now)
    {
        LogicalTime.Require(now, nameof(now));
        return _entries.TryGetValue(peer, out var entry) && now < entry.Expiry
            ? Optional<T>.Some(entry.Value)
            : Optional<T>.None;
    }

    private void Refresh(long now)
    {
        IReadOnlyDictionary<long, T> present = new SortedDictionary<long, T>(
            _entries
                .Where(pair => now < pair.Value.Expiry)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Value));
        PresentCell.Set(present);
    }
}

/// <summary>Per-peer ephemeral presence maintained by heartbeat and membership.</summary>
public sealed class PresenceCell<T> : EphemeralMapCell<T>
{
    /// <summary>Creates a presence map with a fixed time-to-live.</summary>
    public PresenceCell(Context context, long ttl)
        : base(context, ttl)
    {
    }

    /// <summary>The map's non-durable plane.</summary>
    public DataPlane Plane => DataPlane.Ephemeral;

    /// <summary>Adds or refreshes one peer's presence value.</summary>
    public void Heartbeat(long peer, T value, long now) => SetValue(peer, value, now);

    /// <summary>Evicts a peer after membership loss.</summary>
    public void Evict(long peer, long now) => EvictValue(peer, now);

    /// <summary>Evicts values whose time-to-live elapsed.</summary>
    public void Tick(long now) => TickValues(now);
}

/// <summary>Typed last-writer-per-peer ephemeral awareness with time-to-live.</summary>
public sealed class AwarenessCell<T> : EphemeralMapCell<T>
{
    /// <summary>Creates an awareness map with a fixed time-to-live.</summary>
    public AwarenessCell(Context context, long ttl)
        : base(context, ttl)
    {
    }

    /// <summary>The map's non-durable plane.</summary>
    public DataPlane Plane => DataPlane.Ephemeral;

    /// <summary>Sets one peer's latest awareness value.</summary>
    public void Set(long peer, T value, long now) => SetValue(peer, value, now);

    /// <summary>Evicts values whose time-to-live elapsed.</summary>
    public void Tick(long now) => TickValues(now);
}
