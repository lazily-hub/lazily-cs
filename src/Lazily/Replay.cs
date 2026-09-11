// Replay-equivalence proof for a reactive graph (spec tag: lzreplayproof).
//
// `lazily-spec/docs/replay-equivalence.md` draws the line: the pure cores are replay-safe and
// cell VALUES are replay-stable, so a host that re-executes your code from an event log —
// Temporal.io workflow replay, an event-sourced aggregate, a deterministic simulation — can be
// given a reactive graph. This file is what turns that from an assertion into a proof.
//
// The discipline is taken from `tsift`, whose cached excerpts are trustworthy because every one
// records a body hash and REVALIDATES it against the source bytes before the excerpt is
// returned: a stale body deterministically suppresses the cached answer rather than returning a
// plausible-looking one. Here the event log is the source bytes.
//
// Three obligations, one type each:
//
//   1. <see cref="ReplayFingerprint"/> carries the digest of the log that produced it, and
//      <see cref="ReplayHarness.Verify"/> revalidates that binding BEFORE comparing any observed
//      value. Two different logs can settle to the same final values — [+1,+2,+3] and [+3,+2,+1]
//      both sum to 6 — so a value-only comparison would PASS and certify nothing about the log in
//      front of it.
//   2. A divergence is reported at the FIRST checkpoint where the values parted, naming the cell
//      label. A fingerprint covering only the final state says the graph is wrong but not where.
//   3. <see cref="ReplayEncoding"/> is type-tagged and length-framed, and a value it does not
//      define fails loudly. Falling back on the host's default rendering embeds an identity hash
//      in most languages, which reports a FALSE divergence on every run — the exact failure a
//      replay proof exists to make impossible, arriving as a flaky test instead of a real one.
//
// Hashing is SHA-256 from the BCL. The family deliberately does not agree on a hash: fingerprints
// are pinned next to a test in ONE language and are never exchanged between bindings, so what
// must agree is the equality CLASSES the encoding induces, not the bytes. Digests here are not
// wire-compatible with lazily-py's BLAKE2b ones and are not meant to be.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Lazily;

/// <summary>A replay-equivalence proof could not be completed as stated.</summary>
/// <remarks>
/// The base of the family. Every failure below is a DISTINCT type rather than a message a caller
/// has to match on: a driver routing a stale fingerprint to "re-record" and a real divergence to
/// "the graph is broken" must not be able to confuse the two by a wording change.
/// </remarks>
public class ReplayProofException : Exception
{
    /// <summary>Creates the exception with the default message.</summary>
    public ReplayProofException()
        : base("a replay-equivalence proof could not be completed")
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    /// <param name="message">What could not be proven.</param>
    public ReplayProofException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    /// <param name="message">What could not be proven.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ReplayProofException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A value has no canonical byte encoding, so it cannot be fingerprinted.</summary>
/// <remarks>
/// Thrown instead of falling back on <see cref="object.ToString"/>, which for most types renders
/// the type name at best and an identity hash at worst. Either would make the digest a function of
/// something other than the value, so every replay would report a divergence that is not one.
/// </remarks>
public sealed class ReplayEncodingException : ReplayProofException
{
    /// <summary>Creates the exception with the default message.</summary>
    public ReplayEncodingException()
        : base("a value has no canonical replay encoding")
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    /// <param name="message">Which value, at which path, has no encoding.</param>
    public ReplayEncodingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    /// <param name="message">Which value, at which path, has no encoding.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ReplayEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The fingerprint was recorded against a DIFFERENT event log.</summary>
/// <remarks>
/// The tsift rule: revalidate the recorded hash against the source bytes and deterministically
/// suppress the cached answer when they disagree. A stale fingerprint is never compared, so it can
/// neither pass by coincidence nor be misreported as a value divergence — blaming the graph for a
/// stale test artifact is the second failure this refusal prevents.
/// </remarks>
public sealed class ReplayLogMismatchException : ReplayProofException
{
    /// <summary>Creates the exception with the default message.</summary>
    public ReplayLogMismatchException()
        : base("the fingerprint was recorded against a different event log")
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    /// <param name="message">Which digests disagreed.</param>
    public ReplayLogMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    /// <param name="message">Which digests disagreed.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ReplayLogMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception naming both log digests.</summary>
    /// <param name="expectedDigest">The log digest the fingerprint carries.</param>
    /// <param name="actualDigest">The digest of the log just replayed.</param>
    public ReplayLogMismatchException(string expectedDigest, string actualDigest)
        : base(
            $"fingerprint was recorded against a different event log (fingerprint "
            + $"log_digest={expectedDigest}, replayed log digest={actualDigest}); re-record the "
            + "fingerprint against this log")
    {
        ExpectedDigest = expectedDigest;
        ActualDigest = actualDigest;
    }

    /// <summary>The log digest the fingerprint carries.</summary>
    public string? ExpectedDigest { get; }

    /// <summary>The digest of the log that was actually replayed.</summary>
    public string? ActualDigest { get; }
}

/// <summary>The fingerprint was recorded at a different checkpoint stride.</summary>
/// <remarks>
/// A distinct type from <see cref="ReplayLogMismatchException"/> because it is a distinct fault:
/// the log is the right one, but the two checkpoint sequences were never comparable. Equal log
/// digest PLUS equal stride is what makes them comparable at all, so a harness sampling at a
/// different stride is answering a question nobody asked.
/// </remarks>
public sealed class ReplayStrideMismatchException : ReplayProofException
{
    /// <summary>Creates the exception with the default message.</summary>
    public ReplayStrideMismatchException()
        : base("the fingerprint was recorded at a different checkpoint stride")
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    /// <param name="message">Which strides disagreed.</param>
    public ReplayStrideMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    /// <param name="message">Which strides disagreed.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ReplayStrideMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception naming both strides.</summary>
    /// <param name="expectedStride">The stride the fingerprint was recorded at.</param>
    /// <param name="actualStride">The stride this harness samples at.</param>
    public ReplayStrideMismatchException(int expectedStride, int actualStride)
        : base(
            $"fingerprint was recorded at stride {expectedStride} but this harness samples at "
            + $"stride {actualStride}; re-record it")
    {
        ExpectedStride = expectedStride;
        ActualStride = actualStride;
    }

    /// <summary>The stride the fingerprint was recorded at, when known.</summary>
    public int? ExpectedStride { get; }

    /// <summary>The stride the harness samples at, when known.</summary>
    public int? ActualStride { get; }
}

/// <summary>A replayed graph observed a different value than the fingerprint recorded.</summary>
public sealed class ReplayDivergenceException : ReplayProofException
{
    private static readonly ReplayDivergence[] None = [];

    /// <summary>Creates the exception with the default message.</summary>
    public ReplayDivergenceException()
        : base("the replay diverged from the fingerprint")
    {
        Divergences = None;
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    /// <param name="message">Where the replay parted from the fingerprint.</param>
    public ReplayDivergenceException(string message)
        : base(message)
    {
        Divergences = None;
    }

    /// <summary>Creates the exception with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    /// <param name="message">Where the replay parted from the fingerprint.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ReplayDivergenceException(string message, Exception innerException)
        : base(message, innerException)
    {
        Divergences = None;
    }

    /// <summary>Creates the exception over the divergences the comparison collected.</summary>
    /// <param name="divergences">At least one divergence, earliest first.</param>
    /// <exception cref="ArgumentException">When <paramref name="divergences"/> is empty.</exception>
    public ReplayDivergenceException(IReadOnlyList<ReplayDivergence> divergences)
        : base(Describe(divergences))
    {
        Divergences = divergences;
    }

    /// <summary>Every divergence at the first diverging checkpoint, earliest first.</summary>
    public IReadOnlyList<ReplayDivergence> Divergences { get; }

    /// <summary>The earliest divergence, which is the one worth reading.</summary>
    /// <exception cref="InvalidOperationException">When no divergence was recorded.</exception>
    public ReplayDivergence First =>
        Divergences.Count > 0
            ? Divergences[0]
            : throw new InvalidOperationException("this exception carries no divergence");

    private static string Describe(IReadOnlyList<ReplayDivergence> divergences)
    {
        Guard.NotNull(divergences, nameof(divergences));
        if (divergences.Count == 0)
        {
            throw new ArgumentException(
                "a divergence exception needs at least one divergence", nameof(divergences));
        }

        var extra = divergences.Count - 1;
        var tail = extra > 0 ? $" (+{extra} more)" : string.Empty;
        return $"replay diverged from the fingerprint: {divergences[0]}{tail}";
    }
}

/// <summary>Why a cell did not replay to its recorded digest.</summary>
public enum ReplayDivergenceKind
{
    /// <summary>Both sides observed the cell, and the values differ.</summary>
    Value,

    /// <summary>The fingerprint has the cell and the replay did not observe it.</summary>
    Missing,

    /// <summary>The replay observed a cell the fingerprint does not carry.</summary>
    Unexpected,
}

/// <summary>One cell that did not replay to its recorded digest.</summary>
/// <param name="Seq">The checkpoint's sequence number, or <see cref="Replay.InitialSeq"/>.</param>
/// <param name="Label">The cell label that differed.</param>
/// <param name="Kind">Whether the value differed, or the cell itself is missing or extra.</param>
/// <param name="Expected">The recorded digest, or null when the fingerprint has no such cell.</param>
/// <param name="Actual">The replayed digest, or null when the replay observed no such cell.</param>
/// <param name="Preview">A rendering of the observed value, for the failure message only.</param>
public sealed record ReplayDivergence(
    long Seq,
    string Label,
    ReplayDivergenceKind Kind,
    string? Expected,
    string? Actual,
    string? Preview = null)
{
    /// <summary>
    /// The kind's canonical name — the vocabulary the conformance corpus speaks.
    /// </summary>
    /// <remarks>
    /// Owned HERE rather than by each driver. A runner that spelled the enum itself would be
    /// asserting its own mapping against the fixture rather than the library's.
    /// </remarks>
    public string KindName => Kind switch
    {
        ReplayDivergenceKind.Value => "value",
        ReplayDivergenceKind.Missing => "missing",
        ReplayDivergenceKind.Unexpected => "unexpected",
        _ => throw new ReplayProofException($"unknown divergence kind {Kind}"),
    };

    /// <inheritdoc/>
    public override string ToString()
    {
        var where = Seq == Replay.InitialSeq ? "initial state" : $"event seq={Seq}";
        return Kind switch
        {
            ReplayDivergenceKind.Missing =>
                $"{where}: cell '{Label}' was not observed on replay",
            ReplayDivergenceKind.Unexpected =>
                $"{where}: cell '{Label}' appeared on replay but is not in the fingerprint",
            _ => $"{where}: cell '{Label}' expected {Expected} but replayed {Actual}"
                + (Preview is null ? string.Empty : $", observed {Preview}"),
        };
    }
}

/// <summary>Constants shared by the replay-equivalence types.</summary>
public static class Replay
{
    /// <summary>The checkpoint sequence number for the state before any event was applied.</summary>
    /// <remarks>
    /// Negative on purpose: event sequence numbers are non-negative and MAY be non-contiguous, so
    /// no real epoch can collide with the initial observation.
    /// </remarks>
    public const long InitialSeq = -1;

    /// <summary>The schema version <see cref="ReplayFingerprint.ToWire"/> writes.</summary>
    public const int WireSchemaVersion = 1;
}

/// <summary>
/// The canonical observation encoding: type-tagged, length-framed, order-stable bytes.
/// </summary>
/// <remarks>
/// <para>
/// A binding MAY choose its own hash and its own byte layout. What it MUST agree on is the
/// equality CLASSES: mapping order and set order are not part of a value, sequence order is; an
/// integer, its decimal text, the equal float, the boolean and the equal byte string are five
/// different values; and members are FRAMED, so <c>["a","bc"]</c> and <c>["ab","c"]</c> cannot
/// encode alike. That last one is the easy one to get wrong, and a harness that cannot tell those
/// apart certifies a graph that reshaped its own output.
/// </para>
/// <para>
/// A frame is <c>&lt;tag&gt;&lt;decimal length&gt;:&lt;body&gt;</c>. The tag is what separates
/// <c>1</c> from <c>"1"</c>; the length is what stops a concatenation of members from being
/// ambiguous.
/// </para>
/// </remarks>
public static class ReplayEncoding
{
    private const int PreviewLimit = 120;

    /// <summary>Encodes <paramref name="value"/> to canonical bytes.</summary>
    /// <param name="value">The observed value.</param>
    /// <returns>The type-tagged, length-framed encoding.</returns>
    /// <exception cref="ReplayEncodingException">When the value has no defined encoding.</exception>
    public static byte[] Bytes(object? value) => Encode(value, "value");

    /// <summary>The SHA-256 hex digest of <see cref="Bytes"/> of <paramref name="value"/>.</summary>
    /// <param name="value">The observed value.</param>
    /// <returns>The lowercase hex digest.</returns>
    /// <exception cref="ReplayEncodingException">When the value has no defined encoding.</exception>
    public static string Digest(object? value) => Hex(Hash(Bytes(value)));

    /// <summary>A short rendering of <paramref name="value"/>, for failure messages only.</summary>
    /// <param name="value">The observed value.</param>
    /// <returns>The truncated rendering.</returns>
    /// <remarks>
    /// Deliberately NOT part of any digest. This is the host default rendering the encoding above
    /// refuses to fall back on, and it is safe here precisely because nothing compares it.
    /// </remarks>
    public static string Preview(object? value)
    {
        string text;
        try
        {
            text = value switch
            {
                null => "null",
                string item => $"\"{item}\"",
                IEnumerable sequence and not string => "["
                    + string.Join(", ", sequence.Cast<object?>().Select(Preview))
                    + "]",
                _ => value.ToString() ?? string.Empty,
            };
        }
#pragma warning disable CA1031 // a hostile ToString must not take down the failure message
        catch (Exception)
#pragma warning restore CA1031
        {
            return $"<unrenderable {value?.GetType().Name}>";
        }

        return text.Length > PreviewLimit ? text.Substring(0, PreviewLimit - 1) + "…" : text;
    }

    private static byte[] Hash(byte[] bytes)
    {
#if NETSTANDARD2_1
        using var sha = SHA256.Create();
        return sha.ComputeHash(bytes);
#else
        return SHA256.HashData(bytes);
#endif
    }

    private static string Hex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var item in bytes) builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static byte[] Frame(char tag, byte[] body)
    {
        using var buffer = new MemoryStream(body.Length + 8);
        buffer.WriteByte((byte)tag);
        var length = Encoding.ASCII.GetBytes(body.Length.ToString(CultureInfo.InvariantCulture));
        buffer.Write(length, 0, length.Length);
        buffer.WriteByte((byte)':');
        buffer.Write(body, 0, body.Length);
        return buffer.ToArray();
    }

    private static byte[] Concat(IEnumerable<byte[]> parts)
    {
        using var buffer = new MemoryStream();
        foreach (var part in parts) buffer.Write(part, 0, part.Length);
        return buffer.ToArray();
    }

    // Ordinal over the ENCODED bytes, not over the source values: mapping keys and set members
    // are not required to be mutually comparable, and two members of different types routinely
    // are not. Their encodings always are.
    private static IEnumerable<byte[]> Ordered(IEnumerable<byte[]> parts) =>
        parts.OrderBy(part => part, ByteOrdinal.Instance);

    private static byte[] Encode(object? value, string path)
    {
        switch (value)
        {
            case null:
                return Encoding.ASCII.GetBytes("n0:");
            case bool flag:
                return Encoding.ASCII.GetBytes(flag ? "b1:1" : "b1:0");
            case Enum item:
                // The NAME of the enum type is part of the value: two enums that happen to share
                // an underlying number are not the same observation.
                return Frame(
                    'e',
                    Concat([
                        Frame('s', Encoding.UTF8.GetBytes(item.GetType().FullName ?? item.GetType().Name)),
                        Frame('s', Encoding.UTF8.GetBytes(item.ToString())),
                    ]));
            case string text:
                return Frame('s', Encoding.UTF8.GetBytes(text));
            case byte[] bytes:
                return Frame('y', bytes);
            case sbyte or byte or short or ushort or int or uint or long:
                return Frame(
                    'i',
                    Encoding.ASCII.GetBytes(
                        Convert.ToInt64(value, CultureInfo.InvariantCulture)
                            .ToString(CultureInfo.InvariantCulture)));
            case ulong unsigned:
                return Frame('i', Encoding.ASCII.GetBytes(unsigned.ToString(CultureInfo.InvariantCulture)));
            case BigInteger big:
                return Frame('i', Encoding.ASCII.GetBytes(big.ToString(CultureInfo.InvariantCulture)));
            case float single:
                return EncodeDouble(single);
            case double real:
                return EncodeDouble(real);
            case decimal exact:
                // Its own tag, and its own exact text: a decimal is neither an integer nor the
                // nearest binary float, and `1.0m` and `1m` are distinct values with distinct bits.
                return Frame('c', Encoding.ASCII.GetBytes(exact.ToString(CultureInfo.InvariantCulture)));
            case ReplayEvent replayEvent:
                return Frame(
                    'v',
                    Concat([
                        Encode(replayEvent.Seq, $"{path}.seq"),
                        Encode(replayEvent.Name, $"{path}.name"),
                        Encode(replayEvent.Payload, $"{path}.payload"),
                    ]));
        }

        if (TryEntries(value, out var entries))
        {
            return Frame(
                'm',
                Concat(Ordered(entries.Select(entry => Concat([
                    Encode(entry.Key, $"{path}[key]"),
                    Encode(entry.Value, $"{path}[{entry.Key}]"),
                ])))));
        }

        if (IsSet(value) && value is IEnumerable members)
        {
            return Frame(
                't',
                Concat(Ordered(members.Cast<object?>().Select(item => Encode(item, $"{path}{{}}")))));
        }

        if (value is IEnumerable sequence)
        {
            return Frame(
                'l',
                Concat(sequence.Cast<object?>()
                    .Select((item, index) => Encode(item, $"{path}[{index}]"))));
        }

        throw new ReplayEncodingException(
            $"{path}: {value.GetType().FullName} has no canonical replay encoding; observe a "
            + "plain value, or a mapping/sequence/set of them, instead — falling back on the "
            + "host's default rendering would report a false divergence on every run");
    }

    private static byte[] EncodeDouble(double value) =>
        // The BIT PATTERN, not the shortest round-trip text: the latter folds distinct NaN
        // payloads together and renders -0.0 and 0.0 alike, both of which are real differences.
        Frame(
            'f',
            Encoding.ASCII.GetBytes(
                BitConverter.DoubleToInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture)));

    private static bool TryEntries(
        object value,
        out IEnumerable<KeyValuePair<object?, object?>> entries)
    {
        if (value is IDictionary map)
        {
            // Through the DICTIONARY enumerator, not `Cast<DictionaryEntry>()`: a generic
            // Dictionary<,> enumerated as IEnumerable yields KeyValuePair<,>, so the cast throws
            // on exactly the mapping type every observation is built from.
            entries = Entries(map);
            return true;
        }

        if (Implements(value, "IDictionary`2") || Implements(value, "IReadOnlyDictionary`2"))
        {
            // A generic-only dictionary (ImmutableDictionary, for one) still enumerates as
            // KeyValuePair<,>, which is read through its properties rather than by casting to a
            // key/value type this code cannot name.
            entries = ((IEnumerable)value).Cast<object>().Select(Entry);
            return true;
        }

        entries = [];
        return false;
    }

    private static IEnumerable<KeyValuePair<object?, object?>> Entries(IDictionary map)
    {
        var entries = map.GetEnumerator();
        while (entries.MoveNext())
        {
            yield return new KeyValuePair<object?, object?>(entries.Key, entries.Value);
        }
    }

    private static KeyValuePair<object?, object?> Entry(object pair)
    {
        var type = pair.GetType();
        var key = type.GetProperty("Key")?.GetValue(pair);
        var value = type.GetProperty("Value")?.GetValue(pair);
        return new KeyValuePair<object?, object?>(key, value);
    }

    private static bool IsSet(object value) =>
        Implements(value, "ISet`1") || Implements(value, "IReadOnlySet`1");

    private static bool Implements(object value, string interfaceName) =>
        value.GetType().GetInterfaces().Any(item => item.Name == interfaceName);

    private sealed class ByteOrdinal : IComparer<byte[]>
    {
        internal static readonly ByteOrdinal Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var shared = Math.Min(x.Length, y.Length);
            for (var index = 0; index < shared; index++)
            {
                var difference = x[index].CompareTo(y[index]);
                if (difference != 0) return difference;
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}

/// <summary>One entry of an ordered event log.</summary>
public sealed record ReplayEvent
{
    /// <summary>Creates an event.</summary>
    /// <param name="seq">The non-negative sequence number.</param>
    /// <param name="name">The non-empty event name.</param>
    /// <param name="payload">The event body, which must be canonically encodable.</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="seq"/> is negative.</exception>
    /// <exception cref="ArgumentException">When <paramref name="name"/> is empty.</exception>
    public ReplayEvent(long seq, string name, object? payload = null)
    {
        if (seq < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seq), seq, "event seq must be non-negative");
        }

        Guard.NotNullOrEmpty(name, nameof(name));
        Seq = seq;
        Name = name;
        Payload = payload;
    }

    /// <summary>The event's sequence number.</summary>
    public long Seq { get; }

    /// <summary>The event's name.</summary>
    public string Name { get; }

    /// <summary>The event's payload.</summary>
    public object? Payload { get; }
}

/// <summary>An ordered event log with a digest over its canonical bytes.</summary>
/// <remarks>
/// Sequence numbers MUST strictly increase and MAY be non-contiguous: an ack-truncated durable
/// outbox replays real epochs, and renumbering them would hide a truncated prefix that the log
/// digest otherwise catches.
/// </remarks>
public sealed class ReplayLog : IReadOnlyList<ReplayEvent>
{
    /// <summary>Creates a log from already-numbered events.</summary>
    /// <param name="events">The events, in strictly increasing sequence order.</param>
    /// <exception cref="ArgumentException">When the sequence numbers do not strictly increase.</exception>
    public ReplayLog(IEnumerable<ReplayEvent> events)
    {
        Guard.NotNull(events, nameof(events));
        var ordered = events.ToArray();
        long? previous = null;
        foreach (var item in ordered)
        {
            if (previous is { } last && item.Seq <= last)
            {
                throw new ArgumentException(
                    $"event log must be strictly increasing in seq, got {item.Seq} after {last}",
                    nameof(events));
            }

            previous = item.Seq;
        }

        Events = ordered;
        Digest = ReplayEncoding.Digest(ordered);
    }

    /// <summary>The events, in order.</summary>
    public IReadOnlyList<ReplayEvent> Events { get; }

    /// <summary>The digest of the log's canonical bytes.</summary>
    public string Digest { get; }

    /// <inheritdoc/>
    public int Count => Events.Count;

    /// <inheritdoc/>
    public ReplayEvent this[int index] => Events[index];

    /// <summary>A log from already-numbered events.</summary>
    /// <param name="events">The events, in strictly increasing sequence order.</param>
    /// <returns>The log.</returns>
    public static ReplayLog Of(params ReplayEvent[] events) => new(events);

    /// <summary>A log from <c>(name, payload)</c> pairs, numbered <c>0..n-1</c>.</summary>
    /// <param name="records">The named payloads, in order.</param>
    /// <returns>The log.</returns>
    public static ReplayLog FromRecords(IEnumerable<(string Name, object? Payload)> records)
    {
        Guard.NotNull(records, nameof(records));
        return new ReplayLog(
            records.Select((record, index) => new ReplayEvent(index, record.Name, record.Payload)));
    }

    /// <summary>A log from a reliable-sync outbox's retained frames.</summary>
    /// <param name="outbox">The outbox whose retained suffix is the replay source.</param>
    /// <param name="cursor">The caller's cursor; frames above it are replayed.</param>
    /// <param name="name">The event name given to every frame.</param>
    /// <returns>The log.</returns>
    /// <remarks>
    /// <see cref="IDurableOutbox.ReplayFrom"/> is already the replay source a reconnect drains;
    /// this makes it the fingerprinted one too. Outbox epochs become event seqs, so a truncated
    /// prefix shows up in the log digest instead of silently shifting every event. The payload is
    /// the frame's WIRE text rather than the decoded message: the retained bytes are what the peer
    /// would actually replay, and they are canonically encodable without teaching the encoding
    /// every message type in the family.
    /// </remarks>
    public static ReplayLog FromOutbox(IDurableOutbox outbox, ulong cursor = 0, string name = "frame")
    {
        Guard.NotNull(outbox, nameof(outbox));
        return new ReplayLog(
            outbox.ReplayFrom(cursor)
                .Select(entry => new ReplayEvent(
                    checked((long)entry.Epoch), name, IpcWire.Serialize(entry.Message))));
    }

    /// <inheritdoc/>
    public IEnumerator<ReplayEvent> GetEnumerator() => Events.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Per-cell digests observed after applying events through <see cref="Seq"/>.</summary>
public sealed class ReplayCheckpoint
{
    private ReplayCheckpoint(long seq, IReadOnlyDictionary<string, string> cells)
    {
        Seq = seq;
        Cells = cells;
    }

    /// <summary>The sequence number, or <see cref="Replay.InitialSeq"/> before any event.</summary>
    public long Seq { get; }

    /// <summary>Label to value digest, in ordinal label order.</summary>
    public IReadOnlyDictionary<string, string> Cells { get; }

    /// <summary>Digests every observed cell of <paramref name="observed"/>.</summary>
    /// <param name="seq">The checkpoint's sequence number.</param>
    /// <param name="observed">What the graph observed at this checkpoint.</param>
    /// <returns>The checkpoint.</returns>
    /// <exception cref="ReplayEncodingException">When an observed value has no encoding.</exception>
    public static ReplayCheckpoint Of(long seq, IReadOnlyDictionary<string, object?> observed)
    {
        Guard.NotNull(observed, nameof(observed));
        var cells = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in observed) cells[cell.Key] = ReplayEncoding.Digest(cell.Value);
        return new ReplayCheckpoint(seq, cells);
    }

    /// <summary>Rebuilds a checkpoint from already-recorded digests.</summary>
    /// <param name="seq">The checkpoint's sequence number.</param>
    /// <param name="cells">Label to value digest.</param>
    /// <returns>The checkpoint.</returns>
    public static ReplayCheckpoint FromDigests(
        long seq,
        IReadOnlyDictionary<string, string> cells)
    {
        Guard.NotNull(cells, nameof(cells));
        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in cells) ordered[cell.Key] = cell.Value;
        return new ReplayCheckpoint(seq, ordered);
    }
}

/// <summary>The JSON-safe form of one checkpoint.</summary>
/// <param name="Seq">The checkpoint's sequence number.</param>
/// <param name="Cells">Label to value digest.</param>
public sealed record ReplayCheckpointWire(long Seq, IReadOnlyDictionary<string, string> Cells);

/// <summary>The JSON-safe form of a fingerprint, so one can be committed next to a test.</summary>
/// <param name="SchemaVersion">The wire schema version.</param>
/// <param name="LogDigest">The digest of the log the fingerprint was recorded against.</param>
/// <param name="Stride">The checkpoint stride it was sampled at.</param>
/// <param name="Checkpoints">The checkpoints, in order.</param>
public sealed record ReplayFingerprintWire(
    int SchemaVersion,
    string LogDigest,
    int Stride,
    IReadOnlyList<ReplayCheckpointWire> Checkpoints);

/// <summary>A recorded, log-bound observation of a replayed graph.</summary>
public sealed class ReplayFingerprint
{
    /// <summary>Creates a fingerprint.</summary>
    /// <param name="logDigest">The digest of the log that produced it.</param>
    /// <param name="stride">The checkpoint stride it was sampled at.</param>
    /// <param name="checkpoints">The checkpoints, in order, initial state first.</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="stride"/> is below 1.</exception>
    /// <exception cref="ArgumentException">When there is not even an initial checkpoint.</exception>
    public ReplayFingerprint(
        string logDigest,
        int stride,
        IEnumerable<ReplayCheckpoint> checkpoints)
    {
        Guard.NotNullOrEmpty(logDigest, nameof(logDigest));
        Guard.NotNull(checkpoints, nameof(checkpoints));
        if (stride < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "stride must be >= 1");
        }

        var recorded = checkpoints.ToArray();
        if (recorded.Length == 0)
        {
            throw new ArgumentException(
                "a fingerprint needs at least the initial checkpoint", nameof(checkpoints));
        }

        LogDigest = logDigest;
        Stride = stride;
        Checkpoints = recorded;
        Digest = ReplayEncoding.Digest(new object?[]
        {
            logDigest,
            (long)stride,
            recorded
                .Select(checkpoint => new object?[]
                {
                    checkpoint.Seq,
                    checkpoint.Cells
                        .Select(cell => new object?[] { cell.Key, cell.Value })
                        .ToArray(),
                })
                .ToArray(),
        });
    }

    /// <summary>The digest of the log this fingerprint was recorded against.</summary>
    public string LogDigest { get; }

    /// <summary>The checkpoint stride this fingerprint was sampled at.</summary>
    public int Stride { get; }

    /// <summary>The checkpoints, in order.</summary>
    public IReadOnlyList<ReplayCheckpoint> Checkpoints { get; }

    /// <summary>A digest over the log binding, the stride, and every checkpoint.</summary>
    public string Digest { get; }

    /// <summary>The last checkpoint — the end state of the replay.</summary>
    public ReplayCheckpoint Final => Checkpoints[Checkpoints.Count - 1];

    /// <summary>The JSON-safe form.</summary>
    /// <returns>The wire record.</returns>
    public ReplayFingerprintWire ToWire() =>
        new(
            Replay.WireSchemaVersion,
            LogDigest,
            Stride,
            [.. Checkpoints.Select(checkpoint =>
                new ReplayCheckpointWire(checkpoint.Seq, checkpoint.Cells))]);

    /// <summary>Rebuilds from <see cref="ToWire"/>, refusing an unknown schema version.</summary>
    /// <param name="wire">The wire record.</param>
    /// <returns>The fingerprint.</returns>
    /// <exception cref="ReplayProofException">When the schema version is not the current one.</exception>
    public static ReplayFingerprint FromWire(ReplayFingerprintWire wire)
    {
        Guard.NotNull(wire, nameof(wire));
        if (wire.SchemaVersion != Replay.WireSchemaVersion)
        {
            throw new ReplayProofException(
                $"unsupported replay fingerprint schema_version {wire.SchemaVersion}, expected "
                + $"{Replay.WireSchemaVersion}");
        }

        return new ReplayFingerprint(
            wire.LogDigest,
            wire.Stride,
            wire.Checkpoints.Select(checkpoint =>
                ReplayCheckpoint.FromDigests(checkpoint.Seq, checkpoint.Cells)));
    }
}

/// <summary>What the harness needs from the graph it rebuilds.</summary>
public interface IReplayGraph
{
    /// <summary>Advances the graph by exactly one event.</summary>
    /// <param name="replayEvent">The event to apply.</param>
    void Apply(ReplayEvent replayEvent);

    /// <summary>The cell values the fingerprint covers, keyed by a stable label.</summary>
    /// <returns>Label to observed value.</returns>
    IReadOnlyDictionary<string, object?> Observe();
}

/// <summary>Rebuilds a graph from an event log and proves it replays identically.</summary>
/// <remarks>
/// <para>
/// The build delegate is called once per replay and MUST return a FRESH graph. A harness that
/// reuses one instance proves nothing, since the state it would compare against is the state it
/// already has.
/// </para>
/// <para>
/// <see cref="Stride"/> checkpoints every stride-th event; the initial state and the final state
/// are always checkpointed. It is recorded IN the fingerprint, so a fingerprint can never be
/// compared against a replay that sampled differently.
/// </para>
/// <para>
/// Checkpoint VALUES only. Sibling effect order is deliberately free across the family, so the
/// sequence of effects a replay fires is not a stable thing to fingerprint and this harness does
/// not try: it would be asserting a guarantee the family does not make.
/// </para>
/// </remarks>
public sealed class ReplayHarness
{
    private readonly Func<IReplayGraph> _build;

    /// <summary>Creates a harness over a graph factory.</summary>
    /// <param name="build">Returns a FRESH graph on every call.</param>
    /// <param name="stride">Checkpoint every stride-th event; 1 by default.</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="stride"/> is below 1.</exception>
    public ReplayHarness(Func<IReplayGraph> build, int stride = 1)
    {
        Guard.NotNull(build, nameof(build));
        if (stride < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "stride must be >= 1");
        }

        _build = build;
        Stride = stride;
    }

    /// <summary>The checkpoint stride this harness samples at.</summary>
    public int Stride { get; }

    /// <summary>Replays <paramref name="log"/> once and records what the graph observed.</summary>
    /// <param name="log">The event log.</param>
    /// <returns>The fingerprint, bound to this log and this stride.</returns>
    public ReplayFingerprint Record(ReplayLog log) => Drive(log).Fingerprint;

    /// <summary>Replays <paramref name="log"/> and reports the divergences from <paramref name="fingerprint"/>.</summary>
    /// <param name="log">The event log.</param>
    /// <param name="fingerprint">The recorded fingerprint.</param>
    /// <returns>The divergences at the first diverging checkpoint, or an empty list.</returns>
    /// <exception cref="ReplayLogMismatchException">When the fingerprint is bound to another log.</exception>
    /// <exception cref="ReplayStrideMismatchException">When it was recorded at another stride.</exception>
    /// <remarks>
    /// Non-raising for VALUE divergence, so a caller can report all of them. A stale fingerprint is
    /// still refused here: an unanswerable question is not a report.
    /// </remarks>
    public IReadOnlyList<ReplayDivergence> Check(ReplayLog log, ReplayFingerprint fingerprint)
    {
        Guard.NotNull(fingerprint, nameof(fingerprint));
        var (replayed, observed) = Drive(log);
        Revalidate(fingerprint, replayed);
        return Compare(fingerprint, replayed, observed);
    }

    /// <summary>Replays <paramref name="log"/> and raises unless it matches exactly.</summary>
    /// <param name="log">The event log.</param>
    /// <param name="fingerprint">The recorded fingerprint.</param>
    /// <returns>The freshly recorded fingerprint, which equals <paramref name="fingerprint"/>.</returns>
    /// <exception cref="ReplayLogMismatchException">When the fingerprint is bound to another log.</exception>
    /// <exception cref="ReplayStrideMismatchException">When it was recorded at another stride.</exception>
    /// <exception cref="ReplayDivergenceException">When a replayed value differs.</exception>
    public ReplayFingerprint Verify(ReplayLog log, ReplayFingerprint fingerprint)
    {
        Guard.NotNull(fingerprint, nameof(fingerprint));
        var (replayed, observed) = Drive(log);
        Revalidate(fingerprint, replayed);
        var divergences = Compare(fingerprint, replayed, observed);
        if (divergences.Count > 0) throw new ReplayDivergenceException(divergences);
        return replayed;
    }

    /// <summary>Records <paramref name="log"/> and re-replays it, raising on any divergence.</summary>
    /// <param name="log">The event log.</param>
    /// <param name="replays">How many replays to compare; at least 2.</param>
    /// <returns>The fingerprint every replay agreed on.</returns>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="replays"/> is below 2.</exception>
    /// <remarks>
    /// The self-check: no external fingerprint is needed to catch a graph that is not a pure
    /// function of its log, because two replays of the same log in the same process already
    /// disagree.
    /// </remarks>
    public ReplayFingerprint Prove(ReplayLog log, int replays = 2)
    {
        if (replays < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(replays), replays, "prove needs at least 2 replays to compare");
        }

        var fingerprint = Record(log);
        for (var index = 1; index < replays; index++) Verify(log, fingerprint);
        return fingerprint;
    }

    private static void Revalidate(ReplayFingerprint fingerprint, ReplayFingerprint replayed)
    {
        // The log binding FIRST, and the stride second: both are refusals rather than reports, and
        // neither may be reached by a value comparison.
        if (!string.Equals(fingerprint.LogDigest, replayed.LogDigest, StringComparison.Ordinal))
        {
            throw new ReplayLogMismatchException(fingerprint.LogDigest, replayed.LogDigest);
        }

        if (fingerprint.Stride != replayed.Stride)
        {
            throw new ReplayStrideMismatchException(fingerprint.Stride, replayed.Stride);
        }
    }

    private static IReadOnlyList<ReplayDivergence> Compare(
        ReplayFingerprint expected,
        ReplayFingerprint actual,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> observed)
    {
        var divergences = new List<ReplayDivergence>();
        var shared = Math.Min(expected.Checkpoints.Count, actual.Checkpoints.Count);
        for (var index = 0; index < shared; index++)
        {
            var want = expected.Checkpoints[index];
            var got = actual.Checkpoints[index];
            var labels = want.Cells.Keys.Concat(got.Cells.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(label => label, StringComparer.Ordinal);
            foreach (var label in labels)
            {
                var wanted = want.Cells.TryGetValue(label, out var wantDigest) ? wantDigest : null;
                var observedDigest = got.Cells.TryGetValue(label, out var gotDigest) ? gotDigest : null;
                if (string.Equals(wanted, observedDigest, StringComparison.Ordinal)) continue;
                var kind = observedDigest is null
                    ? ReplayDivergenceKind.Missing
                    : wanted is null
                        ? ReplayDivergenceKind.Unexpected
                        : ReplayDivergenceKind.Value;
                var sample = index < observed.Count ? observed[index] : null;
                divergences.Add(new ReplayDivergence(
                    want.Seq,
                    label,
                    kind,
                    wanted,
                    observedDigest,
                    sample is not null && sample.TryGetValue(label, out var value)
                        ? ReplayEncoding.Preview(value)
                        : null));
            }

            // The first diverging checkpoint is the actionable one; later ones are almost always
            // the same defect carried forward.
            if (divergences.Count > 0) break;
        }

        if (divergences.Count == 0 && expected.Checkpoints.Count != actual.Checkpoints.Count)
        {
            // Same log digest and the same stride, so this cannot come from sampling: `Observe`
            // or `Apply` changed the checkpoint count itself.
            throw new ReplayProofException(
                $"fingerprint has {expected.Checkpoints.Count} checkpoints but the replay produced "
                + $"{actual.Checkpoints.Count} for the same log");
        }

        return divergences;
    }

    private (ReplayFingerprint Fingerprint, IReadOnlyList<IReadOnlyDictionary<string, object?>> Observed) Drive(
        ReplayLog log)
    {
        Guard.NotNull(log, nameof(log));
        var graph = _build()
            ?? throw new ReplayProofException("the harness's build delegate returned no graph");
        var checkpoints = new List<ReplayCheckpoint>();
        var observed = new List<IReadOnlyDictionary<string, object?>>();

        var sample = Sample(graph);
        checkpoints.Add(ReplayCheckpoint.Of(Replay.InitialSeq, sample));
        observed.Add(sample);

        for (var index = 0; index < log.Count; index++)
        {
            var replayEvent = log[index];
            graph.Apply(replayEvent);
            if ((index + 1) % Stride != 0 && index + 1 != log.Count) continue;
            sample = Sample(graph);
            checkpoints.Add(ReplayCheckpoint.Of(replayEvent.Seq, sample));
            observed.Add(sample);
        }

        return (new ReplayFingerprint(log.Digest, Stride, checkpoints), observed);
    }

    private static IReadOnlyDictionary<string, object?> Sample(IReplayGraph graph)
    {
        var observed = graph.Observe()
            ?? throw new ReplayProofException("the graph observed nothing at a checkpoint");
        // Copied: a graph that hands back its own live dictionary would otherwise have every
        // checkpoint mutate under the comparison.
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var cell in observed) copy[cell.Key] = cell.Value;
        return copy;
    }
}
