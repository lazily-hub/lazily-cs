using System.Text.RegularExpressions;
using RegexMatch = System.Text.RegularExpressions.Match;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The run-id stamp prefix has TWO sides, and they must be the same string
/// (<c>#lzstampprefixdrift</c>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SpecCorpus.RunIdMarker"/> is the PRODUCER's spelling: the recorder writes
/// <c>&lt;marker&gt; &lt;run id&gt;</c> into the evidence manifest from inside the
/// <c>dotnet test</c> host. <c>scripts/check-conformance-coverage.sh</c> is the CONSUMER, and it
/// holds its own copy — twice, once for the bash rung and once for the <c>PY_DIAG</c> prelude the
/// block and scenario legs share. Nothing but a comment held the three together.
/// </para>
/// <para>
/// A drift fails CLOSED — the guard finds no stamp it recognises and refuses — so the hazard is
/// not a vacuous green. It is the DIAGNOSTIC: a one-character typo in either spelling presents as
/// "<c>build/conformance-fixtures-loaded.txt</c> carries no stamp line", which reads as a stale
/// manifest or a detached recorder and sends the reader to the recorder, the Makefile and CI
/// before the prefix. This test names the real fault instead.
/// </para>
/// <para>
/// Both sides are read from their REAL definitions — the C# constant and the script's own
/// assignments. Restating the literal here would just add a fourth place to drift, which is the
/// shape being removed rather than the shape being added.
/// </para>
/// </remarks>
public sealed class RunIdStampPrefixTests
{
    private const string GuardScriptPath = "scripts/check-conformance-coverage.sh";

    /// <summary>
    /// Every <c>RUN_ID_MARKER</c> assignment in the guard, in bash's <c>NAME='value'</c> form and
    /// in python's <c>NAME = "value"</c> form. The name is the shared one deliberately: a leg that
    /// renames its variable stops being found, which the count floor below turns red.
    /// </summary>
    private static readonly Regex Definition = new(
        """^[ \t]*RUN_ID_MARKER[ \t]*=[ \t]*(?:'(?<v>[^']*)'|"(?<v>[^"]*)")[ \t]*$""",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture);

    /// <summary>
    /// The guard's spellings of the stamp prefix all equal the recorder's.
    /// </summary>
    /// <remarks>
    /// Goes red if <see cref="SpecCorpus.RunIdMarker"/> or either assignment in the guard changes
    /// by one character without the others following.
    /// </remarks>
    [Fact]
    public void GuardAndRecorderAgreeOnTheStampPrefix()
    {
        var (path, text) = ReadGuardScript();
        var found = Definition.Matches(text);

        // A floor, because every assertion below is over the matches the regex FOUND: a regex
        // that stopped matching — a reformatted assignment, a renamed variable, a leg deleted —
        // would report no disagreement over no definitions and print OK. Two is the real count,
        // asserted EXACTLY; a third copy is a third place to drift and belongs in this test's
        // face, not silently inside the set it happens to check.
        Assert.Equal(2, found.Count);

        foreach (RegexMatch match in found)
        {
            Assert.Equal(SpecCorpus.RunIdMarker, match.Groups["v"].Value);
        }

        // And no FOURTH spelling: every occurrence of the prefix anywhere in the guard must be one
        // of the assignments above. A rung that hardcoded the literal into an `awk`/`grep` pattern
        // instead of referencing the variable would agree with the recorder today and drift
        // tomorrow, invisibly to the equality above.
        var occurrences = CountOccurrences(text, SpecCorpus.RunIdMarker);
        Assert.Equal(found.Count, occurrences);
        Assert.Contains(SpecCorpus.RunIdMarker, text, StringComparison.Ordinal);

        // The path is in the message on purpose: this test reads a file outside the test host's
        // working directory, and "2 != 0" with no path is the fault this whole rung exists to stop
        // being.
        Assert.EndsWith(GuardScriptPath, path.Replace('\\', '/'), StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// The guard script, located by climbing out of the test host's output directory.
    /// </summary>
    /// <remarks>
    /// REFUSES rather than skipping when the file is not found. A test that silently passes when
    /// it cannot read the other side of the coupling is the vacuous green the conformance ladder
    /// spends its whole length rejecting.
    /// </remarks>
    private static (string Path, string Text) ReadGuardScript()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, GuardScriptPath));
            if (File.Exists(candidate)) return (candidate, File.ReadAllText(candidate));
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new FileNotFoundException(
            $"could not find '{GuardScriptPath}' climbing out of '{AppContext.BaseDirectory}'. " +
            "It holds the consumer side of the run-id stamp prefix, so without it this test " +
            "cannot compare the two spellings and must not report OK (#lzstampprefixdrift).");
    }
}
