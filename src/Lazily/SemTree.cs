using System;
using System.Collections.Generic;
using System.Linq;

namespace Lazily;

/// <summary>Combines a node's value with its children's derived values.</summary>
/// <typeparam name="TValue">The per-node value type.</typeparam>
/// <typeparam name="TDerived">The folded type.</typeparam>
/// <param name="value">This node's value.</param>
/// <param name="childDerived">The children's folded values, in order.</param>
/// <returns>This node's folded value.</returns>
public delegate TDerived FoldFn<in TValue, TDerived>(TValue value, IReadOnlyList<TDerived> childDerived);

/// <summary>A node spec for <see cref="SemTree{TValue,TDerived}.Build"/>.</summary>
/// <typeparam name="TValue">The per-node value type.</typeparam>
public sealed class TreeNodeSpec<TValue>
{
    /// <summary>The node id, unique within the tree.</summary>
    public required string Id { get; init; }

    /// <summary>The node's value.</summary>
    public required TValue Value { get; init; }

    /// <summary>The ordered child ids. Empty for a leaf.</summary>
    public IReadOnlyList<string> Order { get; init; } = [];

    /// <summary>The child specs, keyed by id.</summary>
    public IReadOnlyDictionary<string, TreeNodeSpec<TValue>> Children { get; init; } =
        new Dictionary<string, TreeNodeSpec<TValue>>(StringComparer.Ordinal);
}

/// <summary>
/// A memoized semantic tree: one guarded memo slot per node, folding that node's value together
/// with its children's folded values.
/// </summary>
/// <remarks>
/// <para>
/// The point is incremental cost. Editing one node recomputes only its ANCESTOR CHAIN — a sibling
/// subtree's memo stays cached, because nothing it depends on changed. And because each memo is
/// equality-guarded, an edit whose fold lands on the same value stops there: a downstream consumer
/// of the root does not re-run. Cost is proportional to the diff, not to the document.
/// </para>
/// <para>
/// The child-slot map is fixed at build time — inserting a brand-new child needs a fresh build, as
/// in the sibling bindings. Removal mutates the parent's ordered-child-keys cell, which is what
/// makes a dropped subtree fall out of the fold.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The per-node value type.</typeparam>
/// <typeparam name="TDerived">The folded type.</typeparam>
public sealed class SemTree<TValue, TDerived>
{
    private readonly Context _ctx;
    private readonly FoldFn<TValue, TDerived> _fold;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

    private SemTree(Context ctx, FoldFn<TValue, TDerived> fold)
    {
        _ctx = ctx;
        _fold = fold;
    }

    /// <summary>The root node's id.</summary>
    public string RootId { get; private set; } = "";

    /// <summary>Builds a tree from <paramref name="rootSpec"/>, folding with <paramref name="fold"/>.</summary>
    /// <param name="ctx">The owning context.</param>
    /// <param name="rootSpec">The root node spec.</param>
    /// <param name="fold">The fold.</param>
    /// <returns>The built tree.</returns>
    public static SemTree<TValue, TDerived> Build(
        Context ctx,
        TreeNodeSpec<TValue> rootSpec,
        FoldFn<TValue, TDerived> fold)
    {
        ArgumentNullException.ThrowIfNull(rootSpec);
        var tree = new SemTree<TValue, TDerived>(ctx, fold) { RootId = rootSpec.Id };
        tree.BuildNode(rootSpec);
        return tree;
    }

    private Node BuildNode(TreeNodeSpec<TValue> spec)
    {
        var node = new Node(_ctx.Source(spec.Value));
        _nodes[spec.Id] = node;

        var childOrder = new List<string>();
        var order = spec.Order.Count > 0
            ? spec.Order
            : [.. spec.Children.Keys.Order(StringComparer.Ordinal)];
        foreach (var childKey in order)
        {
            if (!spec.Children.TryGetValue(childKey, out var childSpec)) continue;
            var child = BuildNode(childSpec);
            childOrder.Add(childSpec.Id);
            node.ChildSlots[childSpec.Id] = child.Slot!;
        }

        // Boxed behind a fresh list per mutation so the cell's guard fires on a real change and
        // nothing else — the same trick the sibling bindings use for a non-comparable sequence.
        node.ChildKeys = _ctx.Source<IReadOnlyList<string>>(childOrder);

        // Registered AFTER ChildKeys exists, so the memo observes it and a removal invalidates.
        node.Slot = _ctx.Computed(
            c =>
            {
                var value = node.Value.Get(c);
                var keys = node.ChildKeys!.Get(c);
                var derived = new List<TDerived>(keys.Count);
                foreach (var kid in keys)
                {
                    if (node.ChildSlots.TryGetValue(kid, out var slot)) derived.Add(slot.Get(c));
                }

                return _fold(value, derived);
            },
            EqualityComparer<TDerived>.Default);

        return node;
    }

    /// <summary>The folded value at <paramref name="id"/>.</summary>
    /// <param name="id">The node id.</param>
    /// <param name="ops">The enclosing computation, when read from inside one.</param>
    /// <returns>The node's folded value.</returns>
    public TDerived Derived(string id, IComputeOps? ops = null)
    {
        var slot = NodeOf(id).Slot!;
        return ops is null ? slot.Get() : slot.Get(ops);
    }

    /// <summary>The memo slot at <paramref name="id"/>, for wiring a downstream consumer.</summary>
    /// <param name="id">The node id.</param>
    /// <returns>The node's memo slot.</returns>
    public Computed<TDerived> Slot(string id) => NodeOf(id).Slot!;

    /// <summary>Writes the value of node <paramref name="id"/>.</summary>
    /// <param name="id">The node id.</param>
    /// <param name="value">The new value.</param>
    public void SetValue(string id, TValue value) => NodeOf(id).Value.Set(value);

    /// <summary>Drops <paramref name="childId"/> from <paramref name="parentId"/>'s ordered children.</summary>
    /// <remarks>The child's slot stays built; it simply stops contributing to the parent's fold.</remarks>
    /// <param name="parentId">The parent node id.</param>
    /// <param name="childId">The child node id.</param>
    /// <returns>Whether the child was present.</returns>
    public bool RemoveChild(string parentId, string childId)
    {
        var parent = NodeOf(parentId);
        var keys = parent.ChildKeys!.Peek();
        if (!keys.Contains(childId, StringComparer.Ordinal)) return false;
        parent.ChildKeys.Set([.. keys.Where(k => !string.Equals(k, childId, StringComparison.Ordinal))]);
        return true;
    }

    private Node NodeOf(string id) =>
        _nodes.TryGetValue(id, out var n) ? n : throw new KeyNotFoundException($"SemTree: unknown node {id}");

    private sealed class Node(Source<TValue> value)
    {
        internal Source<TValue> Value { get; } = value;

        internal Source<IReadOnlyList<string>>? ChildKeys { get; set; }

        internal Dictionary<string, Computed<TDerived>> ChildSlots { get; } = new(StringComparer.Ordinal);

        internal Computed<TDerived>? Slot { get; set; }
    }
}
