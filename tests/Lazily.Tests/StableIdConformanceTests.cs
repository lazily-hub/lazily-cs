using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Replays <c>collections/stableid_alignment.json</c> against <see cref="StableId"/>.
/// </summary>
/// <remarks>
/// The corpus exercises all three identity layers and, more importantly, the boundary between them:
/// a content key must survive reflow but NOT a real edit, an anchored key must survive a complete
/// body rewrite, a pure reorder must come back as all-Same with nothing removed, and a one-word
/// change must read as Edited rather than as a delete plus an insert. That last one is the whole
/// reason the similarity layer exists — the alternative discards every piece of state keyed to the
/// block a user barely touched.
/// </remarks>
public sealed class StableIdConformanceTests
{
    private const string Corpus = "collections";
    private const string Fixture = "stableid_alignment.json";

    [Fact]
    public void ReplaysTheStableIdFixtureWithNoDivergence()
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}; " +
            "clone lazily-spec as a sibling. A skip here would report green while testing nothing.");

        using var doc = SpecCorpus.Load(Corpus, Fixture);
        var divergences = new List<string>();
        var assertions = 0;
        var scenarios = 0;

        foreach (var scenario in SpecCorpus.Scenarios(doc.RootElement, Corpus, Fixture).All())
        {
            var name = scenario.GetProperty("name").GetString()!;
            var expect = FixtureAssertions.Of(scenario, "expect", $"{Corpus}/{Fixture} scenario {name}");

            void Check(string key, object? got, object? want)
            {
                assertions++;
                if (!Equals(got?.ToString(), want?.ToString())) divergences.Add($"{name}:{key} — got {got}, want {want}");
            }

            // Shape A: a flat block list, asserting which manufactured keys collide and which do not.
            if (scenario.TryGetProperty("blocks", out var blocksEl))
            {
                var blocks = ReadBlocks(blocksEl);
                var keys = blocks.Select(b => StableId.KeyOf(b).ToString()).ToArray();

                expect.TryAssertKeyWith(
                    "key_equal",
                    equalPairs =>
                    {
                        foreach (var pair in equalPairs.EnumerateArray())
                        {
                            var (i, j) = (pair[0].GetInt32(), pair[1].GetInt32());
                            Check($"key_equal[{i},{j}]", keys[i] == keys[j], true);
                        }
                    });

                expect.TryAssertKeyWith(
                    "key_not_equal",
                    notEqualPairs =>
                    {
                        foreach (var pair in notEqualPairs.EnumerateArray())
                        {
                            var (i, j) = (pair[0].GetInt32(), pair[1].GetInt32());
                            Check($"key_not_equal[{i},{j}]", keys[i] == keys[j], false);
                        }
                    });

                expect.Verify();
                scenarios++;
                continue;
            }

            // Shape B: old/new sequences, asserting the alignment itself.
            var oldBlocks = ReadBlocks(scenario.GetProperty("old"));
            var newBlocks = ReadBlocks(scenario.GetProperty("new"));

            if (expect.TryGetProperty("matches", out _))
            {
                var alignment = StableId.Align(oldBlocks, newBlocks);
                expect.AssertKeyWith(
                    "matches",
                    wantMatches => Check(
                        "matches",
                        string.Join(",", alignment.NewMatches.Select(m => m.ToString())),
                        string.Join(",", wantMatches.EnumerateArray().Select(x => x.GetString()!))));
                expect.AssertKeyWith(
                    "removed",
                    want => Check(
                        "removed",
                        string.Join(",", alignment.Removed),
                        string.Join(",", want.EnumerateArray().Select(x => x.GetInt32()))));

                // Asserted as a FLOOR on the edit itself, not just on the label: a binding that
                // returned Edited with a similarity under the threshold would be labelling by
                // accident rather than by measurement.
                expect.TryAssertKeyWith(
                    "similarity_min",
                    simMin =>
                    {
                        var edited = alignment.NewMatches.Where(m => m.Kind is MatchKind.Edited).ToList();
                        Check("similarity_min.any_edited", edited.Count > 0, true);
                        Check("similarity_min", edited.All(m => m.Similarity >= simMin.GetDouble()), true);
                    });
            }

            expect.TryAssertKeyWith(
                "new_key_equals_old_key",
                flowPairs =>
                {
                    var assigned = StableId.AssignStableKeys(oldBlocks, newBlocks);
                    foreach (var pair in flowPairs.EnumerateArray())
                    {
                        var (ni, oi) = (pair[0].GetInt32(), pair[1].GetInt32());
                        Check(
                            $"new_key_equals_old_key[{ni},{oi}]",
                            assigned[ni],
                            StableId.KeyOf(oldBlocks[oi]).ToString());
                    }
                });

            expect.Verify();
            scenarios++;
        }

        Assert.Equal(Array.Empty<string>(), divergences.Order(StringComparer.Ordinal).ToArray());
        Assert.True(scenarios > 0, "loaded the fixture but replayed no scenario");
        Assert.True(assertions > 0, "replayed scenarios but checked nothing");
    }

    private static List<Block> ReadBlocks(JsonElement el) =>
    [
        .. el.EnumerateArray().Select(b => new Block(
            b.GetProperty("text").GetString()!,
            b.TryGetProperty("anchor", out var a) ? a.GetString() : null)),
    ];
}
