namespace Lazily;

/// <summary>The independently gated kinds of remote graph operation.</summary>
public enum OpKind
{
    /// <summary>Read or serialize node state.</summary>
    Read,

    /// <summary>Write node state.</summary>
    Write,

    /// <summary>Trigger a remote effect.</summary>
    TriggerEffect,
}

/// <summary>One operation checked at the remote permission boundary.</summary>
public readonly record struct RemoteOp(OpKind Kind, ulong Node)
{
    /// <summary>Constructs a read operation.</summary>
    public static RemoteOp Read(ulong node) => new(OpKind.Read, node);

    /// <summary>Constructs a write operation.</summary>
    public static RemoteOp Write(ulong node) => new(OpKind.Write, node);

    /// <summary>Constructs an effect-trigger operation.</summary>
    public static RemoteOp TriggerEffect(ulong node) => new(OpKind.TriggerEffect, node);
}

/// <summary>A fail-closed permission error for one peer and operation.</summary>
public sealed class PermissionDeniedException(ulong peer, RemoteOp operation)
    : InvalidOperationException(
        $"Peer {peer} is not allowed to {Format(operation.Kind)} node {operation.Node}.")
{
    /// <summary>The denied peer.</summary>
    public ulong Peer { get; } = peer;

    /// <summary>The denied operation.</summary>
    public RemoteOp Operation { get; } = operation;

    private static string Format(OpKind kind) =>
        kind switch
        {
            OpKind.Read => "read",
            OpKind.Write => "write",
            OpKind.TriggerEffect => "trigger_effect",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}

/// <summary>
/// Default-deny, per-peer allowlists. Read, write, and effect-trigger grants never imply one
/// another.
/// </summary>
public sealed class PeerPermissions
{
    private readonly Dictionary<ulong, Dictionary<OpKind, HashSet<ulong>>> _peers = [];

    /// <summary>Number of peers with at least one grant.</summary>
    public int PeerCount => _peers.Count;

    /// <summary>Adds a grant and returns whether the allowlist changed.</summary>
    public bool Allow(ulong peer, RemoteOp operation)
    {
        if (!_peers.TryGetValue(peer, out var byKind))
        {
            byKind = [];
            _peers.Add(peer, byKind);
        }

        if (!byKind.TryGetValue(operation.Kind, out var nodes))
        {
            nodes = [];
            byKind.Add(operation.Kind, nodes);
        }

        var changed = nodes.Add(operation.Node);
        if (changed) LazilyMetrics.PermissionGrantChanged();
        return changed;
    }

    /// <summary>Adds grants for several nodes.</summary>
    public void AllowMany(ulong peer, OpKind kind, IEnumerable<ulong> nodes)
    {
        Guard.NotNull(nodes, nameof(nodes));
        foreach (var node in nodes) Allow(peer, new RemoteOp(kind, node));
    }

    /// <summary>Removes one grant and returns whether the allowlist changed.</summary>
    public bool Revoke(ulong peer, RemoteOp operation)
    {
        if (!_peers.TryGetValue(peer, out var byKind)
            || !byKind.TryGetValue(operation.Kind, out var nodes)
            || !nodes.Remove(operation.Node))
        {
            return false;
        }

        if (nodes.Count == 0) byKind.Remove(operation.Kind);
        if (byKind.Count == 0) _peers.Remove(peer);
        LazilyMetrics.PermissionGrantChanged();
        return true;
    }

    /// <summary>Removes all grants for a peer.</summary>
    public bool RevokePeer(ulong peer)
    {
        var changed = _peers.Remove(peer);
        if (changed) LazilyMetrics.PermissionGrantChanged();
        return changed;
    }

    /// <summary>Returns whether the exact operation is granted.</summary>
    public bool IsAllowed(ulong peer, RemoteOp operation) =>
        _peers.TryGetValue(peer, out var byKind)
        && byKind.TryGetValue(operation.Kind, out var nodes)
        && nodes.Contains(operation.Node);

    /// <summary>Throws unless the exact operation is granted.</summary>
    public void Check(ulong peer, RemoteOp operation)
    {
        if (IsAllowed(peer, operation))
        {
            LazilyMetrics.PermissionAllowed();
            return;
        }

        LazilyMetrics.PermissionDenied();
        throw new PermissionDeniedException(peer, operation);
    }

    /// <summary>Drops unreadable node ids before serialization.</summary>
    public IReadOnlyList<ulong> FilterReadable(ulong peer, IEnumerable<ulong> nodes)
    {
        Guard.NotNull(nodes, nameof(nodes));
        return [.. nodes.Where(node => IsAllowed(peer, RemoteOp.Read(node)))];
    }

    /// <summary>
    /// Drops unreadable nodes, roots, and edges before serializing a snapshot. Nothing is emitted
    /// as an opaque placeholder.
    /// </summary>
    public SnapshotMessage FilterReadable(ulong peer, SnapshotMessage snapshot)
    {
        Guard.NotNull(snapshot, nameof(snapshot));
        var nodes = snapshot.Nodes
            .Where(node => IsAllowed(peer, RemoteOp.Read(node.Node)))
            .ToArray();
        var readable = nodes.Select(node => node.Node).ToHashSet();
        return snapshot with
        {
            Nodes = nodes,
            Edges =
            [
                .. snapshot.Edges.Where(edge =>
                    readable.Contains(edge.Dependent) && readable.Contains(edge.Dependency)),
            ],
            Roots = [.. snapshot.Roots.Where(readable.Contains)],
        };
    }

    /// <summary>Drops delta operations whose complete target is not readable.</summary>
    public DeltaMessage FilterReadable(ulong peer, DeltaMessage delta)
    {
        Guard.NotNull(delta, nameof(delta));
        return delta with
        {
            Ops = [.. delta.Ops.Where(operation => IsReadable(peer, operation))],
        };
    }

    /// <summary>Drops CRDT operations whose node is not readable.</summary>
    public CrdtSyncMessage FilterReadable(ulong peer, CrdtSyncMessage sync)
    {
        Guard.NotNull(sync, nameof(sync));
        return sync with
        {
            Ops =
            [
                .. sync.Ops.Where(operation =>
                    IsAllowed(peer, RemoteOp.Read(operation.Node))),
            ],
        };
    }

    private bool IsReadable(ulong peer, DeltaOp operation) =>
        operation switch
        {
            DeltaOp.CellSet op => IsAllowed(peer, RemoteOp.Read(op.Node)),
            DeltaOp.SlotValue op => IsAllowed(peer, RemoteOp.Read(op.Node)),
            DeltaOp.Invalidate op => IsAllowed(peer, RemoteOp.Read(op.Node)),
            DeltaOp.NodeAdd op => IsAllowed(peer, RemoteOp.Read(op.Node)),
            DeltaOp.NodeRemove op => IsAllowed(peer, RemoteOp.Read(op.Node)),
            DeltaOp.EdgeAdd op =>
                IsAllowed(peer, RemoteOp.Read(op.Dependent))
                && IsAllowed(peer, RemoteOp.Read(op.Dependency)),
            DeltaOp.EdgeRemove op =>
                IsAllowed(peer, RemoteOp.Read(op.Dependent))
                && IsAllowed(peer, RemoteOp.Read(op.Dependency)),
            DeltaOp.QueuePush op => IsAllowed(peer, RemoteOp.Read(op.Node)),
            DeltaOp.QueuePop op => IsAllowed(peer, RemoteOp.Read(op.Node)),
            DeltaOp.QueueClose op => IsAllowed(peer, RemoteOp.Read(op.Node)),
            _ => false,
        };
}
