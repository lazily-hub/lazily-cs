using System.Collections.Concurrent;
using System.Text;

namespace Lazily;

/// <summary>The normative C-ABI operation status discriminants.</summary>
public enum LazilyFfiStatus
{
    /// <summary>Success.</summary>
    Ok = 0,

    /// <summary>A receive found no queued frame.</summary>
    Empty = 1,

    /// <summary>A required native pointer was null.</summary>
    NullPointer = 2,

    /// <summary>The bytes did not decode as a supported IPC message.</summary>
    InvalidMessage = 3,

    /// <summary>A decoded message could not be encoded.</summary>
    EncodeFailed = 4,

    /// <summary>An exception was caught before leaving the managed boundary.</summary>
    Panic = 5,
}

/// <summary>The normative C-ABI IPC message-kind discriminants.</summary>
public enum LazilyFfiMessageKind
{
    /// <summary>Unknown or unset.</summary>
    Unknown = 0,

    /// <summary>A full state snapshot.</summary>
    Snapshot = 1,

    /// <summary>An incremental state delta.</summary>
    Delta = 2,

    /// <summary>A CRDT anti-entropy frame.</summary>
    CrdtSync = 3,

    /// <summary>A reliable-sync snapshot request.</summary>
    ResyncRequest = 4,

    /// <summary>A reliable-sync outbox acknowledgement.</summary>
    OutboxAck = 5,
}

/// <summary>An owned byte buffer at the managed side of the FFI boundary.</summary>
public sealed record LazilyFfiBytes
{
    /// <summary>Copies bytes into a new owned buffer.</summary>
    public LazilyFfiBytes(IEnumerable<byte> bytes)
    {
        Guard.NotNull(bytes, nameof(bytes));
        Bytes = bytes.ToArray();
    }

    /// <summary>The owned bytes.</summary>
    public byte[] Bytes { get; }
}

/// <summary>The result of classifying a serialized IPC frame.</summary>
public sealed record LazilyFfiClassification(
    LazilyFfiStatus Status,
    LazilyFfiMessageKind Kind);

/// <summary>The result of canonicalizing a serialized IPC frame.</summary>
public sealed record LazilyFfiCloneResult(
    LazilyFfiStatus Status,
    LazilyFfiBytes? Output = null);

/// <summary>Pure managed implementation shared by the NativeAOT C exports.</summary>
public static class LazilyFfi
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>Validates that bytes decode as one IPC message.</summary>
    public static LazilyFfiStatus ValidateJson(ReadOnlySpan<byte> frame)
    {
        try
        {
            _ = Decode(frame);
            LazilyMetrics.FfiAccepted();
            return LazilyFfiStatus.Ok;
        }
        catch (Exception)
        {
            LazilyMetrics.FfiRejected();
            return LazilyFfiStatus.InvalidMessage;
        }
    }

    /// <summary>Decodes a frame and derives its message-kind discriminant.</summary>
    public static LazilyFfiClassification ClassifyJson(ReadOnlySpan<byte> frame)
    {
        try
        {
            var message = Decode(frame);
            LazilyMetrics.FfiAccepted();
            return new LazilyFfiClassification(LazilyFfiStatus.Ok, Kind(message));
        }
        catch (Exception)
        {
            LazilyMetrics.FfiRejected();
            return new LazilyFfiClassification(
                LazilyFfiStatus.InvalidMessage,
                LazilyFfiMessageKind.Unknown);
        }
    }

    /// <summary>Decodes and re-encodes canonical JSON bytes with explicit ownership.</summary>
    public static LazilyFfiCloneResult CloneJson(ReadOnlySpan<byte> frame)
    {
        IpcMessage message;
        try
        {
            message = Decode(frame);
        }
        catch (Exception)
        {
            LazilyMetrics.FfiRejected();
            return new LazilyFfiCloneResult(LazilyFfiStatus.InvalidMessage);
        }

        try
        {
            var output = new LazilyFfiBytes(Encoding.UTF8.GetBytes(IpcWire.Serialize(message)));
            LazilyMetrics.FfiAccepted();
            return new LazilyFfiCloneResult(LazilyFfiStatus.Ok, output);
        }
        catch (Exception)
        {
            LazilyMetrics.FfiRejected();
            return new LazilyFfiCloneResult(LazilyFfiStatus.EncodeFailed);
        }
    }

    private static IpcMessage Decode(ReadOnlySpan<byte> frame) =>
        IpcWire.Deserialize(StrictUtf8.GetString(frame.ToArray()));

    private static LazilyFfiMessageKind Kind(IpcMessage message) =>
        message switch
        {
            SnapshotMessage => LazilyFfiMessageKind.Snapshot,
            DeltaMessage => LazilyFfiMessageKind.Delta,
            CrdtSyncMessage => LazilyFfiMessageKind.CrdtSync,
            ResyncRequestMessage => LazilyFfiMessageKind.ResyncRequest,
            OutboxAckMessage => LazilyFfiMessageKind.OutboxAck,
            _ => LazilyFfiMessageKind.Unknown,
        };
}

/// <summary>
/// Thread-safe in-process channel used behind the exported C handle. Every raw frame is decoded
/// and re-encoded before enqueue, so receive always returns canonical JSON.
/// </summary>
public sealed class LazilyFfiChannel
{
    private readonly ConcurrentQueue<LazilyFfiBytes> _frames = new();

    /// <summary>Encodes and enqueues a typed message.</summary>
    public LazilyFfiStatus Send(IpcMessage message)
    {
        Guard.NotNull(message, nameof(message));
        try
        {
            _frames.Enqueue(new LazilyFfiBytes(Encoding.UTF8.GetBytes(IpcWire.Serialize(message))));
            LazilyMetrics.FfiAccepted();
            return LazilyFfiStatus.Ok;
        }
        catch (Exception)
        {
            LazilyMetrics.FfiRejected();
            return LazilyFfiStatus.EncodeFailed;
        }
    }

    /// <summary>Canonicalizes and enqueues raw JSON frame bytes.</summary>
    public LazilyFfiStatus SendJson(ReadOnlySpan<byte> frame)
    {
        var cloned = LazilyFfi.CloneJson(frame);
        if (cloned.Output is not null) _frames.Enqueue(cloned.Output);
        return cloned.Status;
    }

    /// <summary>Dequeues one owned canonical frame.</summary>
    public LazilyFfiStatus Receive(out LazilyFfiBytes? frame)
    {
        if (_frames.TryDequeue(out frame)) return LazilyFfiStatus.Ok;
        frame = null;
        return LazilyFfiStatus.Empty;
    }
}
