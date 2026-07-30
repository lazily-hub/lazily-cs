using System.Collections.Generic;

namespace Lazily;

/// <summary>A stable pending work item. Attempts counts leases already issued.</summary>
public sealed record WorkQueueItem<T>(long ItemId, T Value, int Attempts);

/// <summary>An exclusive worker-owned delivery lease.</summary>
public sealed record WorkQueueDelivery<T>(
    long DeliveryId,
    long ItemId,
    T Value,
    string Worker,
    int Attempt,
    long Deadline);

/// <summary>Why an item exhausted its delivery budget.</summary>
public enum WorkQueueDeadLetterReason
{
    /// <summary>The worker explicitly rejected the final permitted delivery.</summary>
    Nack,
    /// <summary>The final permitted delivery lease expired.</summary>
    Expired,
}

/// <summary>A terminal poison-message record.</summary>
public sealed record WorkQueueDeadLetter<T>(
    long ItemId,
    T Value,
    int Attempts,
    WorkQueueDeadLetterReason Reason);

/// <summary>
/// A reactive competing-consumer queue with exclusive, expiring delivery leases — the
/// single-threaded flavor.
/// </summary>
/// <remarks>
/// The lease algebra lives in <see cref="WorkQueueCore{T}"/> and is shared verbatim with
/// <see cref="ThreadSafeWorkQueueCell{T}"/> and <see cref="AsyncWorkQueueCell{T}"/>.
/// </remarks>
public sealed class WorkQueueCell<T>
{
    private readonly Context _ctx;
    private readonly WorkQueueCore<T> _core;

    private readonly Source<int> _pendingVersion;
    private readonly Source<int> _emptyVersion;
    private readonly Source<int> _inFlightVersion;
    private readonly Source<int> _deadLetterVersion;
    private int _pendingV;
    private int _emptyV;
    private int _inFlightV;
    private int _deadLetterV;

    private readonly Computed<int> _pendingLen;
    private readonly Computed<bool> _isEmpty;
    private readonly Computed<int> _inFlightLen;
    private readonly Computed<int> _deadLetterLen;

    /// <summary>Creates an empty local-authority work queue.</summary>
    public WorkQueueCell(Context ctx, long visibilityTimeout, int maxDeliveries)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _core = new WorkQueueCore<T>(visibilityTimeout, maxDeliveries);
        _ctx = ctx;
        _pendingVersion = ctx.Source(0);
        _emptyVersion = ctx.Source(0);
        _inFlightVersion = ctx.Source(0);
        _deadLetterVersion = ctx.Source(0);
        _pendingLen = ctx.Computed(cx => { cx.Get(_pendingVersion); return _core.PendingLen; });
        _isEmpty = ctx.Computed(cx => { cx.Get(_emptyVersion); return _core.IsEmpty; });
        _inFlightLen = ctx.Computed(cx => { cx.Get(_inFlightVersion); return _core.InFlightLen; });
        _deadLetterLen = ctx.Computed(cx => { cx.Get(_deadLetterVersion); return _core.DeadLetterLen; });
    }

    /// <summary>Append one item and return its stable identity.</summary>
    public long Push(T value)
    {
        var (itemId, invalidates) = _core.Push(value);
        Apply(invalidates);
        return itemId;
    }

    /// <summary>Claim the oldest pending item for a worker, or null when empty.</summary>
    public WorkQueueDelivery<T>? Claim(string worker, long now)
    {
        var (delivery, invalidates) = _core.Claim(worker, now);
        Apply(invalidates);
        return delivery;
    }

    /// <summary>Settle a matching live delivery. Wrong-worker and duplicate acks are no-ops.</summary>
    public bool Ack(string worker, long deliveryId)
    {
        var (acked, invalidates) = _core.Ack(worker, deliveryId);
        Apply(invalidates);
        return acked;
    }

    /// <summary>Reject a live delivery, requeueing it or dead-lettering at the attempt limit.</summary>
    public bool Nack(string worker, long deliveryId)
    {
        var (nacked, invalidates) = _core.Nack(worker, deliveryId);
        Apply(invalidates);
        return nacked;
    }

    /// <summary>
    /// Requeue or dead-letter leases whose deadline is strictly before <paramref name="now"/>.
    /// </summary>
    public int ReapExpired(long now)
    {
        var (expired, invalidates) = _core.ReapExpired(now);
        Apply(invalidates);
        return expired;
    }

    /// <summary>Number of items waiting to be claimed.</summary>
    public int PendingLen() => _pendingLen.Get();
    /// <summary>Tracked pending count read through a compute view.</summary>
    public int PendingLen(IComputeOps ops) => _pendingLen.Get(ops);
    /// <summary>Whether no item is waiting to be claimed.</summary>
    public bool IsEmpty() => _isEmpty.Get();
    /// <summary>Tracked emptiness read through a compute view.</summary>
    public bool IsEmpty(IComputeOps ops) => _isEmpty.Get(ops);
    /// <summary>Number of live delivery leases.</summary>
    public int InFlightLen() => _inFlightLen.Get();
    /// <summary>Tracked in-flight count read through a compute view.</summary>
    public int InFlightLen(IComputeOps ops) => _inFlightLen.Get(ops);
    /// <summary>Number of terminal dead-letter records.</summary>
    public int DeadLetterLen() => _deadLetterLen.Get();
    /// <summary>Tracked dead-letter count read through a compute view.</summary>
    public int DeadLetterLen(IComputeOps ops) => _deadLetterLen.Get(ops);

    /// <summary>Non-reactive pending snapshot, oldest first.</summary>
    public IReadOnlyList<WorkQueueItem<T>> Pending() => _core.Pending();

    /// <summary>Non-reactive in-flight snapshot, sorted by delivery id.</summary>
    public IReadOnlyList<WorkQueueDelivery<T>> InFlight() => _core.InFlight();

    /// <summary>Non-reactive terminal dead-letter snapshot.</summary>
    public IReadOnlyList<WorkQueueDeadLetter<T>> DeadLetters() => _core.DeadLetters();

    /// <summary>Handles to the four reader kinds, for graph-level probes.</summary>
    public (Computed<int> PendingLen, Computed<bool> IsEmpty, Computed<int> InFlightLen,
        Computed<int> DeadLetterLen) ReaderHandles() =>
        (_pendingLen, _isEmpty, _inFlightLen, _deadLetterLen);

    private void Apply(WorkQueueInvalidates invalidates)
    {
        if (!invalidates.Any) return;
        _ctx.Batch(() =>
        {
            if (invalidates.PendingLen) _pendingVersion.Set(++_pendingV);
            if (invalidates.IsEmpty) _emptyVersion.Set(++_emptyV);
            if (invalidates.InFlightLen) _inFlightVersion.Set(++_inFlightV);
            if (invalidates.DeadLetterLen) _deadLetterVersion.Set(++_deadLetterV);
        });
    }
}
