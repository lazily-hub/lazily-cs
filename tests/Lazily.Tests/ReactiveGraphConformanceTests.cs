using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Cross-language conformance for the reactive-graph plane — see
/// <c>../lazily-spec/conformance/reactive-graph/*.json</c>.
/// </summary>
/// <remarks>
/// The corpus is never vendored here; it is resolved through one sibling-relative path. Because a
/// skip-if-absent runner and a green one are indistinguishable from an exit code alone, every test
/// below asserts a positive fixture count and the ledger test pins the exact replayed set — so
/// "ran nothing" fails loudly rather than passing quietly.
/// <para>
/// The runner is parameterised over the EXECUTION MODEL rather than hardcoding
/// <see cref="Context"/>. A cascade defect can be correct synchronously and broken
/// asynchronously — a chain that stops one level below the write serves a stale value forever on
/// a plane whose reads short-circuit on a resolved cache — and no synchronous replay of any
/// fixture can see it. So every model this binding ships replays the same op stream.
/// </para>
/// </remarks>
public sealed class ReactiveGraphConformanceTests
{
    private const string Corpus = "reactive-graph";

    /// <summary>Every execution model the corpus is replayed against.</summary>
    private static IEnumerable<IGraphModel> Models()
    {
        yield return new SyncGraphModel();
        yield return new ThreadSafeGraphModel();
        yield return new AsyncGraphModel();
    }

    /// <summary>
    /// Fixtures NO model can execute, and the exact op or assertion that blocks each.
    /// </summary>
    /// <remarks>
    /// Entries are findings against Lazily's public surface, never relaxations of the fixtures:
    /// nothing here is skipped silently, and a fixture becoming executable fails the ledger
    /// assertion until it is promoted out of this map. Currently empty — the synchronous kernel
    /// models the merge-fold write surface (a source carries its policy in a field) and the
    /// bounded feedback drain, so the whole on-disk corpus replays somewhere.
    /// </remarks>
    private static readonly Dictionary<string, string> Unsupported = [];

    /// <summary>
    /// Fixture/model pairs one execution model cannot run, keyed <c>model/fixture</c>.
    /// </summary>
    /// <remarks>
    /// Same discipline as the binding-wide ledger: each entry is a gap in one plane's public
    /// surface, stated in full, and the per-model completeness assertion subtracts exactly these.
    /// A pair that becomes runnable is therefore not silently left unrun, and one that stops
    /// being runnable cannot be hidden by adding an entry without also saying why.
    /// <para>
    /// The alternative — degrading the missing construct to the nearest thing that plane does
    /// ship — is the worst option available. A lazy slot standing in for an eager signal produces
    /// plausible numbers that satisfy two of the three assertions a signal fixture makes, so the
    /// suite would go green while testing the opposite of what the fixture is for.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> ModelUnsupported = new(StringComparer.Ordinal)
    {
        ["AsyncContext/dispose_signal_reverts_to_lazy.json"] =
            "AsyncContext ships no eager slot constructor, so the `signal` op has nothing to build on that plane",
        ["AsyncContext/signal_materializes_once_per_batch.json"] =
            "AsyncContext ships no eager slot constructor, so the `signal` op has nothing to build on that plane",
        ["AsyncContext/signal_materializes_without_a_read.json"] =
            "AsyncContext ships no eager slot constructor, so the `signal` op has nothing to build on that plane",
        ["AsyncContext/exact_fold_paths_stay_exact.json"] =
            "the `merge_cell` op needs a MergePolicy, which the async plane does not carry (row 15 is a synchronous-kernel row)",
        ["AsyncContext/merge_cell_acquires_no_dependency_edge.json"] =
            "the `merge_cell` op needs a MergePolicy, which the async plane does not carry (row 15 is a synchronous-kernel row)",
        ["AsyncContext/merge_feed_through_a_formula_coalesces.json"] =
            "the `merge_cell` op needs a MergePolicy, which the async plane does not carry (row 15 is a synchronous-kernel row)",
        ["AsyncContext/merge_folds_synchronously_in_batch.json"] =
            "the `merge_cell` op needs a MergePolicy, which the async plane does not carry (row 15 is a synchronous-kernel row)",
        ["AsyncContext/merge_per_settled_cone_not_per_write.json"] =
            "the `merge_cell` op needs a MergePolicy, which the async plane does not carry (row 15 is a synchronous-kernel row)",
        ["AsyncContext/feedback_drain_bound_reports_exhaustion.json"] =
            "the `drain_exhausted` / `writes_own_cone` keys need the bounded effect drain, which is a " +
            "synchronous-scheduler construct; async reruns are executor-scheduled and carry no drain budget",
    };

    /// <summary>
    /// Fixture assertions a model does not satisfy, keyed <c>model/fixture#step:key</c>.
    /// </summary>
    /// <remarks>
    /// Each entry would be a finding against the implementation, never a relaxation of a fixture:
    /// the ledger asserts the observed set equals this one EXACTLY, so a new divergence fails the
    /// build and a fixed one fails it until its entry is deleted. Both directions are load-bearing.
    /// </remarks>
    private static readonly HashSet<string> KnownDivergences = [];

    /// <summary>Every fixture on disk replays on every model, and every assertion in it holds.</summary>
    [Fact]
    public void ReplaysTheWholeCorpusOnEveryModelWithNoUnexpectedDivergence()
    {
        Assert.True(SpecCorpus.Root is not null,
            $"lazily-spec checkout absent at {SpecCorpus.SiblingRelativePath} — clone it beside this repo. " +
            "This is a hard failure, not a skip: a runner that quietly skips its whole corpus is " +
            "indistinguishable from a green one.");

        var names = SpecCorpus.FixtureNames(Corpus);
        Assert.NotEmpty(names);

        var observed = new List<string>();
        var totalChecks = 0;
        var totalReplays = 0;
        var modelCount = 0;

        foreach (var model in Models())
        {
            using (model)
            {
                modelCount++;
                var replayed = new List<string>();
                var gated = 0;

                foreach (var name in names)
                {
                    if (Unsupported.ContainsKey(name)) continue;
                    if (ModelUnsupported.ContainsKey($"{model.Name}/{name}"))
                    {
                        gated++;
                        continue;
                    }
                    replayed.Add(name);
                    // A fresh model per fixture: fixtures are independent scenarios, and a shared
                    // graph would let one fixture's leftover edges answer another's assertions.
                    using var fixtureModel = NewModel(model.Name);
                    totalChecks += ReplayFixture(fixtureModel, name, observed);
                }

                Assert.NotEmpty(replayed);
                Assert.Equal(names.Count - Unsupported.Count - gated, replayed.Count);
                totalReplays += replayed.Count;
            }
        }

        // Positive assertions that the fixtures actually RAN, on every model. Without them a
        // corpus that resolved to an empty directory — or a model loop that silently gated
        // everything — would report green while testing nothing.
        Assert.Equal(3, modelCount);
        Assert.True(totalReplays >= 45,
            $"only {totalReplays} fixture replays across {modelCount} models — the corpus resolved but " +
            "most of it was gated, which is the failure mode this floor exists to catch");
        Assert.True(totalChecks >= 500,
            $"only {totalChecks} assertions ran across {totalReplays} fixture replays — the corpus " +
            "resolved but barely asserted anything");

        var unexpected = observed.Where(d => !KnownDivergences.Contains(Key(d))).ToList();
        var fixedUp = KnownDivergences.Where(k => !observed.Any(d => Key(d) == k)).ToList();
        Assert.True(
            unexpected.Count == 0 && fixedUp.Count == 0,
            $"unexpected divergences:\n  {string.Join("\n  ", unexpected)}\n" +
            $"declared-but-absent divergences:\n  {string.Join("\n  ", fixedUp)}");
    }

    private static IGraphModel NewModel(string name) => name switch
    {
        "Context" => new SyncGraphModel(),
        "ThreadSafeContext" => new ThreadSafeGraphModel(),
        "AsyncContext" => new AsyncGraphModel(),
        _ => throw new NotSupportedException($"unknown execution model {name}"),
    };

    private static int ReplayFixture(IGraphModel model, string name, List<string> observed)
    {
        using var doc = SpecCorpus.Load(Corpus, name);
        var root = doc.RootElement;
        var checks = 0;

        switch (root.GetProperty("shape").GetString())
        {
            case "steps":
                {
                    var engine = new ReactiveGraphEngine(model, name);
                    engine.Replay(root.GetProperty("steps"));
                    observed.AddRange(engine.Divergences.Select(d => $"{model.Name}/{d}"));
                    checks += engine.Checks;
                    break;
                }

            case "scenarios":
                {
                    // The central claim: ending a teardown scope MUST be observationally equal to
                    // disposing each member individually. Both scenarios build the identical graph
                    // and differ only in HOW the nodes go away; every observable must agree.
                    var observations = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                    foreach (var scenario in root.GetProperty("scenarios").EnumerateArray())
                    {
                        var label = scenario.GetProperty("name").GetString()!;
                        using var scenarioModel = NewModel(model.Name);
                        var engine = new ReactiveGraphEngine(scenarioModel, $"{name}[{label}]");
                        engine.Replay(scenario.GetProperty("steps"));
                        if (root.TryGetProperty("expected", out var expected))
                        {
                            var tail = FixtureAssertions.Wrap(expected, $"{Corpus}/{name}[{label}]");
                            // `observationally_equal` is evaluated once for the whole
                            // fixture below, not per scenario.
                            tail.MarkConsumed("observationally_equal");
                            observations[label] = engine.ReplayTail(tail);
                        }
                        observed.AddRange(engine.Divergences.Select(d => $"{model.Name}/{d}"));
                        checks += engine.Checks;
                    }

                    if (root.TryGetProperty("expected", out var exp) &&
                        exp.TryGetProperty("observationally_equal", out var pair))
                    {
                        var labels = pair.EnumerateArray().Select(x => x.GetString()!).ToList();
                        for (var i = 1; i < labels.Count; i++)
                        {
                            if (!observations[labels[0]].SequenceEqual(observations[labels[i]], StringComparer.Ordinal))
                            {
                                observed.Add(
                                    $"{model.Name}/{name}#-1:observationally_equal — {labels[0]} vs {labels[i]}: " +
                                    $"[{string.Join(" | ", observations[labels[0]])}] vs " +
                                    $"[{string.Join(" | ", observations[labels[i]])}]");
                            }
                            checks++;
                        }
                    }
                    break;
                }

            case var other:
                throw new NotSupportedException($"{name}: unknown fixture shape {other}");
        }

        return checks;
    }

    /// <summary>
    /// The on-disk corpus and the declared ledgers agree: every fixture is either replayed or
    /// explicitly declared unsupported with a reason, and no ledger entry names a fixture or a
    /// model that does not exist.
    /// </summary>
    [Fact]
    public void LedgerMatchesTheCorpusOnDisk()
    {
        Assert.True(SpecCorpus.Root is not null,
            $"lazily-spec checkout absent at {SpecCorpus.SiblingRelativePath} — clone it beside this repo. " +
            "This is a hard failure, not a skip: a runner that quietly skips its whole corpus is " +
            "indistinguishable from a green one.");

        var onDisk = SpecCorpus.FixtureNames(Corpus).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(onDisk);

        var ghosts = Unsupported.Keys.Where(k => !onDisk.Contains(k)).ToList();
        Assert.True(ghosts.Count == 0, $"unsupported ledger names fixtures not on disk: {string.Join(", ", ghosts)}");

        foreach (var (fixture, reason) in Unsupported)
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{fixture}: unsupported entries must state why");

        var modelNames = Models().Select(m =>
        {
            using (m) return m.Name;
        }).ToHashSet(StringComparer.Ordinal);

        foreach (var (key, reason) in ModelUnsupported)
        {
            var slash = key.IndexOf('/', StringComparison.Ordinal);
            Assert.True(slash > 0, $"per-model ledger key {key} must be `model/fixture`");
            var model = key[..slash];
            var fixture = key[(slash + 1)..];
            Assert.True(modelNames.Contains(model), $"per-model ledger names unknown model {model}");
            Assert.True(onDisk.Contains(fixture), $"per-model ledger names fixture not on disk: {fixture}");
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{key}: per-model entries must state why");
        }
    }

    private static string Key(string divergence)
    {
        var dash = divergence.IndexOf(" — ", StringComparison.Ordinal);
        return dash < 0 ? divergence : divergence[..dash];
    }
}
