using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The guard on the guard (<c>#lzcsuncomparedvalues</c>): every rung of the comparison seam is
/// exercised here against a hand-built block, including both directions of
/// <see cref="UncomparedCallSites"/>.
/// </summary>
/// <remarks>
/// Without these, the seam is a mechanism the conformance suite happens not to trip, and an
/// exemption ledger whose lookup is never taken cannot tell "nothing is exempt" from "the key
/// format stopped matching". The blocks below are written here rather than read from the corpus
/// on purpose: this file asserts what the TRACKER does, and a corpus fixture would make the answer
/// depend on what lazily-spec happens to carry.
/// </remarks>
public sealed class ComparisonSeamGuardTests
{
    /// <summary>
    /// The shape the whole rung exists for — a callback that reads every element of the fixture's
    /// value and compares it against nothing the run produced.
    /// </summary>
    /// <remarks>
    /// This is the live <c>nonterminal_outcomes</c> defect in miniature: <c>Assert.All</c> over the
    /// fixture's own array, asserting a property of a decoded token rather than of any replay. It
    /// is vacuously true over <c>[]</c>, so the corpus could shrink its own claim to nothing and
    /// stay green — and the old tracker booked the key SATISFIED because the callback returned.
    /// </remarks>
    [Fact]
    public void A_callback_that_reads_the_value_without_comparing_it_fails()
    {
        using var block = JsonDocument.Parse("""{"nonterminal_outcomes": ["observed", "accepted"]}""");
        var tracker = FixtureAssertions.Wrap(block.RootElement, "guard");

        tracker.AssertKeyWith(
            "nonterminal_outcomes",
            want => Assert.All(want.EnumerateArray(), item => Assert.NotNull(item.GetString())));

        var failure = Assert.Throws<Xunit.Sdk.XunitException>(tracker.Verify);
        Assert.Contains("never compared the fixture's own value", failure.Message, StringComparison.Ordinal);
        Assert.Contains("nonterminal_outcomes", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same callback, empty: the vacuity that made the original defect invisible is still
    /// caught, because the rung asks whether a comparison HAPPENED rather than whether one passed.
    /// </summary>
    [Fact]
    public void A_vacuous_callback_over_an_empty_array_fails_too()
    {
        using var block = JsonDocument.Parse("""{"nonterminal_outcomes": []}""");
        var tracker = FixtureAssertions.Wrap(block.RootElement, "guard");

        tracker.AssertKeyWith(
            "nonterminal_outcomes",
            want => Assert.All(want.EnumerateArray(), item => Assert.NotNull(item.GetString())));

        Assert.Contains(
            "never compared the fixture's own value",
            Assert.Throws<Xunit.Sdk.XunitException>(tracker.Verify).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A comparison whose run-produced operand is a constant written into the test is refused.
    /// </summary>
    /// <remarks>
    /// The syntactic check, and it has to be syntactic: a replay that produces the right answer and
    /// a literal spelling the right answer are the same bytes, so only the call site's source text
    /// separates them.
    /// </remarks>
    [Fact]
    public void A_comparison_against_a_hardcoded_literal_fails()
    {
        using var block = JsonDocument.Parse("""{"terminal_outcome": "applied"}""");
        var tracker = FixtureAssertions.Wrap(block.RootElement, "guard");

        var failure = Assert.Throws<Xunit.Sdk.XunitException>(
            () => tracker.AssertKeyWith(
                "terminal_outcome",
                want => want.AssertEqual(value => value.GetString(), "applied")));

        Assert.Contains("compared against the literal", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A literal COLLECTION operand is refused on the same terms.</summary>
    [Fact]
    public void A_comparison_against_a_literal_collection_fails()
    {
        using var block = JsonDocument.Parse("""{"outcomes": ["a", "b"]}""");
        var tracker = FixtureAssertions.Wrap(block.RootElement, "guard");

        var failure = Assert.Throws<Xunit.Sdk.XunitException>(
            () => tracker.AssertKeyWith(
                "outcomes",
                want => want.AssertEqual(
                    value => value.EnumerateArray().Select(item => item.GetString()).ToArray(),
                    new[] { "a", "b" })));

        Assert.Contains("compared against the literal", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A comparison against a run-produced operand books the key and passes.</summary>
    [Fact]
    public void A_comparison_against_replayed_state_books_the_key()
    {
        using var block = JsonDocument.Parse("""{"receipt_count": 4}""");
        var tracker = FixtureAssertions.Wrap(block.RootElement, "guard");
        var replayed = Enumerable.Range(0, 4).Count();

        tracker.AssertKeyWith("receipt_count", want => want.AssertEqual(w => w.GetInt32(), replayed));

        tracker.Verify();
    }

    /// <summary>
    /// A divergence-RECORDING runner keeps recording: the seam compares and hands back the result
    /// rather than throwing.
    /// </summary>
    /// <remarks>
    /// This is the property that made the differential-re-execution mechanism unusable here and
    /// that this one has to preserve. Both halves are asserted — the agreeing run reports no
    /// divergence and stays green, and the disagreeing run reports one with both operands, without
    /// either of them throwing out of the callback.
    /// </remarks>
    [Fact]
    public void A_divergence_recording_runner_still_records_instead_of_throwing()
    {
        using var block = JsonDocument.Parse("""{"len": 3, "head": "a"}""");
        var tracker = FixtureAssertions.Wrap(block.RootElement, "guard");
        var divergences = new List<string>();

        void Check<T>(string key, Divergence<T> comparison)
        {
            if (comparison.Diverged) divergences.Add($"{key} — got {comparison.Got}, want {comparison.Want}");
        }

        var replayedLength = "abc".Length;
        var replayedHead = "abc"[..1];

        tracker.AssertKeyWith("len", want => Check("len", want.Compare(w => w.GetInt32(), replayedLength)));
        tracker.AssertKeyWith("head", want => Check("head", want.Compare(w => w.GetString(), replayedHead)));

        tracker.Verify();
        Assert.Empty(divergences);

        // The same runner over a replay that disagrees: still no throw, and the ledger now holds
        // the finding. A guard that could only be satisfied by a throwing comparison would have
        // forced this runner to stop recording, which is a different test rather than a guarded one.
        var tracker2 = FixtureAssertions.Wrap(block.RootElement, "guard");
        var wrongLength = "ab".Length;
        tracker2.AssertKeyWith("len", want => Check("len", want.Compare(w => w.GetInt32(), wrongLength)));
        tracker2.AssertKeyWith("head", want => Check("head", want.Compare(w => w.GetString(), replayedHead)));

        tracker2.Verify();
        Assert.Equal(["len — got 2, want 3"], divergences);
    }

    /// <summary>Direction one of the ledger: a declared call site may leave a key uncompared.</summary>
    /// <remarks>
    /// Its entry in <see cref="UncomparedCallSites"/> names this method, so the key below is
    /// exempt. If the ledger key format ever stopped matching what
    /// <see cref="FixtureAssertions"/> builds, this test would fail rather than the exemption
    /// silently becoming unreachable.
    /// </remarks>
    [Fact]
    public void An_uncompared_key_passes_when_its_call_site_is_declared()
    {
        using var block = JsonDocument.Parse("""{"nonterminal_outcomes": ["observed"]}""");
        var tracker = FixtureAssertions.Wrap(block.RootElement, "guard");

        tracker.AssertKeyWith(
            "nonterminal_outcomes",
            want => Assert.All(want.EnumerateArray(), item => Assert.NotNull(item.GetString())));

        tracker.Verify();
    }

    /// <summary>Direction two: a declared call site that DOES compare fails as stale.</summary>
    /// <remarks>
    /// Without this direction an exemption survives the fix that made it unnecessary, and the next
    /// AssertKeyWith added to that member inherits a hole nobody declared.
    /// </remarks>
    [Fact]
    public void A_declared_call_site_that_compares_fails_as_stale()
    {
        using var block = JsonDocument.Parse("""{"receipt_count": 2}""");
        var tracker = FixtureAssertions.Wrap(block.RootElement, "guard");
        var replayed = Enumerable.Range(0, 2).Count();

        var failure = Assert.Throws<Xunit.Sdk.XunitException>(
            () => tracker.AssertKeyWith(
                "receipt_count",
                want => want.AssertEqual(w => w.GetInt32(), replayed)));

        Assert.Contains("exemption is stale", failure.Message, StringComparison.Ordinal);
        Assert.Contains("UncomparedCallSites", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <see cref="FixtureAssertions.AssertKeyInto"/> projection that reaches no composite
    /// comparison is uncompared, exactly as a callback that never compares is.
    /// </summary>
    [Fact]
    public void A_bare_projection_is_not_a_comparison()
    {
        using var block = JsonDocument.Parse("""{"id": "node-1"}""");
        var tracker = FixtureAssertions.Wrap(block.RootElement, "guard");

        Assert.Equal("node-1", tracker.AssertKeyInto("id", value => value.GetString()!));

        Assert.Contains(
            "never compared the fixture's own value",
            Assert.Throws<Xunit.Sdk.XunitException>(tracker.Verify).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="FixtureAssertions.CompareInto"/> books the keys the composite was built from,
    /// and only those.
    /// </summary>
    [Fact]
    public void A_composite_comparison_books_the_keys_it_projected()
    {
        using var block = JsonDocument.Parse("""{"generation": 7, "stamped_at": 11}""");
        var tracker = FixtureAssertions.Wrap(block.RootElement, "guard");
        var replayed = (Generation: 6L + 1L, StampedAt: 10L + 1L);

        tracker.CompareInto(
            into => (
                Generation: into.AssertKeyInto("generation", value => value.GetInt64()),
                StampedAt: into.AssertKeyInto("stamped_at", value => value.GetInt64())),
            replayed,
            Assert.Equal);

        tracker.Verify();
    }
}
