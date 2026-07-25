using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Hand-written coverage for behaviour the shared corpus does not pin, and for the C#-specific
/// half of the cell kernel (the runtime fortification guard, the comparer-based write guard).
/// </summary>
public sealed class ReactiveGraphTests
{
    [Fact]
    public void ComputedIsLazyAndCachedUntilADependencyMoves()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var computes = 0;
        var doubled = ctx.Computed<int>(c => { computes++; return n.Get(c) * 2; });

        Assert.Equal(0, computes); // lazy: construction computes nothing
        Assert.Equal(2, doubled.Get());
        Assert.Equal(2, doubled.Get());
        Assert.Equal(1, computes); // cached

        n.Set(5);
        Assert.Equal(10, doubled.Get());
        Assert.Equal(2, computes);
    }

    [Fact]
    public void AnEqualWriteIsNotAWrite()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var computes = 0;
        var derived = ctx.Computed<int>(c => { computes++; return n.Get(c); });
        Assert.Equal(1, derived.Get());

        n.Set(1);
        Assert.Equal(1, derived.Get());
        Assert.Equal(1, computes);
    }

    [Fact]
    public void TheGuardSuppressesTheDownstreamCascade()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        // Recomputes on every write, but its VALUE only moves when n crosses zero.
        var sign = ctx.Computed<int>(c => Math.Sign(n.Get(c)));
        var downstreamComputes = 0;
        var label = ctx.Computed<string>(c => { downstreamComputes++; return sign.Get(c) > 0 ? "pos" : "nonpos"; });

        Assert.Equal("pos", label.Get());
        Assert.Equal(1, downstreamComputes);

        n.Set(7); // sign recomputes to the same value
        Assert.Equal("pos", label.Get());
        Assert.Equal(1, downstreamComputes);

        n.Set(-3);
        Assert.Equal("nonpos", label.Get());
        Assert.Equal(2, downstreamComputes);
    }

    [Fact]
    public void AnUnguardedSlotAlwaysCascades()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var sign = ctx.Slot<int>(c => Math.Sign(n.Get(c)));
        var downstreamComputes = 0;
        var label = ctx.Slot<string>(c => { downstreamComputes++; return $"{sign.Get(c)}"; });

        Assert.Equal("1", label.Get());
        n.Set(7);
        Assert.Equal("1", label.Get());
        Assert.Equal(2, downstreamComputes);
    }

    [Fact]
    public void RippleWhenGatesPropagationNotComputation()
    {
        var ctx = new Context();
        var n = ctx.Source(0);
        var computes = 0;
        // Propagate only when the value crosses a multiple of 10.
        var coarse = ctx.ComputedRippleWhen<int>(
            c => { computes++; return n.Get(c); },
            (old, next) => old / 10 != next / 10);
        var downstream = 0;
        var view = ctx.Computed<int>(c => { downstream++; return coarse.Get(c); });

        Assert.Equal(0, view.Get());
        n.Set(3);
        Assert.Equal(0, view.Get());
        Assert.Equal(2, computes);   // the value is ALWAYS computed
        Assert.Equal(1, downstream); // the cascade is not

        n.Set(11);
        Assert.Equal(11, view.Get());
        Assert.Equal(2, downstream);
    }

    [Fact]
    public void BatchCoalescesTheCascadeButNotTheAlgebra()
    {
        var ctx = new Context();
        var n = ctx.Source(0);
        var runs = 0;
        _ = ctx.Effect(c => { runs++; n.Get(c); return null; });
        Assert.Equal(1, runs);

        ctx.Batch(() =>
        {
            n.Set(1);
            n.Set(2);
            n.Set(3);
        });
        Assert.Equal(2, runs);
        Assert.Equal(3, n.Peek());
    }

    [Fact]
    public void AnEagerComputedMaterializesWithoutARead()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var computes = 0;
        var eager = ctx.Computed<int>(c => { computes++; return n.Get(c) + 10; }).Eager();

        Assert.Equal(1, computes);
        Assert.True(eager.IsEager);

        n.Set(2);
        Assert.Equal(2, computes); // re-materialized by the puller, no read needed
        Assert.Equal(12, eager.Get());
        Assert.Equal(2, computes);

        eager.Lazy();
        Assert.False(eager.IsEager);
        n.Set(3);
        Assert.Equal(2, computes); // lazy again
        Assert.Equal(13, eager.Get());
        Assert.Equal(3, computes);
    }

    [Fact]
    public void EagerIsIdempotent()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var computes = 0;
        var eager = ctx.Computed<int>(c => { computes++; return n.Get(c); }).Eager().Eager();
        n.Set(2);
        Assert.Equal(2, computes); // one puller, not two
        Assert.Equal(1, ctx.DependentCount(eager));
    }

    [Fact]
    public void RepeatedReadsFormOneEdge()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var sum = ctx.Computed<int>(c => n.Get(c) + n.Get(c) + n.Get(c));
        Assert.Equal(3, sum.Get());
        Assert.Equal(1, ctx.DependentCount(n));
        Assert.Equal(1, ctx.DependencyCount(sum));
    }

    [Fact]
    public void AReadThroughTheContextFormsNoEdge()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var untracked = ctx.Computed<int>(c => n.Get(c.Untracked()));
        Assert.Equal(1, untracked.Get());
        Assert.Equal(0, ctx.DependentCount(n));
        Assert.Equal(0, ctx.DependencyCount(untracked));

        n.Set(9);
        Assert.Equal(1, untracked.Get()); // never invalidated, because it never subscribed
    }

    [Fact]
    public void AnEscapedComputeViewFailsTheFortificationGuard()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        Compute? escaped = null;
        var leaky = ctx.Computed<int>(c => { escaped = c; return n.Get(c); });
        Assert.Equal(1, leaky.Get());
        Assert.NotNull(escaped);
        Assert.Throws<StaleComputeException>(() => n.Get(escaped!));
    }

    [Fact]
    public void ReadingADisposedNodeThrowsAndTryGetReports()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var derived = ctx.Computed<int>(c => n.Get(c) + 1);
        Assert.Equal(2, derived.Get());

        derived.Dispose();
        Assert.Throws<DisposedNodeException>(() => derived.Get());
        Assert.False(derived.TryGet(out _, out var err));
        Assert.NotNull(err);
        Assert.Equal("computed", err!.Kind);

        derived.Dispose(); // idempotent
        Assert.Equal(0, ctx.DependentCount(n));
    }

    [Fact]
    public void EffectCleanupRunsBeforeEachRerunAndOnDispose()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var log = new List<string>();
        var effect = ctx.Effect(c =>
        {
            var v = n.Get(c);
            log.Add($"run{v}");
            return () => log.Add($"cleanup{v}");
        });

        n.Set(2);
        effect.Dispose();
        Assert.Equal(["run1", "cleanup1", "run2", "cleanup2"], log);
        Assert.False(effect.IsActive);

        effect.Dispose(); // idempotent
        Assert.Equal(4, log.Count);
    }

    [Fact]
    public void ScopeTeardownIsReverseCreationOrder()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var log = new List<string>();
        var scope = ctx.Scope();
        foreach (var name in (string[])["a", "b", "c"])
        {
            scope.Own(ctx.Effect(c => { n.Get(c); return () => log.Add(name); }));
        }
        Assert.Equal(3, scope.Count);
        scope.Close();
        Assert.Equal(["c", "b", "a"], log);
        Assert.Equal(0, ctx.DependentCount(n));
    }

    [Fact]
    public void DisarmReleasesOwnershipWithoutDisposing()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var scope = ctx.Scope();
        var kept = scope.Own(ctx.Computed<int>(c => n.Get(c) + 1));
        Assert.Equal(2, kept.Get());

        scope.Disarm();
        Assert.Equal(0, scope.Count);
        scope.Close();

        Assert.False(ctx.IsDisposed(kept));
        n.Set(4);
        Assert.Equal(5, kept.Get());
    }

    [Fact]
    public void ContextSizeAndClearTrackCachedValues()
    {
        var ctx = new Context();
        var n = ctx.Source(1);
        var a = ctx.Computed<int>(c => n.Get(c) + 1);
        var b = ctx.Computed<int>(c => a.Get(c) + 1);
        Assert.Equal(0, ctx.Size);
        Assert.Equal(3, b.Get());
        Assert.Equal(2, ctx.Size);

        ctx.Clear();
        Assert.Equal(0, ctx.Size);
        Assert.Equal(3, b.Get());
    }

    [Fact]
    public void ADivergentFeedbackLoopExhaustsTheDrainInsteadOfHanging()
    {
        var ctx = new Context { DrainBudget = 64 };
        var n = ctx.Source(0);
        var effect = ctx.Effect(c =>
        {
            var v = n.Get(c);
            n.Set(v == 0 ? 0 : unchecked(v + 1));
            return null;
        });
        Assert.Null(ctx.LastDrainExhaustion);

        n.Set(1);
        var exhaustion = ctx.LastDrainExhaustion;
        Assert.NotNull(exhaustion);
        Assert.Equal(64, exhaustion!.Budget);
        // The report must IDENTIFY the loop, not merely announce that a counter was hit: a
        // self-rescheduling effect concentrates runs in itself, which is what distinguishes
        // divergence from a long-but-terminating wide cascade.
        Assert.Same(effect, exhaustion.Offender);
        Assert.True(exhaustion.OffenderRuns > 1);
    }

    [Fact]
    public void ACustomComparerDrivesTheWriteGuard()
    {
        var ctx = new Context();
        var n = ctx.Source(new[] { 1, 2, 3 }, new SequenceComparer());
        var computes = 0;
        var len = ctx.Computed<int>(c => { computes++; return n.Get(c).Length; });
        Assert.Equal(3, len.Get());

        n.Set([1, 2, 3]); // structurally equal — not a write
        Assert.Equal(1, computes);

        n.Set([1, 2, 3, 4]);
        Assert.Equal(4, len.Get());
        Assert.Equal(2, computes);
    }

    private sealed class SequenceComparer : IEqualityComparer<int[]>
    {
        public bool Equals(int[]? x, int[]? y) => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(int[] obj)
        {
            var hash = new HashCode();
            foreach (var v in obj) hash.Add(v);
            return hash.ToHashCode();
        }
    }
}
