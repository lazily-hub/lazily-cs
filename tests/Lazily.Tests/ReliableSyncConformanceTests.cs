using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class ReliableSyncConformanceTests
{
    [Fact]
    public void Resync_gap_corpus_requests_once_then_converges_on_snapshot()
    {
        using var document = SpecCorpus.Load("reliable-sync", "resync_gap_converge.json");
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(2, scenarios.Length);

        foreach (var scenario in scenarios)
        {
            var coordinator = new ResyncCoordinator(
            scenario.GetProperty("start_last_epoch").GetUInt64());
            var graph = new GraphProjector();
            var requests = 0;
            foreach (var inbound in scenario.GetProperty("inbound").EnumerateArray())
            {
                if (inbound.TryGetProperty("dropped", out var dropped) && dropped.GetBoolean())
                {
                    continue;
                }

                var message = IpcWire.Deserialize(inbound.GetProperty("frame").GetRawText());
                var decision = coordinator.Ingest(message);
                Assert.Equal(
                Enum.Parse<ResyncAction>(inbound.GetProperty("expect_action").GetString()!),
                decision.Action);
                if (decision.Action == ResyncAction.RequestSnapshot)
                {
                    requests++;
                    Assert.Equal(inbound.GetProperty("request_from").GetUInt64(), decision.FromEpoch);
                }
                else if (decision.Action == ResyncAction.Apply)
                {
                    graph.Apply(message);
                }

                Assert.Equal(inbound.GetProperty("last_epoch_after").GetUInt64(), coordinator.LastEpoch);
            }

            var expected = scenario.GetProperty("expect");
            Assert.Equal(expected.GetProperty("final_last_epoch").GetUInt64(), coordinator.LastEpoch);
            Assert.Equal(expected.GetProperty("resync_requests_emitted").GetInt32(), requests);
            if (expected.TryGetProperty("converged_nodes", out var converged))
            {
                graph.AssertNodes(converged);
            }
        }
    }

    [Fact]
    public void Idempotent_redelivery_corpus_has_exactly_once_effect()
    {
        using var document = SpecCorpus.Load("reliable-sync", "idempotent_redelivery.json");
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(2, scenarios.Length);

        foreach (var scenario in scenarios)
        {
            var coordinator = new ResyncCoordinator(
            scenario.GetProperty("start_last_epoch").GetUInt64());
            var graph = GraphProjector.FromState(scenario.GetProperty("state_before"));
            foreach (var inbound in scenario.GetProperty("inbound").EnumerateArray())
            {
                var message = IpcWire.Deserialize(inbound.GetProperty("frame").GetRawText());
                var decision = coordinator.Ingest(message);
                Assert.Equal(ResyncAction.Ignore, decision.Action);
            }

            var expected = scenario.GetProperty("expect");
            Assert.Equal(expected.GetProperty("final_last_epoch").GetUInt64(), coordinator.LastEpoch);
            graph.AssertState(expected.GetProperty("state_after"));
            Assert.True(expected.GetProperty("net_effect_unchanged").GetBoolean());
        }
    }

    [Fact]
    public void Multi_epoch_delta_corpus_advances_atomically_and_matches_unit_fold()
    {
        using var document = SpecCorpus.Load("reliable-sync", "multi_epoch_delta.json");
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("assertions").GetProperty("span").GetInt32());
        var scenarios = root.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(2, scenarios.Length);

        var spanScenario = scenarios[0];
        var delta = ParseDelta(spanScenario.GetProperty("delta"));
        var coordinator = new ResyncCoordinator(
        spanScenario.GetProperty("receiver_last_epoch").GetUInt64());
        var decision = coordinator.Ingest(delta);
        Assert.Equal(ResyncAction.Apply, decision.Action);
        var batched = new GraphProjector();
        batched.Apply(delta);

        var unit = new GraphProjector();
        var unitCoordinator = new ResyncCoordinator(delta.BaseEpoch);
        foreach (var element in spanScenario.GetProperty("equivalent_unit_fold").EnumerateArray())
        {
            var unitDelta = ParseDelta(element);
            Assert.Equal(ResyncAction.Apply, unitCoordinator.Ingest(unitDelta).Action);
            unit.Apply(unitDelta);
        }

        Assert.Equal(delta.Epoch, coordinator.LastEpoch);
        Assert.Equal(coordinator.LastEpoch, unitCoordinator.LastEpoch);
        Assert.Equal(unit.State, batched.State);

        var gapScenario = scenarios[1];
        var gap = new ResyncCoordinator(gapScenario.GetProperty("receiver_last_epoch").GetUInt64());
        var gapDecision = gap.Ingest(ParseDelta(gapScenario.GetProperty("delta")));
        Assert.Equal(ResyncAction.RequestSnapshot, gapDecision.Action);
        Assert.Equal(gapScenario.GetProperty("expect").GetProperty("request_from").GetUInt64(), gapDecision.FromEpoch);
    }

    [Fact]
    public void Liveness_corpus_replays_orset_lww_cascade_and_retry_convergence()
    {
        using var document = SpecCorpus.Load("reliable-sync", "liveness_orset_lww.json");
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(4, scenarios.Length);

        var addWins = scenarios[0];
        var set = new OrSet();
        var operations = addWins.GetProperty("ops").EnumerateArray().ToArray();
        foreach (var operation in operations) ApplyOrSet(set, operation);
        Assert.Equal(addWins.GetProperty("expect").GetProperty("present").GetBoolean(), set.Present);
        var reverse = new OrSet();
        foreach (var operation in operations.Reverse()) ApplyOrSet(reverse, operation);
        Assert.Equal(set.Present, reverse.Present);
        var redeliveryApplied = operations.Count(operation => ApplyOrSet(set, operation));
        Assert.Equal(
        addWins.GetProperty("expect").GetProperty("redeliver_applied_count").GetInt32(),
        redeliveryApplied);

        var lwwScenario = scenarios[1];
        var writes = lwwScenario.GetProperty("ops").EnumerateArray().ToArray();
        var first = writes[0];
        var register = new WireLwwRegister<bool>(
        ParseStamp(first.GetProperty("stamp")),
        first.GetProperty("value").GetBoolean());
        foreach (var write in writes.Skip(1))
        {
            register.Set(
            ParseStamp(write.GetProperty("stamp")),
            write.GetProperty("value").GetBoolean());
        }
        Assert.Equal(lwwScenario.GetProperty("expect").GetProperty("value").GetBoolean(), register.Value);

        var cascade = scenarios[2];
        var registry = new LivenessRegistry();
        foreach (var open in cascade.GetProperty("open_set").EnumerateArray())
        {
            var (documentName, process) = SplitKey(open.GetProperty("key").GetString()!);
            if (open.GetProperty("present").GetBoolean())
            {
                registry.AddOpen(documentName, process, $"seed:{documentName}:{process}");
            }
        }
        foreach (var alive in cascade.GetProperty("alive_before").EnumerateObject())
        {
            registry.SetAlive(
            NormalizeProcess(alive.Name),
            alive.Value.GetBoolean(),
            new WireStamp(1, 0, 0));
        }
        Assert.Equal(
        Strings(cascade.GetProperty("expect").GetProperty("live_docs_before")),
        registry.LiveDocuments());
        ApplyLiveness(registry, cascade.GetProperty("op"));
        Assert.Equal(
        Strings(cascade.GetProperty("expect").GetProperty("live_docs_after")),
        registry.LiveDocuments());

        var converge = scenarios[3];
        var forward = new LivenessRegistry();
        var backward = new LivenessRegistry();
        var livenessOps = converge.GetProperty("ops").EnumerateArray().ToArray();
        foreach (var operation in livenessOps) ApplyLiveness(forward, operation);
        foreach (var operation in livenessOps.Reverse()) ApplyLiveness(backward, operation);
        var duplicateChanges = livenessOps.Count(operation => ApplyLiveness(forward, operation));
        Assert.Equal(
        converge.GetProperty("expect").GetProperty("redeliver_applied_count").GetInt32(),
        duplicateChanges);
        Assert.Equal(forward.LiveDocuments(), backward.LiveDocuments());
        Assert.Equal(
        Strings(converge.GetProperty("expect").GetProperty("converged_live_docs")),
        forward.LiveDocuments());
    }

    [Fact]
    public void Coalesce_corpus_bounds_state_and_fuses_queue_batches_without_loss()
    {
        using var document = SpecCorpus.Load("reliable-sync", "coalesce_bounds_outbox.json");
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(3, scenarios.Length);

        var stateScenario = scenarios[0];
        var stateOutbox = new DurableOutbox<InMemoryOutboxStore>(new InMemoryOutboxStore());
        var fullRun = new GraphProjector();
        foreach (var element in stateScenario.GetProperty("appended").EnumerateArray())
        {
            var delta = ParseDelta(element);
            stateOutbox.Append(delta.Epoch, delta);
            fullRun.Apply(delta);
        }
        Assert.Equal(stateScenario.GetProperty("retained_before").GetInt32(), stateOutbox.RetainedDepth);
        var snapshot = ParseCoalescedSnapshot(
        stateScenario.GetProperty("coalesce").GetProperty("wire").GetProperty("Snapshot"));
        Assert.True(stateOutbox.CoalesceToSnapshot(snapshot.Epoch, snapshot));
        Assert.Equal(stateScenario.GetProperty("retained_after").GetInt32(), stateOutbox.RetainedDepth);
        var retainedSnapshot = Assert.IsType<SnapshotMessage>(
        Assert.Single(stateOutbox.ReplayFrom(0)).Message);
        var coalescedRun = new GraphProjector();
        coalescedRun.Apply(retainedSnapshot);
        Assert.Equal(fullRun.State, coalescedRun.State);

        var queueScenario = scenarios[1];
        var queueOutbox = new DurableOutbox<InMemoryOutboxStore>(new InMemoryOutboxStore());
        foreach (var element in queueScenario.GetProperty("appended").EnumerateArray())
        {
            var delta = ParseDelta(element);
            queueOutbox.Append(delta.Epoch, delta);
        }
        Assert.False(
        queueOutbox.CoalesceToSnapshot(
        3,
        new SnapshotMessage(2, [], [], [])));
        Assert.True(queueOutbox.FuseQueueDeltaBatch());
        var fused = Assert.IsType<DeltaMessage>(Assert.Single(queueOutbox.ReplayFrom(0)).Message);
        Assert.Equal(0UL, fused.BaseEpoch);
        Assert.Equal(3UL, fused.Epoch);
        Assert.Equal(
        new byte[] { 97, 98, 99 },
        fused.Ops.Select(
        operation =>
        Assert.Single(
        Assert.IsType<IpcValue.Inline>(
        Assert.IsType<DeltaOp.QueuePush>(operation).Payload).Bytes)));

        var ackScenario = scenarios[2];
        var ackOutbox = new DurableOutbox<InMemoryOutboxStore>(new InMemoryOutboxStore());
        foreach (var epoch in ackScenario.GetProperty("appended_epochs").EnumerateArray())
        {
            var value = epoch.GetUInt64();
            ackOutbox.Append(value, new DeltaMessage(value - 1, value, []));
        }
        ackOutbox.AckThrough(
        ackScenario.GetProperty("outbox_ack").GetProperty("through_epoch").GetUInt64());
        Assert.Equal(
        Ulongs(ackScenario.GetProperty("expect").GetProperty("retained_epochs_after")),
        ackOutbox.RetainedEpochs);
    }

    [Fact]
    public void Lease_eviction_corpus_is_peer_isolated_and_rejoins_from_a_fresh_snapshot()
    {
        using var document = SpecCorpus.Load("reliable-sync", "liveness_lease_eviction.json");
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(4, scenarios.Length);

        var slow = scenarios[0];
        var peerB = slow.GetProperty("peers").GetProperty("B");
        var rung = PeerEvictionPolicy.Evaluate(
        new PeerHealth(
        peerB.GetProperty("lease_fresh").GetBoolean(),
        IsFull: peerB.GetProperty("retained").GetInt32() >= 3));
        Assert.Equal(PeerEscalationRung.Backpressure, rung);
        Assert.False(slow.GetProperty("expect").GetProperty("B_evicted").GetBoolean());
        Assert.False(slow.GetProperty("expect").GetProperty("A_stalled_by_B").GetBoolean());

        var expired = scenarios[1];
        var expiredPeer = expired.GetProperty("peers").GetProperty("B");
        Assert.Equal(
        PeerEscalationRung.Evict,
        PeerEvictionPolicy.Evaluate(
        new PeerHealth(expiredPeer.GetProperty("lease_fresh").GetBoolean(), IsFull: true)));
        var reclaimed = new DurableOutbox<InMemoryOutboxStore>(new InMemoryOutboxStore());
        for (ulong epoch = 1; epoch <= 3; epoch++)
        {
            reclaimed.Append(epoch, new DeltaMessage(epoch - 1, epoch, []));
        }
        reclaimed.ReclaimUnacked();
        Assert.Equal(0, reclaimed.RetainedDepth);

        var rejoin = scenarios[2];
        var fresh = new ResyncCoordinator(rejoin.GetProperty("returning_peer_last_epoch").GetUInt64());
        var senderEpoch = rejoin.GetProperty("sender_final_epoch").GetUInt64();
        Assert.Equal(
        ResyncAction.Apply,
        fresh.Ingest(new SnapshotMessage(senderEpoch, [], [], [])).Action);
        Assert.Equal(senderEpoch, fresh.LastEpoch);

        var queue = scenarios[3];
        var quorum = queue.GetProperty("queue");
        Assert.True(
        PeerEvictionPolicy.DistributedQueueAllowsWrite(
        quorum.GetProperty("majority_has_quorum").GetBoolean()));
        Assert.False(
        PeerEvictionPolicy.DistributedQueueAllowsWrite(
        quorum.GetProperty("minority_has_quorum").GetBoolean()));
    }

    [Fact]
    public void Reliable_control_frames_round_trip_strictly_and_validate_against_schema()
    {
        using var gap = SpecCorpus.Load("reliable-sync", "resync_gap_converge.json");
        using var replay = SpecCorpus.Load("reliable-sync", "idempotent_redelivery.json");
        var wires = new[]
        {
gap.RootElement.GetProperty("wire").GetRawText(),
replay.RootElement.GetProperty("wire").GetRawText(),
};
        var schema = JsonSchema.FromText(
        File.ReadAllText(Path.Combine(SchemaRoot(), "reliable-sync.json")));
        foreach (var wire in wires)
        {
            var message = IpcWire.Deserialize(wire);
            var encoded = IpcWire.Serialize(message);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(wire), JsonNode.Parse(encoded)));
            using var actual = JsonDocument.Parse(encoded);
            Assert.True(schema.Evaluate(actual.RootElement).IsValid);
        }

        Assert.Throws<JsonException>(
        () => IpcWire.Deserialize(
        "{\"OutboxAck\":{\"through_epoch\":1,\"unexpected\":true}}"));
        Assert.Throws<JsonException>(
        () => IpcWire.Deserialize("{\"ResyncRequest\":{}}"));
    }

    private static bool ApplyOrSet(OrSet set, JsonElement operation) =>
    operation.GetProperty("op").GetString() switch
    {
        "add" => set.Add(operation.GetProperty("tag").GetString()!),
        "remove" => set.RemoveObserved(
    operation.GetProperty("observed_tags")
    .EnumerateArray()
    .Select(tag => tag.GetString()!)),
        var kind => throw new InvalidOperationException($"Unknown OR-set op {kind}."),
    };

    private static bool ApplyLiveness(LivenessRegistry registry, JsonElement operation)
    {
        var kind = operation.GetProperty("register_kind").GetString();
        var (first, second) = SplitKey(operation.GetProperty("key").GetString()!);
        return kind switch
        {
            "orset" when operation.GetProperty("op").GetString() == "add" =>
            registry.AddOpen(first, second, operation.GetProperty("tag").GetString()!),
            "orset" =>
            registry.RemoveOpen(
            first,
            second,
            operation.GetProperty("observed_tags")
            .EnumerateArray()
            .Select(tag => tag.GetString()!)),
            "lww" =>
            registry.SetAlive(
            second,
            operation.GetProperty("value").GetBoolean(),
            ParseStamp(operation.GetProperty("stamp"))),
            _ => throw new InvalidOperationException($"Unknown liveness register {kind}."),
        };
    }

    private static WireStamp ParseStamp(JsonElement stamp) =>
    new(
    stamp.GetProperty("wall_time").GetUInt64(),
    stamp.GetProperty("logical").GetUInt64(),
    stamp.GetProperty("peer").GetUInt64());

    private static DeltaMessage ParseDelta(JsonElement body)
    {
        var envelope = new JsonObject
        {
            ["Delta"] = JsonNode.Parse(body.GetRawText()),
        };
        return Assert.IsType<DeltaMessage>(IpcWire.Deserialize(envelope.ToJsonString()));
    }

    private static SnapshotMessage ParseCoalescedSnapshot(JsonElement body)
    {
        var nodes = body.GetProperty("nodes").EnumerateArray()
        .Select(
        node =>
        new NodeSnapshot(
        node.GetProperty("node").GetUInt64(),
        "bytes",
        new NodeState.Payload(
        node.GetProperty("payload")
        .GetProperty("Inline")
        .EnumerateArray()
        .Select(value => value.GetByte())
        .ToArray())))
        .ToArray();
        var edges = body.GetProperty("edges").EnumerateArray()
        .Select(
        edge =>
        new EdgeSnapshot(
        edge.GetProperty("dependent").GetUInt64(),
        edge.GetProperty("dependency").GetUInt64()))
        .ToArray();
        return new SnapshotMessage(body.GetProperty("epoch").GetUInt64(), nodes, edges, []);
    }

    private static (string First, string Second) SplitKey(string key)
    {
        var separator = key.IndexOf('/');
        return (key[..separator], key[(separator + 1)..]);
    }

    private static string NormalizeProcess(string value) =>
    value.StartsWith("pid", StringComparison.Ordinal) ? value : $"pid{value}";

    private static string[] Strings(JsonElement values) =>
    values.EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static ulong[] Ulongs(JsonElement values) =>
    values.EnumerateArray().Select(value => value.GetUInt64()).ToArray();

    private static string SchemaRoot() =>
    Path.GetFullPath(Path.Combine(SpecCorpus.Root!, "..", "schemas"));

    private sealed class GraphProjector
    {
        private readonly SortedDictionary<ulong, byte[]> _state = [];

        public IReadOnlyList<string> State =>
        _state.Select(entry => $"{entry.Key}:{Convert.ToHexString(entry.Value)}").ToArray();

        public static GraphProjector FromState(JsonElement state)
        {
            var graph = new GraphProjector();
            foreach (var property in state.EnumerateObject())
            {
                graph._state[ulong.Parse(property.Name, System.Globalization.CultureInfo.InvariantCulture)] =
                property.Value.EnumerateArray().Select(value => value.GetByte()).ToArray();
            }
            return graph;
        }

        public void Apply(IpcMessage message)
        {
            switch (message)
            {
                case SnapshotMessage snapshot:
                    _state.Clear();
                    foreach (var node in snapshot.Nodes)
                    {
                        if (node.State is NodeState.Payload payload) _state[node.Node] = [.. payload.Bytes];
                    }
                    break;
                case DeltaMessage delta:
                    foreach (var operation in delta.Ops)
                    {
                        switch (operation)
                        {
                            case DeltaOp.CellSet set when set.Payload is IpcValue.Inline inline:
                                _state[set.Node] = [.. inline.Bytes];
                                break;
                            case DeltaOp.SlotValue set when set.Payload is IpcValue.Inline inline:
                                _state[set.Node] = [.. inline.Bytes];
                                break;
                            case DeltaOp.NodeAdd add when add.State is NodeState.Payload payload:
                                _state[add.Node] = [.. payload.Bytes];
                                break;
                            case DeltaOp.NodeRemove remove:
                                _state.Remove(remove.Node);
                                break;
                        }
                    }
                    break;
            }
        }

        public void AssertNodes(JsonElement expected) => AssertState(expected);

        public void AssertState(JsonElement expected)
        {
            Assert.Equal(expected.EnumerateObject().Count(), _state.Count);
            foreach (var property in expected.EnumerateObject())
            {
                var node = ulong.Parse(
                property.Name,
                System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(
                property.Value.EnumerateArray().Select(value => value.GetByte()).ToArray(),
                _state[node]);
            }
        }
    }
}
