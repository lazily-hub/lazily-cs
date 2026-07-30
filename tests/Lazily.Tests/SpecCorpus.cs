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

    private static string? Locate()
    {
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
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, corpus, fixture)));
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
