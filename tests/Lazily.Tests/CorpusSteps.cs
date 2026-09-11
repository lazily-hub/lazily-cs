using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The constant-free replacement for a hand-maintained per-fixture step count
/// (<c>#lzcorpusfloorguard</c>).
/// </summary>
/// <remarks>
/// <para>
/// Runners here used to pin how many steps a canonical fixture carries — <c>Assert.Equal(14,
/// steps.GetArrayLength())</c>, <c>Assert.Equal(29, steps)</c>, <c>steps &gt;= 18</c>. Every one of
/// those numbers is a drift clock: it has to be re-pinned by hand each time the corpus moves, and
/// nothing reminds you.
/// </para>
/// <para>
/// The incident that settled it: lazily-spec grew <c>replay/canonical_encoding_equality.json</c>
/// from 11 steps to 14, and nothing in ten repositories noticed. <c>MIN_FIXTURES</c> counts files
/// and <c>MIN_SCENARIOS</c> counts declared scenarios; a step is neither. What each binding kept
/// instead was a per-fixture minimum-steps FLOOR hard-coded inside its own runner, and eight of
/// nine were still pinned at <c>&gt;= 11</c> — so the three new rows sat inside the slack and
/// reported green WITHOUT EVER EXECUTING. This repository was the only one that failed loudly, and
/// only because it had written an EQUALITY rather than a floor. That was the right instinct; it was
/// still the same drift clock with a shorter fuse.
/// </para>
/// <para>
/// So the number is gone. A runner asserts instead that every step it LOADED was EXECUTED, counted
/// inside the dispatch loop. That is exact, carries no constant, and can never drift: a corpus that
/// GROWS can no longer slip rows past a runner unexecuted. Paired with it, an unrecognized op type
/// is a hard failure everywhere in this project — never a silent <c>continue</c> or a
/// <c>default:</c> that does nothing — so "executed" cannot mean "reached the loop body and did
/// nothing".
/// </para>
/// <para>
/// The one thing a floor did do, notice the corpus SHRINKING, now lives upstream at the single
/// place a shrink can happen: lazily-spec's <c>corpus-counts.json</c> pins the corpus's own
/// per-fixture step counts and <c>scripts/check-corpus-floors.mjs</c> fails when a fixture's step
/// count changes without that pin moving. Deleting a step is a two-file change and visible in
/// review; here it would be invisible.
/// </para>
/// </remarks>
internal static class CorpusSteps
{
    /// <summary>Asserts the dispatch loop executed every step the runner loaded.</summary>
    internal static void AssertAllExecuted(string where, int loaded, int executed)
    {
        // Both directions. Fewer executed than loaded is the failure this exists to catch; MORE
        // executed than loaded means the ledger is counting something that is not a loaded step,
        // which would make the guard meaningless in the other direction.
        Assert.True(
            executed == loaded,
            $"{where}: loaded {loaded} steps but executed {executed} — "
            + $"{loaded - executed} step(s) were never dispatched. The corpus moved; this runner "
            + "did not. There is deliberately no step-count constant to re-pin "
            + "(#lzcorpusfloorguard) — find the step the dispatch loop skipped.");
    }

    /// <summary>Total steps declared by a fixture's top-level <c>steps</c> array.</summary>
    internal static int Declared(JsonElement root) =>
        root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
            ? steps.GetArrayLength()
            : 0;

    /// <summary>Total steps declared across a corpus's fixtures, loaded straight from disk.</summary>
    internal static int DeclaredAcross(string corpus, IEnumerable<string> fixtures)
    {
        var total = 0;
        foreach (var fixture in fixtures)
        {
            using var document = SpecCorpus.Load(corpus, fixture);
            total += Declared(document.RootElement);
        }

        return total;
    }
}
