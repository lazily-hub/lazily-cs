using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Replays the canonical <c>materialization</c> corpus against <see cref="SlotMap{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The laws under test are memory laws, not value laws, and that is what makes them easy to fake.
/// "Eager and lazy return the same values" passes trivially against a map that materializes
/// everything and ignores the strategy entirely — so every fixture also pins the PRESENT SET, which
/// is the only observable that separates deferral from eager allocation. A binding that pre-mints
/// under both strategies gets every <c>observe</c> assertion right and every <c>present</c>
/// assertion wrong.
/// </para>
/// <para>
/// Discipline carried over from <c>ReactiveGraphConformanceTests</c>: fixtures are never vendored,
/// an absent corpus is a hard failure rather than a skip, the on-disk set is asserted against the
/// replayed set in both directions, and a positive fixture/assertion floor proves the runner
/// actually executed rather than reporting green over an empty loop.
/// </para>
/// </remarks>
public sealed class MaterializationConformanceTests
{
    private const string Corpus = "materialization";

    /// <summary>
    /// Fixtures no strategy can execute, with the exact op or assertion that blocks each.
    /// </summary>
    /// <remarks>
    /// Empty today. An entry here would be a finding against lazily-cs, never a relaxed assertion —
    /// and the completeness assertion below subtracts exactly these, so a stale entry fails the
    /// build.
    /// </remarks>
    private static readonly Dictionary<string, string> Unsupported = [];

    /// <summary>Assertions this binding does not satisfy, keyed <c>fixture:key</c>.</summary>
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

        foreach (var name in names)
        {
            if (Unsupported.ContainsKey(name)) continue;

            using var doc = SpecCorpus.Load(Corpus, name);
            var fx = doc.RootElement;
            var spec = fx.GetProperty("spec");
            var expected = fx.GetProperty("expected");

            // `spec.val` is a flat key -> canonical value map; `spec.entries` additionally declares
            // each key's EntryKind. Both shapes describe the same thing, so both are normalized to
            // (key, kind, value) before anything is built.
            var entries = ReadEntries(spec);
            var keys = entries.Select(e => e.Key).ToArray();
            var canonical = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            var reads = fx.TryGetProperty("reads", out var r)
                ? r.EnumerateArray().Select(x => x.GetString()!).ToArray()
                : [];

            void Check(string key, object? got, object? want)
            {
                assertions++;
                if (!Equals(got?.ToString(), want?.ToString()))
                {
                    divergences.Add($"{name}:{key} — got {got}, want {want}");
                }
            }

            // ---- EAGER: a pre-mint loop over every key. -------------------------------------
            var eagerCtx = new Context();
            var eagerCells = new CellMap<string, int>(eagerCtx);
            var eagerSlots = new SlotMap<string, int>(eagerCtx);
            foreach (var e in entries)
            {
                if (e.Kind == EntryKind.Cell) eagerCells.Entry(e.Key, e.Value);
            }

            eagerSlots.MaterializeAll(
                entries.Where(e => e.Kind == EntryKind.Slot).Select(e => e.Key),
                k => canonical[k]);

            Check(
                "eager_present",
                Join(entries.Select(e => e.Key).Where(k => Present(eagerCells, eagerSlots, k))),
                Join(expected.GetProperty("eager_present").EnumerateArray().Select(x => x.GetString()!)));

            // ---- LAZY: mint-on-access, nothing pre-minted. ---------------------------------
            var lazyCtx = new Context();
            var lazyCells = new CellMap<string, int>(lazyCtx);
            var lazySlots = new SlotMap<string, int>(lazyCtx);

            // Cell entries are materialized at BUILD under every strategy — that is the
            // orthogonality the entry-kind fixture pins. Slot entries stay absent until read.
            foreach (var e in entries)
            {
                if (e.Kind == EntryKind.Cell) lazyCells.Entry(e.Key, e.Value);
            }

            if (expected.TryGetProperty("lazy_present_at_build", out var atBuild))
            {
                Check(
                    "lazy_present_at_build",
                    Join(keys.Where(k => Present(lazyCells, lazySlots, k))),
                    Join(atBuild.EnumerateArray().Select(x => x.GetString()!)));
            }

            // The read sequence, with the cumulative present-set size sampled after each read.
            // Monotone by construction and unchanged by a re-read — a map that re-minted on every
            // read would climb past the expected counts here while every value stayed right.
            var sizes = new List<int>();
            foreach (var key in reads)
            {
                _ = lazySlots.GetOrInsertWith(key, k => canonical[k]);
                sizes.Add(lazyCells.PresentCount + lazySlots.PresentCount);
            }

            if (expected.TryGetProperty("present_after_each_read", out var afterEach))
            {
                Check(
                    "present_after_each_read",
                    string.Join(",", sizes),
                    string.Join(",", afterEach.EnumerateArray().Select(x => x.GetInt32())));
            }

            Check(
                "lazy_present_after_reads",
                Join(keys.Where(k => Present(lazyCells, lazySlots, k))),
                Join(expected.GetProperty("lazy_present_after_reads").EnumerateArray().Select(x => x.GetString()!)));

            // ---- Observational transparency: identical values from both builds. -------------
            foreach (var want in expected.GetProperty("observe").EnumerateObject())
            {
                var key = want.Name;
                Check($"observe.eager.{key}", Observe(eagerCells, eagerSlots, key, canonical), want.Value.GetInt32());
                Check($"observe.lazy.{key}", Observe(lazyCells, lazySlots, key, canonical), want.Value.GetInt32());
            }

            // Materializing one node never changes another's observed value: after the lazy session
            // has pulled everything, every key still reads canonically.
            foreach (var key in keys)
            {
                Check($"materialize_preserves_observe.{key}", Observe(lazyCells, lazySlots, key, canonical), canonical[key]);
            }

            // The lazy present set is a SUBSET of the eager one — deferral only ever withholds.
            var eagerPresent = keys.Where(k => Present(eagerCells, eagerSlots, k)).ToHashSet(StringComparer.Ordinal);
            Check(
                "lazy_present_subset_eager",
                keys.Where(k => Present(lazyCells, lazySlots, k)).All(eagerPresent.Contains),
                true);

            replayed.Add(name);
        }

        // Two-directional ledger: everything on disk is either replayed or named as unsupported.
        Assert.Equal(
            names.Where(n => !Unsupported.ContainsKey(n)).Order(StringComparer.Ordinal).ToArray(),
            replayed.Order(StringComparer.Ordinal).ToArray());

        Assert.Equal(KnownDivergences.Values.Order(StringComparer.Ordinal).ToArray(), divergences.Order(StringComparer.Ordinal).ToArray());

        // Positive floor: the loop above must have actually run.
        Assert.NotEmpty(replayed);
        Assert.True(assertions > 0, "replayed the corpus but checked nothing");
    }

    private static string Join(IEnumerable<string> keys) => string.Join(",", keys);

    private static bool Present(CellMap<string, int> cells, SlotMap<string, int> slots, string key) =>
        cells.IsPresent(key) || slots.IsPresent(key);

    private static int Observe(
        CellMap<string, int> cells,
        SlotMap<string, int> slots,
        string key,
        IReadOnlyDictionary<string, int> canonical)
    {
        if (cells.TryObserve(key, out var cellValue)) return cellValue;
        return slots.GetOrInsertWith(key, k => canonical[k]);
    }

    private static List<(string Key, EntryKind Kind, int Value)> ReadEntries(JsonElement spec)
    {
        var entries = new List<(string, EntryKind, int)>();
        if (spec.TryGetProperty("entries", out var declared))
        {
            foreach (var e in declared.EnumerateObject())
            {
                var kind = e.Value.GetProperty("kind").GetString() == "cell" ? EntryKind.Cell : EntryKind.Slot;
                entries.Add((e.Name, kind, e.Value.GetProperty("val").GetInt32()));
            }

            return entries;
        }

        foreach (var e in spec.GetProperty("val").EnumerateObject())
        {
            entries.Add((e.Name, EntryKind.Slot, e.Value.GetInt32()));
        }

        return entries;
    }
}
