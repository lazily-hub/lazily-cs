using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The transport attachment of every egress flavor (<c>#lzegress</c>): exactly one Effect per
/// incarnation projects pending into the transport and the in-flight window, an acknowledgement
/// reopens the window reactively, and a reconnect makes the old attachment inert.
/// </summary>
public sealed class EgressCellTests
{
    private sealed class RecordingTransport : IEgressTransport<int>, IAsyncEgressTransport<int>
    {
        private readonly object _gate = new();
        private readonly List<long> _sent = [];
        internal bool FailNext;

        internal IReadOnlyList<long> Sent
        {
            get
            {
                lock (_gate) return [.. _sent];
            }
        }

        public bool Send(EgressEnvelope<int> envelope)
        {
            lock (_gate)
            {
                if (FailNext)
                {
                    FailNext = false;
                    return false;
                }

                _sent.Add(envelope.Sequence);
                return true;
            }
        }

        public async ValueTask<bool> SendAsync(EgressEnvelope<int> envelope)
        {
            await Task.Yield();
            return Send(envelope);
        }
    }

    [Fact]
    public void OneEffectProjectsPendingIntoTransportAndInflight()
    {
        var egress = new EgressCell<int>(new Context(), 1, EgressPolicy.Default);
        var transport = new RecordingTransport();
        var effect = egress.AttachTransport(transport);
        egress.Enqueue(10);
        egress.Enqueue(20);
        Assert.Equal([0L, 1L], transport.Sent);
        Assert.Empty(egress.Pending());
        Assert.Equal(2, egress.Inflight().Count);
        effect.Dispose();
    }

    [Fact]
    public void ReconnectMakesTheOldAttachmentInert()
    {
        var egress = new EgressCell<int>(new Context(), 3, EgressPolicy.Default);
        var old = new RecordingTransport();
        var oldEffect = egress.AttachTransport(old);
        egress.Enqueue(10);
        egress.Reconnect(4);
        egress.Enqueue(20);
        Assert.Equal([0L], old.Sent);

        var fresh = new RecordingTransport();
        var freshEffect = egress.AttachTransport(fresh);
        Assert.Equal([0L, 1L], fresh.Sent);
        Assert.All(egress.Inflight(), envelope => Assert.Equal(4, envelope.Generation));
        oldEffect.Dispose();
        freshEffect.Dispose();
    }

    [Fact]
    public void AckReopensTheWindowWithoutARequestAckHandshake()
    {
        var egress = new EgressCell<int>(new Context(), 1, EgressPolicy.Default with { InflightLimit = 1 });
        var transport = new RecordingTransport();
        var effect = egress.AttachTransport(transport);
        egress.Enqueue(10);
        egress.Enqueue(20);
        Assert.Equal([0L], transport.Sent);
        Assert.Single(egress.Pending());

        Assert.Equal(new EgressAckResult(EgressAckKind.Advanced, Through: 0), egress.Ack(1, 0));
        Assert.Equal([0L, 1L], transport.Sent);
        Assert.Empty(egress.Pending());
        effect.Dispose();
    }

    [Fact]
    public void ARefusedSendParksTheRecordForRetry()
    {
        var egress = new EgressCell<int>(new Context(), 1, EgressPolicy.Default);
        var transport = new RecordingTransport { FailNext = true };
        var effect = egress.AttachTransport(transport);
        egress.Enqueue(10);
        Assert.Empty(transport.Sent);
        Assert.Empty(egress.Inflight());
        Assert.Equal(new EgressRetry(0, 1, 10, false), egress.Retry());

        egress.RetryNow(1, 0);
        Assert.Equal([0L], transport.Sent);
        Assert.Equal(2, egress.Inflight().Single().Attempt);
        effect.Dispose();
    }

    [Fact]
    public void ThreadSafeAttachmentDrivesTheSharedProjection()
    {
        var ctx = new ThreadSafeContext();
        var egress = new ThreadSafeEgressCell<int>(ctx, 1, EgressPolicy.Default with { InflightLimit = 1 });
        var transport = new RecordingTransport();
        var effect = egress.AttachTransport(transport);
        egress.Enqueue(7);
        egress.Enqueue(8);
        Assert.Equal([0L], transport.Sent);
        Assert.Single(egress.Inflight());
        egress.Ack(1, 0);
        Assert.Equal([0L, 1L], transport.Sent);
        ctx.WithLock(_ => effect.Dispose());
    }

    [Fact]
    public void ThreadSafeConcurrentProducersNeverDuplicateOrLoseASequence()
    {
        var ctx = new ThreadSafeContext();
        var egress = new ThreadSafeEgressCell<int>(ctx, 1, EgressPolicy.Default with { InflightLimit = 1_000 });
        var transport = new RecordingTransport();
        var effect = egress.AttachTransport(transport);
        Parallel.For(0, 200, value => egress.Enqueue(value));
        Assert.Equal(Enumerable.Range(0, 200).Select(value => (long)value), transport.Sent.Order());
        Assert.Equal(200, egress.Inflight().Count);
        ctx.WithLock(_ => effect.Dispose());
    }

    [Fact]
    public async Task AsyncAttachmentDrivesTheAsyncProjection()
    {
        await using var ctx = new AsyncContext();
        var egress = new AsyncEgressCell<int>(ctx, 1, EgressPolicy.Default with { InflightLimit = 1 });
        var transport = new RecordingTransport();
        var effect = egress.AttachTransport(transport);
        egress.Enqueue(7);
        egress.Enqueue(8);
        Assert.True(await SettleUntil(ctx, () => transport.Sent.Count == 1));
        Assert.Equal([0L], transport.Sent);
        Assert.Single(await egress.InflightAsync());

        egress.Ack(1, 0);
        Assert.True(await SettleUntil(ctx, () => transport.Sent.Count == 2));
        Assert.Equal([0L, 1L], transport.Sent);
        Assert.Empty(await egress.PendingAsync());
        await effect.DisposeAsync();
    }

    [Fact]
    public async Task AsyncReconnectMakesTheOldAttachmentInert()
    {
        await using var ctx = new AsyncContext();
        var egress = new AsyncEgressCell<int>(ctx, 3, EgressPolicy.Default);
        var old = new RecordingTransport();
        var oldEffect = egress.AttachTransport(old);
        egress.Enqueue(10);
        Assert.True(await SettleUntil(ctx, () => old.Sent.Count == 1));
        egress.Reconnect(4);
        egress.Enqueue(20);
        Assert.True(ctx.Settle());
        Assert.Equal([0L], old.Sent);

        var fresh = new RecordingTransport();
        var freshEffect = egress.AttachTransport(fresh);
        Assert.True(await SettleUntil(ctx, () => fresh.Sent.Count == 2));
        Assert.Equal([0L, 1L], fresh.Sent);
        await oldEffect.DisposeAsync();
        await freshEffect.DisposeAsync();
    }

    [Fact]
    public void AnInvalidPolicyIsRefusedOnEveryFlavor()
    {
        var bad = EgressPolicy.Default with { InflightLimit = 0 };
        Assert.Equal(
            EgressConfigError.ZeroInflightLimit,
            Assert.Throws<EgressConfigException>(() => new EgressCell<int>(new Context(), 1, bad)).Error);
        Assert.Throws<EgressConfigException>(() => new ThreadSafeEgressCell<int>(new ThreadSafeContext(), 1, bad));
        Assert.Equal(
            EgressConfigError.RetryCeilingBelowBase,
            Assert.Throws<EgressConfigException>(() =>
                new EgressCore<int>(1, EgressPolicy.Default with { RetryCeiling = 1 })).Error);
        Assert.Equal(
            EgressConfigError.ZeroRetryBase,
            Assert.Throws<EgressConfigException>(() =>
                new EgressCore<int>(1, EgressPolicy.Default with { RetryBase = 0 })).Error);
    }

    // The effect body is SCHEDULED on the async plane, and its own claims re-schedule it, so the
    // condition is awaited on the graph reaching quiescence rather than on a wall-clock sleep.
    private static async Task<bool> SettleUntil(AsyncContext ctx, Func<bool> condition)
    {
        for (var round = 0; round < 1_000; round++)
        {
            if (ctx.Settle(TimeSpan.FromSeconds(10)) && condition()) return true;
            await Task.Yield();
        }

        return condition();
    }
}
