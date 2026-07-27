// The reactive scope: batch coalescing, effect scheduling, and the node registry.
// Plus the two read surfaces of the value-threaded cell kernel (lzcellkernel):
// Context (untracked) and Compute (tracked).

namespace Lazily;

/// <summary>
/// The compute-time operations subset shared by the two read surfaces (lzcellkernel).
/// </summary>
/// <remarks>
/// Implemented by exactly two types:
/// <list type="bullet">
/// <item><see cref="Context"/> — the owning scope, whose reads are UNTRACKED.</item>
/// <item><see cref="Compute"/> — the per-recompute view handed to a compute/effect body,
/// whose reads register a dependency edge against the recomputing node.</item>
/// </list>
/// The tracking identity itself is an <c>internal</c> member, so this interface cannot be
/// implemented outside this assembly.
/// </remarks>
public interface IComputeOps
{
    /// <summary>The node a tracked read attributes to, or null for an untracked surface.</summary>
    internal ReactiveNode? TrackNode { get; }

    /// <summary>The owning reactive scope.</summary>
    Context Scope { get; }

    /// <summary>Runs <paramref name="fn"/> inside a coalescing batch on the owning scope.</summary>
    void Batch(Action fn);

    /// <summary>
    /// The untracked read surface (the owning <see cref="Context"/>). A read through it forms
    /// no dependency edge — the sole, explicit escape from tracking.
    /// </summary>
    Context Untracked();
}

/// <summary>
/// A reactive scope: batch/effect scheduling plus the node registry.
/// </summary>
/// <remarks>
/// Dependency tracking is value-threaded through the per-recompute <see cref="Compute"/> view;
/// there is no ambient recompute stack. A read attributes to the recomputing node only when it
/// goes through that view. Cached values are stored on the nodes themselves, not here. All
/// nodes that should react to each other must share the same context.
/// <para>
/// <see cref="Context"/> is NOT safe for concurrent use.
/// </para>
/// </remarks>
public sealed class Context : IComputeOps
{
    // Monotonic counter stamped onto each Compute view minted by NewCompute. It is the
    // generation half of the compute-view fortification guard: a view is dead the moment its
    // recompute returns, and the stamp lets a superseded view be told apart from the live one.
    private ulong _computeGen;

    private readonly List<ICacheable> _slots = [];
    private int _cachedCount;

    private int _batchDepth;
    private readonly HashSet<ReactiveNode> _batchedSources = [];
    private readonly List<Effect?> _pendingEffects = [];
    private int _effectsHead;
    private readonly HashSet<Effect> _scheduledEffects = [];
    private bool _flushingEffects;

    /// <summary>
    /// Teardown depth. While non-zero the invalidation cascade marks nodes dirty but must not
    /// RUN anything: an effect rerun or an eager recompute during teardown re-enters a compute
    /// that reads the node being disposed, which breaks teardown idempotence. The contract is
    /// "errors on next recompute", not "errors during the dispose call".
    /// </summary>
    internal int Disposing;

    /// <summary>
    /// The eager side table: maps an eager <see cref="Computed{T}"/> node to the puller
    /// <see cref="Effect"/> that keeps it materialized. Kept off the node so a lazy computed
    /// costs nothing; one entry per eager computed.
    /// </summary>
    internal readonly Dictionary<ReactiveNode, Effect> EagerBy = [];

    /// <summary>Creates an empty reactive scope.</summary>
    public Context() { }

    ReactiveNode? IComputeOps.TrackNode => null;

    /// <summary>The owning reactive scope — itself.</summary>
    public Context Scope => this;

    /// <summary>
    /// The untracked read surface — itself. The context is already untracked, so this is
    /// idempotent and lets <see cref="Context"/> satisfy <see cref="IComputeOps"/> uniformly.
    /// </summary>
    public Context Untracked() => this;

    /// <summary>Records a value-bearing node so <see cref="Clear"/> can reset it.</summary>
    internal void RegisterSlot(ICacheable s)
    {
        ((ReactiveNode)s).SlotIndex = _slots.Count;
        _slots.Add(s);
    }

    /// <summary>Swap-removes a disposed node from the registry in O(1).</summary>
    internal void UnregisterSlot(ReactiveNode n)
    {
        var i = n.SlotIndex;
        if (i < 0 || i >= _slots.Count) return;
        var last = _slots.Count - 1;
        var moved = _slots[last];
        _slots[i] = moved;
        ((ReactiveNode)moved).SlotIndex = i;
        _slots.RemoveAt(last);
        n.SlotIndex = -1;
    }

    internal void CachedCountInc() => _cachedCount++;

    internal void CachedCountDec() => _cachedCount--;

    /// <summary>The number of nodes currently holding a cached value.</summary>
    public int Size => _cachedCount;

    /// <summary>
    /// Drops every cached value. Dependency edges are re-established lazily as nodes are read
    /// again; <see cref="Source{T}"/> values are unaffected.
    /// </summary>
    public void Clear()
    {
        foreach (var s in _slots) s.ClearCache();
        _cachedCount = 0;
    }

    /// <summary>Mints a fresh compute view for <paramref name="node"/>.</summary>
    internal Compute NewCompute(ReactiveNode node) => new(this, node, ++_computeGen);

    /// <summary>Whether a <see cref="Batch(Action)"/> is currently active.</summary>
    public bool IsBatching => _batchDepth > 0;

    /// <summary>
    /// Runs <paramref name="fn"/> inside a batch. Source writes inside the batch defer their
    /// invalidation cascades until the outermost batch exits, at which point a single
    /// coalesced cascade fires and pending effects flush once. Re-entrant.
    /// </summary>
    public void Batch(Action fn)
    {
        Guard.NotNull(fn, nameof(fn));
        _batchDepth++;
        try
        {
            fn();
        }
        finally
        {
            _batchDepth--;
            if (_batchDepth == 0) FlushBatch();
        }
    }

    internal void SourceChanged(ReactiveNode source)
    {
        if (_batchDepth > 0)
        {
            _batchedSources.Add(source);
        }
        else
        {
            source.InvalidateCone();
            FlushEffects();
        }
    }

    private void FlushBatch()
    {
        if (_batchedSources.Count == 0)
        {
            FlushEffects();
            return;
        }
        var sources = _batchedSources.ToArray();
        _batchedSources.Clear();
        foreach (var s in sources) s.InvalidateCone();
        FlushEffects();
    }

    internal void ScheduleEffect(Effect e)
    {
        if (_scheduledEffects.Add(e)) _pendingEffects.Add(e);
    }

    internal void RemovePendingEffect(Effect e)
    {
        _scheduledEffects.Remove(e);
        for (var i = _effectsHead; i < _pendingEffects.Count; i++)
        {
            if (ReferenceEquals(_pendingEffects[i], e))
            {
                _pendingEffects[i] = null;
                return;
            }
        }
    }

    /// <summary>The default bound on effect runs within a single drain.</summary>
    public const int DefaultDrainBudget = 100_000;

    private int _drainBudget = DefaultDrainBudget;

    /// <summary>
    /// The bound on effect runs within one drain.
    /// </summary>
    /// <remarks>
    /// An effect that writes into its own dependency cone closes a feedback loop through the
    /// SCHEDULER, not the graph — it is not a dependency cycle, so acyclicity checks never fire,
    /// and the re-entrancy guard makes it run flat at constant stack depth, so no recursion bound
    /// can catch it either. This budget is the only exit. Raising it is a blunt instrument.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The budget is not positive.</exception>
    public int DrainBudget
    {
        get => _drainBudget;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
            _drainBudget = value;
        }
    }

    /// <summary>
    /// The most recent drain exhaustion, or null if no drain has been cut short since the last
    /// <see cref="ClearDrainExhaustion"/>.
    /// </summary>
    /// <remarks>
    /// Exhaustion is NOT convergence: it reports that the binding failed safely. The record
    /// identifies the effect that concentrated the runs, because a self-rescheduling effect is
    /// what distinguishes divergence from a long-but-terminating wide cascade — without that
    /// attribution a budget cannot tell them apart.
    /// </remarks>
    public DrainExhaustion? LastDrainExhaustion { get; private set; }

    /// <summary>Clears <see cref="LastDrainExhaustion"/> so the next drain is measured alone.</summary>
    public void ClearDrainExhaustion() => LastDrainExhaustion = null;

    internal void FlushEffects()
    {
        if (_flushingEffects) return;
        _flushingEffects = true;
        var runs = 0;
        Dictionary<Effect, int>? runsByEffect = null;
        try
        {
            while (_effectsHead < _pendingEffects.Count)
            {
                if (runs >= _drainBudget)
                {
                    LastDrainExhaustion = DrainExhaustion.From(_drainBudget, runs, runsByEffect);
                    // Drop the worklist: the loop is divergent and re-draining it would hang.
                    for (var i = _effectsHead; i < _pendingEffects.Count; i++)
                    {
                        if (_pendingEffects[i] is { } stranded) _scheduledEffects.Remove(stranded);
                    }
                    return;
                }

                var e = _pendingEffects[_effectsHead];
                _pendingEffects[_effectsHead] = null;
                _effectsHead++;
                if (e is null) continue;
                _scheduledEffects.Remove(e);
                var force = e.ForceRun;
                e.ForceRun = false;
                if (!force && e.IsActive && !e.DependenciesChanged())
                {
                    // Every dependency recomputed to an equal value (a guard upstream
                    // suppressed the change), so there is nothing new to react to.
                    continue;
                }
                runs++;
                runsByEffect ??= [];
                runsByEffect[e] = runsByEffect.GetValueOrDefault(e) + 1;
                e.Rerun();
            }
        }
        finally
        {
            _pendingEffects.Clear();
            _effectsHead = 0;
            _flushingEffects = false;
        }
    }
}

/// <summary>
/// The report produced when a drain hits <see cref="Context.DrainBudget"/> and is cut short.
/// </summary>
/// <param name="Budget">The budget that was hit.</param>
/// <param name="TotalRuns">Effect runs performed before the drain was cut.</param>
/// <param name="Offender">The effect that concentrated the most runs, if any.</param>
/// <param name="OffenderRuns">How many runs the offender accounted for.</param>
public sealed record DrainExhaustion(int Budget, int TotalRuns, Effect? Offender, int OffenderRuns)
{
    internal static DrainExhaustion From(int budget, int runs, Dictionary<Effect, int>? runsByEffect)
    {
        Effect? worst = null;
        var worstRuns = 0;
        if (runsByEffect is not null)
        {
            foreach (var (e, n) in runsByEffect)
            {
                if (n <= worstRuns) continue;
                worst = e;
                worstRuns = n;
            }
        }
        return new DrainExhaustion(budget, runs, worst, worstRuns);
    }
}

/// <summary>
/// The per-recompute view handed to a value-threaded compute or effect body.
/// </summary>
/// <remarks>
/// It carries the recomputing node AS A VALUE, so a tracked read attributes the edge to that
/// node by construction, never to ambient state. It is the sole tracking surface: reading a
/// handle through the owning <see cref="Context"/> (or <see cref="Untracked"/>) forms no edge.
/// <para>
/// <b>Fortification.</b> lazily-rs makes the view non-escapable by construction — a lifetime
/// binds it to the recompute. C# has no such lifetime, so non-escapability is backed by a
/// RUNTIME guard: each view is generation-stamped and marked dead the instant its recompute
/// returns. Any tracked read through a dead or superseded view throws
/// <see cref="StaleComputeException"/> rather than silently registering an edge against a node
/// that is no longer recomputing.
/// </para>
/// </remarks>
public sealed class Compute : IComputeOps
{
    private readonly Context _ctx;
    private readonly ReactiveNode _node;

    internal Compute(Context ctx, ReactiveNode node, ulong gen)
    {
        _ctx = ctx;
        _node = node;
        Generation = gen;
        Live = true;
    }

    internal ulong Generation { get; }

    internal bool Live { get; private set; }

    ReactiveNode? IComputeOps.TrackNode
    {
        get
        {
            if (!Live) throw new StaleComputeException("read after recompute returned");
            return _node;
        }
    }

    /// <summary>The owning reactive scope.</summary>
    public Context Scope => _ctx;

    /// <summary>
    /// The owning <see cref="Context"/>, the explicit untracked escape. A read through it
    /// registers no dependency edge.
    /// </summary>
    public Context Untracked() => _ctx;

    /// <summary>Runs <paramref name="fn"/> inside a coalescing batch on the owning scope.</summary>
    public void Batch(Action fn) => _ctx.Batch(fn);

    /// <summary>Marks the view dead so any later use fails the fortification guard.</summary>
    internal void Close() => Live = false;
}

/// <summary>
/// Thrown when a <see cref="Compute"/> view is used outside the recompute it belongs to — the
/// runtime half of the non-escapability guarantee C# cannot express in the type system.
/// </summary>
public sealed class StaleComputeException : InvalidOperationException
{
    /// <summary>Creates the exception for the given misuse stage.</summary>
    /// <param name="stage">Where the stale use was detected.</param>
    public StaleComputeException(string stage)
        : base($"lazily: stale Compute view used {stage} (the compute view must not escape its recompute)")
        => Stage = stage;

    /// <summary>Where the stale use was detected.</summary>
    public string Stage { get; }
}
