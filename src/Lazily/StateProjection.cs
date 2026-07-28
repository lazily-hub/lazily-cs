namespace Lazily;

/// <summary>The result of folding a state-plane frame into a projection.</summary>
public abstract record StateProjectionApplyStatus
{
    private StateProjectionApplyStatus()
    {
    }

    /// <summary>The frame was applied atomically.</summary>
    public sealed record Applied(ulong Epoch) : StateProjectionApplyStatus;

    /// <summary>The frame was already covered by the local projection.</summary>
    public sealed record Ignored(ulong CurrentEpoch, ulong FrameEpoch) : StateProjectionApplyStatus;

    /// <summary>The delta did not begin at the local projection's current epoch.</summary>
    public sealed record Gap(ulong ExpectedBaseEpoch, ulong ActualBaseEpoch) : StateProjectionApplyStatus;

    /// <summary>The frame was internally inconsistent and left the projection unchanged.</summary>
    public sealed record Invalid(string Reason) : StateProjectionApplyStatus;
}

/// <summary>A node held by the value-mirror state projection.</summary>
public sealed record ProjectedNode(
    ulong Node,
    string TypeTag,
    NodeState State,
    string? Key,
    bool Dirty);

/// <summary>
/// A receiver-side value mirror for <see cref="SnapshotMessage"/> and <see cref="DeltaMessage"/>.
/// Delta batches are validated against a copy and committed only as a whole.
/// </summary>
public sealed class StateProjection
{
    private Dictionary<ulong, ProjectedNode> _nodes = [];
    private HashSet<EdgeSnapshot> _edges = [];
    private HashSet<ulong> _roots = [];

    /// <summary>The most recent fully applied epoch.</summary>
    public ulong LastEpoch { get; private set; }

    /// <summary>Nodes sorted by wire id.</summary>
    public IReadOnlyList<ProjectedNode> Nodes =>
        [.. _nodes.Values.OrderBy(node => node.Node)];

    /// <summary>Edges sorted by dependent and dependency.</summary>
    public IReadOnlyList<EdgeSnapshot> Edges =>
        [.. _edges.OrderBy(edge => edge.Dependent).ThenBy(edge => edge.Dependency)];

    /// <summary>Root node ids sorted ascending.</summary>
    public IReadOnlyList<ulong> Roots => [.. _roots.OrderBy(node => node)];

    /// <summary>Looks up one projected node.</summary>
    public bool TryGetNode(ulong node, out ProjectedNode value) =>
        _nodes.TryGetValue(node, out value!);

    /// <summary>Folds either state-plane frame.</summary>
    public StateProjectionApplyStatus Apply(IpcMessage message) =>
        message switch
        {
            SnapshotMessage snapshot => ApplySnapshot(snapshot),
            DeltaMessage delta => ApplyDelta(delta),
            _ => new StateProjectionApplyStatus.Invalid(
                $"{message.GetType().Name} is not a Snapshot or Delta."),
        };

    /// <summary>Replaces the full projection with a non-stale snapshot.</summary>
    public StateProjectionApplyStatus ApplySnapshot(SnapshotMessage snapshot)
    {
        Guard.NotNull(snapshot, nameof(snapshot));
        if (snapshot.Epoch < LastEpoch)
        {
            LazilyMetrics.StateProjectionIgnored();
            return new StateProjectionApplyStatus.Ignored(LastEpoch, snapshot.Epoch);
        }

        var nodes = new Dictionary<ulong, ProjectedNode>();
        foreach (var node in snapshot.Nodes)
        {
            if (!nodes.TryAdd(
                    node.Node,
                    new ProjectedNode(
                        node.Node,
                        node.TypeTag,
                        CloneState(node.State),
                        node.Key,
                        Dirty: false)))
            {
                return Invalid($"Snapshot contains duplicate node {node.Node}.");
            }
        }

        var roots = snapshot.Roots.ToHashSet();
        if (roots.Any(root => !nodes.ContainsKey(root)))
        {
            return Invalid("Snapshot contains a root that is not present in nodes.");
        }

        var edges = new HashSet<EdgeSnapshot>();
        foreach (var edge in snapshot.Edges)
        {
            if (!nodes.ContainsKey(edge.Dependent) || !nodes.ContainsKey(edge.Dependency))
            {
                return Invalid(
                    $"Snapshot edge {edge.Dependent}->{edge.Dependency} references an unknown node.");
            }

            edges.Add(edge);
        }

        _nodes = nodes;
        _edges = edges;
        _roots = roots;
        LastEpoch = snapshot.Epoch;
        LazilyMetrics.StateProjectionApplied(snapshot.Nodes.Count, snapshot.Edges.Count);
        return new StateProjectionApplyStatus.Applied(LastEpoch);
    }

    /// <summary>
    /// Applies an ordered delta when its base is contiguous. Invalid operations roll the entire
    /// batch back and leave <see cref="LastEpoch"/> unchanged.
    /// </summary>
    public StateProjectionApplyStatus ApplyDelta(DeltaMessage delta)
    {
        Guard.NotNull(delta, nameof(delta));
        if (delta.BaseEpoch < LastEpoch)
        {
            LazilyMetrics.StateProjectionIgnored();
            return new StateProjectionApplyStatus.Ignored(LastEpoch, delta.Epoch);
        }

        if (delta.BaseEpoch > LastEpoch)
        {
            LazilyMetrics.StateProjectionGap();
            return new StateProjectionApplyStatus.Gap(LastEpoch, delta.BaseEpoch);
        }

        if (delta.Epoch <= delta.BaseEpoch)
        {
            return Invalid(
                $"Delta epoch {delta.Epoch} must be greater than base {delta.BaseEpoch}.");
        }

        var nodes = new Dictionary<ulong, ProjectedNode>(_nodes);
        var edges = new HashSet<EdgeSnapshot>(_edges);
        var roots = new HashSet<ulong>(_roots);

        foreach (var operation in delta.Ops)
        {
            var error = ApplyOperation(operation, nodes, edges, roots);
            if (error is not null) return Invalid(error);
        }

        _nodes = nodes;
        _edges = edges;
        _roots = roots;
        LastEpoch = delta.Epoch;
        LazilyMetrics.StateProjectionApplied(delta.Ops.Count, 0);
        return new StateProjectionApplyStatus.Applied(LastEpoch);
    }

    /// <summary>Returns a defensively owned snapshot of the current projection.</summary>
    public SnapshotMessage ToSnapshot() => new(
        LastEpoch,
        Nodes.Select(node => new NodeSnapshot(
                node.Node,
                node.TypeTag,
                CloneState(node.State),
                node.Key))
            .ToArray(),
        Edges.ToArray(),
        Roots.ToArray());

    private static string? ApplyOperation(
        DeltaOp operation,
        Dictionary<ulong, ProjectedNode> nodes,
        HashSet<EdgeSnapshot> edges,
        HashSet<ulong> roots)
    {
        switch (operation)
        {
            case DeltaOp.CellSet set:
                return SetValue(nodes, set.Node, set.Payload, Dirty: false);
            case DeltaOp.SlotValue value:
                return SetValue(nodes, value.Node, value.Payload, Dirty: false);
            case DeltaOp.Invalidate invalidation:
                if (!nodes.TryGetValue(invalidation.Node, out var invalidated))
                {
                    return $"Invalidate references unknown node {invalidation.Node}.";
                }

                nodes[invalidation.Node] = invalidated with { Dirty = true };
                return null;
            case DeltaOp.NodeAdd addition:
                if (nodes.ContainsKey(addition.Node))
                {
                    return $"NodeAdd duplicates node {addition.Node}.";
                }

                nodes.Add(
                    addition.Node,
                    new ProjectedNode(
                        addition.Node,
                        addition.TypeTag,
                        CloneState(addition.State),
                        addition.Key,
                        Dirty: false));
                return null;
            case DeltaOp.NodeRemove removal:
                if (!nodes.Remove(removal.Node))
                {
                    return $"NodeRemove references unknown node {removal.Node}.";
                }

                roots.Remove(removal.Node);
                edges.RemoveWhere(edge =>
                    edge.Dependent == removal.Node || edge.Dependency == removal.Node);
                return null;
            case DeltaOp.EdgeAdd addition:
                if (!nodes.ContainsKey(addition.Dependent)
                    || !nodes.ContainsKey(addition.Dependency))
                {
                    return
                        $"EdgeAdd {addition.Dependent}->{addition.Dependency} references an unknown node.";
                }

                edges.Add(new EdgeSnapshot(addition.Dependent, addition.Dependency));
                return null;
            case DeltaOp.EdgeRemove removal:
                edges.Remove(new EdgeSnapshot(removal.Dependent, removal.Dependency));
                return null;
            case DeltaOp.QueuePush:
            case DeltaOp.QueuePop:
            case DeltaOp.QueueClose:
                return
                    $"{operation.GetType().Name} requires a queue projection adapter; " +
                    "the graph-state projection cannot apply it.";
            default:
                return $"Unsupported delta operation {operation.GetType().Name}.";
        }
    }

    private static string? SetValue(
        Dictionary<ulong, ProjectedNode> nodes,
        ulong node,
        IpcValue payload,
        bool Dirty)
    {
        if (!nodes.TryGetValue(node, out var existing))
        {
            return $"Value operation references unknown node {node}.";
        }

        nodes[node] = existing with
        {
            State = payload switch
            {
                IpcValue.Inline inline => new NodeState.Payload([.. inline.Bytes]),
                IpcValue.SharedBlob shared => new NodeState.SharedBlob(shared.Blob),
                _ => throw new ArgumentOutOfRangeException(nameof(payload)),
            },
            Dirty = Dirty,
        };
        return null;
    }

    private static NodeState CloneState(NodeState state) =>
        state switch
        {
            NodeState.Payload payload => new NodeState.Payload([.. payload.Bytes]),
            NodeState.SharedBlob shared => new NodeState.SharedBlob(shared.Blob),
            NodeState.Opaque => new NodeState.Opaque(),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static StateProjectionApplyStatus Invalid(string reason)
    {
        LazilyMetrics.StateProjectionInvalid();
        return new StateProjectionApplyStatus.Invalid(reason);
    }
}

/// <summary>
/// Producer-side value mirror. Dirty nodes and resolved values are coalesced into one sorted delta
/// per flush; an empty flush emits no frame and does not advance the epoch.
/// </summary>
public sealed class StateProjectionMirror
{
    private readonly HashSet<ulong> _dirty = [];
    private readonly Dictionary<ulong, IpcValue> _resolved = [];

    /// <summary>The epoch from which the next non-empty flush starts.</summary>
    public ulong BaseEpoch { get; private set; }

    /// <summary>Dirty node ids sorted ascending.</summary>
    public IReadOnlyList<ulong> DirtyNodes => [.. _dirty.OrderBy(node => node)];

    /// <summary>Marks a slot dirty.</summary>
    public void MarkDirty(ulong node) => _dirty.Add(node);

    /// <summary>Publishes the resolved value for a dirty slot.</summary>
    public void Resolve(ulong node, IpcValue value)
    {
        Guard.NotNull(value, nameof(value));
        _resolved[node] = value;
        _dirty.Remove(node);
    }

    /// <summary>Builds one coalesced flush delta, or null when there is no accepted change.</summary>
    public DeltaMessage? Flush()
    {
        if (_dirty.Count == 0 && _resolved.Count == 0) return null;

        var operations = new List<DeltaOp>(_dirty.Count + _resolved.Count);
        operations.AddRange(
            _dirty.OrderBy(node => node).Select(node => new DeltaOp.Invalidate(node)));
        operations.AddRange(
            _resolved.OrderBy(pair => pair.Key)
                .Select(pair => new DeltaOp.SlotValue(pair.Key, pair.Value)));

        var delta = new DeltaMessage(BaseEpoch, checked(BaseEpoch + 1), operations);
        BaseEpoch = delta.Epoch;
        _dirty.Clear();
        _resolved.Clear();
        LazilyMetrics.StateMirrorFlushed(operations.Count);
        return delta;
    }
}
