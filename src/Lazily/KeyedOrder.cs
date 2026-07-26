namespace Lazily;

/// <summary>
/// What a present-set mutation did, so the caller knows which signals to bump.
/// A no-op must bump nothing: bumping on a warm insert would invalidate every
/// <c>Len</c> / <c>ContainsKey</c> reader on a pure cache hit.
/// </summary>
public enum MapMutation
{
    /// <summary>Nothing changed — a warm insert, or a remove of an absent key.</summary>
    None,

    /// <summary>A new key joined the present set.</summary>
    Inserted,

    /// <summary>A key left the present set.</summary>
    Removed,
}

/// <summary>
/// What an ordering move did. <see cref="Missing"/> and <see cref="Unchanged"/>
/// are distinct because the public <c>Move*</c> methods report <c>false</c> for a
/// missing key but <c>true</c> for a no-op move — while neither may bump the
/// order signal.
/// </summary>
public enum MapMove
{
    /// <summary>The key (or anchor) is absent. The move did not apply.</summary>
    Missing,

    /// <summary>Already at the requested position. Applied, nothing to bump.</summary>
    Unchanged,

    /// <summary>The order changed. Bump the order signal.</summary>
    Reordered,
}

/// <summary>Extension helpers for the mutation/move outcomes.</summary>
public static class KeyedOrderOutcomes
{
    /// <summary>Whether anything changed.</summary>
    public static bool Changed(this MapMutation mutation) => mutation != MapMutation.None;

    /// <summary>Whether the move applied at all (the bool the public API returns).</summary>
    public static bool Applied(this MapMove move) => move != MapMove.Missing;

    /// <summary>Whether the order actually changed, i.e. whether to bump.</summary>
    public static bool Changed(this MapMove move) => move == MapMove.Reordered;
}

/// <summary>
/// The present set plus its authoritative key order, with the atomic-move
/// algebra (<c>#lzcellmove</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is the GRAPH-AGNOSTIC half of every ReactiveMap flavor. It holds no
/// context, no factory, and no closure: only key-to-handle bookkeeping and the
/// key list. That is exactly why ordering and atomic move bind the
/// single-threaded, thread-safe, and async flavors alike — a move touches no
/// entry handle and awaits nothing, so it is neither thread- nor async-coloured.
/// </para>
/// <para>
/// What is deliberately NOT here is reactivity. Membership and order
/// invalidation is a graph write, and each flavor must mint its own version
/// cells on its own graph; a shared core cannot supply them.
/// </para>
/// <para>
/// Entries and order stay in lockstep: every key in one appears exactly once in
/// the other, including on every failure path. Reordering cannot fail — it is a
/// RemoveAt + Insert with both ends clamped — so there is no error path to
/// desync on.
/// </para>
/// <para>Rust reference: <c>lazily-rs/src/keyed_order.rs</c>.</para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="THandle">The entry handle type.</typeparam>
public sealed class KeyedOrder<TKey, THandle>
    where TKey : notnull
{
    private readonly Dictionary<TKey, THandle> _entries = [];
    private readonly List<TKey> _order = [];

    /// <summary>The handle for <paramref name="key"/>, if present.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="handle">The entry handle when present.</param>
    /// <returns>Whether the key is present.</returns>
    public bool TryGet(TKey key, out THandle handle) => _entries.TryGetValue(key, out handle!);

    /// <summary>Whether <paramref name="key"/> is in the present set.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>Whether the key is present.</returns>
    public bool Contains(TKey key) => _entries.ContainsKey(key);

    /// <summary>A copy of the authoritative key list; the internal list never escapes.</summary>
    /// <returns>The keys in current order.</returns>
    public IReadOnlyList<TKey> Keys() => [.. _order];

    /// <summary>The present-set size.</summary>
    public int Length => _order.Count;

    /// <summary>The current 0-based position of <paramref name="key"/>.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>The position, or -1 when absent.</returns>
    public int Position(TKey key) => _order.IndexOf(key);

    /// <summary>
    /// Inserts <paramref name="handle"/> under <paramref name="key"/>, appending
    /// to the order. A warm key keeps its existing handle (cell-identity: a key's
    /// node is stable for its lifetime) and reports <see cref="MapMutation.None"/>
    /// so the caller bumps nothing.
    /// </summary>
    /// <param name="key">The entry key.</param>
    /// <param name="handle">The freshly minted handle.</param>
    /// <param name="stored">The handle now bound to the key.</param>
    /// <returns>What changed.</returns>
    public MapMutation Insert(TKey key, THandle handle, out THandle stored)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            stored = existing;
            return MapMutation.None;
        }

        _entries[key] = handle;
        _order.Add(key);
        stored = handle;
        return MapMutation.Inserted;
    }

    /// <summary>
    /// Removes <paramref name="key"/>, returning its handle so the caller can
    /// dispose the node on its own graph. The core never touches a handle.
    /// </summary>
    /// <param name="key">The entry key.</param>
    /// <param name="removed">The removed handle, when present.</param>
    /// <returns>What changed.</returns>
    public MapMutation Remove(TKey key, out THandle removed)
    {
        if (!_entries.TryGetValue(key, out removed!))
        {
            return MapMutation.None;
        }

        _entries.Remove(key);
        _order.Remove(key);
        return MapMutation.Removed;
    }

    /// <summary>
    /// Moves <paramref name="key"/> to <paramref name="index"/>, clamped to
    /// [0, len). The entry keeps the same handle, its dependents, and its CRDT
    /// lineage — that is what separates a reorder from a remove + re-mint. Both
    /// ends are clamped; an unclamped negative index is the defect lazily-js
    /// shipped.
    /// </summary>
    /// <param name="key">The entry key.</param>
    /// <param name="index">The requested position.</param>
    /// <returns>What the move did.</returns>
    public MapMove MoveTo(TKey key, int index)
    {
        var from = _order.IndexOf(key);
        if (from < 0)
        {
            return MapMove.Missing;
        }

        var to = Math.Clamp(index, 0, _order.Count - 1);
        if (from == to)
        {
            return MapMove.Unchanged;
        }

        _order.RemoveAt(from);
        _order.Insert(to, key);
        return MapMove.Reordered;
    }

    /// <summary>
    /// Moves <paramref name="key"/> to just before <paramref name="anchor"/>.
    /// </summary>
    /// <remarks>
    /// The target is computed on the PRE-REMOVAL list: when the key currently
    /// precedes the anchor, lifting it out shifts the anchor one slot left, so
    /// the insertion point is anchor-1. Getting this wrong lands the key on the
    /// far side of its anchor — the defect found in lazily-zig, where
    /// MoveBefore("a","d") on [a,b,c,d] produced [b,c,d,a].
    /// </remarks>
    /// <param name="key">The entry key.</param>
    /// <param name="anchor">The anchor key.</param>
    /// <returns>What the move did.</returns>
    public MapMove MoveBefore(TKey key, TKey anchor)
    {
        var anchorIdx = _order.IndexOf(anchor);
        var from = _order.IndexOf(key);
        if (anchorIdx < 0 || from < 0)
        {
            return MapMove.Missing;
        }

        return MoveTo(key, from < anchorIdx ? anchorIdx - 1 : anchorIdx);
    }

    /// <summary>
    /// Moves <paramref name="key"/> to just after <paramref name="anchor"/>. Same
    /// pre-removal reasoning.
    /// </summary>
    /// <param name="key">The entry key.</param>
    /// <param name="anchor">The anchor key.</param>
    /// <returns>What the move did.</returns>
    public MapMove MoveAfter(TKey key, TKey anchor)
    {
        var anchorIdx = _order.IndexOf(anchor);
        var from = _order.IndexOf(key);
        if (anchorIdx < 0 || from < 0)
        {
            return MapMove.Missing;
        }

        return MoveTo(key, from <= anchorIdx ? anchorIdx : anchorIdx + 1);
    }
}
