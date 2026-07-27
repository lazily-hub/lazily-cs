using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Lazily.Tests;

/// <summary>Normative RelayCell core tests matching lazily-spec/docs/relaycell.md.</summary>
public sealed class RelayCellTests
{
    [Fact]
    public void ConvergedEgressIsIndependentOfDrainSchedule()
    {
        var operations = new[] { 3, 1, 4, 1, 5, 9, 2, 6 };
        foreach (var merge in new[] { MergePolicy.Sum<int>(), MergePolicy.Max<int>() })
        {
            var expected = operations.Aggregate(merge.Merge);
            var drainEvery = CreateRelay(merge, highWater: 100, RelayOverflow.Conflate);
            int? accumulated = null;
            foreach (var operation in operations)
            {
                drainEvery.Ingress(operation);
                Assert.True(drainEvery.TryDrain(out var drained));
                accumulated = accumulated is null
                    ? drained
                    : merge.Merge(accumulated.Value, drained);
            }
            Assert.Equal(expected, accumulated);

            var drainOnce = CreateRelay(merge, highWater: 100, RelayOverflow.Conflate);
            foreach (var operation in operations) drainOnce.Ingress(operation);
            Assert.True(drainOnce.TryDrain(out var coalesced));
            Assert.Equal(expected, coalesced);
        }
    }

    [Fact]
    public void ReactiveReadersTrackIngressDrainAndPolicyRetuning()
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            highWater: 3,
            lowWater: 1,
            RelayOverflow.Conflate);
        var relay = new RelayCell<int>(ctx, policy, MergePolicy.Sum<int>());
        var depth = ctx.Computed(cx => relay.Depth(cx));
        var full = ctx.Computed(cx => relay.IsFull(cx));
        var empty = ctx.Computed(cx => relay.IsEmpty(cx));

        Assert.Equal(0UL, depth.Get());
        Assert.False(full.Get());
        Assert.True(empty.Get());
        relay.Ingress(1);
        relay.Ingress(2);
        Assert.Equal(2UL, depth.Get());
        Assert.False(full.Get());
        Assert.False(empty.Get());

        policy.HighWater.Set(2);
        Assert.False(full.Peek(out _));
        Assert.True(full.Get());
        Assert.True(relay.TryDrain(out var value));
        Assert.Equal(3, value);
        Assert.Equal(0UL, depth.Get());
        Assert.True(empty.Get());
    }

    [Fact]
    public void BlockAndLossyOverflowHaveExactOutcomesAndCounters()
    {
        var blocked = CreateRelay(MergePolicy.Sum<int>(), 2, RelayOverflow.Block);
        Assert.Equal(RelayIngressOutcome.Accepted, blocked.Ingress(1));
        Assert.Equal(RelayIngressOutcome.Conflated, blocked.Ingress(1));
        Assert.Equal(RelayIngressOutcome.Blocked, blocked.Ingress(9));
        Assert.Equal(1UL, blocked.Conflated());
        Assert.Equal(0UL, blocked.Dropped());
        Assert.True(blocked.TryDrain(out var blockValue));
        Assert.Equal(2, blockValue);

        var newest = CreateRelay(MergePolicy.Sum<int>(), 2, RelayOverflow.DropNewest);
        newest.Ingress(1);
        newest.Ingress(1);
        Assert.Equal(RelayIngressOutcome.Dropped, newest.Ingress(9));
        Assert.Equal(1UL, newest.Dropped());
        Assert.True(newest.TryDrain(out var newestValue));
        Assert.Equal(2, newestValue);

        var oldest = CreateRelay(MergePolicy.Sum<int>(), 2, RelayOverflow.DropOldest);
        oldest.Ingress(1);
        oldest.Ingress(1);
        Assert.Equal(RelayIngressOutcome.Dropped, oldest.Ingress(9));
        Assert.Equal(1UL, oldest.Depth());
        Assert.Equal(1UL, oldest.Dropped());
        Assert.True(oldest.TryDrain(out var oldestValue));
        Assert.Equal(9, oldestValue);
    }

    [Fact]
    public void RejectsUnconfiguredOrAlgebraicallyUnsoundPolicyPairs()
    {
        var ctx = new Context();
        var conflate = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            4,
            2,
            RelayOverflow.Conflate);
        Assert.Throws<ArgumentException>(
            () => new RelayCell<IReadOnlyList<int>>(
                ctx,
                conflate,
                MergePolicy.RawFifo<int>()));

        var bytes = new BackpressurePolicy(
            ctx,
            BoundDimension.Bytes,
            4,
            2,
            RelayOverflow.Block);
        Assert.Throws<NotSupportedException>(
            () => new RelayCell<int>(ctx, bytes, MergePolicy.Sum<int>()));

        var spill = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            4,
            2,
            RelayOverflow.Spill);
        Assert.Throws<NotSupportedException>(
            () => new RelayCell<int>(ctx, spill, MergePolicy.Max<int>()));
    }

    [Fact]
    public void MergePolicyCanChangeOnlyAtAnEmptyBoundary()
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            4,
            2,
            RelayOverflow.Conflate);
        var relay = new RelayCell<int>(ctx, policy, MergePolicy.Sum<int>());
        Assert.True(relay.CanReconfigure());
        relay.Ingress(2);
        Assert.False(relay.CanReconfigure());
        Assert.False(relay.TryReconfigure(MergePolicy.Max<int>()));
        Assert.True(relay.TryDrain(out _));
        Assert.True(relay.TryReconfigure(MergePolicy.Max<int>()));
        relay.Ingress(2);
        relay.Ingress(5);
        Assert.True(relay.TryDrain(out var value));
        Assert.Equal(5, value);
    }

    [Fact]
    public void InvalidLiveRetuningFailsClosed()
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            4,
            2,
            RelayOverflow.Conflate);
        var relay = new RelayCell<int>(ctx, policy, MergePolicy.Sum<int>());
        policy.LowWater.Set(4);
        Assert.Throws<InvalidOperationException>(() => relay.Ingress(1));
        policy.LowWater.Set(2);
        policy.Overflow.Set(RelayOverflow.Spill);
        Assert.False(relay.OverflowIsLegal());
        Assert.Throws<InvalidOperationException>(() => relay.Ingress(1));
    }

    private static RelayCell<int> CreateRelay(
        MergePolicy<int> merge,
        ulong highWater,
        RelayOverflow overflow)
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            highWater,
            highWater / 2,
            overflow);
        return new RelayCell<int>(ctx, policy, merge);
    }
}
