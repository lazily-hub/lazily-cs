namespace Lazily;

internal static class WindowFold
{
    internal static Optional<T> Merge<T>(Optional<T> accumulator, T value, MergePolicy<T> policy) =>
        accumulator.HasValue
            ? Optional<T>.Some(policy.Merge(accumulator.Value, value))
            : Optional<T>.Some(value);

    internal static Optional<T> Fold<T>(IEnumerable<T> values, MergePolicy<T> policy)
    {
        var accumulator = Optional<T>.None;
        foreach (var value in values) accumulator = Merge(accumulator, value, policy);
        return accumulator;
    }
}

/// <summary>A count-based fixed non-overlapping aggregation window.</summary>
public sealed class TumblingCountWindow<T>
{
    private readonly long _size;
    private readonly MergePolicy<T> _policy;
    private Optional<T> _accumulator;
    private long _count;

    /// <summary>Creates a count-based tumbling window.</summary>
    public TumblingCountWindow(Context context, long size, MergePolicy<T> policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        _size = LogicalTime.NormalizePeriod(size, nameof(size));
        _policy = policy;
        OutputCell = context.Source(Optional<T>.None);
    }

    /// <summary>The reactive last emitted aggregate.</summary>
    public Source<Optional<T>> OutputCell { get; }

    /// <summary>The last emitted aggregate.</summary>
    public Optional<T> Output => OutputCell.Get();

    /// <summary>Accumulates a value and emits on the configured count boundary.</summary>
    public Optional<T> Push(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _accumulator = WindowFold.Merge(_accumulator, value, _policy);
        _count = checked(_count + 1);
        if (_count < _size) return Optional<T>.None;
        _count = 0;
        var emitted = _accumulator;
        _accumulator = Optional<T>.None;
        return ReactiveOutput.Emit(OutputCell, emitted);
    }
}

/// <summary>A logical-time fixed non-overlapping aggregation window.</summary>
public sealed class TumblingTimeWindow<T>
{
    private readonly long _period;
    private readonly MergePolicy<T> _policy;
    private long _next;
    private Optional<T> _accumulator;

    /// <summary>Creates a time-based tumbling window.</summary>
    public TumblingTimeWindow(Context context, long period, MergePolicy<T> policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        _period = LogicalTime.NormalizePeriod(period, nameof(period));
        _policy = policy;
        _next = _period;
        OutputCell = context.Source(Optional<T>.None);
    }

    /// <summary>The reactive last emitted aggregate.</summary>
    public Source<Optional<T>> OutputCell { get; }

    /// <summary>The last emitted aggregate.</summary>
    public Optional<T> Output => OutputCell.Get();

    /// <summary>Accumulates a value in the current time window.</summary>
    public void Push(long now, T value)
    {
        LogicalTime.Require(now, nameof(now));
        ArgumentNullException.ThrowIfNull(value);
        _accumulator = WindowFold.Merge(_accumulator, value, _policy);
    }

    /// <summary>Closes every crossed boundary and emits the non-empty current aggregate.</summary>
    public Optional<T> Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (now < _next) return Optional<T>.None;
        var fires = checked(((now - _next) / _period) + 1);
        _next = LogicalTime.Add(_next, checked(fires * _period));
        var emitted = _accumulator;
        _accumulator = Optional<T>.None;
        return ReactiveOutput.Emit(OutputCell, emitted);
    }
}

/// <summary>An overlapping count window that emits every configured slide.</summary>
public sealed class SlidingWindow<T>
{
    private readonly int _size;
    private readonly long _slide;
    private readonly MergePolicy<T> _policy;
    private readonly Queue<T> _values = new();
    private long _sinceEmission;

    /// <summary>Creates a sliding count window.</summary>
    public SlidingWindow(Context context, int size, long slide, MergePolicy<T> policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        _size = Math.Max(1, size);
        _slide = LogicalTime.NormalizePeriod(slide, nameof(slide));
        _policy = policy;
        OutputCell = context.Source(Optional<T>.None);
    }

    /// <summary>The reactive last emitted aggregate.</summary>
    public Source<Optional<T>> OutputCell { get; }

    /// <summary>The last emitted aggregate.</summary>
    public Optional<T> Output => OutputCell.Get();

    /// <summary>Retains the newest value and emits the current fold on each slide boundary.</summary>
    public Optional<T> Push(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _values.Enqueue(value);
        while (_values.Count > _size) _ = _values.Dequeue();
        _sinceEmission = checked(_sinceEmission + 1);
        if (_sinceEmission < _slide) return Optional<T>.None;
        _sinceEmission = 0;
        return ReactiveOutput.Emit(OutputCell, WindowFold.Fold(_values, _policy));
    }
}

/// <summary>A gap-based session aggregation window.</summary>
public sealed class SessionWindow<T>
{
    private readonly long _gap;
    private readonly MergePolicy<T> _policy;
    private Optional<T> _accumulator;
    private long? _lastInput;

    /// <summary>Creates a session window.</summary>
    public SessionWindow(Context context, long gap, MergePolicy<T> policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        _gap = LogicalTime.Require(gap, nameof(gap));
        _policy = policy;
        OutputCell = context.Source(Optional<T>.None);
    }

    /// <summary>The reactive last emitted aggregate.</summary>
    public Source<Optional<T>> OutputCell { get; }

    /// <summary>The last emitted aggregate.</summary>
    public Optional<T> Output => OutputCell.Get();

    /// <summary>Accumulates within a session or closes the prior session after an idle gap.</summary>
    public Optional<T> Push(long now, T value)
    {
        LogicalTime.Require(now, nameof(now));
        ArgumentNullException.ThrowIfNull(value);
        var idleBreak = _lastInput is not null
            && now - _lastInput.Value > _gap
            && _accumulator.HasValue;
        _lastInput = now;
        if (idleBreak)
        {
            var emitted = _accumulator;
            _accumulator = Optional<T>.Some(value);
            return ReactiveOutput.Emit(OutputCell, emitted);
        }

        _accumulator = WindowFold.Merge(_accumulator, value, _policy);
        return Optional<T>.None;
    }

    /// <summary>Closes an open session only after it has exceeded the idle gap.</summary>
    public Optional<T> Flush(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (_lastInput is null || now - _lastInput.Value <= _gap || !_accumulator.HasValue)
            return Optional<T>.None;
        var emitted = _accumulator;
        _accumulator = Optional<T>.None;
        return ReactiveOutput.Emit(OutputCell, emitted);
    }
}
