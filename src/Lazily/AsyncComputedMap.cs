using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Lazily;

/// <summary>
/// A keyed map of DERIVED ASYNC SLOTS: the materialization surface on the async plane.
/// </summary>
/// <remarks>
/// <para>
/// A distinct type rather than a wrapper, because <see cref="AsyncContext"/> is a distinct graph
/// with its own handles — the same reason <c>AsyncContext</c> itself is not a facade over
/// <c>Context</c>. What it shares with <see cref="ComputedMap{TKey,TValue}"/> is the CONTRACT: eager
/// materialization is a pre-mint loop, lazy is mint-on-access, and the two are observationally
/// identical.
/// </para>
/// <para>
/// "Eventual transparency" is the async form of that contract. The values arrive later, but the
/// present set behaves exactly as it does synchronously — a key is materialized when it is minted,
/// not when it resolves — so a deferred slot is deferred ALLOCATION, never a deferred answer.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class AsyncComputedMap<TKey, TValue>
    where TKey : notnull
{
    private readonly AsyncContext _ctx;
    /// <summary>
    /// Present set + key order + the move algebra, shared with the other two
    /// flavors. Graph-agnostic; the reactivity below is this map's own.
    /// </summary>
    private readonly KeyedOrder<TKey, AsyncComputed<TValue>> _keyed = new();
    private readonly AsyncSource<int> _membership;

    /// <summary>Bumped on add/remove AND on move — every change to the ordered list.</summary>
    private readonly AsyncSource<int> _orderSignal;
    private int _membershipVersion;
    private int _orderVersion;

    /// <summary>Creates an empty async derived-slot map bound to <paramref name="ctx"/>.</summary>
    /// <param name="ctx">The owning async context.</param>
    public AsyncComputedMap(AsyncContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        _membership = ctx.Source(0);
        _orderSignal = ctx.Source(0);
    }

    /// <summary>What kind of node this map's entries are.</summary>
    public EntryKind Kind => EntryKind.Computed;

    /// <summary>The materialized key list, in first-materialization order. Non-reactive.</summary>
    /// <returns>The materialized keys.</returns>
    public IReadOnlyList<TKey> PresentKeys() => _keyed.Keys();

    /// <summary>How many entries are materialized. Non-reactive.</summary>
    public int PresentCount => _keyed.Length;

    /// <summary>
    /// The entry node for <paramref name="key"/>, if present. Non-reactive.
    /// Parity with the single-threaded map, which has always exposed this — and
    /// what makes a removed node's disposal observable at all.
    /// </summary>
    /// <param name="key">The entry key.</param>
    /// <param name="handle">The entry node when present.</param>
    /// <returns>Whether the entry exists.</returns>
    public bool TryGetHandle(TKey key, out AsyncComputed<TValue> handle) =>
        _keyed.TryGet(key, out handle);

    /// <summary>Whether <paramref name="key"/> is materialized. Non-reactive.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether the entry exists.</returns>
    public bool IsPresent(TKey key) => _keyed.Contains(key);

    /// <summary>EAGER materialization: pre-mints a slot for every key in <paramref name="keys"/>.</summary>
    /// <remarks>Minting, not resolving — the values are pulled when something reads them.</remarks>
    /// <param name="keys">The keys to materialize.</param>
    /// <param name="factory">The canonical value producer for a key.</param>
    public void MaterializeAll(IEnumerable<TKey> keys, Func<TKey, TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys) Mint(key, factory);
    }

    /// <summary>LAZY materialization: awaits <paramref name="key"/>, minting it on first access.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="factory">The canonical value producer, invoked only on a cold key.</param>
    /// <returns>The entry's value.</returns>
    public Task<TValue> GetOrInsertWithAsync(TKey key, Func<TKey, TValue> factory)
    {
        if (_keyed.TryGet(key, out var warm)) return warm.GetAsync();
        return Mint(key, factory).GetAsync();
    }

    /// <summary>Awaits <paramref name="key"/> if it is materialized.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>The value, or null when the entry is absent.</returns>
    public async Task<(bool Found, TValue Value)> TryObserveAsync(TKey key)
    {
        if (!_keyed.TryGet(key, out var handle)) return (false, default!);
        return (true, await handle.GetAsync().ConfigureAwait(false));
    }

    /// <summary>Removes an entry.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether an entry was removed.</returns>
    public bool Remove(TKey key)
    {
        if (!_keyed.Remove(key, out var removed).Changed()) return false;

        // Dispose the removed node. Without this a reader holding the handle keeps
        // being served the removed entry's last resolved value — this map was the
        // one flavor whose Remove did not tear the node down.
        removed.Dispose();
        BumpMembership();
        return true;
    }

    /// <summary>
    /// The entry count, read through the membership plane.
    /// </summary>
    /// <remarks>
    /// Pass <paramref name="compute"/> from inside an async body to register the
    /// edge. Without it this read is untracked — which is what it always was: the
    /// membership cell was written on every mutation and never read anywhere, so
    /// the "reactive" count in the doc comment was decoration and the cell was
    /// dead code.
    /// </remarks>
    /// <param name="compute">The caller's async compute surface.</param>
    /// <returns>The entry count.</returns>
    public int Len(AsyncCompute? compute = null)
    {
        if (compute is not null) compute.Track(_membership);
        else _membership.Peek();
        return _keyed.Length;
    }

    // -- Core surface: ordering and atomic move --
    //
    // Ordering is not async-coloured: the move algebra touches no entry handle and
    // awaits nothing, so the async map carries the same Core surface as the other
    // two flavors.

    /// <summary>
    /// The reactive key list, in current order. Subscribes the caller to ORDER
    /// changes (add/remove and move/reorder), not to per-entry value changes.
    /// </summary>
    /// <param name="compute">The caller's async compute surface.</param>
    /// <returns>The keys in current order.</returns>
    public IReadOnlyList<TKey> Keys(AsyncCompute? compute = null)
    {
        if (compute is not null) compute.Track(_orderSignal);
        else _orderSignal.Peek();
        return _keyed.Keys();
    }

    /// <summary>Whether <paramref name="key"/> is a member. Reactive on membership.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="compute">The caller's async compute surface.</param>
    /// <returns>Whether the key is present.</returns>
    public bool ContainsKey(TKey key, AsyncCompute? compute = null)
    {
        if (compute is not null) compute.Track(_membership);
        else _membership.Peek();
        return _keyed.Contains(key);
    }

    /// <summary>The current 0-based position of <paramref name="key"/>. Non-reactive.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="index">The position when present.</param>
    /// <returns>Whether the key is present.</returns>
    public bool TryPosition(TKey key, out int index)
    {
        index = _keyed.Position(key);
        return index >= 0;
    }

    /// <summary>
    /// Atomically moves <paramref name="key"/> to <paramref name="index"/>
    /// (<c>#lzcellmove</c>). The entry keeps the same node, its dependents, and
    /// its lineage; only the order signal is bumped.
    /// </summary>
    /// <param name="key">The entry to move.</param>
    /// <param name="index">The target position, clamped into range.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveTo(TKey key, int index) => ApplyMove(_keyed.MoveTo(key, index));

    /// <summary>Atomically moves <paramref name="key"/> before <paramref name="anchor"/>.</summary>
    /// <param name="key">The entry to move.</param>
    /// <param name="anchor">The entry to move ahead of.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveBefore(TKey key, TKey anchor) => ApplyMove(_keyed.MoveBefore(key, anchor));

    /// <summary>Atomically moves <paramref name="key"/> after <paramref name="anchor"/>.</summary>
    /// <param name="key">The entry to move.</param>
    /// <param name="anchor">The entry to move behind.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveAfter(TKey key, TKey anchor) => ApplyMove(_keyed.MoveAfter(key, anchor));

    private bool ApplyMove(MapMove outcome)
    {
        if (!outcome.Applied()) return false;
        if (outcome.Changed()) BumpOrder();
        return true;
    }

    private void BumpOrder()
    {
        _orderVersion++;
        _orderSignal.Set(_orderVersion);
    }

    private AsyncComputed<TValue> Mint(TKey key, Func<TKey, TValue> factory)
    {
        if (_keyed.TryGet(key, out var existing)) return existing;
        var slot = _ctx.Computed<TValue>(_ => Task.FromResult(factory(key)));
        if (!_keyed.Insert(key, slot, out slot).Changed()) return slot;
        BumpMembership();
        return slot;
    }

    private void BumpMembership()
    {
        _membershipVersion++;
        _membership.Set(_membershipVersion);

        // The key set changed, so the ordered list changed too.
        BumpOrder();
    }
}
