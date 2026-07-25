namespace Lazily.Tests;

/// <summary>
/// The corpus replayed against <see cref="AsyncContext"/> — the plane where an invalidation
/// cascade that stops one level below the write is invisible to every synchronous test.
/// </summary>
/// <remarks>
/// A slot read short-circuits on <see cref="AsyncSlotState.Resolved"/>, so a downstream slot left
/// resolved by a one-level cascade serves its cached value forever and no pull chain rescues it.
/// The synchronous kernel cannot exhibit that failure — it recomputes on demand — which is why
/// replaying <c>transitive_invalidation_reaches_depth</c> here, and not only on
/// <see cref="Context"/>, is the point of parameterising the runner at all.
/// <para>
/// Ops this plane does not ship (<c>signal</c>, <c>merge_cell</c>, the bounded drain) throw with
/// a pointer to the per-model ledger rather than degrading to something plausible. Degrading is
/// the worst option available: a lazy slot standing in for an eager signal produces
/// plausible-looking numbers that satisfy two of the three assertions a signal fixture makes.
/// </para>
/// </remarks>
public sealed class AsyncGraphModel : IGraphModel
{
    private const string GateHint =
        "this fixture must be gated in ReactiveGraphConformanceTests.ModelUnsupported";

    private readonly AsyncContext _ctx = new();
    private readonly EffectLog _log = new();
    private readonly CountLog _computes = new();

    /// <inheritdoc/>
    public string Name => "AsyncContext";

    /// <inheritdoc/>
    public void Dispose()
    {
        _ctx.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public void Settle() => _ctx.Settle();

    /// <inheritdoc/>
    public IReadOnlyList<string> RunLog => _log.Runs;

    /// <inheritdoc/>
    public IReadOnlyList<string> CleanupLog => _log.Cleanups;

    /// <inheritdoc/>
    public int ComputesOf(string id) => _computes.Count(id);

    /// <inheritdoc/>
    public int MergesOf(string id) =>
        throw new NotSupportedException($"AsyncContext ships no merge policy — {GateHint}");

    /// <inheritdoc/>
    public bool KnowsMerge(string id) => false;

    /// <inheritdoc/>
    public void ClearDrainExhaustion() { }

    /// <inheritdoc/>
    public bool DrainExhausted =>
        throw new NotSupportedException($"AsyncContext ships no bounded effect drain — {GateHint}");

    // --- construction -------------------------------------------------------

    /// <inheritdoc/>
    public NodeRef Cell(string id, long value) => new(NodeKind.Cell, id, _ctx.Source(value));

    /// <inheritdoc/>
    public NodeRef MergeCell(string id, long value) =>
        throw new NotSupportedException($"AsyncContext ships no merge policy — {GateHint}");

    /// <inheritdoc/>
    public NodeRef Signal(string id, IReadOnlyList<NodeRef> reads, long offset) =>
        throw new NotSupportedException($"AsyncContext ships no eager slot constructor — {GateHint}");

    /// <inheritdoc/>
    public void DisposeSignal(NodeRef node) =>
        throw new NotSupportedException($"AsyncContext ships no eager slot constructor — {GateHint}");

    /// <inheritdoc/>
    public NodeRef FeedEffect(string id, IReadOnlyList<NodeRef> reads, NodeRef target) =>
        throw new NotSupportedException($"AsyncContext ships no merge policy — {GateHint}");

    /// <inheritdoc/>
    public NodeRef SelfWritingEffect(string id, NodeRef own) =>
        throw new NotSupportedException($"AsyncContext ships no bounded effect drain — {GateHint}");

    /// <inheritdoc/>
    public NodeRef Computed(string id, IReadOnlyList<NodeRef> reads, long offset)
    {
        _computes.Declare(id);
        var deps = reads.ToArray();
        var node = _ctx.Computed<long>(async cc =>
        {
            _computes.Tick(id);
            var sum = offset;
            foreach (var d in deps) sum += await TrackRead(d, cc).ConfigureAwait(false);
            return sum;
        });
        return new NodeRef(NodeKind.Computed, id, node);
    }

    /// <inheritdoc/>
    public NodeRef Effect(string id, IReadOnlyList<NodeRef> reads)
    {
        var deps = reads.ToArray();
        var handle = _ctx.Effect(async cc =>
        {
            _log.Run(id);
            foreach (var d in deps)
            {
                try
                {
                    await TrackRead(d, cc).ConfigureAwait(false);
                }
                catch (DisposedNodeException)
                {
                    // A read that fails because a dependency is gone is the contract, not a test
                    // failure: the effect observes the error and carries on, which is what makes
                    // the cleanup below still get returned and still run on dispose.
                }
            }
            return () =>
            {
                _log.Cleanup(id);
                return Task.CompletedTask;
            };
        });
        return new NodeRef(NodeKind.Effect, id, handle);
    }

    private static Task<long> TrackRead(NodeRef n, AsyncCompute cc) => n.Kind switch
    {
        NodeKind.Cell => Task.FromResult(cc.Track((AsyncSource<long>)n.Handle)),
        NodeKind.Computed => cc.TrackAsync((AsyncComputed<long>)n.Handle),
        _ => throw new InvalidOperationException($"cannot read node kind {n.Kind}"),
    };

    // --- reads / writes -----------------------------------------------------

    /// <inheritdoc/>
    public (bool Ok, long Value) Read(NodeRef node)
    {
        try
        {
            return node.Kind switch
            {
                NodeKind.Cell => (true, ((AsyncSource<long>)node.Handle).Peek()),
                NodeKind.Computed => (true, ((AsyncComputed<long>)node.Handle)
                    .GetAsync().GetAwaiter().GetResult()),
                _ => throw new InvalidOperationException($"cannot read effect {node.Id}"),
            };
        }
        catch (DisposedNodeException)
        {
            return (false, 0L);
        }
    }

    /// <inheritdoc/>
    public void SetCell(NodeRef node, long value) => ((AsyncSource<long>)node.Handle).Set(value);

    /// <inheritdoc/>
    public void MergeInto(NodeRef node, long value) =>
        throw new NotSupportedException($"AsyncContext ships no merge policy — {GateHint}");

    /// <inheritdoc/>
    public void DisposeNode(NodeRef node)
    {
        switch (node.Handle)
        {
            case AsyncSource<long> s: s.Dispose(); break;
            case AsyncComputed<long> c: c.Dispose(); break;
            case AsyncEffectHandle e: e.DisposeAsync().AsTask().GetAwaiter().GetResult(); break;
            default: throw new InvalidOperationException($"unknown node handle {node.Handle.GetType()}");
        }
    }

    /// <inheritdoc/>
    public int DependentCount(NodeRef node) => _ctx.DependentCount(GraphNode(node));

    /// <inheritdoc/>
    public int DependencyCount(NodeRef node) => _ctx.DependencyCount(GraphNode(node));

    /// <inheritdoc/>
    public bool IsEffectActive(NodeRef node) => ((AsyncEffectHandle)node.Handle).IsActive;

    private static AsyncNode GraphNode(NodeRef node) => node.Handle switch
    {
        AsyncSource<long> s => s.GraphNode,
        AsyncComputed<long> c => c.GraphNode,
        AsyncEffectHandle e => e.GraphNode,
        _ => throw new InvalidOperationException($"unknown node handle {node.Handle.GetType()}"),
    };

    /// <inheritdoc/>
    public void Batch(Action writes) => _ctx.Batch(writes);

    /// <inheritdoc/>
    public IScopeModel Scope() => new AsyncScopeModel(this, _ctx.Scope());

    private sealed class AsyncScopeModel(AsyncGraphModel model, AsyncTeardownScope scope) : IScopeModel
    {
        public int Owned => scope.Count;

        public void Disarm() => scope.Disarm();

        public void CloseScope() => scope.CloseAsync().AsTask().GetAwaiter().GetResult();

        public NodeRef Cell(string id, long value)
        {
            var n = model.Cell(id, value);
            scope.Own((AsyncSource<long>)n.Handle);
            return n;
        }

        public NodeRef MergeCell(string id, long value) => model.MergeCell(id, value);

        public NodeRef Computed(string id, IReadOnlyList<NodeRef> reads, long offset)
        {
            var n = model.Computed(id, reads, offset);
            scope.Own((AsyncComputed<long>)n.Handle);
            return n;
        }

        public NodeRef Effect(string id, IReadOnlyList<NodeRef> reads)
        {
            var n = model.Effect(id, reads);
            scope.Own((AsyncEffectHandle)n.Handle);
            return n;
        }
    }
}
