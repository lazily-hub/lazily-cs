// WorkQueueCore — the graph-agnostic competing-consumer lease algebra shared by all three
// work-queue flavors (spec tag: lzqueuefamilyflavors).
//
// This is the portable local-authority lifecycle. The owning instance serializes Claim; a
// distributed/HA host puts that decision behind its leader or consensus log while preserving
// the same operation outcomes. The core performs no graph write.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Lazily;

/// <summary>Which <see cref="WorkQueueCell{T}"/> reader kinds a transition dirtied.</summary>
/// <param name="PendingLen">The pending count changed.</param>
/// <param name="IsEmpty">Pending emptiness flipped.</param>
/// <param name="InFlightLen">The live-lease count changed.</param>
/// <param name="DeadLetterLen">The dead-letter count changed.</param>
public readonly record struct WorkQueueInvalidates(
    bool PendingLen,
    bool IsEmpty,
    bool InFlightLen,
    bool DeadLetterLen)
{
    /// <summary>A rejected op changes nothing and dirties nothing.</summary>
    public static WorkQueueInvalidates None => default;

    /// <summary>Whether any reader kind was dirtied.</summary>
    public bool Any => PendingLen || IsEmpty || InFlightLen || DeadLetterLen;
}

/// <summary>
/// The graph-agnostic work-queue algebra: a pending FIFO, in-flight leases keyed by delivery
/// id, and a dead-letter tail.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class WorkQueueCore<T>
{
    private readonly Queue<WorkQueueItem<T>> _pending = new();
    private readonly SortedDictionary<long, WorkQueueDelivery<T>> _inFlight = new();
    private readonly List<WorkQueueDeadLetter<T>> _deadLetters = [];
    private readonly long _visibilityTimeout;
    private readonly int _maxDeliveries;
    private long _nextItemId;
    private long _nextDeliveryId;

    /// <summary>Creates an empty local-authority work-queue core.</summary>
    /// <param name="visibilityTimeout">Lease duration added to the claim clock.</param>
    /// <param name="maxDeliveries">Delivery budget before an item dead-letters.</param>
    public WorkQueueCore(long visibilityTimeout, int maxDeliveries)
    {
        if (visibilityTimeout <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(visibilityTimeout), visibilityTimeout, "visibility timeout must be positive");
        }
        if (maxDeliveries < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDeliveries), maxDeliveries, "max deliveries must be at least one");
        }
        _visibilityTimeout = visibilityTimeout;
        _maxDeliveries = maxDeliveries;
    }

    /// <summary>Number of items waiting to be claimed.</summary>
    public int PendingLen => _pending.Count;

    /// <summary>Whether no item is waiting to be claimed.</summary>
    public bool IsEmpty => _pending.Count == 0;

    /// <summary>Number of live delivery leases.</summary>
    public int InFlightLen => _inFlight.Count;

    /// <summary>Number of terminal dead-letter records.</summary>
    public int DeadLetterLen => _deadLetters.Count;

    /// <summary>Non-reactive pending snapshot, oldest first.</summary>
    public IReadOnlyList<WorkQueueItem<T>> Pending() => _pending.ToArray();

    /// <summary>Non-reactive in-flight snapshot, sorted by delivery id.</summary>
    public IReadOnlyList<WorkQueueDelivery<T>> InFlight() => _inFlight.Values.ToArray();

    /// <summary>Non-reactive terminal dead-letter snapshot.</summary>
    public IReadOnlyList<WorkQueueDeadLetter<T>> DeadLetters() => _deadLetters.ToArray();

    /// <summary>Append one item and return its stable identity.</summary>
    /// <param name="value">The payload.</param>
    public (long ItemId, WorkQueueInvalidates Invalidates) Push(T value)
    {
        var wasEmpty = _pending.Count == 0;
        var itemId = _nextItemId++;
        _pending.Enqueue(new WorkQueueItem<T>(itemId, value, 0));
        return (itemId, new WorkQueueInvalidates(true, wasEmpty, false, false));
    }

    /// <summary>Claim the oldest pending item for a worker, or null when empty.</summary>
    /// <param name="worker">The claiming worker identity.</param>
    /// <param name="now">The claim clock.</param>
    public (WorkQueueDelivery<T>? Delivery, WorkQueueInvalidates Invalidates) Claim(
        string worker, long now)
    {
        Guard.NotNull(worker, nameof(worker));
        if (_pending.Count == 0) return (null, WorkQueueInvalidates.None);

        var wasLast = _pending.Count == 1;
        var item = _pending.Dequeue();
        var delivery = new WorkQueueDelivery<T>(
            _nextDeliveryId++,
            item.ItemId,
            item.Value,
            worker,
            item.Attempts + 1,
            checked(now + _visibilityTimeout));
        _inFlight.Add(delivery.DeliveryId, delivery);
        return (delivery, new WorkQueueInvalidates(true, wasLast, true, false));
    }

    /// <summary>Settle a matching live delivery. Wrong-worker and duplicate acks are no-ops.</summary>
    /// <param name="worker">The acknowledging worker identity.</param>
    /// <param name="deliveryId">The delivery being settled.</param>
    public (bool Acked, WorkQueueInvalidates Invalidates) Ack(string worker, long deliveryId)
    {
        Guard.NotNull(worker, nameof(worker));
        if (!_inFlight.TryGetValue(deliveryId, out var delivery) ||
            !StringComparer.Ordinal.Equals(delivery.Worker, worker))
        {
            return (false, WorkQueueInvalidates.None);
        }

        _inFlight.Remove(deliveryId);
        return (true, new WorkQueueInvalidates(false, false, true, false));
    }

    /// <summary>Reject a live delivery, requeueing it or dead-lettering at the attempt limit.</summary>
    /// <param name="worker">The rejecting worker identity.</param>
    /// <param name="deliveryId">The delivery being rejected.</param>
    public (bool Nacked, WorkQueueInvalidates Invalidates) Nack(string worker, long deliveryId)
    {
        Guard.NotNull(worker, nameof(worker));
        if (!_inFlight.TryGetValue(deliveryId, out var delivery) ||
            !StringComparer.Ordinal.Equals(delivery.Worker, worker))
        {
            return (false, WorkQueueInvalidates.None);
        }

        _inFlight.Remove(deliveryId);
        if (delivery.Attempt >= _maxDeliveries)
        {
            _deadLetters.Add(new WorkQueueDeadLetter<T>(
                delivery.ItemId, delivery.Value, delivery.Attempt, WorkQueueDeadLetterReason.Nack));
            return (true, new WorkQueueInvalidates(false, false, true, true));
        }

        var wasEmpty = _pending.Count == 0;
        _pending.Enqueue(new WorkQueueItem<T>(delivery.ItemId, delivery.Value, delivery.Attempt));
        return (true, new WorkQueueInvalidates(true, wasEmpty, true, false));
    }

    /// <summary>
    /// Requeue or dead-letter leases whose deadline is strictly before <paramref name="now"/>,
    /// in delivery-id order.
    /// </summary>
    /// <param name="now">The reaping clock.</param>
    public (int Expired, WorkQueueInvalidates Invalidates) ReapExpired(long now)
    {
        var expired = _inFlight.Values
            .Where(delivery => delivery.Deadline < now)
            .OrderBy(delivery => delivery.DeliveryId)
            .ToArray();
        if (expired.Length == 0) return (0, WorkQueueInvalidates.None);

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
                    delivery.ItemId, delivery.Value, delivery.Attempt));
            }
        }

        return (
            expired.Length,
            new WorkQueueInvalidates(
                PendingLen: pendingBefore != _pending.Count,
                IsEmpty: (pendingBefore == 0) != (_pending.Count == 0),
                InFlightLen: true,
                DeadLetterLen: deadBefore != _deadLetters.Count));
    }
}
