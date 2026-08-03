using System.Globalization;
using System.Text;
using System.Text.Json;
using Lazily;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// <c>NodeId</c> exact-representation bound (<c>#lzspecdecoderbound</c>).
/// </summary>
/// <remarks>
/// <para>
/// protocol.md § NodeId / PeerId stated the 2^53 bound as a PRODUCER obligation and said
/// nothing about what a decoder does when it receives a violation. That left the receiving
/// half undefined, which is exactly where the bindings diverged. The clause is now
/// normative: a decoder that cannot represent a received identifier exactly MUST reject the
/// frame rather than round it.
/// </para>
/// <para>
/// lazily-cs is one of the three bindings that never has to refuse: <c>NodeId</c> is a
/// <c>ulong</c> and <c>JsonElement.GetUInt64</c> reads the literal text, so the full u64
/// range — including <c>u64::MAX</c> — decodes exactly. This runner therefore asserts the
/// <c>exact</c> branch for all six scenarios, which makes it a <em>floor</em>: a scenario
/// that turns out to be rejectable here means the fixture is wrong about the wire rather
/// than about any decoder.
/// </para>
/// <para>
/// The fixture carries its wire frames as raw text (json) and hex (msgpack), and its
/// expected identifier as a decimal STRING. .NET would not round a bare 9007199254740993
/// while loading the file, but the contract is uniform across the nine bindings on purpose:
/// the double-backed ones would, and a fixture that reads differently per runtime is not
/// one fixture.
/// </para>
/// </remarks>
public sealed class NodeIdExactRangeConformanceTests
{
    private const string Corpus = "codec";
    private const string Fixture = "nodeid_exact_range.json";

    private static byte[] HexToBytes(string hex)
    {
        Assert.True(hex.Length % 2 == 0, "hex string has an odd length");
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    /// <summary>
    /// Decode a scenario's wire frame with the codec it names.
    /// </summary>
    /// <remarks>
    /// lazily-cs never has to refuse anything in this corpus, so a throw here is a real
    /// failure rather than the <c>exact_or_reject</c> branch — which is why this returns the
    /// message directly instead of a result.
    /// </remarks>
    private static IpcMessage Decode(JsonElement scenario, FixtureAssertions expect)
    {
        var codec = scenario.GetProperty("codec").GetString();
        switch (codec)
        {
            // The raw TEXT through the codec's own entry point, so the parse that would round
            // on a narrower runtime is inside the code under test rather than in the runner.
            case "json":
                var json = scenario.GetProperty("wire_json").GetString()!;
                expect.AssertKey("wire_input_fnv1a64", WireInputDigest.Fnv1a64Hex(Encoding.UTF8.GetBytes(json)));
                return IpcWire.Deserialize(json);
            case "msgpack":
                var msgpack = HexToBytes(scenario.GetProperty("wire_msgpack_hex").GetString()!);
                expect.AssertKey("wire_input_fnv1a64", WireInputDigest.Fnv1a64Hex(msgpack));
                return MsgPackWire.Deserialize(msgpack);
            default:
                throw new InvalidOperationException($"unknown codec {codec}");
        }
    }

    [Fact]
    public void NodeId_exact_representation_bound_is_enforced_by_refusal_never_rounding() =>
        ProseLedger.Replay(Corpus, Fixture, Replay);

    private static void Replay(ProseLedger prose)
    {
        if (SpecCorpus.Root is null) return;

        using var document = SpecCorpus.Load(Corpus, Fixture);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal("NodeIdExactRange", root.GetProperty("kind").GetString());

        var meta = FixtureAssertions.Of(root, "assertions", $"{Corpus}/{Fixture} assertions", prose);
        meta.AssertKey("required_of_binding", "MUST");

        // The three paragraphs the corpus declares, discharged by the keys that carry them
        // (#lzprosekeyconvention). `outcomes` is NOT one of them: its English is nested UNDER a
        // data key, so the assertion is the key SET and the parent key's own assertion
        // discharges it — asserted against the outcomes this run observed, after the loop.
        meta.ProseKey("clause", "outcome", "node_id_decimal");

        // Executable proof that the exact raw text / decoded-hex byte buffer reaches the
        // library decoder rather than a reconstructed proxy.
        meta.ProseKey("wire_encoding", "wire_input_fnv1a64");

        meta.ProseKey("anti_vacuity", "outcome", "node_id_decimal", "node_count");

        meta.ExcuseKey(
            "generator",
            "names the corpus-side script that mints these frames; it states no obligation on a "
            + "binding and nothing here could disagree with it");

        // The declared outcome vocabulary, read through the raw element: every scenario's
        // `outcome` is checked to be a member of it, and the SET is asserted against what the
        // run observed once the loop is done. Reading it here does not consume it.
        var declaredOutcomes = meta.Element.GetProperty("outcomes").EnumerateObject()
            .Select(member => member.Name).ToArray();
        var observedOutcomes = new SortedSet<string>(StringComparer.Ordinal);

        // `codecs` and `scenario_count` are asserted after the loop against what the run really
        // replayed. A hand-written literal, or the fixture's own scenarios array, is green over
        // a runner that decodes nothing.
        var observedCodecs = new SortedSet<string>(StringComparer.Ordinal);

        var scenarios = SpecCorpus.Scenarios(root, Corpus, Fixture);

        // Anti-vacuity. `exact_or_reject` is satisfied by a runner that decodes nothing, so
        // the count of scenarios this run actually ACCEPTED is the assertion that the decoder
        // ran at all. lazily-cs covers the whole corpus, so the expected value is every
        // scenario — anything less is a narrowing of the binding, not of the test.
        var accepted = 0;

        foreach (var scenario in scenarios.All())
        {
            var where = scenario.GetProperty("id").GetString()!;
            var expect = FixtureAssertions.Of(scenario, "expect", $"{Corpus}/{Fixture} {where}", prose);
            var expected = ulong.Parse(
                scenario.GetProperty("expect").GetProperty("node_id_decimal").GetString()!,
                CultureInfo.InvariantCulture);

            // `outcome` is the corpus-wide statement of what a decoder may do. lazily-cs
            // reads it as a constraint on the fixture: both branches oblige it to decode,
            // because a ulong represents everything the wire type allows.
            expect.AssertKeyWith("outcome", want =>
            {
                var outcome = want.GetString()!;
                Assert.True(
                    declaredOutcomes.Contains(outcome, StringComparer.Ordinal),
                    $"{where}: outcome {outcome} is not one the fixture's own `outcomes` "
                    + $"vocabulary declares [{string.Join(", ", declaredOutcomes)}]");
                observedOutcomes.Add(outcome);
            });

            var message = Decode(scenario, expect);
            accepted += 1;
            observedCodecs.Add(scenario.GetProperty("codec").GetString()!);

            var snapshot = Assert.IsType<SnapshotMessage>(message);
            Assert.Equal("Snapshot", scenario.GetProperty("variant").GetString());

            expect.AssertKey("epoch", snapshot.Epoch);
            expect.AssertKey("node_count", snapshot.Nodes.Count);

            var node = snapshot.Nodes[0];
            // The discriminating assertion. A rounding or wrapping decoder yields a
            // NEIGHBOURING identifier that still decodes cleanly and addresses a different
            // node, so comparing the exact value is the only check that sees the substitution.
            Assert.Equal(expected, node.Node);
            expect.AssertKey("node_id_decimal", node.Node.ToString(CultureInfo.InvariantCulture));
            expect.AssertKey("type_tag", node.TypeTag);
            var payload = Assert.IsType<NodeState.Payload>(node.State);
            expect.AssertKey("payload", payload.Bytes);
            Assert.Single(snapshot.Roots);
            expect.AssertKey("root_id_decimal", snapshot.Roots[0].ToString(CultureInfo.InvariantCulture));
            expect.Verify();
        }

        // The vocabulary rung, asserted against what the run OBSERVED rather than against a
        // literal: a fixture that grew a third outcome no scenario carries, or a runner that
        // reached only one of the two branches, is a set difference either way.
        // Through the tracker's key-set entry point rather than a hand-rolled comparison
        // (#lzsubblockkeyset): `outcomes` is object-valued, and Verify() now refuses one that
        // reached neither AssertObjectKey nor AssertKeySet. The comparison is the same; what
        // changed is that the tracker can now tell it happened.
        meta.AssertKeySet("outcomes", observedOutcomes);

        meta.AssertKey("scenario_count", accepted);
        meta.AssertKeyWith(
            "codecs",
            want => Assert.Equal(
                want.EnumerateArray().Select(item => item.GetString())
                    .OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                observedCodecs.ToArray()));

        meta.Verify();
        prose.VerifyProse(Fixture);

        Assert.Equal(6, accepted);
        Assert.Equal(6, scenarios.Count);
    }
}
