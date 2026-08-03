using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Replays the canonical <c>materialization</c> corpus against every keyed map this binding ships.
/// </summary>
/// <remarks>
/// <para>
/// The laws under test are memory laws, not value laws, and that is what makes them easy to fake.
/// "Eager and lazy return the same values" passes trivially against a map that materializes
/// everything and ignores the strategy entirely — so every fixture also pins the PRESENT SET, which
/// is the only observable that separates deferral from eager allocation. A map that pre-mints under
/// both strategies gets every <c>observe</c> assertion right and every <c>present</c> assertion
/// wrong.
/// </para>
/// <para>
/// The corpus runs against all three execution models rather than the easy one. A thread-safe or
/// async map that pre-mints where the synchronous one defers returns identical values at every key;
/// the divergence lives entirely in the present set, and only if something looks.
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

    /// <summary>Fixture/model pairs one model cannot run, keyed <c>model/fixture</c>.</summary>
    private static readonly Dictionary<string, string> ModelUnsupported = [];

    /// <summary>Assertions this binding does not satisfy, keyed <c>model/fixture:key</c>.</summary>
    private static readonly Dictionary<string, string> KnownDivergences = [];

    /// <summary>Every execution model this binding ships a keyed map on.</summary>
    public static TheoryData<string> Models() => ["Context", "ThreadSafeContext", "AsyncContext"];

    [Theory]
    [MemberData(nameof(Models))]
    public void ReplaysTheWholeCorpusWithNoUnexpectedDivergence(string model)
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
            if (Unsupported.ContainsKey(name) || ModelUnsupported.ContainsKey($"{model}/{name}")) continue;

            using var doc = SpecCorpus.Load(Corpus, name);
            var fx = doc.RootElement;
            var spec = fx.GetProperty("spec");
            var expected = FixtureAssertions.Of(fx, "expected", $"{Corpus}/{name} [{model}]");

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
                    divergences.Add($"{model}/{name}:{key} — got {got}, want {want}");
                }
            }

            // ---- EAGER: a pre-mint loop over every slot key. --------------------------------
            using var eager = NewModel(model);
            foreach (var e in entries.Where(e => e.Kind == EntryKind.Source)) eager.SeedCell(e.Key, e.Value);
            eager.MaterializeAll(entries.Where(e => e.Kind == EntryKind.Computed).Select(e => e.Key), k => canonical[k]);

            // `default_mode` names the strategy the fixture was authored against. Assert the
            // BEHAVIOUR it implies, not the label: under "eager" the pre-mint build must hold
            // every key at build time, under "lazy" it must not.
            expected.TryAssertKeyWith(
                "default_mode",
                defaultMode =>
                {
                    using var defaults = NewModel(model);
                    foreach (var e in entries.Where(e => e.Kind == EntryKind.Source)) defaults.SeedCell(e.Key, e.Value);
                    var presentAtBuild = keys.Count(defaults.IsPresent);
                    Check(
                        "default_mode",
                        defaultMode.GetString() switch
                        {
                            "eager" => presentAtBuild == entries.Count(e => e.Kind == EntryKind.Source),
                            "lazy" => presentAtBuild == 0,
                            var other => throw new InvalidOperationException($"unknown default_mode {other}"),
                        },
                        true);
                });

            expected.AssertKeyWith(
                "eager_present",
                want => Check(
                    "eager_present",
                    Join(keys.Where(eager.IsPresent)),
                    Join(want.EnumerateArray().Select(x => x.GetString()!))));

            // ---- LAZY: mint-on-access, nothing pre-minted. ---------------------------------
            using var lazy = NewModel(model);

            // Cell entries are materialized at BUILD under every strategy — that is the
            // orthogonality the entry-kind fixture pins. Slot entries stay absent until read.
            foreach (var e in entries.Where(e => e.Kind == EntryKind.Source)) lazy.SeedCell(e.Key, e.Value);

            expected.TryAssertKeyWith(
                "lazy_present_at_build",
                atBuild => Check(
                    "lazy_present_at_build",
                    Join(keys.Where(lazy.IsPresent)),
                    Join(atBuild.EnumerateArray().Select(x => x.GetString()!))));

            // The read sequence, with the cumulative present-set size sampled after each read.
            // Monotone by construction and unchanged by a re-read — a map that re-minted on every
            // read would climb past the expected counts here while every value stayed right.
            var sizes = new List<int>();
            foreach (var key in reads)
            {
                _ = lazy.GetOrInsert(key, k => canonical[k]);
                sizes.Add(lazy.PresentCount);
            }

            expected.TryAssertKeyWith(
                "present_after_each_read",
                afterEach => Check(
                    "present_after_each_read",
                    string.Join(",", sizes),
                    string.Join(",", afterEach.EnumerateArray().Select(x => x.GetInt32()))));

            expected.AssertKeyWith(
                "lazy_present_after_reads",
                want => Check(
                    "lazy_present_after_reads",
                    Join(keys.Where(lazy.IsPresent)),
                    Join(want.EnumerateArray().Select(x => x.GetString()!))));

            // ---- Observational transparency: identical values from both builds. -------------
            expected.AssertObjectKey(
                "observe",
                observe =>
                {
                    foreach (var entry in observe.EnumerateObject())
                    {
                        var key = entry.Name;
                        observe.AssertKeyWith(key, want =>
                        {
                            Check($"observe.eager.{key}", eager.GetOrInsert(key, k => canonical[k]), want.GetInt32());
                            Check($"observe.lazy.{key}", lazy.GetOrInsert(key, k => canonical[k]), want.GetInt32());
                        });
                    }
                });

            // Materializing one node never changes another's observed value: after the lazy session
            // has pulled everything, every key still reads canonically.
            foreach (var key in keys)
            {
                Check($"materialize_preserves_observe.{key}", lazy.GetOrInsert(key, k => canonical[k]), canonical[key]);
            }

            expected.Verify();
            replayed.Add(name);
        }

        // Two-directional ledger: everything on disk is either replayed or named as unsupported.
        Assert.Equal(
            names.Where(n => !Unsupported.ContainsKey(n) && !ModelUnsupported.ContainsKey($"{model}/{n}"))
                .Order(StringComparer.Ordinal).ToArray(),
            replayed.Order(StringComparer.Ordinal).ToArray());

        Assert.Equal(
            KnownDivergences.Values.Order(StringComparer.Ordinal).ToArray(),
            divergences.Order(StringComparer.Ordinal).ToArray());

        // Positive floor: the loop above must have actually run.
        Assert.NotEmpty(replayed);
        Assert.True(assertions > 0, "replayed the corpus but checked nothing");
    }

    private static string Join(IEnumerable<string> keys) => string.Join(",", keys);

    private static IMapModel NewModel(string model) => model switch
    {
        "Context" => new SyncMapModel(),
        "ThreadSafeContext" => new ThreadSafeMapModel(),
        "AsyncContext" => new AsyncMapModel(),
        _ => throw new InvalidOperationException($"unknown model {model}"),
    };

    private static List<(string Key, EntryKind Kind, int Value)> ReadEntries(JsonElement spec)
    {
        var entries = new List<(string, EntryKind, int)>();
        if (spec.TryGetProperty("entries", out var declared))
        {
            foreach (var e in declared.EnumerateObject())
            {
                entries.Add((e.Name, ParseKind(e.Value.GetProperty("kind").GetString()), e.Value.GetProperty("val").GetInt32()));
            }

            return entries;
        }

        foreach (var e in spec.GetProperty("val").EnumerateObject())
        {
            entries.Add((e.Name, EntryKind.Computed, e.Value.GetInt32()));
        }

        return entries;
    }

    /// <summary>
    /// Pins the kind parse itself: both corpus spellings map to the same entry kinds, and an
    /// unrecognized one throws instead of defaulting.
    /// </summary>
    /// <remarks>
    /// The corpus on disk exercises only <c>"cell"</c> / <c>"slot"</c> today, so the v2 spellings
    /// and the rejection path would otherwise be unexecuted code that reports green.
    /// </remarks>
    [Fact]
    public void ParsesBothEntryKindSpellingsAndRejectsAnyOther()
    {
        Assert.Equal(EntryKind.Source, ParseKind("cell"));
        Assert.Equal(EntryKind.Source, ParseKind("source"));
        Assert.Equal(EntryKind.Computed, ParseKind("slot"));
        Assert.Equal(EntryKind.Computed, ParseKind("computed"));

        Assert.Throws<InvalidOperationException>(() => ParseKind("signal"));
        Assert.Throws<InvalidOperationException>(() => ParseKind(null));
    }

    /// <summary>
    /// Maps a fixture's <c>kind</c> string to an <see cref="EntryKind"/>, accepting the pre-v2
    /// spelling and the v2 one.
    /// </summary>
    /// <remarks>
    /// The corpus still says <c>"cell"</c> / <c>"slot"</c>; the v2 kernel rename will flip it to
    /// <c>"source"</c> / <c>"computed"</c>. Both are accepted so the flip lands in lazily-spec
    /// without breaking this binding first. Anything else is a hard error rather than a default:
    /// the previous ternary treated every unrecognized kind as a slot, which would have replayed a
    /// renamed corpus green while silently seeding nothing.
    /// </remarks>
    /// <param name="kind">The fixture's raw kind string.</param>
    /// <returns>The entry kind it denotes.</returns>
    private static EntryKind ParseKind(string? kind) => kind switch
    {
        "cell" or "source" => EntryKind.Source,
        "slot" or "computed" => EntryKind.Computed,
        _ => throw new InvalidOperationException($"unknown entry kind {kind ?? "<null>"}"),
    };

    /// <summary>One execution model's keyed map, as the corpus needs to drive it.</summary>
    /// <remarks>
    /// The async model blocks internally so the engine stays synchronous — pushing the await into
    /// the one model that needs it keeps the other two free of an executor, exactly as the sibling
    /// runners do.
    /// </remarks>
    private interface IMapModel : IDisposable
    {
        void SeedCell(string key, int value);

        void MaterializeAll(IEnumerable<string> keys, Func<string, int> factory);

        int GetOrInsert(string key, Func<string, int> factory);

        bool IsPresent(string key);

        int PresentCount { get; }
    }

    private sealed class SyncMapModel : IMapModel
    {
        private readonly Context _ctx = new();
        private readonly SourceMap<string, int> _cells;
        private readonly ComputedMap<string, int> _slots;

        internal SyncMapModel()
        {
            _cells = new SourceMap<string, int>(_ctx);
            _slots = new ComputedMap<string, int>(_ctx);
        }

        public int PresentCount => _cells.PresentCount + _slots.PresentCount;

        public void SeedCell(string key, int value) => _cells.Entry(key, value);

        public void MaterializeAll(IEnumerable<string> keys, Func<string, int> factory) =>
            _slots.MaterializeAll(keys, factory);

        public int GetOrInsert(string key, Func<string, int> factory) =>
            _cells.TryObserve(key, out var v) ? v : _slots.GetOrInsertWith(key, factory);

        public bool IsPresent(string key) => _cells.IsPresent(key) || _slots.IsPresent(key);

        public void Dispose()
        {
        }
    }

    private sealed class ThreadSafeMapModel : IMapModel
    {
        private readonly ThreadSafeContext _ctx = new();
        private readonly ThreadSafeSourceMap<string, int> _cells;
        private readonly ThreadSafeComputedMap<string, int> _slots;

        internal ThreadSafeMapModel()
        {
            _cells = new ThreadSafeSourceMap<string, int>(_ctx);
            _slots = new ThreadSafeComputedMap<string, int>(_ctx);
        }

        public int PresentCount => _cells.PresentCount + _slots.PresentCount;

        public void SeedCell(string key, int value) => _cells.Entry(key, value);

        public void MaterializeAll(IEnumerable<string> keys, Func<string, int> factory) =>
            _slots.MaterializeAll(keys, factory);

        public int GetOrInsert(string key, Func<string, int> factory) =>
        _cells.TryObserve(key, out var value) ? value : _slots.GetOrInsertWith(key, factory);

        public bool IsPresent(string key) => _cells.IsPresent(key) || _slots.IsPresent(key);

        public void Dispose()
        {
        }
    }

    private sealed class AsyncMapModel : IMapModel
    {
        private readonly AsyncContext _ctx = new();
        private readonly AsyncSourceMap<string, int> _cells;
        private readonly AsyncComputedMap<string, int> _slots;

        internal AsyncMapModel()
        {
            _cells = new AsyncSourceMap<string, int>(_ctx);
            _slots = new AsyncComputedMap<string, int>(_ctx);
        }

        public int PresentCount => _cells.PresentCount + _slots.PresentCount;

        public void SeedCell(string key, int value) => _cells.Entry(key, value);

        public void MaterializeAll(IEnumerable<string> keys, Func<string, int> factory) =>
            _slots.MaterializeAll(keys, factory);

        public int GetOrInsert(string key, Func<string, int> factory) =>
        _cells.TryObserve(key, out var v)
        ? v
        : _slots.GetOrInsertWithAsync(key, factory).GetAwaiter().GetResult();

        public bool IsPresent(string key) => _cells.IsPresent(key) || _slots.IsPresent(key);

        public void Dispose() => _ctx.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
