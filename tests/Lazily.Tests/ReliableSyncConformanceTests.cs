using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class ReliableSyncConformanceTests
{
    private const string LivenessFixture = "reliable-sync/liveness_orset_lww.json";

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

            var expected = FixtureAssertions.Of(
                scenario,
                "expect",
                $"reliable-sync/resync_gap_converge.json scenario {scenario.GetProperty("name").GetString()}");
            expected.AssertKey("final_last_epoch", coordinator.LastEpoch);
            expected.AssertKey("resync_requests_emitted", requests);
            if (expected.TryAssertKeyWith("converged_nodes", graph.AssertNodes))
            {

                // `equals_no_drop_receiver`: recovery is state-equivalent, not lossy. The
                // covering Snapshot IS the sender's whole state at the final epoch, so a
                // receiver that dropped a suffix ends holding what one that dropped nothing
                // holds. The second half is what gives it teeth — the deltas this receiver
                // actually saw do NOT already carry the dropped writes, so the equality is
                // not satisfied trivially.
                var deltasOnly = new GraphProjector();
                var sender = new GraphProjector();
                foreach (var inbound in scenario.GetProperty("inbound").EnumerateArray())
                {
                    if (!inbound.TryGetProperty("frame", out var frameElement)) continue;
                    var replayed = IpcWire.Deserialize(frameElement.GetRawText());
                    if (replayed is SnapshotMessage) sender.Apply(replayed);
                    else deltasOnly.Apply(replayed);
                }

                expected.AssertKey(
                    "equals_no_drop_receiver",
                    graph.State.SequenceEqual(sender.State));
                Assert.NotEqual(sender.State, deltasOnly.State);
            }

            expected.Verify();
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

            var expected = FixtureAssertions.Of(
                scenario,
                "expect",
                $"reliable-sync/idempotent_redelivery.json scenario {scenario.GetProperty("name").GetString()}");
            expected.AssertKey("final_last_epoch", coordinator.LastEpoch);
            expected.AssertKeyWith("state_after", graph.AssertState);
            expected.AssertKey(
                "net_effect_unchanged",
                graph.State.SequenceEqual(GraphProjector.FromState(scenario.GetProperty("state_before")).State));
            expected.Verify();
        }
    }

    [Fact]
    public void Multi_epoch_delta_corpus_advances_atomically_and_matches_unit_fold()
    {
        using var document = SpecCorpus.Load("reliable-sync", "multi_epoch_delta.json");
        var root = document.RootElement;
        var rootAssertions = FixtureAssertions.Of(
            root,
            "assertions",
            "reliable-sync/multi_epoch_delta.json");
        var scenarios = root.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(2, scenarios.Length);

        var spanScenario = scenarios[0];
        var delta = ParseDelta(spanScenario.GetProperty("delta"));
        // The fixture-level assertions describe the span itself, so they are pinned against
        // the decoded delta rather than left as prose.
        // `span` used to be compared against the literal 3 — the fixture asserted against a
        // number typed into the runner, not against the delta it describes.
        rootAssertions.AssertKey("span", (int)(delta.Epoch - delta.BaseEpoch));
        rootAssertions.AssertKey("base_epoch", delta.BaseEpoch);
        rootAssertions.AssertKey("epoch", delta.Epoch);
        rootAssertions.AssertKey("is_multi_epoch", delta.Epoch > delta.BaseEpoch + 1);
        rootAssertions.AssertKey("op_count", delta.Ops.Count);
        rootAssertions.Verify();
        var coordinator = new ResyncCoordinator(
        spanScenario.GetProperty("receiver_last_epoch").GetUInt64());
        var spanExpected = FixtureAssertions.Of(
            spanScenario,
            "expect",
            "reliable-sync/multi_epoch_delta.json scenario span_3_applies_equal_to_unit_fold");
        var startEpoch = coordinator.LastEpoch;
        var decision = coordinator.Ingest(delta);
        spanExpected.AssertKeyWith(
            "action",
            want => Assert.Equal(Enum.Parse<ResyncAction>(want.GetString()!), decision.Action));
        spanExpected.AssertKey("applied", decision.Action == ResyncAction.Apply);
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
        spanExpected.AssertKey("receiver_last_epoch_after", coordinator.LastEpoch);
        // `atomic_advance`: the whole span lands in ONE ingest — the cursor never rests on
        // an intermediate epoch.
        spanExpected.AssertKey(
            "atomic_advance",
            coordinator.LastEpoch - startEpoch == delta.Epoch - delta.BaseEpoch);
        // `fold_equivalent`: the same span folded as unit deltas lands on the same cursor
        // AND the same graph, so a span is an optimization and not a different protocol.
        spanExpected.AssertKey(
            "fold_equivalent",
            coordinator.LastEpoch == unitCoordinator.LastEpoch && unit.State.SequenceEqual(batched.State));
        Assert.Equal(coordinator.LastEpoch, unitCoordinator.LastEpoch);
        Assert.Equal(unit.State, batched.State);
        spanExpected.Verify();

        var gapScenario = scenarios[1];
        var gap = new ResyncCoordinator(gapScenario.GetProperty("receiver_last_epoch").GetUInt64());
        var gapDecision = gap.Ingest(ParseDelta(gapScenario.GetProperty("delta")));
        var gapExpected = FixtureAssertions.Of(
            gapScenario,
            "expect",
            "reliable-sync/multi_epoch_delta.json scenario gap_rule_unchanged_under_span");
        gapExpected.AssertKeyWith(
            "action",
            want => Assert.Equal(Enum.Parse<ResyncAction>(want.GetString()!), gapDecision.Action));
        gapExpected.AssertKey("applied", gapDecision.Action == ResyncAction.Apply);
        gapExpected.AssertKey("request_from", gapDecision.FromEpoch);
        gapExpected.AssertKey("receiver_last_epoch_after", gap.LastEpoch);
        gapExpected.Verify();
    }

    [Fact]
    public void Liveness_corpus_replays_orset_lww_cascade_and_retry_convergence()
    {
        using var document = SpecCorpus.Load("reliable-sync", "liveness_orset_lww.json");
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(4, scenarios.Length);

        var addWins = scenarios[0];
        var addExpect = FixtureAssertions.Of(
            addWins,
            "expect",
            $"{LivenessFixture} scenario {addWins.GetProperty("name").GetString()}");
        var set = new OrSet();
        var operations = addWins.GetProperty("ops").EnumerateArray().ToArray();
        foreach (var operation in operations) ApplyOrSet(set, operation);
        addExpect.AssertKey("present", set.Present);
        var reverse = new OrSet();
        foreach (var operation in operations.Reverse()) ApplyOrSet(reverse, operation);
        // `order_independent`: an OrSet join is commutative, so the reversed transcript
        // must reach the same verdict.
        addExpect.AssertKey("order_independent", set.Present == reverse.Present);
        var redeliveryApplied = operations.Count(operation => ApplyOrSet(set, operation));
        addExpect.AssertKey("redeliver_applied_count", redeliveryApplied);
        // `reason` is the corpus's prose for WHY the set stays present. Holding it to the
        // tag the replay actually computes stops the explanation drifting from its data.
        var addedTags = operations
            .Where(operation => operation.GetProperty("op").GetString() == "add")
            .Select(operation => operation.GetProperty("tag").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var observedTags = operations
            .Where(operation => operation.GetProperty("op").GetString() == "remove")
            .SelectMany(operation => operation.GetProperty("observed_tags").EnumerateArray())
            .Select(tag => tag.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        addedTags.ExceptWith(observedTags);
        Assert.Equal(addedTags.Count > 0, set.Present);
        addExpect.AssertKeyWith(
            "reason",
            want => Assert.All(
                addedTags,
                tag => Assert.Contains(tag, want.GetString()!, StringComparison.Ordinal)));
        addExpect.Verify();

        var lwwScenario = scenarios[1];
        var lwwExpect = FixtureAssertions.Of(
            lwwScenario,
            "expect",
            $"{LivenessFixture} scenario {lwwScenario.GetProperty("name").GetString()}");
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
        lwwExpect.AssertKey("value", register.Value);
        // `resolution` names the conflict rule. Assert the RULE, not the label: the value
        // that survives must be the one carried by the highest-stamped write.
        lwwExpect.AssertKeyWith("resolution", want => Assert.Equal("max_stamp", want.GetString()));
        var winner = writes
            .OrderByDescending(write => ParseStamp(write.GetProperty("stamp")).WallTime)
            .ThenByDescending(write => ParseStamp(write.GetProperty("stamp")).Logical)
            .ThenByDescending(write => ParseStamp(write.GetProperty("stamp")).Peer)
            .First();
        Assert.Equal(winner.GetProperty("value").GetBoolean(), register.Value);
        // `order_independent`: LWW keeps the highest stamp whatever order it arrives in.
        var reversedRegister = new WireLwwRegister<bool>(
            ParseStamp(writes[^1].GetProperty("stamp")),
            writes[^1].GetProperty("value").GetBoolean());
        foreach (var write in writes.Reverse().Skip(1))
        {
            reversedRegister.Set(
            ParseStamp(write.GetProperty("stamp")),
            write.GetProperty("value").GetBoolean());
        }
        lwwExpect.AssertKey("order_independent", reversedRegister.Value == register.Value);
        lwwExpect.Verify();

        var cascade = scenarios[2];
        var cascadeExpect = FixtureAssertions.Of(
            cascade,
            "expect",
            $"{LivenessFixture} scenario {cascade.GetProperty("name").GetString()}");
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
        var liveBefore = registry.LiveDocuments().ToArray();
        cascadeExpect.AssertKeyWith("live_docs_before", want => Assert.Equal(Strings(want), liveBefore));
        ApplyLiveness(registry, cascade.GetProperty("op"));
        var liveAfter = registry.LiveDocuments().ToArray();
        cascadeExpect.AssertKeyWith("live_docs_after", want => Assert.Equal(Strings(want), liveAfter));
        // `cascade`: ONE process death dropped MORE THAN ONE document, and left every other
        // process's documents alone. Either half on its own is satisfied by an unrelated
        // single drop.
        var deadProcess = NormalizeProcess(
            SplitKey(cascade.GetProperty("op").GetProperty("key").GetString()!).Second);
        var survivors = cascade.GetProperty("open_set")
            .EnumerateArray()
            .Select(open => SplitKey(open.GetProperty("key").GetString()!))
            .Where(entry => NormalizeProcess(entry.Second) != deadProcess)
            .Select(entry => entry.First)
            .ToArray();
        cascadeExpect.AssertKey(
            "cascade",
            liveBefore.Except(liveAfter, StringComparer.Ordinal).Count() > 1
                && survivors.All(document => liveAfter.Contains(document, StringComparer.Ordinal)));
        cascadeExpect.Verify();

        var converge = scenarios[3];
        var convergeExpect = FixtureAssertions.Of(
            converge,
            "expect",
            $"{LivenessFixture} scenario {converge.GetProperty("name").GetString()}");
        var forward = new LivenessRegistry();
        var backward = new LivenessRegistry();
        var livenessOps = converge.GetProperty("ops").EnumerateArray().ToArray();
        foreach (var operation in livenessOps) ApplyLiveness(forward, operation);
        foreach (var operation in livenessOps.Reverse()) ApplyLiveness(backward, operation);
        var duplicateChanges = livenessOps.Count(operation => ApplyLiveness(forward, operation));
        convergeExpect.AssertKey("redeliver_applied_count", duplicateChanges);
        convergeExpect.AssertKey(
            "order_independent",
            forward.LiveDocuments().SequenceEqual(backward.LiveDocuments(), StringComparer.Ordinal));
        convergeExpect.AssertKeyWith(
            "converged_live_docs",
            want => Assert.Equal(Strings(want), forward.LiveDocuments()));
        // `per_doc_isolation`: the aggregate is per document, so one document's retry
        // convergence never rewrites another's.
        convergeExpect.AssertKey(
            "per_doc_isolation",
            forward.LiveDocuments().Distinct(StringComparer.Ordinal).Count()
                == forward.LiveDocuments().Count);
        convergeExpect.Verify();
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
        var ackExpect = FixtureAssertions.Of(
            ackScenario,
            "expect",
            $"reliable-sync/coalesce scenario {ackScenario.GetProperty("name").GetString()}");
        ackExpect.AssertKeyWith(
            "retained_epochs_after",
            want => Assert.Equal(Ulongs(want), ackOutbox.RetainedEpochs));
        // `retained_after_ack` is the DEPTH the ack leaves behind — the bound the scenario
        // exists to pin. The epoch list alone does not state that the bound was respected.
        ackExpect.AssertKey("retained_after_ack", ackOutbox.RetainedEpochs.Count);
        // `dequeue_is_fifo_front`: replay hands frames back in ascending epoch order, so the
        // cursor queue drains from the front rather than from wherever the ack landed.
        var replayOrder = ackOutbox.ReplayFrom(0).Select(entry => entry.Epoch).ToArray();
        ackExpect.AssertKey(
            "dequeue_is_fifo_front",
            replayOrder.SequenceEqual(replayOrder.Order()));
        ackExpect.Verify();
    }

    [Fact]
    public void Lease_eviction_corpus_is_peer_isolated_and_rejoins_from_a_fresh_snapshot()
    {
        using var document = SpecCorpus.Load("reliable-sync", "liveness_lease_eviction.json");
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(4, scenarios.Length);

        var slow = scenarios[0];
        var peerB = slow.GetProperty("peers").GetProperty("B");
        var isFullB = peerB.GetProperty("retained").GetInt32() >= 3;
        var rung = PeerEvictionPolicy.Evaluate(
        new PeerHealth(peerB.GetProperty("lease_fresh").GetBoolean(), IsFull: isFullB));
        Assert.Equal(PeerEscalationRung.Backpressure, rung);
        var slowExpect = FixtureAssertions.Of(
            slow,
            "expect",
            $"reliable-sync/liveness_lease_eviction.json scenario {slow.GetProperty("name").GetString()}");
        slowExpect.AssertKey("rung", rung.ToString().ToLowerInvariant());
        slowExpect.AssertKey("B_is_full", isFullB);
        slowExpect.AssertKey("B_evicted", rung == PeerEscalationRung.Evict);
        // `A_stalled_by_B`: escalation is PER PEER. A's rung is computed from A's own
        // health and must be unaffected by B's — that isolation is the whole claim, and
        // asserting it needs A evaluated, not B's rung reinterpreted.
        var peerA = slow.GetProperty("peers").GetProperty("A");
        var rungA = PeerEvictionPolicy.Evaluate(
        new PeerHealth(
        peerA.GetProperty("lease_fresh").GetBoolean(),
        IsFull: peerA.GetProperty("retained").GetInt32() >= 3));
        slowExpect.AssertKey("A_stalled_by_B", rungA != PeerEscalationRung.Healthy);
        slowExpect.Verify();

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
