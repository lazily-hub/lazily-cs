using System;
using System.Collections.Generic;

namespace Lazily;

/// <summary>
/// A recursive tree node: one value cell plus a keyed child collection.
/// </summary>
/// <remarks>
/// <para>
/// The tree is a COMPOSITION of cells, not a new cell kind — so per-cell merge applies node by
/// node, and every reactivity guarantee of <see cref="SourceMap{TKey,TValue}"/> is inherited rather
/// than reimplemented: reading a node's value subscribes only to that node, adding or removing a
/// child bumps the parent's membership and order planes, and moving a child bumps order alone while
/// the child keeps its identity and its whole subtree.
/// </para>
/// <para>
/// That last property is the reason a tree gets its own type at all. Reordering siblings in a tree
/// built out of plain nested maps would otherwise re-mint the moved subtree, invalidating every
/// reader beneath it for what the caller intended as a pure reorder.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The node-id type.</typeparam>
/// <typeparam name="TValue">The per-node value type.</typeparam>
public sealed class CellTree<TKey, TValue>
    where TKey : notnull
{
    /// <summary>Creates a node with <paramref name="id"/> and <paramref name="initialValue"/> and no children.</summary>
    /// <param name="ctx">The owning context.</param>
    /// <param name="id">This node's id.</param>
    /// <param name="initialValue">This node's initial value.</param>
    public CellTree(Context ctx, TKey id, TValue initialValue)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Id = id;
        Value = ctx.Source(initialValue);
        Children = new SourceMap<TKey, CellTree<TKey, TValue>>(ctx);
        Ctx = ctx;
    }

    private Context Ctx { get; }

    /// <summary>This node's id.</summary>
    public TKey Id { get; }

    /// <summary>This node's value cell.</summary>
    public Source<TValue> Value { get; }

    /// <summary>This node's children, keyed by node id.</summary>
    public SourceMap<TKey, CellTree<TKey, TValue>> Children { get; }

    /// <summary>Reads this node's value, subscribing the caller to THIS node only.</summary>
    /// <param name="ops">The enclosing computation, when read from inside one.</param>
    /// <returns>The node's value.</returns>
    public TValue Get(IComputeOps? ops = null) => ops is null ? Value.Get() : Value.Get(ops);

    /// <summary>Writes this node's value.</summary>
    /// <param name="next">The new value.</param>
    public void Set(TValue next) => Value.Set(next);

    /// <summary>Inserts a child node, returning it. Re-inserting an existing id returns the existing child.</summary>
    /// <param name="id">The child's id.</param>
    /// <param name="value">The child's initial value.</param>
    /// <returns>The child node.</returns>
    public CellTree<TKey, TValue> InsertChild(TKey id, TValue value)
    {
        if (Children.TryObserve(id, out var existing)) return existing;
        var child = new CellTree<TKey, TValue>(Ctx, id, value);
        Children.Entry(id, child);
        return child;
    }

    /// <summary>The child with <paramref name="id"/>, or null.</summary>
    /// <param name="id">The child's id.</param>
    /// <returns>The child node, or null when absent.</returns>
    public CellTree<TKey, TValue>? Child(TKey id) => Children.TryObserve(id, out var c) ? c : null;

    /// <summary>Removes a child and its subtree.</summary>
    /// <param name="id">The child's id.</param>
    /// <returns>Whether a child was removed.</returns>
    public bool RemoveChild(TKey id) => Children.Remove(id);

    /// <summary>Moves a child to <paramref name="index"/>, bumping the order plane only.</summary>
    /// <param name="id">The child's id.</param>
    /// <param name="index">The target position.</param>
    /// <returns>Whether the order changed.</returns>
    public bool MoveChildTo(TKey id, int index) => Children.MoveTo(id, index);

    /// <summary>Moves a child immediately before <paramref name="anchor"/>.</summary>
    /// <param name="id">The child's id.</param>
    /// <param name="anchor">The sibling to move ahead of.</param>
    /// <returns>Whether the order changed.</returns>
    public bool MoveChildBefore(TKey id, TKey anchor) => Children.MoveBefore(id, anchor);

    /// <summary>Moves a child immediately after <paramref name="anchor"/>.</summary>
    /// <param name="id">The child's id.</param>
    /// <param name="anchor">The sibling to move behind.</param>
    /// <returns>Whether the order changed.</returns>
    public bool MoveChildAfter(TKey id, TKey anchor) => Children.MoveAfter(id, anchor);

    /// <summary>The child ids in order. Subscribes the caller to the ORDER plane only.</summary>
    /// <param name="ops">The enclosing computation, when read from inside one.</param>
    /// <returns>The child ids in order.</returns>
    public IReadOnlyList<TKey> ChildIds(IComputeOps? ops = null) => Children.Keys(ops);

    /// <summary>The child count. Subscribes the caller to the MEMBERSHIP plane only.</summary>
    /// <param name="ops">The enclosing computation, when read from inside one.</param>
    /// <returns>The child count.</returns>
    public int ChildCount(IComputeOps? ops = null) => Children.Len(ops);

    /// <summary>Whether a child with <paramref name="id"/> exists. Subscribes to MEMBERSHIP only.</summary>
    /// <param name="id">The child's id.</param>
    /// <param name="ops">The enclosing computation, when read from inside one.</param>
    /// <returns>Whether the child is present.</returns>
    public bool HasChild(TKey id, IComputeOps? ops = null) => Children.ContainsKey(id, ops);
}
