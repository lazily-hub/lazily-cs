// AsyncQueueCell / AsyncTopicCell / AsyncWorkQueueCell — the AsyncContext flavor of the queue
// family (spec tag: lzqueuefamilyflavors).
//
// Same flavor-neutral cores as the other two flavors: QueueCore, TopicCore, WorkQueueCore. Only
// the graph differs.
//
// ORDERING IS NOT ASYNC-COLOURED. What a push, an advance, or a reap changed is a function of
// state the graph does not own — the FIFO, the subscriber cursors, the lease table — and nothing
// has to be awaited to decide it. Every op below is therefore SYNCHRONOUS and returns a plain
// value, and every reader body resolves with Task.FromResult: nothing in these primitives awaits.
//
// The one thing that is Task-typed is a reader READ, because on this binding an AsyncContext slot
// read is Task-typed by construction (AsyncComputed<T>.GetAsync). That is a property of the async
// graph, not of the queue algebra — the same divergence AsyncIngressCell records in AGENTS.md.
//
// Multi-root invalidation goes through AsyncContext.Batch, whose boundary is synchronous: the
// version writes inside queue their roots and the queued roots propagate once at the outermost
// exit, so one op is one frontier walk over the whole dependent cone.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Lazily;

/// <summary>
/// A reactive FIFO queue on the async graph. Reader kinds invalidate independently: a push onto
/// a non-empty queue never touches head, a pop always does.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class AsyncQueueCell<T>
{
    private readonly AsyncContext _ctx;
    private readonly QueueCore<T> _core;
    private readonly object _coreGate = new();

    private readonly AsyncSource<int> _headVersion;
    private readonly AsyncSource<int> _lenVersion;
    private readonly AsyncSource<int> _emptyVersion;
    private readonly AsyncSource<int> _fullVersion;
    private readonly AsyncSource<int> _closedVersion;
    private int _headV, _lenV, _emptyV, _fullV, _closedV;

    private readonly AsyncComputed<T?> _head;
    private readonly AsyncComputed<int> _len;
    private readonly AsyncComputed<bool> _isEmpty;
    private readonly AsyncComputed<bool> _isFull;
    private readonly AsyncComputed<bool> _isClosed;

    /// <summary>Creates an unbounded queue on the async graph.</summary>
    /// <param name="ctx">The owning async reactive scope.</param>
    public AsyncQueueCell(AsyncContext ctx) : this(ctx, null) { }

    /// <summary>Creates a queue, bounded when <paramref name="capacity"/> is given.</summary>
    /// <param name="ctx">The owning async reactive scope.</param>
    /// <param name="capacity">Maximum elements, or <c>null</c> for unbounded.</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="capacity"/> is not positive.</exception>
    public AsyncQueueCell(AsyncContext ctx, int? capacity)
    {
        Guard.NotNull(ctx, nameof(ctx));
        // Validate before minting any graph node: a rejected capacity must leave no reader behind.
        _core = new QueueCore<T>(capacity);
        _ctx = ctx;

        _headVersion = ctx.Source(0);
        _lenVersion = ctx.Source(0);
        _emptyVersion = ctx.Source(0);
        _fullVersion = ctx.Source(0);
        _closedVersion = ctx.Source(0);

        _head = ctx.Computed<T?>(compute =>
        {
            compute.Track(_headVersion);
            lock (_coreGate) return Task.FromResult(_core.Head);
        });
        _len = ctx.Computed(compute =>
        {
            compute.Track(_lenVersion);
            lock (_coreGate) return Task.FromResult(_core.Len);
        });
        _isEmpty = ctx.Computed(compute =>
        {
            compute.Track(_emptyVersion);
            lock (_coreGate) return Task.FromResult(_core.IsEmpty);
        });
        _isFull = ctx.Computed(compute =>
        {
            compute.Track(_fullVersion);
            lock (_coreGate) return Task.FromResult(_core.IsFull);
        });
        _isClosed = ctx.Computed(compute =>
        {
            compute.Track(_closedVersion);
            lock (_coreGate) return Task.FromResult(_core.IsClosed);
        });
    }

    /// <summary>Declared capacity, or <c>null</c> when unbounded. Not reactive.</summary>
    public int? Capacity { get { lock (_coreGate) return _core.Capacity; } }

    /// <summary>Reactive read: the current head, or <c>default</c> when empty.</summary>
    public Task<T?> HeadAsync() => _head.GetAsync();

    /// <summary>Reactive read: the element count.</summary>
    public Task<int> LenAsync() => _len.GetAsync();

    /// <summary>Reactive read: whether the queue holds no elements.</summary>
    public Task<bool> IsEmptyAsync() => _isEmpty.GetAsync();

    /// <summary>Reactive read: whether a bounded queue is at capacity.</summary>
    public Task<bool> IsFullAsync() => _isFull.GetAsync();

    /// <summary>Reactive read: whether the queue is closed.</summary>
    public Task<bool> IsClosedAsync() => _isClosed.GetAsync();

    /// <summary>Non-reactive FIFO-ordered snapshot.</summary>
    public IReadOnlyList<T> Elements() { lock (_coreGate) return _core.Elements(); }

    /// <summary>Handles to the five reader kinds, for graph-level probes.</summary>
    public (AsyncComputed<T?> Head, AsyncComputed<int> Len, AsyncComputed<bool> IsEmpty,
        AsyncComputed<bool> IsFull, AsyncComputed<bool> IsClosed) ReaderHandles() =>
        (_head, _len, _isEmpty, _isFull, _isClosed);

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
/// A broadcast log on the async graph whose subscribers own independent, non-destructive reactive
/// cursors.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class AsyncTopicCell<T>
{
    private readonly AsyncContext _ctx;
    private readonly TopicCore<T> _core;
    private readonly object _coreGate = new();
    private readonly object _readersGate = new();
    private readonly Dictionary<string, AsyncSource<int>> _readerVersions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _readerVersionNumbers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AsyncComputed<IReadOnlyList<T>>> _readers =
        new(StringComparer.Ordinal);

    /// <summary>Creates an empty topic on the async graph.</summary>
    /// <param name="ctx">The owning async reactive scope.</param>
    public AsyncTopicCell(AsyncContext ctx) : this(ctx, new TopicSnapshot<T>()) { }

    /// <summary>Recreates a topic on the async graph from a durable/live-state snapshot.</summary>
    /// <param name="ctx">The owning async reactive scope.</param>
    /// <param name="initial">The snapshot to restore.</param>
    public AsyncTopicCell(AsyncContext ctx, TopicSnapshot<T> initial)
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

    /// <summary>Reactive read: the unread suffix for one connected subscriber.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public Task<IReadOnlyList<T>> ReadStreamAsync(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        var reader = Reader(id);
        return reader is null
            ? Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>())
            : reader.GetAsync();
    }

    /// <summary>Reactive read: the element at the subscriber cursor, or default at the tail.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public async Task<T?> ReadAsync(string id) => (await ReadStreamAsync(id)).FirstOrDefault();

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
    public AsyncComputed<IReadOnlyList<T>>? ReaderHandle(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        return Reader(id);
    }

    /// <summary>Creates an atomic defensive snapshot suitable for restart.</summary>
    public TopicSnapshot<T> Snapshot() { lock (_coreGate) return _core.Snapshot(); }

    private AsyncComputed<IReadOnlyList<T>>? Reader(string id)
    {
        lock (_readersGate) return _readers.GetValueOrDefault(id);
    }

    private void EnsureReader(string id)
    {
        lock (_readersGate)
        {
            if (_readers.ContainsKey(id)) return;
            var version = _ctx.Source(0);
            var reader = _ctx.Computed<IReadOnlyList<T>>(compute =>
            {
                compute.Track(version);
                lock (_coreGate) return Task.FromResult(_core.ReadStream(id));
            });
            _readerVersions.Add(id, version);
            _readerVersionNumbers.Add(id, 0);
            _readers.Add(id, reader);
        }
    }

    private void Apply(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        var targets = ids.Distinct(StringComparer.Ordinal).ToArray();
        var writes = new List<(AsyncSource<int> Version, int Next)>(targets.Length);
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
/// A reactive competing-consumer queue on the async graph, with exclusive expiring delivery
/// leases.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class AsyncWorkQueueCell<T>
{
    private readonly AsyncContext _ctx;
    private readonly WorkQueueCore<T> _core;
    private readonly object _coreGate = new();

    private readonly AsyncSource<int> _pendingVersion;
    private readonly AsyncSource<int> _emptyVersion;
    private readonly AsyncSource<int> _inFlightVersion;
    private readonly AsyncSource<int> _deadLetterVersion;
    private int _pendingV, _emptyV, _inFlightV, _deadLetterV;

    private readonly AsyncComputed<int> _pendingLen;
    private readonly AsyncComputed<bool> _isEmpty;
    private readonly AsyncComputed<int> _inFlightLen;
    private readonly AsyncComputed<int> _deadLetterLen;

    /// <summary>Creates an empty local-authority work queue on the async graph.</summary>
    /// <param name="ctx">The owning async reactive scope.</param>
    /// <param name="visibilityTimeout">Lease duration added to the claim clock.</param>
    /// <param name="maxDeliveries">Delivery budget before an item dead-letters.</param>
    public AsyncWorkQueueCell(AsyncContext ctx, long visibilityTimeout, int maxDeliveries)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _core = new WorkQueueCore<T>(visibilityTimeout, maxDeliveries);
        _ctx = ctx;

        _pendingVersion = ctx.Source(0);
        _emptyVersion = ctx.Source(0);
        _inFlightVersion = ctx.Source(0);
        _deadLetterVersion = ctx.Source(0);

        _pendingLen = ctx.Computed(compute =>
        {
            compute.Track(_pendingVersion);
            lock (_coreGate) return Task.FromResult(_core.PendingLen);
        });
        _isEmpty = ctx.Computed(compute =>
        {
            compute.Track(_emptyVersion);
            lock (_coreGate) return Task.FromResult(_core.IsEmpty);
        });
        _inFlightLen = ctx.Computed(compute =>
        {
            compute.Track(_inFlightVersion);
            lock (_coreGate) return Task.FromResult(_core.InFlightLen);
        });
        _deadLetterLen = ctx.Computed(compute =>
        {
            compute.Track(_deadLetterVersion);
            lock (_coreGate) return Task.FromResult(_core.DeadLetterLen);
        });
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

    /// <summary>Reactive read: the number of items waiting to be claimed.</summary>
    public Task<int> PendingLenAsync() => _pendingLen.GetAsync();

    /// <summary>Reactive read: whether no item is waiting to be claimed.</summary>
    public Task<bool> IsEmptyAsync() => _isEmpty.GetAsync();

    /// <summary>Reactive read: the number of live delivery leases.</summary>
    public Task<int> InFlightLenAsync() => _inFlightLen.GetAsync();

    /// <summary>Reactive read: the number of terminal dead-letter records.</summary>
    public Task<int> DeadLetterLenAsync() => _deadLetterLen.GetAsync();

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
    public (AsyncComputed<int> PendingLen, AsyncComputed<bool> IsEmpty,
        AsyncComputed<int> InFlightLen, AsyncComputed<int> DeadLetterLen) ReaderHandles() =>
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
