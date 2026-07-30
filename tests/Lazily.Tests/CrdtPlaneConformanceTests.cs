using System.Text.Json;
using System.Text.Json.Nodes;
using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class CrdtPlaneConformanceTests
{
    [Fact]
    public void Canonical_anti_entropy_corpus_converges_and_redelivery_is_idempotent()
    {
        using var document = SpecCorpus.Load("distributed", "anti_entropy_converge.json");
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(3, scenarios.Length);
        var assertions = 0;

        foreach (var scenario in scenarios)
        {
            var operations = scenario.GetProperty("ops").EnumerateArray().Select(ParseOperation).ToArray();
            var runtime = new CrdtPlaneRuntime(peer: 99);
            var applied = runtime.Ingest(new CrdtSyncMessage(operations));
            var expected = FixtureAssertions.Of(
                scenario,
                "expect",
                $"distributed/anti_entropy_converge.json scenario {scenario.GetProperty("name").GetString()}");
            Assert.Equal(expected.GetProperty("applied_count").GetInt32(), applied);
            assertions++;

            AssertConverged(expected.GetProperty("converged"), runtime.Converged());
            assertions++;

            if (scenario.TryGetProperty("redeliver", out var redeliver)
            && redeliver.GetBoolean())
            {
                Assert.Equal(
                expected.GetProperty("redeliver_applied_count").GetInt32(),
                runtime.Ingest(new CrdtSyncMessage(operations)));
                assertions++;
            }

            var declaresOrderIndependent = expected.TryGetProperty("order_independent", out var independent);
            if (declaresOrderIndependent
            || (scenario.TryGetProperty("reverse_order_equivalent", out var reverse) && reverse.GetBoolean()))
            {
                var reversed = new CrdtPlaneRuntime(peer: 100);
                Assert.Equal(
                operations.Length,
                reversed.Ingest(new CrdtSyncMessage(operations.Reverse().ToArray())));
                AssertConverged(expected.GetProperty("converged"), reversed.Converged());
                if (declaresOrderIndependent)
                {
                    Assert.Equal(
                        independent.GetBoolean(),
                        Equivalent(runtime.Converged(), reversed.Converged()));
                }

                AssertEquivalent(runtime.Converged(), reversed.Converged());
                assertions += 3;
            }

            // `resolution` names the conflict rule the plane applies. Assert the RULE, not
            // the label: every converged entry must carry the state of the highest-stamped
            // op for its node. Comparing the label alone would pass against a plane that
            // resolved by arrival order and happened to agree.
            Assert.Equal("max_stamp", expected.GetProperty("resolution").GetString());
            foreach (var entry in runtime.Converged())
            {
                var rivals = operations.Where(operation => operation.Node == entry.Node).ToArray();
                Assert.NotEmpty(rivals);
                var winner = rivals
                    .OrderByDescending(operation => operation.Stamp.WallTime)
                    .ThenByDescending(operation => operation.Stamp.Logical)
                    .ThenByDescending(operation => operation.Stamp.Peer)
                    .First();
                Assert.Equal(
                    Assert.IsType<IpcValue.Inline>(winner.State).Bytes,
                    Assert.IsType<IpcValue.Inline>(entry.State).Bytes);
                assertions++;
            }

            expected.Verify();
        }

        Assert.True(assertions >= 8);
    }

    [Fact]
    public void Runtime_bridges_local_and_remote_ops_through_registered_reactive_cells()
    {
        var leftContext = new Context();
        var rightContext = new Context();
        var leftCell = Cell(leftContext);
        var rightCell = Cell(rightContext);
        var left = new CrdtPlaneRuntime(peer: 1);
        var right = new CrdtPlaneRuntime(peer: 2);
        left.Register(7, "counter/global", leftCell, Encode, Decode);
        right.Register(70, "counter/global", rightCell, Encode, Decode);

        var computes = 0;
        var doubled = rightContext.Computed(
        compute =>
        {
            computes++;
            return rightCell.Handle.Get(compute) * 2;
        });
        Assert.Equal(0, doubled.Get());

        var operation = left.LocalUpdate<LwwRegister<int>, int>(
        7,
        nowMicros: 10,
        (register, stamp) => register.Set(21, stamp));
        Assert.NotNull(operation);
        Assert.Equal("counter/global", operation.Key);

        var frame = left.SyncFrame();
        Assert.Equal(1, right.Ingest(frame, nowMicros: 11));
        Assert.Equal(42, doubled.Get());
        Assert.Equal(2, computes);
        Assert.Equal(0, right.Ingest(frame, nowMicros: 12));
        Assert.Equal(42, doubled.Get());
        Assert.Equal(2, computes);
    }

    [Fact]
    public void Frontier_reply_ships_only_missing_ops_and_withholds_stability_until_all_members_seen()
    {
        var context = new Context();
        var cell = Cell(context);
        var sender = new CrdtPlaneRuntime(peer: 1);
        sender.AddPeer(2);
        sender.Register(1, null, cell, Encode, Decode);

        var first = sender.LocalUpdate<LwwRegister<int>, int>(
        1,
        10,
        (register, stamp) => register.Set(1, stamp));
        Assert.NotNull(first);
        Assert.Null(sender.StabilityFrontier);

        var remote = new CrdtOp(
        2,
        null,
        new WireStamp(11, 0, 2),
        new IpcValue.Inline(Encode(new LwwRegister<int>(2, new HlcStamp(11, 0, 2)))));
        Assert.Equal(1, sender.Ingest(new CrdtSyncMessage([remote]), nowMicros: 11));
        Assert.NotNull(sender.StabilityFrontier);

        var receiver = new CrdtPlaneRuntime(peer: 3);
        Assert.Equal(2, receiver.Ingest(sender.SyncFrame()));
        var request = new CrdtSyncMessage([], receiver.Frontier.ToEntries());
        Assert.Empty(sender.SyncReply(request).Ops);

        var second = sender.LocalUpdate<LwwRegister<int>, int>(
        1,
        12,
        (register, stamp) => register.Set(3, stamp));
        Assert.NotNull(second);
        var reply = sender.SyncReply(request);
        Assert.Single(reply.Ops);
        Assert.Equal(second.Stamp, reply.Ops[0].Stamp);
    }

    private static ReplicatedCell<LwwRegister<int>, int> Cell(Context context) =>
    new(context, new LwwRegister<int>(0, new HlcStamp(0, 0, 0)));

    private static byte[] Encode(LwwRegister<int> register) =>
    JsonSerializer.SerializeToUtf8Bytes(
    new RegisterState(
    register.Value,
    register.Stamp.Micros,
    register.Stamp.Counter,
    register.Stamp.Peer));

    private static LwwRegister<int> Decode(ReadOnlyMemory<byte> bytes)
    {
        var state = JsonSerializer.Deserialize<RegisterState>(bytes.Span);
        Assert.NotNull(state);
        return new LwwRegister<int>(
        state.Value,
        new HlcStamp(state.Micros, state.Counter, state.Peer));
    }

    private static CrdtOp ParseOperation(JsonElement operation)
    {
        var envelope = new JsonObject
        {
            ["CrdtSync"] = new JsonObject
            {
                ["frontier"] = new JsonArray(),
                ["ops"] = new JsonArray(JsonNode.Parse(operation.GetRawText())),
            },
        };
        var frame = Assert.IsType<CrdtSyncMessage>(IpcWire.Deserialize(envelope.ToJsonString()));
        return Assert.Single(frame.Ops);
    }

    private static void AssertConverged(
    JsonElement expected,
    IReadOnlyList<ConvergedCrdtEntry> actual)
    {
        var entries = expected.EnumerateArray().ToArray();
        Assert.Equal(entries.Length, actual.Count);
        foreach (var entry in entries)
        {
            var node = entry.GetProperty("node").GetUInt64();
            var key = entry.TryGetProperty("key", out var keyElement)
            ? keyElement.GetString()
            : null;
            var winner = Assert.Single(actual, item => item.Node == node && item.Key == key);
            var expectedState = entry.GetProperty("state")
            .GetProperty("Inline")
            .EnumerateArray()
            .Select(value => value.GetByte())
            .ToArray();
            var inline = Assert.IsType<IpcValue.Inline>(winner.State);
            Assert.Equal(expectedState, inline.Bytes);
        }
    }

    /// <summary>Whether two converged views agree entry for entry.</summary>
    /// <remarks>
    /// The boolean twin of <see cref="AssertEquivalent"/>, so a fixture that declares
    /// <c>order_independent: false</c> is checked in that direction too rather than being
    /// read and discarded.
    /// </remarks>
    private static bool Equivalent(
    IReadOnlyList<ConvergedCrdtEntry> left,
    IReadOnlyList<ConvergedCrdtEntry> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].Node != right[index].Node) return false;
            if (left[index].Key != right[index].Key) return false;
            if (!Equals(left[index].Stamp, right[index].Stamp)) return false;
            if (left[index].State is not IpcValue.Inline leftInline
                || right[index].State is not IpcValue.Inline rightInline
                || !leftInline.Bytes.SequenceEqual(rightInline.Bytes))
            {
                return false;
            }
        }

        return true;
    }

    private static void AssertEquivalent(
    IReadOnlyList<ConvergedCrdtEntry> left,
    IReadOnlyList<ConvergedCrdtEntry> right)
    {
        Assert.Equal(left.Count, right.Count);
        for (var index = 0; index < left.Count; index++)
        {
            Assert.Equal(left[index].Node, right[index].Node);
            Assert.Equal(left[index].Key, right[index].Key);
            Assert.Equal(left[index].Stamp, right[index].Stamp);
            Assert.Equal(
            Assert.IsType<IpcValue.Inline>(left[index].State).Bytes,
            Assert.IsType<IpcValue.Inline>(right[index].State).Bytes);
        }
    }

    private sealed record RegisterState(int Value, long Micros, long Counter, int Peer);
}
