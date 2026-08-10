namespace Lazily;

/// <summary>
/// A fractional sequence position ordered lexicographically by key bytes and then by peer.
/// </summary>
public sealed class SeqPosition : IComparable<SeqPosition>, IEquatable<SeqPosition>
{
    private readonly byte[] _fractionalKey;

    internal SeqPosition(IEnumerable<byte> fractionalKey, int peer)
    {
        _fractionalKey = [.. fractionalKey];
        Peer = peer;
    }

    /// <summary>The fractional-index bytes.</summary>
    public IReadOnlyList<byte> FractionalKey => Array.AsReadOnly(_fractionalKey);

    /// <summary>The peer that minted the position.</summary>
    public int Peer { get; }

    internal byte[] Bytes => _fractionalKey;

    /// <inheritdoc />
    public int CompareTo(SeqPosition? other)
    {
        if (other is null) return 1;
        var common = Math.Min(_fractionalKey.Length, other._fractionalKey.Length);
        for (var index = 0; index < common; index++)
        {
            var order = _fractionalKey[index].CompareTo(other._fractionalKey[index]);
            if (order != 0) return order;
        }

        var length = _fractionalKey.Length.CompareTo(other._fractionalKey.Length);
        return length != 0 ? length : Peer.CompareTo(other.Peer);
    }

    /// <inheritdoc />
    public bool Equals(SeqPosition? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SeqPosition other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _fractionalKey) hash.Add(item);
        hash.Add(Peer);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A move-aware sequence CRDT. Value, position, and deletion are independent LWW registers, so a
/// concurrent move and value edit both survive while concurrent moves converge without duplicating
/// an element.
/// </summary>
/// <typeparam name="TKey">The stable element identity.</typeparam>
/// <typeparam name="TValue">The element value.</typeparam>
public sealed class SeqCrdt<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly IEqualityComparer<TKey> _keyComparer;
    private readonly IEqualityComparer<TValue> _valueComparer;
    private readonly Hlc _clock;

    /// <summary>Creates an empty sequence owned by <paramref name="peer"/>.</summary>
    public SeqCrdt(
        int peer,
        IEqualityComparer<TKey>? keyComparer = null,
        IEqualityComparer<TValue>? valueComparer = null)
    {
        Peer = peer;
        _keyComparer = keyComparer ?? EqualityComparer<TKey>.Default;
        _valueComparer = valueComparer ?? EqualityComparer<TValue>.Default;
        _entries = new Dictionary<TKey, Entry>(_keyComparer);
        _clock = new Hlc(peer);
    }

    /// <summary>
    /// Builds a replica that has already observed <paramref name="entries"/>, resuming
    /// <paramref name="observed"/>'s causal position under <paramref name="peer"/>'s identity.
    /// </summary>
    private SeqCrdt(
        int peer,
        IEqualityComparer<TKey> keyComparer,
        IEqualityComparer<TValue> valueComparer,
        IEnumerable<KeyValuePair<TKey, Entry>> entries,
        Hlc observed)
    {
        Peer = peer;
        _keyComparer = keyComparer;
        _valueComparer = valueComparer;
        _entries = new Dictionary<TKey, Entry>(_keyComparer);
        foreach (var pair in entries) _entries.Add(pair.Key, pair.Value.Copy());

        // Resume the source's clock POSITION, never its peer (#lzzigforkhlcpeer). This
        // constructor used to chain the public one, which mints `new Hlc(peer)` — a clock
        // back at (0, 0) on a replica that already holds every stamp the source did. The
        // first local write whose wall time sits below the source's newest stamp (ordinary
        // skew, the entire reason a hybrid logical clock exists) then stamps CAUSALLY BEHIND
        // state this replica carries, and `LwwRegister.Set` adopts only on strictly-greater,
        // so the write vanishes with no error anywhere.
        //
        // The peer half is the mirror-image bug and matters just as much: the peer is the
        // stamp's final tiebreaker, so two replicas stamping under one id can mint identical
        // (micros, counter, peer) triples, neither adopts the other, and they diverge
        // permanently. lazily-zig shipped exactly that. Position travels, identity does not.
        _clock = new Hlc(peer, observed.LastMicros, observed.LastCounter);
    }

    /// <summary>The peer that owns local mutations on this replica.</summary>
    public int Peer { get; }

    /// <summary>The number of live elements.</summary>
    public int Count => _entries.Values.Count(entry => !entry.Deleted.Value);

    /// <summary>The number of deleted entries retained as tombstones.</summary>
    public int TombstoneCount => _entries.Values.Count(entry => entry.Deleted.Value);

    /// <summary>
    /// Forks this sequence under a new peer identity while preserving all register stamps. The
    /// fork resumes this replica's clock position, so its next local write cannot stamp behind
    /// state it already holds (#lzzigforkhlcpeer).
    /// </summary>
    public SeqCrdt<TKey, TValue> Fork(int peer) =>
        new(peer, _keyComparer, _valueComparer, _entries, _clock);

    /// <summary>Returns an independent copy retaining this replica's peer identity.</summary>
    public SeqCrdt<TKey, TValue> Copy() => Fork(Peer);

    /// <summary>
    /// Inserts a new element between optional live neighbours. Existing identities are not
    /// reinserted; use a move operation to change their position.
    /// </summary>
    public bool InsertBetween(
        TKey id,
        TValue value,
        Optional<TKey> left,
        Optional<TKey> right,
        long nowMicros)
    {
        if (_entries.ContainsKey(id)) return false;
        var lower = TryFraction(left);
        var upper = TryFraction(right);
        var position = new SeqPosition(KeyBetween(lower, upper), Peer);
        var stamp = _clock.Send(nowMicros);
        _entries.Add(
            id,
            new Entry(
                new LwwRegister<TValue>(value, stamp, _valueComparer),
                new LwwRegister<SeqPosition>(position, stamp),
                new LwwRegister<bool>(false, stamp)));
        return true;
    }

    /// <summary>Appends an element after the last live element.</summary>
    public bool InsertBack(TKey id, TValue value, long nowMicros)
    {
        var order = Order();
        var left =
            order.Count == 0
                ? Optional<TKey>.None
                : Optional<TKey>.Some(order[^1]);
        return InsertBetween(id, value, left, Optional<TKey>.None, nowMicros);
    }

    /// <summary>Prepends an element before the first live element.</summary>
    public bool InsertFront(TKey id, TValue value, long nowMicros)
    {
        var order = Order();
        var right =
            order.Count == 0
                ? Optional<TKey>.None
                : Optional<TKey>.Some(order[0]);
        return InsertBetween(id, value, Optional<TKey>.None, right, nowMicros);
    }

    /// <summary>Applies a last-writer-wins update to an element value.</summary>
    public bool SetValue(TKey id, TValue value, long nowMicros) =>
        _entries.TryGetValue(id, out var entry)
        && entry.Value.Set(value, _clock.Send(nowMicros));

    /// <summary>Moves an element between optional neighbours with one LWW position write.</summary>
    public bool MoveBetween(
        TKey id,
        Optional<TKey> left,
        Optional<TKey> right,
        long nowMicros)
    {
        if (!_entries.TryGetValue(id, out var entry)) return false;
        var position = new SeqPosition(
            KeyBetween(TryFraction(left), TryFraction(right)),
            Peer);
        return entry.Position.Set(position, _clock.Send(nowMicros));
    }

    /// <summary>Moves an element immediately after <paramref name="anchor"/>.</summary>
    public bool MoveAfter(TKey id, TKey anchor, long nowMicros)
    {
        var order = Order();
        var anchorIndex = IndexOf(order, anchor);
        var right =
            anchorIndex >= 0 && anchorIndex + 1 < order.Count
                ? Optional<TKey>.Some(order[anchorIndex + 1])
                : Optional<TKey>.None;
        return MoveBetween(id, Optional<TKey>.Some(anchor), right, nowMicros);
    }

    /// <summary>Moves an element immediately before <paramref name="anchor"/>.</summary>
    public bool MoveBefore(TKey id, TKey anchor, long nowMicros)
    {
        var order = Order();
        var anchorIndex = IndexOf(order, anchor);
        var left =
            anchorIndex > 0
                ? Optional<TKey>.Some(order[anchorIndex - 1])
                : Optional<TKey>.None;
        return MoveBetween(id, left, Optional<TKey>.Some(anchor), nowMicros);
    }

    /// <summary>Tombstones an element with a last-writer-wins deletion flag.</summary>
    public bool Remove(TKey id, long nowMicros) =>
        _entries.TryGetValue(id, out var entry)
        && entry.Deleted.Set(true, _clock.Send(nowMicros));

    /// <summary>Whether an identity is present and live.</summary>
    public bool Contains(TKey id) =>
        _entries.TryGetValue(id, out var entry) && !entry.Deleted.Value;

    /// <summary>Reads a live element value.</summary>
    public bool TryGetValue(TKey id, out TValue value)
    {
        if (_entries.TryGetValue(id, out var entry) && !entry.Deleted.Value)
        {
            value = entry.Value.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Returns live identities in converged fractional-index order.</summary>
    public IReadOnlyList<TKey> Order() =>
        _entries
            .Where(pair => !pair.Value.Deleted.Value)
            .OrderBy(pair => pair.Value.Position.Value)
            .Select(pair => pair.Key)
            .ToArray();

    /// <summary>Returns live identity/value pairs in converged order.</summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> Values()
    {
        var result = new List<KeyValuePair<TKey, TValue>>();
        foreach (var id in Order())
        {
            if (TryGetValue(id, out var value))
                result.Add(new KeyValuePair<TKey, TValue>(id, value));
        }

        return result;
    }

    /// <summary>
    /// Merges every independent element register from <paramref name="other"/> and advances the
    /// local HLC beyond the greatest observed stamp.
    /// </summary>
    public bool MergeFrom(SeqCrdt<TKey, TValue> other, long nowMicros = 0)
    {
        Guard.NotNull(other, nameof(other));
        HlcStamp? greatest = null;
        foreach (var entry in other._entries.Values)
        {
            foreach (var stamp in new[]
                     {
                         entry.Value.Stamp,
                         entry.Position.Stamp,
                         entry.Deleted.Stamp,
                     })
            {
                greatest = greatest is null || stamp.CompareTo(greatest.Value) > 0
                    ? stamp
                    : greatest;
            }
        }

        if (greatest is { } observed) _clock.Receive(observed, nowMicros);

        var changed = false;
        foreach (var (id, incoming) in other._entries)
        {
            if (_entries.TryGetValue(id, out var current))
            {
                changed |= current.Value.MergeFrom(incoming.Value);
                changed |= current.Position.MergeFrom(incoming.Position);
                changed |= current.Deleted.MergeFrom(incoming.Deleted);
            }
            else
            {
                _entries.Add(id, incoming.Copy());
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Collects tombstones accepted by the caller's causal-stability predicate.</summary>
    public int GarbageCollect(Func<HlcStamp, bool> isStable)
    {
        Guard.NotNull(isStable, nameof(isStable));
        var removable = _entries
            .Where(pair => pair.Value.Deleted.Value && isStable(pair.Value.Deleted.Stamp))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var id in removable) _entries.Remove(id);
        return removable.Length;
    }

    /// <summary>Collects tombstones whose deletion stamp is at or below a stable watermark.</summary>
    public int GarbageCollect(HlcStamp watermark) =>
        GarbageCollect(stamp => stamp.CompareTo(watermark) <= 0);

    private byte[]? TryFraction(Optional<TKey> id)
    {
        if (!id.HasValue || !_entries.TryGetValue(id.Value, out var entry)) return null;
        return entry.Position.Value.Bytes;
    }

    private int IndexOf(IReadOnlyList<TKey> values, TKey target)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (_keyComparer.Equals(values[index], target)) return index;
        }

        return -1;
    }

    private static byte[] KeyBetween(byte[]? lower, byte[]? upper)
    {
        var result = new List<byte>();
        var index = 0;
        var capacity = (lower?.Length ?? 0) + (upper?.Length ?? 0) + 2;
        while (index <= capacity)
        {
            var low = lower is { } lowerValue && index < lowerValue.Length
                ? lowerValue[index]
                : 0;
            var high = upper switch
            {
                { } upperValue when index < upperValue.Length => upperValue[index],
                { } => 0,
                null => 256,
            };

            if (low + 1 < high)
            {
                result.Add((byte)((low + high) / 2));
                return [.. result];
            }

            result.Add((byte)low);
            index++;
            if (low < high)
            {
                var lowerTail =
                    lower is { } value && index < value.Length
                        ? value[index..]
                        : [];
                result.AddRange(KeyBetween(lowerTail, upper: null));
                return [.. result];
            }
        }

        result.Add(128);
        return [.. result];
    }

    private sealed class Entry(
        LwwRegister<TValue> value,
        LwwRegister<SeqPosition> position,
        LwwRegister<bool> deleted)
    {
        internal LwwRegister<TValue> Value { get; } = value;

        internal LwwRegister<SeqPosition> Position { get; } = position;

        internal LwwRegister<bool> Deleted { get; } = deleted;

        internal Entry Copy() => new(Value.Copy(), Position.Copy(), Deleted.Copy());
    }
}
