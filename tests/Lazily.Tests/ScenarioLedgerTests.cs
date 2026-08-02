using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Self-tests for the scenario ledger's identity resolution
/// (<c>#lzscenariocoverage</c>, <c>#lzspecscenarioids</c>).
/// </summary>
/// <remarks>
/// <para>
/// These exist rather than a comment because <see cref="ScenarioSet.IdOf"/> used to end its
/// <c>id</c> -> <c>name</c> resolution in a positional <c>#&lt;n&gt;</c> fallback. A ledger entry
/// recorded BY POSITION silently rebinds to a different scenario when the corpus array is
/// reordered, and nothing turns red: the coverage guard compares "index 1 was replayed" against
/// whatever now sits at index 1 and agrees with itself. The corpus identifies every scenario now,
/// so the fallback is a hard failure — and a rule enforced only by the corpus happening to be
/// well-formed is not enforced at all.
/// </para>
/// <para>
/// Getting the ORDER wrong fails just as quietly in the other direction: it renames every
/// scenario of a fixture at once, so the guard reports the whole fixture unreplayed and the
/// diagnosis points at the runner instead of at this function.
/// </para>
/// </remarks>
public class ScenarioLedgerTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void IdWinsOverName()
    {
        Assert.Equal("keep_latest", ScenarioSet.IdOf(Parse("""{"id":"keep_latest","name":"ignored"}"""), 7));
    }

    [Fact]
    public void NameIsTheFallback()
    {
        Assert.Equal("repair_converges", ScenarioSet.IdOf(Parse("""{"name":"repair_converges"}"""), 7));
    }

    [Fact]
    public void AnUnidentifiedScenarioIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ScenarioSet.IdOf(Parse("""{"policy":"Sum"}"""), 1));
        Assert.Contains("carries neither `id` nor `name`", error.Message, StringComparison.Ordinal);
        Assert.Contains("index 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankIdentifierIsRefused()
    {
        // A blank id is not an identifier. Accepting it would file every blank-id scenario in
        // the corpus under the SAME ledger entry, which reads as "replayed" the moment any one
        // of them runs.
        Assert.Throws<InvalidOperationException>(
            () => ScenarioSet.IdOf(Parse("""{"id":"  ","name":""}"""), 2));
    }

    [Fact]
    public void ANonObjectScenarioIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => ScenarioSet.IdOf(Parse("""[1,2]"""), 3));
    }
}
