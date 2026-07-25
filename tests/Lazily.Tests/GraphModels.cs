namespace Lazily.Tests;

/// <summary>The kind of node a fixture op created.</summary>
public enum NodeKind
{
    /// <summary>A mutable input cell (`cell` / `merge_cell`).</summary>
    Cell,

    /// <summary>A derived node (`computed`).</summary>
    Computed,

    /// <summary>A side-effect sink (`effect` / `fanout`).</summary>
    Effect,

    /// <summary>An eager derived node (`signal`).</summary>
    Signal,
}

/// <summary>One node in whichever graph is under test.</summary>
/// <remarks>
/// The handle is opaque to the engine; only the model knows how to read or tear it down. That is
/// what lets a single op stream replay against three execution models.
/// </remarks>
/// <param name="Kind">The node kind.</param>
/// <param name="Id">The fixture-level id.</param>
/// <param name="Handle">The model-specific handle.</param>
public sealed record NodeRef(NodeKind Kind, string Id, object Handle);

/// <summary>
/// The constructor surface shared by a context and a teardown scope, so the engine can dispatch a
/// scoped op without a second code path.
/// </summary>
public interface INodeFactory
{
    /// <summary>Creates a plain input cell.</summary>
    /// <param name="id">The fixture-level id.</param>
    /// <param name="value">The initial value.</param>
    NodeRef Cell(string id, long value);

    /// <summary>Creates a merge cell — an input cell whose write folds under a Sum policy.</summary>
    /// <param name="id">The fixture-level id.</param>
    /// <param name="value">The initial value.</param>
    NodeRef MergeCell(string id, long value);

    /// <summary>Creates a lazy derived node summing <paramref name="reads"/> plus an offset.</summary>
    /// <param name="id">The fixture-level id.</param>
    /// <param name="reads">The nodes the body reads, tracked.</param>
    /// <param name="offset">A constant added to the sum.</param>
    NodeRef Computed(string id, IReadOnlyList<NodeRef> reads, long offset);

    /// <summary>Creates a side-effect observer that reads <paramref name="reads"/>, tracked.</summary>
    /// <param name="id">The fixture-level id.</param>
    /// <param name="reads">The nodes the body reads.</param>
    NodeRef Effect(string id, IReadOnlyList<NodeRef> reads);
}

/// <summary>One teardown scope in whichever graph is under test.</summary>
public interface IScopeModel : INodeFactory
{
    /// <summary>How many nodes the scope currently owns.</summary>
    int Owned { get; }

    /// <summary>Cancels the scope's teardown.</summary>
    void Disarm();

    /// <summary>Tears the scope's members down, in reverse creation order.</summary>
    void CloseScope();
}

/// <summary>
/// One execution model, so the same fixture op stream replays against every context this binding
/// ships.
/// </summary>
/// <remarks>
/// The reason the runner is parameterised rather than hardcoding <see cref="Context"/>: a
/// cascade defect can be correct synchronously and broken asynchronously — a chain
/// <c>cell → a → b</c> that stops one level below the write serves a stale <c>b</c> forever, and
/// no synchronous replay of any fixture can see it. The corpus contains
/// <c>transitive_invalidation_reaches_depth</c> precisely for that property, so it has to run on
/// every plane, not just the easy one.
/// </remarks>
public interface IGraphModel : INodeFactory, IDisposable
{
    /// <summary>The model's name, used in divergence and ledger keys.</summary>
    string Name { get; }

    /// <summary>Creates an eager derived node.</summary>
    /// <param name="id">The fixture-level id.</param>
    /// <param name="reads">The nodes the body reads.</param>
    /// <param name="offset">A constant added to the sum.</param>
    NodeRef Signal(string id, IReadOnlyList<NodeRef> reads, long offset);

    /// <summary>Removes only the eager puller, leaving the backing derived node readable.</summary>
    /// <param name="node">The signal to de-eager.</param>
    void DisposeSignal(NodeRef node);

    /// <summary>
    /// Creates an effect that reads <paramref name="reads"/> tracked and folds their sum into
    /// <paramref name="target"/> through the UNTRACKED write surface.
    /// </summary>
    /// <param name="id">The fixture-level id.</param>
    /// <param name="reads">The nodes the body reads.</param>
    /// <param name="target">The merge cell the body folds into.</param>
    NodeRef FeedEffect(string id, IReadOnlyList<NodeRef> reads, NodeRef target);

    /// <summary>
    /// Creates the divergent effect: it reads <paramref name="own"/> tracked and writes an
    /// incremented value back into it, closing a feedback loop through the SCHEDULER.
    /// </summary>
    /// <param name="id">The fixture-level id.</param>
    /// <param name="own">The cell the body both reads and writes.</param>
    NodeRef SelfWritingEffect(string id, NodeRef own);

    /// <summary>Reads a node from outside the graph.</summary>
    /// <param name="node">The node to read.</param>
    /// <returns>The value, or <c>Ok=false</c> when the read is the corpus's read-after-dispose error.</returns>
    (bool Ok, long Value) Read(NodeRef node);

    /// <summary>Writes a cell.</summary>
    /// <param name="node">The cell to write.</param>
    /// <param name="value">The value.</param>
    void SetCell(NodeRef node, long value);

    /// <summary>Folds a value into a merge cell under its policy.</summary>
    /// <param name="node">The merge cell.</param>
    /// <param name="value">The operand.</param>
    void MergeInto(NodeRef node, long value);

    /// <summary>Tears a node down.</summary>
    /// <param name="node">The node to dispose.</param>
    void DisposeNode(NodeRef node);

    /// <summary>The size of a node's reverse edge set.</summary>
    /// <param name="node">The node to measure.</param>
    int DependentCount(NodeRef node);

    /// <summary>The size of a node's forward edge set.</summary>
    /// <param name="node">The node to measure.</param>
    int DependencyCount(NodeRef node);

    /// <summary>Whether an effect is still active.</summary>
    /// <param name="node">The effect to test.</param>
    bool IsEffectActive(NodeRef node);

    /// <summary>Runs <paramref name="writes"/> inside a coalescing batch.</summary>
    /// <param name="writes">The batch body.</param>
    void Batch(Action writes);

    /// <summary>The cumulative number of times a node's compute body ran.</summary>
    /// <param name="id">The fixture-level id.</param>
    int ComputesOf(string id);

    /// <summary>The cumulative number of folds the LIBRARY performed for a merge cell.</summary>
    /// <param name="id">The fixture-level id.</param>
    int MergesOf(string id);

    /// <summary>Whether a merge cell with this id exists in the model.</summary>
    /// <param name="id">The fixture-level id.</param>
    bool KnowsMerge(string id);

    /// <summary>Opens a teardown scope.</summary>
    IScopeModel Scope();

    /// <summary>Resets the drain-exhaustion observable so one op's drain is measured alone.</summary>
    void ClearDrainExhaustion();

    /// <summary>Whether the most recent drain was cut short by the budget.</summary>
    bool DrainExhausted { get; }

    /// <summary>
    /// Drives the model to quiescence before assertions are evaluated.
    /// </summary>
    /// <remarks>
    /// Synchronous models are already quiescent when an op returns; async computes and effect
    /// bodies are spawned, so run order, observation counts, and every degree assertion are
    /// meaningless until the runtime has run them. This changes WHEN an assertion is evaluated,
    /// never what it asserts: an effect that never runs still fails.
    /// </remarks>
    void Settle();

    /// <summary>Effect runs, in order, by fixture id.</summary>
    IReadOnlyList<string> RunLog { get; }

    /// <summary>Effect cleanups, in order, by fixture id.</summary>
    IReadOnlyList<string> CleanupLog { get; }
}

/// <summary>Effect run/cleanup logs, shared by every model.</summary>
/// <remarks>Async bodies run on the thread pool, so the lists are lock-guarded.</remarks>
public sealed class EffectLog
{
    private readonly System.Threading.Lock _gate = new();
    private readonly List<string> _runs = [];
    private readonly List<string> _cleanups = [];

    /// <summary>Records an effect run.</summary>
    /// <param name="id">The effect's fixture id.</param>
    public void Run(string id)
    {
        lock (_gate) _runs.Add(id);
    }

    /// <summary>Records an effect cleanup.</summary>
    /// <param name="id">The effect's fixture id.</param>
    public void Cleanup(string id)
    {
        lock (_gate) _cleanups.Add(id);
    }

    /// <summary>A snapshot of the run log.</summary>
    public IReadOnlyList<string> Runs
    {
        get { lock (_gate) return [.. _runs]; }
    }

    /// <summary>A snapshot of the cleanup log.</summary>
    public IReadOnlyList<string> Cleanups
    {
        get { lock (_gate) return [.. _cleanups]; }
    }
}

/// <summary>A per-id counter, shared by every model.</summary>
public sealed class CountLog
{
    private readonly System.Threading.Lock _gate = new();
    private readonly Dictionary<string, int> _counts = [];

    /// <summary>Declares an id so a zero count is distinguishable from an unknown one.</summary>
    /// <param name="id">The fixture id.</param>
    public void Declare(string id)
    {
        lock (_gate) _counts.TryAdd(id, 0);
    }

    /// <summary>Increments an id's count.</summary>
    /// <param name="id">The fixture id.</param>
    public void Tick(string id)
    {
        lock (_gate) _counts[id] = _counts.GetValueOrDefault(id) + 1;
    }

    /// <summary>Whether an id has been declared.</summary>
    /// <param name="id">The fixture id.</param>
    public bool Knows(string id)
    {
        lock (_gate) return _counts.ContainsKey(id);
    }

    /// <summary>An id's count, or zero.</summary>
    /// <param name="id">The fixture id.</param>
    public int Count(string id)
    {
        lock (_gate) return _counts.GetValueOrDefault(id);
    }
}
