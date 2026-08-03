using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class SignalingConformanceTests
{
    [Fact]
    public void Every_canonical_signaling_frame_round_trips_and_validates()
    {
        using var document = SpecCorpus.Load("signaling", "frames.json");
        var frames = document.RootElement.GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(17, frames.Length);
        var schema = LoadSchema();
        var asserted = 0;

        foreach (var frame in frames)
        {
            var direction = frame.GetProperty("direction").GetString();
            var wire = frame.GetProperty("wire");
            // Fail closed (#lzscenariobodyskip): the ternary's false arm ASSUMED the server
            // direction, so an unrecognised spelling round-tripped a client frame through the
            // server codec — the fixture named one direction and the runner proved the other.
            var actual = direction switch
            {
                "client" => SignalingWire.Serialize(
                    SignalingWire.DeserializeClient(wire.GetRawText())),
                "server" => SignalingWire.Serialize(
                    SignalingWire.DeserializeServer(wire.GetRawText())),
                _ => throw new InvalidOperationException(
                    $"unknown signaling direction in fixture: {direction}"),
            };

            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(wire.GetRawText()), JsonNode.Parse(actual)),
                frame.GetProperty("label").GetString());
            using var actualDocument = JsonDocument.Parse(actual);
            var result = schema.Evaluate(actualDocument.RootElement);
            Assert.True(result.IsValid, result.ToString());

            // Each frame's own `assertions` block (#lznullformblind). Round-tripping the
            // wire proves the codec is self-consistent and says nothing about what the
            // fixture CLAIMS: all seventeen of these blocks were carried by a fixture this
            // runner opens and bound by nothing, so every key in them reported exactly
            // nothing. Not unread — unreachable, because no tracker ever saw them.
            var label = frame.GetProperty("label").GetString()!;
            var assertions = FixtureAssertions.Of(frame, "assertions", $"signaling/frames.json {label}");
            if (direction == "client")
            {
                AssertClientFrame(assertions, SignalingWire.DeserializeClient(wire.GetRawText()));
            }
            else
            {
                AssertServerFrame(assertions, SignalingWire.DeserializeServer(wire.GetRawText()));
            }

            assertions.Verify();
            asserted += 1;
        }

        // Anti-vacuity: a dispatch that stopped matching would leave every block bound and
        // nothing compared, which the two verdicts above cannot distinguish from a corpus
        // that asserts nothing.
        Assert.Equal(17, asserted);
    }

    private static void AssertClientFrame(FixtureAssertions assertions, ClientSignalingFrame frame)
    {
        switch (frame)
        {
            case ClientSignalingFrame.Join join:
                assertions.AssertKey("peer", join.Peer);
                assertions.AssertKey("has_capabilities", join.Capabilities is not null);
                if (assertions.TryGetProperty("capabilities", out _))
                {
                    assertions.AssertKey("capabilities", join.Capabilities ?? []);
                }

                break;
            case ClientSignalingFrame.Offer offer:
                assertions.AssertKey("to", offer.To);
                break;
            case ClientSignalingFrame.Answer answer:
                assertions.AssertKey("to", answer.To);
                break;
            case ClientSignalingFrame.Ice ice:
                assertions.AssertKey("to", ice.To);
                break;
            case ClientSignalingFrame.Relay relay:
                assertions.AssertKey("to", relay.To);
                break;
            case ClientSignalingFrame.Leave:
                // The corpus carries an empty block here: `leave` addresses nobody and
                // names nobody. Nothing to assert and nothing to excuse.
                break;
            default:
                throw new InvalidOperationException($"unhandled client frame {frame.GetType().Name}");
        }
    }

    private static void AssertServerFrame(FixtureAssertions assertions, ServerSignalingFrame frame)
    {
        switch (frame)
        {
            case ServerSignalingFrame.Welcome welcome:
                assertions.AssertKey("peer", welcome.Peer);
                assertions.AssertKey("peers", welcome.Peers.Select(id => (long)id));
                // Derived from the decoded frame rather than asserted as a literal: the
                // claim is that the roster OMITS the addressee, so it has to be computed
                // from the same two fields the frame carries.
                assertions.AssertKey("roster_excludes_self", !welcome.Peers.Contains(welcome.Peer));
                break;
            case ServerSignalingFrame.PeerJoined joined:
                assertions.AssertKey("peer", joined.Peer);
                break;
            case ServerSignalingFrame.PeerLeft left:
                assertions.AssertKey("peer", left.Peer);
                break;
            case ServerSignalingFrame.Offer offer:
                AssertForwarded(assertions, offer.From);
                break;
            case ServerSignalingFrame.Answer answer:
                AssertForwarded(assertions, answer.From);
                break;
            case ServerSignalingFrame.Ice ice:
                AssertForwarded(assertions, ice.From);
                break;
            case ServerSignalingFrame.Relay relay:
                AssertForwarded(assertions, relay.From);
                break;
            case ServerSignalingFrame.Error error:
                assertions.AssertKey("code", error.Code);
                break;
            default:
                throw new InvalidOperationException($"unhandled server frame {frame.GetType().Name}");
        }
    }

    private static void AssertForwarded(FixtureAssertions assertions, ulong from)
    {
        assertions.AssertKey("from", from);
        // `server_stamped_from` is a claim about PROVENANCE, which one frame cannot show.
        // What it pins here is the forwarded SHAPE — the frame carries `from` and no `to`,
        // which is what makes a client-addressed field impossible to spoof through it. The
        // provenance itself is asserted against a live room by the anti_spoof_session
        // replay below.
        assertions.AssertKeyWith(
            "server_stamped_from",
            want => Assert.True(want.GetBoolean() && from != 0));
    }

    [Fact]
    public void Canonical_invalid_frames_fail_closed()
    {
        using var document = SpecCorpus.Load("signaling", "frames.json");
        var rejects = document.RootElement.GetProperty("rejects").EnumerateArray().ToArray();
        Assert.Equal(3, rejects.Length);

        foreach (var reject in rejects)
        {
            var wire = reject.GetProperty("wire").GetRawText();
            // Fail closed (#lzscenariobodyskip): the `else` ASSUMED the server direction, so a
            // reject the fixture aimed at the client decoder was silently proven against the
            // server decoder instead.
            var direction = reject.GetProperty("direction").GetString();
            if (direction == "client")
            {
                Assert.ThrowsAny<JsonException>(() => SignalingWire.DeserializeClient(wire));
            }
            else if (direction == "server")
            {
                Assert.ThrowsAny<JsonException>(() => SignalingWire.DeserializeServer(wire));
            }
            else
            {
                throw new InvalidOperationException(
                    $"unknown signaling direction in fixture: {direction}");
            }
        }
    }

    [Fact]
    public void Canonical_room_transcript_stamps_sender_and_sorts_rosters()
    {
        using var document = SpecCorpus.Load("signaling", "anti_spoof_session.json");
        var root = document.RootElement;
        var room = new SignalingRoom();

        // The three fixture-level assertions used to be `Assert.True(fixture_value)` — a
        // comparison of the fixture against itself, true by construction and blind to the
        // room. They are now observations accumulated off the transcript the room actually
        // emitted, and compared against the fixture's own booleans below.
        var registeredPeer = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var welcomes = 0;
        var forwards = 0;
        var rosterExcludesSelf = true;
        var rosterSortedAscending = true;
        var forwardedFromIsServerRegistered = true;

        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var input = step.GetProperty("input");
            var connection = input.GetProperty("conn").GetString()!;
            var client = SignalingWire.DeserializeClient(input.GetProperty("recv").GetRawText());
            var actual = room.Handle(connection, client);
            var expected = step.GetProperty("expect").EnumerateArray().ToArray();
            Assert.Equal(expected.Length, actual.Count);

            for (var index = 0; index < expected.Length; index++)
            {
                // The per-frame entries are an assertion block too, so `to` and `frame` are
                // compared THROUGH the tracker rather than marked consumed beside it.
                var delivery = actual[index];
                var entry = FixtureAssertions.Wrap(
                    expected[index],
                    "signaling/anti_spoof_session.json step expect entry");
                entry.AssertKey("to", delivery.To);
                entry.AssertKeyDeep(
                    "frame",
                    JsonNode.Parse(SignalingWire.Serialize(delivery.Frame)));
                entry.Verify();
            }

            foreach (var delivery in actual)
            {
                if (delivery.Frame is ServerSignalingFrame.Welcome welcome
                    && string.Equals(delivery.To, connection, StringComparison.Ordinal))
                {
                    registeredPeer[connection] = welcome.Peer;
                    welcomes++;
                    if (welcome.Peers.Contains(welcome.Peer)) rosterExcludesSelf = false;
                    if (!welcome.Peers.SequenceEqual(welcome.Peers.OrderBy(peer => peer)))
                        rosterSortedAscending = false;
                }

                var forwardedFrom = delivery.Frame switch
                {
                    ServerSignalingFrame.Offer offer => offer.From,
                    ServerSignalingFrame.Answer answer => answer.From,
                    ServerSignalingFrame.Ice ice => ice.From,
                    ServerSignalingFrame.Relay relay => relay.From,
                    _ => (ulong?)null,
                };
                if (forwardedFrom is not { } from) continue;
                forwards++;
                if (!registeredPeer.TryGetValue(connection, out var sender) || sender != from)
                    forwardedFromIsServerRegistered = false;
            }
        }

        // A vacuous observation is worse than a wrong one: with no welcome and no forward,
        // all three flags stay true and the fixture agrees for the wrong reason.
        Assert.True(welcomes > 0, "the transcript emitted no welcome — roster claims are vacuous");
        Assert.True(forwards > 0, "the transcript forwarded nothing — the sender claim is vacuous");

        // The canonical transcript never produces a roster with TWO entries, so no ordering
        // can be distinguished from any other on it: reversing the room's sort leaves every
        // frame in the transcript byte-identical. That is a property of the corpus, not
        // something to fix by editing it, so the ordering claim is folded together with an
        // observation from a room this runner drives itself — three peers joining in
        // descending id order, whose roster a room that did not sort would hand back
        // descending.
        var ordered = new SignalingRoom();
        IReadOnlyList<ulong> widestRoster = [];
        foreach (var peer in (ulong[])[30, 10, 20])
        {
            foreach (var delivery in ordered.Handle($"c{peer}", new ClientSignalingFrame.Join(peer)))
            {
                if (delivery.Frame is not ServerSignalingFrame.Welcome welcome) continue;
                if (welcome.Peers.Count <= widestRoster.Count) continue;
                widestRoster = welcome.Peers;
                if (welcome.Peers.Contains(welcome.Peer)) rosterExcludesSelf = false;
                if (!welcome.Peers.SequenceEqual(welcome.Peers.OrderBy(id => id)))
                    rosterSortedAscending = false;
            }
        }

        Assert.True(
            widestRoster.Count >= 2,
            "the ordering claim needs a roster of at least two peers to discriminate");

        var assertions = FixtureAssertions.Of(
            root,
            "assertions",
            "signaling/anti_spoof_session.json");
        assertions.AssertKey("roster_excludes_self", rosterExcludesSelf);
        assertions.AssertKey(
            "forwarded_from_is_server_registered",
            forwardedFromIsServerRegistered);
        assertions.AssertKey("roster_sorted_ascending", rosterSortedAscending);
        assertions.Verify();
    }

    [Fact]
    public void Canonical_spoofed_sender_is_rejected_before_room_routing()
    {
        using var document = SpecCorpus.Load("signaling", "anti_spoof_session.json");
        var reject = Assert.Single(
            document.RootElement.GetProperty("rejects").EnumerateArray().ToArray());
        var wire = reject.GetProperty("input").GetProperty("recv").GetRawText();

        Assert.ThrowsAny<JsonException>(() => SignalingWire.DeserializeClient(wire));
    }

    private static JsonSchema LoadSchema()
    {
        ArgumentNullException.ThrowIfNull(SpecCorpus.Root);
        var path = Path.GetFullPath(
            Path.Combine(SpecCorpus.Root, "..", "schemas", "signaling.json"));
        return JsonSchema.FromText(File.ReadAllText(path));
    }
}
