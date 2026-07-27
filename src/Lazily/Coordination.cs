namespace Lazily;

internal sealed class LeaseCore
{
    internal long? HolderPeer { get; private set; }
    internal long Expiry { get; private set; }
    internal long Fence { get; private set; }

    internal bool IsHeld(long now) => HolderPeer is not null && now < Expiry;

    internal long? Holder(long now) => IsHeld(now) ? HolderPeer : null;

    internal long? Acquire(long peer, long now, long ttl)
    {
        if (HolderPeer is null || now >= Expiry)
        {
            Fence = checked(Fence + 1);
            HolderPeer = peer;
            Expiry = LogicalTime.Add(now, ttl);
            return Fence;
        }
        if (HolderPeer == peer)
        {
            Expiry = LogicalTime.Add(now, ttl);
            return Fence;
        }
        return null;
    }

    internal bool Renew(long peer, long now, long ttl)
    {
        if (!IsHeld(now) || HolderPeer != peer) return false;
        Expiry = LogicalTime.Add(now, ttl);
        return true;
    }

    internal void Release(long peer)
    {
        if (HolderPeer == peer) HolderPeer = null;
    }

    internal bool Tick(long now)
    {
        if (HolderPeer is null || now < Expiry) return false;
        HolderPeer = null;
        return true;
    }
}

/// <summary>A single-writer lease with a monotone fencing token.</summary>
public sealed class LeaseCell
{
    private readonly LeaseCore _core = new();

    /// <summary>Creates an initially free lease.</summary>
    public LeaseCell(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HolderCell = context.Source(Optional<long>.None);
    }

    /// <summary>The reactive current holder.</summary>
    public Source<Optional<long>> HolderCell { get; }

    /// <summary>The latest fencing token, including after expiry.</summary>
    public long Fence => _core.Fence;

    /// <summary>Attempts to acquire or renew the lease, returning its fencing token.</summary>
    public long? Acquire(long peer, long now, long ttl)
    {
        ValidateTime(now, ttl);
        var fence = _core.Acquire(peer, now, ttl);
        Refresh(now);
        return fence;
    }

    /// <summary>Renews a live lease held by <paramref name="peer"/>.</summary>
    public bool Renew(long peer, long now, long ttl)
    {
        ValidateTime(now, ttl);
        var renewed = _core.Renew(peer, now, ttl);
        Refresh(now);
        return renewed;
    }

    /// <summary>Releases the lease when held by <paramref name="peer"/>.</summary>
    public void Release(long peer, long now)
    {
        LogicalTime.Require(now, nameof(now));
        _core.Release(peer);
        Refresh(now);
    }

    /// <summary>Expires the lease at its deadline and reports the expiry edge.</summary>
    public bool Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        var expired = _core.Tick(now);
        Refresh(now);
        return expired;
    }

    /// <summary>Returns the current holder at logical time <paramref name="now"/>.</summary>
    public long? Holder(long now)
    {
        LogicalTime.Require(now, nameof(now));
        return _core.Holder(now);
    }

    /// <summary>Whether the lease is held at logical time <paramref name="now"/>.</summary>
    public bool IsHeld(long now)
    {
        LogicalTime.Require(now, nameof(now));
        return _core.IsHeld(now);
    }

    private static void ValidateTime(long now, long ttl)
    {
        LogicalTime.Require(now, nameof(now));
        LogicalTime.Require(ttl, nameof(ttl));
    }

    private void Refresh(long now)
    {
        var holder = _core.Holder(now);
        HolderCell.Set(holder is null ? Optional<long>.None : Optional<long>.Some(holder.Value));
    }
}

/// <summary>The local node's role in lease-backed leader election.</summary>
public enum LeaderRole
{
    /// <summary>The local node holds the leader lease.</summary>
    Leader,

    /// <summary>Another peer holds the leader lease.</summary>
    Follower,

    /// <summary>No peer currently holds the leader lease.</summary>
    Candidate,
}

/// <summary>Reactive leader election over a lease.</summary>
public sealed class LeaderCell
{
    private readonly long _me;
    private readonly LeaseCore _core = new();

    /// <summary>Creates a leader view from peer <paramref name="me"/>'s perspective.</summary>
    public LeaderCell(Context context, long me)
    {
        ArgumentNullException.ThrowIfNull(context);
        _me = me;
        CurrentLeaderCell = context.Source(Optional<long>.None);
    }

    /// <summary>The reactive current leader.</summary>
    public Source<Optional<long>> CurrentLeaderCell { get; }

    /// <summary>Campaigns for leadership.</summary>
    public LeaderRole Campaign(long now, long ttl)
    {
        _core.Acquire(_me, LogicalTime.Require(now, nameof(now)), LogicalTime.Require(ttl, nameof(ttl)));
        Refresh(now);
        return Role(now);
    }

    /// <summary>Processes another peer's leadership contention.</summary>
    public LeaderRole Contend(long peer, long now, long ttl)
    {
        _core.Acquire(peer, LogicalTime.Require(now, nameof(now)), LogicalTime.Require(ttl, nameof(ttl)));
        Refresh(now);
        return Role(now);
    }

    /// <summary>Advances lease expiry and returns the resulting role.</summary>
    public LeaderRole Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        _core.Tick(now);
        Refresh(now);
        return Role(now);
    }

    /// <summary>Returns the current leader.</summary>
    public long? CurrentLeader(long now)
    {
        LogicalTime.Require(now, nameof(now));
        return _core.Holder(now);
    }

    /// <summary>Returns this node's current role.</summary>
    public LeaderRole Role(long now)
    {
        var holder = CurrentLeader(now);
        return holder is null
            ? LeaderRole.Candidate
            : holder == _me ? LeaderRole.Leader : LeaderRole.Follower;
    }

    private void Refresh(long now)
    {
        var holder = _core.Holder(now);
        CurrentLeaderCell.Set(
            holder is null ? Optional<long>.None : Optional<long>.Some(holder.Value));
    }
}

/// <summary>A distributed mutex backed by a lease and fencing token.</summary>
public sealed class LockCell
{
    private readonly LeaseCore _core = new();

    /// <summary>Creates an initially unlocked mutex.</summary>
    public LockCell(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IsLockedCell = context.Source(false);
    }

    /// <summary>The reactive locked flag.</summary>
    public Source<bool> IsLockedCell { get; }

    /// <summary>The latest fencing token.</summary>
    public long Fence => _core.Fence;

    /// <summary>Attempts to acquire the lock.</summary>
    public long? Acquire(long peer, long now, long ttl)
    {
        LogicalTime.Require(now, nameof(now));
        LogicalTime.Require(ttl, nameof(ttl));
        var fence = _core.Acquire(peer, now, ttl);
        Refresh(now);
        return fence;
    }

    /// <summary>Releases a lock held by <paramref name="peer"/>.</summary>
    public void Release(long peer, long now)
    {
        LogicalTime.Require(now, nameof(now));
        _core.Release(peer);
        Refresh(now);
    }

    /// <summary>Expires the lock and reports the expiry edge.</summary>
    public bool Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        var expired = _core.Tick(now);
        Refresh(now);
        return expired;
    }

    /// <summary>Whether the lock is live at logical time <paramref name="now"/>.</summary>
    public bool IsLocked(long now)
    {
        LogicalTime.Require(now, nameof(now));
        return _core.IsHeld(now);
    }

    /// <summary>Whether a fencing token is current for a live lock.</summary>
    public bool Validate(long fence, long now) => IsLocked(now) && _core.Fence == fence;

    private void Refresh(long now) => IsLockedCell.Set(_core.IsHeld(now));
}

/// <summary>A bounded reactive permit pool.</summary>
public sealed class SemaphoreCell
{
    private readonly int _capacity;
    private int _acquired;

    /// <summary>Creates a semaphore with <paramref name="capacity"/> permits.</summary>
    public SemaphoreCell(Context context, int capacity)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        PermitsAvailableCell = context.Source(capacity);
    }

    /// <summary>The reactive available permit count.</summary>
    public Source<int> PermitsAvailableCell { get; }

    /// <summary>The number of available permits.</summary>
    public int PermitsAvailable => PermitsAvailableCell.Get();

    /// <summary>Attempts to acquire one permit.</summary>
    public bool Acquire()
    {
        if (_acquired >= _capacity) return false;
        _acquired++;
        Refresh();
        return true;
    }

    /// <summary>Releases one permit, saturating at capacity.</summary>
    public void Release()
    {
        if (_acquired > 0) _acquired--;
        Refresh();
    }

    private void Refresh() => PermitsAvailableCell.Set(_capacity - _acquired);
}

/// <summary>A reactive wait-for-N barrier over distinct peers.</summary>
public sealed class BarrierCell
{
    private readonly int _required;
    private readonly HashSet<long> _arrived = [];

    /// <summary>Creates a barrier requiring <paramref name="required"/> distinct arrivals.</summary>
    public BarrierCell(Context context, int required)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (required < 0) throw new ArgumentOutOfRangeException(nameof(required));
        _required = required;
        IsOpenCell = context.Source(required == 0);
    }

    /// <summary>The reactive open flag.</summary>
    public Source<bool> IsOpenCell { get; }

    /// <summary>The number of distinct arrivals.</summary>
    public int Count => _arrived.Count;

    /// <summary>Whether the barrier has met its requirement.</summary>
    public bool IsOpen => IsOpenCell.Get();

    /// <summary>Creates a strict-majority quorum barrier.</summary>
    public static BarrierCell Quorum(Context context, int total) =>
        new(context, checked((total / 2) + 1));

    /// <summary>Registers one peer and returns whether the barrier is open.</summary>
    public bool Arrive(long peer)
    {
        _arrived.Add(peer);
        IsOpenCell.Set(_arrived.Count >= _required);
        return IsOpen;
    }
}
