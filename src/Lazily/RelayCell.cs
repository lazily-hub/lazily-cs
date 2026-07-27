using System;

namespace Lazily;

/// <summary>The quantity measured by a relay backpressure bound.</summary>
public enum BoundDimension
{
    /// <summary>Number of ingress operations merged into the current hot window.</summary>
    Count,

    /// <summary>Encoded byte size.</summary>
    Bytes,

    /// <summary>Number of distinct keys.</summary>
    Keys,

    /// <summary>Age under a logical clock.</summary>
    Age,
}

/// <summary>Action applied when a relay reaches its high-water mark.</summary>
public enum RelayOverflow
{
    /// <summary>Refuse ingress so the producer observes backpressure.</summary>
    Block,

    /// <summary>Discard the accumulated window and start a new one from the incoming operation.</summary>
    DropOldest,

    /// <summary>Discard the incoming operation.</summary>
    DropNewest,

    /// <summary>Continue merging because the algebraic summary is the bound.</summary>
    Conflate,

    /// <summary>Page the accumulated window to a durable tail.</summary>
    Spill,
}

/// <summary>Outcome of one relay ingress operation.</summary>
public enum RelayIngressOutcome
{
    /// <summary>The operation opened an empty hot window.</summary>
    Accepted,

    /// <summary>The operation merged into a non-empty hot window.</summary>
    Conflated,

    /// <summary>The selected lossy policy discarded data.</summary>
    Dropped,

    /// <summary>The block policy refused the operation.</summary>
    Blocked,
}

/// <summary>Reactive limits and overflow selection for a relay.</summary>
public sealed class BackpressurePolicy
{
    /// <summary>Creates a live policy whose fields can be retuned through their source cells.</summary>
    public BackpressurePolicy(
        Context ctx,
        BoundDimension dimension,
        ulong highWater,
        ulong lowWater,
        RelayOverflow overflow)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (highWater == 0)
            throw new ArgumentOutOfRangeException(
                nameof(highWater),
                highWater,
                "high-water mark must be positive");
        if (lowWater >= highWater)
            throw new ArgumentOutOfRangeException(
                nameof(lowWater),
                lowWater,
                "low-water mark must be below the high-water mark");

        Dimension = ctx.Source(dimension);
        HighWater = ctx.Source(highWater);
        LowWater = ctx.Source(lowWater);
        Overflow = ctx.Source(overflow);
    }

    /// <summary>Reactive bound dimension.</summary>
    public Source<BoundDimension> Dimension { get; }

    /// <summary>Reactive ingress gate threshold.</summary>
    public Source<ulong> HighWater { get; }

    /// <summary>Reactive hysteresis re-open threshold.</summary>
    public Source<ulong> LowWater { get; }

    /// <summary>Reactive overflow action.</summary>
    public Source<RelayOverflow> Overflow { get; }
}

/// <summary>
/// Algebra-typed conflating relay: a reactive hot window adapting fast ingress to slow egress.
/// </summary>
public sealed class RelayCell<T>
{
    private readonly record struct Head(T? Value, bool Present);

    private readonly Context _ctx;
    private readonly BackpressurePolicy _policy;
    private MergePolicy<T> _merge;
    private readonly Source<Head> _head;
    private readonly Source<ulong> _pending;
    private readonly Source<ulong> _dropped;
    private readonly Source<ulong> _conflated;
    private readonly Computed<ulong> _depth;
    private readonly Computed<bool> _isFull;
    private readonly Computed<bool> _isEmpty;
    private readonly Computed<bool> _canReconfigure;

    /// <summary>Creates an in-process, count-metered relay and validates its algebra/policy pair.</summary>
    public RelayCell(
        Context ctx,
        BackpressurePolicy policy,
        MergePolicy<T> merge)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(merge);
        ValidatePolicy(policy, merge, construction: true);

        _ctx = ctx;
        _policy = policy;
        _merge = merge;
        _head = ctx.Source(new Head(default, Present: false));
        _pending = ctx.Source(0UL);
        _dropped = ctx.Source(0UL);
        _conflated = ctx.Source(0UL);
        _depth = ctx.Computed(cx => cx.Get(_pending));
        _isFull = ctx.Computed(cx => cx.Get(_depth) >= cx.Get(policy.HighWater));
        _isEmpty = ctx.Computed(cx => !cx.Get(_head).Present);
        _canReconfigure = ctx.Computed(cx => !cx.Get(_head).Present);
    }

    /// <summary>Current hot-window operation count.</summary>
    public ulong Depth() => _depth.Get();

    /// <summary>Tracked hot-window operation count.</summary>
    public ulong Depth(IComputeOps ops) => _depth.Get(ops);

    /// <summary>Whether ingress has reached the current high-water mark.</summary>
    public bool IsFull() => _isFull.Get();

    /// <summary>Tracked fullness.</summary>
    public bool IsFull(IComputeOps ops) => _isFull.Get(ops);

    /// <summary>Whether there is no hot value to drain.</summary>
    public bool IsEmpty() => _isEmpty.Get();

    /// <summary>Tracked emptiness.</summary>
    public bool IsEmpty(IComputeOps ops) => _isEmpty.Get(ops);

    /// <summary>Number of lossy overflow actions.</summary>
    public ulong Dropped() => _dropped.Get();

    /// <summary>Tracked lossy overflow count.</summary>
    public ulong Dropped(IComputeOps ops) => _dropped.Get(ops);

    /// <summary>Number of operations merged into an already non-empty window.</summary>
    public ulong Conflated() => _conflated.Get();

    /// <summary>Tracked conflation count.</summary>
    public ulong Conflated(IComputeOps ops) => _conflated.Get(ops);

    /// <summary>Whether the hot window is empty, allowing its merge policy to change safely.</summary>
    public bool CanReconfigure() => _canReconfigure.Get();

    /// <summary>Tracked merge-reconfiguration readiness.</summary>
    public bool CanReconfigure(IComputeOps ops) => _canReconfigure.Get(ops);

    /// <summary>Whether the current live overflow selection is legal for this relay.</summary>
    public bool OverflowIsLegal()
    {
        var overflow = _policy.Overflow.Peek();
        return overflow switch
        {
            RelayOverflow.Conflate => _merge.Conflates,
            RelayOverflow.Spill => false,
            _ => true,
        };
    }

    /// <summary>Ingests one operation under the current reactive overflow policy.</summary>
    public RelayIngressOutcome Ingress(T operation)
    {
        ValidateLivePolicy();
        var wasEmpty = _pending.Peek() == 0;
        if (IsFullUntracked())
        {
            switch (_policy.Overflow.Peek())
            {
                case RelayOverflow.Block:
                    return RelayIngressOutcome.Blocked;
                case RelayOverflow.DropNewest:
                    _dropped.Set(checked(_dropped.Peek() + 1));
                    return RelayIngressOutcome.Dropped;
                case RelayOverflow.DropOldest:
                    _ctx.Batch(() =>
                    {
                        _head.Set(new Head(operation, Present: true));
                        _pending.Set(1);
                        _dropped.Set(checked(_dropped.Peek() + 1));
                    });
                    return RelayIngressOutcome.Dropped;
                case RelayOverflow.Conflate:
                    break;
                case RelayOverflow.Spill:
                    throw new NotSupportedException(
                        "Spill overflow requires the staged SpillStore integration");
                default:
                    throw new InvalidOperationException("unknown relay overflow policy");
            }
        }

        _ctx.Batch(() =>
        {
            var head = _head.Peek();
            var next = head.Present ? _merge.Merge(head.Value!, operation) : operation;
            _head.Set(new Head(next, Present: true));
            _pending.Set(checked(_pending.Peek() + 1));
            if (!wasEmpty) _conflated.Set(checked(_conflated.Peek() + 1));
        });
        return wasEmpty ? RelayIngressOutcome.Accepted : RelayIngressOutcome.Conflated;
    }

    /// <summary>Drains the coalesced hot window, returning false when it is empty.</summary>
    public bool TryDrain(out T? value)
    {
        var head = _head.Peek();
        value = head.Value;
        if (!head.Present) return false;
        _ctx.Batch(() =>
        {
            _head.Set(new Head(default, Present: false));
            _pending.Set(0);
        });
        return true;
    }

    /// <summary>Reads the coalesced hot window without draining it.</summary>
    public bool TryPeek(out T? value)
    {
        var head = _head.Peek();
        value = head.Value;
        return head.Present;
    }

    /// <summary>
    /// Swaps the merge policy only while the hot window is empty and the current overflow remains
    /// legal. Returns false when a live window makes reconfiguration unsafe.
    /// </summary>
    public bool TryReconfigure(MergePolicy<T> merge)
    {
        ArgumentNullException.ThrowIfNull(merge);
        if (_head.Peek().Present) return false;
        ValidatePolicy(_policy, merge, construction: false);
        _merge = merge;
        return true;
    }

    private bool IsFullUntracked() => _pending.Peek() >= _policy.HighWater.Peek();

    private void ValidateLivePolicy()
    {
        if (_policy.Dimension.Peek() != BoundDimension.Count)
            throw new InvalidOperationException(
                "this RelayCell implementation currently meters only Count");
        if (_policy.HighWater.Peek() == 0 ||
            _policy.LowWater.Peek() >= _policy.HighWater.Peek())
            throw new InvalidOperationException(
                "relay watermarks must satisfy 0 <= low-water < high-water");
        if (!OverflowIsLegal())
            throw new InvalidOperationException(
                _policy.Overflow.Peek() == RelayOverflow.Spill
                    ? "Spill overflow requires the staged SpillStore integration"
                    : "Conflate overflow requires a conflating merge policy");
    }

    private static void ValidatePolicy(
        BackpressurePolicy policy,
        MergePolicy<T> merge,
        bool construction)
    {
        if (policy.Dimension.Peek() != BoundDimension.Count)
            throw new NotSupportedException(
                "this RelayCell implementation currently meters only Count");
        if (policy.Overflow.Peek() == RelayOverflow.Spill)
            throw new NotSupportedException(
                "Spill overflow requires the staged SpillStore integration");
        if (policy.Overflow.Peek() == RelayOverflow.Conflate && !merge.Conflates)
            throw new ArgumentException(
                "Conflate overflow requires a conflating merge policy",
                construction ? nameof(merge) : null);
    }
}
