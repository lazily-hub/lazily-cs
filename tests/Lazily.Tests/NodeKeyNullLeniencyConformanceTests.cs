using System.Globalization;
using System.Text.Json;
using Lazily;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// <c>NodeKey</c> null-leniency on decode (<c>#lzkeynullstrict</c>).
/// </summary>
/// <remarks>
/// <para>
/// protocol.md § NodeKey said a self-describing codec OMITS an absent <c>key</c>, and that a
/// decoder seeing no <c>key</c> field treats it as absent. That settled the omitted form and left
/// an explicit <c>key: null</c> undefined — and three bindings diverged there. The clause is now
/// explicit: omit-when-absent binds the ENCODER, and a decoder MUST accept both forms as absent,
/// refusing neither and constructing a key from neither.
/// </para>
/// <para>
/// lazily-cs was already lenient: <c>OptionalString</c> tests <c>JsonValueKind.Null</c> rather than
/// only property presence. This runner is what holds it there, and pins the other half — the
/// encoder must still OMIT the field, because a decoder that reads null as absent and writes it
/// straight back out has a correct decoded value and a non-conforming encoder.
/// </para>
/// </remarks>
public sealed class NodeKeyNullLeniencyConformanceTests
{
    private const string Corpus = "codec";
    private const string Fixture = "nodekey_null_leniency.json";

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

    private static IpcMessage Decode(JsonElement scenario)
    {
        var codec = scenario.GetProperty("codec").GetString();
        return codec switch
        {
            "json" => IpcWire.Deserialize(scenario.GetProperty("wire_json").GetString()!),
            "msgpack" => MsgPackWire.Deserialize(HexToBytes(scenario.GetProperty("wire_msgpack_hex").GetString()!)),
            _ => throw new InvalidOperationException($"unknown codec {codec}"),
        };
    }

    /// <summary>
    /// Re-encode under the scenario's own codec and read the field set back off the WIRE, not off
    /// the typed record — a typed record cannot distinguish "field absent" from "field present and
    /// null", which is the whole distinction under test.
    /// </summary>
    private static JsonElement ReencodedNode(JsonElement scenario, IpcMessage message, out JsonDocument owner)
    {
        // Fail closed (#lzscenariobodyskip): the ternary's false arm ASSUMED json, so an
        // unrecognised codec was silently re-encoded through the OTHER codec than the one the
        // scenario named — and the msgpack leg of the round trip went unproven while green.
        var codec = scenario.GetProperty("codec").GetString();
        owner = codec switch
        {
            // Through the msgpack codec specifically. Both codecs share the same value tree, but
            // that is worth proving rather than assuming: the #lzmsgpackparity defect was a msgpack
            // encoder writing `key: null` while json omitted it.
            "msgpack" => MsgPackWire.Inspect(MsgPackWire.Serialize(message)),
            "json" => JsonDocument.Parse(IpcWire.Serialize(message)),
            _ => throw new InvalidOperationException($"unknown codec in fixture: {codec}"),
        };

        var root = owner.RootElement;
        return Field(scenario) == "snapshot"
            ? root.GetProperty("Snapshot").GetProperty("nodes")[0]
            : root.GetProperty("Delta").GetProperty("ops")[0].GetProperty("NodeAdd");
    }

    /// <summary>
    /// The wire FORM of this scenario's <c>key</c> slot, read off the RAW frame BEFORE any
    /// decode.
    /// </summary>
    /// <remarks>
    /// The control this fixture's <c>wire_encoding</c> obligation needs and no decoded value
    /// can supply. Every key in the fixture's <c>expect</c> blocks is IDENTICAL for the
    /// <c>omitted</c> and <c>null</c> families — that is the point of the clause, both forms
    /// read as absent — so a runner whose codec collapsed the two the instant it parsed would
    /// satisfy all twelve scenarios while the four <c>null</c> ones were the four
    /// <c>omitted</c> ones wearing a different id. At least one binding was in exactly that
    /// state. This reads the slot where the distinction actually lives, which is what the
    /// sibling blob-backend runner already does for <c>backend</c>, and it is what makes
    /// <c>key_forms</c> an observation instead of a literal.
    /// </remarks>
    private static string WireKeyForm(JsonElement scenario, out JsonDocument owner)
    {
        // Fail closed (#lzscenariobodyskip), and never through the library's decoder: json is
        // parsed as raw text and msgpack is transcribed schema-lessly, so the ABSENT map entry
        // and the explicit nil arrive here as different shapes or not at all.
        var codec = scenario.GetProperty("codec").GetString();
        owner = codec switch
        {
            "json" => JsonDocument.Parse(scenario.GetProperty("wire_json").GetString()!),
            "msgpack" => MsgPackWire.Inspect(
                HexToBytes(scenario.GetProperty("wire_msgpack_hex").GetString()!)),
            _ => throw new InvalidOperationException($"unknown codec in fixture: {codec}"),
        };

        var root = owner.RootElement;
        var node = Field(scenario) == "snapshot"
            ? root.GetProperty("Snapshot").GetProperty("nodes")[0]
            : root.GetProperty("Delta").GetProperty("ops")[0].GetProperty("NodeAdd");

        if (!node.TryGetProperty("key", out var key)) return "omitted";
        return key.ValueKind == JsonValueKind.Null ? "null" : "present";
    }

    /// <summary>
    /// Fail closed (#lzscenariobodyskip): `field` selected between the snapshot and delta wire
    /// shapes through a bare ternary, so an unrecognised spelling did not skip the check — it
    /// silently moved it onto the delta path while the fixture was talking about the snapshot.
    /// </summary>
    private static string Field(JsonElement scenario)
    {
        var field = scenario.GetProperty("field").GetString();
        if (field is not ("snapshot" or "node_add"))
            throw new InvalidOperationException($"unknown node-key field in fixture: {field}");
        return field;
    }

    private static string? DecodedKey(JsonElement scenario, IpcMessage message) =>
        Field(scenario) == "snapshot"
            ? ((SnapshotMessage)message).Nodes[0].Key
            : ((DeltaOp.NodeAdd)((DeltaMessage)message).Ops[0]).Key;

    [Fact]
    public void NodeKey_null_leniency_both_wire_forms_decode_as_absent_and_the_encoder_still_omits() =>
        ProseLedger.Replay(Corpus, Fixture, Replay);

    private static void Replay(ProseLedger prose)
    {
        if (SpecCorpus.Root is null) return;

        using var document = SpecCorpus.Load(Corpus, Fixture);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal("NodeKeyNullLeniency", root.GetProperty("kind").GetString());

        var meta = FixtureAssertions.Of(root, "assertions", $"{Corpus}/{Fixture} assertions", prose);
        meta.AssertKey("required_of_binding", "MUST");

        // `scenario_count`, `codecs`, `fields` and `key_forms` are asserted AFTER the loop,
        // against what the run really replayed — `key_forms` against the forms read off the RAW
        // WIRE. Comparing them to hand-written literals, or to the fixture's own scenarios
        // array, is green over a runner that decodes nothing, which is the vacuity
        // `anti_vacuity` exists to name; and `key_forms` is named by two discharges below, so a
        // literal there would discharge nothing at all.

        // The four paragraphs the corpus declares, each discharged by the keys that carry its
        // obligation rather than by a sentence saying it is prose (#lzprosekeyconvention). The
        // named keys are per-scenario `expect` keys asserted below — the ledger is
        // fixture-scoped, so a claim made here is matched against what the whole replay
        // asserted.
        meta.ProseKey("clause", "decoded_key", "key_forms");

        // PROXY, with the control the paragraph actually needs now in place. `wire_encoding` is
        // an obligation on the RUNNER — parse the raw text and hex rather than re-serialize a
        // pre-parsed object — which no assertion key reddens on its own. `key_forms` is the
        // closest executable key BECAUSE it is now collected from the raw wire slot before any
        // decode: the ABSENT entry and the explicit nil are proven distinguishable in this
        // runner, which is exactly what the paragraph says must survive into it.
        meta.ProseKey("wire_encoding", "codecs", "key_forms");

        meta.ProseKey("reencode_obligation", "reencoded_key_field_present");
        meta.ProseKey("anti_vacuity", "decoded_key", "key_forms");

        // NOT prose, and not declared as such: it names the script that generated the wire
        // frames, and there is nothing in this binding to compare it against.
        meta.ExcuseKey(
            "generator",
            "names the corpus-side script that mints these frames; it states no obligation on a "
            + "binding and nothing here could disagree with it");

        var scenarios = SpecCorpus.Scenarios(root, Corpus, Fixture);

        // Anti-vacuity in both directions. A runner that never decodes reports "absent" for
        // everything and satisfies all eight omitted/null scenarios; the `present` count is what
        // only a real decode can produce.
        var replayed = 0;
        var keysDecoded = 0;
        var observedCodecs = new SortedSet<string>(StringComparer.Ordinal);
        var observedFields = new SortedSet<string>(StringComparer.Ordinal);
        var observedKeyForms = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var scenario in scenarios.All())
        {
            var where = scenario.GetProperty("id").GetString()!;
            var expect = FixtureAssertions.Of(scenario, "expect", $"{Corpus}/{Fixture} {where}", prose);
            replayed += 1;
            observedCodecs.Add(scenario.GetProperty("codec").GetString()!);
            observedFields.Add(Field(scenario));

            // The raw-wire control, BEFORE the decoder runs. The scenario's own `key_form`
            // label is the claim; the bytes are the fact. A codec that collapsed the ABSENT
            // entry into an explicit nil — or a fixture edit that did — diverges here, where
            // every `expect` key of the omitted and null families is identical by design.
            var wireForm = WireKeyForm(scenario, out var wireOwner);
            using (wireOwner)
            {
                Assert.Equal(scenario.GetProperty("key_form").GetString(), wireForm);
            }

            observedKeyForms.Add(wireForm);

            var message = Decode(scenario);
            var key = DecodedKey(scenario, message);
            if (key is not null) keysDecoded += 1;

            // The decode half: omitted and explicit-null must both arrive absent.
            expect.AssertKey("decoded_key", key);

            var node = ReencodedNode(scenario, message, out var owner);
            using (owner)
            {
                // The encode half, which no assertion over the decoded value reaches.
                var present = node.TryGetProperty("key", out var encoded)
                    && encoded.ValueKind != JsonValueKind.Null;
                expect.AssertKey("reencoded_key_field_present", present);

                expect.AssertKey("node", node.GetProperty("node").GetUInt64());
                expect.AssertKey("type_tag", node.GetProperty("type_tag").GetString());
                expect.AssertKey(
                    "payload",
                    node.GetProperty("state").GetProperty("Payload").EnumerateArray()
                        .Select(item => (byte)item.GetInt32()));
            }

            expect.AssertKey(
                "epoch",
                message switch
                {
                    SnapshotMessage s => s.Epoch,
                    DeltaMessage d => d.Epoch,
                    _ => throw new InvalidOperationException($"{where}: unexpected variant"),
                });
            expect.Verify();
        }

        // The count and the three vocabularies, from the run rather than from a literal.
        // `key_forms` in particular is the set of forms read off the WIRE, so it is the
        // control the `wire_encoding` discharge above names rather than a restatement of the
        // fixture's own array.
        meta.AssertKey("scenario_count", replayed);
        meta.AssertKeyWith(
            "codecs",
            want => Assert.Equal(
                want.EnumerateArray().Select(item => item.GetString())
                    .OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                observedCodecs.ToArray()));
        meta.AssertKeyWith(
            "fields",
            want => Assert.Equal(
                want.EnumerateArray().Select(item => item.GetString())
                    .OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                observedFields.ToArray()));
        meta.AssertKeyWith(
            "key_forms",
            want => Assert.Equal(
                want.EnumerateArray().Select(item => item.GetString())
                    .OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                observedKeyForms.ToArray()));

        meta.Verify();
        prose.VerifyProse(Fixture);

        Assert.Equal(12, replayed);
        Assert.Equal(12, scenarios.Count);
        Assert.Equal(4, keysDecoded);

        // Each wire form really occurred, in both codecs — the omitted/null/present split is
        // in the bytes, not only in the scenario labels.
        Assert.Equal(3, observedKeyForms.Count);
    }
}
