using System.Text;
using System.Text.Json;

namespace Lazily;

/// <summary>The structured result of a fail-closed session compatibility check.</summary>
public sealed record CapabilityCheck(bool IsCompatible, string? Field = null, string? Reason = null)
{
    /// <summary>A successful compatibility result.</summary>
    public static CapabilityCheck Compatible { get; } = new(true);

    /// <summary>Constructs an incompatible result for one field.</summary>
    public static CapabilityCheck Fail(string field, string reason) =>
        new(false, field, reason);
}

/// <summary>
/// The standalone compatibility frame exchanged before non-local graph or command traffic.
/// </summary>
public sealed record SessionHandshake(
    string ProtocolId,
    ulong ProtocolMajorVersion,
    string Codec,
    ulong MaxFrameSize,
    bool FragmentationSupported,
    bool OrderedReliable,
    ulong PeerId,
    string SessionId,
    IReadOnlyList<string> Features)
{
    /// <summary>The required protocol identifier.</summary>
    public const string RequiredProtocolId = "lazily-ipc";

    /// <summary>The current breaking-change version.</summary>
    public const ulong RequiredProtocolMajorVersion = 1;

    /// <summary>The command-plane feature token.</summary>
    public const string CommandPlaneV1 = "command-plane-v1";

    /// <summary>Creates a JSON, ordered-reliable handshake with a 1 MiB frame limit.</summary>
    public static SessionHandshake Create(
        ulong peerId,
        string sessionId,
        IEnumerable<string>? features = null) =>
        new(
            RequiredProtocolId,
            RequiredProtocolMajorVersion,
            "json",
            1_048_576,
            FragmentationSupported: false,
            OrderedReliable: true,
            peerId,
            sessionId,
            features?.ToArray() ?? []);

    /// <summary>Returns whether this peer advertises a feature.</summary>
    public bool HasFeature(string feature) =>
        Features.Contains(feature, StringComparer.Ordinal);

    /// <summary>
    /// Checks protocol identity, major version, codec, ordered delivery, and features required of
    /// both peers.
    /// </summary>
    public CapabilityCheck CheckCompatible(
        SessionHandshake other,
        params string[] requiredFeatures)
    {
        Guard.NotNull(other, nameof(other));
        if (!string.Equals(ProtocolId, RequiredProtocolId, StringComparison.Ordinal))
        {
            return Fail("protocol_id", "local protocol_id is not lazily-ipc");
        }

        if (!string.Equals(other.ProtocolId, RequiredProtocolId, StringComparison.Ordinal))
        {
            return Fail("protocol_id", "remote protocol_id is not lazily-ipc");
        }

        if (ProtocolMajorVersion != RequiredProtocolMajorVersion
            || other.ProtocolMajorVersion != RequiredProtocolMajorVersion
            || ProtocolMajorVersion != other.ProtocolMajorVersion)
        {
            return Fail("protocol_major_version", "protocol major versions are incompatible");
        }

        if (!string.Equals(Codec, other.Codec, StringComparison.Ordinal))
        {
            return Fail("codec", $"codec mismatch ({Codec} vs {other.Codec})");
        }

        if (!OrderedReliable || !other.OrderedReliable)
        {
            return Fail(
                "ordered_reliable",
                "both peers must require ordered-reliable delivery");
        }

        if (MaxFrameSize == 0 || other.MaxFrameSize == 0)
        {
            return Fail(
                "max_frame_size",
                "both peers must advertise a positive receive ceiling");
        }

        if (string.IsNullOrEmpty(SessionId)
            || string.IsNullOrEmpty(other.SessionId)
            || !string.Equals(SessionId, other.SessionId, StringComparison.Ordinal))
        {
            return Fail(
                "session_id",
                "both peers must name the same non-empty session");
        }

        foreach (var feature in requiredFeatures.Distinct(StringComparer.Ordinal))
        {
            if (!HasFeature(feature) || !other.HasFeature(feature))
            {
                return Fail(
                    "features",
                    $"required feature '{feature}' must be advertised by both peers");
            }
        }

        LazilyMetrics.HandshakeAccepted();
        return CapabilityCheck.Compatible;

        CapabilityCheck Fail(string field, string reason)
        {
            LazilyMetrics.HandshakeRejected();
            return CapabilityCheck.Fail(field, reason);
        }
    }

    /// <summary>Serializes the standalone frame in canonical field order.</summary>
    public string Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("protocol_id", ProtocolId);
            writer.WriteNumber("protocol_major_version", ProtocolMajorVersion);
            writer.WriteString("codec", Codec);
            writer.WriteNumber("max_frame_size", MaxFrameSize);
            writer.WriteBoolean("fragmentation_supported", FragmentationSupported);
            writer.WriteBoolean("ordered_reliable", OrderedReliable);
            writer.WriteNumber("peer_id", PeerId);
            writer.WriteString("session_id", SessionId);
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var feature in Features) writer.WriteStringValue(feature);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Decodes and structurally validates the standalone frame.</summary>
    public static SessionHandshake Deserialize(string json)
    {
        Guard.NotNullOrWhiteSpace(json, nameof(json));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireExactProperties(
            root,
            "protocol_id",
            "protocol_major_version",
            "codec",
            "max_frame_size",
            "fragmentation_supported",
            "ordered_reliable",
            "peer_id",
            "session_id",
            "features");

        var features = Required(root, "features");
        if (features.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("features must be an array.");
        }

        return new SessionHandshake(
            RequireString(root, "protocol_id"),
            RequireUInt64(root, "protocol_major_version"),
            RequireString(root, "codec"),
            RequireUInt64(root, "max_frame_size"),
            RequireBoolean(root, "fragmentation_supported"),
            RequireBoolean(root, "ordered_reliable"),
            RequireUInt64(root, "peer_id"),
            RequireString(root, "session_id", allowEmpty: true),
            features.EnumerateArray()
                .Select((feature, index) =>
                    feature.ValueKind == JsonValueKind.String
                        ? feature.GetString()!
                        : throw new JsonException($"features[{index}] must be a string."))
                .ToArray());
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw new JsonException($"Missing required property '{name}'.");

    private static string RequireString(
        JsonElement element,
        string name,
        bool allowEmpty = false)
    {
        var value = Required(element, name);
        if (value.ValueKind != JsonValueKind.String
            || (!allowEmpty && string.IsNullOrEmpty(value.GetString())))
        {
            throw new JsonException(
                allowEmpty
                    ? $"{name} must be a string."
                    : $"{name} must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static ulong RequireUInt64(JsonElement element, string name)
    {
        var value = Required(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var result))
        {
            throw new JsonException($"{name} must be a non-negative integer.");
        }

        return result;
    }

    private static bool RequireBoolean(JsonElement element, string name)
    {
        var value = Required(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException($"{name} must be a boolean."),
        };
    }

    private static void RequireExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("SessionHandshake must be an object.");
        }

        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length
            || names.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
        {
            throw new JsonException($"Expected properties: {string.Join(", ", names)}.");
        }
    }
}

/// <summary>A negotiated gate for state or command traffic.</summary>
public sealed class NegotiatedSession
{
    private readonly HashSet<string> _features;

    /// <summary>Validates the two handshakes and captures their feature intersection.</summary>
    public NegotiatedSession(
        SessionHandshake local,
        SessionHandshake remote,
        params string[] requiredFeatures)
    {
        Local = local ?? throw new ArgumentNullException(nameof(local));
        Remote = remote ?? throw new ArgumentNullException(nameof(remote));
        var check = local.CheckCompatible(remote, requiredFeatures);
        if (!check.IsCompatible)
        {
            throw new InvalidOperationException(
                $"Session negotiation failed at {check.Field}: {check.Reason}");
        }

        _features = local.Features
            .Intersect(remote.Features, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        MaxFrameSize = Math.Min(local.MaxFrameSize, remote.MaxFrameSize);
        FragmentationSupported =
            local.FragmentationSupported && remote.FragmentationSupported;
        SessionId = local.SessionId;
    }

    /// <summary>The local handshake.</summary>
    public SessionHandshake Local { get; }

    /// <summary>The remote handshake.</summary>
    public SessionHandshake Remote { get; }

    /// <summary>The common positive receive ceiling used in both directions.</summary>
    public ulong MaxFrameSize { get; }

    /// <summary>Whether both peers can send and reassemble fragmented frames.</summary>
    public bool FragmentationSupported { get; }

    /// <summary>The shared non-empty graph/session identifier.</summary>
    public string SessionId { get; }

    /// <summary>Returns whether both peers advertised the feature.</summary>
    public bool Supports(string feature) => _features.Contains(feature);

    /// <summary>Fails closed unless both peers advertised command-plane-v1.</summary>
    public void RequireCommandPlane()
    {
        if (!Supports(SessionHandshake.CommandPlaneV1))
        {
            throw new InvalidOperationException(
                "command-plane-v1 was not advertised by both peers.");
        }
    }
}
