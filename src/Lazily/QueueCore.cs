// Queue-family cores — the graph-agnostic transition algebra shared by all three flavors
// (spec tag: lzqueuefamilyflavors).
//
// Same split IngressCore makes for the ingress family, and for the same reason: invalidation
// is a graph WRITE, so the core performs none. Every mutator returns which reader kinds the
// transition dirtied, and each shell bumps exactly those version sources on its own graph.
// Three shells, one algebra — which is what makes "the three flavors obey ONE contract" a
// structural fact rather than three copies that have to keep agreeing by hand.
//
// Nothing in this file touches a Context, a lock, or a Task.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Lazily;

/// <summary>Which <see cref="QueueCell{T}"/> reader kinds a transition dirtied.</summary>
/// <param name="Head">The head element changed.</param>
/// <param name="Len">The element count changed.</param>
/// <param name="IsEmpty">Emptiness flipped.</param>
/// <param name="IsFull">Fullness flipped — the backpressure edge.</param>
/// <param name="Closed">The queue transitioned open to closed.</param>
public readonly record struct QueueInvalidates(
    bool Head,
    bool Len,
    bool IsEmpty,
    bool IsFull,
    bool Closed)
{
    /// <summary>A rejected op changes nothing and dirties nothing.</summary>
    public static QueueInvalidates None => default;

    /// <summary>Whether any reader kind was dirtied.</summary>
    public bool Any => Head || Len || IsEmpty || IsFull || Closed;
}

/// <summary>
/// The graph-agnostic FIFO algebra: storage plus the reader-kind transition predicates.
/// </summary>
/// <remarks>
/// The reader-kind independence law lives here — a push onto a non-empty queue does NOT dirty
/// the head reader, a pop always does — so all three flavors inherit it instead of restating it.
/// </remarks>
/// <typeparam name="T">The element type.</typeparam>
public sealed class QueueCore<T>
{
    private readonly LinkedList<T> _elements = new();
    private readonly int? _capacity;
    private bool _closed;

    /// <summary>Creates a core, bounded when <paramref name="capacity"/> is given.</summary>
    /// <param name="capacity">Maximum elements, or <c>null</c> for unbounded.</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="capacity"/> is not positive.</exception>
    public QueueCore(int? capacity)
    {
        if (capacity is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, "capacity must be positive when bounded");
        }
        _capacity = capacity;
    }

    /// <summary>Declared capacity, or <c>null</c> when unbounded.</summary>
    public int? Capacity => _capacity;

    /// <summary>The head element, or <c>default</c> when empty.</summary>
    public T? Head => _elements.First is null ? default : _elements.First.Value;

    /// <summary>The element count.</summary>
    public int Len => _elements.Count;

    /// <summary>Whether the queue holds no elements.</summary>
    public bool IsEmpty => _elements.Count == 0;

    /// <summary>Whether a bounded queue is at capacity.</summary>
    public bool IsFull => _capacity is int cap && _elements.Count >= cap;

    /// <summary>Whether the queue has been closed.</summary>
    public bool IsClosed => _closed;

    /// <summary>Non-reactive FIFO-ordered snapshot.</summary>
    public IReadOnlyList<T> Elements() => _elements.ToArray();

    /// <summary>
    /// Appends to the tail. A rejection leaves the queue unchanged and dirties nothing.
    /// </summary>
    /// <param name="value">The element to append.</param>
    public (QueuePushResult Result, QueueInvalidates Invalidates) TryPush(T value)
    {
        if (_closed) return (QueuePushResult.Closed, QueueInvalidates.None);
        if (_capacity is int cap && _elements.Count >= cap)
        {
            return (QueuePushResult.Full, QueueInvalidates.None);
        }

        var wasEmpty = _elements.Count == 0;
        var wasFull = IsFull;
        _elements.AddLast(value);
        // Head changes on a push only when the queue was empty; a push onto a non-empty queue
        // leaves head untouched — the reader-kind independence law.
        return (QueuePushResult.Ok, Diff(wasEmpty, wasFull, headChanged: wasEmpty));
    }

    /// <summary>
    /// Removes and returns the head. A closed non-empty queue still drains; only closed and
    /// empty yields <see cref="QueuePopStatus.Closed"/>.
    /// </summary>
    public (QueuePopResult<T> Result, QueueInvalidates Invalidates) TryPop()
    {
        if (_elements.Count == 0)
        {
            var status = _closed ? QueuePopStatus.Closed : QueuePopStatus.Empty;
            return (new QueuePopResult<T>(status, default), QueueInvalidates.None);
        }

        var wasFull = IsFull;
        var value = _elements.First!.Value;
        _elements.RemoveFirst();
        // A pop ALWAYS changes the head: front -> next, or front -> empty.
        return (
            new QueuePopResult<T>(QueuePopStatus.Value, value),
            Diff(wasEmpty: false, wasFull: wasFull, headChanged: true));
    }

    /// <summary>Closes the queue. Idempotent and terminal; touches only the closed kind.</summary>
    public QueueInvalidates Close()
    {
        if (_closed) return QueueInvalidates.None;
        _closed = true;
        return new QueueInvalidates(false, false, false, false, Closed: true);
    }

    private QueueInvalidates Diff(bool wasEmpty, bool wasFull, bool headChanged) =>
        new(
            Head: headChanged,
            // Len always changes on a successful push or pop.
            Len: true,
            IsEmpty: wasEmpty != IsEmpty,
            IsFull: wasFull != IsFull,
            Closed: false);
}
