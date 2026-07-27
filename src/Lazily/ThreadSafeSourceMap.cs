using System;
using System.Collections.Generic;
using System.Linq;

namespace Lazily;

/// <summary>
/// A keyed map of input sources whose complete surface is serialized by a
/// <see cref="ThreadSafeContext"/>.
/// </summary>
/// <remarks>
/// This is a lock projection of the real <see cref="SourceMap{TKey,TValue}"/>,
/// not a hand-locked substitute in a conformance runner. Entry handles,
/// membership, order, and value invalidation all belong to the wrapped graph.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class ThreadSafeSourceMap<TKey, TValue>
where TKey : notnull
{
    private readonly ThreadSafeContext _ctx;
    private readonly SourceMap<TKey, TValue> _inner;

    /// <summary>Creates an empty thread-safe source map.</summary>
    /// <param name="ctx">The owning thread-safe context.</param>
    public ThreadSafeSourceMap(ThreadSafeContext ctx)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _ctx = ctx;
        _inner = ctx.WithLock(inner => new SourceMap<TKey, TValue>(inner));
    }

    /// <summary>What kind of node this map's entries are.</summary>
    public EntryKind Kind => EntryKind.Source;

    /// <summary>Returns the entry source, minting it from <paramref name="defaultValue"/> if absent.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="defaultValue">The cold-key initializer.</param>
    /// <returns>The stable entry source.</returns>
    public Source<TValue> EntryWith(TKey key, Func<TValue> defaultValue) =>
        _ctx.WithLock(_ => _inner.EntryWith(key, defaultValue));

    /// <summary>Returns the entry source, minting it with <paramref name="defaultValue"/> if absent.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="defaultValue">The cold-key value.</param>
    /// <returns>The stable entry source.</returns>
    public Source<TValue> Entry(TKey key, TValue defaultValue) =>
        _ctx.WithLock(_ => _inner.Entry(key, defaultValue));

    /// <summary>Writes an entry, minting it if absent.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The new value.</param>
    public void Set(TKey key, TValue value) => _ctx.WithLock(_ => _inner.Set(key, value));

    /// <summary>Reads an entry if present.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The observed value.</param>
    /// <returns>Whether the key was present.</returns>
    public bool TryObserve(TKey key, out TValue value)
    {
        var result = _ctx.WithLock(_ =>
        {
            var found = _inner.TryObserve(key, out var observed);
            return (found, observed);
        });
        value = result.observed;
        return result.found;
    }

    /// <summary>Returns the actual entry handle if present.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="handle">The stable source handle.</param>
    /// <returns>Whether the key was present.</returns>
    public bool TryGetHandle(TKey key, out Source<TValue> handle)
    {
        var result = _ctx.WithLock(_ =>
        {
            var found = _inner.TryGetHandle(key, out var foundHandle);
            return (found, foundHandle);
        });
        handle = result.foundHandle;
        return result.found;
    }

    /// <summary>Removes an entry and invalidates its old source handle.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether an entry was removed.</returns>
    public bool Remove(TKey key) => _ctx.WithLock(_ => _inner.Remove(key));

    /// <summary>Returns a non-reactive ordered-key snapshot.</summary>
    /// <returns>The keys in current order.</returns>
    public IReadOnlyList<TKey> PresentKeys() =>
        _ctx.WithLock(_ => _inner.PresentKeys().ToArray());

    /// <summary>How many entries are materialized.</summary>
    public int PresentCount => _ctx.WithLock(_ => _inner.PresentCount);

    /// <summary>Whether a key is materialized.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether the key is present.</returns>
    public bool IsPresent(TKey key) => _ctx.WithLock(_ => _inner.IsPresent(key));

    /// <summary>Reads the ordered key list through the order plane.</summary>
    /// <param name="ops">The enclosing computation, when tracked.</param>
    /// <returns>A snapshot of the keys in current order.</returns>
    public IReadOnlyList<TKey> Keys(IComputeOps? ops = null) =>
        _ctx.WithLock(_ => _inner.Keys(ops).ToArray());

    /// <summary>Reads the entry count through the membership plane.</summary>
    /// <param name="ops">The enclosing computation, when tracked.</param>
    /// <returns>The entry count.</returns>
    public int Len(IComputeOps? ops = null) => _ctx.WithLock(_ => _inner.Len(ops));

    /// <summary>Reads membership for a key through the membership plane.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="ops">The enclosing computation, when tracked.</param>
    /// <returns>Whether the key is present.</returns>
    public bool ContainsKey(TKey key, IComputeOps? ops = null) =>
        _ctx.WithLock(_ => _inner.ContainsKey(key, ops));

    /// <summary>Returns a key's current position.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="index">The position when present.</param>
    /// <returns>Whether the key is present.</returns>
    public bool TryPosition(TKey key, out int index)
    {
        var result = _ctx.WithLock(_ =>
        {
            var found = _inner.TryPosition(key, out var foundIndex);
            return (found, foundIndex);
        });
        index = result.foundIndex;
        return result.found;
    }

    /// <summary>Atomically moves a key to an absolute index.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="index">The target index.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveTo(TKey key, int index) => _ctx.WithLock(_ => _inner.MoveTo(key, index));

    /// <summary>Atomically moves a key before an anchor.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="anchor">The anchor key.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveBefore(TKey key, TKey anchor) =>
        _ctx.WithLock(_ => _inner.MoveBefore(key, anchor));

    /// <summary>Atomically moves a key after an anchor.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="anchor">The anchor key.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveAfter(TKey key, TKey anchor) =>
        _ctx.WithLock(_ => _inner.MoveAfter(key, anchor));

    /// <summary>Inserts a new key at a requested position.</summary>
    /// <param name="key">The new key.</param>
    /// <param name="value">Its value.</param>
    /// <param name="at">Where to place it.</param>
    /// <param name="index">The absolute index for <see cref="InsertAt.Index"/>.</param>
    /// <param name="anchor">The anchor for relative insertion.</param>
    /// <returns>Whether a new entry was created.</returns>
    public bool Insert(
        TKey key,
        TValue value,
        InsertAt at = InsertAt.End,
        int index = 0,
        TKey? anchor = default) =>
        _ctx.WithLock(_ => _inner.Insert(key, value, at, index, anchor));

    /// <summary>Reconciles this map to a target order and value set.</summary>
    /// <param name="targetOrder">The desired order.</param>
    /// <param name="targetValues">The desired values.</param>
    /// <returns>The applied reconciliation operations.</returns>
    public IReadOnlyList<DiffOp<TKey, TValue>> Reconcile(
        IReadOnlyList<TKey> targetOrder,
        IReadOnlyDictionary<TKey, TValue> targetValues) =>
        _ctx.WithLock(_ => _inner.Reconcile(targetOrder, targetValues));
}
