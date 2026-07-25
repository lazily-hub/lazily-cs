// The dependency-tracking core shared by every node in the reactive graph
// (spec tags: lzcellkernel, lzspecedgeindex).
//
// C# note on the port: Go embeds `reactiveBase` into each node struct and routes
// polymorphic calls through a `self reactiveNode` field. C# has real inheritance,
// so `ReactiveNode` is an abstract base class and `self` disappears — `this` IS
// the concrete node. Edge sets are reference-keyed `HashSet<ReactiveNode>`;
// no node overrides Equals/GetHashCode, so the default comparer is identity.

namespace Lazily;

/// <summary>
/// Any node in a <see cref="Context"/>'s reactive graph: a <see cref="Source{T}"/>,
/// a <see cref="Computed{T}"/>, or an <see cref="Effect"/>.
/// </summary>
/// <remarks>
/// Sealed by construction: the only implementations live in this assembly, and the
/// edge sets are never exposed. The degree accessors on <see cref="Context"/> return
/// counts, never the sets themselves.
/// </remarks>
public abstract class ReactiveNode
{
    internal ReactiveNode(Context ctx) => Ctx = ctx;

    internal Context Ctx { get; }

    /// <summary>Nodes that read this one (downstream / reverse edges).</summary>
    internal readonly HashSet<ReactiveNode> Dependents = [];

    /// <summary>Nodes this one read during its last computation (upstream / forward edges).</summary>
    internal readonly HashSet<ReactiveNode> Dependencies = [];

    /// <summary>
    /// Set by <c>Dispose</c> or by a <see cref="TeardownScope"/>. A disposed node has no
    /// edges in either direction, reads as an error, and reports zero degree.
    /// </summary>
    internal bool Disposed;

    // The two staleness levels of the mark-frontier walk, mirroring lazily-rs's
    // SlotNode.{dirty, force_recompute}:
    //
    //   Dirty          — "check me": something in my ancestry changed, but it may
    //                    recompute to an equal value, so my cached value is not yet
    //                    known to be wrong.
    //   ForceRecompute — "I am definitely wrong": I read the node that was actually
    //                    written, so I must recompute.
    //
    // Direct dependents of a written node become force roots; everything deeper is
    // merely dirty. A dirty-but-not-forced node is resolved at pull time by
    // refreshing its dependencies: if none report a changed value it is marked clean
    // WITHOUT recomputing — the equality guard's downstream suppression.
    internal bool Dirty;
    internal bool ForceRecompute;

    /// <summary>
    /// This node's position in <see cref="Context"/>'s slot registry, or -1 when the node
    /// is not value-bearing. Disposal swap-removes by this index so subscribe/unsubscribe
    /// churn does not grow the registry without bound (lzspecedgeindex).
    /// </summary>
    internal int SlotIndex = -1;

    /// <summary>
    /// Hook run when this node is marked stale by the frontier walk, before the walk
    /// descends to its dependents.
    /// </summary>
    internal virtual void OnInvalidate() { }

    /// <summary>
    /// Brings a stale node up to date at pull time and reports whether its value actually
    /// CHANGED. Non-value-bearing nodes (<see cref="Source{T}"/>, <see cref="Effect"/>) are
    /// never stale and always report false.
    /// </summary>
    internal virtual bool Refresh() => false;

    /// <summary>
    /// Registers <paramref name="parent"/> (the recomputing node, if any) as a dependent of
    /// this node, and records the reverse edge.
    /// </summary>
    /// <remarks>
    /// This is the value-threaded tracking core (lzcellkernel): the identity to attribute
    /// to is passed in as a value, never read from ambient state. A null parent (a top-level
    /// or explicitly untracked read) forms no edge. The <see cref="HashSet{T}"/> membership
    /// check is what makes repeated reads O(1)-deduplicated rather than appending a
    /// duplicate edge per read (lzspecedgeindex).
    /// </remarks>
    internal void TrackTo(ReactiveNode? parent)
    {
        if (parent is null || ReferenceEquals(parent, this)) return;
        if (Dependents.Add(parent)) parent.Dependencies.Add(this);
    }

    /// <summary>
    /// Detaches this node from all of its current upstream dependencies. Called before a
    /// recompute so edges reflect only the most recent computation.
    /// </summary>
    internal void DetachUpstream()
    {
        foreach (var dep in Dependencies) dep.Dependents.Remove(this);
        Dependencies.Clear();
    }

    /// <summary>
    /// Marks this node's transitive dependent cone stale.
    /// </summary>
    /// <remarks>
    /// A NON-CONSUMING mark-frontier walk (the lazily-rs model). It reads
    /// <see cref="Dependents"/> and leaves every edge in place; nothing is recomputed and no
    /// value is produced. That is what makes pull-time suppression possible: a dependent can
    /// be marked clean again WITHOUT recomputing and still be reachable from its source, so
    /// the next genuine change is not lost at depth two.
    /// </remarks>
    internal virtual void InvalidateCone()
    {
        if (this is ICacheable)
        {
            MarkCone([new MarkEntry(this, Force: true)], scheduleEffects: true);
            return;
        }
        OnInvalidate();
        MarkDependents(scheduleEffects: true);
    }

    /// <summary>
    /// Force-marks this node's direct dependents and dirties everything below them, leaving
    /// this node itself alone. Used when a node's own value has just been established as
    /// changed: by a source write, or by a pull-time recompute that did not compare equal.
    /// </summary>
    /// <param name="scheduleEffects">
    /// False on the pull-time path. An effect reached there was already reached by the
    /// write's own cascade — a node only becomes stale through that cascade — so
    /// re-scheduling it would run an effect a second time merely because one of its
    /// dependencies happened to recompute while the effect was mid-flush.
    /// </param>
    internal void MarkDependents(bool scheduleEffects)
    {
        if (Dependents.Count == 0) return;
        var stack = new List<MarkEntry>(Dependents.Count);
        foreach (var d in Dependents) stack.Add(new MarkEntry(d, Force: true));
        MarkCone(stack, scheduleEffects);
    }

    /// <summary>One node on the frontier walk's explicit stack, with the level to apply.</summary>
    internal readonly record struct MarkEntry(ReactiveNode Node, bool Force);

    /// <summary>
    /// The iterative frontier walk shared by every invalidation path. Roots arrive at the
    /// level their caller chose; every node reached from a root is merely dirty, never forced.
    /// </summary>
    /// <remarks>
    /// The walk terminates on a staleness short-circuit rather than on edge consumption: a
    /// node already at or above the level being applied does not need its cone re-walked,
    /// because that cone was marked when it was first reached and nothing has cleaned it
    /// since (a dependent is cleaned only through Refresh, which refreshes — and so cleans —
    /// all of its dependencies).
    /// </remarks>
    internal static void MarkCone(List<MarkEntry> stack, bool scheduleEffects)
    {
        while (stack.Count > 0)
        {
            var entry = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            var n = entry.Node;
            if (n.Disposed) continue;

            if (n is Effect eff)
            {
                // Effects are sinks: scheduled (or, during teardown, deliberately not),
                // never marked, and never descended through.
                if (scheduleEffects) eff.MarkStale(entry.Force);
                continue;
            }

            var propagate = true;
            if (n is ICacheable)
            {
                // Re-walk only when this node learns something new: it was clean, or it is
                // being raised from dirty to forced.
                propagate = !n.Dirty || (entry.Force && !n.ForceRecompute);
                if (entry.Force) n.ForceRecompute = true;
            }
            n.OnInvalidate();
            if (!propagate) continue;

            foreach (var d in n.Dependents) stack.Add(new MarkEntry(d, Force: false));
        }
    }
}

/// <summary>
/// A slot-like node that stores its own memoized value and can be asked to drop it.
/// </summary>
/// <remarks>
/// Cached values live ON the node, not in a shared context-owned map — so a read touches only
/// the node you already hold, and read latency is independent of total graph size. The
/// <see cref="Context"/> keeps a registry of these purely so Clear/Size stay correct and O(1)
/// on the hot path; the registry is never touched by a read.
/// </remarks>
internal interface ICacheable
{
    void ClearCache();
    bool CachedNow { get; }
}
