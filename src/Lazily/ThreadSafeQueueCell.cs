// ThreadSafeQueueCell / ThreadSafeTopicCell / ThreadSafeWorkQueueCell — the lock-serialized
// flavor of the queue family (spec tag: lzqueuefamilyflavors).
//
// Same flavor-neutral cores as the single-threaded shells: QueueCore, TopicCore, WorkQueueCore.
// Only the reactivity differs, and it is minted on THIS context's graph — because the family's
// claim is that all three flavors obey ONE contract.
//
// LOCK DISCIPLINE. Two locks exist and are always taken in this order, never the other:
//
//   1. the context — taken by ThreadSafeContext.WithLock / Batch.
//   2. _coreGate   — the queue algebra.
//
// A reader's compute body runs INSIDE the context lock and takes _coreGate, which is
// context -> core. An op therefore must NOT hold _coreGate while reaching the context: every op
// below scopes its core lock to a block that ends before Apply is called. That is why Apply is a
// separate step taking an already-computed invalidation set rather than something an op does
// inline. A lock-order inversion is invisible in single-threaded tests and shows up as a hang.
//
// MULTI-ROOT INVALIDATION GOES THROUGH Batch(). One pop can dirty head, len, is_empty and
// is_full; bumping them one at a time is one frontier walk each, and a concurrent reader can
// interleave and observe len decremented while is_full still reads stale. One op is one walk.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Lazily;

/// <summary>
/// A lock-serialized reactive FIFO queue. Reader kinds invalidate independently: a push onto a
/// non-empty queue never touches head, a pop always does.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class ThreadSafeQueueCell<T>
{
    private readonly ThreadSafeContext _ctx;
    private readonly QueueCore<T> _core;
    private readonly object _coreGate = new();

    private readonly Source<int> _headVersion;
    private readonly Source<int> _lenVersion;
    private readonly Source<int> _emptyVersion;
    private readonly Source<int> _fullVersion;
    private readonly Source<int> _closedVersion;
    private int _headV, _lenV, _emptyV, _fullV, _closedV;

    private readonly Computed<T?> _head;
    private readonly Computed<int> _len;
    private readonly Computed<bool> _isEmpty;
    private readonly Computed<bool> _isFull;
    private readonly Computed<bool> _isClosed;

    /// <summary>Creates an unbounded lock-serialized queue.</summary>
    /// <param name="ctx">The owning lock-serialized reactive scope.</param>
    public ThreadSafeQueueCell(ThreadSafeContext ctx) : this(ctx, null) { }

    /// <summary>Creates a queue, bounded when <paramref name="capacity"/> is given.</summary>
    /// <param name="ctx">The owning lock-serialized reactive scope.</param>
    /// <param name="capacity">Maximum elements, or <c>null</c> for unbounded.</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="capacity"/> is not positive.</exception>
    public ThreadSafeQueueCell(ThreadSafeContext ctx, int? capacity)
    {
        Guard.NotNull(ctx, nameof(ctx));
        // Validate before minting any graph node: a rejected capacity must leave no reader behind.
        _core = new QueueCore<T>(capacity);
        _ctx = ctx;

        Source<int>? headVersion = null;
        Source<int>? lenVersion = null;
        Source<int>? emptyVersion = null;
        Source<int>? fullVersion = null;
        Source<int>? closedVersion = null;
        Computed<T?>? head = null;
        Computed<int>? len = null;
        Computed<bool>? isEmpty = null;
        Computed<bool>? isFull = null;
        Computed<bool>? isClosed = null;

        ctx.WithLock(inner =>
        {
            headVersion = inner.Source(0);
            lenVersion = inner.Source(0);
            emptyVersion = inner.Source(0);
            fullVersion = inner.Source(0);
            closedVersion = inner.Source(0);
            var hv = headVersion;
            var lv = lenVersion;
            var ev = emptyVersion;
            var fv = fullVersion;
            var cv = closedVersion;
            head = inner.Computed(cx => { cx.Get(hv); lock (_coreGate) return _core.Head; });
            len = inner.Computed(cx => { cx.Get(lv); lock (_coreGate) return _core.Len; });
            isEmpty = inner.Computed(cx => { cx.Get(ev); lock (_coreGate) return _core.IsEmpty; });
            isFull = inner.Computed(cx => { cx.Get(fv); lock (_coreGate) return _core.IsFull; });
            isClosed = inner.Computed(cx => { cx.Get(cv); lock (_coreGate) return _core.IsClosed; });
        });

        _headVersion = headVersion!;
        _lenVersion = lenVersion!;
        _emptyVersion = emptyVersion!;
        _fullVersion = fullVersion!;
        _closedVersion = closedVersion!;
        _head = head!;
        _len = len!;
        _isEmpty = isEmpty!;
        _isFull = isFull!;
        _isClosed = isClosed!;
    }

    /// <summary>Declared capacity, or <c>null</c> when unbounded. Not reactive.</summary>
    public int? Capacity { get { lock (_coreGate) return _core.Capacity; } }

    /// <summary>The current head, or <c>default</c> when empty. Registers a dependency.</summary>
    public T? Head() => _ctx.WithLock(inner => _head.Get(inner));

    /// <summary>The current head read through a compute view, registering the edge.</summary>
    /// <param name="ops">The compute view.</param>
    public T? Head(IComputeOps ops) => _head.Get(ops);

    /// <summary>Element count. Registers a dependency.</summary>
    public int Len() => _ctx.WithLock(inner => _len.Get(inner));

    /// <summary>Element count read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public int Len(IComputeOps ops) => _len.Get(ops);

    /// <summary>Whether the queue holds no elements. Registers a dependency.</summary>
    public bool IsEmpty() => _ctx.WithLock(inner => _isEmpty.Get(inner));

    /// <summary>Emptiness read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public bool IsEmpty(IComputeOps ops) => _isEmpty.Get(ops);

    /// <summary>Whether a bounded queue is at capacity — the backpressure signal.</summary>
    public bool IsFull() => _ctx.WithLock(inner => _isFull.Get(inner));

    /// <summary>Fullness read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public bool IsFull(IComputeOps ops) => _isFull.Get(ops);

    /// <summary>Whether the queue is closed. Registers a dependency.</summary>
    public bool IsClosed() => _ctx.WithLock(inner => _isClosed.Get(inner));

    /// <summary>Closedness read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public bool IsClosed(IComputeOps ops) => _isClosed.Get(ops);

    /// <summary>Non-reactive FIFO-ordered snapshot.</summary>
    public IReadOnlyList<T> Elements() { lock (_coreGate) return _core.Elements(); }

    /// <summary>Handles to the five reader kinds, for graph-level probes.</summary>
    public (Computed<T?> Head, Computed<int> Len, Computed<bool> IsEmpty, Computed<bool> IsFull,
        Computed<bool> IsClosed) ReaderHandles() => (_head, _len, _isEmpty, _isFull, _isClosed);

    /// <summary>Appends to the tail. A rejection leaves the queue unchanged and dirties nothing.</summary>
    /// <param name="value">The element to append.</param>
    public QueuePushResult TryPush(T value)
    {
        QueuePushResult result;
        QueueInvalidates invalidates;
        lock (_coreGate) (result, invalidates) = _core.TryPush(value);
        Apply(invalidates);
        return result;
    }

    /// <summary>Removes and returns the head; a closed non-empty queue still drains.</summary>
    public QueuePopResult<T> TryPop()
    {
        QueuePopResult<T> result;
        QueueInvalidates invalidates;
        lock (_coreGate) (result, invalidates) = _core.TryPop();
        Apply(invalidates);
        return result;
    }

    /// <summary>Closes the queue. Idempotent and terminal.</summary>
    public void Close()
    {
        QueueInvalidates invalidates;
        lock (_coreGate) invalidates = _core.Close();
        Apply(invalidates);
    }

    private void Apply(QueueInvalidates invalidates)
    {
        if (!invalidates.Any) return;
        _ctx.Batch(() =>
        {
            if (invalidates.Len) _lenVersion.Set(++_lenV);
            if (invalidates.IsEmpty) _emptyVersion.Set(++_emptyV);
            if (invalidates.IsFull) _fullVersion.Set(++_fullV);
            if (invalidates.Head) _headVersion.Set(++_headV);
            if (invalidates.Closed) _closedVersion.Set(++_closedV);
        });
    }
}

/// <summary>
/// A lock-serialized broadcast log whose subscribers own independent, non-destructive reactive
/// cursors.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class ThreadSafeTopicCell<T>
{
    private readonly ThreadSafeContext _ctx;
    private readonly TopicCore<T> _core;
    private readonly object _coreGate = new();
    private readonly object _readersGate = new();
    private readonly Dictionary<string, Source<int>> _readerVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _readerVersionNumbers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Computed<IReadOnlyList<T>>> _readers =
        new(StringComparer.Ordinal);

    /// <summary>Creates an empty lock-serialized topic.</summary>
    /// <param name="ctx">The owning lock-serialized reactive scope.</param>
    public ThreadSafeTopicCell(ThreadSafeContext ctx) : this(ctx, new TopicSnapshot<T>()) { }

    /// <summary>Recreates a lock-serialized topic from a durable/live-state snapshot.</summary>
    /// <param name="ctx">The owning lock-serialized reactive scope.</param>
    /// <param name="initial">The snapshot to restore.</param>
    public ThreadSafeTopicCell(ThreadSafeContext ctx, TopicSnapshot<T> initial)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _core = new TopicCore<T>(initial);
        _ctx = ctx;
        foreach (var id in _core.SubscriptionIds()) EnsureReader(id);
    }

    /// <summary>Absolute offset represented by the first retained element.</summary>
    public long BaseOffset { get { lock (_coreGate) return _core.BaseOffset; } }

    /// <summary>Absolute offset immediately after the retained log.</summary>
    public long EndOffset { get { lock (_coreGate) return _core.EndOffset; } }

    /// <summary>Creates a cursor at the tail, or reconnects an offline durable identity.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    /// <param name="durability">Whether the cursor survives disconnect.</param>
    public TopicSubscribeOutcome Subscribe(string id, TopicDurability durability)
    {
        TopicSubscribeOutcome outcome;
        IReadOnlyList<string> invalidated;
        bool created;
        lock (_coreGate) (outcome, invalidated, created) = _core.Subscribe(id, durability);
        if (created) EnsureReader(id);
        Apply(invalidated);
        return outcome;
    }

    /// <summary>Reconnects a durable identity, creating it at the current tail when unknown.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public TopicSubscribeOutcome Reconnect(string id) => Subscribe(id, TopicDurability.Durable);

    /// <summary>Disconnects one subscriber; ephemeral state is removed.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public bool Disconnect(string id)
    {
        bool disconnected;
        IReadOnlyList<string> invalidated;
        bool removed;
        lock (_coreGate) (disconnected, invalidated, removed) = _core.Disconnect(id);
        if (!disconnected) return false;
        // Invalidate BEFORE dropping the reader, so a removed ephemeral subscriber still reports
        // its own final transition.
        Apply(invalidated);
        if (removed)
        {
            lock (_readersGate)
            {
                _readers.Remove(id);
                _readerVersions.Remove(id);
                _readerVersionNumbers.Remove(id);
            }
        }
        return true;
    }

    /// <summary>Appends one element, leaving every cursor unchanged.</summary>
    /// <param name="value">The element to append.</param>
    public long Publish(T value)
    {
        long offset;
        IReadOnlyList<string> invalidated;
        lock (_coreGate) (offset, invalidated) = _core.Publish(value);
        Apply(invalidated);
        return offset;
    }

    /// <summary>Advances only the named subscriber and returns the element it passed.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public T? Advance(string id)
    {
        T? value;
        IReadOnlyList<string> invalidated;
        lock (_coreGate) (value, invalidated) = _core.Advance(id);
        Apply(invalidated);
        return value;
    }

    /// <summary>Collects the log prefix below the minimum durable cursor.</summary>
    public int CollectGarbage() { lock (_coreGate) return _core.CollectGarbage(); }

    /// <summary>Reactive unread suffix for one connected subscriber.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public IReadOnlyList<T> ReadStream(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        var reader = Reader(id);
        return reader is null ? Array.Empty<T>() : _ctx.WithLock(inner => reader.Get(inner));
    }

    /// <summary>Reactive unread suffix read through a compute view.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    /// <param name="ops">The compute view.</param>
    public IReadOnlyList<T> ReadStream(string id, IComputeOps ops)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        Guard.NotNull(ops, nameof(ops));
        var reader = Reader(id);
        return reader is null ? Array.Empty<T>() : reader.Get(ops);
    }

    /// <summary>Reactive element at the subscriber cursor, or default at the tail/offline.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public T? Read(string id) => ReadStream(id).FirstOrDefault();

    /// <summary>Non-reactive retained-log snapshot.</summary>
    public IReadOnlyList<T> Elements() { lock (_coreGate) return _core.Elements(); }

    /// <summary>Subscriber identities in stable ordinal order.</summary>
    public IReadOnlyList<string> SubscriptionIds()
    {
        lock (_coreGate) return _core.SubscriptionIds();
    }

    /// <summary>Non-reactive state for one stable subscriber.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public TopicSubscriptionSnapshot? SubscriptionState(string id)
    {
        lock (_coreGate) return _core.SubscriptionState(id);
    }

    /// <summary>Handle to one subscriber's demand-driven unread suffix.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public Computed<IReadOnlyList<T>>? ReaderHandle(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        return Reader(id);
    }

    /// <summary>Creates an atomic defensive snapshot suitable for restart.</summary>
    public TopicSnapshot<T> Snapshot() { lock (_coreGate) return _core.Snapshot(); }

    private Computed<IReadOnlyList<T>>? Reader(string id)
    {
        lock (_readersGate) return _readers.GetValueOrDefault(id);
    }

    private void EnsureReader(string id)
    {
        lock (_readersGate)
        {
            if (_readers.ContainsKey(id)) return;
            Source<int>? version = null;
            Computed<IReadOnlyList<T>>? reader = null;
            _ctx.WithLock(inner =>
            {
                version = inner.Source(0);
                var v = version;
                reader = inner.Computed<IReadOnlyList<T>>(cx =>
                {
                    cx.Get(v!);
                    lock (_coreGate) return _core.ReadStream(id);
                });
            });
            _readerVersions.Add(id, version!);
            _readerVersionNumbers.Add(id, 0);
            _readers.Add(id, reader!);
        }
    }

    private void Apply(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        var targets = ids.Distinct(StringComparer.Ordinal).ToArray();
        // Handles are resolved BEFORE the batch opens, so the batch body never re-enters the
        // reader table lock in the opposite order.
        var writes = new List<(Source<int> Version, int Next)>(targets.Length);
        lock (_readersGate)
        {
            foreach (var id in targets)
            {
                if (!_readerVersions.TryGetValue(id, out var source)) continue;
                var next = _readerVersionNumbers[id] + 1;
                _readerVersionNumbers[id] = next;
                writes.Add((source, next));
            }
        }
        if (writes.Count == 0) return;
        _ctx.Batch(() =>
        {
            foreach (var (version, next) in writes) version.Set(next);
        });
    }
}

/// <summary>
/// A lock-serialized reactive competing-consumer queue with exclusive, expiring delivery leases.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class ThreadSafeWorkQueueCell<T>
{
    private readonly ThreadSafeContext _ctx;
    private readonly WorkQueueCore<T> _core;
    private readonly object _coreGate = new();

    private readonly Source<int> _pendingVersion;
    private readonly Source<int> _emptyVersion;
    private readonly Source<int> _inFlightVersion;
    private readonly Source<int> _deadLetterVersion;
    private int _pendingV, _emptyV, _inFlightV, _deadLetterV;

    private readonly Computed<int> _pendingLen;
    private readonly Computed<bool> _isEmpty;
    private readonly Computed<int> _inFlightLen;
    private readonly Computed<int> _deadLetterLen;

    /// <summary>Creates an empty lock-serialized local-authority work queue.</summary>
    /// <param name="ctx">The owning lock-serialized reactive scope.</param>
    /// <param name="visibilityTimeout">Lease duration added to the claim clock.</param>
    /// <param name="maxDeliveries">Delivery budget before an item dead-letters.</param>
    public ThreadSafeWorkQueueCell(ThreadSafeContext ctx, long visibilityTimeout, int maxDeliveries)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _core = new WorkQueueCore<T>(visibilityTimeout, maxDeliveries);
        _ctx = ctx;

        Source<int>? pendingVersion = null;
        Source<int>? emptyVersion = null;
        Source<int>? inFlightVersion = null;
        Source<int>? deadLetterVersion = null;
        Computed<int>? pendingLen = null;
        Computed<bool>? isEmpty = null;
        Computed<int>? inFlightLen = null;
        Computed<int>? deadLetterLen = null;

        ctx.WithLock(inner =>
        {
            pendingVersion = inner.Source(0);
            emptyVersion = inner.Source(0);
            inFlightVersion = inner.Source(0);
            deadLetterVersion = inner.Source(0);
            var pv = pendingVersion;
            var ev = emptyVersion;
            var fv = inFlightVersion;
            var dv = deadLetterVersion;
            pendingLen = inner.Computed(cx => { cx.Get(pv); lock (_coreGate) return _core.PendingLen; });
            isEmpty = inner.Computed(cx => { cx.Get(ev); lock (_coreGate) return _core.IsEmpty; });
            inFlightLen = inner.Computed(cx => { cx.Get(fv); lock (_coreGate) return _core.InFlightLen; });
            deadLetterLen = inner.Computed(cx => { cx.Get(dv); lock (_coreGate) return _core.DeadLetterLen; });
        });

        _pendingVersion = pendingVersion!;
        _emptyVersion = emptyVersion!;
        _inFlightVersion = inFlightVersion!;
        _deadLetterVersion = deadLetterVersion!;
        _pendingLen = pendingLen!;
        _isEmpty = isEmpty!;
        _inFlightLen = inFlightLen!;
        _deadLetterLen = deadLetterLen!;
    }

    /// <summary>Append one item and return its stable identity.</summary>
    /// <param name="value">The payload.</param>
    public long Push(T value)
    {
        long itemId;
        WorkQueueInvalidates invalidates;
        lock (_coreGate) (itemId, invalidates) = _core.Push(value);
        Apply(invalidates);
        return itemId;
    }

    /// <summary>Claim the oldest pending item for a worker, or null when empty.</summary>
    /// <param name="worker">The claiming worker identity.</param>
    /// <param name="now">The claim clock.</param>
    public WorkQueueDelivery<T>? Claim(string worker, long now)
    {
        WorkQueueDelivery<T>? delivery;
        WorkQueueInvalidates invalidates;
        lock (_coreGate) (delivery, invalidates) = _core.Claim(worker, now);
        Apply(invalidates);
        return delivery;
    }

    /// <summary>Settle a matching live delivery. Wrong-worker and duplicate acks are no-ops.</summary>
    /// <param name="worker">The acknowledging worker identity.</param>
    /// <param name="deliveryId">The delivery being settled.</param>
    public bool Ack(string worker, long deliveryId)
    {
        bool acked;
        WorkQueueInvalidates invalidates;
        lock (_coreGate) (acked, invalidates) = _core.Ack(worker, deliveryId);
        Apply(invalidates);
        return acked;
    }

    /// <summary>Reject a live delivery, requeueing it or dead-lettering at the attempt limit.</summary>
    /// <param name="worker">The rejecting worker identity.</param>
    /// <param name="deliveryId">The delivery being rejected.</param>
    public bool Nack(string worker, long deliveryId)
    {
        bool nacked;
        WorkQueueInvalidates invalidates;
        lock (_coreGate) (nacked, invalidates) = _core.Nack(worker, deliveryId);
        Apply(invalidates);
        return nacked;
    }

    /// <summary>Requeue or dead-letter leases whose deadline is strictly before the clock.</summary>
    /// <param name="now">The reaping clock.</param>
    public int ReapExpired(long now)
    {
        int expired;
        WorkQueueInvalidates invalidates;
        lock (_coreGate) (expired, invalidates) = _core.ReapExpired(now);
        Apply(invalidates);
        return expired;
    }

    /// <summary>Number of items waiting to be claimed.</summary>
    public int PendingLen() => _ctx.WithLock(inner => _pendingLen.Get(inner));
    /// <summary>Tracked pending count read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public int PendingLen(IComputeOps ops) => _pendingLen.Get(ops);
    /// <summary>Whether no item is waiting to be claimed.</summary>
    public bool IsEmpty() => _ctx.WithLock(inner => _isEmpty.Get(inner));
    /// <summary>Tracked emptiness read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public bool IsEmpty(IComputeOps ops) => _isEmpty.Get(ops);
    /// <summary>Number of live delivery leases.</summary>
    public int InFlightLen() => _ctx.WithLock(inner => _inFlightLen.Get(inner));
    /// <summary>Tracked in-flight count read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public int InFlightLen(IComputeOps ops) => _inFlightLen.Get(ops);
    /// <summary>Number of terminal dead-letter records.</summary>
    public int DeadLetterLen() => _ctx.WithLock(inner => _deadLetterLen.Get(inner));
    /// <summary>Tracked dead-letter count read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public int DeadLetterLen(IComputeOps ops) => _deadLetterLen.Get(ops);

    /// <summary>Non-reactive pending snapshot, oldest first.</summary>
    public IReadOnlyList<WorkQueueItem<T>> Pending() { lock (_coreGate) return _core.Pending(); }

    /// <summary>Non-reactive in-flight snapshot, sorted by delivery id.</summary>
    public IReadOnlyList<WorkQueueDelivery<T>> InFlight()
    {
        lock (_coreGate) return _core.InFlight();
    }

    /// <summary>Non-reactive terminal dead-letter snapshot.</summary>
    public IReadOnlyList<WorkQueueDeadLetter<T>> DeadLetters()
    {
        lock (_coreGate) return _core.DeadLetters();
    }

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
