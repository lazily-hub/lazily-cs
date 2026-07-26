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
    private readonly Dictionary<TKey, AsyncComputed<TValue>> _entries = [];
    private readonly List<TKey> _order = [];
    private readonly AsyncSource<int> _membership;
    private int _membershipVersion;

    /// <summary>Creates an empty async derived-slot map bound to <paramref name="ctx"/>.</summary>
    /// <param name="ctx">The owning async context.</param>
    public AsyncComputedMap(AsyncContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        _membership = ctx.Source(0);
    }

    /// <summary>What kind of node this map's entries are.</summary>
    public EntryKind Kind => EntryKind.Slot;

    /// <summary>The materialized key list, in first-materialization order. Non-reactive.</summary>
    /// <returns>The materialized keys.</returns>
    public IReadOnlyList<TKey> PresentKeys() => [.. _order];

    /// <summary>How many entries are materialized. Non-reactive.</summary>
    public int PresentCount => _order.Count;

    /// <summary>Whether <paramref name="key"/> is materialized. Non-reactive.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether the entry exists.</returns>
    public bool IsPresent(TKey key) => _entries.ContainsKey(key);

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
        if (_entries.TryGetValue(key, out var warm)) return warm.GetAsync();
        return Mint(key, factory).GetAsync();
    }

    /// <summary>Awaits <paramref name="key"/> if it is materialized.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>The value, or null when the entry is absent.</returns>
    public async Task<(bool Found, TValue Value)> TryObserveAsync(TKey key)
    {
        if (!_entries.TryGetValue(key, out var handle)) return (false, default!);
        return (true, await handle.GetAsync().ConfigureAwait(false));
    }

    /// <summary>Removes an entry.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether an entry was removed.</returns>
    public bool Remove(TKey key)
    {
        if (!_entries.Remove(key)) return false;
        _order.Remove(key);
        BumpMembership();
        return true;
    }

    /// <summary>The entry count, read through the membership plane.</summary>
    /// <returns>The entry count.</returns>
    public int Len() => _order.Count;

    private AsyncComputed<TValue> Mint(TKey key, Func<TKey, TValue> factory)
    {
        if (_entries.TryGetValue(key, out var existing)) return existing;
        var slot = _ctx.Computed<TValue>(_ => Task.FromResult(factory(key)));
        _entries[key] = slot;
        _order.Add(key);
        BumpMembership();
        return slot;
    }

    private void BumpMembership()
    {
        _membershipVersion++;
        _membership.Set(_membershipVersion);
    }
}
