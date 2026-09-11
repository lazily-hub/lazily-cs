using System.Globalization;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace Lazily.Tests;

/// <summary>
/// Replays the canonical replay-equivalence corpus against <see cref="ReplayHarness"/>.
/// </summary>
/// <remarks>
/// Three fixtures, one obligation each (<c>lazily-spec/docs/replay-equivalence.md</c>): the
/// fingerprint is bound to its log and that binding is revalidated BEFORE any value compare; a
/// divergence is reported at the first checkpoint where the values parted; the observation
/// encoding agrees with the family on which differences are differences.
///
/// The corpus declares its subjects in prose because a JSON fixture cannot carry a reactive
/// graph, so <see cref="Accumulator"/> below is this binding's copy of that declaration — kept to
/// the letter, including that it observes <c>sum</c> and <c>names</c> under exactly those labels.
/// </remarks>
public sealed class ReplayConformanceTests
{
    private const string Corpus = "replay";

    [Fact]
    public void CanonicalFingerprintIsBoundToItsLog() =>
        DriveHarnessFixture("fingerprint_log_binding.json", expectedSteps: 8);

    [Fact]
    public void CanonicalDivergenceIsLocalizedToItsFirstCheckpoint() =>
        DriveHarnessFixture("divergence_localization.json", expectedSteps: 7);

    [Fact]
    public void CanonicalEncodingAgreesOnTheEqualityClasses()
    {
        const string Fixture = "canonical_encoding_equality.json";
        using var document = SpecCorpus.Load(Corpus, Fixture);
        var root = document.RootElement;
        Assert.Equal("Replay", root.GetProperty("kind").GetString());
        Assert.Equal("CanonicalEncoding", root.GetProperty("model").GetString());

        var values = root.GetProperty("config").GetProperty("values");
        var steps = root.GetProperty("steps");
        Assert.Equal(11, steps.GetArrayLength());

        // Both outcomes must really occur. A runner that only ever observed `false` would satisfy
        // every inequality claim in this fixture with a thoroughly broken encoding.
        var outcomes = new HashSet<bool>();
        var replayed = 0;

        foreach (var step in steps.EnumerateArray())
        {
            var op = step.GetProperty("op");
            var type = op.GetProperty("type").GetString();
            var where = $"{Corpus}/{Fixture} step {replayed} ({type})";
            var expected = FixtureAssertions.Wrap(step.GetProperty("expected"), where);

            switch (type)
            {
                case "digest_equal":
                    var equal = string.Equals(
                        ReplayEncoding.Digest(Value(values.GetProperty(op.GetProperty("left").GetString()!))),
                        ReplayEncoding.Digest(Value(values.GetProperty(op.GetProperty("right").GetString()!))),
                        StringComparison.Ordinal);
                    Assert.Equal(step.GetProperty("returns").GetBoolean(), equal);
                    outcomes.Add(equal);
                    break;

                case "digest_defined":
                    var defined = true;
                    try
                    {
                        ReplayEncoding.Digest(Value(values.GetProperty(op.GetProperty("value").GetString()!)));
                    }
                    catch (ReplayEncodingException)
                    {
                        // The whole obligation: a value the encoding does not define fails LOUDLY
                        // instead of degrading to the host's default rendering, which embeds an
                        // identity and would report a false divergence on every run.
                        defined = false;
                    }

                    Assert.Equal(step.GetProperty("returns").GetBoolean(), defined);
                    expected.AssertKey("outcome", "encoding_error");
                    break;

                default:
                    throw new XunitException($"{where}: unknown canonical encoding operation");
            }

            expected.Verify();
            replayed++;
        }

        Assert.Equal(steps.GetArrayLength(), replayed);
        Assert.Equal([false, true], outcomes.Order());
    }

    private static void DriveHarnessFixture(string fixture, int expectedSteps)
    {
        using var document = SpecCorpus.Load(Corpus, fixture);
        var root = document.RootElement;
        Assert.Equal("Replay", root.GetProperty("kind").GetString());
        Assert.Equal("ReplayHarness", root.GetProperty("model").GetString());

        var config = root.GetProperty("config");
        var logs = config.GetProperty("logs")
            .EnumerateObject()
            .ToDictionary(entry => entry.Name, entry => Log(entry.Value), StringComparer.Ordinal);
        var fingerprints = new Dictionary<string, ReplayFingerprint>(StringComparer.Ordinal);

        var steps = root.GetProperty("steps");
        Assert.Equal(expectedSteps, steps.GetArrayLength());
        var replayed = 0;

        foreach (var step in steps.EnumerateArray())
        {
            var op = step.GetProperty("op");
            var type = op.GetProperty("type").GetString();
            var where = $"{Corpus}/{fixture} step {replayed} ({type})";
            var expected = FixtureAssertions.Wrap(step.GetProperty("expected"), where);

            if (type == "log_digest_equal")
            {
                // Two logs that settle to the same final sum with DIFFERENT digests is the case a
                // value-only comparison would wrongly accept, so the fixture pins the inequality
                // of the digests themselves before it ever asks the harness anything.
                var equal = string.Equals(
                    logs[op.GetProperty("left").GetString()!].Digest,
                    logs[op.GetProperty("right").GetString()!].Digest,
                    StringComparison.Ordinal);
                Assert.Equal(step.GetProperty("returns").GetBoolean(), equal);
                expected.Verify();
                replayed++;
                continue;
            }

            var build = Subject(config, op);
            var stride = Stride(config, op);
            var harness = new ReplayHarness(build, stride);
            var log = logs[op.GetProperty("log").GetString()!];

            switch (type)
            {
                case "record":
                    var recorded = harness.Record(log);
                    fingerprints[op.GetProperty("into").GetString()!] = recorded;
                    expected.AssertKey("outcome", "recorded");
                    expected.AssertKey(
                        "checkpoint_seqs",
                        recorded.Checkpoints.Select(checkpoint => checkpoint.Seq));
                    expected.AssertKey("stride", recorded.Stride);
                    var finalSum = FinalSum(build, log);
                    expected.AssertKey("final_sum", finalSum);

                    // The one place the digest and the declared state meet. Without it the fixture
                    // would accept a harness that observed some OTHER value entirely: the recorded
                    // checkpoint digests are opaque, so `final_sum` alone only pins the subject
                    // this test drives on the side, never what the fingerprint captured.
                    Assert.Equal(ReplayEncoding.Digest(finalSum), recorded.Final.Cells["sum"]);
                    break;

                case "prove":
                    var proven = harness.Prove(log, op.GetProperty("replays").GetInt32());
                    expected.AssertKey("outcome", "ok");

                    // Counted from a real comparison rather than written as 0: `Prove` raises on a
                    // divergence, so a hard-coded zero here would be the runner asserting its own
                    // control flow.
                    expected.AssertKey("divergences", harness.Check(log, proven).Count);
                    break;

                case "verify":
                    VerifyStep(harness, log, fingerprints[op.GetProperty("fingerprint").GetString()!], expected);
                    break;

                case "check":
                    CheckStep(harness, log, fingerprints[op.GetProperty("fingerprint").GetString()!], expected);
                    break;

                default:
                    throw new XunitException($"{where}: unknown canonical replay operation");
            }

            expected.Verify();
            replayed++;
        }

        Assert.Equal(steps.GetArrayLength(), replayed);
    }

    private static void VerifyStep(
        ReplayHarness harness,
        ReplayLog log,
        ReplayFingerprint fingerprint,
        FixtureAssertions expected)
    {
        var outcome = "ok";
        var divergences = 0;
        ReplayDivergence? first = null;
        try
        {
            harness.Verify(log, fingerprint);
        }
        catch (ReplayLogMismatchException)
        {
            // Routed on the TYPE, never on a message: a stale fingerprint is a refusal, and a
            // driver that told it apart from a divergence by wording would be broken by a
            // copy-edit.
            outcome = "log_mismatch";
        }
        catch (ReplayStrideMismatchException)
        {
            outcome = "stride_mismatch";
        }
        catch (ReplayDivergenceException error)
        {
            outcome = "divergent";
            first = error.First;
            divergences = error.Divergences.Count;
        }

        expected.AssertKey("outcome", outcome);
        if (first is null)
        {
            expected.AssertKey("divergences", divergences);
            return;
        }

        expected.AssertKey("first_divergent_seq", first.Seq);
        expected.AssertKey("first_divergent_label", first.Label);
        expected.AssertKey("first_divergent_kind", first.KindName);
    }

    private static void CheckStep(
        ReplayHarness harness,
        ReplayLog log,
        ReplayFingerprint fingerprint,
        FixtureAssertions expected)
    {
        try
        {
            var divergences = harness.Check(log, fingerprint).Count;
            expected.AssertKey("outcome", "ok");
            expected.AssertKey("divergences", divergences);
        }
        catch (ReplayLogMismatchException)
        {
            // The non-raising reporting form still refuses a stale fingerprint. It collects value
            // divergences; an unanswerable question is not a report.
            expected.AssertKey("outcome", "log_mismatch");
            expected.AssertKey("divergences", 0);
        }
    }

    private static Func<IReplayGraph> Subject(JsonElement config, JsonElement op)
    {
        var subject = config.GetProperty("subject").GetString();
        switch (subject)
        {
            case "accumulator":
                return () => new Accumulator();
            case "drifting_accumulator":
                var driftAt = config.GetProperty("drift_at").GetInt64();
                var drift = op.TryGetProperty("drift", out var value) ? value.GetInt64() : 0;
                return () => new Accumulator(driftAt, drift);
            default:
                throw new XunitException($"unknown canonical replay subject '{subject}'");
        }
    }

    private static int Stride(JsonElement config, JsonElement op)
    {
        if (op.TryGetProperty("stride", out var perStep)) return perStep.GetInt32();
        return config.TryGetProperty("stride", out var declared) ? declared.GetInt32() : 1;
    }

    private static ReplayLog Log(JsonElement events) =>
        new(events.EnumerateArray().Select(entry => new ReplayEvent(
            entry.GetProperty("seq").GetInt64(),
            entry.GetProperty("name").GetString()!,
            entry.GetProperty("payload").GetInt64())));

    private static long FinalSum(Func<IReplayGraph> build, ReplayLog log)
    {
        var subject = build();
        foreach (var replayEvent in log) subject.Apply(replayEvent);
        return (long)subject.Observe()["sum"]!;
    }

    private static object? Value(JsonElement tagged)
    {
        var tag = tagged.GetProperty("t").GetString();
        return tag switch
        {
            // Integers arrive as decimal STRINGS so a value beyond 2^53 survives JSON exactly.
            "int" => long.Parse(tagged.GetProperty("v").GetString()!, CultureInfo.InvariantCulture),
            "str" => tagged.GetProperty("v").GetString(),
            "float" => double.Parse(tagged.GetProperty("v").GetString()!, CultureInfo.InvariantCulture),
            "bool" => tagged.GetProperty("v").GetBoolean(),
            "bytes" => Bytes(tagged.GetProperty("v").GetString()!),
            "seq" => tagged.GetProperty("v").EnumerateArray().Select(Value).ToArray(),
            "set" => new HashSet<object?>(tagged.GetProperty("v").EnumerateArray().Select(Value)),
            "map" => tagged.GetProperty("v")
                .EnumerateArray()
                .ToDictionary(
                    entry => entry[0].GetString()!,
                    entry => Value(entry[1]),
                    StringComparer.Ordinal),
            "opaque" => new Opaque(),
            _ => throw new XunitException($"unknown canonical value tag '{tag}'"),
        };
    }

    private static byte[] Bytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = byte.Parse(
                hex.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    /// <summary>The corpus's `accumulator`, and `drifting_accumulator` when a drift is configured.</summary>
    private sealed class Accumulator : IReplayGraph
    {
        private readonly long? _driftAt;
        private readonly long _drift;
        private readonly List<string> _names = [];
        private long _sum;

        internal Accumulator()
        {
        }

        internal Accumulator(long driftAt, long drift)
        {
            _driftAt = driftAt;
            _drift = drift;
        }

        public void Apply(ReplayEvent replayEvent)
        {
            _sum += (long)replayEvent.Payload!;
            _names.Add(replayEvent.Name);

            // The one thing a replay proof is looking for: a value taken from OUTSIDE the log.
            // `drift = 0` is the honest run.
            if (_driftAt == replayEvent.Seq) _sum += _drift;
        }

        public IReadOnlyDictionary<string, object?> Observe() => new Dictionary<string, object?>
        {
            ["sum"] = _sum,
            ["names"] = _names.ToArray(),
        };
    }

    /// <summary>A value the encoding does not define, as the corpus's `opaque` tag.</summary>
    private sealed class Opaque;
}
