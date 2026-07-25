namespace Lazily.Tests;

/// <summary>
/// The corpus replayed against the default single-threaded <see cref="Context"/>.
/// </summary>
/// <remarks>
/// Every graph operation goes through <see cref="Run(Action)"/>, which is the seam
/// <see cref="ThreadSafeGraphModel"/> overrides. That is not indirection for its own sake: it
/// makes the thread-safe context's central claim — that it REFINES this kernel rather than
/// reimplementing it — the literal shape of the code, so the two models cannot drift apart
/// without the diff saying so.
/// </remarks>
public class SyncGraphModel : IGraphModel
{
    private readonly Context _ctx;
    private readonly EffectLog _log = new();
    private readonly CountLog _computes = new();
    private readonly CountLog _merges = new();

    /// <summary>Creates a model over a fresh single-threaded context.</summary>
    public SyncGraphModel()
        : this(new Context()) { }

    /// <summary>Creates a model over an existing context.</summary>
    /// <param name="ctx">The context to replay against.</param>
    protected SyncGraphModel(Context ctx) => _ctx = ctx;

    /// <summary>The context under test.</summary>
    protected Context Ctx => _ctx;

    /// <inheritdoc/>
    public virtual string Name => "Context";

    /// <summary>Runs a graph operation. The thread-safe model overrides this to take its lock.</summary>
    /// <param name="fn">The operation.</param>
    protected virtual void Run(Action fn) => fn();

    /// <summary>Runs a graph operation returning a value.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="fn">The operation.</param>
    protected virtual TResult Run<TResult>(Func<TResult> fn) => fn();

    /// <inheritdoc/>
    public void Dispose() => GC.SuppressFinalize(this);

    /// <inheritdoc/>
    public void Settle() { }

    /// <inheritdoc/>
    public IReadOnlyList<string> RunLog => _log.Runs;

    /// <inheritdoc/>
    public IReadOnlyList<string> CleanupLog => _log.Cleanups;

    /// <inheritdoc/>
    public int ComputesOf(string id) => _computes.Count(id);

    /// <inheritdoc/>
    public int MergesOf(string id) => _merges.Count(id);

    /// <inheritdoc/>
    public bool KnowsMerge(string id) => _merges.Knows(id);

    /// <inheritdoc/>
    public void ClearDrainExhaustion() => Run(_ctx.ClearDrainExhaustion);

    /// <inheritdoc/>
    public bool DrainExhausted => Run(() => _ctx.LastDrainExhaustion is not null);

    // --- construction -------------------------------------------------------

    /// <inheritdoc/>
    public NodeRef Cell(string id, long value) =>
        new(NodeKind.Cell, id, Run(() => _ctx.Source(value)));

    /// <inheritdoc/>
    public NodeRef MergeCell(string id, long value)
    {
        _merges.Declare(id);
        // `merges_of` counts folds the LIBRARY performed, not calls the runner made: the counter
        // lives inside the policy's fold, so a binding that deferred or dropped the fold reports
        // a lower count even though the call was issued. That is exactly the discrimination the
        // merge fixtures are written to make.
        var counted = MergePolicy.Sum<long>() with
        {
            Merge = (a, b) =>
            {
                _merges.Tick(id);
                return a + b;
            },
        };
        return new NodeRef(NodeKind.Cell, id, Run(() => _ctx.Source(value, counted)));
    }

    /// <inheritdoc/>
    public NodeRef Computed(string id, IReadOnlyList<NodeRef> reads, long offset) =>
        new(NodeKind.Computed, id, Run(() => NewComputed(id, reads, offset)));

    /// <inheritdoc/>
    public NodeRef Signal(string id, IReadOnlyList<NodeRef> reads, long offset) =>
        new(NodeKind.Signal, id, Run(() =>
        {
            var node = NewComputed(id, reads, offset);
            node.Eager();
            return node;
        }));

    private Computed<long> NewComputed(string id, IReadOnlyList<NodeRef> reads, long offset)
    {
        _computes.Declare(id);
        var deps = reads.ToArray();
        return _ctx.Computed<long>(c =>
        {
            _computes.Tick(id);
            var sum = offset;
            foreach (var d in deps) sum += TrackRead(d, c);
            return sum;
        }, name: id);
    }

    private static long TrackRead(NodeRef n, Compute c) => n.Kind switch
    {
        NodeKind.Cell => ((Source<long>)n.Handle).Get(c),
        NodeKind.Computed or NodeKind.Signal => ((Computed<long>)n.Handle).Get(c),
        _ => throw new InvalidOperationException($"cannot read node kind {n.Kind}"),
    };

    /// <inheritdoc/>
    public NodeRef Effect(string id, IReadOnlyList<NodeRef> reads) =>
        new(NodeKind.Effect, id, Run(() => NewEffect(id, reads)));

    private Effect NewEffect(string id, IReadOnlyList<NodeRef> reads)
    {
        var deps = reads.ToArray();
        return new Effect(_ctx, c =>
        {
            _log.Run(id);
            foreach (var d in deps) TrackRead(d, c);
            return () => _log.Cleanup(id);
        });
    }

    /// <inheritdoc/>
    public NodeRef FeedEffect(string id, IReadOnlyList<NodeRef> reads, NodeRef target)
    {
        _merges.Declare(target.Id);
        var deps = reads.ToArray();
        var cell = (Source<long>)target.Handle;
        return new NodeRef(NodeKind.Effect, id, Run(() => new Effect(_ctx, c =>
        {
            _log.Run(id);
            long acc = 0;
            foreach (var d in deps) acc += TrackRead(d, c);
            // The write is an argument, not a dependency: it goes through the UNTRACKED surface.
            cell.Merge(acc);
            return () => _log.Cleanup(id);
        })));
    }

    /// <inheritdoc/>
    public NodeRef SelfWritingEffect(string id, NodeRef own)
    {
        var cell = (Source<long>)own.Handle;
        return new NodeRef(NodeKind.Effect, id, Run(() =>
        {
            // Lowering the budget keeps the exhausting loop fast; the loop itself is a
            // scheduler-closed cycle, not a graph cycle, so no acyclicity check can catch it.
            _ctx.DrainBudget = 256;
            return new Effect(_ctx, c =>
            {
                _log.Run(id);
                var v = cell.Get(c);
                // Zero is a fixed point, so creation reads 0, writes 0, and the store guard
                // skips the cascade — the loop is not kicked until the external write lands.
                cell.Set(v == 0 ? 0 : unchecked(v + 1));
                return () => _log.Cleanup(id);
            });
        }));
    }

    // --- reads / writes -----------------------------------------------------

    /// <inheritdoc/>
    public (bool Ok, long Value) Read(NodeRef node) => Run(() =>
    {
        try
        {
            return node.Kind switch
            {
                NodeKind.Cell => (true, ((Source<long>)node.Handle).Get()),
                NodeKind.Computed or NodeKind.Signal => (true, ((Computed<long>)node.Handle).Get()),
                _ => throw new InvalidOperationException($"cannot read effect {node.Id}"),
            };
        }
        catch (DisposedNodeException)
        {
            return (false, 0L);
        }
    });

    /// <inheritdoc/>
    public void SetCell(NodeRef node, long value) => Run(() => ((Source<long>)node.Handle).Set(value));

    /// <inheritdoc/>
    public void MergeInto(NodeRef node, long value) => Run(() => ((Source<long>)node.Handle).Merge(value));

    /// <inheritdoc/>
    public void DisposeNode(NodeRef node) => Run(() =>
    {
        switch (node.Handle)
        {
            case Source<long> s: s.Dispose(); break;
            case Computed<long> c: c.Dispose(); break;
            case Effect e: e.Dispose(); break;
            default: throw new InvalidOperationException($"unknown node handle {node.Handle.GetType()}");
        }
    });

    /// <inheritdoc/>
    public void DisposeSignal(NodeRef node) => Run(() => ((Computed<long>)node.Handle).Lazy());

    /// <inheritdoc/>
    public int DependentCount(NodeRef node) => Run(() => _ctx.DependentCount((ReactiveNode)node.Handle));

    /// <inheritdoc/>
    public int DependencyCount(NodeRef node) => Run(() => _ctx.DependencyCount((ReactiveNode)node.Handle));

    /// <inheritdoc/>
    public bool IsEffectActive(NodeRef node) => Run(() => ((Effect)node.Handle).IsActive);

    /// <inheritdoc/>
    public virtual void Batch(Action writes) => _ctx.Batch(writes);

    /// <inheritdoc/>
    public IScopeModel Scope() => new SyncScopeModel(this, Run(() => _ctx.Scope()));

    private sealed class SyncScopeModel(SyncGraphModel model, TeardownScope scope) : IScopeModel
    {
        public int Owned => model.Run(() => scope.Count);

        public void Disarm() => model.Run(scope.Disarm);

        public void CloseScope() => model.Run(scope.Close);

        public NodeRef Cell(string id, long value) => Own(model.Cell(id, value));

        public NodeRef MergeCell(string id, long value) => Own(model.MergeCell(id, value));

        public NodeRef Computed(string id, IReadOnlyList<NodeRef> reads, long offset) =>
            Own(model.Computed(id, reads, offset));

        public NodeRef Effect(string id, IReadOnlyList<NodeRef> reads) => Own(model.Effect(id, reads));

        private NodeRef Own(NodeRef n)
        {
            model.Run(() => scope.Own((ReactiveNode)n.Handle));
            return n;
        }
    }
}

/// <summary>
/// The corpus replayed against <see cref="ThreadSafeContext"/> — the same kernel, every entry
/// point serialized behind the lock-backed context's public surface.
/// </summary>
/// <remarks>
/// Replaying the whole corpus here is what pins the refinement claim: a single-writer section
/// through the lock must be observationally identical to the plain kernel, fixture for fixture
/// and assertion for assertion. It is a weaker test than a racing one — see
/// <c>ThreadSafeContextTests</c> for the concurrent-writer coalescing property — but it is the
/// one that would catch a lock-backed surface that quietly changed a semantic.
/// </remarks>
public sealed class ThreadSafeGraphModel : SyncGraphModel
{
    private readonly ThreadSafeContext _ts;

    private ThreadSafeGraphModel(ThreadSafeContext ts)
        : base(ts.Inner) => _ts = ts;

    /// <summary>Creates a model over a fresh lock-backed context.</summary>
    public ThreadSafeGraphModel()
        : this(new ThreadSafeContext()) { }

    /// <inheritdoc/>
    public override string Name => "ThreadSafeContext";

    /// <inheritdoc/>
    protected override void Run(Action fn) => _ts.WithLock(_ => fn());

    /// <inheritdoc/>
    protected override TResult Run<TResult>(Func<TResult> fn) => _ts.WithLock(_ => fn());

    /// <inheritdoc/>
    public override void Batch(Action writes) => _ts.Batch(writes);
}
