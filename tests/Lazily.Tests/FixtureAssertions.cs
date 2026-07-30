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
/// </remarks>
public sealed class FixtureAssertions
{
    /// <summary>
    /// Keys that are prose for a human reader, not assertions.
    /// </summary>
    /// <remarks>
    /// The ONLY allowlist here, and deliberately tiny. An assertion a binding does not
    /// implement does NOT belong in it — it belongs in the binding.
    /// </remarks>
    private static readonly HashSet<string> ProseKeys =
        new(StringComparer.Ordinal) { "comment", "description", "note", "notes", "why" };

    private readonly JsonElement _block;
    private readonly string _where;
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asserted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _excused = new(StringComparer.Ordinal);

    private FixtureAssertions(JsonElement block, string where)
    {
        _block = block;
        _where = where;
    }

    /// <summary>Track <paramref name="owner"/>'s <paramref name="property"/> block.</summary>
    /// <param name="owner">The step, scenario, or fixture root carrying the block.</param>
    /// <param name="property">Usually <c>expect</c>, <c>expected</c>, or <c>assertions</c>.</param>
    /// <param name="where">Names the fixture and position for the failure message.</param>
    public static FixtureAssertions Of(JsonElement owner, string property, string where) =>
        new(owner.GetProperty(property), where);

    /// <summary>Track a block the caller already holds.</summary>
    public static FixtureAssertions Wrap(JsonElement block, string where) => new(block, where);

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
        _asserted.Add(name);
        Guarded(name, () => check(value));
    }

    /// <summary>
    /// <see cref="AssertKeyWith"/> for a key the fixture may omit; returns whether it ran.
    /// </summary>
    public bool TryAssertKeyWith(string name, Action<JsonElement> check)
    {
        _read.Add(name);
        if (!_block.TryGetProperty(name, out var value)) return false;
        _asserted.Add(name);
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
    /// Fail when a key was never read, was read but never asserted, or carries a stale
    /// excuse.
    /// </summary>
    public void Verify()
    {
        if (_block.ValueKind != JsonValueKind.Object) return;
        var present = _block.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !ProseKeys.Contains(name))
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
            .Where(name => !_asserted.Contains(name) && !_excused.ContainsKey(name))
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
