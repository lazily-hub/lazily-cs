using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lazily;

/// <summary>The schema-defined state, CRDT, and reliable-sync control message family.</summary>
[JsonConverter(typeof(IpcMessageJsonConverter))]
public abstract record IpcMessage;

/// <summary>A full graph-state message.</summary>
public sealed record SnapshotMessage(
    ulong Epoch,
    IReadOnlyList<NodeSnapshot> Nodes,
    IReadOnlyList<EdgeSnapshot> Edges,
    IReadOnlyList<ulong> Roots) : IpcMessage;

/// <summary>An incremental ordered graph mutation message.</summary>
public sealed record DeltaMessage(
    ulong BaseEpoch,
    ulong Epoch,
    IReadOnlyList<DeltaOp> Ops) : IpcMessage;

/// <summary>A state-based CRDT anti-entropy frame.</summary>
public sealed record CrdtSyncMessage(
IReadOnlyList<CrdtOp> Ops,
IReadOnlyList<StampFrontierEntry>? Frontier = null) : IpcMessage;

/// <summary>Requests a full snapshot covering the receiver's current epoch.</summary>
public sealed record ResyncRequestMessage(ulong FromEpoch) : IpcMessage;

/// <summary>Proves that a receiver has fully applied frames through an epoch.</summary>
public sealed record OutboxAckMessage(ulong ThroughEpoch) : IpcMessage;

/// <summary>Full wire state for one node.</summary>
public sealed record NodeSnapshot(ulong Node, string TypeTag, NodeState State, string? Key = null);

/// <summary>A dependent-to-dependency edge.</summary>
public sealed record EdgeSnapshot(ulong Dependent, ulong Dependency);

/// <summary>A shared blob descriptor.</summary>
public sealed record ShmBlobRef(
    ulong Offset,
    ulong Length,
    ulong Generation,
    ulong Epoch,
    ulong Checksum,
    BlobBackendKind? Backend = null);

/// <summary>The backend that resolves a shared blob descriptor.</summary>
public enum BlobBackendKind
{
    /// <summary>POSIX shared memory; the wire default when omitted.</summary>
    Shm,

    /// <summary>Apache Arrow IPC or Flight.</summary>
    Arrow,

    /// <summary>An in-process blob arena.</summary>
    InProcess,
}

/// <summary>A snapshot node-state union.</summary>
public abstract record NodeState
{
    /// <summary>Concrete serialized bytes.</summary>
    public sealed record Payload(byte[] Bytes) : NodeState;

    /// <summary>A shared blob descriptor.</summary>
    public sealed record SharedBlob(ShmBlobRef Blob) : NodeState;

    /// <summary>A visible but non-serializable node.</summary>
    public sealed record Opaque : NodeState;
}

/// <summary>A delta or CRDT-state payload union.</summary>
public abstract record IpcValue
{
    /// <summary>Inline serialized bytes.</summary>
    public sealed record Inline(byte[] Bytes) : IpcValue;

    /// <summary>A shared blob descriptor.</summary>
    public sealed record SharedBlob(ShmBlobRef Blob) : IpcValue;
}

/// <summary>An externally tagged incremental graph mutation.</summary>
public abstract record DeltaOp
{
    /// <summary>Writes a source cell.</summary>
    public sealed record CellSet(ulong Node, IpcValue Payload) : DeltaOp;

    /// <summary>Publishes a derived slot value.</summary>
    public sealed record SlotValue(ulong Node, IpcValue Payload) : DeltaOp;

    /// <summary>Invalidates a node.</summary>
    public sealed record Invalidate(ulong Node) : DeltaOp;

    /// <summary>Adds a node and its initial state.</summary>
    public sealed record NodeAdd(
        ulong Node,
        string TypeTag,
        NodeState State,
        string? Key = null) : DeltaOp;

    /// <summary>Removes a node.</summary>
    public sealed record NodeRemove(ulong Node) : DeltaOp;

    /// <summary>Adds a dependency edge.</summary>
    public sealed record EdgeAdd(ulong Dependent, ulong Dependency) : DeltaOp;

    /// <summary>Removes a dependency edge.</summary>
    public sealed record EdgeRemove(ulong Dependent, ulong Dependency) : DeltaOp;

    /// <summary>Appends a queue payload.</summary>
    public sealed record QueuePush(ulong Node, IpcValue Payload) : DeltaOp;

    /// <summary>Removes the authoritative queue head.</summary>
    public sealed record QueuePop(ulong Node) : DeltaOp;

    /// <summary>Closes a queue.</summary>
    public sealed record QueueClose(ulong Node) : DeltaOp;
}

/// <summary>The schema's unsigned wire mirror of <see cref="HlcStamp"/>.</summary>
public sealed record WireStamp(ulong WallTime, ulong Logical, ulong Peer)
: IComparable<WireStamp>
{
    /// <summary>Orders stamps by wall time, logical counter, then peer.</summary>
    public int CompareTo(WireStamp? other) =>
    other is null
    ? 1
    : (WallTime, Logical, Peer).CompareTo((other.WallTime, other.Logical, other.Peer));

    /// <summary>Converts a runtime stamp, rejecting values the unsigned wire cannot represent.</summary>
    public static WireStamp FromRuntime(HlcStamp stamp)
    {
        if (stamp.Micros < 0 || stamp.Counter < 0 || stamp.Peer < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stamp),
                "Wire stamps require non-negative wall time, logical counter, and peer.");
        }

        return new WireStamp(
            checked((ulong)stamp.Micros),
            checked((ulong)stamp.Counter),
            checked((ulong)stamp.Peer));
    }

    /// <summary>Converts to the runtime stamp, rejecting values outside its signed representation.</summary>
    public HlcStamp ToRuntime() => new(
        checked((long)WallTime),
        checked((long)Logical),
        checked((int)Peer));
}

/// <summary>One peer's advertised highest observed stamp.</summary>
public sealed record StampFrontierEntry(ulong Peer, WireStamp Stamp);

/// <summary>One state-based CRDT operation.</summary>
public sealed record CrdtOp(
    ulong Node,
    string? Key,
    WireStamp Stamp,
    IpcValue State);

/// <summary>Exact JSON codec for the externally tagged IPC family.</summary>
public static class IpcWire
{
    private static readonly IpcMessageJsonConverter Converter = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    /// <summary>Serializes one message in the lazily-spec wire shape.</summary>
    public static string Serialize(IpcMessage message)
    {
        Guard.NotNull(message, nameof(message));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Converter.Write(writer, message, Options);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Deserializes one externally tagged lazily-spec message.</summary>
    public static IpcMessage Deserialize(string json)
    {
        Guard.NotNullOrWhiteSpace(json, nameof(json));
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        if (!reader.Read()) throw new JsonException("IPC message is empty.");
        var message = Converter.Read(ref reader, typeof(IpcMessage), Options);
        if (reader.Read()) throw new JsonException("IPC message has trailing content.");
        return message;
    }
}

internal sealed class IpcMessageJsonConverter : JsonConverter<IpcMessage>
{
    public override IpcMessage Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var envelope = document.RootElement;
        RequireObject(envelope, "IPC envelope");
        var property = SingleProperty(envelope, "IPC envelope");
        return property.Name switch
        {
            "Snapshot" => ReadSnapshot(property.Value),
            "Delta" => ReadDelta(property.Value),
            "CrdtSync" => ReadCrdtSync(property.Value),
            "ResyncRequest" => ReadResyncRequest(property.Value),
            "OutboxAck" => ReadOutboxAck(property.Value),
            _ => throw new JsonException($"Unknown IPC message variant '{property.Name}'."),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        IpcMessage value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case SnapshotMessage snapshot:
                writer.WritePropertyName("Snapshot");
                WriteSnapshot(writer, snapshot);
                break;
            case DeltaMessage delta:
                writer.WritePropertyName("Delta");
                WriteDelta(writer, delta);
                break;
            case CrdtSyncMessage sync:
                writer.WritePropertyName("CrdtSync");
                WriteCrdtSync(writer, sync);
                break;
            case ResyncRequestMessage request:
                writer.WritePropertyName("ResyncRequest");
                WriteSingleEpoch(writer, "from_epoch", request.FromEpoch);
                break;
            case OutboxAckMessage acknowledgement:
                writer.WritePropertyName("OutboxAck");
                WriteSingleEpoch(writer, "through_epoch", acknowledgement.ThroughEpoch);
                break;
            default:
                throw new JsonException($"Unsupported IPC message type '{value.GetType().Name}'.");
        }
        writer.WriteEndObject();
    }

    private static SnapshotMessage ReadSnapshot(JsonElement body)
    {
        RequireObject(body, "Snapshot");
        return new SnapshotMessage(
            Required(body, "epoch").GetUInt64(),
            Required(body, "nodes").EnumerateArray().Select(ReadNodeSnapshot).ToArray(),
            Required(body, "edges").EnumerateArray().Select(ReadEdge).ToArray(),
            Required(body, "roots").EnumerateArray().Select(item => item.GetUInt64()).ToArray());
    }

    private static DeltaMessage ReadDelta(JsonElement body)
    {
        RequireObject(body, "Delta");
        return new DeltaMessage(
            Required(body, "base_epoch").GetUInt64(),
            Required(body, "epoch").GetUInt64(),
            Required(body, "ops").EnumerateArray().Select(ReadDeltaOp).ToArray());
    }

    private static CrdtSyncMessage ReadCrdtSync(JsonElement body)
    {
        RequireObject(body, "CrdtSync");
        IReadOnlyList<StampFrontierEntry>? frontier = null;
        if (body.TryGetProperty("frontier", out var frontierElement))
        {
            frontier = frontierElement.EnumerateArray().Select(ReadFrontierEntry).ToArray();
        }

        return new CrdtSyncMessage(
            Required(body, "ops").EnumerateArray().Select(ReadCrdtOp).ToArray(),
frontier);
    }

    private static ResyncRequestMessage ReadResyncRequest(JsonElement body)
    {
        RequireExactProperties(body, "ResyncRequest", "from_epoch");
        return new ResyncRequestMessage(Required(body, "from_epoch").GetUInt64());
    }

    private static OutboxAckMessage ReadOutboxAck(JsonElement body)
    {
        RequireExactProperties(body, "OutboxAck", "through_epoch");
        return new OutboxAckMessage(Required(body, "through_epoch").GetUInt64());
    }

    private static NodeSnapshot ReadNodeSnapshot(JsonElement body)
    {
        RequireObject(body, "NodeSnapshot");
        return new NodeSnapshot(
            Required(body, "node").GetUInt64(),
            Required(body, "type_tag").GetString()
                ?? throw new JsonException("NodeSnapshot.type_tag cannot be null."),
            ReadNodeState(Required(body, "state")),
            OptionalString(body, "key"));
    }

    private static EdgeSnapshot ReadEdge(JsonElement body)
    {
        RequireObject(body, "edge");
        return new EdgeSnapshot(
            Required(body, "dependent").GetUInt64(),
            Required(body, "dependency").GetUInt64());
    }

    private static DeltaOp ReadDeltaOp(JsonElement envelope)
    {
        RequireObject(envelope, "DeltaOp");
        var property = SingleProperty(envelope, "DeltaOp");
        var body = property.Value;
        RequireObject(body, property.Name);
        return property.Name switch
        {
            "CellSet" => new DeltaOp.CellSet(
                Required(body, "node").GetUInt64(),
                ReadIpcValue(Required(body, "payload"))),
            "SlotValue" => new DeltaOp.SlotValue(
                Required(body, "node").GetUInt64(),
                ReadIpcValue(Required(body, "payload"))),
            "Invalidate" => new DeltaOp.Invalidate(Required(body, "node").GetUInt64()),
            "NodeAdd" => new DeltaOp.NodeAdd(
                Required(body, "node").GetUInt64(),
                Required(body, "type_tag").GetString()
                    ?? throw new JsonException("NodeAdd.type_tag cannot be null."),
                ReadNodeState(Required(body, "state")),
                OptionalString(body, "key")),
            "NodeRemove" => new DeltaOp.NodeRemove(Required(body, "node").GetUInt64()),
            "EdgeAdd" => new DeltaOp.EdgeAdd(
                Required(body, "dependent").GetUInt64(),
                Required(body, "dependency").GetUInt64()),
            "EdgeRemove" => new DeltaOp.EdgeRemove(
                Required(body, "dependent").GetUInt64(),
                Required(body, "dependency").GetUInt64()),
            "QueuePush" => new DeltaOp.QueuePush(
                Required(body, "node").GetUInt64(),
                ReadIpcValue(Required(body, "payload"))),
            "QueuePop" => new DeltaOp.QueuePop(Required(body, "node").GetUInt64()),
            "QueueClose" => new DeltaOp.QueueClose(Required(body, "node").GetUInt64()),
            _ => throw new JsonException($"Unknown DeltaOp variant '{property.Name}'."),
        };
    }

    private static StampFrontierEntry ReadFrontierEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2)
        {
            throw new JsonException("A frontier entry must be a two-item array.");
        }

        return new StampFrontierEntry(
            element[0].GetUInt64(),
            ReadWireStamp(element[1]));
    }

    private static CrdtOp ReadCrdtOp(JsonElement body)
    {
        RequireObject(body, "CrdtOp");
        var keyElement = Required(body, "key");
        var key = keyElement.ValueKind == JsonValueKind.Null
            ? null
            : keyElement.GetString();
        return new CrdtOp(
            Required(body, "node").GetUInt64(),
            key,
            ReadWireStamp(Required(body, "stamp")),
            ReadIpcValue(Required(body, "state")));
    }

    private static WireStamp ReadWireStamp(JsonElement body)
    {
        RequireObject(body, "WireStamp");
        return new WireStamp(
            Required(body, "wall_time").GetUInt64(),
            Required(body, "logical").GetUInt64(),
            Required(body, "peer").GetUInt64());
    }

    private static NodeState ReadNodeState(JsonElement envelope)
    {
        if (envelope.ValueKind == JsonValueKind.String)
        {
            return envelope.GetString() == "Opaque"
                ? new NodeState.Opaque()
                : throw new JsonException("The only string NodeState is 'Opaque'.");
        }

        RequireObject(envelope, "NodeState");
        var property = SingleProperty(envelope, "NodeState");
        return property.Name switch
        {
            "Payload" => new NodeState.Payload(ReadBytes(property.Value)),
            "SharedBlob" => new NodeState.SharedBlob(ReadBlob(property.Value)),
            _ => throw new JsonException($"Unknown NodeState variant '{property.Name}'."),
        };
    }

    internal static IpcValue ReadIpcValue(JsonElement envelope)
    {
        RequireObject(envelope, "IpcValue");
        var property = SingleProperty(envelope, "IpcValue");
        return property.Name switch
        {
            "Inline" => new IpcValue.Inline(ReadBytes(property.Value)),
            "SharedBlob" => new IpcValue.SharedBlob(ReadBlob(property.Value)),
            _ => throw new JsonException($"Unknown IpcValue variant '{property.Name}'."),
        };
    }

    private static byte[] ReadBytes(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Serialized bytes must be a JSON array, not base64.");
        }

        return array.EnumerateArray().Select(item => item.GetByte()).ToArray();
    }

    private static ShmBlobRef ReadBlob(JsonElement body)
    {
        RequireObject(body, "ShmBlobRef");
        BlobBackendKind? backend = null;

        // #lzblobbackendstrict. An OMITTED `backend` is the forward-compatibility channel — it
        // carries every descriptor minted before the field existed — and only that. A PRESENT
        // value outside the enum is a different fact and gets the opposite answer: refuse the
        // frame and NAME the token. Normalizing an unknown backend to `shm` (five bindings did,
        // each calling it forward-compat) routes a non-shm descriptor into the shm table, which
        // is the misroute `resolve_wrong_backend` discharges STRUCTURALLY by routing on kind;
        // leniency downgrades that to a probabilistic guarantee riding on a 64-bit checksum
        // against a backend this build genuinely resolves — so a collision returns BYTES, where
        // a refusal is a visible protocol error the peer recovers from by resync.
        if (body.TryGetProperty("backend", out var backendElement)
            && backendElement.ValueKind != JsonValueKind.Null)
        {
            // An explicit `backend: null` is the ABSENT form, not a present-unknown one, and is
            // filtered out by the guard above so it leaves `backend` null — decoding as `shm`
            // through EffectiveBackend, and re-encoding with no `backend` entry at all
            // (#lzkeynullstrict, the same rule NodeKey follows). A serde-style peer that did not
            // apply `skip_serializing_if` to an optional field emits null where a conforming
            // encoder omits, so refusing it would be stricter than the reference implementation
            // on a frame the reference implementation produces. This binding used to refuse it —
            // first as the token `''`, naming nothing, then as a ValueKind error — which is the
            // right shape of failure for the wrong fact.
            //
            // A non-string `backend` is the opposite case and still refuses. It used to escape as
            // InvalidOperationException out of GetString(), which is outside this codec's declared
            // failure mode: both MUST-level codecs document JsonException so a caller can guard a
            // decode with ONE catch, and a refusal raised outside that family fails PAST the
            // handler — the frame is still refused and the peer never sees the error.
            if (backendElement.ValueKind != JsonValueKind.String)
            {
                throw new JsonException(
                    $"Blob backend must be one of 'shm', 'arrow', or 'in_process', not the "
                    + $"{backendElement.ValueKind} value '{backendElement}'.");
            }

            backend = backendElement.GetString() switch
            {
                "shm" => BlobBackendKind.Shm,
                "arrow" => BlobBackendKind.Arrow,
                "in_process" => BlobBackendKind.InProcess,
                var value => throw new JsonException($"Unknown blob backend '{value}'."),
            };
        }

        return new ShmBlobRef(
            Required(body, "offset").GetUInt64(),
            Required(body, "len").GetUInt64(),
            Required(body, "generation").GetUInt64(),
            Required(body, "epoch").GetUInt64(),
            Required(body, "checksum").GetUInt64(),
            backend);
    }

    private static void WriteSnapshot(Utf8JsonWriter writer, SnapshotMessage snapshot)
    {
        writer.WriteStartObject();
        writer.WriteNumber("epoch", snapshot.Epoch);
        writer.WritePropertyName("nodes");
        writer.WriteStartArray();
        foreach (var node in snapshot.Nodes) WriteNodeSnapshot(writer, node);
        writer.WriteEndArray();
        writer.WritePropertyName("edges");
        writer.WriteStartArray();
        foreach (var edge in snapshot.Edges) WriteEdge(writer, edge);
        writer.WriteEndArray();
        writer.WritePropertyName("roots");
        writer.WriteStartArray();
        foreach (var root in snapshot.Roots) writer.WriteNumberValue(root);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDelta(Utf8JsonWriter writer, DeltaMessage delta)
    {
        writer.WriteStartObject();
        writer.WriteNumber("base_epoch", delta.BaseEpoch);
        writer.WriteNumber("epoch", delta.Epoch);
        writer.WritePropertyName("ops");
        writer.WriteStartArray();
        foreach (var operation in delta.Ops) WriteDeltaOp(writer, operation);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteCrdtSync(Utf8JsonWriter writer, CrdtSyncMessage sync)
    {
        writer.WriteStartObject();
        if (sync.Frontier is not null)
        {
            writer.WritePropertyName("frontier");
            writer.WriteStartArray();
            foreach (var entry in sync.Frontier)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(entry.Peer);
                WriteWireStamp(writer, entry.Stamp);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }

        writer.WritePropertyName("ops");
        writer.WriteStartArray();
        foreach (var operation in sync.Ops) WriteCrdtOp(writer, operation);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSingleEpoch(Utf8JsonWriter writer, string name, ulong epoch)
    {
        writer.WriteStartObject();
        writer.WriteNumber(name, epoch);
        writer.WriteEndObject();
    }

    private static void WriteNodeSnapshot(Utf8JsonWriter writer, NodeSnapshot node)
    {
        writer.WriteStartObject();
        writer.WriteNumber("node", node.Node);
        writer.WriteString("type_tag", node.TypeTag);
        writer.WritePropertyName("state");
        WriteNodeState(writer, node.State);
        if (node.Key is not null) writer.WriteString("key", node.Key);
        writer.WriteEndObject();
    }

    private static void WriteEdge(Utf8JsonWriter writer, EdgeSnapshot edge)
    {
        writer.WriteStartObject();
        writer.WriteNumber("dependent", edge.Dependent);
        writer.WriteNumber("dependency", edge.Dependency);
        writer.WriteEndObject();
    }

    private static void WriteDeltaOp(Utf8JsonWriter writer, DeltaOp operation)
    {
        writer.WriteStartObject();
        switch (operation)
        {
            case DeltaOp.CellSet op:
                WriteValueOp(writer, "CellSet", op.Node, op.Payload);
                break;
            case DeltaOp.SlotValue op:
                WriteValueOp(writer, "SlotValue", op.Node, op.Payload);
                break;
            case DeltaOp.Invalidate op:
                WriteNodeOp(writer, "Invalidate", op.Node);
                break;
            case DeltaOp.NodeAdd op:
                writer.WritePropertyName("NodeAdd");
                writer.WriteStartObject();
                writer.WriteNumber("node", op.Node);
                writer.WriteString("type_tag", op.TypeTag);
                writer.WritePropertyName("state");
                WriteNodeState(writer, op.State);
                if (op.Key is not null) writer.WriteString("key", op.Key);
                writer.WriteEndObject();
                break;
            case DeltaOp.NodeRemove op:
                WriteNodeOp(writer, "NodeRemove", op.Node);
                break;
            case DeltaOp.EdgeAdd op:
                WriteEdgeOp(writer, "EdgeAdd", op.Dependent, op.Dependency);
                break;
            case DeltaOp.EdgeRemove op:
                WriteEdgeOp(writer, "EdgeRemove", op.Dependent, op.Dependency);
                break;
            case DeltaOp.QueuePush op:
                WriteValueOp(writer, "QueuePush", op.Node, op.Payload);
                break;
            case DeltaOp.QueuePop op:
                WriteNodeOp(writer, "QueuePop", op.Node);
                break;
            case DeltaOp.QueueClose op:
                WriteNodeOp(writer, "QueueClose", op.Node);
                break;
            default:
                throw new JsonException($"Unsupported DeltaOp type '{operation.GetType().Name}'.");
        }
        writer.WriteEndObject();
    }

    private static void WriteValueOp(
        Utf8JsonWriter writer,
        string name,
        ulong node,
        IpcValue payload)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteNumber("node", node);
        writer.WritePropertyName("payload");
        WriteIpcValue(writer, payload);
        writer.WriteEndObject();
    }

    private static void WriteNodeOp(Utf8JsonWriter writer, string name, ulong node)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteNumber("node", node);
        writer.WriteEndObject();
    }

    private static void WriteEdgeOp(
        Utf8JsonWriter writer,
        string name,
        ulong dependent,
        ulong dependency)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteNumber("dependent", dependent);
        writer.WriteNumber("dependency", dependency);
        writer.WriteEndObject();
    }

    private static void WriteCrdtOp(Utf8JsonWriter writer, CrdtOp operation)
    {
        writer.WriteStartObject();
        writer.WriteNumber("node", operation.Node);
        if (operation.Key is null) writer.WriteNull("key");
        else writer.WriteString("key", operation.Key);
        writer.WritePropertyName("stamp");
        WriteWireStamp(writer, operation.Stamp);
        writer.WritePropertyName("state");
        WriteIpcValue(writer, operation.State);
        writer.WriteEndObject();
    }

    private static void WriteWireStamp(Utf8JsonWriter writer, WireStamp stamp)
    {
        writer.WriteStartObject();
        writer.WriteNumber("wall_time", stamp.WallTime);
        writer.WriteNumber("logical", stamp.Logical);
        writer.WriteNumber("peer", stamp.Peer);
        writer.WriteEndObject();
    }

    private static void WriteNodeState(Utf8JsonWriter writer, NodeState state)
    {
        switch (state)
        {
            case NodeState.Payload payload:
                writer.WriteStartObject();
                writer.WritePropertyName("Payload");
                WriteBytes(writer, payload.Bytes);
                writer.WriteEndObject();
                break;
            case NodeState.SharedBlob shared:
                writer.WriteStartObject();
                writer.WritePropertyName("SharedBlob");
                WriteBlob(writer, shared.Blob);
                writer.WriteEndObject();
                break;
            case NodeState.Opaque:
                writer.WriteStringValue("Opaque");
                break;
            default:
                throw new JsonException($"Unsupported NodeState type '{state.GetType().Name}'.");
        }
    }

    internal static void WriteIpcValue(Utf8JsonWriter writer, IpcValue value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case IpcValue.Inline inline:
                writer.WritePropertyName("Inline");
                WriteBytes(writer, inline.Bytes);
                break;
            case IpcValue.SharedBlob shared:
                writer.WritePropertyName("SharedBlob");
                WriteBlob(writer, shared.Blob);
                break;
            default:
                throw new JsonException($"Unsupported IpcValue type '{value.GetType().Name}'.");
        }
        writer.WriteEndObject();
    }

    private static void WriteBytes(Utf8JsonWriter writer, IEnumerable<byte> bytes)
    {
        writer.WriteStartArray();
        foreach (var value in bytes) writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }

    private static void WriteBlob(Utf8JsonWriter writer, ShmBlobRef blob)
    {
        writer.WriteStartObject();
        writer.WriteNumber("offset", blob.Offset);
        writer.WriteNumber("len", blob.Length);
        writer.WriteNumber("generation", blob.Generation);
        writer.WriteNumber("epoch", blob.Epoch);
        writer.WriteNumber("checksum", blob.Checksum);
        if (blob.Backend is not null and not BlobBackendKind.Shm)
        {
            writer.WriteString(
                "backend",
                blob.Backend switch
                {
                    BlobBackendKind.Shm => "shm",
                    BlobBackendKind.Arrow => "arrow",
                    BlobBackendKind.InProcess => "in_process",
                    _ => throw new JsonException($"Unsupported blob backend '{blob.Backend}'."),
                });
        }
        writer.WriteEndObject();
    }

    private static JsonProperty SingleProperty(JsonElement element, string context)
    {
        using var enumerator = element.EnumerateObject();
        if (!enumerator.MoveNext()) throw new JsonException($"{context} must contain one property.");
        var property = enumerator.Current;
        if (enumerator.MoveNext()) throw new JsonException($"{context} must contain one property.");
        return property;
    }

    private static JsonElement Required(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value
            : throw new JsonException($"Required property '{property}' is missing.");

    private static string? OptionalString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static void RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{context} must be an object.");
        }
    }

    private static void RequireExactProperties(
    JsonElement element,
    string context,
    params string[] expected)
    {
        RequireObject(element, context);
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length
        || actual.Any(name => !expected.Contains(name, StringComparer.Ordinal)))
        {
            throw new JsonException(
            $"{context} must contain exactly: {string.Join(", ", expected)}.");
        }
    }
}
