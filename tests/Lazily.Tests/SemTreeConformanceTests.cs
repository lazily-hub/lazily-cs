using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Replays <c>collections/semtree_incremental.json</c> against <see cref="SemTree{TValue,TDerived}"/>.
/// </summary>
/// <remarks>
/// The fixture's claim is about COST, not values: editing one node must recompute only its ancestor
/// chain, and an edit whose fold lands on the same value must not reach a downstream consumer at
/// all. Both are invisible to a value assertion — a tree that refolds the entire document on every
/// edit returns exactly the right numbers — so the two load-bearing expectations
/// (<c>sibling_a_cached</c>, <c>downstream_consumer_reran</c>) are asserted on recompute counters
/// installed inside the observed computations themselves.
/// </remarks>
public sealed class SemTreeConformanceTests
{
    private const string Corpus = "collections";
    private const string Fixture = "semtree_incremental.json";

    [Fact]
    public void ReplaysTheSemTreeFixtureWithNoDivergence()
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}; " +
            "clone lazily-spec as a sibling. A skip here would report green while testing nothing.");

        using var doc = SpecCorpus.Load(Corpus, Fixture);
        var divergences = new List<string>();
        var assertions = 0;
        var scenarios = 0;

        foreach (var scenario in doc.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            var name = scenario.GetProperty("name").GetString()!;

            void Check(string key, object? got, object? want)
            {
                assertions++;
                if (!Equals(got?.ToString(), want?.ToString())) divergences.Add($"{name}:{key} — got {got}, want {want}");
            }

            var ctx = new Context();
            var fold = FoldOf(scenario.GetProperty("fold").GetString()!);
            var tree = SemTree<int, int>.Build(ctx, ReadSpec(scenario.GetProperty("tree")), fold);

            // A consumer of the ROOT memo, counting its own runs. The guard clause is about what
            // reaches this: an edit that does not change the root fold must leave it untouched.
            var consumerRuns = 0;
            var consumer = ctx.Slot(c =>
            {
                consumerRuns++;
                return tree.Derived(tree.RootId, c);
            });

            // Per-node recompute counters, wired as readers of each node's memo. A reader recomputes
            // exactly when its memo is invalidated, which is the observable the fixture names.
            var nodeRuns = new Dictionary<string, int>(StringComparer.Ordinal);
            var watchers = new Dictionary<string, Computed<int>>(StringComparer.Ordinal);
            foreach (var id in NodeIds(scenario.GetProperty("tree")))
            {
                var captured = id;
                nodeRuns[captured] = 0;
                watchers[captured] = ctx.Slot(c =>
                {
                    nodeRuns[captured]++;
                    return tree.Derived(captured, c);
                });
            }

            foreach (var w in watchers.Values) _ = w.Get();
            _ = consumer.Get();

            foreach (var want in scenario.GetProperty("expect_initial").EnumerateObject())
            {
                Check($"initial.{want.Name}", tree.Derived(want.Name), want.Value.GetInt32());
            }

            var runsBefore = nodeRuns.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var consumerBefore = consumerRuns;

            if (scenario.TryGetProperty("edit", out var edit))
            {
                tree.SetValue(edit.GetProperty("id").GetString()!, edit.GetProperty("value").GetInt32());
            }

            if (scenario.TryGetProperty("remove_child", out var removal))
            {
                tree.RemoveChild(removal.GetProperty("parent").GetString()!, removal.GetProperty("child").GetString()!);
            }

            // Pull everything so any invalidation that WOULD recompute has done so.
            foreach (var w in watchers.Values) _ = w.Get();
            _ = consumer.Get();

            var after = scenario.GetProperty("expect_after");
            foreach (var want in after.EnumerateObject())
            {
                switch (want.Name)
                {
                    case "sibling_a_cached":
                        // The sibling subtree must not have recomputed: nothing it reads changed.
                        Check("sibling_a_cached", nodeRuns["a"] == runsBefore["a"], want.Value.GetBoolean());
                        break;

                    case "downstream_consumer_reran":
                        Check("downstream_consumer_reran", consumerRuns > consumerBefore, want.Value.GetBoolean());
                        break;

                    default:
                        Check($"after.{want.Name}", tree.Derived(want.Name), want.Value.GetInt32());
                        break;
                }
            }

            scenarios++;
        }

        Assert.Equal(Array.Empty<string>(), divergences.Order(StringComparer.Ordinal).ToArray());
        Assert.True(scenarios > 0, "loaded the fixture but replayed no scenario");
        Assert.True(assertions > 0, "replayed scenarios but checked nothing");
    }

    private static FoldFn<int, int> FoldOf(string name) => name switch
    {
        "sum" => static (value, children) => value + children.Sum(),
        "count_positive" => static (value, children) => (value > 0 ? 1 : 0) + children.Sum(),
        _ => throw new InvalidOperationException($"unknown fold {name}"),
    };

    private static TreeNodeSpec<int> ReadSpec(JsonElement node)
    {
        var order = new List<string>();
        var children = new Dictionary<string, TreeNodeSpec<int>>(StringComparer.Ordinal);
        if (node.TryGetProperty("children", out var kids))
        {
            foreach (var k in kids.GetProperty("order").EnumerateArray()) order.Add(k.GetString()!);
            foreach (var v in kids.GetProperty("values").EnumerateObject()) children[v.Name] = ReadSpec(v.Value);
        }

        return new TreeNodeSpec<int>
        {
            Id = node.GetProperty("id").GetString()!,
            Value = node.GetProperty("value").GetInt32(),
            Order = order,
            Children = children,
        };
    }

    private static IEnumerable<string> NodeIds(JsonElement node)
    {
        yield return node.GetProperty("id").GetString()!;
        if (!node.TryGetProperty("children", out var kids)) yield break;
        foreach (var v in kids.GetProperty("values").EnumerateObject())
        {
            foreach (var id in NodeIds(v.Value)) yield return id;
        }
    }
}
