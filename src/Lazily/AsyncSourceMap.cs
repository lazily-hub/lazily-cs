using System;
using System.Collections.Generic;
using System.Linq;

namespace Lazily;

/// <summary>A keyed map of mutable input sources on an <see cref="AsyncContext"/>.</summary>
/// <remarks>
/// Source operations remain synchronous on the async graph. The async color
/// appears only when a derived reader awaits a value; inside such a body,
/// passing its <see cref="AsyncCompute"/> registers the dependency edge.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class AsyncSourceMap<TKey, TValue>
where TKey : notnull
{
    private readonly AsyncContext _ctx;
    private readonly KeyedOrder<TKey, AsyncSource<TValue>> _keyed = new();
    private readonly AsyncSource<int> _membership;
    private readonly AsyncSource<int> _orderSignal;
    private int _membershipVersion;
    private int _orderVersion;

    /// <summary>Creates an empty async source map.</summary>
    /// <param name="ctx">The owning async context.</param>
    public AsyncSourceMap(AsyncContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        _membership = ctx.Source(0);
        _orderSignal = ctx.Source(0);
    }

    /// <summary>What kind of node this map's entries are.</summary>
    public EntryKind Kind => EntryKind.Source;

    /// <summary>Returns the entry source, minting it from <paramref name="defaultValue"/> if absent.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="defaultValue">The cold-key initializer.</param>
    /// <returns>The stable entry source.</returns>
    public AsyncSource<TValue> EntryWith(TKey key, Func<TValue> defaultValue)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        if (_keyed.TryGet(key, out var existing)) return existing;
        var source = _ctx.Source(defaultValue());
        if (!_keyed.Insert(key, source, out source).Changed()) return source;
        BumpMembership();
        return source;
    }

    /// <summary>Returns the entry source, minting it with <paramref name="defaultValue"/> if absent.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="defaultValue">The cold-key value.</param>
    /// <returns>The stable entry source.</returns>
    public AsyncSource<TValue> Entry(TKey key, TValue defaultValue) =>
        EntryWith(key, () => defaultValue);

    /// <summary>Writes an entry, minting it if absent.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The new value.</param>
    public void Set(TKey key, TValue value) => Entry(key, value).Set(value);

    /// <summary>Returns the actual entry handle if present.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="handle">The stable async source handle.</param>
    /// <returns>Whether the key was present.</returns>
    public bool TryGetHandle(TKey key, out AsyncSource<TValue> handle) =>
        _keyed.TryGet(key, out handle);

    /// <summary>Reads an entry if present, optionally registering an async dependency.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The observed value.</param>
    /// <param name="compute">The enclosing async computation, when tracked.</param>
    /// <returns>Whether the key was present.</returns>
    public bool TryObserve(TKey key, out TValue value, AsyncCompute? compute = null)
    {
        if (_keyed.TryGet(key, out var handle))
        {
            value = compute is null ? handle.Peek() : compute.Track(handle);
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Removes an entry and disposes its source node.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether an entry was removed.</returns>
    public bool Remove(TKey key)
    {
        if (!_keyed.Remove(key, out var removed).Changed()) return false;
        removed.Dispose();
        BumpMembership();
        return true;
    }

    /// <summary>Returns a non-reactive ordered-key snapshot.</summary>
    /// <returns>The keys in current order.</returns>
    public IReadOnlyList<TKey> PresentKeys() => _keyed.Keys().ToArray();

    /// <summary>How many entries are materialized.</summary>
    public int PresentCount => _keyed.Length;

    /// <summary>Whether a key is materialized.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether the key is present.</returns>
    public bool IsPresent(TKey key) => _keyed.Contains(key);

    /// <summary>Reads the ordered key list through the order plane.</summary>
    /// <param name="compute">The enclosing async computation, when tracked.</param>
    /// <returns>A snapshot of the keys in current order.</returns>
    public IReadOnlyList<TKey> Keys(AsyncCompute? compute = null)
    {
        if (compute is not null) compute.Track(_orderSignal);
        else _orderSignal.Peek();
        return _keyed.Keys().ToArray();
    }

    /// <summary>Reads the entry count through the membership plane.</summary>
    /// <param name="compute">The enclosing async computation, when tracked.</param>
    /// <returns>The entry count.</returns>
    public int Len(AsyncCompute? compute = null)
    {
        if (compute is not null) compute.Track(_membership);
        else _membership.Peek();
        return _keyed.Length;
    }

    /// <summary>Reads membership for a key through the membership plane.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="compute">The enclosing async computation, when tracked.</param>
    /// <returns>Whether the key is present.</returns>
    public bool ContainsKey(TKey key, AsyncCompute? compute = null)
    {
        if (compute is not null) compute.Track(_membership);
        else _membership.Peek();
        return _keyed.Contains(key);
    }

    /// <summary>Returns a key's current position.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="index">The position when present.</param>
    /// <returns>Whether the key is present.</returns>
    public bool TryPosition(TKey key, out int index)
    {
        index = _keyed.Position(key);
        return index >= 0;
    }

    /// <summary>Atomically moves a key to an absolute index.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="index">The target index.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveTo(TKey key, int index) => ApplyMove(_keyed.MoveTo(key, index));

    /// <summary>Atomically moves a key before an anchor.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="anchor">The anchor key.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveBefore(TKey key, TKey anchor) => ApplyMove(_keyed.MoveBefore(key, anchor));

    /// <summary>Atomically moves a key after an anchor.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="anchor">The anchor key.</param>
    /// <returns>Whether the move applied.</returns>
    public bool MoveAfter(TKey key, TKey anchor) => ApplyMove(_keyed.MoveAfter(key, anchor));

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
        TKey? anchor = default)
    {
        if (IsPresent(key)) return false;
        Entry(key, value);
        switch (at)
        {
            case InsertAt.Index: MoveTo(key, index); break;
            case InsertAt.Before when anchor is not null: MoveBefore(key, anchor); break;
            case InsertAt.After when anchor is not null: MoveAfter(key, anchor); break;
            default: break;
        }

        return true;
    }

    private bool ApplyMove(MapMove outcome)
    {
        if (!outcome.Applied()) return false;
        if (outcome.Changed()) BumpOrder();
        return true;
    }

    private void BumpMembership()
    {
        _membership.Set(++_membershipVersion);
        BumpOrder();
    }

    private void BumpOrder() => _orderSignal.Set(++_orderVersion);
}
