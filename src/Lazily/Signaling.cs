using System.Text;
using System.Text.Json;

namespace Lazily;

/// <summary>A signaling frame sent from a peer to the session room.</summary>
public abstract record ClientSignalingFrame
{
    /// <summary>Joins a room and optionally advertises capabilities.</summary>
    public sealed record Join(ulong Peer, IReadOnlyList<string>? Capabilities = null)
        : ClientSignalingFrame;

    /// <summary>Sends a WebRTC offer to a peer.</summary>
    public sealed record Offer(ulong To, string Sdp) : ClientSignalingFrame;

    /// <summary>Sends a WebRTC answer to a peer.</summary>
    public sealed record Answer(ulong To, string Sdp) : ClientSignalingFrame;

    /// <summary>Sends an ICE candidate to a peer.</summary>
    public sealed record Ice(ulong To, string Candidate) : ClientSignalingFrame;

    /// <summary>Relays an opaque signaling payload to a peer.</summary>
    public sealed record Relay(ulong To, JsonElement Payload) : ClientSignalingFrame;

    /// <summary>Leaves the current signaling room.</summary>
    public sealed record Leave : ClientSignalingFrame;
}

/// <summary>A signaling frame emitted by the authoritative session room.</summary>
public abstract record ServerSignalingFrame
{
    /// <summary>Confirms a join with a sorted roster that excludes the joining peer.</summary>
    public sealed record Welcome(ulong Peer, IReadOnlyList<ulong> Peers) : ServerSignalingFrame;

    /// <summary>Announces a newly joined peer.</summary>
    public sealed record PeerJoined(ulong Peer) : ServerSignalingFrame;

    /// <summary>Announces a departed peer.</summary>
    public sealed record PeerLeft(ulong Peer) : ServerSignalingFrame;

    /// <summary>Forwards an offer stamped with the registered sender id.</summary>
    public sealed record Offer(ulong From, string Sdp) : ServerSignalingFrame;

    /// <summary>Forwards an answer stamped with the registered sender id.</summary>
    public sealed record Answer(ulong From, string Sdp) : ServerSignalingFrame;

    /// <summary>Forwards an ICE candidate stamped with the registered sender id.</summary>
    public sealed record Ice(ulong From, string Candidate) : ServerSignalingFrame;

    /// <summary>Forwards an opaque payload stamped with the registered sender id.</summary>
    public sealed record Relay(ulong From, JsonElement Payload) : ServerSignalingFrame;

    /// <summary>Reports a signaling protocol or routing error.</summary>
    public sealed record Error(string Code, string Message) : ServerSignalingFrame;
}

/// <summary>One server frame routed to a connection.</summary>
public sealed record SignalingDelivery(string To, ServerSignalingFrame Frame);

/// <summary>
/// Authoritative open-room signaling state. Directed frames never accept a client-supplied
/// sender; forwarded <c>from</c> ids come only from the registered connection.
/// </summary>
public sealed class SignalingRoom
{
    private readonly Dictionary<string, ulong> _peerByConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, string> _connectionByPeer = [];

    /// <summary>Applies one typed client frame and returns its ordered server deliveries.</summary>
    public IReadOnlyList<SignalingDelivery> Handle(
        string connection,
        ClientSignalingFrame frame)
    {
        Guard.NotNullOrWhiteSpace(connection, nameof(connection));
        Guard.NotNull(frame, nameof(frame));

        return frame switch
        {
            ClientSignalingFrame.Join join => Join(connection, join),
            ClientSignalingFrame.Offer offer => Forward(
                connection,
                offer.To,
                from => new ServerSignalingFrame.Offer(from, offer.Sdp)),
            ClientSignalingFrame.Answer answer => Forward(
                connection,
                answer.To,
                from => new ServerSignalingFrame.Answer(from, answer.Sdp)),
            ClientSignalingFrame.Ice ice => Forward(
                connection,
                ice.To,
                from => new ServerSignalingFrame.Ice(from, ice.Candidate)),
            ClientSignalingFrame.Relay relay => Forward(
                connection,
                relay.To,
                from => new ServerSignalingFrame.Relay(from, relay.Payload.Clone())),
            ClientSignalingFrame.Leave => Leave(connection),
            _ => throw new ArgumentOutOfRangeException(nameof(frame)),
        };
    }

    private IReadOnlyList<SignalingDelivery> Join(
        string connection,
        ClientSignalingFrame.Join join)
    {
        if (_connectionByPeer.ContainsKey(join.Peer)
            || _peerByConnection.ContainsKey(connection))
        {
            return
            [
                new(
                    connection,
                    new ServerSignalingFrame.Error(
                        "peer_already_joined",
                        $"peer {join.Peer} or connection is already in this session")),
            ];
        }

        var roster = _connectionByPeer.Keys.OrderBy(peer => peer).ToArray();
        _connectionByPeer.Add(join.Peer, connection);
        _peerByConnection.Add(connection, join.Peer);

        var deliveries = new List<SignalingDelivery>
        {
            new(connection, new ServerSignalingFrame.Welcome(join.Peer, roster)),
        };
        deliveries.AddRange(
            roster.Select(
                peer => new SignalingDelivery(
                    _connectionByPeer[peer],
                    new ServerSignalingFrame.PeerJoined(join.Peer))));
        return deliveries;
    }

    private IReadOnlyList<SignalingDelivery> Forward(
        string connection,
        ulong target,
        Func<ulong, ServerSignalingFrame> create)
    {
        if (!_peerByConnection.TryGetValue(connection, out var sender))
        {
            return
            [
                new(
                    connection,
                    new ServerSignalingFrame.Error(
                        "not_joined",
                        "connection is not in this session")),
            ];
        }

        if (!_connectionByPeer.TryGetValue(target, out var targetConnection))
        {
            return
            [
                new(
                    connection,
                    new ServerSignalingFrame.Error(
                        "unknown_target",
                        $"peer {target} is not in this session")),
            ];
        }

        return [new SignalingDelivery(targetConnection, create(sender))];
    }

    private IReadOnlyList<SignalingDelivery> Leave(string connection)
    {
        if (!_peerByConnection.Remove(connection, out var peer))
        {
            return [];
        }

        _connectionByPeer.Remove(peer);
        return
        [
            .. _connectionByPeer.OrderBy(entry => entry.Key).Select(
                entry => new SignalingDelivery(
                    entry.Value,
                    new ServerSignalingFrame.PeerLeft(peer))),
        ];
    }
}

/// <summary>Strict codec for every client and server signaling frame variant.</summary>
public static class SignalingWire
{
    private const ulong MaximumJavascriptPeer = 9_007_199_254_740_991;

    /// <summary>Decodes one client frame and rejects server-owned or extra fields.</summary>
    public static ClientSignalingFrame DeserializeClient(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = String(root, "type");
        return type switch
        {
            "join" => DecodeJoin(root),
            "offer" => DecodeClientOffer(root),
            "answer" => DecodeClientAnswer(root),
            "ice" => DecodeClientIce(root),
            "relay" => DecodeClientRelay(root),
            "leave" => DecodeLeave(root),
            _ => throw new JsonException($"Unknown client signaling type: {type}."),
        };
    }

    /// <summary>Decodes one server frame and enforces welcome-roster self exclusion.</summary>
    public static ServerSignalingFrame DeserializeServer(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = String(root, "type");
        return type switch
        {
            "welcome" => DecodeWelcome(root),
            "peer-joined" => DecodePeerJoined(root),
            "peer-left" => DecodePeerLeft(root),
            "offer" => DecodeServerOffer(root),
            "answer" => DecodeServerAnswer(root),
            "ice" => DecodeServerIce(root),
            "relay" => DecodeServerRelay(root),
            "error" => DecodeError(root),
            _ => throw new JsonException($"Unknown server signaling type: {type}."),
        };
    }

    /// <summary>Encodes one client frame in its canonical direction-specific shape.</summary>
    public static string Serialize(ClientSignalingFrame frame)
    {
        Guard.NotNull(frame, nameof(frame));
        return Write(
            writer =>
            {
                switch (frame)
                {
                    case ClientSignalingFrame.Join join:
                        writer.WriteString("type", "join");
                        writer.WriteNumber("peer", join.Peer);
                        if (join.Capabilities is not null)
                        {
                            writer.WritePropertyName("capabilities");
                            writer.WriteStartArray();
                            foreach (var capability in join.Capabilities)
                            {
                                writer.WriteStringValue(capability);
                            }

                            writer.WriteEndArray();
                        }

                        break;
                    case ClientSignalingFrame.Offer offer:
                        WriteDirected(writer, "offer", offer.To, "sdp", offer.Sdp);
                        break;
                    case ClientSignalingFrame.Answer answer:
                        WriteDirected(writer, "answer", answer.To, "sdp", answer.Sdp);
                        break;
                    case ClientSignalingFrame.Ice ice:
                        WriteDirected(writer, "ice", ice.To, "candidate", ice.Candidate);
                        break;
                    case ClientSignalingFrame.Relay relay:
                        writer.WriteString("type", "relay");
                        writer.WriteNumber("to", relay.To);
                        writer.WritePropertyName("payload");
                        relay.Payload.WriteTo(writer);
                        break;
                    case ClientSignalingFrame.Leave:
                        writer.WriteString("type", "leave");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(frame));
                }
            });
    }

    /// <summary>Encodes one authoritative server frame in its canonical shape.</summary>
    public static string Serialize(ServerSignalingFrame frame)
    {
        Guard.NotNull(frame, nameof(frame));
        return Write(
            writer =>
            {
                switch (frame)
                {
                    case ServerSignalingFrame.Welcome welcome:
                        if (welcome.Peers.Contains(welcome.Peer))
                        {
                            throw new ArgumentException(
                                "A welcome roster must exclude the joining peer.",
                                nameof(frame));
                        }

                        writer.WriteString("type", "welcome");
                        writer.WriteNumber("peer", welcome.Peer);
                        writer.WritePropertyName("peers");
                        writer.WriteStartArray();
                        foreach (var peer in welcome.Peers)
                        {
                            writer.WriteNumberValue(peer);
                        }

                        writer.WriteEndArray();
                        break;
                    case ServerSignalingFrame.PeerJoined joined:
                        WritePeerEvent(writer, "peer-joined", joined.Peer);
                        break;
                    case ServerSignalingFrame.PeerLeft left:
                        WritePeerEvent(writer, "peer-left", left.Peer);
                        break;
                    case ServerSignalingFrame.Offer offer:
                        WriteForwarded(writer, "offer", offer.From, "sdp", offer.Sdp);
                        break;
                    case ServerSignalingFrame.Answer answer:
                        WriteForwarded(writer, "answer", answer.From, "sdp", answer.Sdp);
                        break;
                    case ServerSignalingFrame.Ice ice:
                        WriteForwarded(writer, "ice", ice.From, "candidate", ice.Candidate);
                        break;
                    case ServerSignalingFrame.Relay relay:
                        writer.WriteString("type", "relay");
                        writer.WriteNumber("from", relay.From);
                        writer.WritePropertyName("payload");
                        relay.Payload.WriteTo(writer);
                        break;
                    case ServerSignalingFrame.Error error:
                        writer.WriteString("type", "error");
                        writer.WriteString("code", error.Code);
                        writer.WriteString("message", error.Message);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(frame));
                }
            });
    }

    private static ClientSignalingFrame DecodeJoin(JsonElement root)
    {
        var hasCapabilities = root.TryGetProperty("capabilities", out var capabilities);
        Exact(root, hasCapabilities ? ["type", "peer", "capabilities"] : ["type", "peer"]);
        var peer = UInt64(root, "peer");
        if (peer > MaximumJavascriptPeer)
        {
            throw new JsonException("join.peer exceeds the interoperable integer range.");
        }

        return new ClientSignalingFrame.Join(
            peer,
            hasCapabilities
                ? capabilities.EnumerateArray().Select(StringValue).ToArray()
                : null);
    }

    private static ClientSignalingFrame DecodeClientOffer(JsonElement root)
    {
        Exact(root, "type", "to", "sdp");
        return new ClientSignalingFrame.Offer(UInt64(root, "to"), String(root, "sdp"));
    }

    private static ClientSignalingFrame DecodeClientAnswer(JsonElement root)
    {
        Exact(root, "type", "to", "sdp");
        return new ClientSignalingFrame.Answer(UInt64(root, "to"), String(root, "sdp"));
    }

    private static ClientSignalingFrame DecodeClientIce(JsonElement root)
    {
        Exact(root, "type", "to", "candidate");
        return new ClientSignalingFrame.Ice(UInt64(root, "to"), String(root, "candidate"));
    }

    private static ClientSignalingFrame DecodeClientRelay(JsonElement root)
    {
        Exact(root, "type", "to", "payload");
        return new ClientSignalingFrame.Relay(
            UInt64(root, "to"),
            root.GetProperty("payload").Clone());
    }

    private static ClientSignalingFrame DecodeLeave(JsonElement root)
    {
        Exact(root, "type");
        return new ClientSignalingFrame.Leave();
    }

    private static ServerSignalingFrame DecodeWelcome(JsonElement root)
    {
        Exact(root, "type", "peer", "peers");
        var peer = UInt64(root, "peer");
        var peers = root.GetProperty("peers").EnumerateArray().Select(UInt64Value).ToArray();
        if (peers.Contains(peer))
        {
            throw new JsonException("A welcome roster must exclude the joining peer.");
        }

        return new ServerSignalingFrame.Welcome(peer, peers);
    }

    private static ServerSignalingFrame DecodePeerJoined(JsonElement root)
    {
        Exact(root, "type", "peer");
        return new ServerSignalingFrame.PeerJoined(UInt64(root, "peer"));
    }

    private static ServerSignalingFrame DecodePeerLeft(JsonElement root)
    {
        Exact(root, "type", "peer");
        return new ServerSignalingFrame.PeerLeft(UInt64(root, "peer"));
    }

    private static ServerSignalingFrame DecodeServerOffer(JsonElement root)
    {
        Exact(root, "type", "from", "sdp");
        return new ServerSignalingFrame.Offer(UInt64(root, "from"), String(root, "sdp"));
    }

    private static ServerSignalingFrame DecodeServerAnswer(JsonElement root)
    {
        Exact(root, "type", "from", "sdp");
        return new ServerSignalingFrame.Answer(UInt64(root, "from"), String(root, "sdp"));
    }

    private static ServerSignalingFrame DecodeServerIce(JsonElement root)
    {
        Exact(root, "type", "from", "candidate");
        return new ServerSignalingFrame.Ice(
            UInt64(root, "from"),
            String(root, "candidate"));
    }

    private static ServerSignalingFrame DecodeServerRelay(JsonElement root)
    {
        Exact(root, "type", "from", "payload");
        return new ServerSignalingFrame.Relay(
            UInt64(root, "from"),
            root.GetProperty("payload").Clone());
    }

    private static ServerSignalingFrame DecodeError(JsonElement root)
    {
        Exact(root, "type", "code", "message");
        return new ServerSignalingFrame.Error(String(root, "code"), String(root, "message"));
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDirected(
        Utf8JsonWriter writer,
        string type,
        ulong to,
        string payloadName,
        string payload)
    {
        writer.WriteString("type", type);
        writer.WriteNumber("to", to);
        writer.WriteString(payloadName, payload);
    }

    private static void WriteForwarded(
        Utf8JsonWriter writer,
        string type,
        ulong from,
        string payloadName,
        string payload)
    {
        writer.WriteString("type", type);
        writer.WriteNumber("from", from);
        writer.WriteString(payloadName, payload);
    }

    private static void WritePeerEvent(Utf8JsonWriter writer, string type, ulong peer)
    {
        writer.WriteString("type", type);
        writer.WriteNumber("peer", peer);
    }

    private static void Exact(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Signaling frame must be an object.");
        }

        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length
            || names.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
        {
            throw new JsonException($"Expected properties: {string.Join(", ", names)}.");
        }
    }

    private static string String(JsonElement element, string name) =>
        StringValue(element.GetProperty(name));

    private static string StringValue(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new JsonException("Expected a string.");

    private static ulong UInt64(JsonElement element, string name) =>
        UInt64Value(element.GetProperty(name));

    private static ulong UInt64Value(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var value)
            ? value
            : throw new JsonException("Expected a non-negative integer.");
}
