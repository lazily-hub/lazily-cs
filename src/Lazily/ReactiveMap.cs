using System;
using System.Collections.Generic;
using System.Linq;

namespace Lazily;

/// <summary>What kind of node a keyed map's entries are.</summary>
/// <remarks>
/// Orthogonal to materialization strategy: a <see cref="SourceMap{TKey,TValue}"/> holds input cells,
/// which are always materialized on mint under any strategy, while a
/// <see cref="ComputedMap{TKey,TValue}"/> holds derived slots, which are materialized eagerly by a
/// pre-mint loop or lazily on first read. There is deliberately no eager/lazy mode FLAG — eager is
/// <see cref="ComputedMap{TKey,TValue}.MaterializeAll"/> and lazy is
/// <see cref="ReactiveMap{TKey,TValue,THandle}.GetOrInsertWith"/>, which is what makes
/// "materialization changes allocation timing, never observed values" checkable rather than
/// asserted. See the materialization corpus in lazily-spec.
/// </remarks>
public enum EntryKind
{
    /// <summary>Input cells — always materialized when the entry is minted.</summary>
    Cell,

    /// <summary>Derived slots — materialized eagerly by a pre-mint loop, or lazily on first read.</summary>
    Slot,
}

/// <summary>
/// The shared reactive-map core: a keyed collection whose membership, order, and per-entry values
/// are three INDEPENDENT reactive planes.
/// </summary>
/// <remarks>
/// <para>
/// Writing one entry's value invalidates only that entry's readers. Adding or removing a key
/// invalidates membership readers (<see cref="Len"/>, <see cref="ContainsKey"/>) and order readers
/// (<see cref="Keys"/>). A pure reorder — <see cref="MoveTo"/> and friends — invalidates order
/// readers ONLY, and keeps the moved entry's handle, dependents, and lineage: an atomic move is not
/// a remove plus a re-mint.
/// </para>
/// <para>
/// The handle-kind operations (mint / observe / clear) are supplied by the
/// <see cref="SourceMap{TKey,TValue}"/> / <see cref="ComputedMap{TKey,TValue}"/> constructor — the C#
/// analog of the Rust <c>MapHandle</c> trait and the Go constructor closures.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <typeparam name="THandle">The per-entry node handle (a source for cells, a slot for derived entries).</typeparam>
public class ReactiveMap<TKey, TValue, THandle>
    where TKey : notnull
{
    private readonly Context _ctx;
    private readonly Func<Context, Func<TValue>, THandle> _mint;
    private readonly Func<THandle, TValue> _observe;
    private readonly Action<THandle> _clear;

    private readonly Dictionary<TKey, THandle> _entries = [];

    /// <summary>The authoritative key list, in first-materialization order.</summary>
    private readonly List<TKey> _order = [];

    /// <summary>Bumped only when the SET of keys changes (add / remove).</summary>
    private readonly Source<int> _membership;

    /// <summary>Bumped on add / remove AND on move — every change to the ordered list.</summary>
    private readonly Source<int> _orderSignal;

    private int _membershipVersion;
    private int _orderVersion;

    private protected ReactiveMap(
        Context ctx,
        EntryKind kind,
        Func<Context, Func<TValue>, THandle> mint,
        Func<THandle, TValue> observe,
        Action<THandle> clear)
    {
        _ctx = ctx;
        Kind = kind;
        _mint = mint;
        _observe = observe;
        _clear = clear;
        _membership = ctx.Source(0);
        _orderSignal = ctx.Source(0);
    }

    /// <summary>What kind of node this map's entries are.</summary>
    public EntryKind Kind { get; }

    /// <summary>The owning context.</summary>
    private protected Context Ctx => _ctx;

    /// <summary>Bumps only the order signal. A pure move bumps this and nothing else.</summary>
    private void BumpOrder()
    {
        _orderVersion++;
        _orderSignal.Set(_orderVersion);
    }

    /// <summary>
    /// Bumps set membership, then the order signal too: the key set changed, so the ordered list
    /// necessarily did as well.
    /// </summary>
    private void BumpMembership()
    {
        _membershipVersion++;
        _membership.Set(_membershipVersion);
        BumpOrder();
    }

    /// <summary>
    /// Mints the entry node for <paramref name="key"/> on first access, caches the handle, records
    /// order, and bumps membership. Re-minting an existing key returns the cached handle and bumps
    /// nothing.
    /// </summary>
    /// <param name="key">The entry key.</param>
    /// <param name="compute">The canonical value producer for this entry.</param>
    /// <returns>The entry handle.</returns>
    private protected THandle MintWith(TKey key, Func<TValue> compute)
    {
        if (_entries.TryGetValue(key, out var existing)) return existing; // warm: already allocated
        var handle = _mint(_ctx, compute);
        _entries[key] = handle;
        _order.Add(key);
        BumpMembership();
        return handle;
    }

    /// <summary>
    /// Reads the value at <paramref name="key"/>, minting the entry via <paramref name="factory"/>
    /// first if absent — the mint-on-access recipe, which is LAZY materialization for a slot map and
    /// a seeded input for a cell map.
    /// </summary>
    /// <remarks>
    /// Bumps membership only on insert. An existing key returns its current value WITHOUT re-running
    /// the factory, which is what makes a re-read observably free.
    /// </remarks>
    /// <param name="key">The entry key.</param>
    /// <param name="factory">The canonical value producer, invoked only on a cold key.</param>
    /// <returns>The entry's value.</returns>
    public TValue GetOrInsertWith(TKey key, Func<TKey, TValue> factory)
    {
        if (_entries.TryGetValue(key, out var warm)) return _observe(warm);
        var handle = MintWith(key, () => factory(key));
        return _observe(handle);
    }

    /// <summary>The existing entry handle, or <c>false</c>. Non-reactive: does not subscribe the caller.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="handle">The entry handle, when present.</param>
    /// <returns>Whether the entry is materialized.</returns>
    public bool TryGetHandle(TKey key, out THandle handle) => _entries.TryGetValue(key, out handle!);

    /// <summary>Reads the value at <paramref name="key"/> if materialized, subscribing the caller to that entry.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The entry's value, when present.</param>
    /// <returns>Whether the entry is materialized.</returns>
    public bool TryObserve(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var handle))
        {
            value = _observe(handle);
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Removes an entry, detaching its node from the graph so any reader that read it is notified
    /// that its source is gone rather than silently keeping a stale cache.
    /// </summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether an entry was removed.</returns>
    public bool Remove(TKey key)
    {
        if (!_entries.TryGetValue(key, out var handle)) return false;
        _entries.Remove(key);
        _order.Remove(key);
        _clear(handle);
        BumpMembership();
        return true;
    }

    /// <summary>The ordered key list. Subscribes the caller to the ORDER plane only.</summary>
    /// <param name="ops">The enclosing computation, when read from inside one.</param>
    /// <returns>The keys in order.</returns>
    public IReadOnlyList<TKey> Keys(IComputeOps? ops = null)
    {
        _ = ops is null ? _orderSignal.Get() : _orderSignal.Get(ops);
        return [.. _order];
    }

    /// <summary>The materialized key list, WITHOUT subscribing the caller to anything.</summary>
    /// <returns>The materialized keys in order.</returns>
    public IReadOnlyList<TKey> PresentKeys() => [.. _order];

    /// <summary>How many entries are materialized. Non-reactive.</summary>
    public int PresentCount => _order.Count;

    /// <summary>Whether <paramref name="key"/> is materialized. Non-reactive.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether the entry exists.</returns>
    public bool IsPresent(TKey key) => _entries.ContainsKey(key);

    /// <summary>The entry count. Subscribes the caller to the MEMBERSHIP plane only.</summary>
    /// <param name="ops">The enclosing computation, when read from inside one.</param>
    /// <returns>The entry count.</returns>
    public int Len(IComputeOps? ops = null)
    {
        _ = ops is null ? _membership.Get() : _membership.Get(ops);
        return _order.Count;
    }

    /// <summary>Whether the map holds <paramref name="key"/>. Subscribes the caller to MEMBERSHIP only.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="ops">The enclosing computation, when read from inside one.</param>
    /// <returns>Whether the key is present.</returns>
    public bool ContainsKey(TKey key, IComputeOps? ops = null)
    {
        _ = ops is null ? _membership.Get() : _membership.Get(ops);
        return _entries.ContainsKey(key);
    }

    /// <summary>The position of <paramref name="key"/> in the order, or <c>false</c>. Non-reactive.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="index">The position, when present.</param>
    /// <returns>Whether the key is present.</returns>
    public bool TryPosition(TKey key, out int index)
    {
        index = _order.IndexOf(key);
        return index >= 0;
    }

    /// <summary>Moves <paramref name="key"/> to <paramref name="index"/>, bumping ONLY the order signal.</summary>
    /// <remarks>
    /// The entry keeps its handle, its dependents, and its value: a move is a reorder, not a remove
    /// plus a re-mint. That distinction is the whole reason order and membership are separate
    /// signals.
    /// </remarks>
    /// <param name="key">The entry to move.</param>
    /// <param name="index">The target position, clamped into range.</param>
    /// <returns>Whether the order changed.</returns>
    public bool MoveTo(TKey key, int index)
    {
        var from = _order.IndexOf(key);
        if (from < 0) return false;
        var to = Math.Clamp(index, 0, _order.Count - 1);
        if (from == to) return false;
        _order.RemoveAt(from);
        _order.Insert(to, key);
        BumpOrder();
        return true;
    }

    /// <summary>Moves <paramref name="key"/> immediately before <paramref name="anchor"/>.</summary>
    /// <param name="key">The entry to move.</param>
    /// <param name="anchor">The entry to move ahead of.</param>
    /// <returns>Whether the order changed.</returns>
    public bool MoveBefore(TKey key, TKey anchor)
    {
        if (EqualityComparer<TKey>.Default.Equals(key, anchor)) return false;
        var anchorAt = _order.IndexOf(anchor);
        var from = _order.IndexOf(key);
        if (anchorAt < 0 || from < 0) return false;
        return MoveTo(key, from < anchorAt ? anchorAt - 1 : anchorAt);
    }

    /// <summary>Moves <paramref name="key"/> immediately after <paramref name="anchor"/>.</summary>
    /// <param name="key">The entry to move.</param>
    /// <param name="anchor">The entry to move behind.</param>
    /// <returns>Whether the order changed.</returns>
    public bool MoveAfter(TKey key, TKey anchor)
    {
        if (EqualityComparer<TKey>.Default.Equals(key, anchor)) return false;
        var anchorAt = _order.IndexOf(anchor);
        var from = _order.IndexOf(key);
        if (anchorAt < 0 || from < 0) return false;
        return MoveTo(key, from < anchorAt ? anchorAt : anchorAt + 1);
    }
}

/// <summary>A keyed map of INPUT CELLS. Entries are always materialized when minted, under any strategy.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public class SourceMap<TKey, TValue> : ReactiveMap<TKey, TValue, Source<TValue>>
    where TKey : notnull
{
    /// <summary>Creates an empty keyed cell collection bound to <paramref name="ctx"/>.</summary>
    /// <param name="ctx">The owning context.</param>
    public SourceMap(Context ctx)
        : base(
            ctx,
            EntryKind.Cell,
            static (c, compute) => c.Source(compute()),
            static h => h.Get(),
            // Invalidate the orphaned cell's dependents on remove: any reader that read this entry
            // is told its source is gone, rather than being left on a stale cache.
            static h => h.Invalidate())
    {
    }

    /// <summary>
    /// The value cell for <paramref name="key"/>, minting it with <paramref name="defaultValue"/> on
    /// first access. Adding a new key bumps membership; re-fetching an existing one does not.
    /// </summary>
    /// <param name="key">The entry key.</param>
    /// <param name="defaultValue">The seed value, invoked only on a cold key.</param>
    /// <returns>The entry's cell.</returns>
    public Source<TValue> EntryWith(TKey key, Func<TValue> defaultValue) => MintWith(key, defaultValue);

    /// <summary>The value cell for <paramref name="key"/>, minting it with <paramref name="defaultValue"/> on first access.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="defaultValue">The seed value.</param>
    /// <returns>The entry's cell.</returns>
    public Source<TValue> Entry(TKey key, TValue defaultValue) => EntryWith(key, () => defaultValue);

    /// <summary>Writes <paramref name="key"/>, minting the entry if absent. Cell-only: a derived slot is not settable.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The new value.</param>
    public void Set(TKey key, TValue value) => EntryWith(key, () => value).Set(value);

    /// <summary>Inserts <paramref name="key"/> at the requested position.</summary>
    /// <param name="key">The new key.</param>
    /// <param name="value">Its value.</param>
    /// <param name="at">Where to place it.</param>
    /// <param name="index">The absolute index, for <see cref="InsertAt.Index"/>.</param>
    /// <param name="anchor">The anchor key, for <see cref="InsertAt.Before"/> / <see cref="InsertAt.After"/>.</param>
    /// <returns>Whether a new entry was created.</returns>
    public bool Insert(TKey key, TValue value, InsertAt at = InsertAt.End, int index = 0, TKey? anchor = default)
    {
        if (IsPresent(key)) return false;
        Entry(key, value);            // mints at the end and bumps membership
        switch (at)
        {
            case InsertAt.Index: MoveTo(key, index); break;
            case InsertAt.Before when anchor is not null: MoveBefore(key, anchor); break;
            case InsertAt.After when anchor is not null: MoveAfter(key, anchor); break;
            default: break;           // End: already appended
        }

        return true;
    }

    /// <summary>
    /// Reconciles this map to <paramref name="targetOrder"/> / <paramref name="targetValues"/> by
    /// applying the minimal op set.
    /// </summary>
    /// <remarks>
    /// Keys held fixed by the longest-increasing-subsequence are not moved, so their value cells are
    /// never invalidated by a sibling reorder — applying the minimal op set per-cell is what makes
    /// that observable rather than merely intended.
    /// </remarks>
    /// <param name="targetOrder">The desired key order.</param>
    /// <param name="targetValues">The desired values.</param>
    /// <returns>The ops that were applied, in order.</returns>
    public IReadOnlyList<DiffOp<TKey, TValue>> Reconcile(
        IReadOnlyList<TKey> targetOrder,
        IReadOnlyDictionary<TKey, TValue> targetValues)
    {
        ArgumentNullException.ThrowIfNull(targetOrder);
        ArgumentNullException.ThrowIfNull(targetValues);

        var prior = PresentKeys()
            .Select(k => new KeyValuePair<TKey, TValue>(k, TryGetHandle(k, out var h) ? h.Peek() : default!))
            .ToList();
        var target = targetOrder.Select(k => new KeyValuePair<TKey, TValue>(k, targetValues[k])).ToList();

        var ops = Lazily.Reconcile.Diff(prior, target);
        foreach (var op in ops)
        {
            switch (op)
            {
                case DiffOpInsert<TKey, TValue> ins: Insert(ins.Key, ins.Value, InsertAt.Index, ins.Index); break;
                case DiffOpRemove<TKey, TValue> rem: Remove(rem.Key); break;
                case DiffOpMove<TKey, TValue> mv: MoveTo(mv.Key, mv.To); break;
                case DiffOpUpdate<TKey, TValue> upd: Set(upd.Key, upd.Value); break;
                default: break;
            }
        }

        return ops;
    }
}

/// <summary>A keyed map of DERIVED SLOTS — the subject of the materialization corpus.</summary>
/// <remarks>
/// There is no mode flag. Eager materialization is <see cref="MaterializeAll"/>, a pre-mint loop;
/// lazy materialization is <see cref="ReactiveMap{TKey,TValue,THandle}.GetOrInsertWith"/>'s
/// mint-on-access. A slot map has no <c>Set</c>: its entries are derived.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public class ComputedMap<TKey, TValue> : ReactiveMap<TKey, TValue, Computed<TValue>>
    where TKey : notnull
{
    /// <summary>Creates an empty keyed derived-slot collection bound to <paramref name="ctx"/>.</summary>
    /// <param name="ctx">The owning context.</param>
    public ComputedMap(Context ctx)
        : base(
            ctx,
            EntryKind.Slot,
            static (c, compute) => c.Slot(_ => compute()),
            static h => h.Get(),
            static h => h.Dispose())
    {
    }

    /// <summary>EAGER materialization: pre-mints a slot for every key in <paramref name="keys"/>.</summary>
    /// <remarks>
    /// Observationally equivalent to reading each key lazily — that equivalence is exactly what the
    /// observational-transparency fixture pins. Re-materializing an existing key is a no-op, so this
    /// never re-runs a factory or disturbs a warm entry.
    /// </remarks>
    /// <param name="keys">The keys to materialize.</param>
    /// <param name="factory">The canonical value producer for a key.</param>
    public void MaterializeAll(IEnumerable<TKey> keys, Func<TKey, TValue> factory)
    {
        foreach (var key in keys) MintWith(key, () => factory(key));
    }
}

/// <summary>Deprecated alias for <see cref="SourceMap{TKey,TValue}"/>.</summary>
/// <remarks>
/// The v2 kernel renamed the node kinds to <c>Source</c> and <c>Computed</c>, and the keyed-map
/// names followed. C# has no exportable generic type alias — a <c>using</c> alias is file-scoped —
/// so the old name survives as an obsolete SUBCLASS. Existing <c>new CellMap&lt;K,V&gt;(ctx)</c>
/// call sites keep compiling with a deprecation warning, and a <c>CellMap</c> is still accepted
/// everywhere a <see cref="SourceMap{TKey,TValue}"/> is expected. The converse does not hold: a
/// <see cref="SourceMap{TKey,TValue}"/> handed back by the library is not a <c>CellMap</c>.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
[Obsolete("renamed to SourceMap")]
public sealed class CellMap<TKey, TValue> : SourceMap<TKey, TValue>
    where TKey : notnull
{
    /// <summary>Creates an empty keyed cell collection bound to <paramref name="ctx"/>.</summary>
    /// <param name="ctx">The owning context.</param>
    public CellMap(Context ctx)
        : base(ctx)
    {
    }
}

/// <summary>Deprecated alias for <see cref="ComputedMap{TKey,TValue}"/>.</summary>
/// <remarks>
/// See <see cref="CellMap{TKey,TValue}"/> for why the old name is a subclass rather than an alias.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
[Obsolete("renamed to ComputedMap")]
public sealed class SlotMap<TKey, TValue> : ComputedMap<TKey, TValue>
    where TKey : notnull
{
    /// <summary>Creates an empty keyed derived-slot collection bound to <paramref name="ctx"/>.</summary>
    /// <param name="ctx">The owning context.</param>
    public SlotMap(Context ctx)
        : base(ctx)
    {
    }
}
