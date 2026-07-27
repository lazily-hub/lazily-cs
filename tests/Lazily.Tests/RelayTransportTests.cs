using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Lazily.Tests;

/// <summary>Normative relay transport tests matching lazily-spec/docs/relaycell.md.</summary>
public sealed class RelayTransportTests
{
    [Fact]
    public void InProcessTransportPollsTheWholeBufferedFrame()
    {
        var transport = new InProcRelayTransport<int>();
        Assert.False(transport.HasPending);
        Assert.Empty(transport.Poll());
        foreach (var operation in new[] { 1, 2, 3 }) transport.Deliver(operation);

        Assert.True(transport.HasPending);
        Assert.Equal(new[] { 1, 2, 3 }, transport.Poll());
        Assert.False(transport.HasPending);
        Assert.Empty(transport.Poll());
    }

    [Fact]
    public void FramedTransportPreservesOrderAcrossExactBoundaries()
    {
        var transport = new FramedRelayTransport<int>(frameSize: 2);
        foreach (var operation in new[] { 1, 2, 3, 4, 5 }) transport.Deliver(operation);

        Assert.Equal(new[] { 1, 2 }, transport.Poll());
        Assert.Equal(new[] { 3, 4 }, transport.Poll());
        Assert.True(transport.HasPending);
        Assert.Equal(new[] { 5 }, transport.Poll());
        Assert.False(transport.HasPending);
    }

    [Fact]
    public void NonPositiveFrameSizeClampsToOne()
    {
        var transport = new FramedRelayTransport<int>(0);
        Assert.Equal(1, transport.FrameSize);
        transport.Deliver(1);
        transport.Deliver(2);
        Assert.Equal(new[] { 1 }, transport.Poll());
        Assert.Equal(new[] { 2 }, transport.Poll());
    }

    [Fact]
    public void ConvergedEgressIsIndependentOfTransportFraming()
    {
        var operations = new[] { 3, 1, 4, 1, 5, 9, 2, 6 };
        foreach (var merge in new[] { MergePolicy.Sum<int>(), MergePolicy.Max<int>() })
        {
            var expected = operations.Aggregate(merge.Merge);
            foreach (var frameSize in new[] { 1, 2, 3, operations.Length + 1 })
            {
                IRelayTransport<int> transport = frameSize == operations.Length + 1
                    ? new InProcRelayTransport<int>()
                    : new FramedRelayTransport<int>(frameSize);
                foreach (var operation in operations) transport.Deliver(operation);

                int? egress = null;
                while (transport.HasPending)
                {
                    var relay = CreateRelay(merge);
                    foreach (var operation in transport.Poll())
                        relay.Ingress(operation);
                    Assert.True(relay.TryDrain(out var frameSummary));
                    egress = egress is null
                        ? frameSummary
                        : merge.Merge(egress.Value, frameSummary);
                }

                Assert.Equal(expected, egress);
            }
        }
    }

    [Fact]
    public void PollResultsAreDefensiveFrames()
    {
        var transport = new InProcRelayTransport<int>();
        transport.Deliver(1);
        var frame = Assert.IsType<int[]>(transport.Poll());
        frame[0] = 99;
        transport.Deliver(2);
        Assert.Equal(new[] { 2 }, transport.Poll());
    }

    private static RelayCell<int> CreateRelay(MergePolicy<int> merge)
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            highWater: 100,
            lowWater: 50,
            RelayOverflow.Conflate);
        return new RelayCell<int>(ctx, policy, merge);
    }
}
