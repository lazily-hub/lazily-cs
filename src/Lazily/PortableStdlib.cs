namespace Lazily;

/// <summary>Typed unavailable reasons shared by the portable stdlib primitives.</summary>
public enum StdlibUnavailableReason
{
    /// <summary>The requested duration cannot be added without wrapping.</summary>
    DeadlineOverflow,

    /// <summary>A caller supplied a tick older than the last accepted tick.</summary>
    ClockRegression,

    /// <summary>The operation adapter cannot be observed in this runtime.</summary>
    OperationUnavailable,

    /// <summary>The cancellation adapter is foreign or unreadable.</summary>
    CancellationUnavailable,
}

/// <summary>Wire names for portable stdlib outcomes and reasons.</summary>
public static class StdlibWire
{
    /// <summary>Returns the canonical wire spelling of an unavailable reason.</summary>
    public static string Name(this StdlibUnavailableReason reason) =>
        reason switch
        {
            StdlibUnavailableReason.DeadlineOverflow => "deadline_overflow",
            StdlibUnavailableReason.ClockRegression => "clock_regression",
            StdlibUnavailableReason.OperationUnavailable => "operation_unavailable",
            StdlibUnavailableReason.CancellationUnavailable => "cancellation_unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        };

    /// <summary>Returns the canonical wire spelling of a timer outcome.</summary>
    public static string Name(this TimerOutcome outcome) =>
        outcome switch
        {
            TimerOutcome.Pending => "pending",
            TimerOutcome.Fired => "fired",
            TimerOutcome.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };

    /// <summary>Returns the canonical wire spelling of a timeout outcome.</summary>
    public static string Name(this TimeoutOutcome outcome) =>
        outcome switch
        {
            TimeoutOutcome.Pending => "pending",
            TimeoutOutcome.Completed => "completed",
            TimeoutOutcome.TimedOut => "timed_out",
            TimeoutOutcome.Cancelled => "cancelled",
            TimeoutOutcome.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };

    /// <summary>Returns the canonical wire spelling of a revision-barrier outcome.</summary>
    public static string Name(this RevisionBarrierOutcome outcome) =>
        outcome switch
        {
            RevisionBarrierOutcome.Pending => "pending",
            RevisionBarrierOutcome.Satisfied => "satisfied",
            RevisionBarrierOutcome.TimedOut => "timed_out",
            RevisionBarrierOutcome.Cancelled => "cancelled",
            RevisionBarrierOutcome.Disposed => "disposed",
            RevisionBarrierOutcome.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
}

/// <summary>Typed construction failure for an unavailable portable primitive.</summary>
public sealed class StdlibUnavailableException : ArgumentOutOfRangeException
{
    /// <summary>Creates a failure with its wire-stable reason.</summary>
    public StdlibUnavailableException(StdlibUnavailableReason reason)
        : base(nameof(reason), reason.Name())
    {
        Reason = reason;
    }

    /// <summary>The typed reason.</summary>
    public StdlibUnavailableReason Reason { get; }
}

/// <summary>Checked arithmetic for the unsigned 64-bit logical clock.</summary>
public static class PortableClock
{
    /// <summary>Returns <paramref name="now"/> + <paramref name="duration"/> without wrapping.</summary>
    public static ulong CheckedDeadline(ulong now, ulong duration)
    {
        if (duration > ulong.MaxValue - now)
        {
            throw new StdlibUnavailableException(StdlibUnavailableReason.DeadlineOverflow);
        }
        return now + duration;
    }
}

/// <summary>Externally observable timer states.</summary>
public enum TimerOutcome
{
    /// <summary>The deadline has not been reached.</summary>
    Pending,

    /// <summary>The timer fired and its first firing tick is latched.</summary>
    Fired,

    /// <summary>The supplied clock observation was invalid.</summary>
    Unavailable,
}

/// <summary>A deterministic timer observation.</summary>
public readonly record struct TimerObservation(
    TimerOutcome Outcome,
    ulong? Deadline = null,
    ulong? FiredAt = null,
    StdlibUnavailableReason? Reason = null);

/// <summary>A caller-driven, deterministic, single-shot logical-clock timer.</summary>
public sealed class Timer
{
    private readonly object _gate = new();
    private ulong _lastNow;
    private ulong? _firedAt;

    /// <summary>Creates a timer with checked unsigned deadline arithmetic.</summary>
    public Timer(ulong now, ulong duration)
    {
        Deadline = PortableClock.CheckedDeadline(now, duration);
        _lastNow = now;
    }

    /// <summary>The exact firing deadline.</summary>
    public ulong Deadline { get; }

    /// <summary>Observes one caller-supplied logical tick.</summary>
    public TimerObservation Observe(ulong now)
    {
        lock (_gate)
        {
            if (_firedAt is { } firedAt)
            {
                return new(TimerOutcome.Fired, FiredAt: firedAt);
            }
            if (now < _lastNow)
            {
                return new(
                    TimerOutcome.Unavailable,
                    Deadline,
                    Reason: StdlibUnavailableReason.ClockRegression);
            }
            _lastNow = now;
            if (now >= Deadline)
            {
                _firedAt = now;
                return new(TimerOutcome.Fired, FiredAt: now);
            }
            return new(TimerOutcome.Pending, Deadline);
        }
    }

    /// <summary>Task adapter over the scheduler-free synchronous core.</summary>
    public Task<TimerObservation> ObserveAsync(ulong now) => Task.FromResult(Observe(now));
}

/// <summary>The state returned by a timeout operation adapter.</summary>
public enum TimeoutOperationState
{
    /// <summary>The operation remains pending.</summary>
    Pending,

    /// <summary>The operation completed with a value.</summary>
    Completed,

    /// <summary>The operation is unavailable in this runtime.</summary>
    Unavailable,
}

/// <summary>One operation-adapter poll.</summary>
public readonly record struct TimeoutOperation<T>(TimeoutOperationState State, T? Value = default)
{
    /// <summary>Creates a pending result.</summary>
    public static TimeoutOperation<T> Pending() => new(TimeoutOperationState.Pending);

    /// <summary>Creates a completed result.</summary>
    public static TimeoutOperation<T> Completed(T value) =>
        new(TimeoutOperationState.Completed, value);

    /// <summary>Creates an unavailable result.</summary>
    public static TimeoutOperation<T> Unavailable() => new(TimeoutOperationState.Unavailable);
}

/// <summary>Caller-owned cancellation adapter states.</summary>
public enum TimeoutCancellation
{
    /// <summary>Cancellation is not requested.</summary>
    Pending,

    /// <summary>Cancellation is requested.</summary>
    Cancelled,

    /// <summary>The cancellation source is foreign or unreadable.</summary>
    Unavailable,
}

/// <summary>Externally observable timeout states.</summary>
public enum TimeoutOutcome
{
    /// <summary>The operation remains pending.</summary>
    Pending,

    /// <summary>The operation completed.</summary>
    Completed,

    /// <summary>The exact deadline won before adapters were invoked.</summary>
    TimedOut,

    /// <summary>Caller-owned cancellation won.</summary>
    Cancelled,

    /// <summary>An adapter or clock was unavailable.</summary>
    Unavailable,
}

/// <summary>A deterministic, terminal-latching timeout observation.</summary>
public readonly record struct TimeoutObservation<T>(
    TimeoutOutcome Outcome,
    ulong? Deadline = null,
    T? Value = default,
    StdlibUnavailableReason? Reason = null);

/// <summary>Caller-driven timeout with no scheduler or async-runtime ownership.</summary>
public sealed class Timeout<T>
{
    private readonly object _gate = new();
    private ulong _lastNow;
    private TimeoutObservation<T>? _terminal;

    /// <summary>Creates a timeout with checked unsigned deadline arithmetic.</summary>
    public Timeout(ulong now, ulong duration)
    {
        Deadline = PortableClock.CheckedDeadline(now, duration);
        _lastNow = now;
    }

    /// <summary>The exact timeout deadline.</summary>
    public ulong Deadline { get; }

    /// <summary>
    /// Polls both adapters exactly once before the deadline, using completion,
    /// unavailable-operation, cancellation, then pending precedence.
    /// </summary>
    public TimeoutObservation<T> Poll(
        ulong now,
        Func<TimeoutOperation<T>> operation,
        Func<TimeoutCancellation> cancellation)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (cancellation is null) throw new ArgumentNullException(nameof(cancellation));
        if (BeginPoll(now) is { } immediate) return immediate;
        var operationResult = operation();
        var cancellationResult = cancellation();
        return FinishPoll(operationResult, cancellationResult);
    }

    /// <summary>
    /// Task adapter. Both suppliers are started exactly once before either is
    /// awaited; neither is called on deadline or terminal fast paths.
    /// </summary>
    public async Task<TimeoutObservation<T>> PollAsync(
        ulong now,
        Func<Task<TimeoutOperation<T>>> operation,
        Func<Task<TimeoutCancellation>> cancellation)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (cancellation is null) throw new ArgumentNullException(nameof(cancellation));
        if (BeginPoll(now) is { } immediate) return immediate;
        var operationTask = StdlibTasks.InvokeTask(operation);
        var cancellationTask = StdlibTasks.InvokeTask(cancellation);
        var operationResult = await operationTask.ConfigureAwait(false);
        var cancellationResult = await cancellationTask.ConfigureAwait(false);
        return FinishPoll(operationResult, cancellationResult);
    }

    private TimeoutObservation<T>? BeginPoll(ulong now)
    {
        lock (_gate)
        {
            if (_terminal is { } terminal) return terminal;
            if (now < _lastNow)
            {
                return Latch(
                    new(
                        TimeoutOutcome.Unavailable,
                        Reason: StdlibUnavailableReason.ClockRegression));
            }
            _lastNow = now;
            return now >= Deadline
                ? Latch(new(TimeoutOutcome.TimedOut))
                : null;
        }
    }

    private TimeoutObservation<T> FinishPoll(
        TimeoutOperation<T> operation,
        TimeoutCancellation cancellation)
    {
        lock (_gate)
        {
            if (_terminal is { } terminal) return terminal;
            return operation.State switch
            {
                TimeoutOperationState.Completed =>
                    Latch(new(TimeoutOutcome.Completed, Value: operation.Value)),
                TimeoutOperationState.Unavailable =>
                    Latch(
                        new(
                            TimeoutOutcome.Unavailable,
                            Reason: StdlibUnavailableReason.OperationUnavailable)),
                TimeoutOperationState.Pending => cancellation switch
                {
                    TimeoutCancellation.Cancelled =>
                        Latch(new(TimeoutOutcome.Cancelled)),
                    TimeoutCancellation.Unavailable =>
                        Latch(
                            new(
                                TimeoutOutcome.Unavailable,
                                Reason: StdlibUnavailableReason.CancellationUnavailable)),
                    TimeoutCancellation.Pending =>
                        new(TimeoutOutcome.Pending, Deadline),
                    _ => throw new ArgumentOutOfRangeException(nameof(cancellation)),
                },
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
        }
    }

    private TimeoutObservation<T> Latch(TimeoutObservation<T> observation)
    {
        _terminal = observation;
        return observation;
    }
}

/// <summary>Externally observable revision-barrier states.</summary>
public enum RevisionBarrierOutcome
{
    /// <summary>The required revision and predicate are not both satisfied.</summary>
    Pending,

    /// <summary>The authoritative revision and predicate are satisfied.</summary>
    Satisfied,

    /// <summary>The exact deadline won.</summary>
    TimedOut,

    /// <summary>Caller-owned cancellation won.</summary>
    Cancelled,

    /// <summary>The barrier was disposed.</summary>
    Disposed,

    /// <summary>Cancellation was unavailable.</summary>
    Unavailable,
}

/// <summary>A portable revision-barrier observation.</summary>
public readonly record struct RevisionBarrierObservation(
    RevisionBarrierOutcome Outcome,
    ulong Revision,
    ulong Generation,
    StdlibUnavailableReason? Reason = null);

/// <summary>
/// Revision authority with a separate wake generation and register/recheck
/// lost-wakeup protection.
/// </summary>
public sealed class RevisionBarrier
{
    private readonly object _gate = new();
    private readonly ulong _requiredRevision;
    private readonly ulong? _deadline;
    private ulong _revision;
    private ulong _generation;
    private ulong? _lastNow;
    private RevisionBarrierObservation? _terminal;

    /// <summary>Creates a caller-driven barrier.</summary>
    public RevisionBarrier(ulong revision, ulong requiredRevision, ulong? deadline)
    {
        _revision = revision;
        _requiredRevision = requiredRevision;
        _deadline = deadline;
    }

    /// <summary>Observes deadline, predicate, then cancellation in that order.</summary>
    public RevisionBarrierObservation Observe(
        ulong now,
        bool predicate,
        Func<TimeoutCancellation> cancellation)
    {
        if (cancellation is null) throw new ArgumentNullException(nameof(cancellation));
        if (BeginObserve(now, predicate) is { } immediate) return immediate;
        return FinishCancellation(cancellation());
    }

    /// <summary>Task cancellation adapter over the same caller-driven core.</summary>
    public async Task<RevisionBarrierObservation> ObserveAsync(
        ulong now,
        bool predicate,
        Func<Task<TimeoutCancellation>> cancellation)
    {
        if (cancellation is null) throw new ArgumentNullException(nameof(cancellation));
        if (BeginObserve(now, predicate) is { } immediate) return immediate;
        return FinishCancellation(
            await StdlibTasks.InvokeTask(cancellation).ConfigureAwait(false));
    }

    /// <summary>Models waiter registration followed by an authoritative revision recheck.</summary>
    public RevisionBarrierObservation RegisterRecheck(
        ulong now,
        ulong observedRevision,
        bool predicate)
    {
        lock (_gate)
        {
            if (_terminal is { } terminal) return terminal;
            if (RejectClockRegression(now) is { } regression) return regression;
            if (_deadline is { } deadline && now >= deadline)
            {
                return Latch(RevisionBarrierOutcome.TimedOut);
            }
            AcceptRevision(observedRevision);
            return predicate && _revision >= _requiredRevision
                ? Latch(RevisionBarrierOutcome.Satisfied)
                : Snapshot();
        }
    }

    /// <summary>Accepts an increasing authoritative revision and re-evaluates the predicate.</summary>
    public RevisionBarrierObservation Advance(ulong revision, bool predicate)
    {
        lock (_gate)
        {
            if (_terminal is { } terminal) return terminal;
            AcceptRevision(revision);
            return predicate && _revision >= _requiredRevision
                ? Latch(RevisionBarrierOutcome.Satisfied)
                : Snapshot();
        }
    }

    /// <summary>Disposes and terminally wakes the barrier.</summary>
    public RevisionBarrierObservation Dispose()
    {
        lock (_gate)
        {
            return _terminal ?? Latch(RevisionBarrierOutcome.Disposed);
        }
    }

    /// <summary>Observes a receipt without granting it revision authority.</summary>
    public RevisionBarrierObservation Receipt(string key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        lock (_gate) return Snapshot();
    }

    private RevisionBarrierObservation? BeginObserve(ulong now, bool predicate)
    {
        lock (_gate)
        {
            if (_terminal is { } terminal) return terminal;
            if (RejectClockRegression(now) is { } regression) return regression;
            if (_deadline is { } deadline && now >= deadline)
            {
                return Latch(RevisionBarrierOutcome.TimedOut);
            }
            return predicate && _revision >= _requiredRevision
                ? Latch(RevisionBarrierOutcome.Satisfied)
                : null;
        }
    }

    private RevisionBarrierObservation FinishCancellation(TimeoutCancellation cancellation)
    {
        lock (_gate)
        {
            if (_terminal is { } terminal) return terminal;
            return cancellation switch
            {
                TimeoutCancellation.Cancelled => Latch(RevisionBarrierOutcome.Cancelled),
                TimeoutCancellation.Unavailable => Latch(
                    RevisionBarrierOutcome.Unavailable,
                    StdlibUnavailableReason.CancellationUnavailable),
                TimeoutCancellation.Pending => Snapshot(),
                _ => throw new ArgumentOutOfRangeException(nameof(cancellation)),
            };
        }
    }

    private void AcceptRevision(ulong candidate)
    {
        if (candidate <= _revision) return;
        _revision = candidate;
        _generation++;
    }

    private RevisionBarrierObservation? RejectClockRegression(ulong now)
    {
        if (_lastNow is { } previous && now < previous)
        {
            return Latch(
                RevisionBarrierOutcome.Unavailable,
                StdlibUnavailableReason.ClockRegression);
        }
        _lastNow = now;
        return null;
    }

    private RevisionBarrierObservation Latch(
        RevisionBarrierOutcome outcome,
        StdlibUnavailableReason? reason = null)
    {
        var observation = new RevisionBarrierObservation(
            outcome,
            _revision,
            _generation,
            reason);
        _terminal = observation;
        return observation;
    }

    private RevisionBarrierObservation Snapshot() =>
        _terminal ?? new(
            RevisionBarrierOutcome.Pending,
            _revision,
            _generation);
}

internal static class StdlibTasks
{
    internal static Task<T> InvokeTask<T>(Func<Task<T>> supplier)
    {
        try
        {
            return supplier();
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }
}
