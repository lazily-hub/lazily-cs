// Thread-safe reactive context — a lock-serialized batch boundary.
//
// The C# counterpart of lazily-go's ThreadSafeContext, lazily-py's ThreadSafeContext, and the
// Lean LazilyFormal.ThreadSafe model (lazily-spec § "Concurrency layers are required").
//
// The behavioural contract: serializing concurrent cell writes through a batch boundary
// coalesces them into ONE invalidation pass whose result is a deterministic function of the
// writes — independent of the interleaving the lock happened to pick. .NET has real threads, so
// this layer is required of this binding (unlike the single-isolate JS/Dart runtimes, which
// declare the row `—`).
//
// ThreadSafeContext wraps a single-threaded Context with a reentrant lock and reuses the core
// batch coalescing, so a one-write section is observationally identical to a plain Source.Set:
// the thread-safe context REFINES the single-threaded kernel rather than reimplementing it.
// That refinement is what the reactive-graph corpus replay pins — the same fixtures, the same
// assertions, a third execution model.

namespace Lazily;

/// <summary>
/// A reactive context that serializes all access to an underlying <see cref="Context"/> behind a
/// reentrant lock.
/// </summary>
/// <remarks>
/// Build nodes and read/write cells only inside <see cref="WithLock(Action{Context})"/> or
/// <see cref="Batch"/> (or through <see cref="Set{T}"/>); concurrent callers are linearized by
/// the lock, so every batch flush is glitch-free.
/// <para>
/// The lock is reentrant, so a body may itself call <see cref="WithLock(Action{Context})"/>,
/// <see cref="Batch"/>, or <see cref="Set{T}"/> without deadlocking.
/// </para>
/// </remarks>
public sealed class ThreadSafeContext
{
    private readonly Context _ctx = new();
    private readonly object _gate = new();

    /// <summary>Creates a lock-backed reactive context.</summary>
    public ThreadSafeContext() { }

    /// <summary>
    /// The underlying single-threaded context.
    /// </summary>
    /// <remarks>
    /// Touch it only while holding the lock (inside <see cref="WithLock(Action{Context})"/> or
    /// <see cref="Batch"/>). Direct concurrent use is exactly the unsafety this type exists to
    /// prevent; the accessor is public because the node constructors are extension methods on
    /// <see cref="Context"/>, so a caller inside the lock needs the real thing.
    /// </remarks>
    public Context Inner => _ctx;

    /// <summary>Whether the calling thread currently holds this context's lock.</summary>
    public bool IsHeldByCurrentThread => Monitor.IsEntered(_gate);

    /// <summary>
    /// Runs <paramref name="fn"/> while holding the lock, giving it exclusive, race-free access
    /// to the reactive graph (build nodes, read slots, write cells).
    /// </summary>
    /// <param name="fn">The critical section.</param>
    public void WithLock(Action<Context> fn)
    {
        Guard.NotNull(fn, nameof(fn));
        lock (_gate) fn(_ctx);
    }

    /// <summary>
    /// Runs <paramref name="fn"/> while holding the lock and returns its result — the
    /// read-oriented convenience over <see cref="WithLock(Action{Context})"/>.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="fn">The critical section.</param>
    public TResult WithLock<TResult>(Func<Context, TResult> fn)
    {
        Guard.NotNull(fn, nameof(fn));
        lock (_gate) return fn(_ctx);
    }

    /// <summary>
    /// Runs <paramref name="fn"/> under the lock inside an underlying
    /// <see cref="Context.Batch"/>, so every write queued in <paramref name="fn"/> flushes in a
    /// single coalesced invalidation pass at the outermost boundary.
    /// </summary>
    /// <param name="fn">The batch body.</param>
    public void Batch(Action fn)
    {
        Guard.NotNull(fn, nameof(fn));
        lock (_gate) _ctx.Batch(fn);
    }

    /// <summary>
    /// Writes a source's value under the lock. Outside a batch it applies immediately (a
    /// singleton batch is exactly <see cref="Source{T}.Set"/>); inside a <see cref="Batch"/> it
    /// defers to the coalesced flush.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="cell">The source to write.</param>
    /// <param name="value">The value to store.</param>
    public void Set<T>(Source<T> cell, T value)
    {
        Guard.NotNull(cell, nameof(cell));
        lock (_gate) cell.Set(value);
    }

    /// <summary>
    /// Folds <paramref name="op"/> into a source under its merge policy, under the lock.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="cell">The source to fold into.</param>
    /// <param name="op">The operand.</param>
    public void Merge<T>(Source<T> cell, T op)
    {
        Guard.NotNull(cell, nameof(cell));
        lock (_gate) cell.Merge(op);
    }
}

/// <summary>
/// A node's <c>(value, state)</c> pair in the pure batch-flush kernel.
/// </summary>
/// <param name="Value">The node's value.</param>
/// <param name="State">Either <see cref="NodeEntry.Clean"/> or <see cref="NodeEntry.Dirty"/>.</param>
public readonly record struct NodeEntry(object? Value, string State)
{
    /// <summary>The node's cached value is up to date.</summary>
    public const string Clean = "clean";

    /// <summary>The node has been written, or lies in a written node's dependent cone.</summary>
    public const string Dirty = "dirty";
}

/// <summary>A <c>(nodeId, value)</c> write in the pure batch-flush kernel.</summary>
/// <param name="NodeId">The node to write.</param>
/// <param name="Value">The value to write.</param>
public readonly record struct BatchWrite(object NodeId, object? Value);

/// <summary>
/// The pure batch-flush kernel — a faithful port of the Lean <c>LazilyFormal.ThreadSafe</c>
/// model, independent of the live reactive graph.
/// </summary>
/// <remarks>
/// It operates on a plain node table so the coalescing law can be property-tested directly: the
/// post-flush table is a function of the write LIST, so any interleaving the lock serializes to
/// the same list produces the same table. The live <see cref="ThreadSafeContext"/> gets the same
/// property from <see cref="Context.Batch"/>; this kernel is what makes the property checkable
/// without a graph.
/// </remarks>
public static class ThreadSafeKernel
{
    /// <summary>
    /// Applies a batch's value updates (through the equality guard) to a COPY of
    /// <paramref name="nodes"/>.
    /// </summary>
    /// <param name="nodes">The node table.</param>
    /// <param name="batch">The writes, in order.</param>
    /// <returns>The new table and the ids that actually changed, each listed once, in first-change order.</returns>
    public static (IReadOnlyDictionary<object, NodeEntry> Nodes, IReadOnlyList<object> Changed) ApplyBatch(
        IReadOnlyDictionary<object, NodeEntry> nodes, IReadOnlyList<BatchWrite> batch)
    {
        Guard.NotNull(nodes, nameof(nodes));
        Guard.NotNull(batch, nameof(batch));
        var next = new Dictionary<object, NodeEntry>(nodes);
        var changed = new List<object>();
        var seen = new HashSet<object>();
        foreach (var w in batch)
        {
            if (!next.TryGetValue(w.NodeId, out var cur)) continue;
            // The write guard: an equal write is not a change, so it neither dirties the node
            // nor contributes a frontier root. This is the same guard Source<T>.Set applies.
            if (Equals(cur.Value, w.Value)) continue;
            next[w.NodeId] = new NodeEntry(w.Value, NodeEntry.Dirty);
            if (seen.Add(w.NodeId)) changed.Add(w.NodeId);
        }
        return (next, changed);
    }

    /// <summary>
    /// Applies a batch's values, then marks the COALESCED union of the changed sources'
    /// dependents dirty in one pass.
    /// </summary>
    /// <remarks>
    /// The coalescing is the point: a dependent reachable from three written sources appears in
    /// the frontier exactly once, so the flush does one pass rather than three. That is the
    /// property a serialized concurrent writer relies on.
    /// </remarks>
    /// <param name="nodes">The node table.</param>
    /// <param name="dependents">The forward edge map, source id to dependent ids.</param>
    /// <param name="batch">The writes, in order.</param>
    /// <returns>The post-flush table.</returns>
    public static IReadOnlyDictionary<object, NodeEntry> FlushBatch(
        IReadOnlyDictionary<object, NodeEntry> nodes,
        IReadOnlyDictionary<object, IReadOnlyList<object>> dependents,
        IReadOnlyList<BatchWrite> batch)
    {
        Guard.NotNull(dependents, nameof(dependents));
        var (applied, changed) = ApplyBatch(nodes, batch);
        var next = new Dictionary<object, NodeEntry>(applied);
        var inFrontier = new HashSet<object>();
        var frontier = new List<object>();
        foreach (var src in changed)
        {
            if (!dependents.TryGetValue(src, out var ds)) continue;
            foreach (var d in ds)
            {
                if (inFrontier.Add(d)) frontier.Add(d);
            }
        }
        foreach (var d in frontier)
        {
            if (next.TryGetValue(d, out var entry)) next[d] = entry with { State = NodeEntry.Dirty };
        }
        return next;
    }
}
