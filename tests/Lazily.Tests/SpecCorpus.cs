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
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, corpus, fixture)));
    }
}
