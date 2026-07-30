using System.Text.Json;

namespace Lazily.Tests;

/// <summary>
/// A fixture assertion block that remembers which of its keys a runner read, and
/// fails when any key was left unconsumed (<c>#lzassertunknownkeys</c>).
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
    /// enumerates goes on to evaluate each member, and the ones with a throwing default
    /// were already fail-closed — they can call <see cref="MarkConsumed"/> if they need to
    /// say so.
    /// </remarks>
    public JsonElement.ObjectEnumerator EnumerateObject() => _block.EnumerateObject();

    /// <summary>Record <paramref name="name"/> as evaluated by some other route.</summary>
    public void MarkConsumed(string name) => _read.Add(name);

    /// <summary>Fail when the block carries a key no runner read.</summary>
    public void Verify()
    {
        if (_block.ValueKind != JsonValueKind.Object) return;
        var unread = _block.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !_read.Contains(name) && !ProseKeys.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (unread.Length == 0) return;
        throw new Xunit.Sdk.XunitException(
            $"{_where}: unconsumed assertion key(s) [{string.Join(", ", unread)}] — the "
            + "fixture asserts something this runner never evaluated, so replaying it "
            + "proves nothing about that field");
    }
}
