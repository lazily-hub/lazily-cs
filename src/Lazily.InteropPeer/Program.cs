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
    private const string BindingVersion = "0.4.0";
    private static readonly HashSet<string> StdlibFeatures =
    [
        "stdlib_timer_v1",
        "stdlib_timeout_v1",
        "stdlib_revision_barrier_v1",
    ];

    private readonly Context _context = new();
    private readonly Dictionary<ulong, ReplicatedCell<InteropRegister, byte[]?>> _cells = [];
    private readonly Dictionary<string, StdlibFeature> _stdlib = [];
    private CrdtPlaneRuntime? _runtime;

    public JsonObject Handle(JsonObject request) =>
        RequiredString(request, "cmd") switch
        {
            "hello" => Hello(request),
            "local_set" => LocalSet(request),
            "deliver" => Deliver(request),
            "snapshot" => Snapshot(),
            "feature_reset" => FeatureReset(request),
            "feature_step" => FeatureStep(request),
            "feature_observe" => FeatureObserve(request),
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
        var advertised = hello["features"]?.AsArray()
            .Select(feature => feature?.GetValue<string>())
            .Where(feature => feature is not null)
            .ToHashSet(StringComparer.Ordinal)
            ?? throw new InvalidOperationException("hello self-check returned no features");
        Require(
            StdlibFeatures.All(advertised.Contains),
            "stdlib feature advertisement self-check failed");

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

        var steps = new Dictionary<string, JsonObject[]>
        {
            ["stdlib_timer_v1"] =
            [
                ParseObject("""{"op":"start","now":0,"duration":1}"""),
                ParseObject("""{"op":"observe","now":1}"""),
            ],
            ["stdlib_timeout_v1"] =
            [
                ParseObject("""{"op":"start","now":0,"duration":1}"""),
                ParseObject(
                    """{"op":"poll","now":1,"operation":"completed","value":"late","cancellation":"cancelled"}"""),
            ],
            ["stdlib_revision_barrier_v1"] =
            [
                ParseObject(
                    """{"op":"start","revision":0,"required_revision":1,"deadline":null}"""),
                ParseObject("""{"op":"advance","revision":1,"predicate":true}"""),
            ],
        };
        foreach (var (feature, featureSteps) in steps)
        {
            Require(
                peer.Handle(
                    new JsonObject
                    {
                        ["cmd"] = "feature_reset",
                        ["feature"] = feature,
                    })["ok"]?.GetValue<bool>() == true,
                $"{feature} reset self-check failed");
            foreach (var step in featureSteps)
            {
                Require(
                    peer.Handle(
                        new JsonObject
                        {
                            ["cmd"] = "feature_step",
                            ["feature"] = feature,
                            ["step"] = step,
                        })["ok"]?.GetValue<bool>() == true,
                    $"{feature} step self-check failed");
            }
            Require(
                peer.Handle(
                    new JsonObject
                    {
                        ["cmd"] = "feature_observe",
                        ["feature"] = feature,
                    })["ok"]?.GetValue<bool>() == true,
                $"{feature} observe self-check failed");
        }
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
        _stdlib.Clear();
        return new JsonObject
        {
            ["ok"] = true,
            ["binding"] = "lazily-cs",
            ["version"] = BindingVersion,
            ["protocol_version"] = ProtocolVersion,
            ["features"] = StringArray(
                "distributed_crdt",
                "stdlib_timer_v1",
                "stdlib_timeout_v1",
                "stdlib_revision_barrier_v1"),
            ["codecs"] = StringArray("json"),
            ["channels"] = new JsonArray(),
            ["channel_variants"] = new JsonObject(),
            ["platform_profile"] = "portable",
            ["carve_outs"] = StringArray("msgpack", "transport_links"),
        };
    }

    private JsonObject FeatureReset(JsonObject request)
    {
        var feature = RequiredString(request, "feature");
        if (!StdlibFeatures.Contains(feature))
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"unsupported feature {feature}",
                ["unsupported"] = true,
            };
        }
        _stdlib[feature] = new StdlibFeature(feature);
        return new JsonObject
        {
            ["ok"] = true,
            ["feature"] = feature,
        };
    }

    private JsonObject FeatureStep(JsonObject request)
    {
        var featureName = RequiredString(request, "feature");
        if (!_stdlib.TryGetValue(featureName, out var feature))
        {
            throw new InvalidOperationException(
                $"feature {featureName} must be reset before stepping");
        }
        var step = request["step"]?.AsObject()
            ?? throw new JsonException("feature step must be an object");
        return FeatureResponse(featureName, feature.Step(step));
    }

    private JsonObject FeatureObserve(JsonObject request)
    {
        var featureName = RequiredString(request, "feature");
        if (!_stdlib.TryGetValue(featureName, out var feature))
        {
            throw new InvalidOperationException(
                $"feature {featureName} must be reset before observation");
        }
        return FeatureResponse(
            featureName,
            feature.Last
            ?? throw new InvalidOperationException($"feature {featureName} has no observation"));
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

    private static JsonObject FeatureResponse(string feature, JsonObject observation) =>
        new()
        {
            ["ok"] = true,
            ["feature"] = feature,
            ["observation"] = observation.DeepClone(),
        };

    private static JsonArray StringArray(params string[] values) =>
        new(values.Select(value => JsonValue.Create(value)).ToArray());

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json)?.AsObject()
        ?? throw new JsonException("self-check JSON must be an object");

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
            if (value.TryGetValue<string>(out var text)
                && ulong.TryParse(
                    text,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }
        }

        throw new JsonException($"{name} must be an unsigned integer");
    }

    private static ulong? OptionalUInt64(JsonObject request, string name) =>
        request[name] is null ? null : RequiredUInt64(request, name);

    private static bool RequiredBoolean(JsonObject request, string name) =>
        request[name]?.GetValue<bool>()
        ?? throw new JsonException($"{name} must be a boolean");

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

internal sealed class StdlibFeature(string name)
{
    private Lazily.Timer? _timer;
    private Timeout<string>? _timeout;
    private RevisionBarrier? _barrier;

    public JsonObject? Last { get; private set; }

    public JsonObject Step(JsonObject step)
    {
        Last = name switch
        {
            "stdlib_timer_v1" => TimerStep(step),
            "stdlib_timeout_v1" => TimeoutStep(step),
            "stdlib_revision_barrier_v1" => BarrierStep(step),
            _ => throw new InvalidOperationException($"unsupported feature {name}"),
        };
        return Last;
    }

    private JsonObject TimerStep(JsonObject step)
    {
        switch (RequiredString(step, "op"))
        {
            case "start":
                try
                {
                    _timer = new Lazily.Timer(
                        RequiredUInt64(step, "now"),
                        RequiredUInt64(step, "duration"));
                    return new JsonObject
                    {
                        ["outcome"] = "pending",
                        ["deadline"] = _timer.Deadline,
                    };
                }
                catch (StdlibUnavailableException failure)
                {
                    _timer = null;
                    return new JsonObject
                    {
                        ["outcome"] = "unavailable",
                        ["reason"] = failure.Reason.Name(),
                    };
                }
            case "observe":
                return TimerJson(
                    (_timer
                    ?? throw new InvalidOperationException("timer feature is not started"))
                    .Observe(RequiredUInt64(step, "now")));
            default:
                throw new InvalidOperationException("unsupported timer feature step");
        }
    }

    private JsonObject TimeoutStep(JsonObject step)
    {
        switch (RequiredString(step, "op"))
        {
            case "start":
                try
                {
                    _timeout = new Timeout<string>(
                        RequiredUInt64(step, "now"),
                        RequiredUInt64(step, "duration"));
                    return new JsonObject
                    {
                        ["outcome"] = "pending",
                        ["deadline"] = _timeout.Deadline,
                    };
                }
                catch (StdlibUnavailableException failure)
                {
                    _timeout = null;
                    return new JsonObject
                    {
                        ["outcome"] = "unavailable",
                        ["reason"] = failure.Reason.Name(),
                    };
                }
            case "poll":
                var operationCalls = 0;
                var cancellationCalls = 0;
                var observation = (_timeout
                    ?? throw new InvalidOperationException("timeout feature is not started")).Poll(
                    RequiredUInt64(step, "now"),
                    () =>
                    {
                        operationCalls++;
                        return RequiredString(step, "operation") switch
                        {
                            "pending" => TimeoutOperation<string>.Pending(),
                            "completed" => TimeoutOperation<string>.Completed(
                                RequiredString(step, "value")),
                            "unavailable" => TimeoutOperation<string>.Unavailable(),
                            _ => throw new JsonException("unsupported timeout operation"),
                        };
                    },
                    () =>
                    {
                        cancellationCalls++;
                        return Cancellation(step);
                    });
                return TimeoutJson(observation, operationCalls, cancellationCalls);
            default:
                throw new InvalidOperationException("unsupported timeout feature step");
        }
    }

    private JsonObject BarrierStep(JsonObject step)
    {
        var cancellationCalls = 0;
        var operation = RequiredString(step, "op");
        var observation = operation switch
        {
            "start" => (_barrier = new RevisionBarrier(
                RequiredUInt64(step, "revision"),
                RequiredUInt64(step, "required_revision"),
                OptionalUInt64(step, "deadline"))).Receipt(string.Empty),
            "observe" => (_barrier
                ?? throw new InvalidOperationException("barrier feature is not started")).Observe(
                RequiredUInt64(step, "now"),
                RequiredBoolean(step, "predicate"),
                () =>
                {
                    cancellationCalls++;
                    return Cancellation(step);
                }),
            "register_recheck" => (_barrier
                ?? throw new InvalidOperationException("barrier feature is not started"))
                .RegisterRecheck(
                    RequiredUInt64(step, "now"),
                    RequiredUInt64(step, "observed_revision"),
                    RequiredBoolean(step, "predicate")),
            "advance" => (_barrier
                ?? throw new InvalidOperationException("barrier feature is not started")).Advance(
                RequiredUInt64(step, "revision"),
                RequiredBoolean(step, "predicate")),
            "dispose" => (_barrier
                ?? throw new InvalidOperationException("barrier feature is not started")).Dispose(),
            "receipt" => (_barrier
                ?? throw new InvalidOperationException("barrier feature is not started")).Receipt(
                RequiredString(step, "key")),
            _ => throw new InvalidOperationException("unsupported revision barrier feature step"),
        };
        return BarrierJson(
            observation,
            operation == "observe" ? cancellationCalls : null);
    }

    private static TimeoutCancellation Cancellation(JsonObject step) =>
        RequiredString(step, "cancellation") switch
        {
            "pending" => TimeoutCancellation.Pending,
            "cancelled" => TimeoutCancellation.Cancelled,
            "unavailable" => TimeoutCancellation.Unavailable,
            _ => throw new JsonException("unsupported cancellation"),
        };

    private static JsonObject TimerJson(TimerObservation observation)
    {
        var result = new JsonObject { ["outcome"] = observation.Outcome.Name() };
        if (observation.Deadline is { } deadline) result["deadline"] = deadline;
        if (observation.FiredAt is { } firedAt) result["fired_at"] = firedAt;
        if (observation.Reason is { } reason) result["reason"] = reason.Name();
        return result;
    }

    private static JsonObject TimeoutJson(
        TimeoutObservation<string> observation,
        int operationCalls,
        int cancellationCalls)
    {
        var result = new JsonObject
        {
            ["outcome"] = observation.Outcome.Name(),
            ["operation_calls"] = operationCalls,
            ["cancellation_calls"] = cancellationCalls,
        };
        if (observation.Deadline is { } deadline) result["deadline"] = deadline;
        if (observation.Outcome == TimeoutOutcome.Completed) result["value"] = observation.Value;
        if (observation.Reason is { } reason) result["reason"] = reason.Name();
        return result;
    }

    private static JsonObject BarrierJson(
        RevisionBarrierObservation observation,
        int? cancellationCalls)
    {
        var result = new JsonObject
        {
            ["outcome"] = observation.Outcome.Name(),
            ["revision"] = observation.Revision,
            ["generation"] = observation.Generation,
        };
        if (observation.Reason is { } reason) result["reason"] = reason.Name();
        if (cancellationCalls is { } calls) result["cancellation_calls"] = calls;
        return result;
    }

    private static string RequiredString(JsonObject request, string name) =>
        request[name]?.GetValue<string>()
        ?? throw new JsonException($"{name} must be a string");

    private static ulong RequiredUInt64(JsonObject request, string name)
    {
        if (request[name] is JsonValue value)
        {
            if (value.TryGetValue<ulong>(out var unsigned)) return unsigned;
            if (value.TryGetValue<long>(out var signed) && signed >= 0) return (ulong)signed;
            if (value.TryGetValue<int>(out var integer) && integer >= 0) return (ulong)integer;
            if (value.TryGetValue<string>(out var text)
                && ulong.TryParse(
                    text,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }
        }
        throw new JsonException($"{name} must be an unsigned integer");
    }

    private static ulong? OptionalUInt64(JsonObject request, string name) =>
        request[name] is null ? null : RequiredUInt64(request, name);

    private static bool RequiredBoolean(JsonObject request, string name) =>
        request[name]?.GetValue<bool>()
        ?? throw new JsonException($"{name} must be a boolean");
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
