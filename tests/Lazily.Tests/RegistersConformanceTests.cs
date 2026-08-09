using System.Text.Json;
using Lazily;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Replays <c>collections/registers_convergence.json</c> against <see cref="LwwRegister{T}"/>,
/// <see cref="MvRegister{T}"/>, and <see cref="PnCounter"/>.
/// </summary>
/// <remarks>
/// <para>
/// The fixture is SCENARIO-shaped, not step- or reconcile-shaped, so it has its own runner rather
/// than riding <see cref="CollectionsConformanceTests"/>'s corpus enumeration — the same split
/// every other scenario-shaped fixture in the <c>collections</c> directory already uses.
/// </para>
/// <para>
/// Stamps are built DIRECTLY from the fixture's <c>(wall, logical, peer)</c> triple rather than
/// being drawn from <see cref="Hlc"/>. A clock would invent its own ordering, and the whole point
/// of <c>lww_equal_wall_and_logical_break_on_peer</c> is that the corpus pins the tiebreak: a
/// binding that compares only <c>(wall, logical)</c> has no order there and never converges. Let
/// the clock choose the numbers and that assertion stops being about anything.
/// </para>
/// <para>
/// <c>changed</c> is the boolean the library RETURNED from the last operation that reports one
/// (<c>Set</c> or <c>MergeFrom</c>) — never a value re-derived by comparing before and after. A
/// runner that re-derives it passes the fixture while the library hard-codes <c>true</c>, which is
/// exactly the CellCrdt projection defect the fixture exists to catch: an anti-entropy round that
/// carried nothing new must not invalidate the reactive cell's dependents.
/// </para>
/// </remarks>
public sealed class RegistersConformanceTests
{
    private const string Corpus = "collections";
    private const string Fixture = "registers_convergence.json";

    /// <summary>The root replica every scenario seeds and every <c>fork</c> branches from.</summary>
    private const string Root = "a";

    /// <summary>
    /// The scenario ids this runner claims. An id outside this set FAILS rather than being
    /// skipped: a scenario renamed or added upstream is the drift the corpus guards exist for,
    /// and a dispatch that shrugs at an unfamiliar id reports the same green as before.
    /// </summary>
    private static readonly IReadOnlySet<string> Recognised = new HashSet<string>(StringComparer.Ordinal)
    {
        "lww_highest_stamp_wins_in_both_directions",
        "lww_equal_wall_and_logical_break_on_peer",
        "lww_stale_write_is_dropped_and_reports_no_change",
        "lww_winning_merge_reports_change",
        "lww_merge_is_idempotent",
        "mv_concurrent_writes_are_both_retained",
        "mv_causal_write_supersedes_the_set",
        "pncounter_converges_and_merges_by_maximum",
        "pncounter_remerge_reports_no_change",
    };

    [Fact]
    public void ReplaysTheRegisterConvergenceFixtureAcrossAllThreeRegisterKinds()
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}; " +
            "clone lazily-spec as a sibling. A skip here would report green while testing nothing.");

        using var doc = SpecCorpus.Load(Corpus, Fixture);
        var scenarios = SpecCorpus.Scenarios(doc.RootElement, Corpus, Fixture);
        Assert.NotEqual(0, scenarios.Count);

        var replayed = new List<string>();
        var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var assertions = 0;

        foreach (var scenario in scenarios.All())
        {
            var id = scenario.Id;
            if (!Recognised.Contains(id))
            {
                throw new InvalidOperationException(
                    $"{Corpus}/{Fixture}: scenario '{id}' is not one this runner dispatches. " +
                    "Implement it, or record the gap in KNOWN_UNREPLAYED_SCENARIOS in " +
                    "scripts/check-conformance-coverage.sh — skipping it silently is the drift " +
                    "the scenario ledger exists to catch.");
            }

            var kind = scenario.GetProperty("register").GetString()!;
            var where = $"{Corpus}/{Fixture} scenario {id}";
            var expect = FixtureAssertions.Of(scenario, "expect", where);

            var changed = kind switch
            {
                "lww" => ReplayLww(scenario, expect, ref assertions),
                "mv" => ReplayMv(scenario, expect, ref assertions),
                "pncounter" => ReplayPnCounter(scenario, expect, ref assertions),
                _ => throw new InvalidOperationException(
                    $"{where}: unknown register kind '{kind}'."),
            };

            // The CellCrdt projection clause, asserted against the RETURNED boolean.
            var sawChanged = expect.TryAssertKeyWith(
                "changed",
                want =>
                {
                    Assert.True(
                        changed.HasValue,
                        $"{where}: `changed` is asserted, but no operation in this scenario " +
                        "reported one — the runner never captured a Set/MergeFrom result.");
                    Assert.Equal(want.GetBoolean(), changed!.Value);
                });
            if (sawChanged) assertions++;

            expect.Verify();
            replayed.Add(id);
            byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
        }

        Assert.Equal(
            Recognised.Order(StringComparer.Ordinal).ToArray(),
            replayed.Order(StringComparer.Ordinal).ToArray());

        // Floors PER KIND, not one total. A dispatch that replayed the five LWW scenarios and
        // silently no-opped the other two register kinds satisfies every assertion above,
        // because the assertions it skipped are the ones it never reached.
        Assert.Equal(
            "lww=5,mv=2,pncounter=2",
            string.Join(",", byKind.Select(entry => $"{entry.Key}={entry.Value}")));
        Assert.True(assertions >= 15, $"replayed the fixture but only checked {assertions} keys");
    }

    // -- LWW ------------------------------------------------------------------

    private static bool? ReplayLww(Scenario scenario, FixtureAssertions expect, ref int assertions)
    {
        var seed = scenario.GetProperty("seed");
        var replicas = Replay(
            scenario,
            new LwwRegister<string>(seed.GetProperty("value").GetString()!, StampOf(seed)),
            register => register.Copy(),
            (register, step) => step.GetProperty("op").GetString() switch
            {
                "set" => register.Set(step.GetProperty("value").GetString()!, StampOf(step)),
                var op => throw new InvalidOperationException($"unknown lww op '{op}'"),
            },
            (into, from) => into.MergeFrom(from),
            out var changed);

        var count = 0;
        expect.TryAssertObjectKey(
            "value_on",
            values =>
            {
                foreach (var replica in values.EnumerateObject().Select(p => p.Name))
                {
                    values.AssertKey(replica, replicas[replica].Value);
                    count++;
                }
            });

        expect.TryAssertObjectKey(
            "stamp_on",
            stamps =>
            {
                foreach (var replica in stamps.EnumerateObject().Select(p => p.Name))
                {
                    var stamp = replicas[replica].Stamp;
                    stamps.AssertObjectKey(
                        replica,
                        want =>
                        {
                            want.AssertKey("wall", stamp.Micros);
                            want.AssertKey("logical", stamp.Counter);
                            want.AssertKey("peer", stamp.Peer);
                        });
                    count += 3;
                }
            });

        assertions += count;
        return changed;
    }

    // -- MV -------------------------------------------------------------------

    private static bool? ReplayMv(Scenario scenario, FixtureAssertions expect, ref int assertions)
    {
        var replicas = Replay(
            scenario,
            new MvRegister<string>(),
            register => register.Copy(),
            (register, step) => step.GetProperty("op").GetString() switch
            {
                "set" => register.Set(
                    step.GetProperty("value").GetString()!,
                    step.GetProperty("peer").GetInt32()),
                var op => throw new InvalidOperationException($"unknown mv op '{op}'"),
            },
            (into, from) => into.MergeFrom(from),
            out var changed);

        var count = 0;
        expect.TryAssertObjectKey(
            "values_on",
            values =>
            {
                foreach (var replica in values.EnumerateObject().Select(p => p.Name))
                {
                    var got = replicas[replica].Values;
                    values.AssertKeyWith(
                        replica,
                        // Compared as a SET: the corpus deliberately pins no iteration order.
                        want => Assert.Equal(
                            want.EnumerateArray()
                                .Select(item => item.GetString())
                                .OrderBy(item => item, StringComparer.Ordinal)
                                .ToArray(),
                            got.OrderBy(item => item, StringComparer.Ordinal).ToArray()));
                    count++;
                }
            });

        assertions += count;
        return changed;
    }

    // -- PnCounter ------------------------------------------------------------

    private static bool? ReplayPnCounter(Scenario scenario, FixtureAssertions expect, ref int assertions)
    {
        var replicas = Replay(
            scenario,
            new PnCounter(),
            counter => counter.Copy(),
            (counter, step) =>
            {
                var peer = step.GetProperty("peer").GetInt32();
                var amount = step.TryGetProperty("amount", out var a) ? a.GetUInt64() : 1UL;
                var op = step.GetProperty("op").GetString();
                switch (op)
                {
                    case "incr": counter.Increment(peer, amount); break;
                    case "decr": counter.Decrement(peer, amount); break;
                    default: throw new InvalidOperationException($"unknown pncounter op '{op}'");
                }

                // Neither tally mutation reports an observable change; only MergeFrom does.
                return null;
            },
            (into, from) => into.MergeFrom(from),
            out var changed);

        var count = 0;
        expect.TryAssertObjectKey(
            "value_on",
            values =>
            {
                foreach (var replica in values.EnumerateObject().Select(p => p.Name))
                {
                    values.AssertKey(replica, replicas[replica].Value);
                    count++;
                }
            });

        assertions += count;
        return changed;
    }

    // -- Shared scenario driver -----------------------------------------------

    /// <summary>
    /// Drives one scenario's <c>steps</c> over a replica set, returning the replicas and the
    /// last <c>changed</c> boolean any operation reported.
    /// </summary>
    /// <remarks>
    /// <c>fork</c> branches from <see cref="Root"/> through the register's own <c>Copy()</c>.
    /// All three register types expose one, so no fork is synthesised by merging into a fresh
    /// instance — which would be wrong for <see cref="MvRegister{T}"/> anyway: merging into an
    /// empty replica reproduces the entries but the fork must also inherit the version-vector
    /// frontier those entries carry, and a merge-built copy only happens to agree because the
    /// entries ARE the frontier. <c>Copy()</c> states it instead of relying on that.
    /// </remarks>
    private static Dictionary<string, TReplica> Replay<TReplica>(
        Scenario scenario,
        TReplica root,
        Func<TReplica, TReplica> copy,
        Func<TReplica, JsonElement, bool?> apply,
        Func<TReplica, TReplica, bool> merge,
        out bool? changed)
    {
        var replicas = new Dictionary<string, TReplica>(StringComparer.Ordinal) { [Root] = root };
        changed = null;

        foreach (var step in scenario.GetProperty("steps").EnumerateArray())
        {
            if (step.TryGetProperty("fork", out var fork))
            {
                // The fork's `peer` names the new replica's identity. Every op in this corpus
                // carries its own `peer`, so it is read for shape rather than threaded through.
                _ = step.GetProperty("peer").GetInt32();
                replicas[fork.GetString()!] = copy(replicas[Root]);
                continue;
            }

            if (step.TryGetProperty("merge", out var mergeStep))
            {
                changed = merge(
                    replicas[mergeStep.GetProperty("into").GetString()!],
                    replicas[mergeStep.GetProperty("from").GetString()!]);
                continue;
            }

            if (step.TryGetProperty("on", out var on))
            {
                changed = apply(replicas[on.GetString()!], step);
                continue;
            }

            throw new InvalidOperationException(
                $"{Corpus}/{Fixture} scenario {scenario.Id}: step {step} matches no known kind " +
                "(fork / merge / on).");
        }

        return replicas;
    }

    /// <summary>Builds the fixture's <c>(wall, logical, peer)</c> triple directly.</summary>
    private static HlcStamp StampOf(JsonElement element) =>
        new(
            element.GetProperty("wall").GetInt64(),
            element.GetProperty("logical").GetInt64(),
            element.GetProperty("peer").GetInt32());
}
