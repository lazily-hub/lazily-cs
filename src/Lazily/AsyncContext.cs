// The async reactive context — a distinct graph for computations whose values are produced by
// task-returning functions (lazily-spec docs/async.md).
//
// It is NOT an overload of Context or ThreadSafeContext. A synchronous slot's value is either
// present or unset; an async slot can be IN FLIGHT when its inputs change, can complete after
// those inputs are gone, and can be cancelled mid-flight. Those states need an explicit per-slot
// state machine, revision tracking so a stale completion is discarded, dependency edges
// registered BEFORE the awaited read, and a cancellation contract that is safe under waiter
// drop, supersession, and context disposal. So: its own handles, its own graph.
//
// C# note on the port. lazily-go owns the graph state in a single goroutine and funnels every
// mutation through a command channel. .NET's Monitor is reentrant, so the same serialization is
// expressed directly as one lock: every state mutation below happens under `_gate`, compute and
// effect bodies run OUTSIDE it on the thread pool, and their completions re-enter it. The
// invariant is the Go one — graph state is touched by exactly one thread at a time — reached
// with a lock instead of a loop.
//
// The invalidation walk is NON-CONSUMING, matching this binding's synchronous kernel rather than
// lazily-go's edge-consuming async walk: a dependent stays reachable from its dependency across
// an invalidation, so degree assertions mean the same thing on all three of this binding's
// execution models. Termination comes from a visited set instead of edge consumption.

using System.Diagnostics;

namespace Lazily;

/// <summary>
/// The finite-state-machine state of an async slot (lazily-spec docs/async.md § Async slot state
/// machine).
/// </summary>
public enum AsyncSlotState
{
    /// <summary>No cached value and no in-flight computation. Entered on creation and after a hard clear.</summary>
    Empty,

    /// <summary>
    /// A computation is in flight for the current revision. Concurrent readers attach as waiters
    /// rather than spawning duplicate computations.
    /// </summary>
    Computing,

    /// <summary>The cached value is fresh, until dependency invalidation transitions back to computing.</summary>
    Resolved,

    /// <summary>The last computation failed; callers receive the error, or retry on the next read.</summary>
    Error,
}

/// <summary>Thrown by async reads once the owning <see cref="AsyncContext"/> has been disposed.</summary>
public sealed class AsyncContextDisposedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public AsyncContextDisposedException()
        : base("lazily: async context disposed") { }
}

// ---------------------------------------------------------------------------
// Graph nodes (internal state; the public surface is the handle types below)
// ---------------------------------------------------------------------------

/// <summary>A node in an <see cref="AsyncContext"/>'s graph.</summary>
/// <remarks>
/// The base carries the two edge directions and the disposal flag. Every field here is touched
/// only under the owning context's lock.
/// </remarks>
public abstract class AsyncNode
{
    internal AsyncNode() { }

    internal readonly HashSet<AsyncNode> Dependents = [];
    internal readonly HashSet<AsyncNode> Dependencies = [];
    internal bool Disposed;
}

internal sealed class AsyncCellNode : AsyncNode
{
    internal object? Value;
}

internal sealed class AsyncSlotNode : AsyncNode
{
    internal Func<AsyncCompute, Task<object?>> Compute = null!;
    internal Func<object?, object?, bool>? Guard;
    internal AsyncSlotState State = AsyncSlotState.Empty;
    internal int Revision;
    internal object? Value;
    internal bool HasValue;
    internal Exception? Error;
    internal InFlight? InFlight;
    internal readonly List<TaskCompletionSource<AsyncResult>> Waiters = [];
}

internal sealed class AsyncEffectNode : AsyncNode
{
    internal Func<AsyncCompute, Task<Func<Task>?>> Body = null!;
    internal Func<Task>? Cleanup;
    internal bool Running;
    internal bool RerunScheduled;
    internal CancellationTokenSource? Cts;
}

/// <summary>The identity token for one compute run.</summary>
/// <remarks>
/// A completion publishes only while the slot still holds the token its run started with. A
/// superseded or cancelled run has had its token replaced, so its completion is discarded — this
/// is the mechanism behind "stale completion is discarded, not published".
/// </remarks>
internal sealed class InFlight
{
    internal readonly CancellationTokenSource Cts = new();
}

/// <summary>What a waiter is handed when an in-flight computation settles or is superseded.</summary>
internal readonly record struct AsyncResult(object? Value, Exception? Error, bool Superseded);

// ---------------------------------------------------------------------------
// The compute view
// ---------------------------------------------------------------------------

/// <summary>
/// The per-run view handed to an async compute or effect body.
/// </summary>
/// <remarks>
/// Async bodies cannot use an ambient tracking stack — a thread-local does not survive an
/// executor thread migration or a resume across <c>await</c> — so the recomputing node travels
/// as a VALUE on this view, exactly as <see cref="Compute"/> does on the synchronous plane.
/// Reads through it register the dependency edge BEFORE the awaited value is produced, so an
/// invalidation arriving while the body is suspended can supersede the run before it publishes.
/// </remarks>
public sealed class AsyncCompute
{
    private readonly AsyncContext _ctx;
    private readonly AsyncNode _owner;

    internal AsyncCompute(AsyncContext ctx, AsyncNode owner, CancellationToken token)
    {
        _ctx = ctx;
        _owner = owner;
        Token = token;
    }

    /// <summary>
    /// The cancellation token for this run. It is cancelled when the run is superseded, when the
    /// owning node is disposed, or when the context is disposed; long-running bodies should
    /// observe it.
    /// </summary>
    public CancellationToken Token { get; }

    /// <summary>The owning async context.</summary>
    public AsyncContext Scope => _ctx;

    /// <summary>
    /// Reads a source inside an async body, registering the dependency edge before returning.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="source">The source to read.</param>
    /// <exception cref="DisposedNodeException">The source has been disposed.</exception>
    public T Track<T>(AsyncSource<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _ctx.TrackSource<T>(_owner, source.Node, Token);
    }

    /// <summary>
    /// Awaits a computed slot inside an async body, registering the dependency edge BEFORE the
    /// awaited read.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="computed">The slot to await.</param>
    public Task<T> TrackAsync<T>(AsyncComputed<T> computed)
    {
        ArgumentNullException.ThrowIfNull(computed);
        _ctx.TrackEdge(_owner, computed.Node, Token);
        return computed.GetAsync(Token);
    }
}

// ---------------------------------------------------------------------------
// Handles
// ---------------------------------------------------------------------------

/// <summary>A mutable input cell on the async graph.</summary>
/// <remarks>
/// Sources are the SYNCHRONOUS input layer: creation, <see cref="Peek"/>, and
/// <see cref="Set"/> are synchronous even on the async plane. Only computed evaluation and
/// effects are asynchronous.
/// </remarks>
/// <typeparam name="T">The value type.</typeparam>
public sealed class AsyncSource<T>
{
    private readonly AsyncContext _ctx;

    internal AsyncSource(AsyncContext ctx, AsyncCellNode node)
    {
        _ctx = ctx;
        Node = node;
    }

    internal AsyncCellNode Node { get; }

    /// <summary>
    /// This handle's graph node — the opaque token the degree accessors on
    /// <see cref="AsyncContext"/> take. It exposes no state of its own.
    /// </summary>
    public AsyncNode GraphNode => Node;

    /// <summary>Reads the value without registering a dependency.</summary>
    /// <exception cref="DisposedNodeException">This source has been disposed.</exception>
    public T Peek() => _ctx.PeekCell<T>(Node);

    /// <summary>
    /// Reads the value, reporting a <see cref="DisposedNodeException"/> instead of throwing.
    /// </summary>
    /// <param name="value">The value, or <c>default</c> on failure.</param>
    /// <param name="error">The disposal error, or null on success.</param>
    /// <returns>True when a value was read.</returns>
    public bool TryGet(out T value, out DisposedNodeException? error)
    {
        try
        {
            value = Peek();
            error = null;
            return true;
        }
        catch (DisposedNodeException e)
        {
            value = default!;
            error = e;
            return false;
        }
    }

    /// <summary>
    /// Assigns a new value; if it differs from the current one, dependents are invalidated (or
    /// queued, inside a <see cref="AsyncContext.Batch"/>).
    /// </summary>
    /// <param name="value">The value to store.</param>
    public void Set(T value) => _ctx.SetCell(Node, value);

    /// <summary>
    /// Tears down this source: detaches its dependents and dirties the surviving cone. Sources
    /// are pure inputs, so only downstream edges need detaching. Idempotent.
    /// </summary>
    public void Dispose() => _ctx.DisposeCell(Node);
}

/// <summary>A computed/memoized async slot.</summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class AsyncComputed<T>
{
    private readonly AsyncContext _ctx;

    internal AsyncComputed(AsyncContext ctx, AsyncSlotNode node)
    {
        _ctx = ctx;
        Node = node;
    }

    internal AsyncSlotNode Node { get; }

    /// <summary>
    /// This handle's graph node — the opaque token the degree accessors on
    /// <see cref="AsyncContext"/> take. It exposes no state of its own.
    /// </summary>
    public AsyncNode GraphNode => Node;

    /// <summary>This slot's current state-machine state.</summary>
    public AsyncSlotState State => _ctx.SlotState(Node);

    /// <summary>
    /// This slot's revision, incremented on each invalidation. A completion recorded against an
    /// older revision is discarded rather than published.
    /// </summary>
    public int Revision => _ctx.SlotRevision(Node);

    /// <summary>
    /// The synchronous fast path: returns the cached value when the slot is
    /// <see cref="AsyncSlotState.Resolved"/>, and spawns nothing otherwise.
    /// </summary>
    /// <param name="value">The cached value, or <c>default</c>.</param>
    /// <returns>True when the slot was resolved.</returns>
    public bool TryGet(out T value) => _ctx.TrySlotValue(Node, out value);

    /// <summary>
    /// Awaits the slot's value.
    /// </summary>
    /// <remarks>
    /// A resolved slot returns immediately. Otherwise the caller attaches to the in-flight
    /// computation, spawning one only when none is running (in-flight deduplication). The token
    /// cancels THIS waiter only — dropping one waiter never cancels a computation other waiters
    /// still need. Supersession re-resolves transparently rather than surfacing an error.
    /// </remarks>
    /// <param name="cancellationToken">Cancels this waiter.</param>
    public Task<T> GetAsync(CancellationToken cancellationToken = default) =>
        _ctx.GetSlotAsync<T>(Node, cancellationToken);

    /// <summary>
    /// Tears down this slot: cancels any in-flight computation, hands blocked waiters a
    /// <see cref="DisposedNodeException"/>, detaches both edge directions, and dirties the
    /// surviving dependent cone. Idempotent.
    /// </summary>
    public void Dispose() => _ctx.DisposeSlot(Node);
}

/// <summary>An async effect: a body that reruns when a tracked dependency changes.</summary>
/// <remarks>
/// Reruns are serialized per effect — a rerun does not start until the previous cleanup has
/// completed — and the cleanup runs on rerun or dispose and at NO other time. In particular it
/// does not run at the end of the flush that ran the body: the canonical effect acquires a
/// resource in the body and releases it in the cleanup, so a flush-end cleanup would release
/// while the effect is still live.
/// </remarks>
public sealed class AsyncEffectHandle
{
    private readonly AsyncContext _ctx;

    internal AsyncEffectHandle(AsyncContext ctx, AsyncEffectNode node)
    {
        _ctx = ctx;
        Node = node;
    }

    internal AsyncEffectNode Node { get; }

    /// <summary>
    /// This handle's graph node — the opaque token the degree accessors on
    /// <see cref="AsyncContext"/> take. It exposes no state of its own.
    /// </summary>
    public AsyncNode GraphNode => Node;

    /// <summary>Whether the effect is still registered (not disposed).</summary>
    public bool IsActive => _ctx.IsEffectActive(Node);

    /// <summary>
    /// Disposes the effect: cancels an in-flight body, removes pending reruns, detaches its
    /// dependency edges, and awaits its pending cleanup. Idempotent.
    /// </summary>
    public ValueTask DisposeAsync() => _ctx.DisposeEffectAsync(Node);
}

// ---------------------------------------------------------------------------
// The context
// ---------------------------------------------------------------------------

/// <summary>
/// The async reactive surface: a distinct graph whose slots resolve through tasks.
/// </summary>
/// <remarks>
/// Unlike <see cref="Context"/>, an <see cref="AsyncContext"/> is safe for concurrent use: every
/// graph mutation is serialized under one reentrant lock, and bodies run off it.
/// </remarks>
public sealed class AsyncContext : IAsyncDisposable
{
    private readonly System.Threading.Lock _gate = new();

    private bool _disposed;
    private int _batchDepth;
    private readonly HashSet<AsyncNode> _batchQueue = [];
    private readonly HashSet<AsyncSlotNode> _computing = [];
    private readonly HashSet<AsyncEffectNode> _effects = [];
    private int _bodiesRunning;

    /// <summary>Creates an empty async reactive graph.</summary>
    public AsyncContext() { }

    /// <summary>
    /// Whether no compute and no effect body is currently in flight.
    /// </summary>
    /// <remarks>
    /// Every operation on this context is synchronous from the caller's point of view EXCEPT
    /// compute and effect bodies, which are spawned. Assertions about run order, observation
    /// counts, or edge degree are meaningless until those have run, so tests and conformance
    /// runners drive the graph to quiescence first. This changes only WHEN an assertion is
    /// evaluated, never what it asserts: an effect that never runs still fails.
    /// </remarks>
    public bool IsQuiescent
    {
        get
        {
            lock (_gate) return _computing.Count == 0 && _bodiesRunning == 0;
        }
    }

    /// <summary>Blocks until <see cref="IsQuiescent"/> or the timeout elapses.</summary>
    /// <param name="timeout">How long to wait. Defaults to 30 seconds when omitted.</param>
    /// <returns>True when the graph reached quiescence.</returns>
    public bool Settle(TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        while (!IsQuiescent)
        {
            if (deadline.Elapsed > limit) return false;
            Thread.Sleep(TimeSpan.FromMilliseconds(0.05));
        }
        return true;
    }

    // --- construction -------------------------------------------------------

    /// <summary>Creates a mutable input cell.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="initial">The initial value.</param>
    public AsyncSource<T> Source<T>(T initial) => new(this, new AsyncCellNode { Value = initial });

    /// <summary>Creates an UNGUARDED async computed slot: every resolution cascades.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="compute">The async compute body; it reads through the view it is handed.</param>
    public AsyncComputed<T> Slot<T>(Func<AsyncCompute, Task<T>> compute)
    {
        ArgumentNullException.ThrowIfNull(compute);
        return NewSlot(compute, equals: null);
    }

    /// <summary>
    /// Creates a GUARDED async computed slot — the default derived kind. A resolution equal to
    /// the cached value keeps the cache and suppresses the downstream cascade.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="compute">The async compute body.</param>
    /// <param name="comparer">The guard's equality. Defaults to <see cref="EqualityComparer{T}.Default"/>.</param>
    public AsyncComputed<T> Computed<T>(Func<AsyncCompute, Task<T>> compute, IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(compute);
        var cmp = comparer ?? EqualityComparer<T>.Default;
        return NewSlot(compute, (a, b) => cmp.Equals((T)a!, (T)b!));
    }

    private AsyncComputed<T> NewSlot<T>(Func<AsyncCompute, Task<T>> compute, Func<object?, object?, bool>? equals)
    {
        var node = new AsyncSlotNode
        {
            Compute = async cc => await compute(cc).ConfigureAwait(false),
            Guard = equals,
        };
        return new AsyncComputed<T>(this, node);
    }

    /// <summary>
    /// Creates an async effect and schedules its first run.
    /// </summary>
    /// <remarks>
    /// The first run is SCHEDULED, not inline: the handle is returned before the body has run, so
    /// callers that need the body's observations must drive the context to quiescence first.
    /// </remarks>
    /// <param name="body">The effect body, returning an optional async cleanup.</param>
    public AsyncEffectHandle Effect(Func<AsyncCompute, Task<Func<Task>?>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var node = new AsyncEffectNode { Body = body };
        var handle = new AsyncEffectHandle(this, node);
        lock (_gate)
        {
            if (_disposed) return handle;
            _effects.Add(node);
            ScheduleEffectRerun(node);
        }
        return handle;
    }

    /// <summary>Opens a teardown scope on this async graph.</summary>
    public AsyncTeardownScope Scope() => new(this);

    // --- reads / writes -----------------------------------------------------

    internal T PeekCell<T>(AsyncCellNode node)
    {
        lock (_gate)
        {
            if (node.Disposed) throw new DisposedNodeException(null, "source");
            return (T)node.Value!;
        }
    }

    internal void SetCell<T>(AsyncCellNode node, T value)
    {
        lock (_gate)
        {
            if (_disposed || node.Disposed) return;
            if (EqualityComparer<T>.Default.Equals((T)node.Value!, value)) return;
            node.Value = value;
            InvalidateDependents(node);
        }
    }

    internal AsyncSlotState SlotState(AsyncSlotNode node)
    {
        lock (_gate) return node.State;
    }

    internal int SlotRevision(AsyncSlotNode node)
    {
        lock (_gate) return node.Revision;
    }

    internal bool TrySlotValue<T>(AsyncSlotNode node, out T value)
    {
        lock (_gate)
        {
            if (node.State is AsyncSlotState.Resolved)
            {
                value = (T)node.Value!;
                return true;
            }
        }
        value = default!;
        return false;
    }

    internal T TrackSource<T>(AsyncNode owner, AsyncCellNode dep, CancellationToken token)
    {
        lock (_gate)
        {
            if (dep.Disposed) throw new DisposedNodeException(null, "source");
            if (!token.IsCancellationRequested) TrackEdgeLocked(owner, dep);
            return (T)dep.Value!;
        }
    }

    internal void TrackEdge(AsyncNode owner, AsyncNode dep, CancellationToken token)
    {
        lock (_gate)
        {
            if (!token.IsCancellationRequested) TrackEdgeLocked(owner, dep);
        }
    }

    private void TrackEdgeLocked(AsyncNode owner, AsyncNode dep)
    {
        if (_disposed) return;
        // Never build an edge onto or out of a torn-down node. A body runs off the lock and can
        // reach this point AFTER its owner was disposed; disposal already dropped that owner's
        // edges, so a late registration would resurrect one and leak it for the life of the
        // context. That is exactly the shape the churn fixture measures.
        if (dep.Disposed || owner.Disposed) return;
        if (dep.Dependents.Add(owner)) owner.Dependencies.Add(dep);
    }

    internal async Task<T> GetSlotAsync<T>(AsyncSlotNode node, CancellationToken cancellationToken)
    {
        // The re-resolve loop. The slot state is authoritative and can change between lock
        // acquisitions, so each pass re-reads it rather than asserting what it must be: a
        // Computing -> Resolved transition between passes is expected, and a superseded run
        // closes its waiters without a final value.
        while (true)
        {
            TaskCompletionSource<AsyncResult> waiter;
            lock (_gate)
            {
                if (_disposed) throw new AsyncContextDisposedException();
                if (node.Disposed) throw new DisposedNodeException(null, "computed");
                // Only Resolved short-circuits. An Error slot RETRIES — the spec's
                // `Error → Computing` transition, "get_async retry after an error" — rather than
                // serving the cached failure forever, so a transient failure is not permanent.
                // (lazily-go returns the stored error here instead; the spec is the authority.)
                if (node.State is AsyncSlotState.Resolved) return (T)node.Value!;

                waiter = new TaskCompletionSource<AsyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                node.Waiters.Add(waiter);
                if (node.InFlight is null) SpawnCompute(node);
            }

            AsyncResult result;
            // Waiter cancellation drops only THIS waiter; the shared computation keeps running
            // for the waiters that remain, and still publishes its result.
            await using (cancellationToken.Register(() =>
            {
                lock (_gate) node.Waiters.Remove(waiter);
                waiter.TrySetCanceled(cancellationToken);
            }).ConfigureAwait(false))
            {
                result = await waiter.Task.ConfigureAwait(false);
            }

            if (result.Superseded) continue;
            if (result.Error is not null) throw result.Error;
            return (T)result.Value!;
        }
    }

    // --- compute lifecycle (all callers hold the lock) ----------------------

    private void SpawnCompute(AsyncSlotNode node)
    {
        node.State = AsyncSlotState.Computing;
        DetachUpstream(node);
        var inFlight = new InFlight();
        node.InFlight = inFlight;
        _computing.Add(node);
        var view = new AsyncCompute(this, node, inFlight.Cts.Token);
        var body = node.Compute;
        _ = Task.Run(async () =>
        {
            object? value = null;
            Exception? error = null;
            try
            {
                value = await body(view).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // A failed body is the slot's Error state, not a crash: the contract is "errors
                // on the next read", exactly as on the synchronous plane.
                error = e;
            }
            OnComputeComplete(node, inFlight, value, error);
        });
    }

    private void InvalidateSlot(AsyncSlotNode node)
    {
        node.Revision++;
        node.State = AsyncSlotState.Computing;
        SupersedeInFlight(node);
    }

    /// <summary>
    /// Cancels the in-flight computation and tells current waiters to re-resolve. The stale
    /// computation's completion is discarded later by the identity gate in
    /// <see cref="OnComputeComplete"/>.
    /// </summary>
    private void SupersedeInFlight(AsyncSlotNode node)
    {
        var inFlight = node.InFlight;
        node.InFlight = null;
        _computing.Remove(node);
        inFlight?.Cts.Cancel();
        Deliver(node, new AsyncResult(null, null, Superseded: true));
    }

    private void OnComputeComplete(AsyncSlotNode node, InFlight inFlight, object? value, Exception? error)
    {
        lock (_gate)
        {
            // The identity gate: this run's token was replaced by a supersession, a disposal, or
            // a context teardown, so its value describes a revision that no longer exists.
            // Discard it — the waiters were already told to re-resolve.
            if (!ReferenceEquals(node.InFlight, inFlight)) return;
            node.InFlight = null;
            _computing.Remove(node);

            if (error is not null)
            {
                node.Error = error;
                node.State = AsyncSlotState.Error;
                Deliver(node, new AsyncResult(null, error, Superseded: false));
                return;
            }

            if (node.Guard is not null && node.HasValue && node.Guard(node.Value, value))
            {
                // Memo equality suppression: keep the cached value and cascade nothing.
                node.State = AsyncSlotState.Resolved;
                Deliver(node, new AsyncResult(node.Value, null, Superseded: false));
                return;
            }

            node.Value = value;
            node.HasValue = true;
            node.Error = null;
            node.State = AsyncSlotState.Resolved;
            Deliver(node, new AsyncResult(value, null, Superseded: false));
            // Deliberately no cascade here. The write that invalidated this slot already walked
            // the FULL transitive cone, so every downstream slot is already stale and every
            // downstream effect already scheduled. Re-cascading on resolution would reschedule
            // the very effect whose pull caused this computation — a scheduler-closed loop.
        }
    }

    private static void Deliver(AsyncSlotNode node, AsyncResult result)
    {
        if (node.Waiters.Count == 0) return;
        var waiters = node.Waiters.ToArray();
        node.Waiters.Clear();
        // The waiters complete asynchronously (RunContinuationsAsynchronously), so no
        // continuation runs on this thread while the lock is held.
        foreach (var w in waiters) w.TrySetResult(result);
    }

    // --- invalidation -------------------------------------------------------

    private void InvalidateDependents(AsyncNode node)
    {
        if (_disposed) return;
        if (_batchDepth > 0)
        {
            _batchQueue.Add(node);
            return;
        }
        Propagate(node.Dependents.ToArray(), schedule: true);
    }

    /// <summary>
    /// Walks the FULL transitive dependent cone rooted at each of <paramref name="roots"/>.
    /// </summary>
    /// <remarks>
    /// The whole cone, not one level: a read short-circuits on <see cref="AsyncSlotState.Resolved"/>,
    /// so a downstream slot left resolved would serve its cached value forever and no pull chain
    /// could rescue it. That one-level stop is a real defect this walk exists to avoid, and it is
    /// invisible to a synchronous replay.
    /// <para>
    /// Effects are collected and scheduled AFTER the walk. Running one inline would detach its
    /// dependency edges while the walk is still reading them.
    /// </para>
    /// <para>
    /// <paramref name="schedule"/> distinguishes a publish from a teardown. A write invalidates
    /// and then reruns the effects it reached. A disposal invalidates and deliberately does NOT:
    /// running an effect during teardown re-enters a body that reads the node being disposed,
    /// which breaks teardown idempotence. The contract is "errors on next recompute".
    /// </para>
    /// </remarks>
    private void Propagate(IReadOnlyList<AsyncNode> roots, bool schedule)
    {
        if (roots.Count == 0) return;
        var stack = new List<AsyncNode>(roots);
        var visited = new HashSet<AsyncNode>();
        var effects = new List<AsyncEffectNode>();
        while (stack.Count > 0)
        {
            var n = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (n.Disposed || !visited.Add(n)) continue;

            if (n is AsyncEffectNode e)
            {
                // A frontier leaf: nothing can depend on an effect, so the walk stops here.
                effects.Add(e);
                continue;
            }

            if (n is AsyncSlotNode s) InvalidateSlot(s);
            foreach (var d in n.Dependents) stack.Add(d);
        }

        if (!schedule) return;
        foreach (var e in effects) ScheduleEffectRerun(e);
    }

    // --- effects ------------------------------------------------------------

    private void ScheduleEffectRerun(AsyncEffectNode node)
    {
        if (node.Disposed || _disposed) return;
        if (node.Running)
        {
            // Serialized reruns: the next run does not start until this one's cleanup completes.
            node.RerunScheduled = true;
            return;
        }
        RunEffect(node);
    }

    private void RunEffect(AsyncEffectNode node)
    {
        node.Running = true;
        _bodiesRunning++;
        DetachUpstream(node);
        var cts = new CancellationTokenSource();
        node.Cts = cts;
        // The retained cleanup from the PREVIOUS run. It is taken now and awaited before the
        // next body, which is the whole trigger contract: cleanup runs on rerun or dispose, and
        // never merely because a flush ended.
        var cleanup = node.Cleanup;
        node.Cleanup = null;
        var view = new AsyncCompute(this, node, cts.Token);
        var body = node.Body;
        _ = Task.Run(async () =>
        {
            if (cleanup is not null) await RunCleanupSafe(cleanup).ConfigureAwait(false);
            Func<Task>? next = null;
            try
            {
                next = await body(view).ConfigureAwait(false);
            }
            catch
            {
                // A failed body publishes nothing; the effect stays live and reruns on the next
                // change, matching the synchronous plane's "errors on next recompute".
            }
            OnEffectDone(node, next);
        });
    }

    private void OnEffectDone(AsyncEffectNode node, Func<Task>? cleanup)
    {
        lock (_gate)
        {
            node.Running = false;
            _bodiesRunning--;
            if (node.Disposed)
            {
                // A cleanup produced after disposal has nothing to release that disposal did not
                // already release; drop it.
                return;
            }
            node.Cleanup = cleanup;
            if (!node.RerunScheduled) return;
            node.RerunScheduled = false;
            RunEffect(node);
        }
    }

    private static async Task RunCleanupSafe(Func<Task> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch
        {
            // Cleanup is best-effort: a failing release must not strand the next body.
        }
    }

    internal bool IsEffectActive(AsyncEffectNode node)
    {
        lock (_gate) return !node.Disposed;
    }

    internal async ValueTask DisposeEffectAsync(AsyncEffectNode node)
    {
        Func<Task>? cleanup;
        lock (_gate)
        {
            if (node.Disposed) return;
            node.Disposed = true;
            node.RerunScheduled = false;
            node.Cts?.Cancel();
            _effects.Remove(node);
            DetachUpstream(node);
            cleanup = node.Cleanup;
            node.Cleanup = null;
        }
        if (cleanup is not null) await RunCleanupSafe(cleanup).ConfigureAwait(false);
    }

    // --- batching -----------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="run"/> inside a batch.
    /// </summary>
    /// <remarks>
    /// The boundary is SYNCHRONOUS: writes made inside queue their invalidation roots, and the
    /// queued roots propagate once at the outermost exit. Async reruns are scheduled there, so
    /// they run after <paramref name="run"/> returns, never inside it. Re-entrant.
    /// </remarks>
    /// <param name="run">The batch body.</param>
    public void Batch(Action run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate) _batchDepth++;
        try
        {
            run();
        }
        finally
        {
            lock (_gate)
            {
                _batchDepth--;
                if (_batchDepth == 0 && _batchQueue.Count > 0)
                {
                    var queued = _batchQueue.ToArray();
                    _batchQueue.Clear();
                    foreach (var n in queued) InvalidateDependents(n);
                }
            }
        }
    }

    // --- disposal -----------------------------------------------------------

    private void DetachUpstream(AsyncNode node)
    {
        foreach (var dep in node.Dependencies) dep.Dependents.Remove(node);
        node.Dependencies.Clear();
    }

    /// <summary>
    /// Drops a node's reverse edges and dirties its dependent cone WITHOUT running anything.
    /// </summary>
    /// <remarks>
    /// The direct dependents are captured before the edges are cut, because the walk that dirties
    /// the cone has to start from them: cutting first and walking after would reach nothing, and
    /// walking first and cutting after would traverse edges that no longer exist.
    /// </remarks>
    private void DetachNode(AsyncNode node)
    {
        var direct = node.Dependents.ToArray();
        foreach (var d in direct) d.Dependencies.Remove(node);
        node.Dependents.Clear();
        Propagate(direct, schedule: false);
    }

    internal void DisposeCell(AsyncCellNode node)
    {
        lock (_gate)
        {
            if (node.Disposed) return;
            node.Disposed = true;
            DetachNode(node);
        }
    }

    internal void DisposeSlot(AsyncSlotNode node)
    {
        lock (_gate)
        {
            if (node.Disposed) return;
            node.Disposed = true;
            // Blocked waiters get the disposal error rather than a supersession, which would send
            // them round the re-resolve loop only to find the slot gone.
            var inFlight = node.InFlight;
            node.InFlight = null;
            _computing.Remove(node);
            inFlight?.Cts.Cancel();
            Deliver(node, new AsyncResult(null, new DisposedNodeException(null, "computed"), Superseded: false));
            node.State = AsyncSlotState.Empty;
            node.Value = null;
            node.HasValue = false;
            DetachUpstream(node);
            DetachNode(node);
        }
    }

    /// <summary>
    /// How many nodes currently depend on <paramref name="node"/> — the size of its reverse edge
    /// set. Zero for a disposed node.
    /// </summary>
    /// <param name="node">The node to measure.</param>
    public int DependentCount(AsyncNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate) return node.Disposed ? 0 : node.Dependents.Count;
    }

    /// <summary>
    /// How many nodes <paramref name="node"/> currently depends on — the size of its forward edge
    /// set. Zero for a disposed node and for a source, which is a pure input.
    /// </summary>
    /// <param name="node">The node to measure.</param>
    public int DependencyCount(AsyncNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate) return node.Disposed ? 0 : node.Dependencies.Count;
    }

    /// <summary>Whether <paramref name="node"/> has been torn down.</summary>
    /// <param name="node">The node to test.</param>
    public bool IsDisposed(AsyncNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate) return node.Disposed;
    }

    /// <summary>
    /// Disposes the context: cancels every in-flight computation, hands blocked waiters an
    /// <see cref="AsyncContextDisposedException"/>, and runs and AWAITS every active effect's
    /// cleanup before returning. Idempotent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        AsyncEffectNode[] effects;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var s in _computing.ToArray())
            {
                var inFlight = s.InFlight;
                s.InFlight = null;
                inFlight?.Cts.Cancel();
                Deliver(s, new AsyncResult(null, new AsyncContextDisposedException(), Superseded: false));
            }
            _computing.Clear();
            effects = [.. _effects];
        }

        foreach (var e in effects) await DisposeEffectAsync(e).ConfigureAwait(false);

        lock (_gate) _effects.Clear();
    }
}

/// <summary>
/// Groups async nodes so they can be torn down together, in reverse creation order.
/// </summary>
/// <remarks>
/// The <c>Own</c> / <see cref="Disarm"/> / <see cref="CloseAsync"/> shape and its
/// rationale are identical to the synchronous <see cref="TeardownScope"/>.
/// </remarks>
public sealed class AsyncTeardownScope
{
    private readonly AsyncContext _ctx;
    private List<Func<ValueTask>> _owned = [];
    private bool _closed;

    internal AsyncTeardownScope(AsyncContext ctx) => _ctx = ctx;

    /// <summary>How many nodes this scope currently owns.</summary>
    public int Count => _owned.Count;

    /// <summary>Places a source under this scope's ownership and returns it.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="node">The source to own.</param>
    public AsyncSource<T> Own<T>(AsyncSource<T> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_closed) _owned.Add(() => { node.Dispose(); return ValueTask.CompletedTask; });
        return node;
    }

    /// <summary>Places a computed slot under this scope's ownership and returns it.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="node">The slot to own.</param>
    public AsyncComputed<T> Own<T>(AsyncComputed<T> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_closed) _owned.Add(() => { node.Dispose(); return ValueTask.CompletedTask; });
        return node;
    }

    /// <summary>Places an effect under this scope's ownership and returns it.</summary>
    /// <param name="node">The effect to own.</param>
    public AsyncEffectHandle Own(AsyncEffectHandle node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_closed) _owned.Add(node.DisposeAsync);
        return node;
    }

    /// <summary>
    /// Cancels this scope's teardown: <see cref="CloseAsync"/> then disposes nothing and the
    /// nodes revert to plain context ownership, untouched and individually disposable.
    /// </summary>
    public void Disarm() => _owned = [];

    /// <summary>Tears down every node this scope owns, in reverse creation order. Idempotent.</summary>
    public async ValueTask CloseAsync()
    {
        var owned = _owned;
        _owned = [];
        _closed = true;
        for (var i = owned.Count - 1; i >= 0; i--) await owned[i]().ConfigureAwait(false);
    }

    /// <summary>The owning async context.</summary>
    public AsyncContext Scope => _ctx;
}
