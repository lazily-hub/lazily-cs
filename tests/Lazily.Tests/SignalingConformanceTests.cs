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
        }
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
                entry.AssertKeyWith(
                    "frame",
                    want => Assert.True(
                        JsonNode.DeepEquals(
                            JsonNode.Parse(want.GetRawText()),
                            JsonNode.Parse(SignalingWire.Serialize(delivery.Frame)))));
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
