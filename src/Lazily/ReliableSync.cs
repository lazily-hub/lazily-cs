namespace Lazily;

/// <summary>The receiver-side classification of an inbound state frame.</summary>
public enum ResyncAction
{
    /// <summary>Apply the frame and advance the receiver cursor.</summary>
    Apply,

    /// <summary>Request a covering snapshot because a gap was detected.</summary>
    RequestSnapshot,

    /// <summary>Ignore a replay, malformed frame, or duplicate gap notification.</summary>
    Ignore,
}

/// <summary>A resync action plus the receiver cursor used by a snapshot request.</summary>
public readonly record struct ResyncDecision(
ResyncAction Action,
ulong FromEpoch = 0);

/// <summary>Pure receiver-side reliable-sync state machine.</summary>
public sealed class ResyncCoordinator
{
    private bool _resyncing;

    /// <summary>Creates a fresh receiver at epoch zero.</summary>
    public ResyncCoordinator()
    {
    }

    /// <summary>Creates a receiver that has already applied through an epoch.</summary>
    public ResyncCoordinator(ulong lastEpoch)
    {
        LastEpoch = lastEpoch;
    }

    /// <summary>The highest epoch fully applied by this receiver.</summary>
    public ulong LastEpoch { get; private set; }

    /// <summary>True while one gap request is outstanding.</summary>
    public bool IsResyncing => _resyncing;

    /// <summary>Classifies and folds an inbound delta.</summary>
    public ResyncDecision Ingest(DeltaMessage delta)
    {
        Guard.NotNull(delta, nameof(delta));
        if (delta.Epoch <= delta.BaseEpoch) return new(ResyncAction.Ignore);
        if (delta.BaseEpoch < LastEpoch) return new(ResyncAction.Ignore);
        if (delta.BaseEpoch > LastEpoch)
        {
            if (_resyncing) return new(ResyncAction.Ignore);
            _resyncing = true;
            return new(ResyncAction.RequestSnapshot, LastEpoch);
        }

        LastEpoch = delta.Epoch;
        _resyncing = false;
        return new(ResyncAction.Apply);
    }

    /// <summary>Adopts a full snapshot and clears any outstanding gap.</summary>
    public ResyncDecision Ingest(SnapshotMessage snapshot)
    {
        Guard.NotNull(snapshot, nameof(snapshot));
        LastEpoch = snapshot.Epoch;
        _resyncing = false;
        return new(ResyncAction.Apply);
    }

    /// <summary>Classifies an IPC message; non-epoch planes and control frames are ignored.</summary>
    public ResyncDecision Ingest(IpcMessage message)
    {
        Guard.NotNull(message, nameof(message));
        return message switch
        {
            SnapshotMessage snapshot => Ingest(snapshot),
            DeltaMessage delta => Ingest(delta),

            // INTENTIONAL leniency. This coordinator owns exactly ONE thing: the epoch cursor on
            // the snapshot/delta plane. Every other frame on the wire — resync requests, outbox
            // acks, CRDT sync, and any frame kind a newer peer introduces — carries no epoch, so
            // "ignore" is not a guess, it is the complete and correct answer for a message with
            // nothing for this cursor to fold. A shared socket multiplexes all of those planes, so
            // throwing on an unrecognised frame would let an unrelated plane's forward-compatible
            // extension kill sync. Ignore does NOT suppress a gap: LastEpoch is untouched, so the
            // next delta still detects the hole. Pinned by `AnUnknownIpcFrameIsIgnoredNotFolded`.
            _ => new ResyncDecision(ResyncAction.Ignore),
        };
    }

    /// <summary>Builds the retention acknowledgement for this receiver cursor.</summary>
    public OutboxAckMessage Acknowledgement() => new(LastEpoch);
}

/// <summary>An add-wins observed-remove set for one liveness membership entry.</summary>
public sealed class OrSet
{
    private readonly HashSet<string> _adds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removes = new(StringComparer.Ordinal);

    /// <summary>True when at least one add tag remains unobserved by a remove.</summary>
    public bool Present => _adds.Except(_removes).Any();

    /// <summary>Adds a fresh presence tag.</summary>
    public bool Add(string tag)
    {
        Guard.NotNullOrWhiteSpace(tag, nameof(tag));
        return _adds.Add(tag);
    }

    /// <summary>Shadows exactly the add tags observed by a remove.</summary>
    public bool RemoveObserved(IEnumerable<string> tags)
    {
        Guard.NotNull(tags, nameof(tags));
        var changed = false;
        foreach (var tag in tags)
        {
            Guard.NotNullOrWhiteSpace(tag, nameof(tags));
            changed |= _removes.Add(tag);
        }

        return changed;
    }

    /// <summary>Joins another replica by unioning add and remove tags.</summary>
    public bool Join(OrSet other)
    {
        Guard.NotNull(other, nameof(other));
        var beforeAdds = _adds.Count;
        var beforeRemoves = _removes.Count;
        _adds.UnionWith(other._adds);
        _removes.UnionWith(other._removes);
        return beforeAdds != _adds.Count || beforeRemoves != _removes.Count;
    }

    /// <summary>Returns an independent copy.</summary>
    public OrSet Copy()
    {
        var copy = new OrSet();
        copy._adds.UnionWith(_adds);
        copy._removes.UnionWith(_removes);
        return copy;
    }
}

/// <summary>A wire-stamped last-writer-wins liveness register.</summary>
public sealed class WireLwwRegister<T>
{
    /// <summary>Creates a register holding a value at a decisive stamp.</summary>
    public WireLwwRegister(WireStamp stamp, T value)
    {
        Guard.NotNull(stamp, nameof(stamp));
        Stamp = stamp;
        Value = value;
    }

    /// <summary>The decisive maximum stamp.</summary>
    public WireStamp Stamp { get; private set; }

    /// <summary>The current value.</summary>
    public T Value { get; private set; }

    /// <summary>Applies a write exactly when its stamp dominates.</summary>
    public bool Set(WireStamp stamp, T value)
    {
        Guard.NotNull(stamp, nameof(stamp));
        if (stamp.CompareTo(Stamp) <= 0) return false;
        Stamp = stamp;
        Value = value;
        return true;
    }

    /// <summary>Joins another replica by maximum stamp.</summary>
    public bool Join(WireLwwRegister<T> other)
    {
        Guard.NotNull(other, nameof(other));
        return Set(other.Stamp, other.Value);
    }
}

/// <summary>
/// The derived liveness projection: OR-set document presence gated by per-process LWW alive flags.
/// </summary>
public sealed class LivenessRegistry
{
    private readonly Dictionary<string, OrSet> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WireLwwRegister<bool>> _alive =
    new(StringComparer.Ordinal);

    /// <summary>Adds one document/process presence tag.</summary>
    public bool AddOpen(string document, string process, string tag)
    {
        var key = OpenKey(document, process);
        if (!_open.TryGetValue(key, out var set))
        {
            set = new OrSet();
            _open.Add(key, set);
        }

        return set.Add(tag);
    }

    /// <summary>Removes the observed presence tags for one document/process pair.</summary>
    public bool RemoveOpen(
    string document,
    string process,
    IEnumerable<string> observedTags)
    {
        var key = OpenKey(document, process);
        if (!_open.TryGetValue(key, out var set))
        {
            set = new OrSet();
            _open.Add(key, set);
        }

        return set.RemoveObserved(observedTags);
    }

    /// <summary>Writes a process alive flag under wire-stamp LWW ordering.</summary>
    public bool SetAlive(string process, bool alive, WireStamp stamp)
    {
        Guard.NotNullOrWhiteSpace(process, nameof(process));
        Guard.NotNull(stamp, nameof(stamp));
        if (!_alive.TryGetValue(process, out var register))
        {
            _alive.Add(process, new WireLwwRegister<bool>(stamp, alive));
            return true;
        }

        return register.Set(stamp, alive);
    }

    /// <summary>Returns whether one document is live through any currently-alive process.</summary>
    public bool IsDocumentLive(string document) =>
    _open.Any(
    entry =>
    SplitOpenKey(entry.Key) is var address
    && address.Document == document
    && entry.Value.Present
    && _alive.TryGetValue(address.Process, out var alive)
    && alive.Value);

    /// <summary>Returns the sorted derived live-document set.</summary>
    public IReadOnlyList<string> LiveDocuments() =>
    _open.Keys
    .Select(SplitOpenKey)
    .Select(address => address.Document)
    .Distinct(StringComparer.Ordinal)
    .Where(IsDocumentLive)
    .OrderBy(document => document, StringComparer.Ordinal)
    .ToArray();

    private static string OpenKey(string document, string process)
    {
        Guard.NotNullOrWhiteSpace(document, nameof(document));
        Guard.NotNullOrWhiteSpace(process, nameof(process));
        return $"{document}\0{process}";
    }

    private static (string Document, string Process) SplitOpenKey(string key)
    {
        var separator = key.IndexOf('\0');
        return (key[..separator], key[(separator + 1)..]);
    }
}

/// <summary>The partition/eviction escalation state for one isolated peer outbox.</summary>
public enum PeerEscalationRung
{
    /// <summary>The peer is making normal progress.</summary>
    Healthy,

    /// <summary>The peer is alive but behind; throttle only its producer.</summary>
    Backpressure,

    /// <summary>The carrier failed; retain the suffix for replay.</summary>
    RetainAndReplay,

    /// <summary>The lease expired or an unbounded op-log source exhausted all safe alternatives.</summary>
    Evict,
}

/// <summary>Inputs to the peer-isolated eviction policy.</summary>
public readonly record struct PeerHealth(
bool LeaseFresh,
bool IsFull,
bool Partitioned = false,
bool UncoalescibleOverflow = false);

/// <summary>Pure escalation policy: missed acknowledgements alone never evict a live peer.</summary>
public static class PeerEvictionPolicy
{
    /// <summary>Classifies a peer at the current escalation rung.</summary>
    public static PeerEscalationRung Evaluate(PeerHealth health)
    {
        if (!health.LeaseFresh || health.UncoalescibleOverflow)
        {
            return PeerEscalationRung.Evict;
        }

        if (health.IsFull) return PeerEscalationRung.Backpressure;
        if (health.Partitioned) return PeerEscalationRung.RetainAndReplay;
        return PeerEscalationRung.Healthy;
    }

    /// <summary>A consensus-backed distributed queue accepts writes only on a quorum side.</summary>
    public static bool DistributedQueueAllowsWrite(bool hasQuorum) => hasQuorum;
}

/// <summary>Outbound best-effort IPC transport seam.</summary>
public interface IpcSink
{
    /// <summary>Attempts to hand one frame to the carrier; false retains and stalls the driver.</summary>
    bool Send(IpcMessage message);
}

/// <summary>Inbound non-blocking IPC transport seam.</summary>
public interface IpcSource
{
    /// <summary>Returns the next frame, or null when currently exhausted.</summary>
    IpcMessage? Receive();
}

/// <summary>Injected monotonic clock for stall diagnostics and host-owned retry policy.</summary>
public interface ISyncClock
{
    /// <summary>Milliseconds from an arbitrary monotonic origin.</summary>
    ulong NowMilliseconds { get; }
}

/// <summary>Injected sender-side source of full graph snapshots.</summary>
public interface ISnapshotProvider
{
    /// <summary>Builds a snapshot whose epoch covers <paramref name="fromEpoch"/>.</summary>
    SnapshotMessage Snapshot(ulong fromEpoch);
}

/// <summary>Observable work completed by one bounded driver tick.</summary>
public sealed record SyncProgress(
int Sent,
IReadOnlyList<IpcMessage> Applied,
bool ResyncRequested,
int SnapshotsServed,
ulong PeerAckedThrough,
int Retained);

/// <summary>A source read failed and the host must re-establish the carrier.</summary>
public sealed class SyncDriverSourceException : Exception
{
    /// <summary>Wraps the source failure.</summary>
    public SyncDriverSourceException(Exception innerException)
    : base("The reliable-sync source failed; reconnect the carrier.", innerException)
    {
    }
}

/// <summary>
/// Scheduler-neutral, full-duplex reliable-sync loop with append-before-send and reconnect replay.
/// </summary>
public sealed class SyncDriver
{
    private readonly IpcSink _sink;
    private readonly IpcSource _source;
    private readonly IDurableOutbox _outbox;
    private readonly ISyncClock _clock;
    private readonly ISnapshotProvider _snapshots;
    private readonly ResyncCoordinator _coordinator;
    private readonly Queue<OutboxEntry> _pending = [];
    private readonly int _maxFramesPerPhase;
    private Queue<OutboxEntry>? _replay;
    private ulong _peerAckedThrough;
    private bool _replayPending;
    private bool _ackOwed;
    private ulong? _resyncRequestOwed;
    private ulong? _stalledSince;

    /// <summary>Creates a fresh driver at receiver epoch zero.</summary>
    public SyncDriver(
    IpcSink sink,
    IpcSource source,
    IDurableOutbox outbox,
    ISyncClock clock,
    ISnapshotProvider snapshots,
    int maxFramesPerPhase = 1024)
    : this(sink, source, outbox, clock, snapshots, 0, maxFramesPerPhase)
    {
    }

    /// <summary>Creates a driver that has already applied through an epoch.</summary>
    public SyncDriver(
    IpcSink sink,
    IpcSource source,
    IDurableOutbox outbox,
    ISyncClock clock,
    ISnapshotProvider snapshots,
    ulong lastEpoch,
    int maxFramesPerPhase = 1024)
    {
        Guard.NotNull(sink, nameof(sink));
        Guard.NotNull(source, nameof(source));
        Guard.NotNull(outbox, nameof(outbox));
        Guard.NotNull(clock, nameof(clock));
        Guard.NotNull(snapshots, nameof(snapshots));
        if (maxFramesPerPhase <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFramesPerPhase));
        }

        _sink = sink;
        _source = source;
        _outbox = outbox;
        _clock = clock;
        _snapshots = snapshots;
        _coordinator = new ResyncCoordinator(lastEpoch);
        _maxFramesPerPhase = maxFramesPerPhase;
    }

    /// <summary>The receiver's current applied epoch.</summary>
    public ulong LastEpoch => _coordinator.LastEpoch;

    /// <summary>True after a sink failure and before reconnect.</summary>
    public bool IsStalled => _stalledSince is not null;

    /// <summary>The peer's latest advertised acknowledgement cursor.</summary>
    public ulong PeerAckedThrough => _peerAckedThrough;

    /// <summary>The injected durable outbox.</summary>
    public IDurableOutbox Outbox => _outbox;

    /// <summary>Stages one accepted outbound frame for append-before-send.</summary>
    public void Enqueue(ulong epoch, IpcMessage message)
    {
        Guard.NotNull(message, nameof(message));
        _pending.Enqueue(new OutboxEntry(epoch, message));
    }

    /// <summary>Signals a fresh carrier; the next tick replays the unacknowledged suffix.</summary>
    public void OnReconnect()
    {
        _replayPending = true;
        _replay = null;
        _ackOwed = true;
        _stalledSince = null;
    }

    /// <summary>Returns the current stall duration.</summary>
    public ulong StalledFor(ulong nowMilliseconds) =>
    _stalledSince is { } since && nowMilliseconds >= since
    ? nowMilliseconds - since
    : 0;

    /// <summary>Runs one bounded loop pass.</summary>
    public SyncProgress Tick()
    {
        var sent = 0;
        var snapshotsServed = 0;
        var resyncRequested = false;
        var applied = new List<IpcMessage>();

        if (_replayPending && !IsStalled)
        {
            _replay ??= new Queue<OutboxEntry>(_outbox.ReplayFrom(_peerAckedThrough));
            for (var count = 0; count < _maxFramesPerPhase && _replay.Count > 0; count++)
            {
                var entry = _replay.Peek();
                if (!TrySend(entry.Message)) break;
                _replay.Dequeue();
                sent++;
            }

            if (_replay.Count == 0)
            {
                _replayPending = false;
                _replay = null;
            }
        }

        if (_resyncRequestOwed is { } requestFrom && !IsStalled)
        {
            if (TrySend(new ResyncRequestMessage(requestFrom)))
            {
                _resyncRequestOwed = null;
                resyncRequested = true;
            }
        }

        for (var count = 0;
        count < _maxFramesPerPhase && _pending.Count > 0 && !IsStalled;
        count++)
        {
            var entry = _pending.Dequeue();
            _outbox.Append(entry.Epoch, entry.Message);
            if (!TrySend(entry.Message)) break;
            sent++;
        }

        for (var count = 0; count < _maxFramesPerPhase; count++)
        {
            IpcMessage? message;
            try
            {
                message = _source.Receive();
            }
            catch (Exception error)
            {
                throw new SyncDriverSourceException(error);
            }

            if (message is null) break;
            switch (message)
            {
                case OutboxAckMessage acknowledgement:
                    _peerAckedThrough = Math.Max(_peerAckedThrough, acknowledgement.ThroughEpoch);
                    _outbox.AckThrough(acknowledgement.ThroughEpoch);
                    break;
                case ResyncRequestMessage request:
                    var snapshot = _snapshots.Snapshot(request.FromEpoch);
                    if (snapshot.Epoch < request.FromEpoch)
                    {
                        throw new InvalidOperationException(
                        "SnapshotProvider returned a snapshot that does not cover the requested epoch.");
                    }

                    if (TrySend(snapshot)) snapshotsServed++;
                    break;
                case CrdtSyncMessage:
                    applied.Add(message);
                    break;
                case SnapshotMessage:
                case DeltaMessage:
                    var decision = _coordinator.Ingest(message);
                    switch (decision.Action)
                    {
                        case ResyncAction.Apply:
                            _ackOwed = true;
                            applied.Add(message);
                            break;
                        case ResyncAction.RequestSnapshot:
                            _resyncRequestOwed = decision.FromEpoch;
                            if (!IsStalled && TrySend(new ResyncRequestMessage(decision.FromEpoch)))
                            {
                                _resyncRequestOwed = null;
                                resyncRequested = true;
                            }
                            break;
                        case ResyncAction.Ignore:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(message));
            }
        }

        if (_ackOwed && !IsStalled && TrySend(_coordinator.Acknowledgement()))
        {
            _ackOwed = false;
        }

        return new SyncProgress(
        sent,
        applied,
        resyncRequested,
        snapshotsServed,
        _peerAckedThrough,
        _outbox.RetainedDepth);
    }

    private bool TrySend(IpcMessage message)
    {
        if (_sink.Send(message)) return true;
        _stalledSince ??= _clock.NowMilliseconds;
        return false;
    }
}
