using System;
using System.Collections.Generic;

namespace Lazily;

/// <summary>Where <see cref="SourceMap{TKey,TValue}.Insert"/> places a new key.</summary>
/// <remarks>The string values are the normative wire tokens shared with the sibling bindings.</remarks>
public enum InsertAt
{
    /// <summary>Append at the end. The default.</summary>
    End,

    /// <summary>Insert at an absolute index.</summary>
    Index,

    /// <summary>Insert immediately before the anchor.</summary>
    Before,

    /// <summary>Insert immediately after the anchor.</summary>
    After,
}

/// <summary>One keyed-reconciliation op. A sealed union of the four concrete kinds below.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public abstract record DiffOp<TKey, TValue>
    where TKey : notnull
{
    private protected DiffOp()
    {
    }
}

/// <summary>Inserts a brand-new key at its final position in the target sequence.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="Key">The new key.</param>
/// <param name="Value">Its value.</param>
/// <param name="Index">Its final index in the target order.</param>
public sealed record DiffOpInsert<TKey, TValue>(TKey Key, TValue Value, int Index) : DiffOp<TKey, TValue>
    where TKey : notnull;

/// <summary>Removes a key present in prior and absent from target.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="Key">The key to remove.</param>
public sealed record DiffOpRemove<TKey, TValue>(TKey Key) : DiffOp<TKey, TValue>
    where TKey : notnull;

/// <summary>Atomic-moves a common key to its target index, keeping its handle, dependents, and lineage.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="Key">The key to move.</param>
/// <param name="To">Its target index.</param>
public sealed record DiffOpMove<TKey, TValue>(TKey Key, int To) : DiffOp<TKey, TValue>
    where TKey : notnull;

/// <summary>Writes a common key whose value changed.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="Key">The key to write.</param>
/// <param name="Value">Its new value.</param>
public sealed record DiffOpUpdate<TKey, TValue>(TKey Key, TValue Value) : DiffOp<TKey, TValue>
    where TKey : notnull;

/// <summary>Keyed reconciliation: the minimal op set that turns a prior sequence into a target one.</summary>
/// <remarks>
/// The move set is minimized by holding the longest increasing subsequence of the common keys'
/// PRIOR indices fixed. Those keys are already in the right relative order, so they need no move —
/// and because a move is the only thing that would touch them, their value cells are not
/// invalidated by a sibling reorder. That is the whole point: reconciling
/// <c>[a,b,c,d] -&gt; [b,c,a]</c> emits exactly <c>{remove d, move a}</c>, and <c>b</c> and
/// <c>c</c> stay untouched rather than being re-minted.
/// </remarks>
public static class Reconcile
{
    /// <summary>
    /// Refuses an insert placement no map flavor can honour, BEFORE the entry is minted.
    /// </summary>
    /// <remarks>
    /// Two shapes are refused here, and both used to be absorbed by a <c>default: break</c> that
    /// appended silently and still reported success. `at` survives an unchecked cast from
    /// <c>int</c>, and an anchored placement with no anchor is the ordinary caller mistake — each
    /// produced a correct membership set at a WRONG index, and ordering is the only thing these
    /// maps add over a dictionary. Shared so the synchronous, thread-safe, and async flavors
    /// cannot disagree about which placements exist.
    /// </remarks>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <param name="at">The requested placement.</param>
    /// <param name="anchor">The anchor, required by <see cref="InsertAt.Before"/> / <see cref="InsertAt.After"/>.</param>
    internal static void RequirePlacement<TKey>(InsertAt at, TKey? anchor)
    {
        switch (at)
        {
            case InsertAt.End:
            case InsertAt.Index:
                return;

            case InsertAt.Before:
            case InsertAt.After:
                if (anchor is null)
                {
                    throw new ArgumentNullException(
                        nameof(anchor), $"InsertAt.{at} requires an anchor key.");
                }

                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(at), at, "Unknown insert position.");
        }
    }

    /// <summary>Computes the minimal op set from <paramref name="prior"/> to <paramref name="target"/>.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="prior">The current ordered entries.</param>
    /// <param name="target">The desired ordered entries.</param>
    /// <returns>Removes, then inserts and moves in target order, then updates.</returns>
    public static IReadOnlyList<DiffOp<TKey, TValue>> Diff<TKey, TValue>(
        IReadOnlyList<KeyValuePair<TKey, TValue>> prior,
        IReadOnlyList<KeyValuePair<TKey, TValue>> target)
        where TKey : notnull
    {
        Guard.NotNull(prior, nameof(prior));
        Guard.NotNull(target, nameof(target));

        var priorIndex = new Dictionary<TKey, int>();
        var priorValue = new Dictionary<TKey, TValue>();
        for (var i = 0; i < prior.Count; i++)
        {
            priorIndex[prior[i].Key] = i;
            priorValue[prior[i].Key] = prior[i].Value;
        }

        var targetValue = new Dictionary<TKey, TValue>();
        foreach (var e in target) targetValue[e.Key] = e.Value;

        var ops = new List<DiffOp<TKey, TValue>>();

        // Removes: keys in prior, absent from target.
        foreach (var e in prior)
        {
            if (!targetValue.ContainsKey(e.Key)) ops.Add(new DiffOpRemove<TKey, TValue>(e.Key));
        }

        // Common keys in TARGET order, carrying their prior indices for the LIS.
        var commonIdxOf = new Dictionary<TKey, int>();
        var priorIdxSeq = new List<int>();
        foreach (var e in target)
        {
            if (!priorIndex.TryGetValue(e.Key, out var pi)) continue;
            commonIdxOf[e.Key] = priorIdxSeq.Count;
            priorIdxSeq.Add(pi);
        }

        var stable = LongestIncreasingSubsequence(priorIdxSeq).ToHashSet();

        // Inserts and moves, walking the target left to right.
        for (var ti = 0; ti < target.Count; ti++)
        {
            var key = target[ti].Key;
            if (!priorIndex.ContainsKey(key))
            {
                ops.Add(new DiffOpInsert<TKey, TValue>(key, target[ti].Value, ti));
                continue;
            }

            if (!stable.Contains(commonIdxOf[key])) ops.Add(new DiffOpMove<TKey, TValue>(key, ti));
        }

        // Updates: common keys whose value changed.
        foreach (var e in target)
        {
            if (priorValue.TryGetValue(e.Key, out var pv) &&
                !EqualityComparer<TValue>.Default.Equals(pv, e.Value))
            {
                ops.Add(new DiffOpUpdate<TKey, TValue>(e.Key, e.Value));
            }
        }

        return ops;
    }

    /// <summary>
    /// The indices (into <paramref name="seq"/>) of a longest strictly-increasing subsequence, ascending.
    /// </summary>
    /// <remarks>Patience-sort LIS, O(n log n) — the same algorithm as the sibling bindings' reconcilers.</remarks>
    /// <param name="seq">The sequence to scan.</param>
    /// <returns>The chosen indices, in ascending order.</returns>
    internal static List<int> LongestIncreasingSubsequence(IReadOnlyList<int> seq)
    {
        var n = seq.Count;
        if (n == 0) return [];

        var tails = new List<int>();      // tails[k] = index of the smallest tail of an IS of length k+1
        var prev = new int[n];
        Array.Fill(prev, -1);

        for (var i = 0; i < n; i++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (seq[tails[mid]] < seq[i]) lo = mid + 1;
                else hi = mid;
            }

            if (lo > 0) prev[i] = tails[lo - 1];
            if (lo == tails.Count) tails.Add(i);
            else tails[lo] = i;
        }

        var chosen = new List<int>();
        for (var k = tails.Count == 0 ? -1 : tails[^1]; k >= 0; k = prev[k]) chosen.Add(k);
        chosen.Reverse();
        return chosen;
    }
}
