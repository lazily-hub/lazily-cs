using System;
using System.Collections.Generic;

namespace Lazily;

/// <summary>
/// Pluggable delivery mechanism for relay operations. Framing belongs to the transport; the
/// relay merges every operation a frame delivers.
/// </summary>
public interface IRelayTransport<T>
{
    /// <summary>Enqueues one operation for delivery.</summary>
    void Deliver(T operation);

    /// <summary>Pulls the next transport-defined frame, empty when no operation is ready.</summary>
    IReadOnlyList<T> Poll();

    /// <summary>Whether any operation remains buffered.</summary>
    bool HasPending { get; }
}

/// <summary>Direct in-process delivery: every buffered operation is returned in one frame.</summary>
public sealed class InProcRelayTransport<T> : IRelayTransport<T>
{
    private readonly Queue<T> _buffer = new();

    /// <inheritdoc />
    public void Deliver(T operation) => _buffer.Enqueue(operation);

    /// <inheritdoc />
    public IReadOnlyList<T> Poll()
    {
        var frame = _buffer.ToArray();
        _buffer.Clear();
        return frame;
    }

    /// <inheritdoc />
    public bool HasPending => _buffer.Count > 0;
}

/// <summary>
/// Bounded-frame transport modeling cross-thread, IPC, WebSocket, or MTU batch boundaries.
/// </summary>
public sealed class FramedRelayTransport<T> : IRelayTransport<T>
{
    private readonly Queue<T> _buffer = new();
    private readonly int _frameSize;

    /// <summary>Creates a transport whose frames hold at most the requested positive size.</summary>
    public FramedRelayTransport(int frameSize)
    {
        _frameSize = Math.Max(1, frameSize);
    }

    /// <summary>Maximum operation count returned by one poll.</summary>
    public int FrameSize => _frameSize;

    /// <inheritdoc />
    public void Deliver(T operation) => _buffer.Enqueue(operation);

    /// <inheritdoc />
    public IReadOnlyList<T> Poll()
    {
        var count = Math.Min(_frameSize, _buffer.Count);
        var frame = new T[count];
        for (var i = 0; i < count; i++) frame[i] = _buffer.Dequeue();
        return frame;
    }

    /// <inheritdoc />
    public bool HasPending => _buffer.Count > 0;
}
