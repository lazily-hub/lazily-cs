using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lazily.Tests;

public sealed class StateProjectionConformanceTests
{
    [Fact]
    public void ReplaysAgentDocSnapshotAndDeltaThroughTheProductionMirror()
    {
        using var snapshotFixture = SpecCorpus.Load(
            "agent-doc",
            "snapshot_agent_doc_state.json");
        using var deltaFixture = SpecCorpus.Load("agent-doc", "delta_agent_doc_state.json");
        var snapshotWire = snapshotFixture.RootElement.GetProperty("wire");
        var deltaWire = deltaFixture.RootElement.GetProperty("wire");
        var snapshot = Assert.IsType<SnapshotMessage>(
            IpcWire.Deserialize(snapshotWire.GetRawText()));
        var delta = Assert.IsType<DeltaMessage>(IpcWire.Deserialize(deltaWire.GetRawText()));

        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(snapshotWire.GetRawText()),
                JsonNode.Parse(IpcWire.Serialize(snapshot))));
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(deltaWire.GetRawText()),
                JsonNode.Parse(IpcWire.Serialize(delta))));

        var projection = new StateProjection();
        Assert.IsType<StateProjectionApplyStatus.Applied>(projection.ApplySnapshot(snapshot));
        Assert.Equal(3UL, projection.LastEpoch);
        Assert.Equal(3, projection.Nodes.Count);
        Assert.Equal(2, projection.Edges.Count);
        Assert.Single(projection.Roots);
        AssertPayloadField(projection, 102, "phase", "preflight_started");
        AssertPayloadField(projection, 103, "phase", "selected");

        Assert.IsType<StateProjectionApplyStatus.Applied>(projection.ApplyDelta(delta));
        Assert.Equal(6UL, projection.LastEpoch);
        Assert.Equal(4, projection.Nodes.Count);
        Assert.Equal(3, projection.Edges.Count);
        AssertPayloadField(projection, 102, "phase", "committed");
        AssertPayloadField(projection, 103, "phase", "completed");
        Assert.True(projection.TryGetNode(104, out var transport));
        Assert.Equal("agent_doc.transport.patch", transport.TypeTag);
        Assert.Contains(
            new EdgeSnapshot(104, 102),
            projection.Edges);

        var materialized = projection.ToSnapshot();
        Assert.Equal(6UL, materialized.Epoch);
        Assert.Equal(4, materialized.Nodes.Count);

        // Both fixtures' `assertions` blocks (#lznullformblind). Every claim above is
        // asserted against a HARDCODED literal, so the corpus's own values never reached a
        // comparison and neither block was ever bound to a tracker — editing the fixture
        // changed nothing here. Binding them makes the fixture the source of the expected
        // values and puts both blocks inside the ledger rungs.
        // A second projection carrying the snapshot ALONE: the one above has had the delta
        // applied, so `cycle_phase` (the snapshot's claim) and `cycle_phase_after` (the
        // delta's) would both be read off post-delta state and the first would be checked
        // against the wrong thing.
        var snapshotOnly = new StateProjection();
        Assert.IsType<StateProjectionApplyStatus.Applied>(snapshotOnly.ApplySnapshot(snapshot));

        var snapshotAssertions = FixtureAssertions.Of(
            snapshotFixture.RootElement,
            "assertions",
            "agent-doc/snapshot_agent_doc_state.json");
        snapshotAssertions.AssertKey("epoch", snapshot.Epoch);
        snapshotAssertions.AssertKey("node_count", snapshot.Nodes.Count);
        snapshotAssertions.AssertKey("edge_count", snapshot.Edges.Count);
        snapshotAssertions.AssertKey("root_count", snapshot.Roots.Count);
        snapshotAssertions.AssertKey("type_tags", snapshot.Nodes.Select(node => node.TypeTag));
        snapshotAssertions.AssertKeyWith(
            "all_type_tags_in_vocabulary",
            want => Assert.Equal(
                want.GetBoolean(),
                snapshot.Nodes.All(node => node.TypeTag.StartsWith("agent_doc.", StringComparison.Ordinal))));
        snapshotAssertions.AssertKeyWith(
            "cycle_phase",
            want => AssertPayloadField(snapshotOnly, 102, "phase", want.GetString()!));
        snapshotAssertions.AssertKeyWith(
            "queue_head_phase",
            want => AssertPayloadField(snapshotOnly, 103, "phase", want.GetString()!));
        snapshotAssertions.Verify();

        var deltaAssertions = FixtureAssertions.Of(
            deltaFixture.RootElement,
            "assertions",
            "agent-doc/delta_agent_doc_state.json");
        deltaAssertions.AssertKey("base_epoch", delta.BaseEpoch);
        deltaAssertions.AssertKey("epoch", delta.Epoch);
        deltaAssertions.AssertKey("op_count", delta.Ops.Count);
        deltaAssertions.AssertKey(
            "added_type_tags",
            delta.Ops.OfType<DeltaOp.NodeAdd>().Select(op => op.TypeTag));
        deltaAssertions.AssertKeyWith(
            "all_type_tags_in_vocabulary",
            want => Assert.Equal(
                want.GetBoolean(),
                delta.Ops.OfType<DeltaOp.NodeAdd>()
                    .All(op => op.TypeTag.StartsWith("agent_doc.", StringComparison.Ordinal))));
        deltaAssertions.AssertKeyWith(
            "cycle_phase_after",
            want => AssertPayloadField(projection, 102, "phase", want.GetString()!));
        deltaAssertions.AssertKeyWith(
            "queue_head_phase_after",
            want => AssertPayloadField(projection, 103, "phase", want.GetString()!));
        deltaAssertions.Verify();
    }

    [Fact]
    public void DeltaGapAndInvalidBatchFailClosedWithoutPartialMutation()
    {
        var projection = new StateProjection();
        projection.ApplySnapshot(
            new SnapshotMessage(
                3,
                [new NodeSnapshot(1, "u8", new NodeState.Payload([1]))],
                [],
                [1]));

        Assert.IsType<StateProjectionApplyStatus.Gap>(
            projection.ApplyDelta(
                new DeltaMessage(
                    4,
                    5,
                    [new DeltaOp.CellSet(1, new IpcValue.Inline([2]))])));
        Assert.Equal(3UL, projection.LastEpoch);
        AssertPayload(projection, 1, [1]);

        var invalid = new DeltaMessage(
            3,
            4,
            [
                new DeltaOp.CellSet(1, new IpcValue.Inline([2])),
                new DeltaOp.EdgeAdd(1, 99),
            ]);
        Assert.IsType<StateProjectionApplyStatus.Invalid>(projection.ApplyDelta(invalid));
        Assert.Equal(3UL, projection.LastEpoch);
        AssertPayload(projection, 1, [1]);

        var wrongProjection = new DeltaMessage(
            3,
            4,
            [new DeltaOp.QueuePush(1, new IpcValue.Inline([2]))]);
        var unsupported =
            Assert.IsType<StateProjectionApplyStatus.Invalid>(
                projection.ApplyDelta(wrongProjection));
        Assert.Contains("queue projection adapter", unsupported.Reason);
        Assert.Equal(3UL, projection.LastEpoch);
        AssertPayload(projection, 1, [1]);
    }

    [Fact]
    public void ProducerMirrorSortsAndCoalescesOneEpochPerNonemptyFlush()
    {
        var mirror = new StateProjectionMirror();
        mirror.MarkDirty(9);
        mirror.MarkDirty(2);
        mirror.Resolve(9, new IpcValue.Inline([90]));
        var first = Assert.IsType<DeltaMessage>(mirror.Flush());
        Assert.Equal(0UL, first.BaseEpoch);
        Assert.Equal(1UL, first.Epoch);
        Assert.Collection(
            first.Ops,
            operation => Assert.Equal(2UL, Assert.IsType<DeltaOp.Invalidate>(operation).Node),
            operation => Assert.Equal(9UL, Assert.IsType<DeltaOp.SlotValue>(operation).Node));

        Assert.Null(mirror.Flush());
        Assert.Equal(1UL, mirror.BaseEpoch);
    }

    private static void AssertPayloadField(
        StateProjection projection,
        ulong node,
        string field,
        string expected)
    {
        Assert.True(projection.TryGetNode(node, out var projected));
        var payload = Assert.IsType<NodeState.Payload>(projected.State);
        using var json = JsonDocument.Parse(payload.Bytes);
        Assert.Equal(expected, json.RootElement.GetProperty(field).GetString());
    }

    private static void AssertPayload(
        StateProjection projection,
        ulong node,
        byte[] expected)
    {
        Assert.True(projection.TryGetNode(node, out var projected));
        Assert.Equal(expected, Assert.IsType<NodeState.Payload>(projected.State).Bytes);
    }
}
