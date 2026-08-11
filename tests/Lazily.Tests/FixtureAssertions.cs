using System.Runtime.CompilerServices;
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
/// siblings, or by a callback that reaches <see cref="FixtureValue.Against"/> — all of
/// which hand the fixture's OWN value to the comparison — or by <see cref="ExcuseKey"/>,
/// which demands a written reason.
/// </para>
/// <para>
/// <see cref="ExcuseKey"/> is two-directional exactly as the coverage allowlist is:
/// excusing a key the same run also asserts fails, because that excuse has gone stale
/// and is now hiding nothing. Prefer implementing the assertion; excusing is for a key
/// with nothing here to compare against.
/// </para>
/// <para>
/// ASSERTED IS NOT COMPARED (<c>#lzcsuncomparedvalues</c>). Reaching
/// <see cref="AssertKeyWith"/> proves the callback RECEIVED the fixture's value, never that
/// it COMPARED it against anything the run produced. <c>CausalReceiptConformanceTests</c>
/// held the live example until 1df7e7e: <c>AssertKeyWith("nonterminal_outcomes", want =>
/// Assert.All(want.EnumerateArray(), item =&gt; Assert.False(ParseOutcome(item.GetString()!)
/// .IsTerminal())))</c> asserted a property of the <c>ReceiptOutcome</c> ENUM and never
/// touched the projection. <c>Assert.All</c> over an empty sequence is vacuously true, so
/// <c>[]</c> passed, dropping an element passed, and reordering passed — and the key was
/// still marked SATISFIED, which means every rung below was green over an assertion that
/// could not fail.
/// </para>
/// <para>
/// The fix is a comparison SEAM the tracker owns. A callback now receives a
/// <see cref="FixtureValue"/>, and the only route to booking the key COMPARED is
/// <see cref="FixtureValue.Against"/> (or the <c>AssertEqual</c> / <c>Compare</c> conveniences
/// over it), which takes the run-produced operand and the comparison and supplies the
/// FIXTURE'S OWN VALUE itself. So the expected side of every booked comparison came from the
/// block rather than from the call site, a callback that compares nothing books nothing, and
/// <see cref="Verify"/> fails it. The run-produced operand is refused when the call site wrote
/// a compile-time literal there, which is the same defect one level in.
/// </para>
/// <para>
/// Two other mechanisms were spiked first and both are recorded rather than half-built.
/// (1) A RECORDING WRAPPER that books the key when the callback READS its argument catches a
/// callback that ignores the fixture, but NOT this one — the vacuous callback read every
/// element. Recording reads answers "did it look", and the question is "did the value reach a
/// comparison". (2) DIFFERENTIAL RE-EXECUTION — run the callback again against a
/// type-preserving perturbation and require it to fail — needs no call-site edits and does
/// catch this one, but it cannot work here: the runners RECORD divergences into a ledger
/// instead of throwing (<c>Check(...)</c>), so a perturbed re-run signals nothing and instead
/// appends phantom divergences into the ledger the test then compares against its declared
/// set. Measured on the spike: 8 tests broken by the double execution and 288 keys falsely
/// reported "insensitive". <see cref="FixtureValue.Compare"/> is what lets those runners keep
/// recording while the comparison becomes visible.
/// </para>
/// <para>
/// The exemption ledger is <see cref="UncomparedCallSites"/>, keyed by <c>File.cs::Member</c>
/// and two-directional: an uncompared key whose call site is undeclared fails, and a declared
/// call site that does compare fails as stale. No conformance runner is in it —
/// <c>ComparisonSeamGuardTests</c> exercises both directions so the lookup is never dead.
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

    /// <summary>
    /// Keys whose fixture value actually reached a COMPARISON the tracker owns
    /// (<c>#lzcsuncomparedvalues</c>).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="_asserted"/>, and the distinction is the whole point. A key is
    /// ASSERTED when a callback that received its value returned; it is COMPARED only when that
    /// value reached <see cref="FixtureValue.Against"/> — the seam that supplies the fixture's own
    /// value to the comparison rather than trusting the callback to have used it. The two sets are
    /// equal for every entry point below whose comparison the tracker WRITES (`AssertKey`,
    /// `AssertKeyDeep`, `AssertKeySet`, `Deep`); they diverge exactly at the callback forms, which
    /// is where the vacuous `nonterminal_outcomes` lived.
    /// </remarks>
    private readonly HashSet<string> _compared = new(StringComparer.Ordinal);

    /// <summary>The <c>File.cs::Member</c> that wrote each callback-form key's check.</summary>
    private readonly Dictionary<string, string> _sites = new(StringComparer.Ordinal);

    /// <summary>Keys whose value was PROJECTED by <see cref="AssertKeyInto"/>.</summary>
    private readonly HashSet<string> _projected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _excused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _discharged = new(StringComparer.Ordinal);

    /// <summary>
    /// Keys whose OBJECT value had its key set checked — by descent
    /// (<see cref="AssertObjectKey"/>) or by comparison (<see cref="AssertKeySet"/>).
    /// </summary>
    /// <remarks>
    /// <c>#lzsubblockkeyset</c>. Without this the tracker cannot tell an object-valued key
    /// whose sub-fields were all accounted for from one where five were compared by name and
    /// a sixth added upstream is compared by nothing.
    /// </remarks>
    private readonly HashSet<string> _descended = new(StringComparer.Ordinal);

    private FixtureAssertions(JsonElement block, string where, ProseLedger? ledger)
        : this(block, where, ledger, recordBind: true)
    {
    }

    private FixtureAssertions(JsonElement block, string where, ProseLedger? ledger, bool recordBind)
    {
        // Rung 0 (#lznullformblind): book this block as BOUND, keyed by its CONTENT rather
        // than by <paramref name="where"/>. Every other rung is scoped to a block a runner
        // already bound, so a block nothing binds reports nothing at all — its keys are not
        // unread, nothing reads them. Content keying is what stops the ledger inheriting the
        // inconsistent spellings runners give `where`.
        //
        // A CHILD tracker created by AssertObjectKey does NOT book: the bind ledger is
        // two-directional against the blocks `SpecCorpus` inventoried at read time, and a
        // sub-object is not one of them. Booking it would put a bind in the ledger with no
        // inventory entry to match.
        if (recordBind) SpecCorpus.RecordBlockBind(block);
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

    /// <summary>
    /// Bind the WHOLE block and consume it by comparing it structurally against
    /// <paramref name="actual"/> — the object the run really produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The block-level analogue of <see cref="AssertKeyDeep"/>, and it earns the same
    /// blanket discharge for the same reason: a structural comparison is two-directional
    /// at every depth, so a key the corpus adds tomorrow is compared by this call without
    /// anyone editing it. A key set, a value, and a nesting level are all covered.
    /// </para>
    /// <para>
    /// It exists because a runner that already compared a whole `expect` with
    /// <c>JsonNode.DeepEquals</c> — the portable-stdlib replay does exactly that for all 54
    /// of its per-step blocks — was invisible to rung 0: the comparison is complete, and the
    /// bind ledger still read "no runner bound this block", which is indistinguishable from
    /// a block nobody checks. Reporting a complete assertion as a gap trains the reader to
    /// excuse gaps, so the honest fix is a binding entry point rather than an excuse.
    /// </para>
    /// <para>
    /// A block declaring <c>prose</c> is refused: rule 1 says a paragraph is discharged and
    /// never asserted, and a deep comparison would assert it (<c>#lzprosekeyconvention</c>).
    /// </para>
    /// </remarks>
    public static void Deep(
        JsonElement block,
        string where,
        System.Text.Json.Nodes.JsonNode? actual,
        string? detail = null)
    {
        var tracker = Wrap(block, where);
        if (block.ValueKind == JsonValueKind.Object
            && block.TryGetProperty(ProseDeclaration, out _))
            throw new Xunit.Sdk.XunitException(
                $"{where}: this block declares `{ProseDeclaration}`, so a structural comparison "
                + "would ASSERT a paragraph; discharge its prose keys with ProseKey instead");

        var want = System.Text.Json.Nodes.JsonNode.Parse(block.GetRawText());
        Xunit.Assert.True(
            System.Text.Json.Nodes.JsonNode.DeepEquals(want, actual),
            $"{where}: got {actual?.ToJsonString()}, want {want?.ToJsonString()}"
            + (detail is null ? string.Empty : $" ({detail})"));

        // Every key is asserted AND key-set checked by construction — that is what
        // "structural, in both directions" means. Marking them keeps Verify()'s rungs
        // uniform rather than making this a path that skips them.
        if (block.ValueKind != JsonValueKind.Object) return;
        foreach (var property in block.EnumerateObject())
        {
            tracker._read.Add(property.Name);
            tracker.MarkAsserted(property.Name);
            tracker._descended.Add(property.Name);
            tracker._compared.Add(property.Name);
        }

        tracker.Verify();
    }

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
    /// Read <paramref name="name"/>, hand its value to <paramref name="check"/>,
    /// and mark it asserted only after the check succeeds.
    /// </summary>
    /// <remarks>
    /// The general form, for comparisons that are not equality — a tolerance, a set
    /// containment, a shape check, or an equality the caller must decode itself.
    /// </remarks>
    public void AssertKeyWith(
        string name,
        Action<FixtureValue> check,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null)
    {
        _read.Add(name);
        var value = _block.GetProperty(name);
        Invoke(name, value, check, file, member);
        MarkAsserted(name);
    }

    /// <summary>
    /// Run a callback over <paramref name="name"/>'s value with a live comparison seam, and
    /// remember which call site wrote it.
    /// </summary>
    /// <remarks>
    /// The call site is recorded rather than derived because the exemption ledger is keyed by it:
    /// <see cref="Verify"/> has to be able to say WHICH callback never compared anything, and
    /// <c>_where</c> names the fixture, not the source.
    /// </remarks>
    private void Invoke(
        string name,
        JsonElement value,
        Action<FixtureValue> check,
        string? file,
        string? member)
    {
        ArgumentNullException.ThrowIfNull(check);
        var site = CallSiteOf(file, member);
        _sites[name] = site;
        var seam = new ComparisonSeam(() => _compared.Add(name), site, name, _where);
        Guarded(name, () => check(new FixtureValue(value, seam)));
    }

    private static string CallSiteOf(string? file, string? member) =>
        $"{Path.GetFileName(file) ?? "?"}::{member ?? "?"}";

    /// <summary>
    /// Run a comparison THIS class wrote over <paramref name="name"/>'s value, and book the key
    /// compared.
    /// </summary>
    /// <remarks>
    /// The typed <see cref="AssertKey(string, bool)"/> family and the structural forms below need
    /// no seam: the tracker supplies both the fixture's value and the comparison, so "the fixture's
    /// own value reached a comparison" is not a claim about a closure it cannot see. Routing them
    /// through the public callback form instead would book every one of them against
    /// <c>FixtureAssertions.cs</c> as the call site, which is the wrong file to hold an exemption.
    /// </remarks>
    private void AssertKeyOwned(
        string name,
        Action<JsonElement> compare,
        string? file = null,
        string? member = null)
    {
        _read.Add(name);
        var value = _block.GetProperty(name);
        Guarded(name, () => compare(value));
        Book(name, file, member);
        MarkAsserted(name);
    }

    /// <summary>
    /// Book <paramref name="name"/> COMPARED through the seam, so the stale-exemption direction
    /// fires here exactly as it does for a callback (<c>#lzcsuncomparedvalues</c>).
    /// </summary>
    /// <remarks>
    /// Routing the tracker-owned comparisons through the same seam is what stops
    /// <see cref="UncomparedCallSites"/> from having a blind spot: an entry naming a member whose
    /// keys all go through <see cref="AssertKey(string, bool)"/> would otherwise sit there forever,
    /// exempting nothing and reported by nothing.
    /// </remarks>
    private void Book(string name, string? file, string? member) =>
        new ComparisonSeam(
            () => _compared.Add(name),
            CallSiteOf(file, member),
            name,
            _where).Compared();

    /// <summary>
    /// Mark <paramref name="name"/> asserted and return <paramref name="project"/> applied to the
    /// fixture's value.
    /// </summary>
    /// <remarks>
    /// For a field that reaches its comparison as part of a COMPOSITE the caller assembles — an
    /// expected record built out of several sibling keys and compared once. The fixture's value
    /// still reaches the comparison, which is the requirement; what this does not do is compare
    /// on its own, so it belongs only where the composite assertion immediately follows.
    /// </remarks>
    public T AssertKeyInto<T>(
        string name,
        Func<JsonElement, T> project,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        _read.Add(name);
        var value = _block.GetProperty(name);
        _sites[name] = CallSiteOf(file, member);
        _projected.Add(name);
        var projected = project(value);
        MarkAsserted(name);
        return projected;
    }

    /// <summary>
    /// Build a COMPOSITE out of several of this block's keys and compare it against
    /// <paramref name="actual"/> — the record the run really produced — booking exactly the keys
    /// <paramref name="build"/> projected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The composite form of the comparison seam (<c>#lzcsuncomparedvalues</c>). An
    /// <see cref="AssertKeyInto"/> on its own books a key ASSERTED without any comparison having
    /// happened: the projection may be an input the runner feeds to an op, or a field of a record
    /// nobody ends up comparing. Sequencing the build here is what turns "these keys reached the
    /// comparison" into something observed rather than assumed — only keys projected DURING
    /// <paramref name="build"/> are booked, so a field dropped from the composite stops being
    /// booked without anyone editing this call.
    /// </para>
    /// <para>
    /// What it still does not prove is that <paramref name="build"/> used each projection in the
    /// record it returns. That gap is why the plain <see cref="AssertKeyInto"/> path books no
    /// comparison at all and has to be declared in <see cref="UncomparedCallSites"/>.
    /// </para>
    /// </remarks>
    public void CompareInto<TWant, TActual>(
        Func<FixtureAssertions, TWant> build,
        TActual actual,
        Action<TWant, TActual> compare,
        [CallerArgumentExpression(nameof(actual))] string? actualExpression = null,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(compare);
        var site = CallSiteOf(file, member);
        var before = new HashSet<string>(_projected, StringComparer.Ordinal);
        var want = build(this);
        var used = _projected.Where(name => !before.Contains(name)).ToArray();
        if (used.Length == 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: CompareInto projected no key of this block — a composite assembled "
                + "from nothing is compared against nothing");
        foreach (var name in used)
        {
            var seam = new ComparisonSeam(() => _compared.Add(name), site, name, _where);
            seam.RejectLiteralOperand(actualExpression);
        }

        compare(want, actual);
        foreach (var name in used)
            new ComparisonSeam(() => _compared.Add(name), site, name, _where).Compared();
    }

    /// <summary>
    /// <see cref="AssertKeyWith"/> for a key the fixture may omit; returns whether it ran.
    /// </summary>
    public bool TryAssertKeyWith(
        string name,
        Action<FixtureValue> check,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null)
    {
        _read.Add(name);
        if (!_block.TryGetProperty(name, out var value)) return false;
        Invoke(name, value, check, file, member);
        MarkAsserted(name);
        return true;
    }

    // ---------------------------------------------------------------------------
    // Object-valued keys (#lzsubblockkeyset).
    //
    // An assertion key whose value is an OBJECT carries two obligations: the value of each
    // sub-field, and the sub-field KEY SET. Discharging only the first makes the object the
    // null form one level down — a field added to it upstream is compared by nothing, and
    // the fixture reports clean over the very change it exists to catch. lazily-zig's corpus
    // perturbation pass found exactly that: a key planted inside `arena_blob.json`'s
    // `descriptor` left the suite green while every scalar sibling reddened.
    //
    // The obligation lives HERE rather than at the call site. A per-call-site field count
    // relies on the next author remembering, which is the property this rung removes:
    // Verify() fails an object-valued key that reached neither of the two entry points below.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Consume the OBJECT under <paramref name="name"/> by descending into it: <paramref
    /// name="check"/> receives a child tracker bound to the object, and every sub-field it
    /// leaves unconsumed fails exactly as an unconsumed top-level key does.
    /// </summary>
    /// <remarks>
    /// The form to prefer. The obligation moves DOWN rather than being restated — the child
    /// runs the same unread / read-but-not-asserted / stale-excuse rungs the parent does, so
    /// a sub-field the corpus grows later fails without anyone having edited this call site.
    /// </remarks>
    public void AssertObjectKey(
        string name,
        Action<FixtureAssertions> check,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        Descend(name, _block.GetProperty(name), check, file, member);

    /// <summary>
    /// <see cref="AssertObjectKey"/> for a key the fixture may omit; returns whether it ran.
    /// </summary>
    public bool TryAssertObjectKey(
        string name,
        Action<FixtureAssertions> check,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null)
    {
        _read.Add(name);
        if (!_block.TryGetProperty(name, out var value)) return false;
        Descend(name, value, check, file, member);
        return true;
    }

    private void Descend(
        string name,
        JsonElement value,
        Action<FixtureAssertions> check,
        string? file = null,
        string? member = null)
    {
        _read.Add(name);
        if (value.ValueKind != JsonValueKind.Object)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: AssertObjectKey('{name}') on a {value.ValueKind} value — "
                + "descending is for an object; use AssertKey/AssertKeyWith");
        MarkAsserted(name);
        _descended.Add(name);
        // The obligation moved DOWN: the child runs the comparison rung over every sub-field, so
        // the parent key is discharged by the child's Verify(), not by this call returning.
        Book(name, file, member);
        var child = new FixtureAssertions(value, $"{_where}.{name}", _ledger, recordBind: false);
        Guarded(name, () =>
        {
            check(child);
            child.Verify();
        });
    }

    /// <summary>
    /// Consume the OBJECT under <paramref name="name"/> by comparing its KEY SET against
    /// <paramref name="actual"/> — the set the run really produced.
    /// </summary>
    /// <remarks>
    /// The form for a key whose sub-fields are a VOCABULARY rather than data:
    /// <c>nodeid_exact_range.json</c>'s <c>outcomes</c> maps outcome tokens to English
    /// glosses, and the assertion is which tokens exist, not what the glosses say. The
    /// comparison is two-directional by construction — a fixture token nothing replayed and a
    /// replayed token the fixture omits are each failures, and only the second direction
    /// catches a runner that stopped exercising an arm.
    /// </remarks>
    /// <summary>
    /// Consume the OBJECT under <paramref name="name"/> by comparing the WHOLE value
    /// structurally against <paramref name="actual"/>.
    /// </summary>
    /// <remarks>
    /// The third discharge, for a key compared by deep equality rather than field by field: a
    /// structural comparison already covers the key set, in both directions, at every depth. It
    /// is a separate entry point rather than a comment beside <see cref="AssertKeyWith"/>
    /// because the tracker cannot see inside a closure — the point of <c>#lzsubblockkeyset</c>
    /// is that "this really did compare everything" is a claim the guard checks, not one the
    /// call site asserts.
    /// </remarks>
    public void AssertKeyDeep(
        string name,
        System.Text.Json.Nodes.JsonNode? actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        DeepCompare(name, _block.GetProperty(name), actual, file, member);

    /// <summary>
    /// <see cref="AssertKeyDeep"/> for a key the fixture may omit; returns whether it ran.
    /// </summary>
    public bool TryAssertKeyDeep(
        string name,
        Func<System.Text.Json.Nodes.JsonNode?> actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null)
    {
        _read.Add(name);
        if (!_block.TryGetProperty(name, out var value)) return false;
        DeepCompare(name, value, actual(), file, member);
        return true;
    }

    private void DeepCompare(
        string name,
        JsonElement value,
        System.Text.Json.Nodes.JsonNode? actual,
        string? file = null,
        string? member = null)
    {
        _read.Add(name);
        MarkAsserted(name);
        _descended.Add(name);
        Book(name, file, member);
        Guarded(name, () => Xunit.Assert.True(
            System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText()),
                actual),
            $"{_where}: `{name}` — fixture and runner disagree structurally"));
    }

    public void AssertKeySet(
        string name,
        IEnumerable<string> actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null)
    {
        _read.Add(name);
        var value = _block.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Object)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: AssertKeySet('{name}') on a {value.ValueKind} value — "
                + "the key set of a non-object is not a thing this can compare");
        MarkAsserted(name);
        _descended.Add(name);
        Book(name, file, member);
        var want = value.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var got = actual.Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        Guarded(name, () => Xunit.Assert.Equal(want, got));
    }

    /// <summary>Assert <paramref name="name"/>'s boolean value equals <paramref name="actual"/>.</summary>
    public void AssertKey(
        string name,
        bool actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        AssertKeyOwned(name, want => Xunit.Assert.Equal(want.GetBoolean(), actual), file, member);

    /// <summary>Assert <paramref name="name"/>'s integer value equals <paramref name="actual"/>.</summary>
    public void AssertKey(
        string name,
        int actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        AssertKeyOwned(name, want => Xunit.Assert.Equal(want.GetInt32(), actual), file, member);

    /// <inheritdoc cref="AssertKey(string, int)"/>
    public void AssertKey(
        string name,
        long actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        AssertKeyOwned(name, want => Xunit.Assert.Equal(want.GetInt64(), actual), file, member);

    /// <inheritdoc cref="AssertKey(string, int)"/>
    public void AssertKey(
        string name,
        ulong actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        AssertKeyOwned(name, want => Xunit.Assert.Equal(want.GetUInt64(), actual), file, member);

    /// <summary>Assert <paramref name="name"/>'s numeric value equals <paramref name="actual"/> within <paramref name="tolerance"/>.</summary>
    public void AssertKey(
        string name,
        double actual,
        double tolerance,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        AssertKeyOwned(
            name,
            want => Xunit.Assert.True(
                Math.Abs(want.GetDouble() - actual) <= tolerance,
                $"expected {want.GetDouble()} ± {tolerance}, got {actual}"),
            file,
            member);

    /// <summary>Assert <paramref name="name"/>'s string value equals <paramref name="actual"/>.</summary>
    public void AssertKey(
        string name,
        string? actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        AssertKeyOwned(name, want => Xunit.Assert.Equal(want.GetString(), actual), file, member);

    /// <summary>Assert <paramref name="name"/>'s array of strings equals <paramref name="actual"/>.</summary>
    public void AssertKey(
        string name,
        IEnumerable<string?> actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        AssertKeyOwned(
            name,
            want => Xunit.Assert.Equal(
                want.EnumerateArray().Select(item => item.GetString()).ToArray(),
                actual.ToArray()),
            file,
            member);

    /// <summary>Assert <paramref name="name"/>'s array of integers equals <paramref name="actual"/>.</summary>
    public void AssertKey(
        string name,
        IEnumerable<long> actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        AssertKeyOwned(
            name,
            want => Xunit.Assert.Equal(
                want.EnumerateArray().Select(item => item.GetInt64()).ToArray(),
                actual.ToArray()),
            file,
            member);

    /// <inheritdoc cref="AssertKey(string, IEnumerable{long})"/>
    public void AssertKey(
        string name,
        IEnumerable<int> actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        AssertKeyOwned(
            name,
            want => Xunit.Assert.Equal(
                want.EnumerateArray().Select(item => item.GetInt32()).ToArray(),
                actual.ToArray()),
            file,
            member);

    /// <summary>Assert <paramref name="name"/>'s array of bytes equals <paramref name="actual"/>.</summary>
    public void AssertKey(
        string name,
        IEnumerable<byte> actual,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null) =>
        AssertKeyOwned(
            name,
            want => Xunit.Assert.Equal(
                want.EnumerateArray().Select(item => item.GetByte()).ToArray(),
                actual.ToArray()),
            file,
            member);

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

        // #lzcsuncomparedvalues. A key the runner ASSERTED whose value never reached a comparison
        // the tracker owns. Every rung above is satisfied — it is read, asserted, not excused —
        // by a callback that returned. `nonterminal_outcomes` is the live example: it read every
        // element of the fixture's array, asserted a property of the ReceiptOutcome ENUM, and
        // touched the projection with nothing. This is the rung that separates "the callback
        // returned" from "the fixture's own value was compared against what the run produced".
        var uncompared = present
            .Where(name => _asserted.Contains(name)
                && !_compared.Contains(name)
                && !_excused.ContainsKey(name)
                && !_discharged.ContainsKey(name)
                && !UncomparedCallSites.Declared.ContainsKey(SiteOf(name)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (uncompared.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: assertion key(s) "
                + $"[{string.Join(", ", uncompared.Select(name => $"{name} ({SiteOf(name)})"))}] "
                + "were asserted by a callback that never compared the fixture's own value "
                + "against anything the run produced — a callback that returns is not a "
                + "comparison, and one over an empty sequence returns whatever it was given. "
                + "Route the comparison through the value's own seam (`want.Against(got, ...)`, "
                + "`want.AssertEqual(decode, got)`, `want.Compare(decode, got)`), or declare the "
                + "call site in UncomparedCallSites with a reason");

        // #lzsubblockkeyset. An OBJECT value that was asserted without its key set being
        // checked is the null form one level down: the five sub-fields a call site named are
        // compared, and a sixth the corpus grows later is compared by nothing. Every rung
        // above is satisfied — the key is read, asserted, and not excused — by a check that
        // cannot fail. This is the one that cannot be satisfied by remembering.
        var undescended = present
            .Where(name => _asserted.Contains(name)
                && !_descended.Contains(name)
                && _block.GetProperty(name).ValueKind == JsonValueKind.Object)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (undescended.Length > 0)
            throw new Xunit.Sdk.XunitException(
                $"{_where}: object-valued assertion key(s) "
                + $"[{string.Join(", ", undescended)}] consumed without a key-set check — a "
                + "sub-field the corpus adds later would be compared by nothing here; "
                + "descend with AssertObjectKey, or compare the vocabulary with AssertKeySet");
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
        _compared.Add(ProseDeclaration);
        _ledger?.BlockVerified(_where, proseNames);
        return declared;
    }

    /// <summary>The call site that wrote <paramref name="name"/>'s check, for the ledger key.</summary>
    private string SiteOf(string name) =>
        _sites.TryGetValue(name, out var site) ? site : "?::?";

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
