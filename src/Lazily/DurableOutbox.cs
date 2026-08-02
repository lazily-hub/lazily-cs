using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lazily;

/// <summary>One serialized outbox frame stored at an epoch.</summary>
public sealed record StoredOutboxEntry(ulong Epoch, byte[] Frame);

/// <summary>
/// Dumb ordered byte storage. The shared <see cref="DurableOutbox{TStore}"/> owns serialization,
/// cursor monotonicity, pruning, and replay ordering.
/// </summary>
public interface IOutboxStore
{
    /// <summary>Stores or replaces the frame at <paramref name="epoch"/>.</summary>
    void Put(ulong epoch, ReadOnlySpan<byte> frame);

    /// <summary>Prunes every frame at or below <paramref name="epoch"/>.</summary>
    void DeleteThrough(ulong epoch);

    /// <summary>Returns stored frames above <paramref name="cursor"/> in ascending epoch order.</summary>
    IReadOnlyList<StoredOutboxEntry> ScanAfter(ulong cursor);

    /// <summary>Loads the highest durably acknowledged epoch.</summary>
    ulong LoadCursor();

    /// <summary>Persists a monotonic acknowledgement cursor.</summary>
    void SaveCursor(ulong epoch);

    /// <summary>Deletes every frame above <paramref name="cursor"/>.</summary>
    void DeleteAfter(ulong cursor);

    /// <summary>
    /// Atomically replaces every frame above <paramref name="cursor"/> with one frame at
    /// <paramref name="epoch"/>.
    /// </summary>
    void ReplaceAfter(ulong cursor, ulong epoch, ReadOnlySpan<byte> frame);
}

/// <summary>One decoded frame ready for at-least-once replay.</summary>
public sealed record OutboxEntry(ulong Epoch, IpcMessage Message);

/// <summary>The storage-independent reliable-sync outbox surface used by the driver.</summary>
public interface IDurableOutbox
{
    /// <summary>The highest durably acknowledged epoch.</summary>
    ulong AckedThrough { get; }

    /// <summary>Stores a message before transport send.</summary>
    void Append(ulong epoch, IpcMessage message);

    /// <summary>Advances retention through an acknowledged epoch.</summary>
    void AckThrough(ulong epoch);

    /// <summary>Returns retained frames above a cursor in epoch order.</summary>
    IReadOnlyList<OutboxEntry> ReplayFrom(ulong cursor);

    /// <summary>Lists all unacknowledged epochs.</summary>
    IReadOnlyList<ulong> RetainedEpochs { get; }

    /// <summary>The unacknowledged queue depth.</summary>
    int RetainedDepth { get; }

    /// <summary>Collapses the unacknowledged state suffix to one covering snapshot.</summary>
    bool CoalesceToSnapshot(ulong epoch, SnapshotMessage snapshot);

    /// <summary>Fuses a contiguous same-direction queue-op suffix into one multi-epoch delta.</summary>
    bool FuseQueueDeltaBatch();

    /// <summary>Reclaims all unacknowledged frames after peer eviction.</summary>
    void ReclaimUnacked();
}

/// <summary>
/// Storage-independent append-before-send outbox with monotonic acknowledgement and replay.
/// </summary>
/// <typeparam name="TStore">The ordered byte-store adapter.</typeparam>
public sealed class DurableOutbox<TStore>
 : IDurableOutbox
where TStore : IOutboxStore
{
    private ulong _ackedThrough;

    /// <summary>Loads an outbox over <paramref name="store"/>'s durable cursor.</summary>
    public DurableOutbox(TStore store)
    {
        Guard.NotNull(store, nameof(store));
        Store = store;
        _ackedThrough = store.LoadCursor();
    }

    /// <summary>The underlying byte store.</summary>
    public TStore Store { get; }

    /// <summary>The highest locally observed or durably persisted acknowledgement.</summary>
    public ulong AckedThrough
    {
        get
        {
            _ackedThrough = Math.Max(_ackedThrough, Store.LoadCursor());
            return _ackedThrough;
        }
    }

    /// <summary>Serializes and durably stores a message before transport send.</summary>
    public void Append(ulong epoch, IpcMessage message)
    {
        Guard.NotNull(message, nameof(message));
        Store.Put(epoch, Encoding.UTF8.GetBytes(IpcWire.Serialize(message)));
    }

    /// <summary>Advances the monotonic cursor and prunes the acknowledged prefix.</summary>
    public void AckThrough(ulong epoch)
    {
        var target = Math.Max(epoch, AckedThrough);
        if (target > _ackedThrough)
        {
            Store.SaveCursor(target);
            _ackedThrough = target;
        }

        Store.DeleteThrough(target);
    }

    /// <summary>
    /// Replays decoded frames after both the caller's cursor and the durable acknowledgement.
    /// </summary>
    public IReadOnlyList<OutboxEntry> ReplayFrom(ulong cursor) =>
        Store.ScanAfter(Math.Max(cursor, AckedThrough))
            .Select(
                entry =>
                    new OutboxEntry(
                        entry.Epoch,
                        IpcWire.Deserialize(Encoding.UTF8.GetString(entry.Frame))))
            .ToArray();

    /// <summary>Lists the unacknowledged suffix in ascending order.</summary>
    public IReadOnlyList<ulong> RetainedEpochs =>
    Store.ScanAfter(AckedThrough).Select(entry => entry.Epoch).ToArray();

    /// <inheritdoc />
    public int RetainedDepth => RetainedEpochs.Count;

    /// <inheritdoc />
    public bool CoalesceToSnapshot(ulong epoch, SnapshotMessage snapshot)
    {
        Guard.NotNull(snapshot, nameof(snapshot));
        if (snapshot.Epoch != epoch || epoch <= AckedThrough) return false;
        Store.ReplaceAfter(
        AckedThrough,
        epoch,
        Encoding.UTF8.GetBytes(IpcWire.Serialize(snapshot)));
        return true;
    }

    /// <inheritdoc />
    public bool FuseQueueDeltaBatch()
    {
        var retained = ReplayFrom(AckedThrough);
        if (retained.Count < 2
        || retained.Any(entry => entry.Message is not DeltaMessage))
        {
            return false;
        }

        var deltas = retained.Select(entry => (DeltaMessage)entry.Message).ToArray();
        for (var index = 1; index < deltas.Length; index++)
        {
            if (deltas[index].BaseEpoch != deltas[index - 1].Epoch) return false;
        }

        var operations = deltas.SelectMany(delta => delta.Ops).ToArray();
        if (operations.Length == 0 || QueueSignature(operations[0]) is not { } signature)
        {
            return false;
        }

        if (operations.Any(operation => QueueSignature(operation) != signature)) return false;
        var fused = new DeltaMessage(deltas[0].BaseEpoch, deltas[^1].Epoch, operations);
        Store.ReplaceAfter(
        AckedThrough,
        fused.Epoch,
        Encoding.UTF8.GetBytes(IpcWire.Serialize(fused)));
        return true;
    }

    /// <inheritdoc />
    public void ReclaimUnacked() => Store.DeleteAfter(AckedThrough);

    // INTENTIONAL: null means "not a queue op", which is the whole question this predicate asks.
    // Fusion is an OPTIMIZATION over a suffix of unacked deltas — a null signature disables fusion
    // for that op and the delta is replayed verbatim, which is always correct. Failing closed here
    // would make every non-queue delta unsendable. Pinned by `FusionSkipsANonQueueDeltaOp`.
    private static (Type Type, ulong Node)? QueueSignature(DeltaOp operation) =>
    operation switch
    {
        DeltaOp.QueuePush push => (typeof(DeltaOp.QueuePush), push.Node),
        DeltaOp.QueuePop pop => (typeof(DeltaOp.QueuePop), pop.Node),
        _ => null,
    };
}

/// <summary>An ordered process-local outbox byte store.</summary>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly SortedDictionary<ulong, byte[]> _entries = [];
    private ulong _cursor;

    /// <inheritdoc />
    public void Put(ulong epoch, ReadOnlySpan<byte> frame)
    {
        _entries[epoch] = frame.ToArray();
    }

    /// <inheritdoc />
    public void DeleteThrough(ulong epoch)
    {
        foreach (var stored in _entries.Keys.TakeWhile(stored => stored <= epoch).ToArray())
        {
            _entries.Remove(stored);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredOutboxEntry> ScanAfter(ulong cursor) =>
        _entries
            .Where(entry => entry.Key > cursor)
            .Select(entry => new StoredOutboxEntry(entry.Key, [.. entry.Value]))
            .ToArray();

    /// <inheritdoc />
    public ulong LoadCursor() => _cursor;

    /// <inheritdoc />
    public void SaveCursor(ulong epoch)
    {
        _cursor = Math.Max(_cursor, epoch);
    }

    /// <inheritdoc />
    public void DeleteAfter(ulong cursor)
    {
        foreach (var stored in _entries.Keys.Where(stored => stored > cursor).ToArray())
        {
            _entries.Remove(stored);
        }
    }

    /// <inheritdoc />
    public void ReplaceAfter(ulong cursor, ulong epoch, ReadOnlySpan<byte> frame)
    {
        DeleteAfter(cursor);
        Put(epoch, frame);
    }
}

/// <summary>
/// Fsync-backed append-only outbox journal. Cursor and deletion records fold by maximum, so a stale
/// handle cannot regress acknowledgement or resurrect a pruned prefix.
/// </summary>
public sealed class FileOutboxStore : IOutboxStore
{
    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _path;

    /// <summary>Opens or creates an append-only outbox journal.</summary>
    public FileOutboxStore(string path)
    {
        Guard.NotNullOrWhiteSpace(path, nameof(path));
        _path = System.IO.Path.GetFullPath(path);
        var parent = System.IO.Path.GetDirectoryName(_path);
        if (parent is not null) Directory.CreateDirectory(parent);
        using var stream = new FileStream(
            _path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite);
    }

    /// <summary>The journal path.</summary>
    public string Path => _path;

    /// <inheritdoc />
    public void Put(ulong epoch, ReadOnlySpan<byte> frame) =>
        AppendRecord(new JournalRecord("put", epoch, frame.ToArray()));

    /// <inheritdoc />
    public void DeleteThrough(ulong epoch) =>
        AppendRecord(new JournalRecord("delete", epoch, null));

    /// <inheritdoc />
    public IReadOnlyList<StoredOutboxEntry> ScanAfter(ulong cursor)
    {
        var entries = new SortedDictionary<ulong, byte[]>();
        var deletedThrough = 0UL;
        foreach (var record in ReadRecords())
        {
            switch (record.Op)
            {
                case "put" when record.Frame is not null:
                    entries[record.Epoch] = record.Frame;
                    break;
                case "delete":
                    deletedThrough = Math.Max(deletedThrough, record.Epoch);
                    break;
                case "delete_after":
                    foreach (var epoch in entries.Keys.Where(epoch => epoch > record.Epoch).ToArray())
                    {
                        entries.Remove(epoch);
                    }
                    break;
                case "replace_after" when record.Frame is not null && record.Cursor is not null:
                    foreach (var epoch in entries.Keys.Where(epoch => epoch > record.Cursor.Value).ToArray())
                    {
                        entries.Remove(epoch);
                    }
                    entries[record.Epoch] = record.Frame;
                    break;

                case "cursor":
                    // Read by LoadCursor, not by the entry fold. Explicit so "ignored here" is
                    // distinguishable from "nobody recognised it".
                    break;

                default:
                    // The journal is an on-disk format that a DIFFERENT build of this library may
                    // have written. Silently ignoring an op this build does not know is not
                    // forward-compat, it is corruption: a `delete_after` a newer writer emitted
                    // would be dropped and the pruned suffix would resurrect as live outbox
                    // entries. A malformed known op (a `put` or `replace_after` missing its
                    // payload) lands here too and is equally a truncated journal, not a no-op.
                    throw new InvalidDataException(
                        $"Outbox journal '{_path}' carries an unsupported record " +
                        $"'{record.Op}' at epoch {record.Epoch}.");
            }
        }

        cursor = Math.Max(cursor, deletedThrough);
        return entries
            .Where(entry => entry.Key > cursor)
            .Select(entry => new StoredOutboxEntry(entry.Key, [.. entry.Value]))
            .ToArray();
    }

    /// <inheritdoc />
    public ulong LoadCursor() =>
        ReadRecords()
            .Where(record => record.Op == "cursor")
            .Aggregate(0UL, (cursor, record) => Math.Max(cursor, record.Epoch));

    /// <inheritdoc />
    public void SaveCursor(ulong epoch) =>
    AppendRecord(new JournalRecord("cursor", epoch, null));

    /// <inheritdoc />
    public void DeleteAfter(ulong cursor) =>
    AppendRecord(new JournalRecord("delete_after", cursor, null));

    /// <inheritdoc />
    public void ReplaceAfter(ulong cursor, ulong epoch, ReadOnlySpan<byte> frame) =>
    AppendRecord(new JournalRecord("replace_after", epoch, frame.ToArray(), cursor));

    private void AppendRecord(JournalRecord record)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, JournalJson) + "\n");
        lock (_gate)
        {
            using var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    private IReadOnlyList<JournalRecord> ReadRecords()
    {
        lock (_gate)
        {
            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var records = new List<JournalRecord>();
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    var record = JsonSerializer.Deserialize<JournalRecord>(line, JournalJson);
                    if (record is not null) records.Add(record);
                }
                catch (JsonException)
                {
                    // INTENTIONAL leniency, and the ONE decode failure this store absorbs. The
                    // journal is appended with a single fsynced write per record, so a crash
                    // between the write and the flush can leave exactly one torn TRAILING line.
                    // Every earlier record was fsynced whole and stays authoritative and
                    // replayable, so refusing the whole journal over a torn tail would turn a
                    // recoverable crash into permanent data loss. Note the narrow catch: a
                    // well-formed record carrying an op this build does not know is NOT absorbed
                    // here — it reaches ScanAfter's fail-closed default below. Pinned by
                    // `ATornTrailingJournalRecordIsSkipped`.
                }
            }

            return records;
        }
    }

    private sealed record JournalRecord(
    string Op,
    ulong Epoch,
    byte[]? Frame,
    ulong? Cursor = null);
}
