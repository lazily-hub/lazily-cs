using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lazily;

namespace Lazily.Native;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFfiBytes
{
    public byte* Pointer;
    public nuint Length;
}

internal static unsafe class NativeExports
{
    [UnmanagedCallersOnly(
        EntryPoint = "lazily_ffi_ipc_message_validate_json",
        CallConvs = [typeof(CallConvCdecl)])]
    public static int ValidateJson(byte* pointer, nuint length)
    {
        try
        {
            return (int)Validate(pointer, length);
        }
        catch
        {
            return (int)LazilyFfiStatus.Panic;
        }
    }

    [UnmanagedCallersOnly(
        EntryPoint = "lazily_ffi_ipc_message_kind_json",
        CallConvs = [typeof(CallConvCdecl)])]
    public static int KindJson(byte* pointer, nuint length, int* kind)
    {
        try
        {
            if (kind is null) return (int)LazilyFfiStatus.NullPointer;
            *kind = (int)LazilyFfiMessageKind.Unknown;
            if (pointer is null) return (int)LazilyFfiStatus.NullPointer;
            var result = LazilyFfi.ClassifyJson(Span(pointer, length));
            *kind = (int)result.Kind;
            return (int)result.Status;
        }
        catch
        {
            return (int)LazilyFfiStatus.Panic;
        }
    }

    [UnmanagedCallersOnly(
        EntryPoint = "lazily_ffi_ipc_message_clone_json",
        CallConvs = [typeof(CallConvCdecl)])]
    public static int CloneJson(byte* pointer, nuint length, NativeFfiBytes* output)
    {
        try
        {
            if (output is null) return (int)LazilyFfiStatus.NullPointer;
            *output = default;
            if (pointer is null) return (int)LazilyFfiStatus.NullPointer;
            var result = LazilyFfi.CloneJson(Span(pointer, length));
            if (result.Status != LazilyFfiStatus.Ok || result.Output is null)
            {
                return (int)result.Status;
            }

            *output = Allocate(result.Output.Bytes);
            return (int)LazilyFfiStatus.Ok;
        }
        catch
        {
            return (int)LazilyFfiStatus.Panic;
        }
    }

    [UnmanagedCallersOnly(
        EntryPoint = "lazily_ffi_bytes_free",
        CallConvs = [typeof(CallConvCdecl)])]
    public static void BytesFree(NativeFfiBytes bytes)
    {
        try
        {
            NativeMemory.Free(bytes.Pointer);
        }
        catch
        {
            // A native free has no error channel. Never unwind across the ABI.
        }
    }

    [UnmanagedCallersOnly(
        EntryPoint = "lazily_ffi_channel_new",
        CallConvs = [typeof(CallConvCdecl)])]
    public static nint ChannelNew()
    {
        try
        {
            return GCHandle.ToIntPtr(GCHandle.Alloc(new LazilyFfiChannel()));
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(
        EntryPoint = "lazily_ffi_channel_free",
        CallConvs = [typeof(CallConvCdecl)])]
    public static int ChannelFree(nint handle)
    {
        try
        {
            if (handle == 0) return (int)LazilyFfiStatus.NullPointer;
            GCHandle.FromIntPtr(handle).Free();
            return (int)LazilyFfiStatus.Ok;
        }
        catch
        {
            return (int)LazilyFfiStatus.Panic;
        }
    }

    [UnmanagedCallersOnly(
        EntryPoint = "lazily_ffi_channel_send_json",
        CallConvs = [typeof(CallConvCdecl)])]
    public static int ChannelSendJson(nint handle, byte* pointer, nuint length)
    {
        try
        {
            if (pointer is null || !TryChannel(handle, out var channel))
            {
                return (int)LazilyFfiStatus.NullPointer;
            }

            return (int)channel.SendJson(Span(pointer, length));
        }
        catch
        {
            return (int)LazilyFfiStatus.Panic;
        }
    }

    [UnmanagedCallersOnly(
        EntryPoint = "lazily_ffi_channel_recv_json",
        CallConvs = [typeof(CallConvCdecl)])]
    public static int ChannelReceiveJson(nint handle, NativeFfiBytes* output)
    {
        try
        {
            if (output is null || !TryChannel(handle, out var channel))
            {
                return (int)LazilyFfiStatus.NullPointer;
            }

            *output = default;
            var status = channel.Receive(out var frame);
            if (status == LazilyFfiStatus.Ok && frame is not null)
            {
                *output = Allocate(frame.Bytes);
            }

            return (int)status;
        }
        catch
        {
            return (int)LazilyFfiStatus.Panic;
        }
    }

    private static LazilyFfiStatus Validate(byte* pointer, nuint length) =>
        pointer is null
            ? LazilyFfiStatus.NullPointer
            : LazilyFfi.ValidateJson(Span(pointer, length));

    private static ReadOnlySpan<byte> Span(byte* pointer, nuint length) =>
        new(pointer, checked((int)length));

    private static NativeFfiBytes Allocate(byte[] bytes)
    {
        var pointer = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
        if (pointer is null) throw new OutOfMemoryException();
        bytes.AsSpan().CopyTo(new Span<byte>(pointer, bytes.Length));
        return new NativeFfiBytes
        {
            Pointer = pointer,
            Length = (nuint)bytes.Length,
        };
    }

    private static bool TryChannel(nint handle, out LazilyFfiChannel channel)
    {
        channel = null!;
        if (handle == 0) return false;
        channel = GCHandle.FromIntPtr(handle).Target as LazilyFfiChannel ?? null!;
        return channel is not null;
    }
}
