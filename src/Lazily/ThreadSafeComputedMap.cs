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
}
