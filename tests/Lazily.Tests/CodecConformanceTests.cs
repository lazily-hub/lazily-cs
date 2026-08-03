using System.Text.Json;
using Lazily;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Frame-codec round-trip conformance (<c>#lzmsgpackparity</c>).
/// </summary>
/// <remarks>
/// <para>
/// protocol.md § Frame codecs makes <c>json</c> (the reference codec) and <c>msgpack</c>
/// (the cross-language binary default) MUST-level for every binding, and requires every
/// frame to round-trip through both for all three <c>IpcMessage</c> variants. That
/// requirement lived only in prose. The four conformance rungs — was the fixture OPENED,
/// were its keys CONSUMED, were they ASSERTED, was every SCENARIO replayed — all reason
/// about fixture <em>content</em>, and content replay never exercises a codec, so a binding
/// could carve out a MUST-level codec and stay green on every rung.
/// </para>
/// <para>
/// lazily-cs implements BOTH halves (<c>#lzmsgpackseven</c>). <c>msgpack</c> was an explicit
/// carve-out until <see cref="MsgPackWire"/> landed; the <c>KNOWN_UNCOVERED</c> entry in
/// <c>scripts/check-conformance-coverage.sh</c> is gone with it, because an allowlist that
/// outlives its gap understates coverage silently.
/// </para>
/// <para>
/// The runner decodes <c>wire</c>, RE-ENCODES the decoded message, decodes again, and checks
/// every <c>expect</c> key against that second decode. Asserting against the fixture literal
/// would prove nothing: the literal never passed through an encoder.
/// </para>
/// <para>
/// The msgpack half additionally introspects the ENCODED BYTES schema-lessly through
/// <see cref="MsgPackWire.Inspect"/>. The named-field rule is a property of the ENCODING and
/// is therefore invisible to every assertion over a decoded <c>IpcMessage</c>: a positional
/// encoder round-trips each value correctly and is still speaking a private framing no peer
/// that negotiated <c>msgpack</c> could read.
/// </para>
/// </remarks>
public sealed class CodecConformanceTests
{
    private const string Corpus = "codec";
    private const string JsonFixture = "frame_roundtrip_json.json";
    private const string MsgPackFixture = "frame_roundtrip_msgpack.json";

    private static string VariantOf(IpcMessage message) => message switch
    {
        SnapshotMessage => "Snapshot",
        DeltaMessage => "Delta",
        CrdtSyncMessage => "CrdtSync",
        _ => throw new InvalidOperationException($"codec fixture pins no runner for {message}"),
    };

    private static string OpVariantOf(DeltaOp op) => op switch
    {
        DeltaOp.CellSet => "CellSet",
        DeltaOp.SlotValue => "SlotValue",
        DeltaOp.Invalidate => "Invalidate",
        DeltaOp.NodeAdd => "NodeAdd",
        DeltaOp.NodeRemove => "NodeRemove",
        DeltaOp.EdgeAdd => "EdgeAdd",
        DeltaOp.EdgeRemove => "EdgeRemove",
        _ => throw new InvalidOperationException($"unknown DeltaOp {op}"),
    };

    private static void AssertSnapshot(FixtureAssertions expect, SnapshotMessage snap)
    {
        expect.AssertKey("epoch", snap.Epoch);
        expect.AssertKey("node_count", snap.Nodes.Count);
        expect.AssertKey("edge_count", snap.Edges.Count);
        expect.AssertKey("root_count", snap.Roots.Count);
        expect.AssertKey("first_node_type_tag", snap.Nodes[0].TypeTag);
        var payload = Assert.IsType<NodeState.Payload>(snap.Nodes[0].State);
        expect.AssertKey("first_node_payload", payload.Bytes);

        var opaque = snap.Nodes.First(n => n.State is NodeState.Opaque);
        expect.AssertKey("opaque_node_id", opaque.Node);
        // The externally-tagged UNIT variant is the shape most likely to decay into
        // {"Opaque": null} under a re-encode, so name it rather than infer it.
        expect.AssertKey("opaque_node_state_tag", opaque.State is NodeState.Opaque ? "Opaque" : null);

        expect.AssertKey("first_edge", new[] { snap.Edges[0].Dependent, snap.Edges[0].Dependency }.Select(v => (long)v));
        expect.AssertKey("roots", snap.Roots.Select(v => (long)v));
    }

    private static void AssertDelta(FixtureAssertions expect, DeltaMessage delta)
    {
        expect.AssertKey("base_epoch", delta.BaseEpoch);
        expect.AssertKey("epoch", delta.Epoch);
        expect.AssertKey("op_count", delta.Ops.Count);
        expect.AssertKey("op_variants", delta.Ops.Select(OpVariantOf));

        var cellSet = Assert.IsType<DeltaOp.CellSet>(delta.Ops[0]);
        var inline = Assert.IsType<IpcValue.Inline>(cellSet.Payload);
        expect.AssertKey("first_op_payload", inline.Bytes);

        var nodeAdd = delta.Ops.OfType<DeltaOp.NodeAdd>().First();
        expect.AssertKey("node_add_type_tag", nodeAdd.TypeTag);
    }

    private static void AssertCrdtSync(FixtureAssertions expect, CrdtSyncMessage sync)
    {
        var frontier = sync.Frontier ?? [];
        expect.AssertKey("frontier_len", frontier.Count);
        expect.AssertKey("frontier_first_peer", frontier[0].Peer);
        expect.AssertKey("frontier_first_stamp_wall_time", frontier[0].Stamp.WallTime);
        expect.AssertKey("op_count", sync.Ops.Count);
        expect.AssertKey("first_op_node", sync.Ops[0].Node);
        // Decoded-value assertion, not an encoding one: both self-describing codecs WRITE
        // `key` for a CrdtOp (null when unset — an anti-entropy op's addressing is part of
        // its merge identity). What must survive the round trip is that the decoder reads
        // that null back as absent.
        expect.AssertKey("first_op_key_absent", sync.Ops[0].Key is null);
        expect.AssertKey("second_op_node", sync.Ops[1].Node);
        expect.AssertKey("second_op_key", sync.Ops[1].Key);
        expect.AssertKey("second_op_stamp_peer", sync.Ops[1].Stamp.Peer);
    }

    private static void AssertValues(FixtureAssertions expect, IpcMessage message)
    {
        switch (message)
        {
            case SnapshotMessage snap: AssertSnapshot(expect, snap); break;
            case DeltaMessage delta: AssertDelta(expect, delta); break;
            case CrdtSyncMessage sync: AssertCrdtSync(expect, sync); break;
            default: throw new InvalidOperationException($"codec fixture pins no runner for {message}");
        }
    }

    [Fact]
    public void Json_frames_round_trip_through_the_reference_codec() =>
        ProseLedger.Replay(Corpus, JsonFixture, ReplayJson);

    private static void ReplayJson(ProseLedger prose)
    {
        if (SpecCorpus.Root is null) return;

        using var document = SpecCorpus.Load(Corpus, JsonFixture);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal("FrameCodecRoundTrip", root.GetProperty("kind").GetString());
        Assert.Equal("json", root.GetProperty("codec").GetString());

        // The fixture-level block pins the codec's identity and the two distinct senses of
        // "canonical" protocol.md keeps apart (`role` = the required interop floor,
        // `byte_canonical` = one deterministic byte form per message).
        var meta = FixtureAssertions.Of(root, "assertions", $"{Corpus}/{JsonFixture} assertions", prose);
        meta.AssertKey("codec", "json");
        meta.AssertKey("self_describing", true);
        meta.AssertKey("byte_canonical", true);
        meta.AssertKey("required_of_binding", "MUST");
        meta.AssertKey("role", "reference");

        // PROXY, and said so at the call site (#lzprosekeyconvention). The paragraph keeps two
        // senses of "canonical" apart, so the keys it is about are `role` and `byte_canonical`
        // — but both are compared against a literal this runner writes, and no run observes
        // "this codec is the required interop floor". `round_trip_equals_source` is the
        // executable half: it is what a codec that is really spoken here produces, so the
        // claim is not green over a runner that decodes nothing.
        meta.ProseKey("note", "role", "byte_canonical", "round_trip_equals_source");

        var scenarios = SpecCorpus.Scenarios(root, Corpus, JsonFixture);
        var replayed = 0;
        foreach (var scenario in scenarios.All())
        {
            var where = scenario.GetProperty("id").GetString()!;
            var source = IpcWire.Deserialize(scenario.GetProperty("wire").GetRawText());
            Assert.Equal(scenario.GetProperty("variant").GetString(), VariantOf(source));

            // Encode the DECODED message and decode the result. The fixture literal is never
            // re-asserted, so a codec that silently drops a field cannot be masked by reading
            // the input back.
            var roundTripped = IpcWire.Deserialize(IpcWire.Serialize(source));

            var expect = FixtureAssertions.Of(scenario, "expect", $"{Corpus}/{JsonFixture} {where}", prose);
            expect.AssertKey("round_trip_equals_source", RoundTripEqual(roundTripped, source));
            AssertValues(expect, roundTripped);
            expect.Verify();
            replayed += 1;
        }

        // Against what the run REPLAYED, never against the fixture's own scenarios array —
        // that comparison is the fixture agreeing with itself and is green over a runner that
        // opened the file and stopped.
        meta.AssertKey("scenario_count", replayed);
        meta.Verify();

        Assert.Equal(3, replayed);
        prose.VerifyProse(JsonFixture);
    }

    [Fact]
    public void Msgpack_frames_round_trip_through_the_cross_language_binary_default() =>
        ProseLedger.Replay(Corpus, MsgPackFixture, ReplayMsgPack);

    private static void ReplayMsgPack(ProseLedger prose)
    {
        if (SpecCorpus.Root is null) return;

        using var document = SpecCorpus.Load(Corpus, MsgPackFixture);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal("FrameCodecRoundTrip", root.GetProperty("kind").GetString());
        Assert.Equal("msgpack", root.GetProperty("codec").GetString());

        // `byte_canonical: false` is the whole reason this fixture pins decoded values and
        // encoded FIELD NAMES instead of golden bytes: a MessagePack map's key order is
        // encoder-defined, so two conforming bindings MAY emit byte-different frames for the
        // same IpcMessage.
        var meta = FixtureAssertions.Of(root, "assertions", $"{Corpus}/{MsgPackFixture} assertions", prose);
        meta.AssertKey("codec", "msgpack");
        meta.AssertKey("self_describing", true);
        meta.AssertKey("byte_canonical", false);
        meta.AssertKey("required_of_binding", "MUST");
        meta.AssertKey("role", "cross_language_binary_default");

        // The paragraph states WHY this fixture pins decoded values and field NAMES instead of
        // golden bytes, and names the key that verifies the named-map encoding.
        // `byte_canonical` is a literal comparison and carries the obligation only by proxy —
        // non-canonicality is not observable, which is the paragraph's own point —  but
        // `encoded_body_field_names` is read off the ENCODED BYTES, so this discharge has an
        // executable half that a positional encoder fails (#lzprosekeyconvention). It is a key
        // of a per-scenario `expect` block, which is why the ledger is fixture-scoped.
        meta.ProseKey("note", "byte_canonical", "encoded_body_field_names");

        var scenarios = SpecCorpus.Scenarios(root, Corpus, MsgPackFixture);
        var replayed = 0;
        var encodingKeys = 0;
        foreach (var scenario in scenarios.All())
        {
            var where = scenario.GetProperty("id").GetString()!;

            // `wire` is written in the REFERENCE json form — the same three values the json
            // fixture carries — so the msgpack half starts from the identical message and
            // differs only in how it is serialized.
            var source = IpcWire.Deserialize(scenario.GetProperty("wire").GetRawText());
            Assert.Equal(scenario.GetProperty("variant").GetString(), VariantOf(source));

            var frame = MsgPackWire.Serialize(source);
            var expect = FixtureAssertions.Of(scenario, "expect", $"{Corpus}/{MsgPackFixture} {where}", prose);

            // Encoding first, decoded values second. A positional encoder decodes back to an
            // equal message under a matching positional decoder and is still non-conforming,
            // so the named-field rule is checked where it lives: in the bytes.
            encodingKeys += AssertEncoding(expect, frame);

            var roundTripped = MsgPackWire.Deserialize(frame);
            expect.AssertKey("round_trip_equals_source", RoundTripEqual(roundTripped, source));
            AssertValues(expect, roundTripped);
            expect.Verify();
            replayed += 1;
        }

        meta.AssertKey("scenario_count", replayed);
        meta.Verify();

        // Non-zero, expected magnitude (#lzvacuousrun). "Three green rungs over nothing" is
        // the failure this suite has actually shipped before: a runner that replayed no
        // scenario, or reached no encoded_* key, would otherwise report OK over an empty set.
        Assert.Equal(3, replayed);
        Assert.Equal(3, scenarios.Count);
        // 2 envelope/body keys per scenario, plus first_node (Snapshot) and first_op/second_op
        // (CrdtSync). Exact, not a floor: a runner that quietly reached fewer would pass one.
        Assert.Equal(9, encodingKeys);
        prose.VerifyProse(MsgPackFixture);
    }

    /// <summary>
    /// The fixture pins three variants; <c>msgpack</c> is MUST-level for the whole family.
    /// </summary>
    [Fact]
    public void Msgpack_covers_the_frames_the_fixture_does_not_carry()
    {
        var request = new ResyncRequestMessage(12);
        Assert.Equal(request, MsgPackWire.Deserialize(MsgPackWire.Serialize(request)));
        var ack = new OutboxAckMessage(41);
        Assert.Equal(ack, MsgPackWire.Deserialize(MsgPackWire.Serialize(ack)));

        using var view = MsgPackWire.Inspect(MsgPackWire.Serialize(request));
        Assert.Equal(new[] { "ResyncRequest" }, SortedFieldNames(view.RootElement));
        Assert.Equal(
            new[] { "from_epoch" },
            SortedFieldNames(view.RootElement.GetProperty("ResyncRequest")));

        // SharedBlob is the other externally tagged NodeState/IpcValue arm, and a NodeSnapshot
        // WITH a key is the other side of the omit-when-absent rule.
        var blob = new ShmBlobRef(8, 16, 1, 2, 99, BlobBackendKind.Arrow);
        var snapshot = new SnapshotMessage(
            3,
            [new NodeSnapshot(1, "blob", new NodeState.SharedBlob(blob), "scores/alice")],
            [],
            [1]);
        Assert.Equal(
            IpcWire.Serialize(snapshot),
            IpcWire.Serialize(MsgPackWire.Deserialize(MsgPackWire.Serialize(snapshot))));

        using var encoded = MsgPackWire.Inspect(MsgPackWire.Serialize(snapshot));
        var node = encoded.RootElement.GetProperty("Snapshot").GetProperty("nodes")[0];
        Assert.Equal(new[] { "key", "node", "state", "type_tag" }, SortedFieldNames(node));
        Assert.Equal(new[] { "SharedBlob" }, SortedFieldNames(node.GetProperty("state")));
    }

    /// <summary>
    /// The frames a conforming peer never sends are REJECTED, not read leniently.
    /// </summary>
    /// <remarks>
    /// Accepting them would make this binding read frames no conforming peer produces — a
    /// private extension wearing the <c>msgpack</c> token, which is the defect protocol.md
    /// § Frame codecs names outright.
    /// </remarks>
    [Fact]
    public void Msgpack_rejects_frames_outside_the_wire()
    {
        // `bin` in a byte-payload position. The reference encoder writes Vec<u8> through
        // serde's default seq impl, so a byte payload is an ARRAY OF INTEGERS; MessagePack
        // libraries that map byte[] to `bin` by default are not speaking this wire.
        // {"Delta":{"base_epoch":0,"epoch":1,"ops":[{"CellSet":{"node":1,"payload":{"Inline":bin[10]}}}]}}
        var binPayload = new List<byte>
        {
            0x81, 0xa5, 0x44, 0x65, 0x6c, 0x74, 0x61,           // {"Delta":
            0x83,                                                //   3-entry map
            0xaa, 0x62, 0x61, 0x73, 0x65, 0x5f, 0x65, 0x70, 0x6f, 0x63, 0x68, 0x00,
            0xa5, 0x65, 0x70, 0x6f, 0x63, 0x68, 0x01,
            0xa3, 0x6f, 0x70, 0x73, 0x91,                        //   "ops": [
            0x81, 0xa7, 0x43, 0x65, 0x6c, 0x6c, 0x53, 0x65, 0x74, // {"CellSet":
            0x82,
            0xa4, 0x6e, 0x6f, 0x64, 0x65, 0x01,
            0xa7, 0x70, 0x61, 0x79, 0x6c, 0x6f, 0x61, 0x64,
            0x81, 0xa6, 0x49, 0x6e, 0x6c, 0x69, 0x6e, 0x65,
            0xc4, 0x01, 0x0a,                                    // bin8 [10]  <- not this wire
        };
        Assert.Throws<JsonException>(() => MsgPackWire.Deserialize([.. binPayload]));

        // A positional struct: [1, "i32", …] where the wire requires a named-field map.
        Assert.Throws<JsonException>(() => MsgPackWire.Deserialize([0x81, 0xa9, 0x4f, 0x75, 0x74,
            0x62, 0x6f, 0x78, 0x41, 0x63, 0x6b, 0x91, 0x29]));

        // An integer-keyed map is the discriminator framing protocol.md excludes outright.
        Assert.Throws<JsonException>(() => MsgPackWire.Deserialize([0x81, 0x00, 0x01]));

        var ack = MsgPackWire.Serialize(new OutboxAckMessage(41));
        Assert.Throws<JsonException>(() => MsgPackWire.Deserialize(ack[..^1]));
        Assert.Throws<JsonException>(() => MsgPackWire.Deserialize([.. ack, 0x00]));
    }

    /// <summary>
    /// Assert the fixture's <c>encoded_*</c> keys against a schema-less view of the FRAME BYTES.
    /// </summary>
    /// <returns>How many <c>encoded_*</c> keys this scenario actually reached.</returns>
    /// <remarks>
    /// Nothing here touches the decoded <c>IpcMessage</c>. These are the only assertions that
    /// can see the difference between the <c>msgpack</c> wire and a private MessagePack
    /// framing that carries the same values.
    /// </remarks>
    private static int AssertEncoding(FixtureAssertions expect, byte[] frame)
    {
        using var encoded = MsgPackWire.Inspect(frame);
        var envelope = encoded.RootElement;

        // Externally tagged: exactly one entry, whose KEY is the variant name. An internally
        // tagged {"type": 0, "value": …} frame has two.
        Assert.Equal(JsonValueKind.Object, envelope.ValueKind);
        var tagged = Assert.Single(envelope.EnumerateObject());

        expect.AssertKey("encoded_envelope_key", tagged.Name);
        expect.AssertKey("encoded_body_field_names", SortedFieldNames(tagged.Value));
        var reached = 2;

        reached += AssertNestedFieldNames(expect, "first_node_encoded_field_names", tagged.Value, "nodes", 0);
        reached += AssertNestedFieldNames(expect, "first_op_encoded_field_names", tagged.Value, "ops", 0);
        reached += AssertNestedFieldNames(expect, "second_op_encoded_field_names", tagged.Value, "ops", 1);
        return reached;
    }

    private static int AssertNestedFieldNames(
        FixtureAssertions expect,
        string key,
        JsonElement body,
        string collection,
        int index) =>
        expect.TryAssertKeyWith(
            key,
            want => Assert.Equal(
                want.EnumerateArray().Select(item => item.GetString()!).ToArray(),
                SortedFieldNames(body.GetProperty(collection)[index])))
            ? 1
            : 0;

    /// <summary>
    /// The field names of one encoded struct, SORTED.
    /// </summary>
    /// <remarks>
    /// Sorted because a MessagePack map's key order is encoder-defined — the fixture pins the
    /// SET of names, never their order. The <c>Object</c> requirement is the assertion a
    /// positional encoder fails: it hands back an array with no names at all.
    /// </remarks>
    private static string[] SortedFieldNames(JsonElement map)
    {
        Assert.Equal(JsonValueKind.Object, map.ValueKind);
        return [.. map.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Structural equality for two decoded frames.
    /// </summary>
    /// <remarks>
    /// C# records give value equality, but a record holding <c>byte[]</c> /
    /// <c>IReadOnlyList&lt;T&gt;</c> members compares those by REFERENCE, so two frames
    /// decoded from identical bytes are unequal under <c>==</c>. Comparing the canonical
    /// re-serialization instead answers the question the fixture actually asks: did the round
    /// trip preserve the frame? It is not a byte-canonicality claim about the codec — the
    /// same encoder produced both strings.
    /// </remarks>
    private static bool RoundTripEqual(IpcMessage a, IpcMessage b) =>
        string.Equals(IpcWire.Serialize(a), IpcWire.Serialize(b), StringComparison.Ordinal);
}
