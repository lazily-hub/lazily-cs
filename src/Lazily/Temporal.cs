namespace Lazily;

internal static class LogicalTime
{
    internal static long NormalizePeriod(long value, string parameter)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(parameter, value, "logical duration must be non-negative");
        return Math.Max(1, value);
    }

    internal static long Require(long value, string parameter)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(parameter, value, "logical time must be non-negative");
        return value;
    }

    internal static long Add(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}

/// <summary>A pure temporal source driven by a monotone logical clock.</summary>
public interface ITimelineSource
{
    /// <summary>Advances to <paramref name="now"/> and reports whether a fire edge occurred.</summary>
    bool Tick(long now);

    /// <summary>The next logical fire time, or null when the source is exhausted.</summary>
    long? NextFire { get; }
}

/// <summary>A manually advanced monotone logical clock for tests and deterministic runtimes.</summary>
public sealed class ManualLogicalClock
{
    /// <summary>The current logical time.</summary>
    public long Now { get; private set; }

    /// <summary>Advances monotonically, clamping a backwards request to the current time.</summary>
    public long Advance(long now)
    {
        LogicalTime.Require(now, nameof(now));
        Now = Math.Max(Now, now);
        return Now;
    }
}

/// <summary>The single value emitted by a timer.</summary>
public readonly record struct Unit;

/// <summary>A single-shot logical-clock timer.</summary>
public sealed class TimerCell : ITimelineSource
{
    private readonly long _fireAt;

    /// <summary>Creates a timer that fires once at or after <paramref name="fireAt"/>.</summary>
    public TimerCell(Context context, long fireAt)
    {
        Guard.NotNull(context, nameof(context));
        _fireAt = LogicalTime.Require(fireAt, nameof(fireAt));
        FiredCell = context.Source(false);
    }

    /// <summary>The reactive fired flag.</summary>
    public Source<bool> FiredCell { get; }

    /// <summary>Whether the timer has fired.</summary>
    public bool HasFired => FiredCell.Get();

    /// <summary>The optional unit value emitted by the timer.</summary>
    public Optional<Unit> Value => HasFired ? Optional<Unit>.Some(default) : Optional<Unit>.None;

    /// <inheritdoc />
    public long? NextFire => HasFired ? null : _fireAt;

    /// <inheritdoc />
    public bool Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (HasFired || now < _fireAt) return false;
        FiredCell.Set(true);
        return true;
    }
}

/// <summary>A periodic logical-clock source whose count includes every crossed boundary.</summary>
public sealed class IntervalCell : ITimelineSource
{
    private readonly long _period;
    private long _next;

    /// <summary>Creates an interval with boundaries at period, 2×period, and so on.</summary>
    public IntervalCell(Context context, long period)
    {
        Guard.NotNull(context, nameof(context));
        _period = LogicalTime.NormalizePeriod(period, nameof(period));
        _next = _period;
        CountCell = context.Source(0L);
    }

    /// <summary>The reactive total fire count.</summary>
    public Source<long> CountCell { get; }

    /// <summary>Total boundaries crossed so far.</summary>
    public long Count => CountCell.Get();

    /// <inheritdoc />
    public long? NextFire => _next;

    /// <inheritdoc />
    public bool Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (now < _next) return false;
        var fires = checked(((now - _next) / _period) + 1);
        CountCell.Set(checked(Count + fires));
        _next = LogicalTime.Add(_next, checked(fires * _period));
        return true;
    }
}

/// <summary>A pattern-periodic source whose cycle offsets form a cron-shaped schedule.</summary>
public sealed class CronCell : ITimelineSource
{
    private readonly long _cycle;
    private readonly long[] _offsets;
    private long _cursor;

    /// <summary>Creates a pattern schedule, normalizing, sorting, and deduplicating offsets.</summary>
    public CronCell(Context context, long cycle, IEnumerable<long> offsets)
    {
        Guard.NotNull(context, nameof(context));
        Guard.NotNull(offsets, nameof(offsets));
        _cycle = LogicalTime.NormalizePeriod(cycle, nameof(cycle));
        _offsets = offsets
            .Select(offset => LogicalTime.Require(offset, nameof(offsets)) % _cycle)
            .Distinct()
            .OrderBy(offset => offset)
            .ToArray();
        CountCell = context.Source(0L);
    }

    /// <summary>The reactive total fire count.</summary>
    public Source<long> CountCell { get; }

    /// <summary>Total matching ticks observed so far.</summary>
    public long Count => CountCell.Get();

    /// <inheritdoc />
    public long? NextFire
    {
        get
        {
            if (_offsets.Length == 0 || _cursor == long.MaxValue) return null;
            var start = _cursor + 1;
            long? best = null;
            foreach (var offset in _offsets)
            {
                var remainder = start % _cycle;
                var delta = (offset - remainder + _cycle) % _cycle;
                var candidate = LogicalTime.Add(start, delta);
                if (candidate < start) continue;
                if (best is null || candidate < best) best = candidate;
            }
            return best;
        }
    }

    /// <inheritdoc />
    public bool Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (now <= _cursor) return false;
        long fires = 0;
        foreach (var offset in _offsets)
        {
            fires = checked(fires + CountUpTo(now, offset) - CountUpTo(_cursor, offset));
        }
        _cursor = now;
        if (fires == 0) return false;
        CountCell.Set(checked(Count + fires));
        return true;
    }

    private long CountUpTo(long now, long offset)
    {
        if (offset == 0) return now / _cycle;
        return offset <= now ? ((now - offset) / _cycle) + 1 : 0;
    }
}

/// <summary>The liveness state of a deadlined value.</summary>
public enum DeadlinePhase
{
    /// <summary>The deadline has not elapsed.</summary>
    Live,

    /// <summary>The deadline has elapsed.</summary>
    Expired,
}

/// <summary>A value paired with its live or expired phase.</summary>
public sealed record Deadlined<T>(DeadlinePhase Phase, T Value);

/// <summary>A value-preserving logical deadline that expires exactly once.</summary>
public sealed class DeadlineCell<T> : ITimelineSource
{
    private readonly long _deadline;
    private readonly T _value;

    /// <summary>Creates a live value that expires at <paramref name="deadline"/>.</summary>
    public DeadlineCell(Context context, T value, long deadline)
    {
        Guard.NotNull(context, nameof(context));
        Guard.NotNull(value, nameof(value));
        _value = value;
        _deadline = LogicalTime.Require(deadline, nameof(deadline));
        ExpiredCell = context.Source(false);
    }

    /// <summary>The reactive expiry flag.</summary>
    public Source<bool> ExpiredCell { get; }

    /// <summary>The current value and liveness phase.</summary>
    public Deadlined<T> State => new(ExpiredCell.Get() ? DeadlinePhase.Expired : DeadlinePhase.Live, _value);

    /// <inheritdoc />
    public long? NextFire => ExpiredCell.Get() ? null : _deadline;

    /// <inheritdoc />
    public bool Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (ExpiredCell.Get() || now < _deadline) return false;
        ExpiredCell.Set(true);
        return true;
    }
}
