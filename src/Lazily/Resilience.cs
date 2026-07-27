namespace Lazily;

/// <summary>The lifecycle state of a circuit breaker.</summary>
public enum BreakerState
{
    /// <summary>Calls are admitted and outcomes are recorded.</summary>
    Closed,

    /// <summary>Calls fail fast until the reset deadline.</summary>
    Open,

    /// <summary>A probe call is admitted to determine recovery.</summary>
    HalfOpen,
}

/// <summary>A reactive sliding-window circuit breaker.</summary>
public sealed class CircuitBreakerCell
{
    private readonly int _window;
    private readonly int _failureThreshold;
    private readonly long _resetTimeout;
    private readonly Queue<bool> _outcomes = [];
    private long _openUntil;

    /// <summary>Creates a circuit breaker.</summary>
    public CircuitBreakerCell(
        Context context,
        int window,
        int failureThreshold,
        long resetTimeout)
    {
        ArgumentNullException.ThrowIfNull(context);
        _window = Math.Max(1, window);
        _failureThreshold = Math.Max(1, failureThreshold);
        _resetTimeout = LogicalTime.Require(resetTimeout, nameof(resetTimeout));
        StateCell = context.Source(BreakerState.Closed);
    }

    /// <summary>The reactive breaker state.</summary>
    public Source<BreakerState> StateCell { get; }

    /// <summary>The current breaker state.</summary>
    public BreakerState State => StateCell.Get();

    /// <summary>Returns whether a call may proceed, transitioning Open to HalfOpen at its deadline.</summary>
    public bool Allow(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (State == BreakerState.Closed) return true;
        if (State == BreakerState.Open)
        {
            if (now < _openUntil) return false;
            StateCell.Set(BreakerState.HalfOpen);
        }
        return true;
    }

    /// <summary>Records an admitted call's outcome.</summary>
    public void Record(bool success, long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (State == BreakerState.HalfOpen)
        {
            if (success)
            {
                _outcomes.Clear();
                StateCell.Set(BreakerState.Closed);
            }
            else
            {
                Open(now);
            }
            return;
        }
        if (State != BreakerState.Closed) return;
        _outcomes.Enqueue(success);
        while (_outcomes.Count > _window) _outcomes.Dequeue();
        if (_outcomes.Count(outcome => !outcome) >= _failureThreshold) Open(now);
    }

    private void Open(long now)
    {
        _openUntil = LogicalTime.Add(now, _resetTimeout);
        StateCell.Set(BreakerState.Open);
    }
}

/// <summary>A reactive exponential-backoff retry policy.</summary>
public sealed class RetryPolicyCell
{
    private readonly long _baseDelay;
    private readonly long _cap;
    private int _attempt;

    /// <summary>Creates a saturating exponential-backoff policy.</summary>
    public RetryPolicyCell(Context context, long baseDelay, long cap)
    {
        ArgumentNullException.ThrowIfNull(context);
        _baseDelay = LogicalTime.Require(baseDelay, nameof(baseDelay));
        _cap = LogicalTime.Require(cap, nameof(cap));
        DelayCell = context.Source(0L);
    }

    /// <summary>The reactive most recently yielded delay.</summary>
    public Source<long> DelayCell { get; }

    /// <summary>The most recently yielded delay.</summary>
    public long Delay => DelayCell.Get();

    /// <summary>Yields the current attempt's delay and advances to the next attempt.</summary>
    public long NextDelay()
    {
        var delay = DelayFor(_attempt);
        if (_attempt < int.MaxValue) _attempt++;
        DelayCell.Set(delay);
        return delay;
    }

    /// <summary>Resets the attempt counter and projected delay.</summary>
    public void Reset()
    {
        _attempt = 0;
        DelayCell.Set(0);
    }

    private long DelayFor(int attempt)
    {
        if (_baseDelay >= _cap || attempt >= 63) return _cap;
        var multiplier = 1UL << attempt;
        if ((ulong)_baseDelay > (ulong)_cap / multiplier) return _cap;
        return Math.Min(_cap, checked(_baseDelay * (long)multiplier));
    }
}

/// <summary>A reactive bounded isolation pool.</summary>
public sealed class BulkheadCell
{
    private readonly int _capacity;
    private int _inUse;

    /// <summary>Creates a bulkhead with <paramref name="capacity"/> concurrent permits.</summary>
    public BulkheadCell(Context context, int capacity)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        InUseCell = context.Source(0);
    }

    /// <summary>The reactive permit count in use.</summary>
    public Source<int> InUseCell { get; }

    /// <summary>The number of permits in use.</summary>
    public int InUse => InUseCell.Get();

    /// <summary>Attempts to enter the isolation pool.</summary>
    public bool Acquire()
    {
        if (_inUse >= _capacity) return false;
        _inUse++;
        InUseCell.Set(_inUse);
        return true;
    }

    /// <summary>Releases one permit, saturating at zero.</summary>
    public void Release()
    {
        if (_inUse > 0) _inUse--;
        InUseCell.Set(_inUse);
    }
}

/// <summary>A reactive logical deadline for one bounded call.</summary>
public sealed class TimeoutCell
{
    private long _deadline;
    private bool _armed;

    /// <summary>Creates an unarmed timeout.</summary>
    public TimeoutCell(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IsTimedOutCell = context.Source(false);
    }

    /// <summary>The reactive timeout flag.</summary>
    public Source<bool> IsTimedOutCell { get; }

    /// <summary>Whether the current call timed out.</summary>
    public bool IsTimedOut => IsTimedOutCell.Get();

    /// <summary>Arms a fresh deadline and returns the cleared timeout flag.</summary>
    public bool Arm(long now, long timeout)
    {
        LogicalTime.Require(now, nameof(now));
        LogicalTime.Require(timeout, nameof(timeout));
        _deadline = LogicalTime.Add(now, timeout);
        _armed = true;
        IsTimedOutCell.Set(false);
        return false;
    }

    /// <summary>Advances the deadline and reports the timeout edge exactly once.</summary>
    public bool Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (!_armed || IsTimedOut || now < _deadline) return false;
        IsTimedOutCell.Set(true);
        return true;
    }
}
