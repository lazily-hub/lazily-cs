namespace Lazily;

/// <summary>A per-peer maximum-stamp frontier used by CRDT anti-entropy.</summary>
public sealed class StampFrontier
{
    private readonly SortedDictionary<ulong, WireStamp> _stamps = [];

    /// <summary>The number of observed peers.</summary>
    public int Count => _stamps.Count;

    /// <summary>True when no peer has been observed.</summary>
    public bool IsEmpty => _stamps.Count == 0;

    /// <summary>Records a peer's highest observed stamp.</summary>
    public bool Observe(ulong peer, WireStamp stamp)
    {
        Guard.NotNull(stamp, nameof(stamp));
        if (_stamps.TryGetValue(peer, out var current) && current.CompareTo(stamp) >= 0)
        {
            return false;
        }

        _stamps[peer] = stamp;
        return true;
    }

    /// <summary>Returns a peer's highest observed stamp.</summary>
    public WireStamp? Get(ulong peer) => _stamps.GetValueOrDefault(peer);

    /// <summary>Merges another frontier by taking the per-peer maximum.</summary>
    public bool Merge(IEnumerable<StampFrontierEntry> entries)
    {
        Guard.NotNull(entries, nameof(entries));
        var changed = false;
        foreach (var entry in entries)
        {
            changed |= Observe(entry.Peer, entry.Stamp);
        }

        return changed;
    }

    /// <summary>Returns whether this frontier dominates every entry in another frontier.</summary>
    public bool Dominates(IEnumerable<StampFrontierEntry> other)
    {
        Guard.NotNull(other, nameof(other));
        return other.All(
        entry =>
        _stamps.TryGetValue(entry.Peer, out var current)
        && current.CompareTo(entry.Stamp) >= 0);
    }

    /// <summary>Returns the minimum observed stamp across all expected members.</summary>
    public WireStamp? StabilityFrontier(IEnumerable<ulong> membership)
    {
        Guard.NotNull(membership, nameof(membership));
        WireStamp? minimum = null;
        var any = false;
        foreach (var peer in membership)
        {
            any = true;
            if (!_stamps.TryGetValue(peer, out var stamp)) return null;
            if (minimum is null || stamp.CompareTo(minimum) < 0) minimum = stamp;
        }

        return any ? minimum : null;
    }

    /// <summary>Returns the canonical peer-ordered wire form.</summary>
    public IReadOnlyList<StampFrontierEntry> ToEntries() =>
    _stamps.Select(entry => new StampFrontierEntry(entry.Key, entry.Value)).ToArray();
}

/// <summary>The winning CRDT state at one logical address.</summary>
public sealed record ConvergedCrdtEntry(
ulong Node,
string? Key,
WireStamp Stamp,
IpcValue State);

/// <summary>
/// Live distributed CRDT-plane runtime: a stamped op log, per-peer frontier, anti-entropy replies,
/// and an optional registry of reactive replicated root cells.
/// </summary>
public sealed class CrdtPlaneRuntime
{
    private readonly ulong _peer;
    private readonly WireHlc _clock;
    private readonly SortedSet<ulong> _membership;
    private readonly StampFrontier _frontier = new();
    private readonly SortedDictionary<WireStamp, CrdtOp> _ops = [];
    private readonly Dictionary<CrdtAddress, CrdtOp> _winners = [];
    private readonly Dictionary<ulong, IRegisteredPlaneCell> _cells = [];
    private readonly Dictionary<string, ulong> _keyIndex = new(StringComparer.Ordinal);

    /// <summary>Creates a runtime for one local peer.</summary>
    public CrdtPlaneRuntime(ulong peer)
    {
        _peer = peer;
        _clock = new WireHlc(peer);
        _membership = [peer];
    }

    /// <summary>The local replica identity.</summary>
    public ulong Peer => _peer;

    /// <summary>The number of registered reactive root cells.</summary>
    public int RegisteredCount => _cells.Count;

    /// <summary>The number of unique stamped operations retained for anti-entropy.</summary>
    public int OperationCount => _ops.Count;

    /// <summary>The expected session membership, including the local peer.</summary>
    public IReadOnlyList<ulong> Membership => _membership.ToArray();

    /// <summary>The local per-peer frontier.</summary>
    public StampFrontier Frontier => _frontier;

    /// <summary>The causal-stability watermark, withheld until every expected member was observed.</summary>
    public WireStamp? StabilityFrontier => _frontier.StabilityFrontier(_membership);

    /// <summary>Declares another expected session member.</summary>
    public void AddPeer(ulong peer) => _membership.Add(peer);

    /// <summary>True when a stamp is at or below the cross-membership stability watermark.</summary>
    public bool IsCollectable(WireStamp stamp)
    {
        Guard.NotNull(stamp, nameof(stamp));
        return StabilityFrontier is { } frontier && stamp.CompareTo(frontier) <= 0;
    }

    /// <summary>
    /// Registers a typed replicated cell. The supplied codec is the production state boundary used for
    /// both local broadcast and remote merge.
    /// </summary>
    public void Register<TCrdt, TValue>(
    ulong node,
    string? key,
    ReplicatedCell<TCrdt, TValue> cell,
    Func<TCrdt, byte[]> encode,
    Func<ReadOnlyMemory<byte>, TCrdt> decode)
    where TCrdt : ICellCrdt<TCrdt, TValue>
    {
        Guard.NotNull(cell, nameof(cell));
        Guard.NotNull(encode, nameof(encode));
        Guard.NotNull(decode, nameof(decode));
        if (key is not null) _keyIndex[key] = node;
        _cells[node] = new RegisteredPlaneCell<TCrdt, TValue>(cell, encode, decode);
    }

    /// <summary>
    /// Applies a local typed mutation, records the converged state, and returns the op to broadcast.
    /// A value-preserving mutation produces no graph invalidation and no wire op.
    /// </summary>
    public CrdtOp? LocalUpdate<TCrdt, TValue>(
    ulong node,
    ulong nowMicros,
    Action<TCrdt, HlcStamp> mutate)
    where TCrdt : ICellCrdt<TCrdt, TValue>
    {
        Guard.NotNull(mutate, nameof(mutate));
        if (!_cells.TryGetValue(node, out var erased)
        || erased is not RegisteredPlaneCell<TCrdt, TValue> cell)
        {
            return null;
        }

        var stamp = _clock.Send(nowMicros);
        var runtimeStamp = stamp.ToRuntime();
        if (!cell.Update(crdt => mutate(crdt, runtimeStamp))) return null;

        var key = _keyIndex
        .Where(entry => entry.Value == node)
        .Select(entry => entry.Key)
        .FirstOrDefault();
        var operation = new CrdtOp(
        node,
        key,
        stamp,
        new IpcValue.Inline(cell.Encode()));
        Record(operation);
        return operation;
    }

    /// <summary>
    /// Ingests an anti-entropy frame in stamp order. Duplicate stamps are ignored, remote reactive
    /// roots are merged through their registered codec, and the frontier remains resumable.
    /// </summary>
    public int Ingest(CrdtSyncMessage frame, ulong nowMicros = 0)
    {
        Guard.NotNull(frame, nameof(frame));
        if (frame.Frontier is not null)
        {
            foreach (var entry in frame.Frontier)
            {
                _membership.Add(entry.Peer);
                _frontier.Observe(entry.Peer, entry.Stamp);
                _clock.Receive(entry.Stamp, nowMicros);
            }
        }

        var applied = 0;
        foreach (var operation in frame.Ops.OrderBy(operation => operation.Stamp))
        {
            if (_ops.ContainsKey(operation.Stamp)) continue;
            _clock.Receive(operation.Stamp, nowMicros);
            _membership.Add(operation.Stamp.Peer);
            Record(operation);
            MergeRegisteredCell(operation);
            applied++;
        }

        return applied;
    }

    /// <summary>The converged maximum-stamp state at every logical address.</summary>
    public IReadOnlyList<ConvergedCrdtEntry> Converged() =>
    _winners.Values
    .OrderBy(operation => operation.Node)
    .ThenBy(operation => operation.Key, StringComparer.Ordinal)
    .Select(
    operation =>
    new ConvergedCrdtEntry(
    operation.Node,
    operation.Key,
    operation.Stamp,
    CloneValue(operation.State)))
    .ToArray();

    /// <summary>Builds a full anti-entropy frame for a cold peer.</summary>
    public CrdtSyncMessage SyncFrame(bool includeFrontier = true) =>
    SyncFrameSince([], includeFrontier);

    /// <summary>Builds a frame containing only operations missing from a peer's frontier.</summary>
    public CrdtSyncMessage SyncFrameSince(
    IEnumerable<StampFrontierEntry> since,
    bool includeFrontier = true)
    {
        Guard.NotNull(since, nameof(since));
        var peerFrontier = new StampFrontier();
        peerFrontier.Merge(since);
        var operations = _ops
        .Where(
        entry =>
        peerFrontier.Get(entry.Key.Peer) is not { } seen
        || entry.Key.CompareTo(seen) > 0)
        .Select(entry => CloneOperation(entry.Value))
        .ToArray();
        return new CrdtSyncMessage(
        operations,
        includeFrontier ? _frontier.ToEntries() : null);
    }

    /// <summary>Answers a peer pull request from its advertised frontier.</summary>
    public CrdtSyncMessage SyncReply(CrdtSyncMessage request)
    {
        Guard.NotNull(request, nameof(request));
        return SyncFrameSince(request.Frontier ?? []);
    }

    private void Record(CrdtOp operation)
    {
        var cloned = CloneOperation(operation);
        _ops[cloned.Stamp] = cloned;
        _frontier.Observe(cloned.Stamp.Peer, cloned.Stamp);
        var address = CrdtAddress.From(cloned);
        if (!_winners.TryGetValue(address, out var winner)
|| cloned.Stamp.CompareTo(winner.Stamp) > 0)
        {
            _winners[address] = cloned;
        }
    }

    private void MergeRegisteredCell(CrdtOp operation)
    {
        var node = operation.Key is not null && _keyIndex.TryGetValue(operation.Key, out var keyedNode)
        ? keyedNode
        : operation.Node;
        if (!_cells.TryGetValue(node, out var cell)
        || operation.State is not IpcValue.Inline inline)
        {
            return;
        }

        cell.Merge(inline.Bytes);
    }

    private static CrdtOp CloneOperation(CrdtOp operation) =>
    new(operation.Node, operation.Key, operation.Stamp, CloneValue(operation.State));

    private static IpcValue CloneValue(IpcValue value) =>
    value switch
    {
        IpcValue.Inline inline => new IpcValue.Inline([.. inline.Bytes]),
        IpcValue.SharedBlob shared => new IpcValue.SharedBlob(shared.Blob),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private readonly record struct CrdtAddress(ulong Node, string? Key)
    {
        public static CrdtAddress From(CrdtOp operation) =>
        operation.Key is null
        ? new CrdtAddress(operation.Node, null)
        : new CrdtAddress(0, operation.Key);
    }

    private interface IRegisteredPlaneCell
    {
        bool Merge(ReadOnlyMemory<byte> state);
    }

    private sealed class RegisteredPlaneCell<TCrdt, TValue>(
    ReplicatedCell<TCrdt, TValue> cell,
    Func<TCrdt, byte[]> encode,
    Func<ReadOnlyMemory<byte>, TCrdt> decode)
    : IRegisteredPlaneCell
    where TCrdt : ICellCrdt<TCrdt, TValue>
    {
        public byte[] Encode() => encode(cell.Crdt);

        public bool Update(Action<TCrdt> mutate) => cell.Update(mutate);

        public bool Merge(ReadOnlyMemory<byte> state) => cell.MergeRemote(decode(state));
    }

    private sealed class WireHlc(ulong peer)
    {
        private ulong _wallTime;
        private ulong _logical;

        public WireStamp Send(ulong nowMicros)
        {
            if (nowMicros > _wallTime)
            {
                _wallTime = nowMicros;
                _logical = 0;
            }
            else
            {
                _logical = checked(_logical + 1);
            }

            return new WireStamp(_wallTime, _logical, peer);
        }

        public WireStamp Receive(WireStamp remote, ulong nowMicros)
        {
            var wall = Math.Max(Math.Max(_wallTime, remote.WallTime), nowMicros);
            if (wall == _wallTime && wall == remote.WallTime)
            {
                _logical = checked(Math.Max(_logical, remote.Logical) + 1);
            }
            else if (wall == _wallTime)
            {
                _logical = checked(_logical + 1);
            }
            else if (wall == remote.WallTime)
            {
                _logical = checked(remote.Logical + 1);
            }
            else
            {
                _logical = 0;
            }

            _wallTime = wall;
            return new WireStamp(_wallTime, _logical, peer);
        }
    }
}
