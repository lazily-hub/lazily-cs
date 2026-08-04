using System.Globalization;
using System.Text;
using System.Text.Json;
using Lazily;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Blob-backend discriminator strictness on decode (<c>#lzblobbackendstrict</c>).
/// </summary>
/// <remarks>
/// <para>
/// protocol.md § Zero-copy blob descriptor makes <c>ShmBlobRef.backend</c> optional with a
/// default of <c>shm</c>, and that OPTIONALITY is the forward-compatibility channel — it carries
/// every descriptor minted before the field existed. A PRESENT value outside
/// {<c>shm</c>, <c>arrow</c>, <c>in_process</c>} is a different fact and gets the opposite
/// answer: refuse the frame and NAME the token, never normalize it to <c>shm</c> or to a
/// sentinel.
/// </para>
/// <para>
/// Five of nine bindings normalized an unknown token to <c>shm</c>, each with a written
/// forward-compat rationale. That inverts the <c>resolve_wrong_backend</c> theorem
/// (docs/zero-copy-transport.md), which discharges non-resolution STRUCTURALLY by routing on
/// kind: normalizing routes a non-shm descriptor into the shm table and leaves the guarantee
/// riding, probabilistically and downstream, on a 64-bit checksum against a backend this build
/// really does resolve.
/// </para>
/// <para>
/// Fixture v2 carries the four shapes v1 declared or implied without carrying, and every one of
/// them lands on this binding:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>in_process</c>, the third declared backend. It decodes here and always did — the enum has
/// carried three members since the descriptor existed — so this is the control that proves the
/// vocabulary, not a fix. What did NOT exist was a runner assertion tying
/// <c>assertions.backends</c> to the set of backends an accept scenario actually decodes; a
/// scenario count cannot reach that, it is a set difference, and it is asserted below.
/// </description></item>
/// <item><description>
/// An explicit <c>null</c>, which is the ABSENT form and MUST decode as <c>shm</c>. This binding
/// REFUSED it — first as the token <c>''</c>, naming nothing, then, after v1, as a ValueKind
/// error. The right shape of failure for the wrong fact. <c>IpcWire.ReadBlob</c> now filters
/// <c>JsonValueKind.Null</c> out before the string check, so the descriptor is built with a null
/// <c>Backend</c> and the encoder omits the entry — the null does not survive a round trip.
/// </description></item>
/// <item><description>
/// A non-string <c>backend</c>, which is this binding's own defect from the v1 audit: the
/// integer walked into <c>GetString()</c> and escaped as <see cref="InvalidOperationException"/>,
/// outside the <see cref="JsonException"/> family both MUST-level codecs document. A caller
/// guarding a decode with one catch never saw it — the frame was still refused, and the refusal
/// failed PAST the handler. Repaired under v1 from a local theory; <c>rejection_is_decode_error</c>
/// is the canonical assertion that now pins it.
/// </description></item>
/// <item><description>
/// A Delta frame epoch (9) distinct from the descriptor epoch (5). v1 carried 9 in both, so
/// <c>expect.epoch</c> could not tell a runner reading the frame's epoch from one reading the
/// blob's. The key was REMOVED upstream rather than redefined, so a runner still reading it dies
/// loudly; <c>frame_epoch</c> and <c>blob_epoch</c> are asserted below against their own sources.
/// </description></item>
/// </list>
/// <para>
/// TWO CODECS ARE NOT TWO IMPLEMENTATIONS HERE, and this binding records that rather than
/// inferring independence from the scenario count (fixture <c>assertions.anti_vacuity</c>):
/// <see cref="MsgPackWire.Deserialize"/> transcribes the frame to JSON text and hands it to
/// <see cref="IpcWire.Deserialize"/>, so both codecs reach ONE discriminator dispatch. The
/// msgpack half still covers the bridge — a nil that failed to transcribe to a JSON null, or a
/// positive fixint that transcribed as a string, would diverge here — and the encoder, which is
/// genuinely a second path through <see cref="MsgPackWire.Serialize"/>.
/// </para>
/// </remarks>
public sealed class BlobBackendDiscriminatorConformanceTests
{
    private const string Corpus = "codec";
    private const string Fixture = "blob_backend_discriminator.json";

    /// <summary>
    /// The wire spelling of a backend, transcribed INDEPENDENTLY of the library's own private
    /// mapping.
    /// </summary>
    /// <remarks>
    /// A runner that asked the library how it spells a backend and then compared that to itself
    /// would agree with any renaming. This is the external transcription, so a drift in either
    /// direction is a mismatch against the fixture.
    /// </remarks>
    private static string TokenOf(BlobBackendKind kind) => kind switch
    {
        BlobBackendKind.Shm => "shm",
        BlobBackendKind.Arrow => "arrow",
        BlobBackendKind.InProcess => "in_process",
        _ => throw new InvalidOperationException($"unknown backend kind: {kind}"),
    };

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
    /// Decode the scenario's RAW wire form.
    /// </summary>
    /// <remarks>
    /// Never by re-serializing a parsed object: <c>schemas/defs.json</c> closes <c>backend</c> to
    /// an enum, so the reject frames AND the null frames are schema-INVALID by design and cannot
    /// survive a round trip through structured JSON. Raw text and raw hex are what the fixture
    /// carries, and the ABSENT map entry the omitted scenarios test only exists on the wire.
    /// </remarks>
    private static IpcMessage Decode(string codec, JsonElement scenario, FixtureAssertions expect)
    {
        switch (codec)
        {
            case "json":
                var json = scenario.GetProperty("wire_json").GetString()!;
                expect.AssertKey("wire_input_fnv1a64", WireInputDigest.Fnv1a64Hex(Encoding.UTF8.GetBytes(json)));
                return IpcWire.Deserialize(json);
            case "msgpack":
                var msgpack = HexToBytes(scenario.GetProperty("wire_msgpack_hex").GetString()!);
                expect.AssertKey("wire_input_fnv1a64", WireInputDigest.Fnv1a64Hex(msgpack));
                return MsgPackWire.Deserialize(msgpack);
            default:
                throw new InvalidOperationException($"unknown codec in fixture: {codec}");
        }
    }

    /// <summary>
    /// Re-encode under the scenario's OWN codec and read the blob body back off the WIRE.
    /// </summary>
    /// <remarks>
    /// The typed <see cref="ShmBlobRef"/> cannot distinguish "field omitted" from "field written
    /// as shm" — that distinction lives only in the encoded bytes, and it is the encoder half of
    /// the clause. Fail closed on an unrecognised codec (<c>#lzscenariobodyskip</c>): a ternary
    /// whose false arm assumed json would silently prove the json leg twice and the msgpack leg
    /// never.
    /// </remarks>
    private static JsonElement ReencodedBlob(string codec, IpcMessage message, out JsonDocument owner)
    {
        owner = codec switch
        {
            "msgpack" => MsgPackWire.Inspect(MsgPackWire.Serialize(message)),
            "json" => JsonDocument.Parse(IpcWire.Serialize(message)),
            _ => throw new InvalidOperationException($"unknown codec in fixture: {codec}"),
        };

        return owner.RootElement
            .GetProperty("Delta").GetProperty("ops")[0]
            .GetProperty("SlotValue").GetProperty("payload").GetProperty("SharedBlob");
    }

    private static (ulong Node, ShmBlobRef Blob, ulong FrameEpoch) Descriptor(IpcMessage message)
    {
        var delta = Assert.IsType<DeltaMessage>(message);
        var op = Assert.IsType<DeltaOp.SlotValue>(Assert.Single(delta.Ops));
        var payload = Assert.IsType<IpcValue.SharedBlob>(op.Payload);
        return (op.Node, payload.Blob, delta.Epoch);
    }

    /// <summary>
    /// Which of the two documented refusals a <see cref="JsonException"/> is, read off the
    /// LIBRARY's own message rather than off the scenario's label.
    /// </summary>
    /// <remarks>
    /// Mapping <c>backend_form</c> to <c>rejection_kind</c> would compare the fixture against
    /// itself and stay green for a decoder that raised one undifferentiated error for both. The
    /// two refusals are distinct facts — one names a token that is not in the enum, the other
    /// says the slot did not hold a token at all — and a caller that cannot tell them apart
    /// cannot tell a peer running a newer protocol from a peer emitting garbage.
    /// </remarks>
    private static string RejectionKindOf(JsonException failure) => failure.Message switch
    {
        var message when message.Contains("Unknown blob backend", StringComparison.Ordinal) =>
            "unknown_token",
        var message when message.Contains("must be one of", StringComparison.Ordinal) =>
            "non_string",
        var message => throw new Xunit.Sdk.XunitException(
            $"the decode error is neither documented refusal: {message}"),
    };

    [Fact]
    public void Blob_backend_discriminator_is_read_strictly_across_every_wire_form() =>
        ProseLedger.Replay(Corpus, Fixture, Replay);

    private static void Replay(ProseLedger prose)
    {
        if (SpecCorpus.Root is null) return;

        using var document = SpecCorpus.Load(Corpus, Fixture);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal("BlobBackendDiscriminator", root.GetProperty("kind").GetString());

        var meta = FixtureAssertions.Of(root, "assertions", $"{Corpus}/{Fixture} assertions", prose);
        meta.AssertKey("required_of_binding", "MUST");

        // `scenario_count`, `codecs` and `outcomes` are asserted AFTER the loop, against what
        // the run really replayed. Comparing `scenario_count` to the fixture's own scenarios
        // array is the fixture agreeing with itself, and comparing `codecs` / `outcomes` to a
        // hand-written literal is green over a runner that decodes nothing — the exact vacuity
        // `anti_vacuity` exists to name, which is why neither may be named in a discharge
        // until it is observed.

        // The nine paragraphs `assertions.prose` declares, each DISCHARGED by the executable
        // keys that carry its obligation (#lzprosekeyconvention). This replaces nine free-text
        // excuses that already named the discharging assertions in English — falsifiable in
        // principle and checked by nothing, which is exactly the state the convention closes.
        // Every key named below is asserted by this run, in this block or in a per-scenario
        // `expect` block, and the ledger fails the run if one of them stops being.
        meta.ProseKey(
            "clause", "decoded_backend", "rejected", "rejection_is_decode_error", "rejection_kind");

        // Executable proof that the exact raw text / decoded-hex byte buffer reaches the
        // library decoder rather than a reconstructed proxy.
        meta.ProseKey("wire_encoding", "wire_input_fnv1a64");

        meta.ProseKey("backend_form_vocabulary", "backend_forms", "backends", "decoded_backend");
        meta.ProseKey("reject_obligation", "error_names_token", "rejection_kind");
        meta.ProseKey("null_form", "decoded_backend", "reencoded_backend_field_present");
        meta.ProseKey("non_string_form", "rejection_is_decode_error", "rejection_kind");

        // The claim v1 could not make: the two epochs are read from their OWN sources, and
        // both keys are per-scenario — asserted long after this block is finished, which is
        // why the ledger is fixture-scoped rather than block-scoped.
        meta.ProseKey("epoch_disambiguation", "frame_epoch", "blob_epoch");

        meta.ProseKey(
            "anti_vacuity", "decoded_backend", "reencoded_backend_field_present", "backends");

        // PROXY. `theorem` names resolve_wrong_backend, a Lean theorem in lazily-formal; a run
        // can only prove its CONSEQUENCE. `rejected` and `decoded_backend` are that
        // consequence: an unknown kind is refused rather than routed, and an accepted one
        // resolves to the kind it arrived as, which is what "receivers route by kind" means
        // where this binding can see it.
        meta.ProseKey("theorem", "rejected", "decoded_backend");

        var scenarios = SpecCorpus.Scenarios(root, Corpus, Fixture);

        // Anti-vacuity, in every direction the fixture's own `anti_vacuity` note names.
        // `accepted`/`rejected` prove both outcomes were exercised; `arrowsDecoded` proves the
        // field was really READ (a decoder hardcoding shm passes the omitted, explicit-shm and
        // null scenarios and dies here); `fieldsEmitted` proves the encoder half is asymmetric
        // rather than echoing whatever arrived.
        var replayed = 0;
        var accepted = 0;
        var rejected = 0;
        var arrowsDecoded = 0;
        var fieldsEmitted = 0;

        // The three set-valued facts, accumulated from OBSERVED behaviour and compared against the
        // fixture's own vocabularies after the loop. `decodedBackends` is the one that would have
        // caught v1's missing `in_process`: it is a set difference against `assertions.backends`,
        // and no count of scenarios reaches it.
        var decodedBackends = new SortedSet<string>(StringComparer.Ordinal);
        var formsReplayed = new SortedSet<string>(StringComparer.Ordinal);
        var rejectionKinds = new SortedSet<string>(StringComparer.Ordinal);
        var codecsReplayed = new SortedSet<string>(StringComparer.Ordinal);
        var outcomesReplayed = new SortedSet<string>(StringComparer.Ordinal);

        // Every scenario is replayed even when an earlier one fails, and all of the failures are
        // reported together. A `foreach` that lets the first Assert unwind reports ONE scenario
        // and silently leaves the remaining thirteen unrun — which reads as "one thing is broken"
        // when the truth may be "the discriminator is not implemented at all", and makes the
        // fixture's json/msgpack pairing unobservable: the msgpack twin of a broken json scenario
        // never executes, so nothing here would notice if only the json leg worked. lazily-cs hit
        // exactly that while implementing v1.
        var failures = new List<string>();

        foreach (var scenario in scenarios.All())
        {
            var where = scenario.GetProperty("id").GetString()!;

            // Fail closed on every discriminator (#lzscenariobodyskip), and do it OUTSIDE the
            // collecting try: an unrecognised codec, outcome, or backend form is a fault in the
            // RUNNER's agreement with the corpus, not a finding about the library, and collecting
            // it would let a corpus this runner no longer understands report as fourteen ordinary
            // scenario failures.
            var codec = scenario.GetProperty("codec").GetString();
            if (codec is not ("json" or "msgpack"))
                throw new InvalidOperationException($"{where}: unknown codec in fixture: {codec}");

            var outcome = scenario.GetProperty("outcome").GetString();
            if (outcome is not ("accept" or "reject"))
                throw new InvalidOperationException($"{where}: unknown outcome: {outcome}");

            var form = scenario.GetProperty("backend_form").GetString();
            if (form is not ("omitted" or "shm" or "arrow" or "in_process" or "null"
                or "non_string" or "rdma"))
            {
                throw new InvalidOperationException($"{where}: unknown backend form: {form}");
            }

            Assert.Equal("Delta", scenario.GetProperty("variant").GetString());
            formsReplayed.Add(form);

            // Booked from the scenario the runner is about to REPLAY, so `codecs` and
            // `outcomes` below say which codecs and outcomes this run really exercised.
            codecsReplayed.Add(codec);
            outcomesReplayed.Add(outcome);

            var expect = FixtureAssertions.Of(scenario, "expect", $"{Corpus}/{Fixture} {where}", prose);
            replayed += 1;

            try
            {
                switch (outcome)
                {
                    case "accept":
                        {
                            accepted += 1;
                            var message = Decode(codec, scenario, expect);
                            var (node, blob, frameEpoch) = Descriptor(message);

                            // Through the library's OWN resolution function — the one every backend
                            // routes on — so an omitted or null discriminator is asserted where it
                            // is actually consumed, not where the runner would like it to be.
                            var token = TokenOf(blob.EffectiveBackend());
                            decodedBackends.Add(token);
                            if (blob.EffectiveBackend() == BlobBackendKind.Arrow) arrowsDecoded += 1;
                            expect.AssertKey("decoded_backend", token);

                            expect.AssertKey("node", node);
                            expect.AssertKey("offset", blob.Offset);
                            expect.AssertKey("len", blob.Length);
                            expect.AssertKey("generation", blob.Generation);
                            expect.AssertKey("checksum", blob.Checksum);

                            // The two epochs, each against its OWN source. v1 carried 9 in both, so
                            // one `expect.epoch` could not tell them apart and two bindings
                            // reported the ambiguity independently. Reading the frame epoch where
                            // the blob epoch belongs — the mutation this pins — now diverges by 4.
                            // The NotEqual keeps that separation from silently re-collapsing: if a
                            // future corpus edit made the two numbers equal again, every assertion
                            // above would still pass while proving nothing about which was read.
                            expect.AssertKey("frame_epoch", frameEpoch);
                            expect.AssertKey("blob_epoch", blob.Epoch);
                            Assert.NotEqual(
                                scenario.GetProperty("expect").GetProperty("frame_epoch").GetUInt64(),
                                scenario.GetProperty("expect").GetProperty("blob_epoch").GetUInt64());

                            var body = ReencodedBlob(codec, message, out var owner);
                            using (owner)
                            {
                                var present = body.TryGetProperty("backend", out var encoded);
                                if (present) fieldsEmitted += 1;
                                expect.AssertKey("reencoded_backend_field_present", present);

                                // A present field must also carry the SPELLING the decode produced.
                                // "present" alone is satisfied by an encoder writing "Arrow".
                                if (present) Assert.Equal(token, encoded.GetString());
                            }

                            break;
                        }

                    case "reject":
                        {
                            rejected += 1;

                            // `rejected` is asserted from the OBSERVED outcome of a real decode, not
                            // from a literal the runner already believes.
                            var caught = Record.Exception(() => Decode(codec, scenario, expect));
                            expect.AssertKey("rejected", caught is not null);

                            // The assertion that pins this binding's v1 defect. Both MUST-level
                            // codecs declare JsonException so a caller can guard a decode with ONE
                            // catch; the non-string used to arrive as InvalidOperationException out
                            // of GetString(), which is a refusal that fails PAST the handler — the
                            // frame is still refused and the peer never sees the error.
                            expect.AssertKey("rejection_is_decode_error", caught is JsonException);
                            var failure = Assert.IsType<JsonException>(caught);

                            var kind = RejectionKindOf(failure);
                            rejectionKinds.Add(kind);
                            expect.AssertKey("rejection_kind", kind);

                            // The assertion that separates "refused" from "refused for the stated
                            // reason". A decoder that mis-parses `checksum` and throws satisfies a
                            // bare is-error check while implementing none of the clause. Only the
                            // unknown-token refusal carries it — there is no token to name in a
                            // non-string slot — and WHICH scenarios carry it is asserted too, so a
                            // corpus that dropped the key from the token case could not slip past
                            // as "optional".
                            var namesToken = expect.TryAssertKeyWith(
                                "error_names_token",
                                want => Assert.Contains(
                                    want.GetString()!, failure.Message, StringComparison.Ordinal));
                            Assert.Equal(kind == "unknown_token", namesToken);
                            break;
                        }
                }

                expect.Verify();
            }
            catch (Exception failure)
            {
                failures.Add($"{where}: {failure.Message}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{Corpus}/{Fixture}: {failures.Count} of {scenarios.Count} scenarios failed:\n\n"
            + string.Join("\n\n", failures));

        // The vocabulary rungs, asserted against what the run OBSERVED rather than against a
        // literal. Both are set comparisons: `backend_forms` is sorted on both sides because the
        // fixture's declaration order (rdma last) is not the replay order (rdma before
        // non_string), and a sequence comparison here would be asserting the corpus's authoring
        // order rather than this binding's coverage of it.
        // The count and the two vocabularies, from the run rather than from the fixture or a
        // literal: `scenario_count` is how many scenarios were REPLAYED, and a runner that
        // decoded nothing reaches neither the codecs nor the outcomes below.
        meta.AssertKey("scenario_count", replayed);
        meta.AssertKeyWith(
            "codecs",
            want => Assert.Equal(
                want.EnumerateArray().Select(item => item.GetString())
                    .OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                codecsReplayed.ToArray()));
        meta.AssertKeyWith(
            "outcomes",
            want => Assert.Equal(
                want.EnumerateArray().Select(item => item.GetString())
                    .OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                outcomesReplayed.ToArray()));

        meta.AssertKeyWith(
            "backend_forms",
            want => Assert.Equal(
                want.EnumerateArray().Select(item => item.GetString())
                    .OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                formsReplayed.ToArray()));

        meta.AssertKeyWith(
            "rejection_kinds",
            want => Assert.Equal(
                want.EnumerateArray().Select(item => item.GetString())
                    .OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                rejectionKinds.ToArray()));

        meta.AssertKeyWith("backends", want =>
        {
            var declared = want.EnumerateArray().Select(item => item.GetString()!).ToArray();

            // (a) The fixture's vocabulary and the LIBRARY's enum are the same set, in the same
            //     order — a backend added upstream or dropped here is a mismatch either way.
            Assert.Equal(Enum.GetValues<BlobBackendKind>().Select(TokenOf).ToArray(), declared);

            // (b) VOCABULARY COMPLETENESS, and the reason v1 stayed green with `in_process`
            //     uncarried: every declared backend must be the decoded_backend of some ACCEPT
            //     scenario. A binding knowing only {shm, arrow} rejects `in_process` by the letter
            //     of the clause — naming the token, conformingly — and passes every other
            //     assertion here. This is a set difference; a scenario count cannot reach it.
            var uncarried = declared.Where(token => !decodedBackends.Contains(token)).ToArray();
            Assert.True(
                uncarried.Length == 0,
                $"declared backend(s) [{string.Join(", ", uncarried)}] are never the "
                + "decoded_backend of an accept scenario, so nothing here proves this binding "
                + $"implements them; accepted backends were [{string.Join(", ", decodedBackends)}]");
        });

        meta.Verify();
        // Rule 6: every key the nine discharges name was really asserted by this run. A claim
        // verified by nothing is the state the free-text excuses were in.
        prose.VerifyProse(Fixture);

        Assert.Equal(14, replayed);
        Assert.Equal(14, scenarios.Count);
        Assert.Equal(10, accepted);
        Assert.Equal(4, rejected);
        Assert.Equal(2, arrowsDecoded);

        // Four, not ten: arrow and in_process round-trip the field, while omitted, explicit shm
        // and null all re-encode with no `backend` entry at all.
        Assert.Equal(4, fieldsEmitted);
    }

    /// <summary>
    /// A non-string <c>backend</c> is a codec error naming the offending value, not an
    /// <see cref="InvalidOperationException"/> escaping from <c>GetString()</c>.
    /// </summary>
    /// <remarks>
    /// The fixture carries the integer case in both codecs; this widens it to the other JSON
    /// kinds a peer can put in the slot, which the corpus cannot carry without making one frame
    /// invalid in several ways at once. <c>null</c> is deliberately NOT here — it is the ABSENT
    /// form and decodes as <c>shm</c>, which is what the backend_null_* scenarios assert.
    /// </remarks>
    [Theory]
    [InlineData("3", "Number")]
    [InlineData("{\"kind\": \"rdma\"}", "Object")]
    [InlineData("[\"shm\"]", "Array")]
    [InlineData("true", "True")]
    public void Backend_of_a_non_string_kind_is_refused_as_a_codec_error_naming_the_offending_value(
        string literal,
        string kind)
    {
        var frame =
            "{\"Delta\": {\"base_epoch\": 8, \"epoch\": 9, \"ops\": [{\"SlotValue\": {\"node\": 7, "
            + "\"payload\": {\"SharedBlob\": {\"offset\": 40, \"len\": 17, \"generation\": 2, "
            + "\"epoch\": 5, \"checksum\": 987654321, \"backend\": " + literal + "}}}}]}}";

        var failure = Assert.Throws<JsonException>(() => IpcWire.Deserialize(frame));
        Assert.Contains(kind, failure.Message, StringComparison.Ordinal);

        // The msgpack leg needs no separate case here: MsgPackWire transcribes to this same
        // reader, which is what the fixture's backend_non_string_msgpack scenario proves against
        // the integer path.
    }
}
