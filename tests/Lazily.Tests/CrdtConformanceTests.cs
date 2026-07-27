using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Strict replay of the canonical TextCrdt, SeqCrdt, and CrdtTree fixtures.
/// </summary>
public sealed class CrdtConformanceTests
{
    private const string Collections = "collections";

    [Fact]
    public void ReplaysEveryTextCrdtFixture()
    {
        RequireFixture(Collections, "textcrdt_convergence.json");
        RequireFixture(Collections, "textcrdt_delta_sync.json");

        var scenarios = 0;
        var steps = 0;
        var assertions = 0;
        ReplayTextFixture(
            "textcrdt_convergence.json",
            ref scenarios,
            ref steps,
            ref assertions);
        ReplayTextFixture(
            "textcrdt_delta_sync.json",
            ref scenarios,
            ref steps,
            ref assertions);

        Assert.Equal(10, scenarios);
        Assert.True(steps >= 46, $"text CRDT runner replayed only {steps} steps");
        Assert.True(assertions >= 24, $"text CRDT runner made only {assertions} assertions");
    }

    [Fact]
    public void ReplaysTheSeqCrdtFixture()
    {
        const string fixture = "seqcrdt_convergence.json";
        RequireFixture(Collections, fixture);
        using var document = SpecCorpus.Load(Collections, fixture);
        var scenarios = document.RootElement.GetProperty("scenarios");
        var stepCount = 0;
        var assertions = 0;

        foreach (var scenario in scenarios.EnumerateArray())
        {
            var replicas = SeedSequence(scenario);
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                stepCount++;
                ApplySequenceStep(replicas, step);
            }

            AssertSequenceExpectations(replicas, scenario.GetProperty("expect"), ref assertions);
        }

        Assert.Equal(6, scenarios.GetArrayLength());
        Assert.True(stepCount >= 33, $"SeqCrdt runner replayed only {stepCount} steps");
        Assert.True(assertions >= 16, $"SeqCrdt runner made only {assertions} assertions");
    }

    [Fact]
    public void ReplaysTheCrdtTreeAlgebraFixture()
    {
        const string corpus = "crdt-tree";
        const string fixture = "algebra.json";
        var names = SpecCorpus.FixtureNames(corpus);
        Assert.Equal([fixture], names);
        using var document = SpecCorpus.Load(corpus, fixture);
        var scenarios = document.RootElement.GetProperty("scenarios");
        var assertions = 0;

        foreach (var scenario in scenarios.EnumerateArray())
        {
            var name = scenario.GetProperty("name").GetString();
            switch (name)
            {
                case "merge algebra is order and duplication independent":
                    ReplayMergeAlgebra(scenario, ref assertions);
                    break;
                case "empty frontier snapshot preserves lineage":
                    ReplaySnapshotLineage(scenario, ref assertions);
                    break;
                case "own frontier emits an empty delta":
                    ReplayOwnFrontier(scenario, ref assertions);
                    break;
                default:
                    throw new InvalidOperationException($"unknown CrdtTree scenario: {name}");
            }
        }

        Assert.Equal(3, scenarios.GetArrayLength());
        Assert.True(assertions >= 10, $"CrdtTree runner made only {assertions} assertions");
    }

    [Fact]
    public void ProductionCrdtsRejectBoundaryMutationsAndCollectOnlyStableTombstones()
    {
        var text = TextCrdt.FromString(1, "abc");
        Assert.True(text.Delete(2));
        Assert.Equal(0, text.GarbageCollect(_ => false));
        Assert.Equal(1, text.GarbageCollect(_ => true));
        Assert.Equal("ab", text.Text);

        var sequence = new SeqCrdt<string, int>(1);
        Assert.True(sequence.InsertBack("a", 1, 1));
        Assert.True(sequence.Remove("a", 2));
        Assert.Equal(0, sequence.GarbageCollect(_ => false));
        Assert.Equal(1, sequence.GarbageCollect(_ => true));

        var tree = new LosslessTreeCrdt(1);
        var leaf = tree.CreateNode(TreeNodeId.Root, NodeSeed.Leaf(LeafKind.Raw, "é"));
        var error = Assert.Throws<TreeException>(() => tree.EditLeaf(leaf, 1, 0, "x"));
        Assert.Equal(TreeError.NonScalarBoundary, error.Error);
        Assert.Equal("é", tree.Render());
    }

    private static void RequireFixture(string corpus, string fixture)
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}");
        Assert.Contains(fixture, SpecCorpus.FixtureNames(corpus));
    }

    private static void ReplayTextFixture(
        string fixture,
        ref int scenarioCount,
        ref int stepCount,
        ref int assertionCount)
    {
        using var document = SpecCorpus.Load(Collections, fixture);
        foreach (var scenario in document.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            scenarioCount++;
            var replicas = SeedText(scenario);
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                stepCount++;
                ApplyTextStep(replicas, step, ref assertionCount);
            }

            AssertTextExpectations(
                replicas,
                scenario.GetProperty("expect"),
                ref assertionCount);
        }
    }

    private static Dictionary<string, TextCrdt> SeedText(JsonElement scenario)
    {
        var seed = scenario.GetProperty("seed");
        long peer;
        string text;
        if (seed.ValueKind == JsonValueKind.String)
        {
            peer = scenario.GetProperty("replica").GetProperty("peer").GetInt64();
            text = seed.GetString()!;
        }
        else
        {
            peer = seed.GetProperty("peer").GetInt64();
            text = seed.GetProperty("text").GetString()!;
        }

        return new Dictionary<string, TextCrdt>(StringComparer.Ordinal)
        {
            ["a"] = TextCrdt.FromString(peer, text),
        };
    }

    private static void ApplyTextStep(
        IDictionary<string, TextCrdt> replicas,
        JsonElement step,
        ref int assertionCount)
    {
        if (step.TryGetProperty("fork", out var fork))
        {
            replicas.Add(
                fork.GetString()!,
                replicas["a"].Fork(step.GetProperty("peer").GetInt64()));
            return;
        }

        if (step.TryGetProperty("clone", out var clone))
        {
            replicas.Add(
                clone.GetString()!,
                replicas[step.GetProperty("from").GetString()!].Copy());
            return;
        }

        if (step.TryGetProperty("new", out var fresh))
        {
            replicas.Add(
                fresh.GetString()!,
                new TextCrdt(step.GetProperty("peer").GetInt64()));
            return;
        }

        if (step.TryGetProperty("merge", out var merge))
        {
            replicas[merge.GetProperty("into").GetString()!]
                .MergeFrom(replicas[merge.GetProperty("from").GetString()!]);
            return;
        }

        if (step.TryGetProperty("exchange", out var exchange))
        {
            var leftName = exchange[0].GetString()!;
            var rightName = exchange[1].GetString()!;
            var left = replicas[leftName];
            var right = replicas[rightName];
            var toLeft = right.DeltaSince(left.VersionVector());
            var toRight = left.DeltaSince(right.VersionVector());
            left.ApplyDelta(toLeft);
            right.ApplyDelta(toRight);
            return;
        }

        if (step.TryGetProperty("snapshot", out var snapshot))
        {
            var source = replicas[snapshot.GetProperty("from").GetString()!];
            var target = new TextCrdt(snapshot.GetProperty("peer").GetInt64());
            var changed = target.ApplyDelta(
                source.DeltaSince(new Dictionary<long, long>()));
            replicas.Add(snapshot.GetProperty("into").GetString()!, target);
            assertionCount++;
            Assert.Equal(step.GetProperty("expect_changed").GetBoolean(), changed);
            return;
        }

        if (step.TryGetProperty("delta", out var delta))
        {
            var target = replicas[delta.GetProperty("into").GetString()!];
            var source = replicas[delta.GetProperty("from").GetString()!];
            var changed = target.ApplyDelta(source.DeltaSince(target.VersionVector()));
            assertionCount++;
            Assert.Equal(step.GetProperty("expect_changed").GetBoolean(), changed);
            return;
        }

        var targetName =
            step.TryGetProperty("on", out var on)
                ? on.GetString()!
                : "a";
        var targetReplica = replicas[targetName];
        var operation = step.GetProperty("op").GetString();
        switch (operation)
        {
            case "insert":
                targetReplica.Insert(
                    step.GetProperty("index").GetInt32(),
                    step.GetProperty("ch").GetString()!);
                break;
            case "insert_str":
                targetReplica.InsertString(
                    step.GetProperty("index").GetInt32(),
                    step.GetProperty("str").GetString()!);
                break;
            case "delete":
                targetReplica.Delete(step.GetProperty("index").GetInt32());
                break;
            case "gc":
                var stable = step.GetProperty("stable").GetBoolean();
                var collected = targetReplica.GarbageCollect(_ => stable);
                assertionCount++;
                Assert.Equal(step.GetProperty("expect_collected").GetInt32(), collected);
                break;
            default:
                throw new InvalidOperationException($"unknown TextCrdt op: {operation}");
        }
    }

    private static void AssertTextExpectations(
        IReadOnlyDictionary<string, TextCrdt> replicas,
        JsonElement expect,
        ref int assertions)
    {
        if (expect.TryGetProperty("texts_equal", out var equalGroups))
        {
            foreach (var group in equalGroups.EnumerateArray())
            {
                var names = group.EnumerateArray().Select(item => item.GetString()!).ToArray();
                var convergedText = replicas[names[0]].Text;
                foreach (var name in names.Skip(1))
                {
                    assertions++;
                    Assert.Equal(convergedText, replicas[name].Text);
                }
            }
        }

        if (expect.TryGetProperty("text", out var text))
        {
            assertions++;
            Assert.Equal(text.GetString(), replicas["a"].Text);
        }

        if (expect.TryGetProperty("text_on", out var perReplica))
        {
            foreach (var property in perReplica.EnumerateObject())
            {
                assertions++;
                Assert.Equal(property.Value.GetString(), replicas[property.Name].Text);
            }
        }

        if (expect.TryGetProperty("len", out var length))
        {
            assertions++;
            Assert.Equal(length.GetInt32(), replicas["a"].Length);
        }

        if (expect.TryGetProperty("tombstone_count", out var tombstones))
        {
            assertions++;
            Assert.Equal(tombstones.GetInt32(), replicas["a"].TombstoneCount);
        }

        if (expect.TryGetProperty("a_starts_with", out var startsWith))
        {
            assertions++;
            Assert.StartsWith(startsWith.GetString()!, replicas["a"].Text, StringComparison.Ordinal);
        }

        if (expect.TryGetProperty("a_ends_with", out var endsWith))
        {
            assertions++;
            Assert.EndsWith(endsWith.GetString()!, replicas["a"].Text, StringComparison.Ordinal);
        }

        if (expect.TryGetProperty("version_vector_on", out var vectors))
        {
            foreach (var property in vectors.EnumerateObject())
            {
                var actual = replicas[property.Name].VersionVector();
                var expected = property.Value
                    .EnumerateObject()
                    .ToDictionary(
                        item => long.Parse(item.Name, System.Globalization.CultureInfo.InvariantCulture),
                        item => item.Value.GetInt64());
                assertions++;
                Assert.Equal(expected.OrderBy(item => item.Key), actual.OrderBy(item => item.Key));
            }
        }
    }

    private static Dictionary<string, SeqCrdt<string, string>> SeedSequence(JsonElement scenario)
    {
        JsonElement seed;
        int peer;
        if (scenario.TryGetProperty("seed", out seed))
        {
            peer = seed.GetProperty("peer").GetInt32();
        }
        else
        {
            peer = scenario.GetProperty("replica").GetProperty("peer").GetInt32();
        }

        var sequence = new SeqCrdt<string, string>(peer);
        if (seed.ValueKind == JsonValueKind.Object
            && seed.TryGetProperty("inserts", out var inserts))
        {
            foreach (var insert in inserts.EnumerateArray())
            {
                sequence.InsertBack(
                    insert.GetProperty("id").GetString()!,
                    SequenceValue(insert.GetProperty("value")),
                    insert.GetProperty("now").GetInt64());
            }
        }

        return new Dictionary<string, SeqCrdt<string, string>>(StringComparer.Ordinal)
        {
            ["a"] = sequence,
        };
    }

    private static void ApplySequenceStep(
        IDictionary<string, SeqCrdt<string, string>> replicas,
        JsonElement step)
    {
        if (step.TryGetProperty("fork", out var fork))
        {
            replicas.Add(
                fork.GetString()!,
                replicas["a"].Fork(step.GetProperty("peer").GetInt32()));
            return;
        }

        if (step.TryGetProperty("clone", out var clone))
        {
            replicas.Add(
                clone.GetString()!,
                replicas[step.GetProperty("from").GetString()!].Copy());
            return;
        }

        if (step.TryGetProperty("merge", out var merge))
        {
            replicas[merge.GetProperty("into").GetString()!]
                .MergeFrom(
                    replicas[merge.GetProperty("from").GetString()!],
                    step.GetProperty("now").GetInt64());
            return;
        }

        var target =
            replicas[
                step.TryGetProperty("on", out var on)
                    ? on.GetString()!
                    : "a"];
        var id = step.GetProperty("id").GetString()!;
        var now = step.GetProperty("now").GetInt64();
        switch (step.GetProperty("op").GetString())
        {
            case "insert_back":
                target.InsertBack(id, SequenceValue(step.GetProperty("value")), now);
                break;
            case "insert_front":
                target.InsertFront(id, SequenceValue(step.GetProperty("value")), now);
                break;
            case "move_after":
                target.MoveAfter(id, step.GetProperty("anchor").GetString()!, now);
                break;
            case "move_before":
                target.MoveBefore(id, step.GetProperty("anchor").GetString()!, now);
                break;
            case "set_value":
                target.SetValue(id, SequenceValue(step.GetProperty("value")), now);
                break;
            case "remove":
                target.Remove(id, now);
                break;
            default:
                throw new InvalidOperationException(
                    $"unknown SeqCrdt op: {step.GetProperty("op").GetString()}");
        }
    }

    private static void AssertSequenceExpectations(
        IReadOnlyDictionary<string, SeqCrdt<string, string>> replicas,
        JsonElement expect,
        ref int assertions)
    {
        if (expect.TryGetProperty("order", out var order))
            AssertOrder(replicas["a"], order, ref assertions);

        if (expect.TryGetProperty("order_on", out var orderOn))
        {
            foreach (var property in orderOn.EnumerateObject())
                AssertOrder(replicas[property.Name], property.Value, ref assertions);
        }

        if (expect.TryGetProperty("orders_equal", out var groups))
        {
            foreach (var group in groups.EnumerateArray())
            {
                var names = group.EnumerateArray().Select(item => item.GetString()!).ToArray();
                var first = replicas[names[0]].Order();
                foreach (var name in names.Skip(1))
                {
                    assertions++;
                    Assert.Equal(first, replicas[name].Order());
                }
            }
        }

        if (expect.TryGetProperty("get", out var get))
            AssertGets(replicas["a"], get, ref assertions);

        if (expect.TryGetProperty("get_on", out var getOn))
        {
            foreach (var property in getOn.EnumerateObject())
                AssertGets(replicas[property.Name], property.Value, ref assertions);
        }

        if (expect.TryGetProperty("len", out var length))
        {
            var target = ConvergedTarget(expect);
            assertions++;
            Assert.Equal(length.GetInt32(), replicas[target].Count);
        }

        if (expect.TryGetProperty("contains_all", out var contains))
        {
            var target = ConvergedTarget(expect);
            foreach (var id in contains.EnumerateArray())
            {
                assertions++;
                Assert.True(replicas[target].Contains(id.GetString()!));
            }
        }

        if (expect.TryGetProperty("not_contains_on", out var absentOn))
        {
            foreach (var property in absentOn.EnumerateObject())
            {
                foreach (var id in property.Value.EnumerateArray())
                {
                    assertions++;
                    Assert.False(replicas[property.Name].Contains(id.GetString()!));
                }
            }
        }
    }

    private static void AssertOrder(
        SeqCrdt<string, string> sequence,
        JsonElement expected,
        ref int assertions)
    {
        assertions++;
        Assert.Equal(
            expected.EnumerateArray().Select(item => item.GetString()!).ToArray(),
            sequence.Order());
    }

    private static void AssertGets(
        SeqCrdt<string, string> sequence,
        JsonElement expected,
        ref int assertions)
    {
        foreach (var property in expected.EnumerateObject())
        {
            assertions++;
            Assert.True(sequence.TryGetValue(property.Name, out var actual));
            Assert.Equal(SequenceValue(property.Value), actual);
        }
    }

    private static string SequenceValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : value.GetRawText();

    private static string ConvergedTarget(JsonElement expect)
    {
        if (!expect.TryGetProperty("orders_equal", out var groups)
            || groups.GetArrayLength() == 0
            || groups[0].GetArrayLength() == 0)
        {
            return "a";
        }

        return groups[0][0].GetString()!;
    }

    private static void ReplayMergeAlgebra(JsonElement scenario, ref int assertions)
    {
        var seedSpec = scenario.GetProperty("seed");
        var seed = TextCrdt.FromString(
            seedSpec.GetProperty("peer").GetInt64(),
            seedSpec.GetProperty("text").GetString()!);
        var replicas = new Dictionary<string, TextCrdt>(StringComparer.Ordinal);
        foreach (var replica in scenario.GetProperty("replicas").EnumerateArray())
        {
            var fork = seed.Fork(replica.GetProperty("peer").GetInt64());
            fork.InsertString(fork.Length, replica.GetProperty("insert").GetString()!);
            replicas.Add(replica.GetProperty("name").GetString()!, fork);
        }

        var merged = new List<TextCrdt>();
        var index = 0;
        foreach (var order in scenario.GetProperty("merge_orders").EnumerateArray())
        {
            var result = seed.Fork(100 + index++);
            foreach (var name in order.EnumerateArray())
                result.MergeFrom(replicas[name.GetString()!]);
            merged.Add(result);
        }

        var expected = scenario.GetProperty("expect");
        if (expected.GetProperty("texts_equal").GetBoolean())
        {
            foreach (var result in merged.Skip(1))
            {
                assertions++;
                Assert.Equal(merged[0].Text, result.Text);
            }
        }
        if (expected.GetProperty("version_vectors_equal").GetBoolean())
        {
            var vector = merged[0].VersionVector().OrderBy(pair => pair.Key).ToArray();
            foreach (var result in merged.Skip(1))
            {
                assertions++;
                Assert.Equal(vector, result.VersionVector().OrderBy(pair => pair.Key));
            }
        }
    }

    private static void ReplaySnapshotLineage(JsonElement scenario, ref int assertions)
    {
        var seedSpec = scenario.GetProperty("seed");
        var original = TextCrdt.FromString(
            seedSpec.GetProperty("peer").GetInt64(),
            seedSpec.GetProperty("text").GetString()!);
        var snapshot = original.DeltaSince(new Dictionary<long, long>());
        var restored = new TextCrdt(scenario.GetProperty("restore_peer").GetInt64());
        assertions++;
        Assert.True(restored.ApplyDelta(snapshot));
        assertions++;
        Assert.Equal(original.Text, restored.Text);
        assertions++;
        Assert.Equal(snapshot, restored.DeltaSince(new Dictionary<long, long>()));

        original.InsertString(original.Length, "a");
        restored.InsertString(restored.Length, "b");
        var toOriginal = restored.DeltaSince(original.VersionVector());
        var toRestored = original.DeltaSince(restored.VersionVector());
        original.ApplyDelta(toOriginal);
        restored.ApplyDelta(toRestored);
        assertions++;
        Assert.Equal(original.Text, restored.Text);
        assertions++;
        Assert.Equal(0, CountDuplicateInsertIds(original.DeltaSince(new Dictionary<long, long>())));
    }

    private static void ReplayOwnFrontier(JsonElement scenario, ref int assertions)
    {
        var seed = scenario.GetProperty("seed");
        var tree = TextCrdt.FromString(
            seed.GetProperty("peer").GetInt64(),
            seed.GetProperty("text").GetString()!);
        var delta = tree.DeltaSince(tree.VersionVector());
        assertions++;
        Assert.Empty(delta);
        assertions++;
        Assert.False(tree.ApplyDelta(delta));
    }

    private static int CountDuplicateInsertIds(IEnumerable<TextOp> operations)
    {
        var ids = new HashSet<TextOpId>();
        var duplicates = 0;
        foreach (var operation in operations)
        {
            if (!ids.Add(operation.Id)) duplicates++;
        }
        return duplicates;
    }
}
