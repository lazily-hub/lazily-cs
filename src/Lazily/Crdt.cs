namespace Lazily;

/// <summary>The merge mechanism exposed by a register-backed replicated cell.</summary>
public enum MergeMechanism
{
    /// <summary>The highest hybrid-logical-clock stamp wins.</summary>
    Lww,

    /// <summary>State merges commutatively, associatively, and idempotently.</summary>
    Crdt,
}

/// <summary>
/// A state-based CRDT whose observable value can be projected into the reactive graph.
/// </summary>
/// <typeparam name="TSelf">The concrete CRDT state type.</typeparam>
/// <typeparam name="TValue">The observable value type.</typeparam>
public interface ICellCrdt<TSelf, TValue>
    where TSelf : ICellCrdt<TSelf, TValue>
{
    /// <summary>The current converged value.</summary>
    TValue Value { get; }

    /// <summary>
    /// Merges <paramref name="other"/> into this state.
    /// </summary>
    /// <returns>True exactly when the observable value changed.</returns>
    bool MergeFrom(TSelf other);
}

/// <summary>A cell CRDT that declares the merge mechanism it implements.</summary>
public interface IRegisterCrdt<TSelf, TValue> : ICellCrdt<TSelf, TValue>
    where TSelf : IRegisterCrdt<TSelf, TValue>
{
    /// <summary>The register's merge mechanism.</summary>
    MergeMechanism Mechanism { get; }
}

/// <summary>
/// A deterministic hybrid logical clock. The caller supplies physical time so tests and replay do
/// not depend on the system clock.
/// </summary>
public sealed class Hlc
{
    private long _lastMicros;
    private long _lastCounter;

    /// <summary>Creates a clock for <paramref name="peer"/>.</summary>
    public Hlc(int peer)
    {
        Peer = peer;
    }

    /// <summary>The clock's originating peer.</summary>
    public int Peer { get; }

    /// <summary>Stamps a local event at <paramref name="nowMicros"/>.</summary>
    public HlcStamp Send(long nowMicros)
    {
        if (nowMicros > _lastMicros)
        {
            _lastMicros = nowMicros;
            _lastCounter = 0;
        }
        else
        {
            _lastCounter = checked(_lastCounter + 1);
        }

        return new HlcStamp(_lastMicros, _lastCounter, Peer);
    }

    /// <summary>
    /// Observes <paramref name="remote"/> and returns a fresh local stamp causally after it.
    /// </summary>
    public HlcStamp Receive(HlcStamp remote, long nowMicros)
    {
        var micros = Math.Max(Math.Max(_lastMicros, remote.Micros), nowMicros);
        if (micros == _lastMicros && micros == remote.Micros)
        {
            _lastCounter = checked(Math.Max(_lastCounter, remote.Counter) + 1);
        }
        else if (micros == _lastMicros)
        {
            _lastCounter = checked(_lastCounter + 1);
        }
        else if (micros == remote.Micros)
        {
            _lastCounter = checked(remote.Counter + 1);
        }
        else
        {
            _lastCounter = 0;
        }

        _lastMicros = micros;
        return new HlcStamp(_lastMicros, _lastCounter, Peer);
    }
}

/// <summary>A last-writer-wins register ordered by <see cref="HlcStamp"/>.</summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class LwwRegister<T> : IRegisterCrdt<LwwRegister<T>, T>
{
    private readonly IEqualityComparer<T> _comparer;

    /// <summary>Creates a register holding <paramref name="value"/> at <paramref name="stamp"/>.</summary>
    public LwwRegister(
        T value,
        HlcStamp stamp,
        IEqualityComparer<T>? comparer = null)
    {
        Value = value;
        Stamp = stamp;
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    /// <inheritdoc />
    public MergeMechanism Mechanism => MergeMechanism.Lww;

    /// <inheritdoc />
    public T Value { get; private set; }

    /// <summary>The winning stamp.</summary>
    public HlcStamp Stamp { get; private set; }

    /// <summary>
    /// Applies a local write if its stamp wins.
    /// </summary>
    /// <returns>True when the register state accepted the write.</returns>
    public bool Set(T value, HlcStamp stamp)
    {
        if (stamp.CompareTo(Stamp) <= 0) return false;

        Value = value;
        Stamp = stamp;
        return true;
    }

    /// <inheritdoc />
    public bool MergeFrom(LwwRegister<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Stamp.CompareTo(Stamp) <= 0) return false;

        var changed = !_comparer.Equals(Value, other.Value);
        Value = other.Value;
        Stamp = other.Stamp;
        return changed;
    }

    /// <summary>Returns an independent copy of this register.</summary>
    public LwwRegister<T> Copy() => new(Value, Stamp, _comparer);
}

/// <summary>
/// A multi-value register. Concurrent writes remain visible; a later local write causally
/// supersedes every value currently observed by that replica.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class MvRegister<T>
    : IRegisterCrdt<MvRegister<T>, IReadOnlyList<T>>
{
    private readonly IEqualityComparer<T> _comparer;
    private List<Entry> _entries = [];

    /// <summary>Creates an empty register.</summary>
    public MvRegister(IEqualityComparer<T>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    /// <inheritdoc />
    public MergeMechanism Mechanism => MergeMechanism.Crdt;

    /// <inheritdoc />
    public IReadOnlyList<T> Value => Values;

    /// <summary>The visible concurrent values in deterministic version-vector order.</summary>
    public IReadOnlyList<T> Values => _entries.Select(entry => entry.Value).ToArray();

    /// <summary>
    /// Writes a value for <paramref name="peer"/>, causally after all entries this replica has
    /// observed.
    /// </summary>
    /// <returns>True exactly when the observable values changed.</returns>
    public bool Set(T value, int peer)
    {
        var floor = new VersionVector();
        foreach (var entry in _entries) floor.MergeMax(entry.Version);

        var next = new VersionVector();
        next.Bump(peer, floor);
        var changed = _entries.Count != 1 || !_comparer.Equals(_entries[0].Value, value);
        _entries = [new Entry(next, value)];
        return changed;
    }

    /// <inheritdoc />
    public bool MergeFrom(MvRegister<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var before = Values;
        _entries.AddRange(other._entries.Select(entry => entry.Copy()));
        Normalize();
        return !SequenceEqual(before, Values);
    }

    /// <summary>Returns an independent copy of this register.</summary>
    public MvRegister<T> Copy()
    {
        var copy = new MvRegister<T>(_comparer);
        copy._entries = _entries.Select(entry => entry.Copy()).ToList();
        return copy;
    }

    private void Normalize()
    {
        var candidates = _entries;
        var kept = new List<Entry>();

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var strictlyDominated = candidates.Any(
                other =>
                    !ReferenceEquals(other, candidate)
                    && other.Version.Dominates(candidate.Version)
                    && !other.Version.Equals(candidate.Version));
            if (strictlyDominated) continue;

            if (kept.Any(
                    other =>
                        other.Version.Equals(candidate.Version)
                        && _comparer.Equals(other.Value, candidate.Value)))
            {
                continue;
            }

            kept.Add(candidate);
        }

        kept.Sort((left, right) => left.Version.CompareTo(right.Version));
        _entries = kept;
    }

    private bool SequenceEqual(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!_comparer.Equals(left[index], right[index])) return false;
        }

        return true;
    }

    private sealed record Entry(VersionVector Version, T Value)
    {
        public Entry Copy() => new(Version.Copy(), Value);
    }

    private sealed class VersionVector : IEquatable<VersionVector>, IComparable<VersionVector>
    {
        private readonly SortedDictionary<int, ulong> _components = [];

        public void Bump(int peer, VersionVector floor)
        {
            MergeMax(floor);
            _components[peer] = checked(Get(peer) + 1);
        }

        public void MergeMax(VersionVector other)
        {
            foreach (var (peer, counter) in other._components)
            {
                _components[peer] = Math.Max(Get(peer), counter);
            }
        }

        public bool Dominates(VersionVector other) =>
            other._components.All(pair => Get(pair.Key) >= pair.Value);

        public VersionVector Copy()
        {
            var copy = new VersionVector();
            copy.MergeMax(this);
            return copy;
        }

        public int CompareTo(VersionVector? other)
        {
            if (other is null) return 1;
            using var left = _components.GetEnumerator();
            using var right = other._components.GetEnumerator();
            while (true)
            {
                var hasLeft = left.MoveNext();
                var hasRight = right.MoveNext();
                if (!hasLeft || !hasRight) return hasLeft.CompareTo(hasRight);

                var peerOrder = left.Current.Key.CompareTo(right.Current.Key);
                if (peerOrder != 0) return peerOrder;
                var counterOrder = left.Current.Value.CompareTo(right.Current.Value);
                if (counterOrder != 0) return counterOrder;
            }
        }

        public bool Equals(VersionVector? other) =>
            other is not null && _components.SequenceEqual(other._components);

        public override bool Equals(object? obj) => obj is VersionVector other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var component in _components) hash.Add(component);
            return hash.ToHashCode();
        }

        private ulong Get(int peer) => _components.GetValueOrDefault(peer);
    }
}

/// <summary>
/// A positive-negative counter whose per-peer components merge by maximum.
/// </summary>
public sealed class PnCounter : IRegisterCrdt<PnCounter, long>
{
    private readonly SortedDictionary<int, ulong> _increments = [];
    private readonly SortedDictionary<int, ulong> _decrements = [];

    /// <inheritdoc />
    public MergeMechanism Mechanism => MergeMechanism.Crdt;

    /// <inheritdoc />
    public long Value
    {
        get
        {
            var increments = _increments.Values.Aggregate(0UL, checked((sum, value) => sum + value));
            var decrements = _decrements.Values.Aggregate(0UL, checked((sum, value) => sum + value));
            return checked((long)increments - (long)decrements);
        }
    }

    /// <summary>Adds <paramref name="amount"/> to <paramref name="peer"/>'s positive component.</summary>
    public void Increment(int peer, ulong amount = 1)
    {
        _increments[peer] = checked(_increments.GetValueOrDefault(peer) + amount);
    }

    /// <summary>Adds <paramref name="amount"/> to <paramref name="peer"/>'s negative component.</summary>
    public void Decrement(int peer, ulong amount = 1)
    {
        _decrements[peer] = checked(_decrements.GetValueOrDefault(peer) + amount);
    }

    /// <inheritdoc />
    public bool MergeFrom(PnCounter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var before = Value;
        MergeMax(_increments, other._increments);
        MergeMax(_decrements, other._decrements);
        return Value != before;
    }

    /// <summary>Returns an independent copy of this counter.</summary>
    public PnCounter Copy()
    {
        var copy = new PnCounter();
        MergeMax(copy._increments, _increments);
        MergeMax(copy._decrements, _decrements);
        return copy;
    }

    private static void MergeMax(
        IDictionary<int, ulong> target,
        IReadOnlyDictionary<int, ulong> source)
    {
        foreach (var (peer, count) in source)
        {
            target.TryGetValue(peer, out var current);
            target[peer] = Math.Max(current, count);
        }
    }
}

/// <summary>
/// Binds a CRDT replica to a reactive root source. Redundant state merges update no graph edge.
/// </summary>
/// <typeparam name="TCrdt">The CRDT state type.</typeparam>
/// <typeparam name="TValue">The observable value type.</typeparam>
public sealed class ReplicatedCell<TCrdt, TValue>
    where TCrdt : ICellCrdt<TCrdt, TValue>
{
    private readonly IEqualityComparer<TValue> _comparer;

    /// <summary>Creates a reactive source seeded with <paramref name="crdt"/>'s current value.</summary>
    public ReplicatedCell(
        Context context,
        TCrdt crdt,
        IEqualityComparer<TValue>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(crdt);
        _comparer = comparer ?? EqualityComparer<TValue>.Default;
        Crdt = crdt;
        Handle = context.Source(crdt.Value, _comparer);
    }

    /// <summary>The mutable CRDT replica.</summary>
    public TCrdt Crdt { get; }

    /// <summary>The reactive source projected from the replica.</summary>
    public Source<TValue> Handle { get; }

    /// <summary>The current converged value.</summary>
    public TValue Value => Crdt.Value;

    /// <summary>Merges a remote replica and updates the source only on an observable change.</summary>
    public bool MergeRemote(TCrdt remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (!Crdt.MergeFrom(remote)) return false;

        Handle.Set(Crdt.Value);
        return true;
    }

    /// <summary>Applies a local mutation and updates the source only on an observable change.</summary>
    public bool Update(Action<TCrdt> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var before = Crdt.Value;
        mutate(Crdt);
        var after = Crdt.Value;
        if (_comparer.Equals(before, after)) return false;

        Handle.Set(after);
        return true;
    }
}
