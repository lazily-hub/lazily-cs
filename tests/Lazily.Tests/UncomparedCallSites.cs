namespace Lazily.Tests;

/// <summary>
/// The declared ledger of call sites whose <c>AssertKeyWith</c> / <c>AssertKeyInto</c> callbacks
/// do NOT reach a tracker-owned comparison (<c>#lzcsuncomparedvalues</c>), each with the reason.
/// </summary>
/// <remarks>
/// <para>
/// TWO-DIRECTIONAL, like every other ledger in this repo. An uncompared key whose call site is
/// absent here fails the run where it happens, in <see cref="FixtureAssertions.Verify"/>; a call
/// site listed here that DOES reach a comparison fails as STALE, in
/// <see cref="ComparisonSeam.Compared"/>. Neither direction can be satisfied by editing the other.
/// </para>
/// <para>
/// The key is <c>File.cs::Member</c>, captured by
/// <see cref="System.Runtime.CompilerServices.CallerFilePathAttribute"/> and
/// <see cref="System.Runtime.CompilerServices.CallerMemberNameAttribute"/> — never by a line
/// number, which every insertion above it would renumber, and never by the assertion KEY, which is
/// a loop variable at most of these sites and would turn the ledger into a transcript of the
/// corpus. The granularity is ALL-OR-NOTHING on purpose: an entry goes stale the moment any
/// AssertKeyWith in that member reaches a comparison, so a member cannot be half-converted and
/// half-excused.
/// </para>
/// <para>
/// NO CONFORMANCE RUNNER IS LISTED. All 200 <c>AssertKeyWith</c> call sites in this suite were
/// converted to the seam rather than declared here, so the emptiness below is the claim: every
/// fixture key this binding books as asserted reached a comparison against the fixture's own
/// value. The two entries that exist belong to the guard's OWN self-tests, and they are what stops
/// this from being an allowlist nothing exercises — a ledger whose lookup is never taken cannot
/// tell "no exemptions" from "the key format stopped matching".
/// </para>
/// <para>
/// An entry is a FINDING against this binding, not a relaxation. The reason has to say what the
/// callback does instead of comparing, and where — if anywhere — the fact is proven. "not
/// converted yet" is not a reason; "this key is a scenario INPUT, and its effect is asserted as
/// &lt;key&gt;" is, and where that was true the call site now says so with
/// <see cref="FixtureAssertions.ExcuseKey"/> instead, which is checked the same two ways.
/// </para>
/// </remarks>
internal static class UncomparedCallSites
{
    internal static readonly IReadOnlyDictionary<string, string> Declared =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ComparisonSeamGuardTests.cs::An_uncompared_key_passes_when_its_call_site_is_declared"] =
                "self-test: the callback here deliberately compares nothing, so this entry is what "
                + "proves the exemption lookup resolves a real File.cs::Member key",
            ["ComparisonSeamGuardTests.cs::A_declared_call_site_that_compares_fails_as_stale"] =
                "self-test: the callback here deliberately DOES compare, so this entry is what "
                + "proves the stale direction fires instead of being quietly honoured",
        };
}
