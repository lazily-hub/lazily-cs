using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lazily.Tests;

public sealed class PlaneBoundaryTests
{
    [Fact]
    public void SessionHandshakeRoundTripsAndCommandPlaneFailsClosed()
    {
        var local = SessionHandshake.Create(
            1,
            "session",
            [SessionHandshake.CommandPlaneV1, "shared-blob"]);
        var remote = SessionHandshake.Create(
            2,
            "session",
            [SessionHandshake.CommandPlaneV1]);
        var decoded = SessionHandshake.Deserialize(local.Serialize());
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(local.Serialize()),
                JsonNode.Parse(decoded.Serialize())));
        Assert.True(
            local.CheckCompatible(remote, SessionHandshake.CommandPlaneV1).IsCompatible);

        var unsupported = SessionHandshake.Create(3, "session");
        var failure = local.CheckCompatible(unsupported, SessionHandshake.CommandPlaneV1);
        Assert.False(failure.IsCompatible);
        Assert.Equal("features", failure.Field);
        Assert.Throws<InvalidOperationException>(
            () => new NegotiatedSession(
                local,
                unsupported,
                SessionHandshake.CommandPlaneV1));

        const string extra =
            """{"protocol_id":"lazily-ipc","protocol_major_version":1,"codec":"json","max_frame_size":1,"fragmentation_supported":false,"ordered_reliable":true,"peer_id":1,"session_id":"s","features":[],"extra":true}""";
        Assert.Throws<JsonException>(() => SessionHandshake.Deserialize(extra));
    }

    [Fact]
    public async Task RpcCallCompletesOnlyAfterTerminalReceipt()
    {
        var session = new NegotiatedSession(
            SessionHandshake.Create(1, "s", [SessionHandshake.CommandPlaneV1]),
            SessionHandshake.Create(2, "s", [SessionHandshake.CommandPlaneV1]),
            SessionHandshake.CommandPlaneV1);
        var transport = new RecordingTransport();
        var client = new CommandRpcClient(transport, session);
        var submit = Submit();

        var call = client.CallAsync(submit);
        Assert.False(call.IsCompleted);
        Assert.Single(transport.Messages);
        client.Ingest(
            new CommandMessage.Events(
                new CommandEvents(
                    [new CommandEvent("event", "command", CommandEventKind.Accepted, 4, null)])));
        Assert.False(call.IsCompleted);

        client.Ingest(CausalReceipt.Applied("receipt", "command", "controller", 4));
        var terminal = await call;
        Assert.Equal(CommandStatus.Applied, terminal.Status);
        Assert.True(terminal.Terminal);
    }

    [Fact]
    public void PeerPermissionsGateKindsIndependentlyAndOmitUnreadableState()
    {
        var permissions = new PeerPermissions();
        Assert.False(permissions.IsAllowed(7, RemoteOp.Read(1)));
        permissions.Allow(7, RemoteOp.Read(1));
        permissions.Allow(7, RemoteOp.Write(2));
        Assert.True(permissions.IsAllowed(7, RemoteOp.Read(1)));
        Assert.False(permissions.IsAllowed(7, RemoteOp.Write(1)));
        Assert.False(permissions.IsAllowed(7, RemoteOp.Read(2)));
        Assert.Throws<PermissionDeniedException>(
            () => permissions.Check(7, RemoteOp.TriggerEffect(1)));

        var snapshot = new SnapshotMessage(
            1,
            [
                new NodeSnapshot(1, "u8", new NodeState.Payload([1])),
                new NodeSnapshot(2, "u8", new NodeState.Payload([2])),
            ],
            [new EdgeSnapshot(2, 1)],
            [1, 2]);
        var filtered = permissions.FilterReadable(7, snapshot);
        Assert.Equal(1UL, Assert.Single(filtered.Nodes).Node);
        Assert.Empty(filtered.Edges);
        Assert.Equal([1UL], filtered.Roots);

        var delta = new DeltaMessage(
            1,
            2,
            [
                new DeltaOp.CellSet(1, new IpcValue.Inline([3])),
                new DeltaOp.CellSet(2, new IpcValue.Inline([4])),
                new DeltaOp.EdgeAdd(2, 1),
            ]);
        var filteredDelta = permissions.FilterReadable(7, delta);
        Assert.IsType<DeltaOp.CellSet>(Assert.Single(filteredDelta.Ops));
    }

    [Fact]
    public void FfiBoundaryClassifiesAllKindsAndCanonicalizesChannelFrames()
    {
        var messages = new (IpcMessage Message, LazilyFfiMessageKind Kind)[]
        {
            (
                new SnapshotMessage(
                    1,
                    [new NodeSnapshot(1, "u8", new NodeState.Payload([1]))],
                    [],
                    [1]),
                LazilyFfiMessageKind.Snapshot),
            (new DeltaMessage(1, 2, []), LazilyFfiMessageKind.Delta),
            (new CrdtSyncMessage([]), LazilyFfiMessageKind.CrdtSync),
            (new ResyncRequestMessage(2), LazilyFfiMessageKind.ResyncRequest),
            (new OutboxAckMessage(2), LazilyFfiMessageKind.OutboxAck),
        };

        foreach (var (message, expected) in messages)
        {
            var bytes = Encoding.UTF8.GetBytes(IpcWire.Serialize(message));
            Assert.Equal(LazilyFfiStatus.Ok, LazilyFfi.ValidateJson(bytes));
            Assert.Equal(expected, LazilyFfi.ClassifyJson(bytes).Kind);
        }

        Assert.Equal(
            LazilyFfiStatus.InvalidMessage,
            LazilyFfi.ValidateJson(Encoding.UTF8.GetBytes("""{"Nope":{}}""")));

        var channel = new LazilyFfiChannel();
        Assert.Equal(LazilyFfiStatus.Empty, channel.Receive(out _));
        var padded = Encoding.UTF8.GetBytes(
            "  " + IpcWire.Serialize(messages[0].Message) + Environment.NewLine);
        Assert.Equal(LazilyFfiStatus.Ok, channel.SendJson(padded));
        Assert.Equal(LazilyFfiStatus.Ok, channel.Receive(out var received));
        Assert.NotNull(received);
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(IpcWire.Serialize(messages[0].Message)),
                JsonNode.Parse(Encoding.UTF8.GetString(received!.Bytes))));
    }

    [Fact]
    public void InstrumentationCountsProductionPathsAndBenchmarkHarnessRuns()
    {
        var before = LazilyMetrics.Snapshot();
        var projection = new StateProjection();
        projection.ApplySnapshot(
            new SnapshotMessage(
                1,
                [new NodeSnapshot(1, "u8", new NodeState.Payload([1]))],
                [],
                [1]));
        var after = LazilyMetrics.Snapshot();
        Assert.True(
            after.StateProjectionFramesApplied > before.StateProjectionFramesApplied);

        var results = LazilyBenchmark.RunSuite(iterations: 2);
        Assert.Equal(3, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(2, result.Iterations);
            Assert.True(result.ElapsedTicks >= 0);
            Assert.True(result.OperationsPerSecond > 0);
        });
    }

    private static CommandSubmit Submit() =>
        new(
            "command",
            "command",
            "client",
            "controller",
            "tests",
            "run",
            4,
            "key",
            1_000,
            new CommandPolicy(
                DedupePolicy.SameIdempotencyKey,
                Supersede: false,
                CancelOnPreempt: true),
            "tests.run.v1",
            "sha256:test",
            new IpcValue.Inline([]),
            ["causal-receipts"]);

    private sealed class RecordingTransport : ICommandTransport
    {
        public List<CommandMessage> Messages { get; } = [];

        public void Send(CommandMessage message) => Messages.Add(message);
    }
}
