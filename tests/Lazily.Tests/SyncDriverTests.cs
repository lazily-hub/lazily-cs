using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class SyncDriverTests
{
    [Fact]
    public void Failed_send_is_retained_then_replayed_and_pruned_after_ack()
    {
        var sink = new ScriptedSink();
        sink.Results.Enqueue(false);
        var source = new ScriptedSource();
        var clock = new ManualClock { NowMilliseconds = 10 };
        var outbox = new DurableOutbox<InMemoryOutboxStore>(new InMemoryOutboxStore());
        var driver = new SyncDriver(sink, source, outbox, clock, new SnapshotProvider());
        var delta = Frame(1);

        driver.Enqueue(1, delta);
        var failed = driver.Tick();
        Assert.Equal(0, failed.Sent);
        Assert.True(driver.IsStalled);
        Assert.Equal(1, failed.Retained);
        Assert.Equal([1UL], outbox.RetainedEpochs);

        clock.NowMilliseconds = 25;
        Assert.Equal(15UL, driver.StalledFor(clock.NowMilliseconds));
        driver.OnReconnect();
        var replayed = driver.Tick();
        Assert.Equal(1, replayed.Sent);
        Assert.False(driver.IsStalled);
        Assert.Contains(sink.Sent, message => message is DeltaMessage { Epoch: 1 });
        Assert.Contains(sink.Sent, message => message is OutboxAckMessage { ThroughEpoch: 0 });

        source.Messages.Enqueue(new OutboxAckMessage(1));
        var acknowledged = driver.Tick();
        Assert.Equal(1UL, acknowledged.PeerAckedThrough);
        Assert.Equal(0, acknowledged.Retained);
        Assert.Empty(outbox.RetainedEpochs);
    }

    [Fact]
    public void Gap_emits_request_once_snapshot_applies_and_peer_request_is_served()
    {
        var sink = new ScriptedSink();
        var source = new ScriptedSource();
        var provider = new SnapshotProvider(epoch: 8);
        var driver = new SyncDriver(
        sink,
        source,
        new DurableOutbox<InMemoryOutboxStore>(new InMemoryOutboxStore()),
        new ManualClock(),
        provider,
        lastEpoch: 2);

        source.Messages.Enqueue(new DeltaMessage(3, 4, []));
        source.Messages.Enqueue(new DeltaMessage(4, 5, []));
        var gap = driver.Tick();
        Assert.True(gap.ResyncRequested);
        Assert.Empty(gap.Applied);
        Assert.Single(sink.Sent, message => message is ResyncRequestMessage { FromEpoch: 2 });

        source.Messages.Enqueue(new SnapshotMessage(6, [], [], []));
        var resynced = driver.Tick();
        Assert.Single(resynced.Applied);
        Assert.Equal(6UL, driver.LastEpoch);
        Assert.Contains(sink.Sent, message => message is OutboxAckMessage { ThroughEpoch: 6 });

        source.Messages.Enqueue(new ResyncRequestMessage(7));
        var served = driver.Tick();
        Assert.Equal(1, served.SnapshotsServed);
        Assert.Equal([7UL], provider.Requests);
        Assert.Contains(sink.Sent, message => message is SnapshotMessage { Epoch: 8 });
    }

    [Fact]
    public void Tick_is_bounded_and_source_failures_require_reconnect()
    {
        var source = new ScriptedSource();
        source.Messages.Enqueue(new CrdtSyncMessage([]));
        source.Messages.Enqueue(new CrdtSyncMessage([]));
        var driver = new SyncDriver(
        new ScriptedSink(),
        source,
        new DurableOutbox<InMemoryOutboxStore>(new InMemoryOutboxStore()),
        new ManualClock(),
        new SnapshotProvider(),
        maxFramesPerPhase: 1);

        Assert.Single(driver.Tick().Applied);
        Assert.Single(driver.Tick().Applied);

        source.Failure = new IOException("carrier closed");
        var error = Assert.Throws<SyncDriverSourceException>(() => driver.Tick());
        Assert.IsType<IOException>(error.InnerException);
    }

    [Fact]
    public void Invalid_snapshot_provider_fails_closed()
    {
        var source = new ScriptedSource();
        source.Messages.Enqueue(new ResyncRequestMessage(9));
        var driver = new SyncDriver(
        new ScriptedSink(),
        source,
        new DurableOutbox<InMemoryOutboxStore>(new InMemoryOutboxStore()),
        new ManualClock(),
        new SnapshotProvider(epoch: 8));

        Assert.Throws<InvalidOperationException>(() => driver.Tick());
    }

    [Fact]
    public void File_outbox_coalesce_and_reclaim_survive_restart()
    {
        var directory = Path.Combine(
        Path.GetTempPath(),
        "lazily-cs-sync-tests",
        Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "outbox.jsonl");
        var outbox = new DurableOutbox<FileOutboxStore>(new FileOutboxStore(path));
        outbox.Append(1, Frame(1));
        outbox.Append(2, Frame(2));
        var snapshot = new SnapshotMessage(2, [], [], []);
        Assert.True(outbox.CoalesceToSnapshot(2, snapshot));

        var restarted = new DurableOutbox<FileOutboxStore>(new FileOutboxStore(path));
        var entry = Assert.Single(restarted.ReplayFrom(0));
        Assert.Equal(2UL, entry.Epoch);
        Assert.IsType<SnapshotMessage>(entry.Message);
        restarted.ReclaimUnacked();
        Assert.Empty(
        new DurableOutbox<FileOutboxStore>(new FileOutboxStore(path)).RetainedEpochs);
    }

    private static DeltaMessage Frame(ulong epoch) =>
    new(epoch - 1, epoch, [new DeltaOp.CellSet(1, new IpcValue.Inline([(byte)epoch]))]);

    private sealed class ScriptedSink : IpcSink
    {
        public Queue<bool> Results { get; } = [];

        public List<IpcMessage> Sent { get; } = [];

        public bool Send(IpcMessage message)
        {
            var result = Results.Count == 0 || Results.Dequeue();
            if (result) Sent.Add(message);
            return result;
        }
    }

    private sealed class ScriptedSource : IpcSource
    {
        public Queue<IpcMessage> Messages { get; } = [];

        public Exception? Failure { get; set; }

        public IpcMessage? Receive()
        {
            if (Failure is not null)
            {
                var failure = Failure;
                Failure = null;
                throw failure;
            }

            return Messages.Count == 0 ? null : Messages.Dequeue();
        }
    }

    private sealed class ManualClock : ISyncClock
    {
        public ulong NowMilliseconds { get; set; }
    }

    private sealed class SnapshotProvider(ulong epoch = 1) : ISnapshotProvider
    {
        public List<ulong> Requests { get; } = [];

        public SnapshotMessage Snapshot(ulong fromEpoch)
        {
            Requests.Add(fromEpoch);
            return new SnapshotMessage(epoch, [], [], []);
        }
    }
}
