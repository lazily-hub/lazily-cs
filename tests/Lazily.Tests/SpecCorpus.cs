using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lazily.Tests;

/// <summary>
/// Resolves the canonical lazily-spec conformance corpus.
/// </summary>
/// <remarks>
/// Fixtures are NEVER vendored into this repo — a bundled copy drifts from the spec. The corpus
/// is resolved through one sibling-relative path, and CI guards that the checkout is present so
/// "green" and "ran nothing" cannot be confused. Every runner that consumes it also asserts a
/// positive fixture count, because a skip-if-absent runner with no guard is worse than no runner.
/// </remarks>
public static class SpecCorpus
{
    /// <summary>The sibling checkout path, relative to the repository root.</summary>
    public const string SiblingRelativePath = "../lazily-spec/conformance";

    private static readonly Lazy<string?> RootLazy = new(Locate);

    /// <summary>The absolute conformance directory, or null when the sibling checkout is absent.</summary>
    public static string? Root => RootLazy.Value;

    /// <summary>
    /// The override <c>scripts/check-conformance-coverage.sh</c> already reads, honoured here so
    /// the suite and the guard auditing it can never be pointed at two different corpora.
    /// </summary>
    /// <remarks>
    /// Without this, a corpus-perturbation probe — flip one assertion key, confirm the suite
    /// reddens — had nowhere to point but the shared <c>../lazily-spec</c> checkout itself, and
    /// editing that reddens all nine bindings at once, so the probe could not be run at all. A
    /// vacuous assertion is only visible when the fixture can be changed under the runner.
    /// </remarks>
    public const string DirOverrideVar = "LAZILY_SPEC_CONFORMANCE_DIR";

    private static string? Locate()
    {
        var overridden = Environment.GetEnvironmentVariable(DirOverrideVar);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            // Fail closed: an override naming a missing directory is a wrong invocation, not a
            // reason to silently replay the canonical corpus and report green against it.
            var resolved = Path.GetFullPath(overridden);
            if (!Directory.Exists(resolved))
            {
                throw new DirectoryNotFoundException(
                    $"{DirOverrideVar}={overridden} names no directory ({resolved}); " +
                    "refusing to fall back to the sibling corpus.");
            }
            return resolved;
        }

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, SiblingRelativePath));
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>Every <c>*.json</c> fixture in <paramref name="corpus"/>, sorted by file name.</summary>
    /// <param name="corpus">The corpus directory name, e.g. "reactive-graph".</param>
    public static IReadOnlyList<string> FixtureNames(string corpus)
    {
        var dir = Root is null ? null : Path.Combine(Root, corpus);
        if (dir is null || !Directory.Exists(dir)) return [];
        return [.. Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];
    }

    /// <summary>Loads and parses one fixture.</summary>
    /// <param name="corpus">The corpus directory name.</param>
    /// <param name="fixture">The fixture file name.</param>
    public static JsonDocument Load(string corpus, string fixture)
    {
        ArgumentNullException.ThrowIfNull(Root);
        Record(corpus, fixture);
        var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, corpus, fixture)));
        // Rung 0 (#lznullformblind): inventory the assertion blocks these bytes
        // carry, at READ time. Every rung above is scoped to a block a runner
        // BOUND, so reading the file is the only moment the corpus's full set of
        // blocks is in hand.
        RecordDeclaredBlocks(Key(corpus, fixture), document.RootElement);
        return document;
    }

    /// <summary>The fixture's <c>scenarios</c>, wrapped in the replay ledger.</summary>
    /// <param name="root">The fixture root element.</param>
    /// <param name="corpus">The corpus directory name, as passed to <see cref="Load"/>.</param>
    /// <param name="fixture">The fixture file name, as passed to <see cref="Load"/>.</param>
    /// <remarks>
    /// Throws when the fixture carries no <c>scenarios</c> array, exactly as
    /// <c>GetProperty("scenarios")</c> did. Use <see cref="TryScenarios"/> for a corpus whose
    /// fixtures are a mix of the two shapes.
    /// </remarks>
    public static ScenarioSet Scenarios(JsonElement root, string corpus, string fixture) =>
        new(root.GetProperty("scenarios"), Key(corpus, fixture));

    /// <summary><see cref="Scenarios"/> for a fixture that may carry no scenarios at all.</summary>
    public static bool TryScenarios(
        JsonElement root,
        string corpus,
        string fixture,
        out ScenarioSet scenarios)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("scenarios", out var element)
            && element.ValueKind == JsonValueKind.Array)
        {
            scenarios = new ScenarioSet(element, Key(corpus, fixture));
            return true;
        }

        scenarios = new ScenarioSet(default, Key(corpus, fixture));
        return false;
    }

    // -- Runtime conformance manifest (#lazilyupgradeconformance) --------------
    //
    // This binding discovers fixtures by enumerating the corpus directory, so no
    // fixture filename ever appears literally in its sources. A static grep over
    // the test sources therefore reports almost the whole corpus as uncovered,
    // which is not a measurement of anything — it is an artifact of how the loader
    // works. Only recording the actual read says what was replayed.
    //
    // Every load funnels through here, which is why cs needs no per-call-site
    // edits: the seam the other compiled bindings have to build already exists.
    //
    // Records to LAZILY_CONFORMANCE_MANIFEST; a no-op when unset, so a bare
    // `dotnet test` is unaffected. Appends at process exit because the manifest is
    // a union across however many test processes run.
    private static readonly object ManifestGate = new();
    private static readonly SortedSet<string> Opened = new(StringComparer.Ordinal);
    private static bool _flushRegistered;

    private static string Key(string corpus, string fixture) =>
        string.IsNullOrEmpty(corpus) ? fixture : corpus + "/" + fixture;

    private static void Record(string corpus, string fixture) => Append(Key(corpus, fixture));

    // -- Runtime scenario ledger (#lzscenariocoverage) --------------------------
    //
    // Rides in the SAME manifest as the fixture record above, one evidence file and
    // one env var, distinguished by a TAB: a bare line is "this fixture was opened",
    // a `fixture<TAB>id` line is "this scenario was replayed". The coverage guard
    // matches opened fixtures with `grep -qxF` on the whole line, so a suffixed
    // scenario line can never be mistaken for a fixture record.
    //
    // Called from ScenarioSet at the point a runner actually reaches a scenario —
    // never from a bookkeeping read, because naming a scenario is not replaying it.
    internal static void RecordScenario(string fixture, string id) => Append(fixture + "\t" + id);

    // -- Runtime prose-verification ledger (#lzprosekeyconvention) --------------
    //
    // Rule 8: rules 1-7 are all satisfied over an EMPTY population, so a fixture
    // that is opened and then never replayed passes every one of them while proving
    // nothing — the vacuity `anti_vacuity` exists to name, reappearing in the guard
    // meant to enforce it. Nothing inside a test that never ran can report that, so
    // the fact rides out in the same manifest and the guard derives the REQUIRED set
    // from the corpus: every fixture whose `assertions` declares `prose` must appear
    // here, and no hand-kept count is consulted.
    //
    // The marker is a PREFIX, not a suffix: a `fixture<TAB>id` line already means
    // "this scenario was replayed", so a trailing marker would be read as a scenario
    // id the fixture does not carry.
    internal const string ProseVerifiedMarker = "prose-verified";

    internal static void RecordProseVerified(string corpus, string fixture) =>
        Append(ProseVerifiedMarker + "\t" + Key(corpus, fixture));

    // -- Rung 0: the assertion-block BIND ledger (#lznullformblind) -------------
    //
    // Every rung above this one is scoped to a block a runner already BOUND to a
    // FixtureAssertions tracker. The unconsumed-key check fires on a key nothing
    // read; the read-but-not-asserted check on a key read and discarded; the prose
    // ledger on a discharge naming nothing. NONE of them can fire for a block no
    // runner ever bound, because there is no tracker: its keys are not unread,
    // nothing reads them, and the fixture reports exactly nothing. lazily-dart
    // found two such blocks carrying eight silent keys, one of them the anti-spoof
    // invariant its fixture exists for; lazily-cpp found a third; lazily-zig found
    // twenty-two.
    //
    // The two sides are matched by the block's CONTENT digest, never by its `where`
    // label: runners spell those inconsistently (`assertions`, `frames[3] warn`,
    // `scenarios[2].expect`) and a label-keyed ledger would silently miss the
    // mismatch rather than report it.
    //
    // Both channels ride the SAME manifest as everything above, under `blocks-`
    // prefixes — no second file, no second env var, and no second CI wiring.
    internal const string BlockDeclaredMarker = "blocks-declared";
    internal const string BlockBoundMarker = "blocks-bound";

    /// <summary>Book an assertion block as BOUND. Called from the FixtureAssertions ctor.</summary>
    internal static void RecordBlockBind(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object) return;
        Append(BlockBoundMarker + "\t" + BlockDigest(block));
    }

    private static void RecordDeclaredBlocks(string fixtureKey, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("assertions", out var top)) Declare(fixtureKey, "assertions", top);
        foreach (var container in new[] { "frames", "scenarios", "rejects" })
        {
            if (!root.TryGetProperty(container, out var items)) continue;
            if (items.ValueKind != JsonValueKind.Array) continue;
            var index = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("assertions", out var block))
                {
                    Declare(fixtureKey, $"{container}[{index}].assertions", block);
                }

                index++;
            }
        }
    }

    private static void Declare(string fixtureKey, string where, JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object) return;
        Append(BlockDeclaredMarker + "\t" + fixtureKey + "\t" + BlockDigest(block) + "\t" + where);
    }

    /// <summary>
    /// FNV-1a over a structural walk of the block: a content key, so a block is booked by
    /// what it SAYS rather than by what a runner chose to call it.
    /// </summary>
    /// <remarks>
    /// Walked rather than hashed off <c>GetRawText()</c> on purpose: raw text carries the
    /// fixture's whitespace, so a runner that binds a block it rebuilt or re-serialized would
    /// digest differently and be reported unbound for a formatting difference. Type tags are
    /// folded in so <c>1</c> and <c>"1"</c> cannot collide, and numbers are folded by their
    /// raw lexical form, which is what both sides read out of the same file.
    /// </remarks>
    private static string BlockDigest(JsonElement value)
    {
        var hash = 0xcbf29ce484222325UL;
        Hash(ref hash, value);
        return hash.ToString("x16", CultureInfo.InvariantCulture);

        static void Feed(ref ulong hash, string text)
        {
            foreach (var b in Encoding.UTF8.GetBytes(text))
            {
                hash ^= b;
                hash *= 0x100000001b3UL;
            }
        }

        static void Hash(ref ulong hash, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    Feed(ref hash, "{");
                    foreach (var property in element.EnumerateObject())
                    {
                        Feed(ref hash, property.Name);
                        Feed(ref hash, "=");
                        Hash(ref hash, property.Value);
                    }

                    Feed(ref hash, "}");
                    break;
                case JsonValueKind.Array:
                    Feed(ref hash, "[");
                    foreach (var item in element.EnumerateArray()) Hash(ref hash, item);
                    Feed(ref hash, "]");
                    break;
                case JsonValueKind.String:
                    Feed(ref hash, "s");
                    Feed(ref hash, element.GetString() ?? string.Empty);
                    break;
                case JsonValueKind.Number:
                    Feed(ref hash, "n");
                    Feed(ref hash, element.GetRawText());
                    break;
                case JsonValueKind.True:
                    Feed(ref hash, "b1");
                    break;
                case JsonValueKind.False:
                    Feed(ref hash, "b0");
                    break;
                default:
                    Feed(ref hash, "z");
                    break;
            }
        }
    }

    private static void Append(string line)
    {
        var manifest = Environment.GetEnvironmentVariable("LAZILY_CONFORMANCE_MANIFEST");
        if (string.IsNullOrEmpty(manifest)) return;
        lock (ManifestGate)
        {
            Opened.Add(line);
            if (_flushRegistered) return;
            _flushRegistered = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(manifest);
        }
    }

    private static void Flush(string manifest)
    {
        lock (ManifestGate)
        {
            if (Opened.Count == 0) return;
            try
            {
                File.AppendAllLines(manifest, Opened);
            }
            catch (IOException)
            {
                // A manifest we cannot write shows up downstream as missing
                // evidence, which is the correct outcome. Never fail a suite over
                // bookkeeping.
            }
        }
    }
}
