using System.Collections.Generic;

namespace Lazily;

/// <summary>Outcome of a push attempt.</summary>
public enum QueuePushResult
{
    /// <summary>Accepted into the queue.</summary>
    Ok,
    /// <summary>Bounded and at capacity; the queue is unchanged.</summary>
    Full,
    /// <summary>Closed; the queue is unchanged.</summary>
    Closed,
}

/// <summary>Outcome of a pop attempt.</summary>
public enum QueuePopStatus
{
    /// <summary>An element was removed and returned.</summary>
    Value,
    /// <summary>Open and empty — more may arrive.</summary>
    Empty,
    /// <summary>Closed and drained. Distinct from <see cref="Empty"/>: nothing more will arrive.</summary>
    Closed,
}

/// <summary>The result of a pop: a status and, when <see cref="QueuePopStatus.Value"/>, the element.</summary>
public readonly record struct QueuePopResult<T>(QueuePopStatus Status, T? Value)
{
    /// <summary>True when an element was returned.</summary>
    public bool IsValue => Status == QueuePopStatus.Value;
}

/// <summary>
/// A reactive FIFO queue (<c>#lzcsqueues</c>) — the single-threaded flavor.
/// </summary>
/// <remarks>
/// <para>
/// Invalidation is scoped to READER KIND, not to the queue as a whole. A push onto a
/// non-empty queue changes <c>Len</c> but not <c>Head</c>, so a subscriber watching only
/// the head is not woken; a pop always changes the head. That independence is the
/// contract the canonical <c>queuecell_*.json</c> corpus asserts, and it is why the
/// queue cannot simply hang its readers off one state cell.
/// </para>
/// <para>
/// The transitions themselves live in <see cref="QueueCore{T}"/> and are shared verbatim with
/// <see cref="ThreadSafeQueueCell{T}"/> and <see cref="AsyncQueueCell{T}"/>. This shell owns only
/// the reactivity: each reader kind gets its own <see cref="Source{T}"/> version cell, bumped
/// when and only when the core reports that kind dirtied — the same mechanism
/// <see cref="ReactiveMap{TKey,TValue,THandle}"/> already uses for its membership and
/// order signals. This is NOT the "poll a counter" shape the spec rules out: a reader is
/// a <see cref="Computed{T}"/> that reads its version cell to register the dependency edge
/// and then derives the real value from the core. Callers never see a version number.
/// </para>
/// <para>
/// The core sits outside the graph, so a reader has no reactive dependency other than its
/// version cell and stays memoized until the shell bumps it.
/// </para>
/// </remarks>
public sealed class QueueCell<T>
{
    private readonly Context _ctx;
    private readonly QueueCore<T> _core;

    // One version cell per reader kind. Bumping exactly the kinds the core reported as changed
    // is what keeps the kinds independent.
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

    /// <summary>Creates an unbounded queue.</summary>
    /// <param name="ctx">The owning scope.</param>
    public QueueCell(Context ctx) : this(ctx, null) { }

    /// <summary>Creates a queue, bounded when <paramref name="capacity"/> is given.</summary>
    /// <param name="ctx">The owning scope.</param>
    /// <param name="capacity">Maximum elements, or <c>null</c> for unbounded.</param>
    /// <exception cref="System.ArgumentOutOfRangeException">When <paramref name="capacity"/> is not positive.</exception>
    public QueueCell(Context ctx, int? capacity)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _core = new QueueCore<T>(capacity);
        _ctx = ctx;

        _headVersion = ctx.Source(0);
        _lenVersion = ctx.Source(0);
        _emptyVersion = ctx.Source(0);
        _fullVersion = ctx.Source(0);
        _closedVersion = ctx.Source(0);

        _head = ctx.Computed(cx => { cx.Get(_headVersion); return _core.Head; });
        _len = ctx.Computed(cx => { cx.Get(_lenVersion); return _core.Len; });
        _isEmpty = ctx.Computed(cx => { cx.Get(_emptyVersion); return _core.IsEmpty; });
        _isFull = ctx.Computed(cx => { cx.Get(_fullVersion); return _core.IsFull; });
        _isClosed = ctx.Computed(cx => { cx.Get(_closedVersion); return _core.IsClosed; });
    }

    /// <summary>Declared capacity, or <c>null</c> when unbounded. Not reactive.</summary>
    public int? Capacity => _core.Capacity;

    /// <summary>The current head, or <c>default</c> when empty. Registers a dependency.</summary>
    public T? Head() => _head.Get();

    /// <summary>The current head read through a compute view, registering the edge.</summary>
    /// <param name="ops">The compute view.</param>
    public T? Head(IComputeOps ops) => _head.Get(ops);

    /// <summary>Element count. Registers a dependency.</summary>
    public int Len() => _len.Get();

    /// <summary>Element count read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public int Len(IComputeOps ops) => _len.Get(ops);

    /// <summary>Whether the queue holds no elements. Registers a dependency.</summary>
    public bool IsEmpty() => _isEmpty.Get();

    /// <summary>Emptiness read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public bool IsEmpty(IComputeOps ops) => _isEmpty.Get(ops);

    /// <summary>Whether a bounded queue is at capacity — the backpressure signal.</summary>
    public bool IsFull() => _isFull.Get();

    /// <summary>Fullness read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public bool IsFull(IComputeOps ops) => _isFull.Get(ops);

    /// <summary>Whether the queue is closed. Registers a dependency.</summary>
    public bool IsClosed() => _isClosed.Get();

    /// <summary>Closedness read through a compute view.</summary>
    /// <param name="ops">The compute view.</param>
    public bool IsClosed(IComputeOps ops) => _isClosed.Get(ops);

    /// <summary>Non-reactive FIFO-ordered snapshot.</summary>
    public IReadOnlyList<T> Elements() => _core.Elements();

    /// <summary>Handles to the five reader kinds, for graph-level probes.</summary>
    public (Computed<T?> Head, Computed<int> Len, Computed<bool> IsEmpty, Computed<bool> IsFull,
        Computed<bool> IsClosed) ReaderHandles() => (_head, _len, _isEmpty, _isFull, _isClosed);

    /// <summary>
    /// Appends to the tail. Returns <see cref="QueuePushResult.Full"/> when bounded and at
    /// capacity, or <see cref="QueuePushResult.Closed"/> when closed; on either the queue is
    /// unchanged and NOTHING is invalidated.
    /// </summary>
    /// <param name="value">The element to append.</param>
    public QueuePushResult TryPush(T value)
    {
        var (result, invalidates) = _core.TryPush(value);
        Apply(invalidates);
        return result;
    }

    /// <summary>
    /// Removes and returns the head. <see cref="QueuePopStatus.Empty"/> when open and empty,
    /// <see cref="QueuePopStatus.Closed"/> when closed and empty — a closed non-empty queue
    /// still drains.
    /// </summary>
    public QueuePopResult<T> TryPop()
    {
        var (result, invalidates) = _core.TryPop();
        Apply(invalidates);
        return result;
    }

    /// <summary>
    /// Closes the queue. Idempotent and terminal: the first close invalidates closed readers,
    /// later ones invalidate nothing.
    /// </summary>
    public void Close() => Apply(_core.Close());

    /// <summary>
    /// Bumps exactly the reader kinds the core reported dirtied, inside one batch so a
    /// subscriber never observes a partial transition (len decremented while is_full still
    /// reads stale).
    /// </summary>
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
