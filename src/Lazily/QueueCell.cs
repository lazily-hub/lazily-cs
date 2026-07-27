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
/// A reactive FIFO queue (<c>#lzcsqueues</c>).
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
/// Each reader kind therefore gets its own <see cref="Source{T}"/> version cell, bumped
/// when and only when that kind's derived value changes — the same mechanism
/// <see cref="ReactiveMap{TKey,TValue,THandle}"/> already uses for its membership and
/// order signals. This is NOT the "poll a counter" shape the spec rules out: a reader is
/// a <see cref="Computed{T}"/> that reads its version cell to register the dependency edge
/// and then derives the real value from storage. Callers never see a version number.
/// </para>
/// <para>
/// Storage sits outside the graph, so a reader has no reactive dependency other than its
/// version cell and stays memoized until the shell bumps it.
/// </para>
/// </remarks>
public sealed class QueueCell<T>
{
    private readonly Context _ctx;
    private readonly LinkedList<T> _elements = new();
    private readonly int? _capacity;
    private bool _closed;

    // One version cell per reader kind. Bumping exactly the kinds whose derived value
    // changed is what keeps the kinds independent.
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
        if (capacity is <= 0)
        {
            throw new System.ArgumentOutOfRangeException(
                nameof(capacity), capacity, "capacity must be positive when bounded");
        }
        _ctx = ctx;
        _capacity = capacity;

        _headVersion = ctx.Source(0);
        _lenVersion = ctx.Source(0);
        _emptyVersion = ctx.Source(0);
        _fullVersion = ctx.Source(0);
        _closedVersion = ctx.Source(0);

        _head = ctx.Computed(cx => { cx.Get(_headVersion); return _elements.First is null ? default : _elements.First.Value; });
        _len = ctx.Computed(cx => { cx.Get(_lenVersion); return _elements.Count; });
        _isEmpty = ctx.Computed(cx => { cx.Get(_emptyVersion); return _elements.Count == 0; });
        _isFull = ctx.Computed(cx => { cx.Get(_fullVersion); return _capacity is int cap && _elements.Count >= cap; });
        _isClosed = ctx.Computed(cx => { cx.Get(_closedVersion); return _closed; });
    }

    /// <summary>Declared capacity, or <c>null</c> when unbounded. Not reactive.</summary>
    public int? Capacity => _capacity;

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

    /// <summary>
    /// Appends to the tail. Returns <see cref="QueuePushResult.Full"/> when bounded and at
    /// capacity, or <see cref="QueuePushResult.Closed"/> when closed; on either the queue is
    /// unchanged and NOTHING is invalidated.
    /// </summary>
    /// <param name="value">The element to append.</param>
    public QueuePushResult TryPush(T value)
    {
        if (_closed) return QueuePushResult.Closed;
        if (_capacity is int cap && _elements.Count >= cap) return QueuePushResult.Full;

        var wasEmpty = _elements.Count == 0;
        var wasFull = _capacity is int c0 && _elements.Count >= c0;
        _elements.AddLast(value);
        Invalidate(wasEmpty, wasFull, headChanged: wasEmpty);
        return QueuePushResult.Ok;
    }

    /// <summary>
    /// Removes and returns the head. <see cref="QueuePopStatus.Empty"/> when open and empty,
    /// <see cref="QueuePopStatus.Closed"/> when closed and empty — a closed non-empty queue
    /// still drains.
    /// </summary>
    public QueuePopResult<T> TryPop()
    {
        if (_elements.Count == 0)
        {
            return new QueuePopResult<T>(_closed ? QueuePopStatus.Closed : QueuePopStatus.Empty, default);
        }
        var wasEmpty = false;
        var wasFull = _capacity is int c0 && _elements.Count >= c0;
        var value = _elements.First!.Value;
        _elements.RemoveFirst();
        // A pop ALWAYS changes the head: front -> next, or front -> empty.
        Invalidate(wasEmpty, wasFull, headChanged: true);
        return new QueuePopResult<T>(QueuePopStatus.Value, value);
    }

    /// <summary>
    /// Closes the queue. Idempotent and terminal: the first close invalidates closed readers,
    /// later ones invalidate nothing.
    /// </summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _closedVersion.Set(++_closedV);
    }

    /// <summary>
    /// Bumps exactly the reader kinds whose derived value changed, inside one batch so a
    /// subscriber never observes a partial transition (len decremented while is_full still
    /// reads stale). <c>closed</c> is never touched here — only <see cref="Close"/> moves it.
    /// </summary>
    private void Invalidate(bool wasEmpty, bool wasFull, bool headChanged)
    {
        var isEmpty = _elements.Count == 0;
        var isFull = _capacity is int cap && _elements.Count >= cap;

        _ctx.Batch(() =>
        {
            // Len always changes on a successful op.
            _lenVersion.Set(++_lenV);
            if (wasEmpty != isEmpty) _emptyVersion.Set(++_emptyV);
            if (wasFull != isFull) _fullVersion.Set(++_fullV);
            if (headChanged) _headVersion.Set(++_headV);
        });
    }
}
