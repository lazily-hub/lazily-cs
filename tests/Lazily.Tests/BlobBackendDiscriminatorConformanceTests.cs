using System.Globalization;
using System.Text.Json;
using Lazily;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Blob-backend discriminator strictness on decode (<c>#lzblobbackendstrict</c>).
/// </summary>
/// <remarks>
/// <para>
/// protocol.md § Shared-memory payload path makes <c>ShmBlobRef.backend</c> optional with a
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
/// lazily-cs was already strict — <see cref="IpcWire"/>'s blob reader has a real string→enum
/// dispatch with a throwing default that interpolates the token, and its encoder already omitted
/// <c>backend</c> for <c>shm</c>. An earlier audit of this repo reported NO backend dispatch site
/// at all, which was wrong in the direction that matters: "no dispatch site" and "a dispatch site
/// that silently binds the enum's zero value" look identical from the outside, and only a decode
/// tells them apart. This runner is what holds the behaviour, and it pins BOTH halves — a decoder
/// that reads the discriminator correctly and re-emits <c>backend: "shm"</c> has a correct decoded
/// value and a non-conforming encoder.
/// </para>
/// <para>
/// The one thing that did NOT hold was the failure mode for a non-string <c>backend</c>: it
/// escaped as <see cref="InvalidOperationException"/> out of <c>GetString()</c>, outside the
/// JsonException contract both MUST-level codecs declare. The non-string theory below pins the
/// correction; the canonical fixture carries no non-string scenario, so nothing upstream would
/// have caught it.
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
    /// an enum, so the reject frames are schema-INVALID by design and cannot survive a round trip
    /// through structured JSON. Raw text and raw hex are what the fixture carries, and the ABSENT
    /// map entry the omitted scenarios test only exists on the wire.
    /// </remarks>
    private static IpcMessage Decode(string codec, JsonElement scenario) => codec switch
    {
        "json" => IpcWire.Deserialize(scenario.GetProperty("wire_json").GetString()!),
        "msgpack" => MsgPackWire.Deserialize(HexToBytes(scenario.GetProperty("wire_msgpack_hex").GetString()!)),
        _ => throw new InvalidOperationException($"unknown codec in fixture: {codec}"),
    };

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

    private static (ulong Node, ShmBlobRef Blob, ulong Epoch) Descriptor(IpcMessage message)
    {
        var delta = Assert.IsType<DeltaMessage>(message);
        var op = Assert.IsType<DeltaOp.SlotValue>(Assert.Single(delta.Ops));
        var payload = Assert.IsType<IpcValue.SharedBlob>(op.Payload);
        return (op.Node, payload.Blob, delta.Epoch);
    }

    [Fact]
    public void Blob_backend_omitted_defaults_to_shm_and_an_unknown_token_is_refused_by_name()
    {
        if (SpecCorpus.Root is null) return;

        using var document = SpecCorpus.Load(Corpus, Fixture);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal("BlobBackendDiscriminator", root.GetProperty("kind").GetString());

        var meta = FixtureAssertions.Of(root, "assertions", $"{Corpus}/{Fixture} assertions");
        meta.AssertKey("required_of_binding", "MUST");
        meta.AssertKey("scenario_count", root.GetProperty("scenarios").GetArrayLength());
        meta.AssertKey("codecs", new[] { "json", "msgpack" }.AsEnumerable());
        meta.AssertKey("outcomes", new[] { "accept", "reject" }.AsEnumerable());

        // Against the LIBRARY's enum, not a literal: the fixture's backend set and
        // BlobBackendKind must be the same set, in the same order, or one of them has drifted.
        meta.AssertKey(
            "backends",
            Enum.GetValues<BlobBackendKind>().Select(TokenOf).AsEnumerable());

        foreach (var prose in new[]
                 {
                     "clause", "wire_encoding", "reject_obligation", "anti_vacuity", "theorem",
                     "generator",
                 })
        {
            meta.ExcuseKey(
                prose,
                "prose: it states WHY the fixture is shaped this way; the behaviour it describes " +
                "is asserted by the per-scenario decode, refusal, and re-encode below");
        }

        meta.Verify();

        var scenarios = SpecCorpus.Scenarios(root, Corpus, Fixture);

        // Anti-vacuity, in every direction the fixture's own `anti_vacuity` note names.
        // `accepted`/`rejected` prove both outcomes were exercised; `arrowsDecoded` proves the
        // field was really READ (a decoder hardcoding shm passes four of six accept assertions
        // and dies here); `fieldsEmitted` proves the encoder half is asymmetric rather than
        // echoing whatever arrived.
        var replayed = 0;
        var accepted = 0;
        var rejected = 0;
        var arrowsDecoded = 0;
        var fieldsEmitted = 0;

        // Every scenario is replayed even when an earlier one fails, and all of the failures are
        // reported together. A `foreach` that lets the first Assert unwind reports ONE scenario
        // and silently leaves the remaining seven unrun — which reads as "one thing is broken"
        // when the truth may be "the discriminator is not implemented at all", and makes the
        // fixture's json/msgpack pairing unobservable: the msgpack twin of a broken json
        // scenario never executes, so nothing here would notice if only the json leg worked.
        var failures = new List<string>();

        foreach (var scenario in scenarios.All())
        {
            var where = scenario.GetProperty("id").GetString()!;
            var codec = scenario.GetProperty("codec").GetString();
            if (codec is not ("json" or "msgpack"))
                throw new InvalidOperationException($"{where}: unknown codec in fixture: {codec}");

            // Fail closed on both discriminators (#lzscenariobodyskip). An unrecognised outcome
            // or backend form must throw, never fall through into a green no-op.
            var outcome = scenario.GetProperty("outcome").GetString();
            var form = scenario.GetProperty("backend_form").GetString();
            if (form is not ("omitted" or "shm" or "arrow" or "rdma"))
                throw new InvalidOperationException($"{where}: unknown backend form: {form}");
            Assert.Equal("Delta", scenario.GetProperty("variant").GetString());

            var expect = FixtureAssertions.Of(scenario, "expect", $"{Corpus}/{Fixture} {where}");
            replayed += 1;

            try
            {
                switch (outcome)
                {
                    case "accept":
                        {
                            accepted += 1;
                            var message = Decode(codec, scenario);
                            var (node, blob, epoch) = Descriptor(message);

                            // Through the library's OWN resolution function — the one every backend
                            // routes on — so an omitted discriminator is asserted where it is
                            // actually consumed, not where the runner would like it to be.
                            var token = TokenOf(blob.EffectiveBackend());
                            if (blob.EffectiveBackend() == BlobBackendKind.Arrow) arrowsDecoded += 1;
                            expect.AssertKey("decoded_backend", token);

                            expect.AssertKey("node", node);
                            expect.AssertKey("offset", blob.Offset);
                            expect.AssertKey("len", blob.Length);
                            expect.AssertKey("generation", blob.Generation);
                            expect.AssertKey("epoch", blob.Epoch);
                            expect.AssertKey("checksum", blob.Checksum);
                            Assert.Equal(blob.Epoch, epoch);

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
                            var caught = Record.Exception(() => Decode(codec, scenario));
                            expect.AssertKey("rejected", caught is not null);

                            // JsonException specifically: both MUST-level codecs declare that failure
                            // mode so a caller can handle them through one catch, and the msgpack leg
                            // reaches this dispatch through the same reader.
                            var failure = Assert.IsType<JsonException>(caught);

                            // The assertion that separates "refused" from "refused for the stated
                            // reason". A decoder that mis-parses `checksum` and throws satisfies a
                            // bare is-error check while implementing none of the clause.
                            expect.AssertKeyWith(
                                "error_names_token",
                                want => Assert.Contains(want.GetString()!, failure.Message, StringComparison.Ordinal));
                            break;
                        }

                    default:
                        throw new InvalidOperationException($"{where}: unknown outcome: {outcome}");
                }

                expect.Verify();
            }
            catch (Exception failure) when (failure is not InvalidOperationException)
            {
                // A fail-closed dispatch fault (unknown outcome, codec, or backend form) is a
                // fault in the RUNNER's agreement with the corpus, not a finding about the
                // library, so it is never collected — it unwinds immediately.
                failures.Add($"{where}: {failure.Message}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{Corpus}/{Fixture}: {failures.Count} of {scenarios.Count} scenarios failed:\n\n"
            + string.Join("\n\n", failures));

        Assert.Equal(8, replayed);
        Assert.Equal(8, scenarios.Count);
        Assert.Equal(6, accepted);
        Assert.Equal(2, rejected);
        Assert.Equal(2, arrowsDecoded);
        Assert.Equal(2, fieldsEmitted);
    }

    /// <summary>
    /// A non-string <c>backend</c> is a codec error naming the offending value, not an
    /// <see cref="InvalidOperationException"/> escaping from <c>GetString()</c>.
    /// </summary>
    /// <remarks>
    /// The canonical fixture carries no non-string scenario — it cannot, since the reject frames
    /// already sit outside <c>schemas/defs.json</c> and a number there would be a second kind of
    /// invalidity in one frame. This is the local half of the clause: a caller that catches
    /// <see cref="JsonException"/>, exactly as both codecs document, must not be walked past by a
    /// number, a null, or an object in the discriminator position.
    /// </remarks>
    [Theory]
    [InlineData("3", "Number")]
    [InlineData("null", "Null")]
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
            + "\"epoch\": 9, \"checksum\": 987654321, \"backend\": " + literal + "}}}}]}}";

        var failure = Assert.Throws<JsonException>(() => IpcWire.Deserialize(frame));
        Assert.Contains(kind, failure.Message, StringComparison.Ordinal);

        // The msgpack leg needs no separate case here: MsgPackWire transcribes to this same
        // reader, which is what the fixture's `backend_unknown_msgpack` scenario proves against
        // the string path.
    }
}
