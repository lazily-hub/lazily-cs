using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Lazily;

/// <summary>Classifies failures raised by <see cref="ShmBlobArena"/> and blob backends.</summary>
public enum ShmBlobArenaError
{
    /// <summary>The backing buffer cannot hold a header and one payload byte.</summary>
    CapacityTooSmall,

    /// <summary>The payload cannot fit in the backing buffer.</summary>
    BlobTooLarge,

    /// <summary>The descriptor points outside the backing buffer.</summary>
    DescriptorOutOfBounds,

    /// <summary>The descriptor does not match the stored header.</summary>
    DescriptorMismatch,

    /// <summary>The stored payload does not match its checksum.</summary>
    ChecksumMismatch,

    /// <summary>The generation or epoch counter overflowed.</summary>
    CounterOverflow,

    /// <summary>A shared-memory mapping could not be created or opened.</summary>
    BackendIo,
}

/// <summary>An error raised while writing or resolving a shared blob.</summary>
public sealed class ShmBlobArenaException : Exception
{
    /// <summary>Creates an arena exception with a stable error classification.</summary>
    public ShmBlobArenaException(ShmBlobArenaError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Creates an arena exception caused by a backend I/O failure.</summary>
    public ShmBlobArenaException(ShmBlobArenaError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>The stable error classification.</summary>
    public ShmBlobArenaError Error { get; }
}

/// <summary>
/// Fixed-size byte arena used by shared-blob transports.
/// </summary>
/// <remarks>
/// Each payload is preceded by the canonical 40-byte <c>LZSH</c> header. Writes wrap to the
/// beginning when the remaining tail is too small; monotonically increasing generations make
/// descriptors overwritten by a later wrap fail closed.
/// </remarks>
public sealed class ShmBlobArena
{
    /// <summary>Bytes reserved before every arena payload.</summary>
    public const int HeaderLength = 40;

    private const uint Magic = 0x4c5a5348;
    private const ushort Version = 1;
    private readonly object _gate = new();
    private readonly byte[] _buffer;
    private int _writeOffset;
    private ulong _nextGeneration = 1;
    private ulong _epoch;

    /// <summary>Creates an arena with a fixed byte capacity and initial epoch.</summary>
    public ShmBlobArena(int capacity, ulong epoch = 0)
    {
        if (capacity < HeaderLength + 1)
        {
            throw new ShmBlobArenaException(
                ShmBlobArenaError.CapacityTooSmall,
                $"SHM blob arena capacity {capacity} is smaller than minimum {HeaderLength + 1}.");
        }

        _buffer = new byte[capacity];
        _epoch = epoch;
    }

    /// <summary>Total backing-buffer capacity.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Largest payload that can fit in one arena entry.</summary>
    public int MaximumBlobLength => Capacity - HeaderLength;

    /// <summary>Current validity epoch.</summary>
    public ulong Epoch
    {
        get
        {
            lock (_gate)
            {
                return _epoch;
            }
        }
    }

    /// <summary>Current arena write cursor.</summary>
    public int WriteOffset
    {
        get
        {
            lock (_gate)
            {
                return _writeOffset;
            }
        }
    }

    /// <summary>A read-only view of the complete backing buffer, including entry headers.</summary>
    public ReadOnlyMemory<byte> Bytes => _buffer;

    internal byte[] DangerousBuffer => _buffer;

    /// <summary>Writes bytes at the current epoch and returns their descriptor.</summary>
    public ShmBlobRef Write(ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            return WriteLocked(_epoch, payload);
        }
    }

    /// <summary>
    /// Writes bytes at an explicit epoch and returns their descriptor.
    /// </summary>
    /// <remarks>The explicit epoch becomes the arena's current validity epoch.</remarks>
    public ShmBlobRef WriteBlob(ulong epoch, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            _epoch = epoch;
            return WriteLocked(epoch, payload);
        }
    }

    /// <summary>Returns a defensive copy of a resolved payload.</summary>
    public byte[] Read(ShmBlobRef descriptor)
    {
        lock (_gate)
        {
            var view = ReadViewLocked(descriptor);
            return view.ToArray();
        }
    }

    /// <summary>
    /// Resolves a descriptor to the arena's own backing memory without copying.
    /// </summary>
    public bool TryReadView(ShmBlobRef descriptor, out ReadOnlyMemory<byte> view)
    {
        lock (_gate)
        {
            try
            {
                view = ReadViewLocked(descriptor);
                return true;
            }
            catch (ShmBlobArenaException)
            {
                view = default;
                return false;
            }
        }
    }

    /// <summary>Advances the validity epoch, invalidating every prior descriptor.</summary>
    public void AdvanceEpoch()
    {
        lock (_gate)
        {
            if (_epoch == ulong.MaxValue)
            {
                throw new ShmBlobArenaException(
                    ShmBlobArenaError.CounterOverflow,
                    "SHM blob epoch counter overflowed.");
            }

            _epoch++;
        }
    }

    /// <summary>Computes the canonical FNV-1a-64 payload checksum.</summary>
    public static ulong Checksum(ReadOnlySpan<byte> bytes)
    {
        const ulong offsetBasis = 0xcbf29ce484222325;
        const ulong prime = 0x100000001b3;
        var hash = offsetBasis;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash = unchecked(hash * prime);
        }

        return hash;
    }

    private ShmBlobRef WriteLocked(ulong epoch, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumBlobLength)
        {
            throw new ShmBlobArenaException(
                ShmBlobArenaError.BlobTooLarge,
                $"SHM blob length {payload.Length} exceeds maximum {MaximumBlobLength}.");
        }

        var totalLength = checked(HeaderLength + payload.Length);
        if (_writeOffset + totalLength > Capacity)
        {
            _writeOffset = 0;
        }

        var generation = _nextGeneration;
        if (generation == ulong.MaxValue)
        {
            throw new ShmBlobArenaException(
                ShmBlobArenaError.CounterOverflow,
                "SHM blob generation counter overflowed.");
        }

        _nextGeneration++;
        var offset = _writeOffset;
        var checksum = Checksum(payload);
        var descriptor = new ShmBlobRef(
            checked((ulong)offset),
            checked((ulong)payload.Length),
            generation,
            epoch,
            checksum);

        WriteHeader(_buffer.AsSpan(offset, HeaderLength), descriptor);
        payload.CopyTo(_buffer.AsSpan(offset + HeaderLength, payload.Length));
        _writeOffset += totalLength;
        if (_writeOffset == Capacity)
        {
            _writeOffset = 0;
        }

        return descriptor;
    }

    private ReadOnlyMemory<byte> ReadViewLocked(ShmBlobRef descriptor)
    {
        if (descriptor.Epoch != _epoch)
        {
            throw Mismatch("epoch");
        }

        if (descriptor.Offset > int.MaxValue || descriptor.Length > int.MaxValue)
        {
            throw OutOfBounds(descriptor);
        }

        var offset = checked((int)descriptor.Offset);
        var length = checked((int)descriptor.Length);
        if (offset < 0 ||
            length < 0 ||
            offset > Capacity - HeaderLength ||
            length > Capacity - offset - HeaderLength)
        {
            throw OutOfBounds(descriptor);
        }

        var header = ReadHeader(_buffer.AsSpan(offset, HeaderLength));
        if (header.Generation != descriptor.Generation)
        {
            throw Mismatch("generation");
        }

        if (header.Epoch != descriptor.Epoch)
        {
            throw Mismatch("epoch");
        }

        if (header.Length != descriptor.Length)
        {
            throw Mismatch("length");
        }

        if (header.Checksum != descriptor.Checksum)
        {
            throw Mismatch("checksum");
        }

        var payload = _buffer.AsMemory(offset + HeaderLength, length);
        var actualChecksum = Checksum(payload.Span);
        if (actualChecksum != descriptor.Checksum)
        {
            throw new ShmBlobArenaException(
                ShmBlobArenaError.ChecksumMismatch,
                $"SHM blob checksum mismatch: expected {descriptor.Checksum:x}, got {actualChecksum:x}.");
        }

        return payload;
    }

    private static void WriteHeader(Span<byte> header, ShmBlobRef descriptor)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], HeaderLength);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], descriptor.Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], descriptor.Epoch);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], descriptor.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], descriptor.Checksum);
    }

    private static ShmBlobRef ReadHeader(ReadOnlySpan<byte> header)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
        {
            throw Mismatch("magic");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Version)
        {
            throw Mismatch("version");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != HeaderLength)
        {
            throw Mismatch("header_length");
        }

        return new ShmBlobRef(
            0,
            BinaryPrimitives.ReadUInt64LittleEndian(header[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[32..]));
    }

    private ShmBlobArenaException OutOfBounds(ShmBlobRef descriptor) =>
        new(
            ShmBlobArenaError.DescriptorOutOfBounds,
            $"SHM blob descriptor offset={descriptor.Offset} len={descriptor.Length} exceeds arena capacity {Capacity}.");

    private static ShmBlobArenaException Mismatch(string field) =>
        new(ShmBlobArenaError.DescriptorMismatch, $"SHM blob descriptor mismatch for {field}.");
}

/// <summary>Adapter contract for zero-copy blob storage.</summary>
public interface IBlobBackend
{
    /// <summary>The wire discriminator served by this backend.</summary>
    BlobBackendKind Kind { get; }

    /// <summary>Stores immutable bytes and returns a descriptor.</summary>
    ShmBlobRef Write(ReadOnlySpan<byte> bytes);

    /// <summary>Resolves a descriptor to backend-owned memory without copying.</summary>
    bool TryReadView(ShmBlobRef descriptor, out ReadOnlyMemory<byte> view);

    /// <summary>Advances the validity epoch, invalidating prior descriptors.</summary>
    void AdvanceEpoch();
}

/// <summary>Single-address-space blob backend backed by <see cref="ShmBlobArena"/>.</summary>
public sealed class InProcessBackend : IBlobBackend
{
    /// <summary>Default in-process arena capacity (1 MiB).</summary>
    public const int DefaultCapacity = 1 << 20;

    /// <summary>Creates a backend with a fresh arena.</summary>
    public InProcessBackend(int capacity = DefaultCapacity)
        : this(new ShmBlobArena(capacity))
    {
    }

    /// <summary>Creates a backend over an existing arena.</summary>
    public InProcessBackend(ShmBlobArena arena)
    {
        Arena = arena ?? throw new ArgumentNullException(nameof(arena));
    }

    /// <summary>The backing arena.</summary>
    public ShmBlobArena Arena { get; }

    /// <inheritdoc />
    public BlobBackendKind Kind => BlobBackendKind.InProcess;

    /// <inheritdoc />
    public ShmBlobRef Write(ReadOnlySpan<byte> bytes) =>
        Arena.Write(bytes) with { Backend = BlobBackendKind.InProcess };

    /// <inheritdoc />
    public bool TryReadView(ShmBlobRef descriptor, out ReadOnlyMemory<byte> view)
    {
        if (descriptor.EffectiveBackend() != Kind)
        {
            view = default;
            return false;
        }

        return Arena.TryReadView(descriptor, out view);
    }

    /// <inheritdoc />
    public void AdvanceEpoch() => Arena.AdvanceEpoch();
}

/// <summary>Blob backend holding Apache Arrow IPC stream bytes.</summary>
public sealed class ArrowBackend : IBlobBackend
{
    /// <summary>Default Arrow arena capacity (4 MiB).</summary>
    public const int DefaultCapacity = 1 << 22;

    /// <summary>Creates a backend with a fresh arena.</summary>
    public ArrowBackend(int capacity = DefaultCapacity)
        : this(new ShmBlobArena(capacity))
    {
    }

    /// <summary>Creates a backend over an existing arena.</summary>
    public ArrowBackend(ShmBlobArena arena)
    {
        Arena = arena ?? throw new ArgumentNullException(nameof(arena));
    }

    /// <summary>The backing arena containing Arrow IPC stream buffers.</summary>
    public ShmBlobArena Arena { get; }

    /// <inheritdoc />
    public BlobBackendKind Kind => BlobBackendKind.Arrow;

    /// <inheritdoc />
    public ShmBlobRef Write(ReadOnlySpan<byte> bytes) =>
        Arena.Write(bytes) with { Backend = BlobBackendKind.Arrow };

    /// <inheritdoc />
    public bool TryReadView(ShmBlobRef descriptor, out ReadOnlyMemory<byte> view)
    {
        if (descriptor.EffectiveBackend() != Kind)
        {
            view = default;
            return false;
        }

        return Arena.TryReadView(descriptor, out view);
    }

    /// <inheritdoc />
    public void AdvanceEpoch() => Arena.AdvanceEpoch();
}

/// <summary>
/// File-backed shared-memory backend for same-host cross-process transport.
/// </summary>
/// <remarks>
/// Linux regions live under <c>/dev/shm</c>; other platforms use their temporary directory.
/// The region header contains atomic bump, generation, and epoch counters, followed by immutable
/// slots. Separate processes opening the same name map and resolve the same bytes.
/// </remarks>
public sealed unsafe class ShmBackend : IBlobBackend, IDisposable
{
    private const long RegionMagic = 0x4c5a5348424c4f42;
    private const int RegionHeaderLength = 40;
    private const int SlotHeaderLength = 24;
    private const int MagicOffset = 0;
    private const int CapacityOffset = 8;
    private const int BumpOffset = 16;
    private const int GenerationOffset = 24;
    private const int EpochOffset = 32;

    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly MappedMemoryManager _memory;
    private byte* _pointer;
    private bool _disposed;

    private ShmBackend(
        string name,
        string regionPath,
        int capacity,
        MemoryMappedFile mapping,
        MemoryMappedViewAccessor view)
    {
        Name = name;
        RegionPath = regionPath;
        Capacity = capacity;
        _mapping = mapping;
        _view = view;

        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _pointer = pointer + _view.PointerOffset;
        _memory = new MappedMemoryManager(_pointer, capacity);
    }

    /// <summary>The caller-visible shared region name.</summary>
    public string Name { get; }

    /// <summary>The filesystem path backing the shared region.</summary>
    public string RegionPath { get; }

    /// <summary>Total mapped capacity.</summary>
    public int Capacity { get; }

    /// <summary>Current shared validity epoch.</summary>
    public ulong Epoch
    {
        get
        {
            ThrowIfDisposed();
            return checked((ulong)Interlocked.Read(ref HeaderLong(EpochOffset)));
        }
    }

    /// <inheritdoc />
    public BlobBackendKind Kind => BlobBackendKind.Shm;

    /// <summary>Creates or truncates a named shared region.</summary>
    public static ShmBackend Create(string name, int capacity)
    {
        ValidatePlatformAndCapacity(name, capacity);
        var path = ResolvePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            stream.SetLength(capacity);
            var mapping = MemoryMappedFile.CreateFromFile(
                stream,
                null,
                capacity,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.Inheritable,
                leaveOpen: false);
            stream = null;
            var view = mapping.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
            var backend = new ShmBackend(name, path, capacity, mapping, view);
            backend.InitializeHeader();
            return backend;
        }
        catch (Exception error) when (
            error is IOException ||
            error is UnauthorizedAccessException ||
            error is PlatformNotSupportedException)
        {
            throw BackendError($"Could not create shared-memory region '{name}'.", error);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>Opens an existing named shared region.</summary>
    public static ShmBackend Open(string name)
    {
        ValidateName(name);
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Shared blob regions require a little-endian runtime.");
        }

        var path = ResolvePath(name);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (length is <= RegionHeaderLength or > int.MaxValue)
            {
                throw new ShmBlobArenaException(
                    ShmBlobArenaError.BackendIo,
                    $"Shared-memory region '{name}' reports invalid capacity {length}.");
            }

            var capacity = checked((int)length);
            var mapping = MemoryMappedFile.CreateFromFile(
                stream,
                null,
                capacity,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.Inheritable,
                leaveOpen: false);
            stream = null;
            var view = mapping.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
            var backend = new ShmBackend(name, path, capacity, mapping, view);
            backend.ValidateHeader();
            return backend;
        }
        catch (ShmBlobArenaException)
        {
            throw;
        }
        catch (Exception error) when (
            error is IOException ||
            error is UnauthorizedAccessException ||
            error is PlatformNotSupportedException)
        {
            throw BackendError($"Could not open shared-memory region '{name}'.", error);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>Removes a shared region name after its users have disposed their mappings.</summary>
    public static void Unlink(string name)
    {
        ValidateName(name);
        var path = ResolvePath(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <inheritdoc />
    public ShmBlobRef Write(ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();
        var needed = checked(SlotHeaderLength + bytes.Length);
        var offset = Interlocked.Add(ref HeaderLong(BumpOffset), needed) - needed;
        if (offset < RegionHeaderLength || offset > Capacity - needed)
        {
            throw new ShmBlobArenaException(
                ShmBlobArenaError.BlobTooLarge,
                $"SHM blob length {bytes.Length} cannot fit at offset {offset} in capacity {Capacity}.");
        }

        var generation = Interlocked.Increment(ref HeaderLong(GenerationOffset));
        if (generation <= 0)
        {
            throw new ShmBlobArenaException(
                ShmBlobArenaError.CounterOverflow,
                "SHM blob generation counter overflowed.");
        }

        var epoch = Interlocked.Read(ref HeaderLong(EpochOffset));
        var checksum = ShmBlobArena.Checksum(bytes);
        var slotOffset = checked((int)offset);
        BinaryPrimitives.WriteUInt64LittleEndian(
            new Span<byte>(_pointer + slotOffset, 8),
            checked((ulong)generation));
        BinaryPrimitives.WriteUInt64LittleEndian(
            new Span<byte>(_pointer + slotOffset + 8, 8),
            checked((ulong)bytes.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(
            new Span<byte>(_pointer + slotOffset + 16, 8),
            checksum);
        bytes.CopyTo(new Span<byte>(_pointer + slotOffset + SlotHeaderLength, bytes.Length));
        Thread.MemoryBarrier();

        return new ShmBlobRef(
            checked((ulong)(slotOffset + SlotHeaderLength)),
            checked((ulong)bytes.Length),
            checked((ulong)generation),
            checked((ulong)epoch),
            checksum);
    }

    /// <inheritdoc />
    public bool TryReadView(ShmBlobRef descriptor, out ReadOnlyMemory<byte> view)
    {
        ThrowIfDisposed();
        view = default;
        if (descriptor.EffectiveBackend() != Kind ||
            descriptor.Offset < RegionHeaderLength + SlotHeaderLength ||
            descriptor.Offset > int.MaxValue ||
            descriptor.Length > int.MaxValue)
        {
            return false;
        }

        var payloadOffset = checked((int)descriptor.Offset);
        var length = checked((int)descriptor.Length);
        var slotOffset = payloadOffset - SlotHeaderLength;
        if (slotOffset < RegionHeaderLength || length > Capacity - payloadOffset)
        {
            return false;
        }

        var slot = new ReadOnlySpan<byte>(_pointer + slotOffset, SlotHeaderLength);
        if (BinaryPrimitives.ReadUInt64LittleEndian(slot) != descriptor.Generation ||
            BinaryPrimitives.ReadUInt64LittleEndian(slot[8..]) != descriptor.Length ||
            BinaryPrimitives.ReadUInt64LittleEndian(slot[16..]) != descriptor.Checksum ||
            checked((ulong)Interlocked.Read(ref HeaderLong(EpochOffset))) != descriptor.Epoch)
        {
            return false;
        }

        view = _memory.Memory.Slice(payloadOffset, length);
        return true;
    }

    /// <inheritdoc />
    public void AdvanceEpoch()
    {
        ThrowIfDisposed();
        if (Interlocked.Increment(ref HeaderLong(EpochOffset)) <= 0)
        {
            throw new ShmBlobArenaException(
                ShmBlobArenaError.CounterOverflow,
                "SHM blob epoch counter overflowed.");
        }
    }

    /// <summary>Flushes pending mapped writes to the shared region.</summary>
    public void Flush()
    {
        ThrowIfDisposed();
        _view.Flush();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the mapped view if the caller did not dispose it.</summary>
    ~ShmBackend()
    {
        Dispose(disposing: false);
    }

    private static void ValidatePlatformAndCapacity(string name, int capacity)
    {
        ValidateName(name);
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Shared blob regions require a little-endian runtime.");
        }

        if (capacity <= RegionHeaderLength + SlotHeaderLength)
        {
            throw new ShmBlobArenaException(
                ShmBlobArenaError.CapacityTooSmall,
                $"Shared-memory capacity {capacity} must exceed {RegionHeaderLength + SlotHeaderLength}.");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A shared-memory region name is required.", nameof(name));
        }
    }

    private static string ResolvePath(string name)
    {
        var normalized = name.Trim().TrimStart('/');
        foreach (var separator in new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
        {
            normalized = normalized.Replace(separator, '_');
        }

        var directory =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Directory.Exists("/dev/shm")
                ? "/dev/shm"
                : Path.GetTempPath();
        return Path.Combine(directory, $"lazily-{normalized}.shm");
    }

    private static ShmBlobArenaException BackendError(string message, Exception error) =>
        new(ShmBlobArenaError.BackendIo, message, error);

    private ref long HeaderLong(int offset) => ref *(long*)(_pointer + offset);

    private void InitializeHeader()
    {
        Interlocked.Exchange(ref HeaderLong(MagicOffset), RegionMagic);
        Interlocked.Exchange(ref HeaderLong(CapacityOffset), Capacity);
        Interlocked.Exchange(ref HeaderLong(BumpOffset), RegionHeaderLength);
        Interlocked.Exchange(ref HeaderLong(GenerationOffset), 0);
        Interlocked.Exchange(ref HeaderLong(EpochOffset), 0);
        _view.Flush();
    }

    private void ValidateHeader()
    {
        if (Interlocked.Read(ref HeaderLong(MagicOffset)) != RegionMagic ||
            Interlocked.Read(ref HeaderLong(CapacityOffset)) != Capacity)
        {
            Dispose();
            throw new ShmBlobArenaException(
                ShmBlobArenaError.BackendIo,
                $"Shared-memory region '{Name}' has an invalid header.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ShmBackend));
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _memory.Invalidate();
        if (_pointer != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        if (disposing)
        {
            _view.Dispose();
            _mapping.Dispose();
        }
    }

    private sealed class MappedMemoryManager : MemoryManager<byte>
    {
        private readonly byte* _pointer;
        private readonly int _length;
        private bool _invalid;

        public MappedMemoryManager(byte* pointer, int length)
        {
            _pointer = pointer;
            _length = length;
        }

        public override Span<byte> GetSpan()
        {
            ThrowIfInvalid();
            return new Span<byte>(_pointer, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ThrowIfInvalid();
            if ((uint)elementIndex > (uint)_length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            return new MemoryHandle(_pointer + elementIndex);
        }

        public override void Unpin()
        {
        }

        public void Invalidate() => _invalid = true;

        protected override void Dispose(bool disposing) => _invalid = true;

        private void ThrowIfInvalid()
        {
            if (_invalid)
            {
                throw new ObjectDisposedException(nameof(ShmBackend));
            }
        }
    }
}

/// <summary>Utilities for descriptor routing, spilling, and resolution.</summary>
public static class BlobTransport
{
    /// <summary>Default payload size at or above which values spill to a backend.</summary>
    public const int DefaultSpillThreshold = 512;

    /// <summary>Returns the effective descriptor backend; an omitted discriminator means SHM.</summary>
    public static BlobBackendKind EffectiveBackend(this ShmBlobRef descriptor) =>
        descriptor.Backend ?? BlobBackendKind.Shm;

    /// <summary>Spills an inline value when it meets the configured threshold.</summary>
    public static (IpcValue Value, ulong BytesSpilled) SpillValue(
        IpcValue value,
        IBlobBackend backend,
        int threshold = DefaultSpillThreshold)
    {
        Guard.NotNull(value, nameof(value));
        Guard.NotNull(backend, nameof(backend));
        ValidateThreshold(threshold);
        if (value is not IpcValue.Inline inline || inline.Bytes.Length < threshold)
        {
            return (value, 0);
        }

        try
        {
            return (
                new IpcValue.SharedBlob(backend.Write(inline.Bytes)),
                checked((ulong)inline.Bytes.Length));
        }
        catch (ShmBlobArenaException)
        {
            return (value, 0);
        }
    }

    /// <summary>
    /// Spills all oversized payload sites in a Snapshot, Delta, or CrdtSync message.
    /// </summary>
    public static BlobSpillResult SpillMessage(
        IpcMessage message,
        IBlobBackend backend,
        int threshold = DefaultSpillThreshold)
    {
        Guard.NotNull(message, nameof(message));
        Guard.NotNull(backend, nameof(backend));
        ValidateThreshold(threshold);
        ulong total = 0;

        IpcMessage result = message switch
        {
            SnapshotMessage snapshot => snapshot with
            {
                Nodes = snapshot.Nodes.Select(
                    node => node with { State = SpillState(node.State, backend, threshold, ref total) })
                    .ToArray(),
            },
            DeltaMessage delta => delta with
            {
                Ops = delta.Ops.Select(
                    operation => SpillOperation(operation, backend, threshold, ref total))
                    .ToArray(),
            },
            CrdtSyncMessage sync => sync with
            {
                Ops = sync.Ops.Select(
                    operation =>
                    {
                        var spilled = SpillValue(operation.State, backend, threshold);
                        total += spilled.BytesSpilled;
                        return operation with { State = spilled.Value };
                    })
                    .ToArray(),
            },

            // INTENTIONAL leniency. Spilling is a SIZE OPTIMIZATION, not a semantic step: an
            // unspilled frame is byte-for-byte the frame the caller handed in and stays fully
            // decodable by every peer. Resync requests and outbox acks carry no payload at all, so
            // identity is the correct and complete answer for them, and a frame kind introduced
            // later is at worst sent inline. Failing closed here would make this transport refuse
            // to send control frames. Pinned by `AnUnknownFrameIsForwardedUnspilled`.
            _ => message,
        };

        return new BlobSpillResult(result, total);
    }

    /// <summary>Resolves an inline value or a descriptor against one backend.</summary>
    public static bool TryResolve(
        IpcValue value,
        IBlobBackend backend,
        out ReadOnlyMemory<byte> view)
    {
        Guard.NotNull(value, nameof(value));
        Guard.NotNull(backend, nameof(backend));
        if (value is IpcValue.Inline inline)
        {
            view = inline.Bytes;
            return true;
        }

        return backend.TryReadView(((IpcValue.SharedBlob)value).Blob, out view);
    }

    private static NodeState SpillState(
        NodeState state,
        IBlobBackend backend,
        int threshold,
        ref ulong total)
    {
        if (state is not NodeState.Payload payload || payload.Bytes.Length < threshold)
        {
            return state;
        }

        try
        {
            total += checked((ulong)payload.Bytes.Length);
            return new NodeState.SharedBlob(backend.Write(payload.Bytes));
        }
        catch (ShmBlobArenaException)
        {
            total -= checked((ulong)payload.Bytes.Length);
            return state;
        }
    }

    private static DeltaOp SpillOperation(
        DeltaOp operation,
        IBlobBackend backend,
        int threshold,
        ref ulong total)
    {
        return operation switch
        {
            DeltaOp.CellSet cell => cell with
            {
                Payload = SpillAndCount(cell.Payload, backend, threshold, ref total),
            },
            DeltaOp.SlotValue slot => slot with
            {
                Payload = SpillAndCount(slot.Payload, backend, threshold, ref total),
            },
            DeltaOp.NodeAdd add => add with
            {
                State = SpillState(add.State, backend, threshold, ref total),
            },
            DeltaOp.QueuePush push => push with
            {
                Payload = SpillAndCount(push.Payload, backend, threshold, ref total),
            },

            // INTENTIONAL, same contract as SpillMessage above: these four are every DeltaOp that
            // carries bytes. Invalidate, NodeRemove, the edge ops, QueuePop and QueueClose carry
            // only ids, so there is nothing to page out and identity is exact — and an op variant
            // a newer peer relays through is forwarded inline rather than dropped.
            // Pinned by `AnUnknownDeltaOpIsForwardedUnspilled`.
            _ => operation,
        };
    }

    private static IpcValue SpillAndCount(
        IpcValue value,
        IBlobBackend backend,
        int threshold,
        ref ulong total)
    {
        var spilled = SpillValue(value, backend, threshold);
        total += spilled.BytesSpilled;
        return spilled.Value;
    }

    private static void ValidateThreshold(int threshold)
    {
        if (threshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }
    }
}

/// <summary>The transformed message and number of payload bytes moved to a backend.</summary>
public sealed record BlobSpillResult(IpcMessage Message, ulong BytesSpilled);

/// <summary>Receiver-side resolver routing descriptors by backend discriminator.</summary>
public sealed class BlobRouter
{
    private readonly Dictionary<BlobBackendKind, IBlobBackend> _backends = [];

    /// <summary>Registers or replaces a backend and returns this router.</summary>
    public BlobRouter Register(IBlobBackend backend)
    {
        Guard.NotNull(backend, nameof(backend));
        _backends[backend.Kind] = backend;
        return this;
    }

    /// <summary>Resolves a descriptor against the backend selected by its discriminator.</summary>
    public bool TryReadView(ShmBlobRef descriptor, out ReadOnlyMemory<byte> view)
    {
        if (!_backends.TryGetValue(descriptor.EffectiveBackend(), out var backend))
        {
            view = default;
            return false;
        }

        return backend.TryReadView(descriptor, out view);
    }

    /// <summary>Resolves an inline value directly or routes its descriptor.</summary>
    public bool TryResolve(IpcValue value, out ReadOnlyMemory<byte> view)
    {
        Guard.NotNull(value, nameof(value));
        if (value is IpcValue.Inline inline)
        {
            view = inline.Bytes;
            return true;
        }

        return TryReadView(((IpcValue.SharedBlob)value).Blob, out view);
    }
}
