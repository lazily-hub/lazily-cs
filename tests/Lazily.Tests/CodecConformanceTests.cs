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
/// lazily-cs implements the <c>json</c> half. <c>msgpack</c> is an explicit carve-out
/// (declared in the interop peer and now in <c>scripts/check-conformance-coverage.sh</c>),
/// so <c>codec/frame_roundtrip_msgpack.json</c> is listed as known-uncovered rather than
/// silently ignored.
/// </para>
/// <para>
/// The runner decodes <c>wire</c>, RE-ENCODES the decoded message, decodes again, and checks
/// every <c>expect</c> key against that second decode. Asserting against the fixture literal
/// would prove nothing: the literal never passed through an encoder.
/// </para>
/// </remarks>
public sealed class CodecConformanceTests
{
    private const string Corpus = "codec";
    private const string JsonFixture = "frame_roundtrip_json.json";

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
    public void Json_frames_round_trip_through_the_reference_codec()
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
        var meta = FixtureAssertions.Of(root, "assertions", $"{Corpus}/{JsonFixture} assertions");
        meta.AssertKey("codec", "json");
        meta.AssertKey("self_describing", true);
        meta.AssertKey("byte_canonical", true);
        meta.AssertKey("required_of_binding", "MUST");
        meta.AssertKey("role", "reference");
        meta.AssertKey("scenario_count", root.GetProperty("scenarios").GetArrayLength());
        meta.ExcuseKey(
            "note",
            "prose: documents the reference-vs-byte-canonical distinction, states nothing the replay observes");
        meta.Verify();

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

            var expect = FixtureAssertions.Of(scenario, "expect", $"{Corpus}/{JsonFixture} {where}");
            expect.AssertKey("round_trip_equals_source", RoundTripEqual(roundTripped, source));
            AssertValues(expect, roundTripped);
            expect.Verify();
            replayed += 1;
        }

        Assert.Equal(3, replayed);
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
