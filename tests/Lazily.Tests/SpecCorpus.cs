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

    private static void Record(string corpus, string fixture)
    {
        var manifest = Environment.GetEnvironmentVariable("LAZILY_CONFORMANCE_MANIFEST");
        if (string.IsNullOrEmpty(manifest)) return;
        lock (ManifestGate)
        {
            Opened.Add(string.IsNullOrEmpty(corpus) ? fixture : corpus + "/" + fixture);
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
