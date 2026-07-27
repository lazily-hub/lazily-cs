namespace Lazily;

internal static class ReactiveOutput
{
    internal static Optional<T> Emit<T>(Source<Optional<T>> output, Optional<T> emitted)
    {
        if (emitted.HasValue) output.Set(emitted);
        return emitted;
    }
}

/// <summary>Emits the latest input after a quiet logical-time period.</summary>
public sealed class DebounceCell<T>
{
    private readonly long _quiet;
    private Optional<T> _pending;
    private long _fireAt;
    private bool _armed;

    /// <summary>Creates a debounce operator.</summary>
    public DebounceCell(Context context, long quiet)
    {
        ArgumentNullException.ThrowIfNull(context);
        _quiet = LogicalTime.Require(quiet, nameof(quiet));
        OutputCell = context.Source(Optional<T>.None);
    }

    /// <summary>The reactive last emitted value.</summary>
    public Source<Optional<T>> OutputCell { get; }

    /// <summary>The last emitted value.</summary>
    public Optional<T> Output => OutputCell.Get();

    /// <summary>Coalesces an input and resets the quiet deadline.</summary>
    public void Input(long now, T value)
    {
        LogicalTime.Require(now, nameof(now));
        ArgumentNullException.ThrowIfNull(value);
        _pending = Optional<T>.Some(value);
        _fireAt = LogicalTime.Add(now, _quiet);
        _armed = true;
    }

    /// <summary>Emits the pending latest value when its quiet deadline has elapsed.</summary>
    public Optional<T> Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (!_armed || !_pending.HasValue || now < _fireAt) return Optional<T>.None;
        _armed = false;
        var emitted = _pending;
        _pending = Optional<T>.None;
        return ReactiveOutput.Emit(OutputCell, emitted);
    }
}

/// <summary>The edge of a throttle window that emits.</summary>
public enum ThrottleEdge
{
    /// <summary>The first input in each window emits immediately.</summary>
    Leading,

    /// <summary>The latest input emits when the window closes.</summary>
    Trailing,
}

/// <summary>Limits output to one emission per logical-time window.</summary>
public sealed class ThrottleCell<T>
{
    private readonly ThrottleEdge _edge;
    private readonly long _window;
    private long? _windowEnd;
    private long? _windowStart;
    private Optional<T> _pending;

    /// <summary>Creates a leading- or trailing-edge throttle.</summary>
    public ThrottleCell(Context context, ThrottleEdge edge, long window)
    {
        ArgumentNullException.ThrowIfNull(context);
        _edge = edge;
        _window = LogicalTime.Require(window, nameof(window));
        OutputCell = context.Source(Optional<T>.None);
    }

    /// <summary>The reactive last emitted value.</summary>
    public Source<Optional<T>> OutputCell { get; }

    /// <summary>The last emitted value.</summary>
    public Optional<T> Output => OutputCell.Get();

    /// <summary>Processes one input according to the selected throttle edge.</summary>
    public Optional<T> Input(long now, T value)
    {
        LogicalTime.Require(now, nameof(now));
        ArgumentNullException.ThrowIfNull(value);
        if (_edge == ThrottleEdge.Leading)
        {
            if (_windowEnd is not null && now < _windowEnd) return Optional<T>.None;
            _windowEnd = LogicalTime.Add(now, _window);
            return ReactiveOutput.Emit(OutputCell, Optional<T>.Some(value));
        }

        _windowStart ??= now;
        _pending = Optional<T>.Some(value);
        return Optional<T>.None;
    }

    /// <summary>Closes a trailing window and emits its latest pending input.</summary>
    public Optional<T> Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (_edge != ThrottleEdge.Trailing || _windowStart is null) return Optional<T>.None;
        if (now < LogicalTime.Add(_windowStart.Value, _window) || !_pending.HasValue)
            return Optional<T>.None;
        _windowStart = null;
        var emitted = _pending;
        _pending = Optional<T>.None;
        return ReactiveOutput.Emit(OutputCell, emitted);
    }
}

/// <summary>The deterministic sampling strategy.</summary>
public enum SampleModeKind
{
    /// <summary>Emit every Nth input.</summary>
    Count,

    /// <summary>Emit the held latest value at period boundaries.</summary>
    Time,
}

/// <summary>A deterministic sample configuration.</summary>
public readonly record struct SampleMode(SampleModeKind Kind, long Value)
{
    /// <summary>Creates a count-based mode.</summary>
    public static SampleMode Count(long every) => new(SampleModeKind.Count, every);

    /// <summary>Creates a time-based mode.</summary>
    public static SampleMode Time(long period) => new(SampleModeKind.Time, period);
}

/// <summary>Samples by input count or logical-time boundary.</summary>
public sealed class SampleCell<T>
{
    private readonly SampleModeKind _kind;
    private readonly long _value;
    private long _counter;
    private long _next;
    private Optional<T> _held;

    /// <summary>Creates a sampler.</summary>
    public SampleCell(Context context, SampleMode mode)
    {
        ArgumentNullException.ThrowIfNull(context);
        _kind = mode.Kind;
        _value = LogicalTime.NormalizePeriod(mode.Value, nameof(mode));
        _next = _kind == SampleModeKind.Time ? _value : 0;
        OutputCell = context.Source(Optional<T>.None);
    }

    /// <summary>The reactive last emitted value.</summary>
    public Source<Optional<T>> OutputCell { get; }

    /// <summary>The last emitted value.</summary>
    public Optional<T> Output => OutputCell.Get();

    /// <summary>Processes one input, emitting only in count mode on an Nth boundary.</summary>
    public Optional<T> Input(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_kind == SampleModeKind.Time)
        {
            _held = Optional<T>.Some(value);
            return Optional<T>.None;
        }

        _counter = checked(_counter + 1);
        return _counter % _value == 0
            ? ReactiveOutput.Emit(OutputCell, Optional<T>.Some(value))
            : Optional<T>.None;
    }

    /// <summary>Emits the held latest value at a crossed time boundary.</summary>
    public Optional<T> Tick(long now)
    {
        LogicalTime.Require(now, nameof(now));
        if (_kind != SampleModeKind.Time || now < _next) return Optional<T>.None;
        var fires = checked(((now - _next) / _value) + 1);
        _next = LogicalTime.Add(_next, checked(fires * _value));
        return ReactiveOutput.Emit(OutputCell, _held);
    }
}

/// <summary>An injectable source of uniform random draws in [0, 1).</summary>
public interface ISampleRandom
{
    /// <summary>Returns the next random draw.</summary>
    double NextDouble();
}

/// <summary>A deterministic SplitMix64 random source used for reproducible sampling.</summary>
public sealed class SplitMix64Random : ISampleRandom
{
    private ulong _state;

    /// <summary>Creates a deterministic random stream.</summary>
    public SplitMix64Random(ulong seed) => _state = seed;

    /// <inheritdoc />
    public double NextDouble()
    {
        _state = unchecked(_state + 0x9E37_79B9_7F4A_7C15UL);
        var value = _state;
        value = unchecked((value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL);
        value ^= value >> 31;
        return (value >> 11) / (double)(1UL << 53);
    }
}

/// <summary>Tail-samples inputs using an injected deterministic random source.</summary>
public sealed class ProbabilisticSampleCell<T>
{
    private readonly double _rate;
    private readonly ISampleRandom _random;

    /// <summary>Creates a probabilistic sampler with a clamped rate.</summary>
    public ProbabilisticSampleCell(Context context, double rate, ISampleRandom random)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(random);
        if (double.IsNaN(rate)) throw new ArgumentOutOfRangeException(nameof(rate));
        _rate = Math.Clamp(rate, 0d, 1d);
        _random = random;
        OutputCell = context.Source(Optional<T>.None);
    }

    /// <summary>The reactive last emitted value.</summary>
    public Source<Optional<T>> OutputCell { get; }

    /// <summary>The last emitted value.</summary>
    public Optional<T> Output => OutputCell.Get();

    /// <summary>Samples an input using the owned random source.</summary>
    public Optional<T> Input(T value) => InputWithDraw(value, _random.NextDouble());

    /// <summary>Samples an input against an explicit conformance draw.</summary>
    public Optional<T> InputWithDraw(T value, double draw)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (double.IsNaN(draw) || draw < 0d || draw >= 1d)
            throw new ArgumentOutOfRangeException(nameof(draw), draw, "sample draw must be in [0, 1)");
        return draw < _rate
            ? ReactiveOutput.Emit(OutputCell, Optional<T>.Some(value))
            : Optional<T>.None;
    }
}
