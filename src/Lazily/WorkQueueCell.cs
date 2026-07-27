using System;
using System.Collections.Generic;
using System.Linq;

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
/// A reactive competing-consumer queue with exclusive, expiring delivery leases.
/// </summary>
public sealed class WorkQueueCell<T>
{
    private readonly Context _ctx;
    private readonly Queue<WorkQueueItem<T>> _pending = new();
    private readonly SortedDictionary<long, WorkQueueDelivery<T>> _inFlight = new();
    private readonly List<WorkQueueDeadLetter<T>> _deadLetters = [];
    private readonly long _visibilityTimeout;
    private readonly int _maxDeliveries;
    private long _nextItemId;
    private long _nextDeliveryId;

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
        if (visibilityTimeout <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(visibilityTimeout),
                visibilityTimeout,
                "visibility timeout must be positive");
        if (maxDeliveries < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maxDeliveries),
                maxDeliveries,
                "max deliveries must be at least one");

        _ctx = ctx;
        _visibilityTimeout = visibilityTimeout;
        _maxDeliveries = maxDeliveries;
        _pendingVersion = ctx.Source(0);
        _emptyVersion = ctx.Source(0);
        _inFlightVersion = ctx.Source(0);
        _deadLetterVersion = ctx.Source(0);
        _pendingLen = ctx.Computed(cx =>
        {
            cx.Get(_pendingVersion);
            return _pending.Count;
        });
        _isEmpty = ctx.Computed(cx =>
        {
            cx.Get(_emptyVersion);
            return _pending.Count == 0;
        });
        _inFlightLen = ctx.Computed(cx =>
        {
            cx.Get(_inFlightVersion);
            return _inFlight.Count;
        });
        _deadLetterLen = ctx.Computed(cx =>
        {
            cx.Get(_deadLetterVersion);
            return _deadLetters.Count;
        });
    }

    /// <summary>Append one item and return its stable identity.</summary>
    public long Push(T value)
    {
        var wasEmpty = _pending.Count == 0;
        var itemId = _nextItemId++;
        _pending.Enqueue(new WorkQueueItem<T>(itemId, value, 0));
        Invalidate(pending: true, empty: wasEmpty, inFlight: false, deadLetters: false);
        return itemId;
    }

    /// <summary>Claim the oldest pending item for a worker, or null when empty.</summary>
    public WorkQueueDelivery<T>? Claim(string worker, long now)
    {
        Guard.NotNull(worker, nameof(worker));
        if (_pending.Count == 0) return null;

        var wasLast = _pending.Count == 1;
        var item = _pending.Dequeue();
        var attempt = item.Attempts + 1;
        var delivery = new WorkQueueDelivery<T>(
            _nextDeliveryId++,
            item.ItemId,
            item.Value,
            worker,
            attempt,
            checked(now + _visibilityTimeout));
        _inFlight.Add(delivery.DeliveryId, delivery);
        Invalidate(pending: true, empty: wasLast, inFlight: true, deadLetters: false);
        return delivery;
    }

    /// <summary>Settle a matching live delivery. Wrong-worker and duplicate acks are no-ops.</summary>
    public bool Ack(string worker, long deliveryId)
    {
        Guard.NotNull(worker, nameof(worker));
        if (!_inFlight.TryGetValue(deliveryId, out var delivery) ||
            !StringComparer.Ordinal.Equals(delivery.Worker, worker))
            return false;

        _inFlight.Remove(deliveryId);
        Invalidate(pending: false, empty: false, inFlight: true, deadLetters: false);
        return true;
    }

    /// <summary>Reject a live delivery, requeueing it or dead-lettering at the attempt limit.</summary>
    public bool Nack(string worker, long deliveryId)
    {
        Guard.NotNull(worker, nameof(worker));
        if (!_inFlight.TryGetValue(deliveryId, out var delivery) ||
            !StringComparer.Ordinal.Equals(delivery.Worker, worker))
            return false;

        _inFlight.Remove(deliveryId);
        if (delivery.Attempt >= _maxDeliveries)
        {
            _deadLetters.Add(new WorkQueueDeadLetter<T>(
                delivery.ItemId,
                delivery.Value,
                delivery.Attempt,
                WorkQueueDeadLetterReason.Nack));
            Invalidate(pending: false, empty: false, inFlight: true, deadLetters: true);
        }
        else
        {
            var wasEmpty = _pending.Count == 0;
            _pending.Enqueue(new WorkQueueItem<T>(
                delivery.ItemId,
                delivery.Value,
                delivery.Attempt));
            Invalidate(pending: true, empty: wasEmpty, inFlight: true, deadLetters: false);
        }
        return true;
    }

    /// <summary>
    /// Requeue or dead-letter leases whose deadline is strictly before <paramref name="now"/>.
    /// </summary>
    public int ReapExpired(long now)
    {
        var expired = _inFlight.Values
            .Where(delivery => delivery.Deadline < now)
            .OrderBy(delivery => delivery.DeliveryId)
            .ToArray();
        if (expired.Length == 0) return 0;

        var pendingBefore = _pending.Count;
        var deadBefore = _deadLetters.Count;
        foreach (var delivery in expired)
        {
            _inFlight.Remove(delivery.DeliveryId);
            if (delivery.Attempt >= _maxDeliveries)
            {
                _deadLetters.Add(new WorkQueueDeadLetter<T>(
                    delivery.ItemId,
                    delivery.Value,
                    delivery.Attempt,
                    WorkQueueDeadLetterReason.Expired));
            }
            else
            {
                _pending.Enqueue(new WorkQueueItem<T>(
                    delivery.ItemId,
                    delivery.Value,
                    delivery.Attempt));
            }
        }

        Invalidate(
            pending: pendingBefore != _pending.Count,
            empty: (pendingBefore == 0) != (_pending.Count == 0),
            inFlight: true,
            deadLetters: deadBefore != _deadLetters.Count);
        return expired.Length;
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
    public IReadOnlyList<WorkQueueItem<T>> Pending() => _pending.ToArray();

    /// <summary>Non-reactive in-flight snapshot, sorted by delivery id.</summary>
    public IReadOnlyList<WorkQueueDelivery<T>> InFlight() => _inFlight.Values.ToArray();

    /// <summary>Non-reactive terminal dead-letter snapshot.</summary>
    public IReadOnlyList<WorkQueueDeadLetter<T>> DeadLetters() => _deadLetters.ToArray();

    private void Invalidate(bool pending, bool empty, bool inFlight, bool deadLetters)
    {
        _ctx.Batch(() =>
        {
            if (pending) _pendingVersion.Set(++_pendingV);
            if (empty) _emptyVersion.Set(++_emptyV);
            if (inFlight) _inFlightVersion.Set(++_inFlightV);
            if (deadLetters) _deadLetterVersion.Set(++_deadLetterV);
        });
    }
}
