using System.Text.Json;

namespace Lazily.Tests;

/// <summary>
/// A fixture assertion block that remembers which of its keys a runner read and which
/// of them reached a comparison against the fixture's own value, and fails when a key
/// was left unconsumed (<c>#lzassertunknownkeys</c>), was read but never asserted, or
/// carries a stale excuse (<c>#lzconsumednotasserted</c>).
/// </summary>
/// <remarks>
/// <para>
/// A runner that reads named keys out of a fixture's assertion block and silently
/// ignores the rest reports the fixture as replayed while never checking the field the
/// fixture exists for. The fixture round-trips, the suite goes green, and the assertion
/// proves nothing. That is one level below the coverage guard in <see cref="SpecCorpus"/>:
/// that one proves a fixture was OPENED, this proves that, having opened it, every
/// assertion it carries was READ.
/// </para>
/// <para>
/// C# has two ways to lose a key. The one this repo actually has is the hand-rolled
/// <c>TryGetProperty</c> reader whose <c>if</c> simply does not fire — <c>AssertScenario</c>
/// in the durable-outbox runner ran ZERO assertions for a scenario whose <c>expect</c>
/// used none of its four known names. The other is <c>JsonSerializer</c>, which ignores
/// unmapped members unless <c>JsonUnmappedMemberHandling.Disallow</c> is set; no fixture
/// assertion block in this repo is bound to a POCO, so that setting would close nothing
/// here, and the tracking below is what closes the gap that exists.
/// </para>
/// <para>
/// This is a tracking wrapper rather than a per-runner allowlist on purpose. An allowlist
/// records what a runner CLAIMS to evaluate; tracking records what it actually read, so a
/// key named in a <c>knownKeys</c> set whose branch was deleted is still caught. The
/// members are named exactly as <see cref="JsonElement"/>'s, so adopting it is a change of
/// type at the binding site and nothing else.
/// </para>
/// <para>
/// <see cref="Verify"/> is explicit rather than an <c>IDisposable</c>: a <c>Dispose</c>
/// that throws while a real assertion failure is unwinding REPLACES that failure, and
/// losing the message that says what actually diverged is a worse trade than an
/// occasional forgotten call.
/// </para>
/// <para>
/// Tracking a read proves consumption, not assertion. A runner can read a key and do
/// nothing with it — a named <c>continue</c> inside a loop that iterates the block, a
/// value bound to a local that no comparison mentions, or an arm that reads the key and
/// then compares against a hardcoded literal so that editing the fixture changes
/// nothing. All three mark the key read and all three prove nothing. So a key becomes
/// SATISFIED only by going through <see cref="AssertKey(string, bool)"/> and its
/// siblings, or <see cref="AssertKeyWith"/>, both of which hand the fixture's OWN value
/// to the comparison — or by <see cref="ExcuseKey"/>, which demands a written reason.
/// </para>
/// <para>
/// <see cref="ExcuseKey"/> is two-directional exactly as the coverage allowlist is:
/// excusing a key the same run also asserts fails, because that excuse has gone stale
/// and is now hiding nothing. Prefer implementing the assertion; excusing is for a key
/// with nothing here to compare against.
/// </para>
/// <para>
/// A key the CORPUS declares to be an English paragraph (<c>assertions.prose</c>,
/// <c>#lzprosekeyconvention</c>) takes neither route: it is DISCHARGED through
/// <see cref="ProseKey"/>, which names the executable keys carrying its obligation and hands
/// the naming to a fixture-scoped <see cref="ProseLedger"/> that checks it. Asserting one
/// pins wording rather than behaviour; excusing one with free text is the unfalsifiable
/// default the clause exists to remove. Both fail here.
/// </para>
/// </remarks>
public sealed class FixtureAssertions
{
    /// <summary>
    /// Names that are ANNOTATIONS wherever they appear: prose for a human reader, exempt by
    /// name.
    /// </summary>
    /// <remarks>
    /// The ONLY allowlist here, and deliberately tiny. An assertion a binding does not
    /// implement does NOT belong in it — it belongs in the binding. The exemption is by NAME
    /// and it is overridden by the corpus: a block that lists one of these in its own
    /// <c>prose</c> array has said the key states an obligation, and an obligation living
    /// under a reserved name is a place no runner could be made to discharge anything, so the
    /// declaration wins and the key must be discharged like any other paragraph
    /// (<c>#lzprosekeyconvention</c>).
    /// </remarks>
    private static readonly HashSet<string> AnnotationNames =
        new(StringComparer.Ordinal) { "comment", "description", "note", "notes", "why" };

    /// <summary>The corpus's own declaration of which sibling keys are English paragraphs.</summary>
    private const string ProseDeclaration = "prose";

    private readonly JsonElement _block;
    private readonly string _where;
    private readonly ProseLedger? _ledger;
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asserted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _excused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _discharged = new(StringComparer.Ordinal);

    private FixtureAssertions(JsonElement block, string where, ProseLedger? ledger)
    {
        // Rung 0 (#lznullformblind): book this block as BOUND, keyed by its CONTENT rather
        // than by <paramref name="where"/>. Every other rung is scoped to a block a runner
        // already bound, so a block nothing binds reports nothing at all — its keys are not
        // unread, nothing reads them. Content keying is what stops the ledger inheriting the
        // inconsistent spellings runners give `where`.
        SpecCorpus.RecordBlockBind(block);
        _block = block;
        _where = where;
        _ledger = ledger;
    }

    /// <summary>Track <paramref name="owner"/>'s <paramref name="property"/> block.</summary>
    /// <param name="owner">The step, scenario, or fixture root carrying the block.</param>
    /// <param name="property">Usually <c>expect</c>, <c>expected</c>, or <c>assertions</c>.</param>
    /// <param name="where">Names the fixture and position for the failure message.</param>
    /// <param name="ledger">
    /// The fixture's prose ledger, for a fixture whose corpus declares prose keys. Every block
    /// of that fixture passes the SAME ledger — an obligation stated in <c>assertions</c> is
    /// routinely discharged by a per-scenario <c>expect</c> key, so the keys a block asserts
    /// have to be visible to a claim made in another one.
    /// </param>
    public static FixtureAssertions Of(
        JsonElement owner,
        string property,
        string where,
        ProseLedger? ledger = null) =>
        new(owner.GetProperty(property), where, ledger);

    /// <summary>Track a block the caller already holds.</summary>
    public static FixtureAssertions Wrap(JsonElement block, string where, ProseLedger? ledger = null) =>
        new(block, where, ledger);

    /// <summary>The underlying element, for reads that are not key lookups.</summary>
    public JsonElement Element => _block;

    /// <inheritdoc cref="JsonElement.ValueKind"/>
    public JsonValueKind ValueKind => _block.ValueKind;

    /// <inheritdoc cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
    public bool TryGetProperty(string name, out JsonElement value)
    {
        _read.Add(name);
        return _block.TryGetProperty(name, out value);
    }

    /// <inheritdoc cref="JsonElement.GetProperty(string)"/>
    public JsonElement GetProperty(string name)
    {
        _read.Add(name);
        return _block.GetProperty(name);
    }

    /// <summary>
    /// Enumerate the block's members.
    /// </summary>
    /// <remarks>
    /// Enumerating does NOT count as reading: switching over the key set with a
    /// fall-through default is the exact shape this guard exists to catch. A runner that
    /// enumerates goes on to evaluate each member, and it says so by routing that
    /// evaluation through <see cref="AssertKeyWith"/>.
    /// </remarks>
    /// <remarks>
    /// There is deliberately no "mark this consumed" escape any more. Marking a key read
    /// without comparing anything is precisely the defect <c>#lzconsumednotasserted</c>
    /// names: it silenced the unconsumed-key gate while proving nothing. The only two ways
    /// out are an assertion that receives the fixture's value, or an
    /// <see cref="ExcuseKey"/> carrying a written reason.
    /// </remarks>
    public JsonElement.ObjectEnumerator EnumerateObject() => _block.EnumerateObject();

    // ---------------------------------------------------------------------------
    // Assertion entry points (#lzconsumednotasserted).
    //
    // Every one of these hands the FIXTURE'S value to the comparison. That is the whole
    // point: an arm that compares against a literal cannot reach this path, so it never
    // marks the key asserted and Verify() catches it.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Read <paramref name="name"/>, mark it asserted, and hand its value to
    /// <paramref name="check"/>.
    /// </summary>
    /// <remarks>
    /// The general form, for comparisons that are not equality — a tolerance, a set
    /// containment, a shape check, or an equality the caller must decode itself.
    /// </remarks>
    public void AssertKeyWith(string name, Action<JsonElement> check)
    {
        _read.Add(name);
        var value = _block.GetProperty(name);
        MarkAsserted(name);
        Guarded(name, () => check(value));
    }

    /// <summary>
    /// <see cref="AssertKeyWith"/> for a key the fixture may omit; returns whether it ran.
    /// </summary>
    public bool TryAssertKeyWith(string name, Action<JsonElement> check)
    {
        _read.Add(name);
        if (!_block.TryGetProperty(name, out var value)) return false;
        MarkAsserted(name);
        Guarded(name, () => check(value));
        return true;
    }

    /// <summary>Assert <paramref name="name"/>'s boolean value equals <paramref name="actual"/>.</summary>
    public void AssertKey(string name, bool actual) =>
        AssertKeyWith(name, want => Xunit.Assert.Equal(want.GetBoolean(), actual));

    /// <summary>Assert <paramref name="name"/>'s integer value equals <paramref name="actual"/>.</summary>
    public void AssertKey(string name, int actual) =>
        AssertKeyWith(name, want => Xunit.Assert.Equal(want.GetInt32(), actual));

    /// <inheritdoc cref="AssertKey(string, int)"/>
    public void AssertKey(string name, long actual) =>
        AssertKeyWith(name, want => Xunit.Assert.Equal(want.GetInt64(), actual));

    /// <inheritdoc cref="AssertKey(string, int)"/>
    public void AssertKey(string name, ulong actual) =>
        AssertKeyWith(name, want => Xunit.Assert.Equal(want.GetUInt64(), actual));

    /// <summary>Assert <paramref name="name"/>'s numeric value equals <paramref name="actual"/> within <paramref name="tolerance"/>.</summary>
    public void AssertKey(string name, double actual, double tolerance) =>
        AssertKeyWith(
            name,
            want => Xunit.Assert.True(
                Math.Abs(want.GetDouble() - actual) <= tolerance,
                $"expected {want.GetDouble()} ± {tolerance}, got {actual}"));

    /// <summary>Assert <paramref name="name"/>'s string value equals <paramref name="actual"/>.</summary>
    public void AssertKey(string name, string? actual) =>
        AssertKeyWith(name, want => Xunit.Assert.Equal(want.GetString(), actual));

    /// <summary>Assert <paramref name="name"/>'s array of strings equals <paramref name="actual"/>.</summary>
    public void AssertKey(string name, IEnumerable<string?> actual) =>
        AssertKeyWith(
            name,
            want => Xunit.Assert.Equal(
                want.EnumerateArray().Select(item => item.GetString()).ToArray(),
                actual.ToArray()));

    /// <summary>Assert <paramref name="name"/>'s array of integers equals <paramref name="actual"/>.</summary>
    public void AssertKey(string name, IEnumerable<long> actual) =>
        AssertKeyWith(
            name,
            want => Xunit.Assert.Equal(
                want.EnumerateArray().Select(item => item.GetInt64()).ToArray(),
                actual.ToArray()));

    /// <inheritdoc cref="AssertKey(string, IEnumerable{long})"/>
    public void AssertKey(string name, IEnumerable<int> actual) =>
        AssertKeyWith(
            name,
            want => Xunit.Assert.Equal(
                want.EnumerateArray().Select(item => item.GetInt32()).ToArray(),
                actual.ToArray()));

    /// <summary>Assert <paramref name="name"/>'s array of bytes equals <paramref name="actual"/>.</summary>
    public void AssertKey(string name, IEnumerable<byte> actual) =>
        AssertKeyWith(
            name,
            want => Xunit.Assert.Equal(
                want.EnumerateArray().Select(item => item.GetByte()).ToArray(),
                actual.ToArray()));

    /// <summary>
    /// Declare that <paramref name="name"/> cannot be asserted here, and say why.
    /// </summary>
    /// <param name="reason">
    /// Non-empty, and it has to name where the fact is proven instead or why it is
    /// unprovable at this call site. "not implemented" is not a reason.
    /// </param>
    /// <remarks>
    /// Two-directional: a key excused by one route and asserted by another in the same
    /// run fails <see cref="Verify"/>, because the excuse has stopped hiding anything.
    /// </remarks>
    public void ExcuseKey(string name, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new Xunit.Sdk.XunitException(
                $"{_where}: ExcuseKey('{name}') needs a reason — an excuse nobody had to "
                + "justify is an allowlist entry wearing a different hat");
        _read.Add(name);
        _excused[name] = reason;
    }

    /// <summary>
    /// Discharge the prose key <paramref name="name"/> by naming the executable assertion keys
    /// that carry its obligation (<c>#lzprosekeyconvention</c>).
    /// </summary>
    /// <param name="name">A key the block's own <c>prose</c> array declares.</param>
    /// <param name="dischargedBy">
    /// The keys that carry the paragraph's obligation, matched by NAME in any block of the
    /// same fixture — <c>epoch_disambiguation</c> is discharged by <c>expect.frame_epoch</c>
    /// and <c>expect.blob_epoch</c>, asserted per scenario long after this block is finished.
    /// Every one of them must be a key the run really asserts: that is what turns "this is
    /// prose" into a claim the tracker can falsify.
    /// </param>
    /// <remarks>
    /// This REPLACES the free-text <see cref="ExcuseKey"/> reasons that used to be written for
    /// these keys rather than sitting beside them: two paths to satisfy one key is the
    /// ambiguity the clause removes, so a key both discharged and excused — or both discharged
    /// and asserted — fails <see cref="Verify"/>.
    /// </remarks>
    public void ProseKey(string name, params string[] dischargedBy)
    {
        ArgumentNullException.ThrowIfNull(dischargedBy);

        // A claim nobody can check is the state this whole convention replaces: rule 6 is
        // fixture-scoped, so a block with no ledger could record a naming that no later pass
        // ever compares against the keys the run asserted.
        if (_ledger is null)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: ProseKey('{name}') needs the fixture's ProseLedger — a discharge "
                + "claim that reaches no ledger is checked by nothing, which is the "
                + "unfalsifiable excuse this replaces");

        // Rule 5.
        if (dischargedBy.Length == 0 || dischargedBy.Any(string.IsNullOrWhiteSpace))
            throw new Xunit.Sdk.XunitException(
                $"{_where}: ProseKey('{name}') names no discharging key — a paragraph states an "
                + "obligation, and a discharge that names nothing says only that the runner "
                + "noticed it");

        if (_discharged.ContainsKey(name))
            throw new Xunit.Sdk.XunitException(
                $"{_where}: ProseKey('{name}') was declared twice; the second naming silently "
                + "replaces the first, so one of the two claims is checked by nothing");

        _read.Add(name);
        _discharged[name] = dischargedBy;
        _ledger.Discharge(_where, name, dischargedBy);
    }

    /// <summary>
    /// Fail when a key was never read, was read but never asserted, carries a stale excuse, or
    /// breaks one of the prose-key rules.
    /// </summary>
    public void Verify()
    {
        if (_block.ValueKind != JsonValueKind.Object) return;

        // The declaration is read off the RAW block, BEFORE any name-based exemption is
        // subtracted. A tracker that filters its reserved names first makes the corpus's own
        // declaration invisible — the key is exempt from the unread guard, exempt from the
        // unasserted guard, and never discharged, so both frame_roundtrip fixtures would skip
        // this convention entirely while the binding still reported conforming. Three of nine
        // hit that independently.
        var declaresProse = VerifyProseKeys() is not null;

        var present = _block.EnumerateObject()
            .Select(property => property.Name)
            // Inside a declaring block the name exemption is off ENTIRELY: the corpus wins, so
            // a `note` sitting in such a block but absent from its array needs an assertion or
            // an excuse like any other key. Everywhere else the exemption stands.
            .Where(name => !AnnotationNames.Contains(name) || declaresProse)
            .ToArray();

        var unread = present
            .Where(name => !_read.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (unread.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: unconsumed assertion key(s) [{string.Join(", ", unread)}] — the "
                + "fixture asserts something this runner never evaluated, so replaying it "
                + "proves nothing about that field");

        var stale = present
            .Where(name => _excused.ContainsKey(name) && _asserted.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (stale.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: stale excuse(s) [{string.Join(", ", stale)}] — this run BOTH "
                + "asserts and excuses the key, so the excuse hides nothing and its reason "
                + "is now a lie: "
                + string.Join("; ", stale.Select(name => $"{name}: \"{_excused[name]}\"")));

        var readOnly = present
            .Where(name => !_asserted.Contains(name)
                && !_excused.ContainsKey(name)
                && !_discharged.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (readOnly.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: read-but-not-asserted assertion key(s) "
                + $"[{string.Join(", ", readOnly)}] — the runner consumed the key and then "
                + "never compared the fixture's own value against anything, so editing the "
                + "fixture changes nothing; route it through AssertKey/AssertKeyWith, or "
                + "declare an ExcuseKey with a reason");
    }

    /// <summary>
    /// Raise the block-local half of the prose-key rules and return the declared prose keys.
    /// </summary>
    /// <remarks>
    /// The set comparison at the end is what CONSUMES <c>prose</c> itself: the declaration is
    /// an ordinary key of the block, so the unconsumed-key gate above sees a runner that
    /// ignores it, and a forgotten paragraph fails here rather than vanishing.
    /// </remarks>
    private IReadOnlySet<string>? VerifyProseKeys()
    {
        if (!_block.TryGetProperty(ProseDeclaration, out var declaration))
        {
            // Rule 3, in the degenerate direction: nothing in this block is prose, so nothing
            // in it can be discharged.
            if (_discharged.Count > 0)
                throw new Xunit.Sdk.XunitException(
                    $"{_where}: discharged key(s) "
                    + $"[{string.Join(", ", _discharged.Keys.Order(StringComparer.Ordinal))}] in a "
                    + $"block that declares no `{ProseDeclaration}` — only the CORPUS says which "
                    + "keys are English paragraphs, and a binding deciding for itself is the "
                    + "split this convention closes");
            return null;
        }

        if (declaration.ValueKind != JsonValueKind.Array)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: `{ProseDeclaration}` is {declaration.ValueKind}, not an array of "
                + "sibling key names");

        var declared = new HashSet<string>(
            declaration.EnumerateArray().Select(item => item.GetString()!),
            StringComparer.Ordinal);

        if (declared.Contains(ProseDeclaration))
            throw new Xunit.Sdk.XunitException(
                $"{_where}: `{ProseDeclaration}` lists itself; the declaration is a value a "
                + "runner compares, not a paragraph");

        var members = _block.EnumerateObject().Select(property => property.Name).ToArray();

        var undeclaredMembers = declared.Where(name => !members.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        if (undeclaredMembers.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: `{ProseDeclaration}` names [{string.Join(", ", undeclaredMembers)}], "
                + "which this block does not carry");

        // A block that is entirely prose has nothing that could discharge it.
        if (!members.Any(name => name != ProseDeclaration && !declared.Contains(name)))
            throw new Xunit.Sdk.XunitException(
                $"{_where}: every key of this block is prose, so no assertion here can carry "
                + "any of their obligations");

        // Rule 1.
        var asserted = declared.Where(_asserted.Contains).Order(StringComparer.Ordinal).ToArray();
        if (asserted.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: prose key(s) [{string.Join(", ", asserted)}] were ASSERTED — "
                + "comparing an English paragraph, or a tally derived from one, pins wording "
                + "rather than behaviour: a copy-edit reddens the run and a library regression "
                + "does not. Discharge them with ProseKey instead");

        // Rule 2.
        var excused = declared.Where(_excused.ContainsKey).Order(StringComparer.Ordinal).ToArray();
        if (excused.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: prose key(s) [{string.Join(", ", excused)}] were EXCUSED with free "
                + "text — an unfalsifiable reason is indistinguishable from the undocumented "
                + "default this clause removes. Discharge them with ProseKey, naming the keys "
                + "that carry the obligation: "
                + string.Join("; ", excused.Select(name => $"{name}: \"{_excused[name]}\"")));

        // Rule 3.
        var notProse = _discharged.Keys.Where(name => !declared.Contains(name))
            .Order(StringComparer.Ordinal).ToArray();
        if (notProse.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: discharged key(s) [{string.Join(", ", notProse)}] the corpus does "
                + $"NOT declare in `{ProseDeclaration}` — a key carrying a comparable value is "
                + "asserted, not discharged");

        // Rule 7, over the declared paragraphs SEEDED WITH `prose` ITSELF. The seed is not
        // redundant: `prose` never lists itself, so without it a discharge naming `prose`
        // slips past — and rule 4's own comparison marks `prose` asserted, so rule 6 would
        // wave it through. A paragraph discharged by the declaration that it is a paragraph
        // proves nothing.
        var proseNames = new HashSet<string>(declared, StringComparer.Ordinal) { ProseDeclaration };
        var namesProse = _discharged
            .Select(entry => (entry.Key, named: entry.Value.Where(proseNames.Contains).ToArray()))
            .Where(entry => entry.named.Length > 0)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key} -> [{string.Join(", ", entry.named)}]")
            .ToArray();
        if (namesProse.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: discharge(s) naming a key that is itself prose — a paragraph cannot "
                + "carry another paragraph's obligation: " + string.Join("; ", namesProse));

        // Rule 4, and the comparison that consumes `prose`.
        var declaredNames = declared.Order(StringComparer.Ordinal).ToArray();
        var dischargedNames = _discharged.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!declaredNames.SequenceEqual(dischargedNames, StringComparer.Ordinal))
            throw new Xunit.Sdk.XunitException(
                $"{_where}: the discharged key set [{string.Join(", ", dischargedNames)}] differs "
                + $"from `{ProseDeclaration}` [{string.Join(", ", declaredNames)}] — a paragraph "
                + "the corpus declares and this runner never discharged proves nothing, and this "
                + "comparison is what makes it fail rather than vanish");

        _read.Add(ProseDeclaration);
        MarkAsserted(ProseDeclaration);
        _ledger?.BlockVerified(_where, proseNames);
        return declared;
    }

    private void MarkAsserted(string name)
    {
        _asserted.Add(name);
        _ledger?.Asserted(name);
    }

    /// <summary>Add the fixture and key to whatever the caller's comparison reports.</summary>
    private void Guarded(string name, Action check)
    {
        try
        {
            check();
        }
        catch (Xunit.Sdk.XunitException failure)
        {
            throw new Xunit.Sdk.XunitException($"{_where}: assertion key '{name}': {failure.Message}");
        }
    }
}
