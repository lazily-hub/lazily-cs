using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>Strict replay of every canonical lossless-tree CRDT fixture.</summary>
public sealed class LosslessTreeConformanceTests
{
    private const string Corpus = "lossless-tree";

    private static readonly string[] ExpectedFixtures =
    [
        "concurrent_conflict_preserves_text.json",
        "concurrent_insert_same_parent.json",
        "concurrent_reorder_and_leaf_edit.json",
        "exact_roundtrip.json",
        "invalid_source_roundtrip.json",
        "non_contiguous_anti_entropy.json",
        "one_leaf_edit_delta.json",
        "split_merge.json",
        "token_trivia_preservation.json",
    ];

    [Fact]
    public void ReplaysTheCompleteLosslessTreeCorpus()
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}");
        Assert.Equal(ExpectedFixtures, SpecCorpus.FixtureNames(Corpus));

        var fixtureCount = 0;
        var scenarioCount = 0;
        var stepCount = 0;
        var assertionCount = 0;
        foreach (var fixture in ExpectedFixtures)
        {
            fixtureCount++;
            using var document = SpecCorpus.Load(Corpus, fixture);
            foreach (var scenario in SpecCorpus.Scenarios(document.RootElement, Corpus, fixture).All())
            {
                scenarioCount++;
                var world = SeedWorld(scenario);
                if (scenario.TryGetProperty("steps", out var steps)
                    && steps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var step in steps.EnumerateArray())
                    {
                        stepCount++;
                        ApplyStep(world, step);
                    }
                }

                var expect = FixtureAssertions.Of(
                    scenario,
                    "expect",
                    $"lossless-tree/{fixture} scenario {scenarioCount}");
                AssertExpectations(world, expect, ref assertionCount);
                expect.Verify();
            }
        }

        Assert.Equal(9, fixtureCount);
        Assert.Equal(14, scenarioCount);
        Assert.True(stepCount >= 41, $"lossless-tree runner replayed only {stepCount} steps");
        Assert.True(
            assertionCount >= 29,
            $"lossless-tree runner made only {assertionCount} assertions");
    }

    [Fact]
    public void DottedFrontierRetainsAndRepairsAnInteriorHole()
    {
        var source = new LosslessTreeCrdt(1);
        var parent = source.CreateNode(TreeNodeId.Root, NodeSeed.Element("para"));
        var zero = source.CreateNode(parent, NodeSeed.Leaf(LeafKind.Raw, "0"));
        var target = source.Fork(2);
        var one = source.CreateNode(
            parent,
            Optional<TreeNodeId>.Some(zero),
            NodeSeed.Leaf(LeafKind.Raw, "1"));
        var two = source.CreateNode(
            parent,
            Optional<TreeNodeId>.Some(one),
            NodeSeed.Leaf(LeafKind.Raw, "2"));
        source.CreateNode(
            parent,
            Optional<TreeNodeId>.Some(two),
            NodeSeed.Leaf(LeafKind.Raw, "3"));

        var update = source.Diff(target.Frontier());
        Assert.Equal(3, update.Operations.Count);
        target.ApplyUpdate(new TreeUpdate([update.Operations[0], update.Operations[2]]));
        Assert.Equal("013", target.Render());

        var repair = source.Diff(target.Frontier());
        Assert.Single(repair.Operations);
        target.ApplyUpdate(repair);
        Assert.Equal(source.Render(), target.Render());
    }

    /// <summary>
    /// `Diff` must return operations in canonical `(counter, peer)` order. That order is a
    /// CROSS-BINDING contract, not an implementation detail: the shared corpus addresses diff
    /// results POSITIONALLY — `lossless-tree/non_contiguous_anti_entropy.json` carries
    /// `deliver.only: [0, 2]` — so the fixture only means the same thing in every binding while
    /// every binding returns the same order.
    ///
    /// The corpus cannot catch a regression here. Measured in lazily-zig (#lzzigdiffmutant):
    /// reversing the sort, or deleting it outright, left the entire suite green, because the two
    /// indices select the same SET either way and applying an update is order-tolerant by design.
    /// Only a direct test pins it, so this is that test.
    /// </summary>
    [Fact]
    public void DiffReturnsOperationsInCanonicalCounterPeerOrder()
    {
        var a = new LosslessTreeCrdt(1);
        var para = a.CreateNode(TreeNodeId.Root, NodeSeed.Element("para"));
        var baseLeaf = a.CreateNode(para, NodeSeed.Leaf(LeafKind.Trivia, "0"));

        var b = a.Fork(2);

        // `a` runs ahead to counter 4 while `b`'s single op stays at counter 3. The remote op
        // therefore lands LAST in a's log while sorting EARLIER than a's own later ops — the only
        // shape in which arrival order and canonical order genuinely disagree.
        var one = a.CreateNode(
            para,
            Optional<TreeNodeId>.Some(baseLeaf),
            NodeSeed.Leaf(LeafKind.Trivia, "1"));
        var two = a.CreateNode(
            para,
            Optional<TreeNodeId>.Some(one),
            NodeSeed.Leaf(LeafKind.Trivia, "2"));
        var remote = b.CreateNode(
            para,
            Optional<TreeNodeId>.Some(baseLeaf),
            NodeSeed.Leaf(LeafKind.Trivia, "9"));
        a.ApplyUpdate(b.Diff(a.Frontier()));

        // The operation log is private, so arrival order is RECONSTRUCTED from the ids the create
        // calls returned rather than read out of the replica. That keeps the two failure modes
        // apart: "diff returns the wrong order" and "the test has gone vacuous" fail on different
        // assertions instead of collapsing into one.
        TreeOpId[] arrival =
        [
            para.Operation,
            baseLeaf.Operation,
            one.Operation,
            two.Operation,
            remote.Operation,
        ];
        var canonical = arrival.OrderBy(id => id).ToArray();

        // Non-vacuity gate, asserted BEFORE the ordering check. If arrival order and canonical
        // order ever coincide, the ordering assertion below holds for an unsorted or reversed
        // diff too and pins nothing — so a refactor that makes them coincide must fail HERE,
        // loudly, rather than quietly hollowing the test out.
        Assert.NotEqual(arrival, canonical);

        var all = a.Diff(new TreeVersionFrontier());
        Assert.Equal(arrival.Length, all.Operations.Count);
        Assert.Equal(canonical, all.Operations.Select(operation => operation.Id).ToArray());

        for (var index = 1; index < all.Operations.Count; index++)
        {
            var previous = all.Operations[index - 1].Id;
            var current = all.Operations[index].Id;
            Assert.True(
                previous.CompareTo(current) < 0,
                $"diff op {index - 1} {previous} does not strictly precede op {index} {current} "
                + "in canonical (counter, peer) order");
        }
    }

    private static World SeedWorld(JsonElement scenario)
    {
        var seed = scenario.GetProperty("seed");
        var world = new World(
            new Dictionary<string, LosslessTreeCrdt>(StringComparer.Ordinal)
            {
                ["a"] = new LosslessTreeCrdt(seed.GetProperty("peer").GetInt64()),
            },
            new Dictionary<string, TreeNodeId>(StringComparer.Ordinal));
        BuildChildren(
            world,
            seed.GetProperty("tree"),
            TreeNodeId.Root);
        return world;
    }

    private static void BuildChildren(World world, JsonElement specification, TreeNodeId parent)
    {
        if (!specification.TryGetProperty("children", out var children)) return;
        var previous = Optional<TreeNodeId>.None;
        foreach (var child in children.EnumerateArray())
        {
            var node = world.Replicas["a"].CreateNode(parent, previous, Seed(child));
            world.Ids.Add(child.GetProperty("label").GetString()!, node);
            BuildChildren(world, child, node);
            previous = Optional<TreeNodeId>.Some(node);
        }
    }

    private static NodeSeed Seed(JsonElement specification)
    {
        if (specification.TryGetProperty("element", out var element))
            return NodeSeed.Element(element.GetString()!);

        var leaf = specification.GetProperty("leaf");
        return NodeSeed.Leaf(
            leaf.GetProperty("kind").GetString() switch
            {
                "token" => LeafKind.Token,
                "trivia" => LeafKind.Trivia,
                "raw" => LeafKind.Raw,
                "error" => LeafKind.Error,
                var kind => throw new InvalidOperationException($"unknown leaf kind: {kind}"),
            },
            leaf.GetProperty("text").GetString()!);
    }

    private static void ApplyStep(World world, JsonElement step)
    {
        if (step.TryGetProperty("fork", out var fork))
        {
            world.Replicas.Add(
                fork.GetString()!,
                world.Replicas["a"].Fork(step.GetProperty("peer").GetInt64()));
            return;
        }

        if (step.TryGetProperty("clone", out var clone))
        {
            world.Replicas.Add(
                clone.GetString()!,
                world.Replicas[step.GetProperty("from").GetString()!].Copy());
            return;
        }

        if (step.TryGetProperty("sync", out var sync))
        {
            var source = world.Replicas[sync.GetProperty("from").GetString()!];
            var target = world.Replicas[sync.GetProperty("to").GetString()!];
            target.ApplyUpdate(source.Diff(target.Frontier()));
            return;
        }

        if (step.TryGetProperty("deliver", out var deliver))
        {
            var source = world.Replicas[deliver.GetProperty("from").GetString()!];
            var target = world.Replicas[deliver.GetProperty("to").GetString()!];
            var full = source.Diff(target.Frontier());
            var selected = deliver.GetProperty("only")
                .EnumerateArray()
                .Select(index => full.Operations[index.GetInt32()])
                .ToArray();
            target.ApplyUpdate(new TreeUpdate(selected));
            return;
        }

        var replica = step.GetProperty("on").GetString()!;
        ApplyOperation(world, world.Replicas[replica], step);
    }

    private static void ApplyOperation(
        World world,
        LosslessTreeCrdt replica,
        JsonElement operation)
    {
        switch (operation.GetProperty("op").GetString())
        {
            case "create":
                var label = operation.GetProperty("label").GetString()!;
                world.Ids.Add(
                    label,
                    replica.CreateNode(
                        world.Id(operation.GetProperty("parent").GetString()!),
                        world.After(operation),
                        Seed(operation)));
                break;
            case "edit_leaf":
                replica.EditLeaf(
                    world.Id(operation.GetProperty("node").GetString()!),
                    operation.GetProperty("at_byte").GetInt32(),
                    operation.TryGetProperty("delete_bytes", out var delete)
                        ? delete.GetInt32()
                        : 0,
                    operation.TryGetProperty("insert", out var insert)
                        ? insert.GetString()!
                        : string.Empty);
                break;
            case "split":
                world.Ids.Add(
                    operation.GetProperty("new_label").GetString()!,
                    replica.SplitLeaf(
                        world.Id(operation.GetProperty("node").GetString()!),
                        operation.GetProperty("at_byte").GetInt32()));
                break;
            case "merge_leaves":
                replica.MergeAdjacentLeaves(
                    world.Id(operation.GetProperty("left").GetString()!),
                    world.Id(operation.GetProperty("right").GetString()!));
                break;
            case "reorder":
                replica.ReorderChild(
                    world.Id(operation.GetProperty("node").GetString()!),
                    world.After(operation));
                break;
            case "tombstone":
                replica.TombstoneNode(
                    world.Id(operation.GetProperty("node").GetString()!));
                break;
            default:
                throw new InvalidOperationException(
                    $"unknown lossless-tree op: {operation.GetProperty("op").GetString()}");
        }
    }

    private static void AssertExpectations(
        World world,
        FixtureAssertions expect,
        ref int assertionsOut)
    {
        var assertions = 0;

        if (expect.TryAssertKeyWith(
                "render",
                render => render.AssertEqual(w => w.GetString(), world.Replicas["a"].Render())))
            assertions++;

        expect.TryAssertObjectKey(
            "render_on",
            renderOn =>
            {
                foreach (var property in renderOn.EnumerateObject())
                {
                    var name = property.Name;
                    assertions++;
                    renderOn.AssertKeyWith(
                        name,
                        want => want.AssertEqual(w => w.GetString(), world.Replicas[name].Render()));
                }
            });

        if (expect.TryAssertKeyWith(
                "live_nodes",
                liveNodes => liveNodes.AssertEqual(w => w.GetInt32(), world.Replicas["a"].LiveNodeCount)))
            assertions++;

        expect.TryAssertKeyWith(
            "converged",
            converged => converged.Against(world.Replicas, (expect, replicas) =>
            {
                var names = expect.EnumerateArray().Select(value => value.GetString()!).ToArray();
                var rendered = replicas[names[0]].Render();
                foreach (var name in names.Skip(1))
                {
                    assertions++;
                    Assert.Equal(rendered, replicas[name].Render());
                }
            }));

        assertionsOut += assertions;
    }

    private sealed record World(
        Dictionary<string, LosslessTreeCrdt> Replicas,
        Dictionary<string, TreeNodeId> Ids)
    {
        internal TreeNodeId Id(string label) =>
            Ids.TryGetValue(label, out var id)
                ? id
                : throw new InvalidOperationException($"unknown node label: {label}");

        internal Optional<TreeNodeId> After(JsonElement operation)
        {
            if (!operation.TryGetProperty("after", out var after)
                || after.ValueKind == JsonValueKind.Null)
            {
                return Optional<TreeNodeId>.None;
            }

            return Optional<TreeNodeId>.Some(Id(after.GetString()!));
        }
    }
}
