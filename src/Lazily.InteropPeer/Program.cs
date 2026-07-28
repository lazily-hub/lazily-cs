using System.Text.Json;
using System.Text.Json.Nodes;
using Lazily;

if (args.Contains("--self-check", StringComparer.Ordinal))
{
    InteropPeer.SelfCheck();
    Console.Error.WriteLine("lazily-cs interop peer self-check: ok");
    return;
}

var peer = new InteropPeer();
while (Console.In.ReadLine() is { } line)
{
    JsonObject? request = null;
    JsonObject response;
    try
    {
        request = JsonNode.Parse(line)?.AsObject()
            ?? throw new JsonException("request must be a JSON object");
        response = peer.Handle(request);
    }
    catch (Exception error)
    {
        response = new JsonObject
        {
            ["ok"] = false,
            ["error"] = error.Message,
        };
    }

    Console.Out.WriteLine(response.ToJsonString());
    Console.Out.Flush();
    if (request?["cmd"]?.GetValue<string>() == "bye") break;
}

internal sealed class InteropPeer
{
    private const ulong ProtocolVersion = 1;
    private const string BindingVersion = "0.3.0";

    private readonly Context _context = new();
    private readonly Dictionary<ulong, ReplicatedCell<InteropRegister, byte[]?>> _cells = [];
    private CrdtPlaneRuntime? _runtime;

    public JsonObject Handle(JsonObject request) =>
        RequiredString(request, "cmd") switch
        {
            "hello" => Hello(request),
            "local_set" => LocalSet(request),
            "deliver" => Deliver(request),
            "snapshot" => Snapshot(),
            "bye" => Ok(),
            "link_open" or "link_send" or "link_recv" or "link_close" or "link_stats" =>
                new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = "unsupported channel",
                    ["unsupported"] = true,
                },
            _ => new JsonObject
            {
                ["ok"] = false,
                ["error"] = "unknown command",
            },
        };

    public static void SelfCheck()
    {
        var peer = new InteropPeer();
        var hello = peer.Handle(
            new JsonObject
            {
                ["cmd"] = "hello",
                ["peer"] = 1,
                ["protocol_version"] = ProtocolVersion,
            });
        Require(hello["ok"]?.GetValue<bool>() == true, "hello self-check failed");

        var local = peer.Handle(
            new JsonObject
            {
                ["cmd"] = "local_set",
                ["node"] = 7,
                ["key"] = null,
                ["state"] = new JsonObject
                {
                    ["Inline"] = new JsonArray(65),
                },
                ["at"] = 10,
            });
        var frame = local["frame"]?.DeepClone()
            ?? throw new InvalidOperationException("local_set self-check returned no frame");
        var duplicate = peer.Handle(
            new JsonObject
            {
                ["cmd"] = "deliver",
                ["frame"] = frame,
                ["at"] = 11,
            });
        Require(
            duplicate["applied"]?.GetValue<int>() == 0,
            "duplicate delivery self-check failed");

        var snapshot = peer.Handle(new JsonObject { ["cmd"] = "snapshot" });
        Require(
            ReadByte(snapshot["cells"]?[0]?["state"]?["Inline"]?[0]) == 65,
            "snapshot self-check failed");
    }

    private JsonObject Hello(JsonObject request)
    {
        var protocolVersion = RequiredUInt64(request, "protocol_version");
        if (protocolVersion != ProtocolVersion)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"unsupported protocol_version {protocolVersion}",
            };
        }

        var peer = RequiredUInt64(request, "peer");
        _runtime = new CrdtPlaneRuntime(peer);
        _cells.Clear();
        return new JsonObject
        {
            ["ok"] = true,
            ["binding"] = "lazily-cs",
            ["version"] = BindingVersion,
            ["protocol_version"] = ProtocolVersion,
            ["features"] = StringArray("distributed_crdt"),
            ["codecs"] = StringArray("json"),
            ["channels"] = new JsonArray(),
            ["channel_variants"] = new JsonObject(),
            ["platform_profile"] = "portable",
            ["carve_outs"] = StringArray("msgpack", "transport_links"),
        };
    }

    private JsonObject LocalSet(JsonObject request)
    {
        var runtime = Ready();
        var node = RequiredUInt64(request, "node");
        var at = RequiredUInt64(request, "at");
        var key = OptionalString(request, "key");
        var state = request["state"]?.AsObject()
            ?? throw new JsonException("local_set requires object state");
        var bytes = ReadInline(state);

        if (!_cells.TryGetValue(node, out var cell))
        {
            cell = new ReplicatedCell<InteropRegister, byte[]?>(
                _context,
                new InteropRegister(null),
                ByteArrayComparer.Instance);
            _cells[node] = cell;
            runtime.Register(node, key, cell, Encode, Decode);
        }

        var operation = runtime.LocalUpdate<InteropRegister, byte[]?>(
            node,
            at,
            (register, _) => register.Set(bytes));
        if (operation is null)
        {
            throw new InvalidOperationException("production runtime rejected fresh local op");
        }

        var frame = new CrdtSyncMessage([operation], runtime.Frontier.ToEntries());
        return new JsonObject
        {
            ["ok"] = true,
            ["frame"] = JsonNode.Parse(IpcWire.Serialize(frame)),
        };
    }

    private JsonObject Deliver(JsonObject request)
    {
        var runtime = Ready();
        var frameNode = request["frame"]
            ?? throw new JsonException("deliver requires frame");
        var frame = IpcWire.Deserialize(frameNode.ToJsonString()) as CrdtSyncMessage
            ?? throw new JsonException("deliver requires CrdtSync");
        var applied = runtime.Ingest(frame, RequiredUInt64(request, "at"));
        return new JsonObject
        {
            ["ok"] = true,
            ["applied"] = applied,
        };
    }

    private JsonObject Snapshot()
    {
        var cells = new JsonArray();
        foreach (var entry in Ready().Converged())
        {
            cells.Add(
                new JsonObject
                {
                    ["node"] = entry.Node,
                    ["key"] = entry.Key,
                    ["state"] = SerializeState(entry.State),
                });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["cells"] = cells,
        };
    }

    private CrdtPlaneRuntime Ready() =>
        _runtime ?? throw new InvalidOperationException("hello must run first");

    private static byte[] ReadInline(JsonObject state)
    {
        if (state.Count != 1 || state["Inline"] is not JsonArray inline)
        {
            throw new JsonException("local_set supports only Inline state");
        }

        return inline.Select(ReadByte).ToArray();
    }

    private static JsonNode SerializeState(IpcValue state)
    {
        var envelope = new CrdtSyncMessage(
            [new CrdtOp(0, null, new WireStamp(0, 0, 0), state)]);
        return JsonNode.Parse(IpcWire.Serialize(envelope))?["CrdtSync"]?["ops"]?[0]?["state"]
                ?.DeepClone()
            ?? throw new JsonException("production IPC codec returned no state");
    }

    private static byte[] Encode(InteropRegister register) => [.. register.Value ?? []];

    private static InteropRegister Decode(ReadOnlyMemory<byte> bytes) =>
        new(bytes.ToArray());

    private static JsonObject Ok() => new() { ["ok"] = true };

    private static JsonArray StringArray(params string[] values) =>
        new(values.Select(value => JsonValue.Create(value)).ToArray());

    private static string RequiredString(JsonObject request, string name) =>
        request[name]?.GetValue<string>()
        ?? throw new JsonException($"{name} must be a string");

    private static string? OptionalString(JsonObject request, string name) =>
        request[name] is null ? null : request[name]!.GetValue<string>();

    private static ulong RequiredUInt64(JsonObject request, string name)
    {
        if (request[name] is JsonValue value)
        {
            if (value.TryGetValue<ulong>(out var unsigned)) return unsigned;
            if (value.TryGetValue<long>(out var signed) && signed >= 0) return (ulong)signed;
            if (value.TryGetValue<int>(out var integer) && integer >= 0) return (ulong)integer;
        }

        throw new JsonException($"{name} must be an unsigned integer");
    }

    private static byte ReadByte(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<byte>(out var item)) return item;
            if (value.TryGetValue<int>(out var integer) && integer is >= 0 and <= byte.MaxValue)
            {
                return (byte)integer;
            }
        }

        throw new JsonException("Inline bytes must be unsigned integers");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class InteropRegister(byte[]? value)
    : ICellCrdt<InteropRegister, byte[]?>
{
    public byte[]? Value { get; private set; } = value is null ? null : [.. value];

    public bool Set(byte[] value)
    {
        if (Value is not null && Value.AsSpan().SequenceEqual(value)) return false;
        Value = [.. value];
        return true;
    }

    public bool MergeFrom(InteropRegister other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Value is null) return false;
        return Set(other.Value);
    }
}

internal sealed class ByteArrayComparer : IEqualityComparer<byte[]?>
{
    public static ByteArrayComparer Instance { get; } = new();

    public bool Equals(byte[]? left, byte[]? right) =>
        ReferenceEquals(left, right)
        || (left is not null && right is not null && left.AsSpan().SequenceEqual(right));

    public int GetHashCode(byte[]? value)
    {
        if (value is null) return 0;
        var hash = new HashCode();
        foreach (var item in value) hash.Add(item);
        return hash.ToHashCode();
    }
}
