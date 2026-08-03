namespace Lazily.Tests;

/// <summary>
/// The fixture-scoped half of the prose-assertion-key convention
/// (<c>#lzprosekeyconvention</c>): it remembers every assertion key the fixture's run
/// actually asserted, every discharge claim a block made, and fails a run that made a claim
/// nobody ever verified.
/// </summary>
/// <remarks>
/// <para>
/// A prose key — <c>clause</c>, <c>anti_vacuity</c>, <c>null_form</c>, <c>theorem</c>, a
/// top-level <c>note</c> — carries an English paragraph and nothing a runner can compare
/// against observed behaviour. It is DISCHARGED, never asserted and never excused: a runner
/// names the executable assertion keys that carry the paragraph's obligation, and this ledger
/// checks the naming. "<c>epoch_disambiguation</c> is discharged by <c>frame_epoch</c> and
/// <c>blob_epoch</c>" is a claim about the run; "<c>epoch_disambiguation</c> is prose" is not,
/// and the free-text excuses this replaces were the second kind.
/// </para>
/// <para>
/// The ledger is FIXTURE-scoped rather than block-scoped because the obligation stated in
/// <c>assertions</c> is routinely carried by a per-scenario <c>expect</c> key:
/// <c>epoch_disambiguation</c> is discharged by <c>expect.frame_epoch</c> and
/// <c>expect.blob_epoch</c>, asserted long after the <c>assertions</c> block is finished. A
/// named key is therefore matched by KEY NAME in any block of that fixture, and the match runs
/// when the whole replay is done.
/// </para>
/// <para>
/// Rules 1-5 and 7 are local to the block that declares <c>prose</c> and are raised by
/// <see cref="FixtureAssertions.Verify"/>. This type raises rule 6 — a discharge naming a key
/// the run never asserted — plus the two things only a fixture-scoped view can see: a claim
/// whose declaring block was never verified, and a claim nobody verified at all.
/// </para>
/// <para>
/// ARMING. <see cref="Replay"/> owns the ledger's lifetime, and <see cref="Dispose"/> is what
/// fails a run that never called <see cref="VerifyProse"/> — an unverified discharge claim is
/// exactly as bad as an unconsumed key, and a check the runner can forget is not a check. What
/// <see cref="Replay"/> adds over a bare <c>using</c> is the DISARM on an in-flight failure:
/// a <c>Dispose</c> that throws while a real assertion failure is unwinding REPLACES that
/// failure, and losing the message that says what actually diverged is the trade
/// <see cref="FixtureAssertions.Verify"/> already refuses to make. Disarming there costs
/// nothing — the run fails anyway, and the claim only matters on a run that would otherwise
/// be green.
/// </para>
/// </remarks>
public sealed class ProseLedger : IDisposable
{
    private sealed class Claim(string where, string key, string[] dischargedBy)
    {
        public string Where { get; } = where;

        public string Key { get; } = key;

        public string[] DischargedBy { get; } = dischargedBy;
    }

    private readonly string _fixture;
    private readonly string _where;
    private readonly List<Claim> _claims = [];
    private readonly HashSet<string> _asserted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _declaredProse = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verifiedBlocks = new(StringComparer.Ordinal);
    private bool _verified;
    private bool _armed = true;

    private ProseLedger(string corpus, string fixture)
    {
        _fixture = fixture;
        _where = $"{corpus}/{fixture}";
    }

    /// <summary>
    /// Replay one fixture under a ledger, and fail the run when it left a discharge claim
    /// unverified.
    /// </summary>
    /// <param name="corpus">The corpus directory name, as passed to <see cref="SpecCorpus.Load"/>.</param>
    /// <param name="fixture">The fixture file name, as passed to <see cref="SpecCorpus.Load"/>.</param>
    /// <param name="replay">The body of the test.</param>
    public static void Replay(string corpus, string fixture, Action<ProseLedger> replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        using var ledger = new ProseLedger(corpus, fixture);
        try
        {
            replay(ledger);
        }
        catch
        {
            // The run already fails and already says why. See ARMING above.
            ledger._armed = false;
            throw;
        }
    }

    /// <summary>
    /// Check every discharge claim this fixture's run made, and mark the ledger verified.
    /// </summary>
    /// <param name="fixture">
    /// The fixture being verified. Checked against the ledger's own — a runner that verified
    /// the wrong fixture's ledger would otherwise report a green claim about a run that never
    /// happened.
    /// </param>
    public void VerifyProse(string fixture)
    {
        if (!string.Equals(fixture, _fixture, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException(
                $"{_where}: VerifyProse('{fixture}') was handed a different fixture than this "
                + "ledger recorded, so it would verify claims nothing here made");

        // Rule 6, the one that makes the excuse falsifiable: a discharge names keys, and the
        // ledger knows which keys this fixture's run actually ASSERTED.
        var unasserted = _claims
            .Select(claim => (claim, missing: claim.DischargedBy.Where(name => !_asserted.Contains(name)).ToArray()))
            .Where(entry => entry.missing.Length > 0)
            .Select(entry =>
                $"{entry.claim.Where}: '{entry.claim.Key}' names [{string.Join(", ", entry.missing)}]")
            .ToArray();
        if (unasserted.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: discharge(s) naming key(s) this fixture's run never asserted — "
                + "the claim is false, or the assertion that carried it was deleted: "
                + string.Join("; ", unasserted)
                + $"; asserted keys were [{string.Join(", ", _asserted.Order(StringComparer.Ordinal))}]");

        // Rule 7 again, over the whole fixture: a key that is prose in ANY block of this
        // fixture cannot discharge another prose key, wherever it was named.
        var proseNamed = _claims
            .Select(claim => (claim, named: claim.DischargedBy.Where(_declaredProse.Contains).ToArray()))
            .Where(entry => entry.named.Length > 0)
            .Select(entry =>
                $"{entry.claim.Where}: '{entry.claim.Key}' names [{string.Join(", ", entry.named)}]")
            .ToArray();
        if (proseNamed.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: discharge(s) naming a key that is itself prose — a paragraph cannot "
                + "carry another paragraph's obligation: " + string.Join("; ", proseNamed));

        // A claim whose declaring block never reached Verify() had rules 1-5 and 7 checked by
        // nothing, and its set comparison against `assertions.prose` never ran.
        var unverifiedBlocks = _claims
            .Where(claim => !_verifiedBlocks.Contains(claim.Where))
            .Select(claim => $"{claim.Where}: '{claim.Key}'")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unverifiedBlocks.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: discharge(s) whose block never called Verify(), so the declared "
                + "`prose` set was never compared against them: "
                + string.Join("; ", unverifiedBlocks));

        _verified = true;
    }

    /// <summary>Record that <paramref name="key"/> was asserted somewhere in this fixture.</summary>
    internal void Asserted(string key) => _asserted.Add(key);

    /// <summary>Record a block's discharge claim.</summary>
    internal void Discharge(string where, string key, string[] dischargedBy) =>
        _claims.Add(new Claim(where, key, dischargedBy));

    /// <summary>Record that a block declaring <c>prose</c> completed its own verification.</summary>
    internal void BlockVerified(string where, IEnumerable<string> declaredProse)
    {
        _verifiedBlocks.Add(where);
        foreach (var key in declaredProse) _declaredProse.Add(key);
    }

    /// <summary>Fail when the run left a discharge claim unverified.</summary>
    public void Dispose()
    {
        if (!_armed || _verified || _claims.Count == 0) return;
        throw new Xunit.Sdk.XunitException(
            $"{_where}: {_claims.Count} prose discharge claim(s) were recorded and VerifyProse "
            + "was never called — an unverified claim proves exactly as much as an unconsumed "
            + $"key: [{string.Join(", ", _claims.Select(claim => claim.Key).Order(StringComparer.Ordinal))}]");
    }
}
