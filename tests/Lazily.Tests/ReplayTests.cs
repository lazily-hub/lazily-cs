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
    public void AnObservationWithNoCanonicalEncodingFailsLoudly()
    {
        var harness = new ReplayHarness(() => new Opaque());

        Assert.Throws<ReplayEncodingException>(
            () => harness.Record(ReplayLog.FromRecords([("add", 1L)])));
    }

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
