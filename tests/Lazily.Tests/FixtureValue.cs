using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Lazily.Tests;

/// <summary>The outcome of one tracker-owned comparison, for a runner that RECORDS divergences.</summary>
/// <remarks>
/// The divergence-recording runners in this repo do not throw on a mismatch — they append to a
/// ledger that the test asserts against a declared set at the end. <see cref="FixtureValue.Compare"/>
/// hands them this instead of throwing, so the comparison becomes visible to the tracker without
/// changing what the runner does with the result.
/// </remarks>
public readonly record struct Divergence<T>(bool Diverged, T Want, T Got);

/// <summary>
/// The fixture's own value for one assertion key, plus the seam through which a comparison
/// against it becomes OBSERVABLE to <see cref="FixtureAssertions"/> (<c>#lzcsuncomparedvalues</c>).
/// </summary>
/// <remarks>
/// <para>
/// The defect this closes: <see cref="FixtureAssertions.AssertKeyWith"/> booked a key SATISFIED
/// when its callback RETURNED, which is not the same fact as "a comparison happened". The live
/// example was <c>CausalReceiptConformanceTests</c> before 1df7e7e:
/// <c>AssertKeyWith("nonterminal_outcomes", want =&gt; Assert.All(want.EnumerateArray(), item =&gt;
/// Assert.False(ParseOutcome(item.GetString()!).IsTerminal())))</c>. That callback read every
/// element of the fixture's array and then asserted a property of the <c>ReceiptOutcome</c> ENUM;
/// it never touched the projection. <c>Assert.All</c> over an empty sequence is vacuously true, so
/// <c>[]</c> passed, dropping an element passed, and reordering passed — and the key was booked
/// SATISFIED, which left every unconsumed-key rung blind to it.
/// </para>
/// <para>
/// The seam inverts who owns the expected operand. A callback no longer compares the fixture's
/// value to something itself; it hands the tracker the value the RUN produced and the comparison
/// to perform, and the tracker supplies the fixture's own value from
/// <see cref="FixtureAssertions"/>'s block. So "compared against the fixture's own value" is true
/// by construction rather than by inspection, and a callback that never reaches
/// <see cref="Against"/> books no comparison at all — which is what
/// <see cref="FixtureAssertions.Verify"/> now fails on.
/// </para>
/// <para>
/// The read surface mirrors <see cref="JsonElement"/>'s member for member, and there is an
/// implicit conversion back to it, so adopting this at a call site is a change of what the
/// callback DOES, never a change of how it reads. Reading is still not asserting: the reads
/// below record nothing, exactly as <see cref="FixtureAssertions.GetProperty"/> records nothing
/// beyond consumption.
/// </para>
/// <para>
/// WHY NOT THE TWO MECHANISMS THAT WERE SPIKED FIRST. (1) A RECORDING WRAPPER that books the key
/// when the callback READS its argument misses this exact case — the vacuous callback read every
/// element. Recording reads answers "did the callback look at the fixture", and the question is
/// "did the fixture's value reach a comparison". (2) DIFFERENTIAL RE-EXECUTION — perturb the
/// fixture value, run the callback again, require it to fail — needs no call-site edits and does
/// catch this case, but it cannot work in this repo: the runners RECORD divergences into a ledger
/// instead of throwing, so a perturbed re-run signals nothing and instead appends phantom
/// divergences into the ledger the test then compares against its declared set. Measured on the
/// spike: 8 tests broken by the double execution and 288 keys falsely reported "insensitive".
/// This seam is the third option and the one the shape of these runners actually admits — see
/// <see cref="Compare"/>, which is a comparison the tracker owns whose RESULT the runner still
/// records rather than throws.
/// </para>
/// </remarks>
public readonly struct FixtureValue
{
    private readonly JsonElement _value;
    private readonly ComparisonSeam? _seam;

    internal FixtureValue(JsonElement value, ComparisonSeam? seam)
    {
        _value = value;
        _seam = seam;
    }

    /// <summary>Wrap a value with no seam — for a caller that only reads.</summary>
    public static FixtureValue Read(JsonElement value) => new(value, seam: null);

    /// <summary>The underlying element, for a read this surface does not mirror.</summary>
    public JsonElement Element => _value;

    public static implicit operator JsonElement(FixtureValue value) => value._value;

    // -- reads: identical to JsonElement's, and none of them books anything ------

    public JsonValueKind ValueKind => _value.ValueKind;

    public string? GetString() => _value.GetString();

    public bool GetBoolean() => _value.GetBoolean();

    public byte GetByte() => _value.GetByte();

    public int GetInt32() => _value.GetInt32();

    public long GetInt64() => _value.GetInt64();

    public ulong GetUInt64() => _value.GetUInt64();

    public uint GetUInt32() => _value.GetUInt32();

    public double GetDouble() => _value.GetDouble();

    public string GetRawText() => _value.GetRawText();

    public int GetArrayLength() => _value.GetArrayLength();

    public JsonElement.ArrayEnumerator EnumerateArray() => _value.EnumerateArray();

    public JsonElement.ObjectEnumerator EnumerateObject() => _value.EnumerateObject();

    public JsonElement GetProperty(string name) => _value.GetProperty(name);

    public bool TryGetProperty(string name, out JsonElement value) =>
        _value.TryGetProperty(name, out value);

    public override string ToString() => _value.ToString();

    // -- the seam: the ONLY route to booking this key COMPARED -------------------

    /// <summary>
    /// Compare the fixture's OWN value against <paramref name="actual"/> — the value the run
    /// produced — with <paramref name="compare"/>, and book the key as compared.
    /// </summary>
    /// <param name="actual">
    /// The run-produced operand. It is rejected when the call site wrote a compile-time LITERAL
    /// here: comparing the fixture against a hardcoded constant is the same defect one level in —
    /// editing the fixture changes the expected side and nothing checks the run.
    /// </param>
    /// <param name="compare">
    /// The comparison. It receives the fixture's value from the tracker rather than from the call
    /// site, which is what makes "a comparison against the fixture's own value" a fact the tracker
    /// holds instead of a claim the reviewer has to verify.
    /// </param>
    /// <remarks>
    /// The general form, and the one every other member here is written in terms of. It is
    /// deliberately agnostic about what the comparison DOES — throw, record a divergence, assert a
    /// containment, or a tolerance — because the property being tracked is that a comparison
    /// happened with the fixture's value on one side, not which assertion library ran.
    /// </remarks>
    public void Against<TActual>(
        TActual actual,
        Action<JsonElement, TActual> compare,
        [CallerArgumentExpression(nameof(actual))] string? actualExpression = null)
    {
        ArgumentNullException.ThrowIfNull(compare);
        _seam?.RejectLiteralOperand(actualExpression);
        compare(_value, actual);
        _seam?.Compared();
    }

    /// <summary>
    /// <see cref="Against"/> for an equality: decode the fixture's value and assert it equals
    /// <paramref name="actual"/>.
    /// </summary>
    public void AssertEqual<T>(
        Func<JsonElement, T> decode,
        T actual,
        [CallerArgumentExpression(nameof(actual))] string? actualExpression = null)
    {
        ArgumentNullException.ThrowIfNull(decode);
        Against(actual, (want, got) => Xunit.Assert.Equal(decode(want), got), actualExpression);
    }

    /// <summary>
    /// <see cref="Against"/> for a divergence-RECORDING runner: compare and hand back the result
    /// instead of throwing, so the runner appends to its ledger exactly as it does today.
    /// </summary>
    /// <remarks>
    /// This is the member that makes the seam adoptable by the runners this repo actually has.
    /// A guard that only offered a throwing comparison would have forced every
    /// <c>Check(key, got, want)</c> runner to change what it DOES with a mismatch, and a ledger
    /// that stops recording is a different test, not a guarded one.
    /// </remarks>
    public Divergence<T> Compare<T>(
        Func<JsonElement, T> decode,
        T actual,
        [CallerArgumentExpression(nameof(actual))] string? actualExpression = null)
    {
        ArgumentNullException.ThrowIfNull(decode);
        var result = default(Divergence<T>);
        Against(
            actual,
            (element, got) =>
            {
                var want = decode(element);
                result = new Divergence<T>(
                    !EqualityComparer<T>.Default.Equals(want, got),
                    want,
                    got);
            },
            actualExpression);
        return result;
    }
}

/// <summary>
/// The tracker's end of <see cref="FixtureValue"/>: books one key as compared, refuses a
/// hardcoded operand, and refuses a stale exemption.
/// </summary>
internal sealed class ComparisonSeam
{
    private readonly Action _book;

    internal ComparisonSeam(Action book, string callSite, string key, string where)
    {
        _book = book;
        CallSite = callSite;
        Key = key;
        Where = where;
    }

    /// <summary>The <c>File.cs::Member</c> this key's callback was written in.</summary>
    internal string CallSite { get; }

    internal string Key { get; }

    internal string Where { get; }

    internal void Compared()
    {
        // The exemption ledger is two-directional exactly as `ExcuseKey` and `KnownDivergences`
        // are. An entry naming a call site that DOES reach a comparison has stopped hiding
        // anything, and leaving it there would silently re-open the gate for the next
        // AssertKeyWith someone adds to that member.
        if (UncomparedCallSites.Declared.TryGetValue(CallSite, out var reason))
            throw new Xunit.Sdk.XunitException(
                $"{Where}: assertion key '{Key}' DID reach a tracker-owned comparison, but "
                + $"`{CallSite}` is declared in UncomparedCallSites — that exemption is stale and "
                + $"now hides nothing. Delete it. Its reason was: \"{reason}\"");
        _book();
    }

    internal void RejectLiteralOperand(string? expression)
    {
        if (!IsLiteral(expression)) return;
        throw new Xunit.Sdk.XunitException(
            $"{Where}: assertion key '{Key}' was compared against the literal `{expression}` — "
            + "the run-produced operand of a fixture comparison cannot be a constant written into "
            + "the test, because then editing the fixture moves only the expected side and the "
            + "library is checked by nothing. Pass what the replay produced.");
    }

    /// <summary>
    /// Whether a call site's argument EXPRESSION is a compile-time constant.
    /// </summary>
    /// <remarks>
    /// Syntactic on purpose: the value cannot answer this, because a run that produces the right
    /// answer and a literal spelling the right answer are the same bytes. The source text is the
    /// only place the difference survives, and <see cref="CallerArgumentExpressionAttribute"/> is
    /// how a call site's text reaches here. It is deliberately conservative — an operand this
    /// cannot prove constant is allowed through, since the rung above it already requires the
    /// comparison to exist at all.
    /// </remarks>
    internal static bool IsLiteral(string? expression)
    {
        var text = expression?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        if (text is "true" or "false" or "null") return true;
        if (text[0] is '"' or '\'') return true;

        // A collection of literals is a literal: `new[] { "a", "b" }` and `["a", "b"]` are the
        // shape a runner reaches for when it hardcodes an expected sequence.
        var inner = text.StartsWith('[') && text.EndsWith(']')
            ? text[1..^1]
            : text.StartsWith("new", StringComparison.Ordinal) && text.EndsWith('}')
                ? text[(text.IndexOf('{') + 1)..^1]
                : null;
        if (inner is not null && inner.Length > 0)
            return inner.Split(',').All(part => IsLiteral(part));

        var negated = text.StartsWith('-') ? text[1..].TrimStart() : text;
        return negated.Length > 0
            && char.IsAsciiDigit(negated[0])
            && negated.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_');
    }
}
