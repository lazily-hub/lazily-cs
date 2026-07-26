using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Lazily;

/// <summary>A hybrid logical clock stamp: physical micros, a tiebreak counter, and the origin peer.</summary>
/// <param name="Micros">The physical component.</param>
/// <param name="Counter">The logical tiebreak within one microsecond.</param>
/// <param name="Peer">The origin peer, the final tiebreak so the order is total.</param>
public readonly record struct HlcStamp(long Micros, long Counter, int Peer) : IComparable<HlcStamp>
{
    /// <summary>Orders two stamps. Total, so last-writer-wins never has to break a tie arbitrarily.</summary>
    /// <param name="other">The stamp to compare against.</param>
    /// <returns>The comparison result.</returns>
    public int CompareTo(HlcStamp other) =>
        (Micros, Counter, Peer).CompareTo((other.Micros, other.Counter, other.Peer));

    /// <summary>The wire form, for logging and divergence messages.</summary>
    /// <returns>The wire form.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Micros}.{Counter}@{Peer}");
}

/// <summary>A keyed last-writer-wins op for one family entry.</summary>
/// <typeparam name="TValue">The entry value type.</typeparam>
/// <param name="Namespace">The family namespace.</param>
/// <param name="KeySuffix">The entry key within the family.</param>
/// <param name="Value">The converged register value.</param>
/// <param name="Stamp">The write's stamp.</param>
public readonly record struct FamilyOp<TValue>(string Namespace, string KeySuffix, TValue Value, HlcStamp Stamp);

/// <summary>
/// One replica's view of reactive family-granularity sync.
/// </summary>
/// <remarks>
/// <para>
/// The gap this closes: a plain CRDT plane drops a keyed op for an entry it has never registered,
/// because there is no local node to merge into. So membership never propagates — a key created on
/// one replica is simply invisible on every other one, and any aggregate over the family silently
/// disagrees between peers forever.
/// </para>
/// <para>
/// Here an op for an unknown key MATERIALIZES the entry, seeded from the op's own converged
/// register. Seeding from the op state IS the pointwise merge, so this inherits full semilattice
/// convergence rather than bolting a special case onto it: ingest is idempotent, membership only
/// grows, and a later write converges by last-writer-wins on a total stamp order.
/// </para>
/// <para>
/// The aggregate is a real reactive derived over the membership epoch, not a recomputed scan — which
/// is what makes "a live count converges across replicas" a property of the graph rather than of
/// whoever remembers to re-read.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The entry value type.</typeparam>
public sealed class FamilySync<TValue>
{
    private readonly Context _ctx;
    private readonly HashSet<string> _families = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _members = new(StringComparer.Ordinal);
    private readonly Source<int> _membershipEpoch;

    private long _lastMicros;
    private long _lastCounter;
    private int _epoch;

    /// <summary>Creates a replica bound to <paramref name="ctx"/>.</summary>
    /// <param name="ctx">The owning context.</param>
    /// <param name="peerId">This replica's peer id, the final stamp tiebreak.</param>
    public FamilySync(Context ctx, int peerId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        PeerId = peerId;
        _membershipEpoch = ctx.Source(0);
    }

    /// <summary>This replica's peer id.</summary>
    public int PeerId { get; }

    /// <summary>
    /// The reactive membership signal. Bumped only when a family's present set GROWS.
    /// </summary>
    /// <remarks>
    /// A value update does not bump it: an aggregate that depends on membership alone must not
    /// recompute because someone flipped a bool, which is the same plane separation the keyed maps
    /// keep.
    /// </remarks>
    public Source<int> MembershipEpoch => _membershipEpoch;

    /// <summary>
    /// Registers a family namespace, so a keyed op for one of its entries materializes locally
    /// instead of being dropped.
    /// </summary>
    /// <remarks>Replicas that share a session must register the same namespaces.</remarks>
    /// <param name="ns">The family namespace.</param>
    public void RegisterFamily(string ns)
    {
        _families.Add(ns);
        _members.TryAdd(ns, []);
    }

    /// <summary>Writes a local entry and returns the op to broadcast.</summary>
    /// <param name="ns">The family namespace.</param>
    /// <param name="keySuffix">The entry key.</param>
    /// <param name="value">The new value.</param>
    /// <param name="nowMicros">The physical clock reading for this write.</param>
    /// <returns>The op to broadcast, or null when the write was stamp-dominated.</returns>
    public FamilyOp<TValue>? Set(string ns, string keySuffix, TValue value, long nowMicros)
    {
        var stamp = Tick(nowMicros);
        var op = new FamilyOp<TValue>(ns, keySuffix, value, stamp);
        return Ingest(op) ? op : null;
    }

    /// <summary>
    /// Applies a keyed op: a known key updates under last-writer-wins, an unknown key materializes.
    /// </summary>
    /// <param name="op">The op to apply.</param>
    /// <returns>Whether the winning value changed. False means the op was a no-op — a dominated stamp or an exact re-ingest.</returns>
    public bool Ingest(FamilyOp<TValue> op)
    {
        RegisterFamily(op.Namespace);
        Observe(op.Stamp);
        var path = Path(op.Namespace, op.KeySuffix);

        if (_entries.TryGetValue(path, out var existing))
        {
            // LWW: only a strictly greater stamp wins, which is what makes a re-ingest of the same
            // op a no-op rather than a redundant write.
            if (op.Stamp.CompareTo(existing.Stamp) <= 0) return false;
            _entries[path] = existing with { Value = op.Value, Stamp = op.Stamp };
            return true;
        }

        _entries[path] = new Entry(op.Namespace, op.KeySuffix, op.Value, op.Stamp);
        var members = _members[op.Namespace];
        if (!members.Contains(op.KeySuffix, StringComparer.Ordinal)) members.Add(op.KeySuffix);
        BumpEpoch();
        return true;
    }

    /// <summary>The present keys of <paramref name="ns"/>, in first-materialization order.</summary>
    /// <param name="ns">The family namespace.</param>
    /// <returns>The present keys.</returns>
    public IReadOnlyList<string> Keys(string ns) => _members.TryGetValue(ns, out var m) ? [.. m] : [];

    /// <summary>How many entries of <paramref name="ns"/> are present.</summary>
    /// <param name="ns">The family namespace.</param>
    /// <returns>The present count.</returns>
    public int PresentCount(string ns) => Keys(ns).Count;

    /// <summary>Reads one entry's converged value.</summary>
    /// <param name="ns">The family namespace.</param>
    /// <param name="keySuffix">The entry key.</param>
    /// <param name="value">The value, when present.</param>
    /// <returns>Whether the entry is present.</returns>
    public bool TryValue(string ns, string keySuffix, out TValue value)
    {
        if (_entries.TryGetValue(Path(ns, keySuffix), out var e))
        {
            value = e.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// A reactive count of the entries of <paramref name="ns"/> satisfying <paramref name="predicate"/>.
    /// </summary>
    /// <remarks>
    /// Derived over <see cref="MembershipEpoch"/>, so it is a live aggregate: the fixture's claim is
    /// that this converges across replicas, and a scan re-run by hand at assertion time would prove
    /// nothing about the graph.
    /// </remarks>
    /// <param name="ns">The family namespace.</param>
    /// <param name="predicate">The entry predicate.</param>
    /// <returns>The derived count.</returns>
    public Computed<int> Aggregate(string ns, Func<TValue, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return _ctx.Computed(c =>
        {
            _ = _membershipEpoch.Get(c);
            return Keys(ns).Count(k => TryValue(ns, k, out var v) && predicate(v));
        });
    }

    private static string Path(string ns, string keySuffix) => ns + "/" + keySuffix;

    private void BumpEpoch()
    {
        _epoch++;
        _membershipEpoch.Set(_epoch);
    }

    /// <summary>Advances the local clock for an outgoing write.</summary>
    private HlcStamp Tick(long nowMicros)
    {
        var micros = Math.Max(nowMicros, _lastMicros);
        _lastCounter = micros == _lastMicros ? _lastCounter + 1 : 0;
        _lastMicros = micros;
        return new HlcStamp(micros, _lastCounter, PeerId);
    }

    /// <summary>Absorbs a remote stamp, so a later local write is ordered after everything seen.</summary>
    private void Observe(HlcStamp stamp)
    {
        if (stamp.Micros > _lastMicros)
        {
            _lastMicros = stamp.Micros;
            _lastCounter = stamp.Counter;
        }
        else if (stamp.Micros == _lastMicros && stamp.Counter > _lastCounter)
        {
            _lastCounter = stamp.Counter;
        }
    }

    private readonly record struct Entry(string Namespace, string KeySuffix, TValue Value, HlcStamp Stamp);
}
