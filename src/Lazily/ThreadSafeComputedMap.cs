using System;
using System.Collections.Generic;

namespace Lazily;

/// <summary>
/// A <see cref="ComputedMap{TKey,TValue}"/> serialized by a <see cref="ThreadSafeContext"/>'s lock.
/// </summary>
/// <remarks>
/// <para>
/// This REFINES the single-threaded map rather than reimplementing it: the entries, the three
/// reactive planes, and the eager/lazy materialization strategies are all the kernel's, and the only
/// thing added is that every operation runs inside the context's critical section. A second
/// implementation would be free to drift from the first — and the materialization laws are exactly
/// the kind that drift silently, because a wrong one still returns right-looking values.
/// </para>
/// <para>
/// The lock is reentrant, so a compute body that reads another entry of the same map does not
/// deadlock.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class ThreadSafeComputedMap<TKey, TValue>
    where TKey : notnull
{
    private readonly ThreadSafeContext _ctx;
    private readonly ComputedMap<TKey, TValue> _inner;

    /// <summary>Creates an empty thread-safe derived-slot map bound to <paramref name="ctx"/>.</summary>
    /// <param name="ctx">The owning thread-safe context.</param>
    public ThreadSafeComputedMap(ThreadSafeContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        _inner = ctx.WithLock(inner => new ComputedMap<TKey, TValue>(inner));
    }

    /// <summary>What kind of node this map's entries are.</summary>
    public EntryKind Kind => EntryKind.Computed;

    /// <summary>EAGER materialization: pre-mints a slot for every key.</summary>
    /// <param name="keys">The keys to materialize.</param>
    /// <param name="factory">The canonical value producer for a key.</param>
    public void MaterializeAll(IEnumerable<TKey> keys, Func<TKey, TValue> factory) =>
        _ctx.WithLock(_ => _inner.MaterializeAll(keys, factory));

    /// <summary>LAZY materialization: reads <paramref name="key"/>, minting it on first access.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="factory">The canonical value producer, invoked only on a cold key.</param>
    /// <returns>The entry's value.</returns>
    public TValue GetOrInsertWith(TKey key, Func<TKey, TValue> factory) =>
        _ctx.WithLock(_ => _inner.GetOrInsertWith(key, factory));

    /// <summary>The materialized key list. Non-reactive.</summary>
    /// <returns>The materialized keys in order.</returns>
    public IReadOnlyList<TKey> PresentKeys() => _ctx.WithLock(_ => _inner.PresentKeys());

    /// <summary>How many entries are materialized. Non-reactive.</summary>
    public int PresentCount => _ctx.WithLock(_ => _inner.PresentCount);

    /// <summary>Whether <paramref name="key"/> is materialized. Non-reactive.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether the entry exists.</returns>
    public bool IsPresent(TKey key) => _ctx.WithLock(_ => _inner.IsPresent(key));

    /// <summary>Reads <paramref name="key"/> if materialized.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The value, when present.</param>
    /// <returns>Whether the entry is materialized.</returns>
    public bool TryObserve(TKey key, out TValue value)
    {
        var (found, observed) = _ctx.WithLock(_ =>
        {
            var ok = _inner.TryObserve(key, out var v);
            return (ok, v);
        });
        value = observed;
        return found;
    }

    /// <summary>Removes an entry, detaching its node from the graph.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether an entry was removed.</returns>
    public bool Remove(TKey key) => _ctx.WithLock(_ => _inner.Remove(key));

    /// <summary>The entry count, subscribing the caller to MEMBERSHIP.</summary>
    /// <returns>The entry count.</returns>
    public int Len() => _ctx.WithLock(_ => _inner.Len());

    // -- Core surface: ordering, atomic move, reactive membership --
    //
    // These bind every flavor. The move algebra touches no entry handle and
    // awaits nothing, so it is neither thread- nor async-coloured. This map is a
    // delegating shell over a real ComputedMap, and the thread-safe context
    // projects onto the same graph, so closing its gap really is delegation —
    // there was never a missing primitive, only a missing surface.

    /// <summary>
    /// The reactive key list, in current order. Subscribes the caller to ORDER
    /// changes (add/remove and move/reorder), not to per-entry value changes.
    /// </summary>
    /// <param name="ops">The caller's read surface; a compute registers the edge.</param>
    /// <returns>The keys in current order.</returns>
    public IReadOnlyList<TKey> Keys(IComputeOps? ops = null) =>
        _ctx.WithLock(_ => _inner.Keys(ops));

    /// <summary>Whether <paramref name="key"/> is a member. Reactive on membership.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="ops">The caller's read surface.</param>
    /// <returns>Whether the key is present.</returns>
    public bool ContainsKey(TKey key, IComputeOps? ops = null) =>
        _ctx.WithLock(_ => _inner.ContainsKey(key, ops));

    /// <summary>The current 0-based position of <paramref name="key"/>. Non-reactive.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="index">The position when present.</param>
    /// <returns>Whether the key is present.</returns>
    public bool TryPosition(TKey key, out int index)
    {
        var found = false;
        var at = -1;
        _ctx.WithLock(_ =>
        {
            found = _inner.TryPosition(key, out at);
            return 0;
        });
        index = at;
        return found;
    }

    /// <summary>
    /// Atomically moves <paramref name="key"/> to <paramref name="index"/>
    /// (<c>#lzcellmove</c>). The entry keeps the same node, its dependents, and
    /// its lineage; only the order signal is bumped.
    /// </summary>
    /// <param name="key">The entry to move.</param>
    /// <param name="index">The target position, clamped into range.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveTo(TKey key, int index) => _ctx.WithLock(_ => _inner.MoveTo(key, index));

    /// <summary>Atomically moves <paramref name="key"/> before <paramref name="anchor"/>.</summary>
    /// <param name="key">The entry to move.</param>
    /// <param name="anchor">The entry to move ahead of.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveBefore(TKey key, TKey anchor) =>
        _ctx.WithLock(_ => _inner.MoveBefore(key, anchor));

    /// <summary>Atomically moves <paramref name="key"/> after <paramref name="anchor"/>.</summary>
    /// <param name="key">The entry to move.</param>
    /// <param name="anchor">The entry to move behind.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveAfter(TKey key, TKey anchor) =>
        _ctx.WithLock(_ => _inner.MoveAfter(key, anchor));
}
