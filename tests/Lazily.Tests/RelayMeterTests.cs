using System.Text;
using Xunit;

namespace Lazily.Tests;

/// <summary>Normative integration tests for every RelayCell backpressure dimension.</summary>
public sealed class RelayMeterTests
{
    [Fact]
    public void BytesMeasureTheRetainedEncodedHeadAndGateIngress()
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Bytes,
            highWater: 5,
            lowWater: 2,
            RelayOverflow.Block);
        var meter = new RelayMeter<string>(
            byteSize: value => checked((ulong)Encoding.UTF8.GetByteCount(value)));
        var relay = RelayCell<string>.WithMetering(
            ctx,
            policy,
            MergePolicy.KeepLatest<string>(),
            meter);
        var bytes = ctx.Computed(cx => relay.Bytes(cx));
        var measure = ctx.Computed(cx => relay.Measure(cx));
        var full = ctx.Computed(cx => relay.IsFull(cx));

        Assert.Equal(RelayIngressOutcome.Accepted, relay.Ingress("é"));
        Assert.Equal(2UL, bytes.Get());
        Assert.Equal(RelayIngressOutcome.Conflated, relay.Ingress("abcd"));
        Assert.Equal(4UL, measure.Get());
        Assert.False(full.Get());

        Assert.Equal(RelayIngressOutcome.Conflated, relay.Ingress("hello"));
        Assert.Equal(5UL, bytes.Get());
        Assert.True(full.Get());
        Assert.Equal(RelayIngressOutcome.Blocked, relay.Ingress("x"));
        Assert.Equal(5UL, relay.Bytes());

        Assert.True(relay.TryDrain(out var drained));
        Assert.Equal("hello", drained);
        Assert.Equal(0UL, bytes.Get());
        Assert.Equal(0UL, measure.Get());
        Assert.False(full.Get());
    }

    [Fact]
    public void KeysCountDistinctIngressKeysAndDropOldestResetsTheWindow()
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Keys,
            highWater: 2,
            lowWater: 1,
            RelayOverflow.DropOldest);
        var meter = new RelayMeter<KeyedValue>(
            keySelector: operation => operation.Key);
        var relay = RelayCell<KeyedValue>.WithMetering(
            ctx,
            policy,
            MergePolicy.KeepLatest<KeyedValue>(),
            meter);
        var pendingKeys = ctx.Computed(cx => relay.PendingKeys(cx));

        relay.Ingress(new KeyedValue("a", 1));
        relay.Ingress(new KeyedValue("a", 2));
        Assert.Equal(1UL, pendingKeys.Get());

        relay.Ingress(new KeyedValue("b", 3));
        Assert.Equal(2UL, pendingKeys.Get());
        Assert.True(relay.IsFull());

        Assert.Equal(
            RelayIngressOutcome.Dropped,
            relay.Ingress(new KeyedValue("c", 4)));
        Assert.Equal(1UL, pendingKeys.Get());
        Assert.Equal(1UL, relay.Depth());
        Assert.True(relay.TryDrain(out var drained));
        Assert.Equal(new KeyedValue("c", 4), drained);
        Assert.Equal(0UL, pendingKeys.Get());
    }

    [Fact]
    public void AgeIsReactiveAndResetsAtEveryNewHotWindow()
    {
        var ctx = new Context();
        var clock = ctx.Source(10UL);
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Age,
            highWater: 5,
            lowWater: 2,
            RelayOverflow.Block);
        var relay = RelayCell<int>.WithMetering(
            ctx,
            policy,
            MergePolicy.Sum<int>(),
            new RelayMeter<int>(logicalClock: clock));
        var age = ctx.Computed(cx => relay.Age(cx));
        var full = ctx.Computed(cx => relay.IsFull(cx));

        relay.Ingress(1);
        Assert.Equal(0UL, age.Get());
        clock.Set(14);
        Assert.False(age.Peek(out _));
        Assert.Equal(4UL, age.Get());
        Assert.False(full.Get());

        clock.Set(15);
        Assert.Equal(5UL, age.Get());
        Assert.True(full.Get());
        Assert.Equal(RelayIngressOutcome.Blocked, relay.Ingress(2));
        Assert.Equal(5UL, relay.Age());

        Assert.True(relay.TryDrain(out var drained));
        Assert.Equal(1, drained);
        Assert.Equal(0UL, age.Get());
        Assert.False(full.Get());

        clock.Set(100);
        relay.Ingress(3);
        Assert.Equal(0UL, age.Get());
        clock.Set(102);
        Assert.Equal(2UL, age.Get());
    }

    [Fact]
    public void ConfiguredMetersStayCurrentAcrossLiveDimensionRetuning()
    {
        var ctx = new Context();
        var clock = ctx.Source(0UL);
        var policy = new BackpressurePolicy(
            ctx,
            BoundDimension.Count,
            highWater: 20,
            lowWater: 10,
            RelayOverflow.Conflate);
        var meter = new RelayMeter<KeyedValue>(
            byteSize: value => checked((ulong)value.Value),
            keySelector: value => value.Key,
            logicalClock: clock);
        var relay = RelayCell<KeyedValue>.WithMetering(
            ctx,
            policy,
            MergePolicy.KeepLatest<KeyedValue>(),
            meter);
        var measure = ctx.Computed(cx => relay.Measure(cx));

        relay.Ingress(new KeyedValue("a", 3));
        relay.Ingress(new KeyedValue("b", 7));
        clock.Set(11);
        Assert.Equal(2UL, measure.Get());

        policy.Dimension.Set(BoundDimension.Bytes);
        Assert.Equal(7UL, measure.Get());
        policy.Dimension.Set(BoundDimension.Keys);
        Assert.Equal(2UL, measure.Get());
        policy.Dimension.Set(BoundDimension.Age);
        Assert.Equal(11UL, measure.Get());
    }

    [Fact]
    public void MissingOrForeignMeterConfigurationFailsClosed()
    {
        foreach (var dimension in new[]
                 {
                     BoundDimension.Bytes,
                     BoundDimension.Keys,
                     BoundDimension.Age,
                 })
        {
            var ctx = new Context();
            var policy = new BackpressurePolicy(
                ctx,
                dimension,
                highWater: 4,
                lowWater: 2,
                RelayOverflow.Block);
            Assert.Throws<NotSupportedException>(
                () => RelayCell<int>.WithMetering(
                    ctx,
                    policy,
                    MergePolicy.Sum<int>(),
                    new RelayMeter<int>()));
        }

        var relayContext = new Context();
        var clockContext = new Context();
        var agePolicy = new BackpressurePolicy(
            relayContext,
            BoundDimension.Age,
            highWater: 4,
            lowWater: 2,
            RelayOverflow.Block);
        Assert.Throws<ArgumentException>(
            () => RelayCell<int>.WithMetering(
                relayContext,
                agePolicy,
                MergePolicy.Sum<int>(),
                new RelayMeter<int>(logicalClock: clockContext.Source(0UL))));

        var livePolicy = new BackpressurePolicy(
            relayContext,
            BoundDimension.Count,
            highWater: 4,
            lowWater: 2,
            RelayOverflow.Conflate);
        var countOnly = new RelayCell<int>(
            relayContext,
            livePolicy,
            MergePolicy.Sum<int>());
        livePolicy.Dimension.Set(BoundDimension.Bytes);
        Assert.Throws<InvalidOperationException>(() => countOnly.Measure());
        Assert.Throws<InvalidOperationException>(() => countOnly.Ingress(1));
    }

    private sealed record KeyedValue(string Key, int Value);
}
