// The merge algebra (spec tag: relaycell).
//
// A merge policy is an associative fold ⊕: T×T→T; the properties it satisfies
// (associativity always; commutativity = reordering tax; idempotency = durability tax)
// select which overflow behaviour is sound downstream. A Source generalizes a plain cell —
// cell ≡ Source(KeepLatest) — a source whose write is a merge.

using System.Numerics;

namespace Lazily;

/// <summary>
/// An associative merge ⊕ with its transport-selected property flags.
/// </summary>
/// <remarks>
/// Associativity (<c>(a⊕b)⊕c == a⊕(b⊕c)</c>) is a LAW, verified by the law tests, not a flag.
/// <see cref="Commutative"/> is the reordering tax; <see cref="Idempotent"/> the durability tax;
/// <see cref="Conflates"/> gates conflating overflow — only raw FIFO cannot bound.
/// </remarks>
/// <typeparam name="T">The merged value type.</typeparam>
/// <param name="Name">The policy's canonical name, as used on the wire.</param>
/// <param name="Merge">The associative fold.</param>
/// <param name="Commutative">Whether <c>a⊕b == b⊕a</c>.</param>
/// <param name="Idempotent">Whether <c>a⊕a == a</c>.</param>
/// <param name="Conflates">Whether the policy may drop intermediate operands under overflow.</param>
public sealed record MergePolicy<T>(
    string Name,
    Func<T, T, T> Merge,
    bool Commutative,
    bool Idempotent,
    bool Conflates);

/// <summary>The canonical merge policies of the algebra.</summary>
public static class MergePolicy
{
    /// <summary>
    /// The keep-latest band (<c>old ⊕ op = op</c>) — the policy behind a plain cell.
    /// Associative and idempotent, not commutative.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    public static MergePolicy<T> KeepLatest<T>() => new(
        "KeepLatest", static (_, op) => op, Commutative: false, Idempotent: true, Conflates: true);

    /// <summary>The additive commutative monoid (<c>old + op</c>). Not idempotent.</summary>
    /// <typeparam name="T">A number type.</typeparam>
    public static MergePolicy<T> Sum<T>() where T : INumber<T> => new(
        "Sum", static (a, b) => a + b, Commutative: true, Idempotent: false, Conflates: true);

    /// <summary>The max semilattice. Associative, commutative, idempotent.</summary>
    /// <typeparam name="T">A comparable number type.</typeparam>
    public static MergePolicy<T> Max<T>() where T : INumber<T> => new(
        "Max", static (a, b) => b > a ? b : a, Commutative: true, Idempotent: true, Conflates: true);

    /// <summary>The grow-only set-union semilattice.</summary>
    /// <typeparam name="TElement">The set element type.</typeparam>
    public static MergePolicy<IReadOnlySet<TElement>> SetUnion<TElement>() => new(
        "SetUnion",
        static (old, op) =>
        {
            var outSet = new HashSet<TElement>(old);
            outSet.UnionWith(op);
            return outSet;
        },
        Commutative: true,
        Idempotent: true,
        Conflates: true);

    /// <summary>
    /// Raw FIFO append (<c>old ++ op</c>). Order and multiplicity are meaning — associative only;
    /// cannot conflate.
    /// </summary>
    /// <typeparam name="TElement">The element type.</typeparam>
    public static MergePolicy<IReadOnlyList<TElement>> RawFifo<TElement>() => new(
        "RawFifo",
        static (old, op) =>
        {
            var outList = new List<TElement>(old.Count + op.Count);
            outList.AddRange(old);
            outList.AddRange(op);
            return outList;
        },
        Commutative: false,
        Idempotent: false,
        Conflates: false);
}
