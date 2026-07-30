using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Replays the canonical <c>familysync</c> corpus against <see cref="FamilySync{TValue}"/>.
/// </summary>
/// <remarks>
/// Two replicas, not one. Every scenario writes on the origin peer and asserts on the TARGET peer,
/// because the defect this plane exists to close is invisible from a single replica: a plain CRDT
/// plane drops a keyed op for an entry it never registered, so membership never propagates and the
/// origin's own view stays perfectly correct while every peer silently disagrees.
/// </remarks>
public sealed class FamilySyncConformanceTests
{
    private const string Corpus = "familysync";

    /// <summary>Fixtures this binding cannot execute, with the surface that blocks each.</summary>
    private static readonly Dictionary<string, string> Unsupported = [];

    /// <summary>Assertions this binding does not satisfy, keyed <c>fixture/scenario:key</c>.</summary>
    private static readonly Dictionary<string, string> KnownDivergences = [];

    [Fact]
    public void ReplaysTheWholeCorpusWithNoUnexpectedDivergence()
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}; " +
            "clone lazily-spec as a sibling. A skip here would report green while testing nothing.");

        var names = SpecCorpus.FixtureNames(Corpus);
        Assert.NotEmpty(names);

        var replayed = new List<string>();
        var divergences = new List<string>();
        var assertions = 0;
        var scenarios = 0;

        foreach (var name in names)
        {
            if (Unsupported.ContainsKey(name)) continue;

            using var doc = SpecCorpus.Load(Corpus, name);
            var fx = doc.RootElement;
            var ns = fx.GetProperty("namespace").GetString()!;

            foreach (var scenario in fx.GetProperty("scenarios").EnumerateArray())
            {
                var label = scenario.GetProperty("name").GetString()!;

                void Check(string key, object? got, object? want)
                {
                    assertions++;
                    if (!Equals(got?.ToString(), want?.ToString()))
                    {
                        divergences.Add($"{name}/{label}:{key} — got {got}, want {want}");
                    }
                }

                var ctx = new Context();
                var origin = new FamilySync<bool>(ctx, scenario.GetProperty("origin_peer").GetInt32());
                var target = new FamilySync<bool>(ctx, scenario.GetProperty("target_peer").GetInt32());
                origin.RegisterFamily(ns);
                target.RegisterFamily(ns);

                // A live aggregate on the TARGET, wired before any op arrives. Counting after the
                // fact would prove the runner can scan a dictionary; the fixture's claim is that a
                // derived count converges, which only means something if the derivation predates
                // the sync.
                var countTrue = target.Aggregate(ns, v => v);
                var aggregateRuns = 0;
                var observed = ctx.Slot(c =>
                {
                    aggregateRuns++;
                    return countTrue.Get(c);
                });
                _ = observed.Get();

                var epochBefore = target.MembershipEpoch.Peek();

                var ops = new List<FamilyOp<bool>>();
                foreach (var set in scenario.GetProperty("origin_sets").EnumerateArray())
                {
                    var op = origin.Set(
                        ns,
                        set.GetProperty("key").GetString()!,
                        set.GetProperty("value").GetBoolean(),
                        set.GetProperty("now").GetInt64());
                    if (op is { } produced) ops.Add(produced);
                }

                foreach (var op in ops) target.Ingest(op);

                var expect = FixtureAssertions.Of(
                    scenario,
                    "expect",
                    $"{Corpus}/{name} scenario {scenario.GetProperty("name").GetString()}");

                if (scenario.TryGetProperty("reingest", out var reingest) && reingest.GetBoolean())
                {
                    // Idempotence, asserted on the APPLY COUNT rather than on the resulting state:
                    // a binding that re-applied every op would land on the same values, because the
                    // ops are the same. Only the count separates "converged" from "idempotent".
                    //
                    // This used to build a THROWAWAY tracker to read the count out of, and mark
                    // the key consumed on the real one — so the real tracker saw a read that never
                    // reached a comparison. The count is now asserted through the tracker that
                    // verifies the scenario.
                    var applied = ops.Count(target.Ingest);
                    expect.AssertKeyWith(
                        "reingest_applied",
                        want => Check("reingest_applied", applied, want.GetInt32()));
                }

                expect.AssertKeyWith(
                    "target_keys",
                    want => Check(
                        "target_keys",
                        string.Join(",", target.Keys(ns)),
                        string.Join(",", want.EnumerateArray().Select(x => x.GetString()!))));

                expect.AssertKeyWith(
                    "target_values",
                    wants =>
                    {
                        foreach (var want in wants.EnumerateObject())
                        {
                            Check(
                                $"target_values.{want.Name}",
                                target.TryValue(ns, want.Name, out var v) ? v : null,
                                want.Value.GetBoolean());
                        }
                    });

                expect.AssertKeyWith(
                    "target_present_count",
                    want => Check("target_present_count", target.PresentCount(ns), want.GetInt32()));
                expect.AssertKeyWith(
                    "target_count_true",
                    want => Check("target_count_true", observed.Get(), want.GetInt32()));

                expect.TryAssertKeyWith(
                    "target_epoch_bumped",
                    bumped => Check(
                        "target_epoch_bumped",
                        target.MembershipEpoch.Peek() > epochBefore,
                        bumped.GetBoolean()));

                // The aggregate must be a real derived: it recomputed because membership changed,
                // not because the assertion asked for it.
                Check("aggregate_is_reactive", aggregateRuns > 1, true);

                expect.Verify();
                scenarios++;
            }

            replayed.Add(name);
        }

        Assert.Equal(
            names.Where(n => !Unsupported.ContainsKey(n)).Order(StringComparer.Ordinal).ToArray(),
            replayed.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            KnownDivergences.Values.Order(StringComparer.Ordinal).ToArray(),
            divergences.Order(StringComparer.Ordinal).ToArray());
        Assert.NotEmpty(replayed);
        Assert.True(scenarios > 0, "replayed fixtures but no scenario");
        Assert.True(assertions > 0, "replayed scenarios but checked nothing");
    }
}
