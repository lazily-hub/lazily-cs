// Disposal, teardown scopes, and edge-degree introspection (spec tag: lzspecedgeindex).
//
// Why this exists: dropping the last C# reference to a node reclaims nothing reactive. The
// node's edge on each of its dependencies is a STRONG reference held by the graph, so a
// long-lived source retains every node that ever read it. Under subscribe/unsubscribe churn the
// dependent set grows without bound even though the live subscriber count is constant, and the
// cost is paid twice — memory, and propagation, since every write walks the whole list.
//
// Three semantics this file must preserve:
//
//   1. Disposal DIRTIES the surviving dependent cone. Detaching edges without marking dependents
//      leaves a live reader frozen on the value it cached before the disposal. The cone walk
//      here reuses ReactiveNode.InvalidateCone rather than adding a second walk that could drift.
//
//   2. Effects reached by that walk are MARKED, NOT RUN. Disposal is not a publish: running an
//      effect mid-teardown re-enters a compute that reads the node being disposed, which breaks
//      idempotence. Context.Disposing gates this.
//
//   3. Scope teardown is REVERSE CREATION ORDER. Graph state is order independent, but effect
//      cleanups are side effects with an observable order, and ending a scope is proved
//      observationally equal to disposing each member individually (lazily-formal
//      `disposeScope_eq_disposeAll`).
//
// Why reads of a disposed node throw: disposal is a caller contract ("nothing may still read
// it"). A compute body has no error channel, so an exception is the only mechanism that crosses
// an arbitrary user closure — and it matches the reference bindings, which panic. TryGet is the
// checked form at the boundary.

namespace Lazily;

/// <summary>Thrown by a read of a disposed reactive node, and returned by the checked forms.</summary>
public sealed class DisposedNodeException : InvalidOperationException
{
    /// <summary>Creates the exception for a node of the given kind.</summary>
    /// <param name="name">The node's debug name, when it has one.</param>
    /// <param name="kind">"source" or "computed".</param>
    public DisposedNodeException(string? name, string kind)
        : base(string.IsNullOrEmpty(name)
            ? $"lazily: read of disposed {kind}"
            : $"lazily: read of disposed {kind} {name}")
    {
        NodeName = name;
        Kind = kind;
    }

    /// <summary>The node's debug name, when it has one.</summary>
    public string? NodeName { get; }

    /// <summary>The node kind: "source" or "computed".</summary>
    public string Kind { get; }
}

/// <summary>Disposal, degree introspection, and teardown scoping on a <see cref="Context"/>.</summary>
public static class Disposal
{
    /// <summary>
    /// How many nodes currently depend on <paramref name="node"/> — the size of its reverse edge
    /// set.
    /// </summary>
    /// <remarks>
    /// This is the observable the disposal contract is written against: a subscribe/unsubscribe
    /// cycle that disposes what it creates must leave this at its starting value no matter how
    /// many cycles run. A binding that leaks shows total-ever-created here instead of live
    /// subscriber count.
    /// <para>
    /// It counts LIVE edges. Invalidation does not consume them — the cascade is a non-consuming
    /// mark-frontier walk — so a degree read immediately after a write and before the dependents
    /// are pulled reports the same edges as before the write. An edge changes only when a node
    /// recomputes and re-tracks, or when disposal detaches it.
    /// </para>
    /// </remarks>
    /// <param name="ctx">The owning scope.</param>
    /// <param name="node">The node to measure.</param>
    public static int DependentCount(this Context ctx, ReactiveNode node)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(node, nameof(node));
        return node.Disposed ? 0 : node.Dependents.Count;
    }

    /// <summary>
    /// How many nodes <paramref name="node"/> currently depends on — the size of its forward edge
    /// set. Disposal must detach both directions; a binding that detaches only one leaves a
    /// dangling half-edge visible here.
    /// </summary>
    /// <param name="ctx">The owning scope.</param>
    /// <param name="node">The node to measure.</param>
    public static int DependencyCount(this Context ctx, ReactiveNode node)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(node, nameof(node));
        return node.Disposed ? 0 : node.Dependencies.Count;
    }

    /// <summary>Whether <paramref name="node"/> has been torn down.</summary>
    /// <param name="ctx">The owning scope.</param>
    /// <param name="node">The node to test.</param>
    public static bool IsDisposed(this Context ctx, ReactiveNode node)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(node, nameof(node));
        return node.Disposed;
    }

    /// <summary>Opens a teardown scope. Nodes added with <c>Own</c> are disposed by Close.</summary>
    /// <param name="ctx">The owning scope.</param>
    public static TeardownScope Scope(this Context ctx)
    {
        Guard.NotNull(ctx, nameof(ctx));
        return new TeardownScope(ctx);
    }

    /// <summary>
    /// Runs <paramref name="fn"/> with a fresh teardown scope and closes it on return, including
    /// on exception.
    /// </summary>
    /// <param name="ctx">The owning scope.</param>
    /// <param name="fn">The body.</param>
    public static void WithScope(this Context ctx, Action<TeardownScope> fn)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(fn, nameof(fn));
        var s = ctx.Scope();
        try
        {
            fn(s);
        }
        finally
        {
            s.Close();
        }
    }

    /// <summary>The single teardown path for every node kind.</summary>
    internal static void DisposeNode(this Context ctx, ReactiveNode? n)
    {
        if (n is null || n.Disposed) return; // idempotent

        // An eager computed owns a puller effect; tear it down first so disposing the formula
        // never strands a live puller that re-pulls a disposed node.
        if (ctx.EagerBy.Remove(n, out var puller)) puller.Dispose();

        if (n is Effect e)
        {
            // Effects own a cleanup callback and a pending-queue entry, so their teardown is
            // Effect.Dispose. It detaches upstream and marks the node disposed; effects are
            // sinks, so there is no cone to dirty.
            e.Dispose();
            return;
        }
        ctx.Teardown(n);
    }

    /// <summary>Detaches a node in both directions and dirties what survives.</summary>
    private static void Teardown(this Context ctx, ReactiveNode n)
    {
        n.Disposed = true;

        // Drop the memoized value and its registry entry, so a churn workload does not grow the
        // registry without bound.
        if (n is ICacheable cn)
        {
            if (cn.CachedNow) ctx.CachedCountDec();
            cn.ClearCache();
            ctx.UnregisterSlot(n);
        }

        // Upstream: remove this node from each dependency's dependent set.
        n.DetachUpstream();

        // Downstream: snapshot the dependents, then drop the edge in both directions before
        // cascading, so nothing observes a half-detached graph.
        if (n.Dependents.Count == 0) return;
        var survivors = n.Dependents.ToArray();
        foreach (var d in survivors) d.Dependencies.Remove(n);
        n.Dependents.Clear();

        // Semantic 1: the surviving cone must be dirtied, or a live reader stays frozen on its
        // cached value forever. Semantic 2: `Disposing` makes that cascade mark-only.
        ctx.Disposing++;
        try
        {
            foreach (var d in survivors) d.InvalidateCone();
        }
        finally
        {
            ctx.Disposing--;
        }
    }
}

/// <summary>
/// Groups nodes so they can be torn down together.
/// </summary>
/// <remarks>
/// Grouping bounds TEARDOWN, not visibility: a scoped node reads unscoped or sibling-scope nodes
/// freely, and an unscoped node may read a scoped one. Closing a scope tears down its members
/// even if something outside still reads them, and that reader throws on its next recompute.
/// </remarks>
public sealed class TeardownScope
{
    private readonly Context _ctx;
    private List<ReactiveNode> _owned = [];
    private bool _closed;

    internal TeardownScope(Context ctx) => _ctx = ctx;

    /// <summary>
    /// Places <paramref name="node"/> under this scope's ownership and returns it, so a node can
    /// be created and scoped in one expression.
    /// </summary>
    /// <typeparam name="TNode">The node type.</typeparam>
    /// <param name="node">The node to own.</param>
    public TNode Own<TNode>(TNode node) where TNode : ReactiveNode
    {
        Guard.NotNull(node, nameof(node));
        if (!_closed) _owned.Add(node);
        return node;
    }

    /// <summary>How many nodes this scope currently owns.</summary>
    public int Count => _owned.Count;

    /// <summary>
    /// Cancels this scope's teardown: releases every node it owns back to plain context
    /// ownership, so <see cref="Close"/> disposes nothing. The nodes themselves are untouched —
    /// no disposal, no detachment — and each stays individually disposable.
    /// </summary>
    public void Disarm() => _owned = [];

    /// <summary>
    /// Tears down every node this scope owns, in REVERSE creation order, then marks the scope
    /// closed. Idempotent.
    /// </summary>
    /// <remarks>
    /// Reverse order matters for effect cleanups, which are observable side effects; graph state
    /// alone is order independent. It also keeps a scope from transiently dangling inside itself,
    /// since dependents go before what they read.
    /// </remarks>
    public void Close()
    {
        var owned = _owned;
        _owned = [];
        _closed = true;
        for (var i = owned.Count - 1; i >= 0; i--) _ctx.DisposeNode(owned[i]);
    }
}
