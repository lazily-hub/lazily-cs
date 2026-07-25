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
/// </remarks>
public sealed class ReactiveGraphConformanceTests
{
    private const string Corpus = "reactive-graph";

    /// <summary>
    /// Fixtures this binding cannot execute, and the exact op or assertion that blocks it.
    /// </summary>
    /// <remarks>
    /// Entries are findings against Lazily's public surface, never relaxations of the fixtures:
    /// nothing here is skipped silently, and a fixture becoming executable fails the ledger
    /// assertion until it is promoted out of this map. Currently empty — the C# engine models the
    /// merge-fold write surface (a source carries its policy in a field) and the bounded feedback
    /// drain, so the whole on-disk corpus replays.
    /// </remarks>
    private static readonly Dictionary<string, string> Unsupported = [];

    /// <summary>
    /// Fixture assertions this binding does not satisfy, keyed <c>fixture#step:key</c>.
    /// </summary>
    /// <remarks>
    /// Each entry would be a finding against the implementation, never a relaxation of a fixture:
    /// the ledger asserts the observed set equals this one EXACTLY, so a new divergence fails the
    /// build and a fixed one fails it until its entry is deleted. Both directions are load-bearing.
    /// </remarks>
    private static readonly HashSet<string> KnownDivergences = [];

    /// <summary>Every fixture on disk replays, and every assertion in it holds.</summary>
    [Fact]
    public void ReplaysTheWholeCorpusWithNoUnexpectedDivergence()
    {
        Assert.True(SpecCorpus.Root is not null,
            $"lazily-spec checkout absent at {SpecCorpus.SiblingRelativePath} — clone it beside this repo. " +
            "This is a hard failure, not a skip: a runner that quietly skips its whole corpus is " +
            "indistinguishable from a green one.");

        var names = SpecCorpus.FixtureNames(Corpus);
        Assert.NotEmpty(names);

        var observed = new List<string>();
        var replayed = new List<string>();
        var totalChecks = 0;

        foreach (var name in names)
        {
            if (Unsupported.ContainsKey(name)) continue;
            replayed.Add(name);
            using var doc = SpecCorpus.Load(Corpus, name);
            var root = doc.RootElement;

            switch (root.GetProperty("shape").GetString())
            {
                case "steps":
                    {
                        var engine = new ReactiveGraphEngine(name);
                        engine.Replay(root.GetProperty("steps"));
                        observed.AddRange(engine.Divergences);
                        totalChecks += engine.Checks;
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
                            var engine = new ReactiveGraphEngine($"{name}[{label}]");
                            engine.Replay(scenario.GetProperty("steps"));
                            if (root.TryGetProperty("expected", out var expected))
                                observations[label] = engine.ReplayTail(expected);
                            observed.AddRange(engine.Divergences);
                            totalChecks += engine.Checks;
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
                                        $"{name}#-1:observationally_equal — {labels[0]} vs {labels[i]}: " +
                                        $"[{string.Join(" | ", observations[labels[0]])}] vs " +
                                        $"[{string.Join(" | ", observations[labels[i]])}]");
                                }
                                totalChecks++;
                            }
                        }
                        break;
                    }

                case var other:
                    throw new NotSupportedException($"{name}: unknown fixture shape {other}");
            }
        }

        // A positive assertion that the fixtures actually RAN. Without it, a corpus that resolved
        // to an empty directory would report green while testing nothing.
        Assert.NotEmpty(replayed);
        Assert.Equal(names.Count - Unsupported.Count, replayed.Count);
        Assert.True(
            totalChecks >= 200,
            $"only {totalChecks} assertions ran across {replayed.Count} fixtures — the corpus resolved but " +
            "barely asserted anything, which is the failure mode this floor exists to catch");

        var unexpected = observed.Where(d => !KnownDivergences.Contains(Key(d))).ToList();
        var fixedUp = KnownDivergences.Where(k => !observed.Any(d => Key(d) == k)).ToList();
        Assert.True(
            unexpected.Count == 0 && fixedUp.Count == 0,
            $"unexpected divergences:\n  {string.Join("\n  ", unexpected)}\n" +
            $"declared-but-absent divergences:\n  {string.Join("\n  ", fixedUp)}");
    }

    /// <summary>
    /// The on-disk corpus and the declared ledgers agree: every fixture is either replayed or
    /// explicitly declared unsupported with a reason, and no ledger entry names a fixture that is
    /// not on disk.
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
    }

    private static string Key(string divergence)
    {
        var dash = divergence.IndexOf(" — ", StringComparison.Ordinal);
        return dash < 0 ? divergence : divergence[..dash];
    }
}
