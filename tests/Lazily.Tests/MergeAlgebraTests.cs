using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Law tests for the merge algebra.
/// </summary>
/// <remarks>
/// Associativity is a LAW every policy must satisfy, not a flag — so it is verified here rather
/// than declared. The three flags are declarations about which overflow behaviour is sound
/// downstream, and each is checked against the fold it describes: a policy that claims
/// commutativity or idempotency and does not have it would let a transport reorder or replay
/// operands and silently land on the wrong value.
/// </remarks>
public sealed class MergeAlgebraTests
{
    private static readonly long[] Operands = [0, 1, -1, 2, 7, -13, 100, long.MaxValue / 4];

    public static TheoryData<string> NumericPolicyNames() => ["KeepLatest", "Sum", "Max"];

    private static MergePolicy<long> Numeric(string name) => name switch
    {
        "KeepLatest" => MergePolicy.KeepLatest<long>(),
        "Sum" => MergePolicy.Sum<long>(),
        "Max" => MergePolicy.Max<long>(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(NumericPolicyNames))]
    public void NumericPoliciesAreAssociative(string name)
    {
        var p = Numeric(name);
        foreach (var a in Operands)
        {
            foreach (var b in Operands)
            {
                foreach (var c in Operands)
                {
                    Assert.Equal(p.Merge(p.Merge(a, b), c), p.Merge(a, p.Merge(b, c)));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(NumericPolicyNames))]
    public void NumericPoliciesHonourTheirDeclaredFlags(string name)
    {
        var p = Numeric(name);
        foreach (var a in Operands)
        {
            if (p.Commutative)
            {
                foreach (var b in Operands) Assert.Equal(p.Merge(a, b), p.Merge(b, a));
            }
            if (p.Idempotent) Assert.Equal(a, p.Merge(a, a));
        }
    }

    [Fact]
    public void SumIsNotIdempotentAndKeepLatestIsNotCommutative()
    {
        // The flags are load-bearing claims, so their negatives are pinned too: a policy wrongly
        // declaring idempotency lets a durable transport replay an operand for free.
        Assert.False(MergePolicy.Sum<long>().Idempotent);
        Assert.NotEqual(2L, MergePolicy.Sum<long>().Merge(2, 2));

        Assert.False(MergePolicy.KeepLatest<long>().Commutative);
        var kl = MergePolicy.KeepLatest<long>();
        Assert.NotEqual(kl.Merge(1, 2), kl.Merge(2, 1));
    }

    [Fact]
    public void SetUnionIsAGrowOnlySemilattice()
    {
        var p = MergePolicy.SetUnion<int>();
        IReadOnlySet<int> a = new HashSet<int> { 1, 2 };
        IReadOnlySet<int> b = new HashSet<int> { 2, 3 };
        IReadOnlySet<int> c = new HashSet<int> { 4 };

        Assert.True(p.Merge(p.Merge(a, b), c).SetEquals(p.Merge(a, p.Merge(b, c))));
        Assert.True(p.Merge(a, b).SetEquals(p.Merge(b, a)));
        Assert.True(p.Merge(a, a).SetEquals(a));
        Assert.True(p.Commutative && p.Idempotent && p.Conflates);
    }

    [Fact]
    public void RawFifoIsAssociativeOnlyAndCannotConflate()
    {
        var p = MergePolicy.RawFifo<int>();
        IReadOnlyList<int> a = [1];
        IReadOnlyList<int> b = [2];
        IReadOnlyList<int> c = [3];

        Assert.Equal(p.Merge(p.Merge(a, b), c), p.Merge(a, p.Merge(b, c)));
        // Order and multiplicity are meaning, so it is neither commutative nor idempotent, and it
        // is the one policy that cannot bound a window by dropping operands.
        Assert.NotEqual(p.Merge(a, b), p.Merge(b, a));
        Assert.NotEqual(p.Merge(a, a), a);
        Assert.False(p.Commutative);
        Assert.False(p.Idempotent);
        Assert.False(p.Conflates);
    }

    [Fact]
    public void APlainSourceIsExactlyASourceUnderKeepLatest()
    {
        // `Cell ≡ Source<KeepLatest>`: one kind, the policy in a field.
        var ctx = new Context();
        var plain = ctx.Source(1);
        Assert.Equal("KeepLatest", plain.Policy.Name);
        plain.Merge(9);
        Assert.Equal(9, plain.Peek());
    }

    [Fact]
    public void AnIdempotentPolicysNoOpMergeFiresNoCascade()
    {
        // The write guard runs on the merged result, so an idempotent policy gets dedup for free.
        var ctx = new Context();
        var high = ctx.Source(10L, MergePolicy.Max<long>());
        var computes = 0;
        var view = ctx.Computed<long>(c => { computes++; return high.Get(c); });
        Assert.Equal(10, view.Get());

        high.Merge(4); // max(10, 4) == 10 — not a write
        Assert.Equal(10, view.Get());
        Assert.Equal(1, computes);

        high.Merge(42);
        Assert.Equal(42, view.Get());
        Assert.Equal(2, computes);
    }

    [Fact]
    public void ASumSourceAccumulatesAndOnlyTheSourceCanWrite()
    {
        var ctx = new Context();
        var acc = ctx.Source(0L, MergePolicy.Sum<long>());
        acc.Merge(1);
        acc.Merge(2);
        acc.Merge(3);
        Assert.Equal(6, acc.Peek());

        // The read surface is uniform across kinds; the WRITE surface is not. `Computed` exposes
        // no Set/Merge at all, so write protection is in the type rather than a runtime gate.
        var derived = ctx.Computed<long>(c => acc.Get(c));
        Assert.IsNotType<Source<long>>(derived);
    }
}
