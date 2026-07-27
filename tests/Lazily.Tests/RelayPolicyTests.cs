using Xunit;

namespace Lazily.Tests;

/// <summary>Normative RelayCell role and policy integration tests.</summary>
public sealed class RelayPolicyTests
{
    [Fact]
    public void OutboxConflatesStateAndCanBackpressureByEncodedBytes()
    {
        var ctx = new Context();
        var state = new Outbox<int>(
            ctx,
            highWater: 2,
            MergePolicy.KeepLatest<int>());
        foreach (var value in new[] { 1, 2, 3, 4, 5 })
            Assert.NotEqual(RelayIngressOutcome.Blocked, state.Send(value));
        Assert.True(state.TryDrain(out var latest));
        Assert.Equal(5, latest);

        var bytes = new Outbox<string>(
            ctx,
            highWater: 4,
            MergePolicy.KeepLatest<string>(),
            dimension: BoundDimension.Bytes,
            overflow: RelayOverflow.Block,
            meter: new RelayMeter<string>(byteSize: value => checked((ulong)value.Length)));
        Assert.Equal(RelayIngressOutcome.Accepted, bytes.Send("ab"));
        Assert.Equal(RelayIngressOutcome.Conflated, bytes.Send("four"));
        Assert.True(bytes.IsFull());
        Assert.Equal(RelayIngressOutcome.Blocked, bytes.Send("x"));
    }

    [Fact]
    public void InboxCreditsMeterTheRemoteAndTrackedReadiness()
    {
        var ctx = new Context();
        var inbox = new Inbox<int>(
            ctx,
            highWater: 100,
            maxCredits: 2,
            MergePolicy.Sum<int>());
        var ready = ctx.Computed(cx => inbox.Ready(cx));
        var credits = ctx.Computed(cx => inbox.Credits(cx));

        Assert.True(ready.Get());
        inbox.Receive(5);
        inbox.Receive(3);
        Assert.False(ready.Get());
        Assert.Equal(0UL, credits.Get());
        inbox.Receive(100);
        Assert.Equal(0UL, credits.Get());

        Assert.True(inbox.TryConsume(replenish: 20, out var window));
        Assert.Equal(108, window);
        Assert.True(ready.Get());
        Assert.Equal(2UL, credits.Get());
    }

    [Fact]
    public void OutboxTransportInboxLinkConverges()
    {
        var ctx = new Context();
        var outbox = new Outbox<int>(
            ctx,
            highWater: 64,
            MergePolicy.Sum<int>());
        var inbox = new Inbox<int>(
            ctx,
            highWater: 64,
            maxCredits: 64,
            MergePolicy.Sum<int>());
        IRelayTransport<int> transport = new FramedRelayTransport<int>(frameSize: 1);

        foreach (var operation in new[] { 1, 2, 3, 4 }) outbox.Send(operation);
        Assert.True(outbox.TryDrain(out var outbound));
        transport.Deliver(outbound);
        foreach (var frame in transport.Poll()) inbox.Receive(frame);

        Assert.True(inbox.TryConsume(64, out var received));
        Assert.Equal(10, received);
    }

    [Fact]
    public void RateAndWindowPoliciesPaceAndGroupEgress()
    {
        var rate = new RatePolicy(capacity: 3, refillPerTick: 2);
        Assert.True(rate.TryEgress());
        Assert.True(rate.TryEgress());
        Assert.True(rate.TryEgress());
        Assert.False(rate.TryEgress());
        rate.Tick();
        Assert.Equal(2UL, rate.Tokens);
        Assert.True(rate.TryEgress());
        rate.Tick();
        rate.Tick();
        Assert.Equal(3UL, rate.Tokens);

        var window = new WindowPolicy(windowOperations: 3);
        Assert.False(window.OnIngress());
        Assert.False(window.OnIngress());
        Assert.True(window.OnIngress());
        Assert.False(window.OnIngress());
        Assert.True(window.Tick());
        Assert.False(window.Tick());
    }

    [Fact]
    public void WindowFlushGroupingPreservesTheFlatFold()
    {
        var ctx = new Context();
        var relay = new RelayCell<int>(
            ctx,
            new BackpressurePolicy(
                ctx,
                BoundDimension.Count,
                highWater: 100,
                lowWater: 50,
                RelayOverflow.Conflate),
            MergePolicy.Sum<int>());
        var window = new WindowPolicy(windowOperations: 3);
        var converged = 0;

        foreach (var operation in new[] { 1, 2, 3, 4, 5, 6, 7 })
        {
            relay.Ingress(operation);
            if (window.OnIngress() && relay.TryDrain(out var group)) converged += group;
        }
        if (relay.TryDrain(out var tail)) converged += tail;

        Assert.Equal(28, converged);
    }

    [Fact]
    public void ExpiryDropsOnlyValuesPastTheInclusiveTtlBoundary()
    {
        var expiry = new ExpiryPolicy(timeToLive: 10);
        expiry.Advance(5);
        Assert.True(expiry.IsLive(stampedAt: 0));
        expiry.Advance(8);
        Assert.False(expiry.IsLive(stampedAt: 0));
        Assert.True(expiry.IsLive(stampedAt: 3));
        Assert.Equal(
            new[] { "edge", "hot" },
            expiry.RetainLive(
                new[]
                {
                    (Timestamp: 0UL, Value: "cold"),
                    (Timestamp: 3UL, Value: "edge"),
                    (Timestamp: 13UL, Value: "hot"),
                }));
    }

    [Fact]
    public void PriorityStorageUsesHighestPriorityAndFifoTies()
    {
        var priority = new PriorityStorage<string>();
        priority.Push(1, "low-a");
        priority.Push(3, "high-a");
        priority.Push(2, "mid");
        priority.Push(3, "high-b");
        Assert.Equal(4, priority.Count);

        Assert.True(priority.TryPop(out var highA));
        Assert.Equal("high-a", highA);
        Assert.True(priority.TryPop(out var highB));
        Assert.Equal("high-b", highB);
        Assert.True(priority.TryPop(out var mid));
        Assert.Equal("mid", mid);
        Assert.True(priority.TryPop(out var low));
        Assert.Equal("low-a", low);
        Assert.False(priority.TryPop(out _));
        Assert.True(priority.IsEmpty);
    }

    [Fact]
    public void KeyedRelayConvergesPerKeyAndRejectsUnsoundSharding()
    {
        var ctx = new Context();
        var keyed = new KeyedRelay<string, int>(
            ctx,
            highWater: 64,
            RelayOverflow.Conflate,
            MergePolicy.Sum<int>());
        foreach (var (key, value) in new[]
                 {
                     ("a", 1),
                     ("b", 10),
                     ("a", 2),
                     ("b", 20),
                     ("a", 3),
                 })
            keyed.Ingress(key, value);

        Assert.Equal(new[] { "a", "b" }, keyed.Keys);
        Assert.True(keyed.TryDrain("a", out var a));
        Assert.Equal(6, a);
        Assert.True(keyed.TryDrain("b", out var b));
        Assert.Equal(30, b);
        Assert.False(keyed.TryDrain("missing", out _));

        Assert.Throws<ArgumentException>(
            () => new KeyedRelay<string, int>(
                ctx,
                highWater: 64,
                RelayOverflow.Conflate,
                MergePolicy.KeepLatest<int>()));
        Assert.Throws<NotSupportedException>(
            () => new KeyedRelay<string, int>(
                ctx,
                highWater: 64,
                RelayOverflow.Spill,
                MergePolicy.Sum<int>()));
    }
}
