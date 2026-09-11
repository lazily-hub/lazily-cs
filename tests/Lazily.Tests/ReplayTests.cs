using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The replay-equivalence surface the canonical corpus does not reach.
/// </summary>
/// <remarks>
/// The three fixtures in <c>replay/</c> drive the three obligations; what they never touch is the
/// wire form, the log constructors, and the self-check over a graph that is not a pure function of
/// its log. Untested public surface is surface nothing would notice breaking, so it is pinned here.
/// </remarks>
public sealed class ReplayTests
{
    [Fact]
    public void ARecordedFingerprintSurvivesItsWireForm()
    {
        var log = ReplayLog.FromRecords([("add", 1L), ("add", 2L)]);
        var recorded = new ReplayHarness(() => new Counter()).Record(log);

        var restored = ReplayFingerprint.FromWire(recorded.ToWire());

        Assert.Equal(recorded.Digest, restored.Digest);
        Assert.Equal(recorded.LogDigest, restored.LogDigest);
        Assert.Equal(recorded.Stride, restored.Stride);
        Assert.Equal(3L, (long)restored.Checkpoints.Count);

        // And it is still a usable fingerprint, not just an equal-looking record: this is the
        // point of committing one next to a test.
        new ReplayHarness(() => new Counter()).Verify(log, restored);
    }

    [Fact]
    public void AFingerprintFromAnUnknownSchemaVersionIsRefused()
    {
        var wire = new ReplayHarness(() => new Counter())
            .Record(ReplayLog.FromRecords([("add", 1L)]))
            .ToWire();

        var error = Assert.Throws<ReplayProofException>(
            () => ReplayFingerprint.FromWire(wire with { SchemaVersion = Replay.WireSchemaVersion + 1 }));

        Assert.Contains("schema_version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogRefusesSequenceNumbersThatDoNotStrictlyIncrease()
    {
        // Non-contiguous is FINE — an ack-truncated outbox replays real epochs, and renumbering
        // them would hide a truncated prefix the log digest otherwise catches.
        var sparse = ReplayLog.Of(new ReplayEvent(3, "add", 1L), new ReplayEvent(9, "add", 2L));
        Assert.Equal([3L, 9L], sparse.Select(replayEvent => replayEvent.Seq));

        Assert.Throws<ArgumentException>(() =>
            ReplayLog.Of(new ReplayEvent(2, "add", 1L), new ReplayEvent(2, "add", 2L)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayEvent(-1, "add"));
    }

    [Fact]
    public void ATruncatedPrefixChangesTheLogDigest()
    {
        var outbox = new DurableOutbox<InMemoryOutboxStore>(new InMemoryOutboxStore());
        outbox.Append(1, new OutboxAckMessage(1));
        outbox.Append(2, new OutboxAckMessage(2));
        var full = ReplayLog.FromOutbox(outbox);
        Assert.Equal([1L, 2L], full.Select(replayEvent => replayEvent.Seq));

        outbox.AckThrough(1);
        var truncated = ReplayLog.FromOutbox(outbox);

        Assert.Equal([2L], truncated.Select(replayEvent => replayEvent.Seq));
        Assert.NotEqual(full.Digest, truncated.Digest);
    }

    [Fact]
    public void ProveCatchesAGraphThatIsNotAFunctionOfItsLog()
    {
        // No external fingerprint is needed: two replays of the same log in the same process
        // already disagree when the graph reads something the log does not carry.
        var ticks = 0;
        var harness = new ReplayHarness(() => new Counter(impureTick: () => ++ticks));

        var error = Assert.Throws<ReplayDivergenceException>(
            () => harness.Prove(ReplayLog.FromRecords([("add", 1L)])));

        Assert.Equal(0, error.First.Seq);
        Assert.Equal("total", error.First.Label);
        Assert.Equal(ReplayDivergenceKind.Value, error.First.Kind);
        Assert.Equal("value", error.First.KindName);
    }

    [Fact]
    public void AStrideCoarserThanTheLogStillCheckpointsBothEnds()
    {
        var log = ReplayLog.FromRecords([("add", 1L), ("add", 2L), ("add", 3L)]);

        var sparse = new ReplayHarness(() => new Counter(), stride: 2).Record(log);

        Assert.Equal([Replay.InitialSeq, 1L, 2L], sparse.Checkpoints.Select(point => point.Seq));
        Assert.Throws<ReplayStrideMismatchException>(
            () => new ReplayHarness(() => new Counter()).Verify(log, sparse));
    }

    [Fact]
    public void CanonicalFramingIsTagThenDecimalLengthThenColonThenBody()
    {
        // The layout the pair-based test below is constructed AGAINST. Pinned separately from it
        // so that the collision assertions fail on their own merits rather than behind this one:
        // a length-dropping encoder breaks both, and the pair test is the one whose failure names
        // the property.
        Assert.Equal("s2:bc", Text(ReplayEncoding.Bytes("bc")));
        Assert.Equal("i2:12", Text(ReplayEncoding.Bytes(12L)));
        Assert.Equal("l9:s1:as2:bc", Text(ReplayEncoding.Bytes(new object?[] { "a", "bc" })));
        Assert.Equal(
            "m8:s1:as1:b",
            Text(ReplayEncoding.Bytes(new Dictionary<string, object?> { ["a"] = "b" })));
    }

    [Fact]
    public void MemberLengthIsPinnedByPairsThatCollideWithoutIt()
    {
        // The obligation the shared corpus CANNOT carry (#lzreplayframing, obligation 3 of
        // lazily-spec/docs/replay-equivalence.md). The corpus's reference pair `["a","sbc"]` vs
        // `["as","bc"]` assumes a layout whose member prefix is the bare tag byte `s`. This
        // binding's string tag IS `s`, but its frame is `<tag><decimal length>:<body>` — the
        // separator `:` survives when the length is removed, so under THIS binding's
        // length-dropped bytes the corpus pair still differs:
        //     ["a","sbc"] -> "l:" "s:a"  "s:sbc" = "l:s:as:sbc"
        //     ["as","bc"] -> "l:" "s:as" "s:bc"  = "l:s:ass:bc"
        // The pair that actually collides here has to spell the WHOLE surviving prefix, `s:`, so
        // the member boundary lands one byte further along and the two concatenations coincide:
        //     ["a","s:bc"] -> "l:" "s:a"   "s:s:bc" = "l:s:as:s:bc"
        //     ["as:","bc"] -> "l:" "s:as:" "s:bc"   = "l:s:as:s:bc"
        // Remove the length from `Frame` and these two digests become equal; that mutation is the
        // one this repo previously watched survive the corpus pair and wrongly ruled benign.
        Assert.NotEqual(
            ReplayEncoding.Digest(new object?[] { "a", "s:bc" }),
            ReplayEncoding.Digest(new object?[] { "as:", "bc" }));

        // The mapping analogue: a key is framed apart from its value, so moving the surviving
        // prefix across the key/value boundary must not produce the same entry bytes.
        //     {"a":"s:b"} -> "s:a"   "s:s:b" = "s:as:s:b"
        //     {"as:":"b"} -> "s:as:" "s:b"   = "s:as:s:b"
        Assert.NotEqual(
            ReplayEncoding.Digest(new Dictionary<string, object?> { ["a"] = "s:b" }),
            ReplayEncoding.Digest(new Dictionary<string, object?> { ["as:"] = "b" }));

        // The layout-INDEPENDENT row, kept here as well as in the corpus: a nested container's
        // boundary has no tag to hide behind, so it collides under an unframed concatenation
        // whatever the tags and separators are.
        //     [["a"],"b"] -> "l:" "l:" "s:a"       "s:b" = "l:l:s:as:b"
        //     [["a","b"]] -> "l:" "l:" "s:a" "s:b"       = "l:l:s:as:b"
        Assert.NotEqual(
            ReplayEncoding.Digest(new object?[] { new object?[] { "a" }, "b" }),
            ReplayEncoding.Digest(new object?[] { new object?[] { "a", "b" } }));

        // And the corpus's original equality-class row, which states a real requirement even
        // though it does not pin the length.
        Assert.NotEqual(
            ReplayEncoding.Digest(new object?[] { "a", "bc" }),
            ReplayEncoding.Digest(new object?[] { "ab", "c" }));
    }

    [Fact]
    public void AnObservationWithNoCanonicalEncodingFailsLoudly()
    {
        var harness = new ReplayHarness(() => new Opaque());

        Assert.Throws<ReplayEncodingException>(
            () => harness.Record(ReplayLog.FromRecords([("add", 1L)])));
    }

    private static string Text(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    private sealed class Counter(Func<long>? impureTick = null) : IReplayGraph
    {
        private long _total;

        public void Apply(ReplayEvent replayEvent) =>
            _total += (long)replayEvent.Payload! + (impureTick?.Invoke() ?? 0);

        public IReadOnlyDictionary<string, object?> Observe() =>
            new Dictionary<string, object?> { ["total"] = _total };
    }

    private sealed class Opaque : IReplayGraph
    {
        public void Apply(ReplayEvent replayEvent)
        {
        }

        public IReadOnlyDictionary<string, object?> Observe() =>
            new Dictionary<string, object?> { ["cell"] = new object() };
    }
}
