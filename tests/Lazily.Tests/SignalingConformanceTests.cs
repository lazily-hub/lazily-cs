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
            var actual = direction == "client"
                ? SignalingWire.Serialize(SignalingWire.DeserializeClient(wire.GetRawText()))
                : SignalingWire.Serialize(SignalingWire.DeserializeServer(wire.GetRawText()));

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
            if (reject.GetProperty("direction").GetString() == "client")
            {
                Assert.ThrowsAny<JsonException>(() => SignalingWire.DeserializeClient(wire));
            }
            else
            {
                Assert.ThrowsAny<JsonException>(() => SignalingWire.DeserializeServer(wire));
            }
        }
    }

    [Fact]
    public void Canonical_room_transcript_stamps_sender_and_sorts_rosters()
    {
        using var document = SpecCorpus.Load("signaling", "anti_spoof_session.json");
        var root = document.RootElement;
        var room = new SignalingRoom();

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
                Assert.Equal(expected[index].GetProperty("to").GetString(), actual[index].To);
                Assert.True(
                    JsonNode.DeepEquals(
                        JsonNode.Parse(expected[index].GetProperty("frame").GetRawText()),
                        JsonNode.Parse(SignalingWire.Serialize(actual[index].Frame))));
            }
        }

        Assert.True(root.GetProperty("assertions").GetProperty("roster_excludes_self").GetBoolean());
        Assert.True(
            root.GetProperty("assertions")
                .GetProperty("forwarded_from_is_server_registered")
                .GetBoolean());
        Assert.True(
            root.GetProperty("assertions").GetProperty("roster_sorted_ascending").GetBoolean());
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
