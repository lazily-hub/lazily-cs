using System;
using System.Linq;
using Xunit;

namespace Lazily.Tests;

/// <summary>End-to-end RelayCell Spill overflow tests.</summary>
public sealed class RelaySpillIntegrationTests
{
    [Fact]
    public void FullWindowsSpillBeforeTheNextOperationAndReconstructLosslessly()
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            highWater: 3,
            lowWater: 1,
            RelayOverflow.Spill);
        var store = new SpillStore<int>(
            SpillMode.AppendCompact,
            pageSize: 4,
            MergePolicy.Sum<int>());
        var relay = new RelayCell<int>(
            ctx,
            policy,
            MergePolicy.Sum<int>(),
            store,
            spillSize: static _ => 8,
            spillDeduplicatesReplay: true);
        var spilled = ctx.Computed(cx => relay.Spilled(cx));
        Assert.Equal(0UL, spilled.Get());

        foreach (var operation in new[] { 1, 2, 3, 4, 5, 6, 7 })
            relay.Ingress(operation);

        Assert.False(spilled.Peek(out _));
        Assert.Equal(2UL, spilled.Get());
        Assert.Equal(new[] { 6, 15 }, store.PendingPages().Select(page => page.Summary));
        Assert.Equal(new ulong[] { 8, 8 }, store.Manifest().Select(page => page.Bytes));
        Assert.Equal(1UL, relay.Depth());
        Assert.Equal(28, relay.Reconstruct(0));
        Assert.True(relay.TryDrain(out var hot));
        Assert.Equal(7, hot);
        Assert.Equal(21, store.Reconstruct(0, hot: 0, hasHot: false));
    }

    [Fact]
    public void IdempotentSpillDoesNotRequireReplayDeduplication()
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            highWater: 2,
            lowWater: 1,
            RelayOverflow.Spill);
        var store = new SpillStore<int>(
            SpillMode.AppendCompact,
            pageSize: 1,
            MergePolicy.Max<int>());
        var relay = new RelayCell<int>(
            ctx,
            policy,
            MergePolicy.Max<int>(),
            store);

        foreach (var operation in new[] { 3, 7, 5, 9, 2 })
            relay.Ingress(operation);
        Assert.Equal(9, relay.Reconstruct(0));
        var once = store.ReplayUnacknowledged(0);
        Assert.Equal(once, store.ReplayUnacknowledged(once));
    }

    [Fact]
    public void NonIdempotentSpillWithoutDeduplicationIsRejected()
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            highWater: 2,
            lowWater: 1,
            RelayOverflow.Spill);
        var store = new SpillStore<int>(
            SpillMode.AppendCompact,
            pageSize: 1,
            MergePolicy.Sum<int>());

        Assert.Throws<ArgumentException>(
            () => new RelayCell<int>(
                ctx,
                policy,
                MergePolicy.Sum<int>(),
                store));
    }

    [Fact]
    public void LiveSwitchToSpillFailsClosedWithoutAStore()
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            highWater: 2,
            lowWater: 1,
            RelayOverflow.Block);
        var relay = new RelayCell<int>(ctx, policy, MergePolicy.Max<int>());
        policy.Overflow.Set(RelayOverflow.Spill);

        Assert.False(relay.OverflowIsLegal());
        Assert.Throws<InvalidOperationException>(() => relay.Ingress(1));
    }
}
