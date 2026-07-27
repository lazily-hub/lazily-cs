using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lazily;

/// <summary>A text block, optionally carrying an in-band anchor.</summary>
/// <param name="Text">The block body.</param>
/// <param name="Anchor">The in-band anchor, or null when the block is unanchored.</param>
public readonly record struct Block(string Text, string? Anchor = null);

/// <summary>How a block key was manufactured.</summary>
public enum BlockKeyKind
{
    /// <summary>From an in-band anchor: exact, and survives a full body rewrite.</summary>
    Anchored,

    /// <summary>From a hash of the whitespace-normalized body: survives reflow, changes on edit.</summary>
    Content,
}

/// <summary>A manufactured block key.</summary>
/// <remarks>
/// The <c>a:</c> / <c>c:</c> wire prefixes keep the two keyspaces from ever colliding — an anchor
/// literally named like a hash must not be mistaken for one.
/// </remarks>
public readonly record struct BlockKey
{
    private BlockKey(BlockKeyKind kind, string anchor, ulong content)
    {
        Kind = kind;
        AnchorValue = anchor;
        ContentValue = content;
    }

    /// <summary>The anchored-key wire prefix.</summary>
    public const string AnchorPrefix = "a:";

    /// <summary>The content-key wire prefix.</summary>
    public const string ContentPrefix = "c:";

    /// <summary>How this key was manufactured.</summary>
    public BlockKeyKind Kind { get; }

    /// <summary>The anchor, when <see cref="Kind"/> is <see cref="BlockKeyKind.Anchored"/>.</summary>
    public string AnchorValue { get; }

    /// <summary>The content hash, when <see cref="Kind"/> is <see cref="BlockKeyKind.Content"/>.</summary>
    public ulong ContentValue { get; }

    /// <summary>An anchored key.</summary>
    /// <param name="value">The anchor.</param>
    /// <returns>The key.</returns>
    public static BlockKey Anchored(string value) => new(BlockKeyKind.Anchored, value, 0);

    /// <summary>A content-derived key.</summary>
    /// <param name="value">The content hash.</param>
    /// <returns>The key.</returns>
    public static BlockKey Content(ulong value) => new(BlockKeyKind.Content, "", value);

    /// <summary>The wire form: <c>a:&lt;anchor&gt;</c> or <c>c:</c> plus 16 hex digits.</summary>
    /// <returns>The wire form.</returns>
    public override string ToString() => Kind is BlockKeyKind.Anchored
        ? AnchorPrefix + AnchorValue
        : ContentPrefix + ContentValue.ToString("x16", CultureInfo.InvariantCulture);
}

/// <summary>How a new block matched against the old set.</summary>
public enum MatchKind
{
    /// <summary>Keys matched exactly.</summary>
    Same,

    /// <summary>No key match, but similarity cleared the threshold — identity is inherited.</summary>
    Edited,

    /// <summary>Nothing similar enough. A genuinely new block.</summary>
    Inserted,
}

/// <summary>One new block's match against the old set.</summary>
/// <param name="Kind">The match kind.</param>
/// <param name="OldIndex">The matched old index, or -1 when inserted.</param>
/// <param name="Similarity">The word-LCS similarity, 1.0 for an exact key match and 0.0 for an insert.</param>
public readonly record struct Match(MatchKind Kind, int OldIndex, double Similarity)
{
    /// <summary>The wire form: <c>Same:&lt;i&gt;</c>, <c>Edited:&lt;i&gt;</c>, or <c>Inserted</c>.</summary>
    /// <returns>The wire form.</returns>
    public override string ToString() =>
        Kind is MatchKind.Inserted
            ? "Inserted"
            : FormattableString.Invariant($"{Kind}:{OldIndex}");
}

/// <summary>The alignment of a new block sequence against an old one.</summary>
/// <param name="NewMatches">One match per new block.</param>
/// <param name="Removed">Old indices nothing matched.</param>
public sealed record Alignment(IReadOnlyList<Match> NewMatches, IReadOnlyList<int> Removed);

/// <summary>
/// Manufactured identity for text: three layers that let a document keep stable keys across edits it
/// never asked to be tracked through.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>In-band anchors — exact, and survive a complete body rewrite.</item>
/// <item>Content hashes over whitespace-normalized text — survive reflow and reorder, change on a
/// real edit.</item>
/// <item>Word-LCS similarity — at or above <see cref="EditThreshold"/> a block is Edited and
/// inherits its predecessor's key; below it, the block is genuinely Inserted.</item>
/// </list>
/// The third layer is what keeps a one-word edit from reading as a delete plus an insert, which
/// would throw away every piece of state keyed to that block.
/// </remarks>
public static partial class StableId
{
    /// <summary>Below this similarity a match is treated as an insert rather than an edit.</summary>
    public const double EditThreshold = 0.5;

    /// <summary>Collapses whitespace runs to single spaces and trims.</summary>
    /// <param name="text">The text to normalize.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string text) =>
        string.Join(" ", Whitespace().Split(text ?? "").Where(p => p.Length > 0));

    /// <summary>The FNV-1a 64-bit hash of the UTF-8 bytes of <see cref="Normalize"/>d text.</summary>
    /// <remarks>Cross-language stable: the sibling bindings hash the same normalized byte sequence.</remarks>
    /// <param name="text">The text to hash.</param>
    /// <returns>The content hash.</returns>
    public static ulong ContentHash(string text)
    {
        const ulong Offset = 0xcbf29ce484222325;
        const ulong Prime = 0x100000001b3;
        var hash = Offset;
        foreach (var b in Encoding.UTF8.GetBytes(Normalize(text)))
        {
            hash ^= b;
            hash *= Prime;
        }

        return hash;
    }

    /// <summary>The manufactured key for <paramref name="block"/>: its anchor when it has one, else its content hash.</summary>
    /// <param name="block">The block.</param>
    /// <returns>The block's key.</returns>
    public static BlockKey KeyOf(Block block) =>
        block.Anchor is { } anchor ? BlockKey.Anchored(anchor) : BlockKey.Content(ContentHash(block.Text));

    /// <summary>Word-LCS similarity in [0, 1]: <c>2·LCS / (|a| + |b|)</c> over whitespace tokens.</summary>
    /// <param name="a">One text.</param>
    /// <param name="b">The other.</param>
    /// <returns>The similarity.</returns>
    public static double Similarity(string a, string b)
    {
        var ta = Tokenize(a);
        var tb = Tokenize(b);
        if (ta.Length == 0 && tb.Length == 0) return 1.0;
        if (ta.Length == 0 || tb.Length == 0) return 0.0;
        return 2.0 * LcsLength(ta, tb) / (ta.Length + tb.Length);
    }

    /// <summary>
    /// Aligns <paramref name="newBlocks"/> against <paramref name="oldBlocks"/>: exact key matches
    /// first, then similarity with a nearest-index tiebreak.
    /// </summary>
    /// <remarks>
    /// Exact matching runs as its own complete pass BEFORE any similarity matching. Interleaving them
    /// would let a merely-similar block consume an old entry that some later block matches exactly,
    /// which is how a pure reorder degrades into a pile of edits.
    /// </remarks>
    /// <param name="oldBlocks">The prior blocks.</param>
    /// <param name="newBlocks">The new blocks.</param>
    /// <returns>The alignment.</returns>
    public static Alignment Align(IReadOnlyList<Block> oldBlocks, IReadOnlyList<Block> newBlocks)
    {
        Guard.NotNull(oldBlocks, nameof(oldBlocks));
        Guard.NotNull(newBlocks, nameof(newBlocks));

        var oldKeys = oldBlocks.Select(KeyOf).ToArray();
        var newKeys = newBlocks.Select(KeyOf).ToArray();
        var oldUsed = new bool[oldBlocks.Count];
        var matches = new Match[newBlocks.Count];
        var matched = new bool[newBlocks.Count];

        // Pass 1: exact key match, taking the lowest unused old index.
        for (var ni = 0; ni < newBlocks.Count; ni++)
        {
            for (var oi = 0; oi < oldBlocks.Count; oi++)
            {
                if (oldUsed[oi] || !newKeys[ni].Equals(oldKeys[oi])) continue;
                matches[ni] = new Match(MatchKind.Same, oi, 1.0);
                matched[ni] = true;
                oldUsed[oi] = true;
                break;
            }
        }

        // Pass 2: similarity match for whatever is left.
        for (var ni = 0; ni < newBlocks.Count; ni++)
        {
            if (matched[ni]) continue;

            var bestOi = -1;
            var bestSim = 0.0;
            var bestDist = int.MaxValue;
            for (var oi = 0; oi < oldBlocks.Count; oi++)
            {
                if (oldUsed[oi]) continue;
                var sim = Similarity(newBlocks[ni].Text, oldBlocks[oi].Text);
                var dist = Math.Abs(oi - ni);
                if (sim > bestSim || (sim == bestSim && sim >= EditThreshold && dist < bestDist))
                {
                    bestSim = sim;
                    bestOi = oi;
                    bestDist = dist;
                }
            }

            if (bestOi >= 0 && bestSim >= EditThreshold)
            {
                matches[ni] = new Match(MatchKind.Edited, bestOi, bestSim);
                oldUsed[bestOi] = true;
            }
            else
            {
                matches[ni] = new Match(MatchKind.Inserted, -1, 0.0);
            }
        }

        var removed = Enumerable.Range(0, oldBlocks.Count).Where(oi => !oldUsed[oi]).ToArray();
        return new Alignment(matches, removed);
    }

    /// <summary>
    /// Assigns stable keys to <paramref name="newBlocks"/> by flowing identity through the alignment.
    /// </summary>
    /// <remarks>Same and Edited inherit their predecessor's key; Inserted gets a fresh one.</remarks>
    /// <param name="oldBlocks">The prior blocks.</param>
    /// <param name="newBlocks">The new blocks.</param>
    /// <returns>One key per new block, in order.</returns>
    public static IReadOnlyList<string> AssignStableKeys(
        IReadOnlyList<Block> oldBlocks,
        IReadOnlyList<Block> newBlocks)
    {
        Guard.NotNull(newBlocks, nameof(newBlocks));
        var alignment = Align(oldBlocks, newBlocks);
        return [.. newBlocks.Select((b, ni) =>
        {
            var m = alignment.NewMatches[ni];
            return m.Kind is MatchKind.Inserted ? KeyOf(b).ToString() : KeyOf(oldBlocks[m.OldIndex]).ToString();
        })];
    }

    private static string[] Tokenize(string text) =>
        [.. Whitespace().Split(text ?? "").Where(p => p.Length > 0)];

    private static int LcsLength(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var prev = new int[b.Count + 1];
        var cur = new int[b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        {
            for (var j = 1; j <= b.Count; j++)
            {
                cur[j] = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal)
                    ? prev[j - 1] + 1
                    : Math.Max(prev[j], cur[j - 1]);
            }

            (prev, cur) = (cur, prev);
            Array.Clear(cur, 0, cur.Length);
        }

        return prev[b.Count];
    }

    private static Regex Whitespace() => new(@"\s+", RegexOptions.Compiled);
}
