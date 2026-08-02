namespace Lazily;

/// <summary>The SWIM liveness state of a peer.</summary>
public enum PeerState
{
    /// <summary>The peer is responding to heartbeats.</summary>
    Alive,

    /// <summary>The failure detector considers the peer suspect.</summary>
    Suspect,

    /// <summary>The suspect timeout elapsed without recovery.</summary>
    Dead,

    /// <summary>The peer explicitly left the group.</summary>
    Left,
}

/// <summary>Configuration for SWIM membership and Phi-accrual failure detection.</summary>
public sealed record MembershipConfig(
    double PhiThreshold = 8.0,
    long SuspectTimeout = 5,
    int MaxSamples = 100,
    double MinStandardDeviation = 0.1);

/// <summary>A membership state transition.</summary>
public sealed record PeerChange(string Type, long Peer, PeerState? From = null, PeerState? To = null);

/// <summary>A Phi-accrual failure detector over a logical clock.</summary>
public sealed class PhiAccrual
{
    private readonly int _maxSamples;
    private readonly double _minStandardDeviation;
    private readonly Queue<long> _intervals = [];
    private long? _lastHeartbeat;

    /// <summary>Creates a bounded Phi-accrual detector.</summary>
    public PhiAccrual(int maxSamples = 100, double minStandardDeviation = 0.1)
    {
        _maxSamples = Math.Max(1, maxSamples);
        _minStandardDeviation = Math.Max(double.Epsilon, minStandardDeviation);
    }

    /// <summary>Records a heartbeat at monotone logical time <paramref name="now"/>.</summary>
    public void Heartbeat(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (_lastHeartbeat is not null)
        {
            _intervals.Enqueue(Math.Max(0, now - _lastHeartbeat.Value));
            while (_intervals.Count > _maxSamples) _intervals.Dequeue();
        }
        _lastHeartbeat = now;
    }

    /// <summary>Returns the suspicion value at logical time <paramref name="now"/>.</summary>
    public double Phi(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (_lastHeartbeat is null || _intervals.Count == 0) return 0;
        var mean = _intervals.Average();
        var variance = _intervals.Average(value => Math.Pow(value - mean, 2));
        var deviation = Math.Max(Math.Sqrt(variance), _minStandardDeviation);
        var elapsed = now - _lastHeartbeat.Value;
        var y = (elapsed - mean) / deviation;
        var exponent = Math.Exp(-y * (1.5976 + (0.070566 * y * y)));
        return elapsed > mean
            ? -Math.Log10(exponent / (1 + exponent))
            : -Math.Log10(1 - (1 / (1 + exponent)));
    }
}

/// <summary>
/// Reactive SWIM membership backed by a Phi-accrual failure detector per peer.
/// </summary>
public sealed class MembershipCell
{
    private sealed class PeerRecord
    {
        internal PeerRecord(PhiAccrual detector)
        {
            Detector = detector;
        }

        internal PeerState State { get; set; } = PeerState.Alive;
        internal PhiAccrual Detector { get; }
        internal long? SuspectSince { get; set; }
    }

    private readonly MembershipConfig _config;
    private readonly Dictionary<long, PeerRecord> _peers = [];

    /// <summary>Creates an empty membership view.</summary>
    public MembershipCell(Context context, MembershipConfig? config = null)
    {
        Guard.NotNull(context, nameof(context));
        _config = config ?? new MembershipConfig();
        if (_config.SuspectTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(config), "suspect timeout must be non-negative");
        PeerSetCell = context.Source<IReadOnlyList<long>>(
            [],
            SequenceEqualityComparer<long>.Instance);
    }

    /// <summary>The reactive sorted set of alive peers.</summary>
    public Source<IReadOnlyList<long>> PeerSetCell { get; }

    /// <summary>The sorted set of alive peers.</summary>
    public IReadOnlyList<long> PeerSet => PeerSetCell.Get();

    /// <summary>Returns a peer's state, or null when it is unknown.</summary>
    public PeerState? State(long peer) => _peers.TryGetValue(peer, out var record) ? record.State : null;

    /// <summary>Adds or revives a peer.</summary>
    public IReadOnlyList<PeerChange> Join(long peer, long now)
    {
        LogicalTime.Require(now, nameof(now));
        var previous = State(peer);
        var detector = NewDetector();
        detector.Heartbeat(now);
        _peers[peer] = new PeerRecord(detector);
        Refresh();
        return previous switch
        {
            null => [new PeerChange("Joined", peer)],
            PeerState.Alive => [],

            // Every non-Alive state is a REVIVAL, and they report identically by construction:
            // the change carries `previous` verbatim, so the three are distinguishable to a reader
            // without three arms here. Named rather than absorbed so a state added later has to be
            // classified deliberately instead of silently reported as a revival to Alive.
            PeerState.Suspect or PeerState.Dead or PeerState.Left =>
                [new PeerChange("StateChanged", peer, previous, PeerState.Alive)],

            _ => throw new ArgumentOutOfRangeException(
                nameof(peer), previous, "Unknown prior peer state."),
        };
    }

    /// <summary>Records a heartbeat, joining an unknown peer and reviving a suspect or dead one.</summary>
    public IReadOnlyList<PeerChange> Heartbeat(long peer, long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (!_peers.TryGetValue(peer, out var record)) return Join(peer, now);
        record.Detector.Heartbeat(now);
        var previous = record.State;
        if (previous is not (PeerState.Alive or PeerState.Left))
        {
            record.State = PeerState.Alive;
            record.SuspectSince = null;
            Refresh();
            return [new PeerChange("StateChanged", peer, previous, PeerState.Alive)];
        }
        Refresh();
        return [];
    }

    /// <summary>Marks a known peer as having explicitly left.</summary>
    public IReadOnlyList<PeerChange> Leave(long peer, long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (!_peers.TryGetValue(peer, out var record) || record.State == PeerState.Left) return [];
        record.State = PeerState.Left;
        record.SuspectSince = null;
        Refresh();
        return [new PeerChange("Left", peer)];
    }

    /// <summary>Advances failure detection and returns all state transitions.</summary>
    public IReadOnlyList<PeerChange> Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        List<PeerChange> changes = [];
        foreach (var (peer, record) in _peers)
        {
            if (record.State == PeerState.Alive && record.Detector.Phi(now) > _config.PhiThreshold)
            {
                record.State = PeerState.Suspect;
                record.SuspectSince = now;
                changes.Add(new PeerChange(
                    "StateChanged", peer, PeerState.Alive, PeerState.Suspect));
            }
            else if (record.State == PeerState.Suspect
                     && record.SuspectSince is not null
                     && now - record.SuspectSince.Value >= _config.SuspectTimeout)
            {
                record.State = PeerState.Dead;
                changes.Add(new PeerChange(
                    "StateChanged", peer, PeerState.Suspect, PeerState.Dead));
            }
        }
        Refresh();
        return changes;
    }

    private PhiAccrual NewDetector() =>
        new(_config.MaxSamples, _config.MinStandardDeviation);

    private void Refresh()
    {
        IReadOnlyList<long> alive = [.. _peers
            .Where(pair => pair.Value.State == PeerState.Alive)
            .Select(pair => pair.Key)
            .OrderBy(peer => peer)];
        PeerSetCell.Set(alive);
    }
}
