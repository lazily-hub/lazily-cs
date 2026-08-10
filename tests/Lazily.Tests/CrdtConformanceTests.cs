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
        var scenarios = SpecCorpus.Scenarios(document.RootElement, Collections, fixture);
        var stepCount = 0;
        var assertions = 0;

        foreach (var scenario in scenarios.All())
        {
            var replicas = SeedSequence(scenario);
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                stepCount++;
                ApplySequenceStep(replicas, step);
            }

            var expect = FixtureAssertions.Of(
                scenario,
                "expect",
                $"{Collections}/{fixture} scenario {scenario.GetProperty("name").GetString()}");
            AssertSequenceExpectations(replicas, expect, ref assertions);
            expect.Verify();
        }

        // 8 since the corpus pinned the fork-clock rule (#lzzigforkhlcpeer): a fork
        // resumes the source's HLC causal position but stamps under its OWN peer.
        // The step/assertion floors are the exact observed totals, so a scenario that
        // stops being replayed reddens here rather than hiding behind the census.
        Assert.Equal(8, scenarios.Count);
        Assert.True(stepCount >= 42, $"SeqCrdt runner replayed only {stepCount} steps");
        Assert.True(assertions >= 22, $"SeqCrdt runner made only {assertions} assertions");
    }

    [Fact]
    public void ReplaysTheCrdtTreeAlgebraFixture()
    {
        const string corpus = "crdt-tree";
        const string fixture = "algebra.json";
        var names = SpecCorpus.FixtureNames(corpus);
        Assert.Equal([fixture], names);
        using var document = SpecCorpus.Load(corpus, fixture);
        var scenarios = SpecCorpus.Scenarios(document.RootElement, corpus, fixture);
        var assertions = 0;

        foreach (var scenario in scenarios.All())
        {
            // Dispatch on `id`, the canonical scenario identity
            // (#recommendedconformanceco). `name` is a prose label, so keying on
            // it means a copy-edit upstream silently drops through to `default`.
            var name = scenario.GetProperty("id").GetString();
            switch (name)
            {
                case "merge_is_order_and_duplication_independent":
                    ReplayMergeAlgebra(scenario, ref assertions);
                    break;
                case "empty_frontier_snapshot_preserves_lineage":
                    ReplaySnapshotLineage(scenario, ref assertions);
                    break;
                case "own_frontier_emits_empty_delta":
                    ReplayOwnFrontier(scenario, ref assertions);
                    break;
                default:
                    throw new InvalidOperationException($"unknown CrdtTree scenario: {name}");
            }
        }

        Assert.Equal(3, scenarios.Count);
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
        foreach (var scenario in SpecCorpus.Scenarios(document.RootElement, Collections, fixture).All())
        {
            scenarioCount++;
            var replicas = SeedText(scenario);
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                stepCount++;
                ApplyTextStep(replicas, step, ref assertionCount);
            }

            var expect = FixtureAssertions.Of(
                scenario,
                "expect",
                $"{Collections}/{fixture} scenario {scenario.GetProperty("name").GetString()}");
            AssertTextExpectations(replicas, expect, ref assertionCount);
            expect.Verify();
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
        FixtureAssertions expect,
        ref int assertionsOut)
    {
        var assertions = 0;

        expect.TryAssertKeyWith(
            "texts_equal",
            equalGroups =>
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
            });

        if (expect.TryAssertKeyWith("text", text => Assert.Equal(text.GetString(), replicas["a"].Text)))
            assertions++;

        expect.TryAssertObjectKey(
            "text_on",
            perReplica =>
            {
                foreach (var property in perReplica.EnumerateObject())
                {
                    var name = property.Name;
                    assertions++;
                    perReplica.AssertKeyWith(
                        name,
                        want => Assert.Equal(want.GetString(), replicas[name].Text));
                }
            });

        if (expect.TryAssertKeyWith("len", length => Assert.Equal(length.GetInt32(), replicas["a"].Length)))
            assertions++;

        if (expect.TryAssertKeyWith(
                "tombstone_count",
                tombstones => Assert.Equal(tombstones.GetInt32(), replicas["a"].TombstoneCount)))
            assertions++;

        if (expect.TryAssertKeyWith(
                "a_starts_with",
                startsWith => Assert.StartsWith(
                    startsWith.GetString()!,
                    replicas["a"].Text,
                    StringComparison.Ordinal)))
            assertions++;

        if (expect.TryAssertKeyWith(
                "a_ends_with",
                endsWith => Assert.EndsWith(
                    endsWith.GetString()!,
                    replicas["a"].Text,
                    StringComparison.Ordinal)))
            assertions++;

        expect.TryAssertObjectKey(
            "version_vector_on",
            vectors =>
            {
                foreach (var property in vectors.EnumerateObject())
                {
                    var name = property.Name;
                    vectors.AssertObjectKey(name, want =>
                    {
                        var actual = replicas[name].VersionVector();
                        var expected = new Dictionary<long, long>();
                        foreach (var item in want.EnumerateObject())
                        {
                            var node = item.Name;
                            expected[long.Parse(node, System.Globalization.CultureInfo.InvariantCulture)] =
                                want.AssertKeyInto(node, v => v.GetInt64());
                        }
                        Assert.Equal(expected.OrderBy(item => item.Key), actual.OrderBy(item => item.Key));
                    });
                    assertions++;
                }
            });

        assertionsOut += assertions;
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
        FixtureAssertions expect,
        ref int assertionsOut)
    {
        var assertions = 0;

        expect.TryAssertKeyWith("order", order => assertions += AssertOrder(replicas["a"], order));

        expect.TryAssertObjectKey(
            "order_on",
            orderOn =>
            {
                foreach (var property in orderOn.EnumerateObject())
                {
                    var name = property.Name;
                    assertions += orderOn.AssertKeyInto(
                        name,
                        want => AssertOrder(replicas[name], want));
                }
            });

        expect.TryAssertKeyWith(
            "orders_equal",
            groups =>
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
            });

        // Descended (#lzsubblockkeyset): the id set of `get` — and of each replica's block
        // under `get_on` — is now owned by a child tracker rather than by a loop that could
        // not prove it visited everything.
        expect.TryAssertObjectKey("get", get => assertions += AssertGets(replicas["a"], get));

        expect.TryAssertObjectKey(
            "get_on",
            getOn =>
            {
                foreach (var property in getOn.EnumerateObject())
                {
                    var name = property.Name;
                    getOn.AssertObjectKey(
                        name,
                        want => assertions += AssertGets(replicas[name], want));
                }
            });

        var convergedTarget = ConvergedTarget(expect);

        if (expect.TryAssertKeyWith(
                "len",
                length => Assert.Equal(length.GetInt32(), replicas[convergedTarget].Count)))
            assertions++;

        expect.TryAssertKeyWith(
            "contains_all",
            contains =>
            {
                foreach (var id in contains.EnumerateArray())
                {
                    assertions++;
                    Assert.True(replicas[convergedTarget].Contains(id.GetString()!));
                }
            });

        expect.TryAssertObjectKey(
            "not_contains_on",
            absentOn =>
            {
                foreach (var property in absentOn.EnumerateObject())
                {
                    var name = property.Name;
                    absentOn.AssertKeyWith(name, want =>
                    {
                        foreach (var id in want.EnumerateArray())
                        {
                            assertions++;
                            Assert.False(replicas[name].Contains(id.GetString()!));
                        }
                    });
                }
            });

        assertionsOut += assertions;
    }

    private static int AssertOrder(SeqCrdt<string, string> sequence, JsonElement expected)
    {
        Assert.Equal(
            expected.EnumerateArray().Select(item => item.GetString()!).ToArray(),
            sequence.Order());
        return 1;
    }

    private static int AssertGets(SeqCrdt<string, string> sequence, FixtureAssertions expected)
    {
        var made = 0;
        foreach (var property in expected.EnumerateObject())
        {
            var name = property.Name;
            made++;
            expected.AssertKeyWith(name, want =>
            {
                Assert.True(sequence.TryGetValue(name, out var actual));
                Assert.Equal(SequenceValue(want), actual);
            });
        }
        return made;
    }

    private static string SequenceValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : value.GetRawText();

    private static string ConvergedTarget(FixtureAssertions expect)
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

        var expected = FixtureAssertions.Of(
            scenario,
            "expect",
            $"crdt-tree/algebra.json scenario {scenario.GetProperty("name").GetString()}");
        // Both keys used to GATE their loop and never be asserted, so a fixture flipped
        // to false would have retired the check rather than failing it. Each is now
        // compared against what the merges actually produced.
        var textsEqual = merged.Skip(1).All(result => result.Text == merged[0].Text);
        expected.AssertKey("texts_equal", textsEqual);
        assertions += merged.Count - 1;

        var vector = merged[0].VersionVector().OrderBy(pair => pair.Key).ToArray();
        var vectorsEqual = merged
            .Skip(1)
            .All(result => result.VersionVector().OrderBy(pair => pair.Key).SequenceEqual(vector));
        expected.AssertKey("version_vectors_equal", vectorsEqual);
        assertions += merged.Count - 1;

        expected.Verify();
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
