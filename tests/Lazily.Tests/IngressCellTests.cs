using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The ingress family's shell-level invariants: the ones the JSON corpus cannot state because they
/// are about EFFECT RUNS rather than reader values.
/// </summary>
/// <remarks>
/// The corpus pins <c>invalidates</c> per reader kind, which catches a shell that clears the wrong
/// set. It cannot catch a shell that clears the RIGHT set in several frontier walks — every value
/// assertion still holds, and only an effect reading two reader kinds can tell the difference. That
/// is the partial fan-out a generation handoff must never expose ("new value, old authority"), so
/// there is one frontier-walk gate per flavor below.
/// </remarks>
public sealed class IngressCellTests
{
    private static IngressEnvelope<string, long> Env(
        string key,
        long generation,
        long sequence,
        long stampedAt,
        long payload) => new(key, generation, sequence, stampedAt, payload);

    private static IngressPolicy Defaults => new();

    // ---------------------------------------------------------------------
    // Frontier-walk gates — one per flavor
    // ---------------------------------------------------------------------

    /// <summary>A handoff lands value and authority in ONE frontier walk, single-threaded.</summary>
    [Fact]
    public void SingleThreadedHandoffNeverShowsANewValueWithStaleAuthority()
    {
        var ctx = new Context();
        var cell = new IngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        cell.Admit(Env("a", 1, 0, 0, 5));

        var value = cell.ValueHandle("a");
        var authority = cell.AuthorityHandle("a");
        var seen = new List<(long? Window, long? Generation)>();
        var effect = ctx.Effect(cx =>
        {
            var window = value.Get(cx);
            var claim = authority.Get(cx);
            seen.Add((window.HasValue ? window.Value : null, claim?.Generation));
            return null;
        });

        cell.Admit(Env("a", 2, 0, 0, 9));
        Assert.Equal([(5L, 1L), (9L, 2L)], seen);
        effect.Dispose();
    }

    /// <summary>A handoff lands value and authority in ONE frontier walk, thread-safe.</summary>
    /// <remarks>
    /// The probe for "invalidation runs outside the core lock and fans out through one batch". A
    /// shell that cleared each root separately runs this effect twice and shows the intermediate
    /// (new value, old authority) pair.
    /// </remarks>
    [Fact]
    public void ThreadSafeHandoffNeverShowsANewValueWithStaleAuthority()
    {
        var ctx = new ThreadSafeContext();
        var cell = new ThreadSafeIngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        cell.Admit(Env("a", 1, 0, 0, 5));

        var value = cell.ValueHandle("a");
        var authority = cell.AuthorityHandle("a");
        var seen = new List<(long? Window, long? Generation)>();
        var effect = ctx.WithLock(inner => inner.Effect(cx =>
        {
            var window = value.Get(cx);
            var claim = authority.Get(cx);
            seen.Add((window.HasValue ? window.Value : null, claim?.Generation));
            return null;
        }));

        cell.Admit(Env("a", 2, 0, 0, 9));
        Assert.Equal([(5L, 1L), (9L, 2L)], seen);
        ctx.WithLock(_ => effect.Dispose());
    }

    /// <summary>A handoff lands value and authority in ONE frontier walk, async.</summary>
    [Fact]
    public async Task AsyncHandoffNeverShowsANewValueWithStaleAuthorityAsync()
    {
        await using var ctx = new AsyncContext();
        var cell = new AsyncIngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        cell.Admit(Env("a", 1, 0, 0, 5));

        var value = cell.ValueHandle("a");
        var authority = cell.AuthorityHandle("a");
        var gate = new object();
        var seen = new List<(long? Window, long? Generation)>();
        var effect = ctx.Effect(async compute =>
        {
            var window = await compute.TrackAsync(value).ConfigureAwait(false);
            var claim = await compute.TrackAsync(authority).ConfigureAwait(false);
            lock (gate) seen.Add((window.HasValue ? window.Value : null, claim?.Generation));
            return (Func<Task>?)null;
        });
        Assert.True(ctx.Settle());

        cell.Admit(Env("a", 2, 0, 0, 9));
        Assert.True(ctx.Settle());
        lock (gate) Assert.Equal([(5L, 1L), (9L, 2L)], seen);
        await effect.DisposeAsync();
    }

    // ---------------------------------------------------------------------
    // The negative cases — the ones that ARE the contract
    // ---------------------------------------------------------------------

    /// <summary>A buffered out-of-order envelope reruns no value effect and mints no receipt.</summary>
    [Fact]
    public void ABufferedEnvelopeRerunsNoEffect()
    {
        var ctx = new Context();
        var cell = new IngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        cell.Open("a", 1);

        var value = cell.ValueHandle("a");
        var runs = 0;
        var observed = new List<long?>();
        var effect = ctx.Effect(cx =>
        {
            runs++;
            var window = value.Get(cx);
            observed.Add(window.HasValue ? window.Value : null);
            return null;
        });
        Assert.Equal(1, runs);

        cell.Admit(Env("a", 1, 2, 0, 4));
        cell.Admit(Env("a", 1, 1, 0, 2));
        Assert.Equal(1, runs);
        Assert.Empty(cell.Accepted());

        // The delivery that closes the gap flushes all three as ONE value change.
        cell.Admit(Env("a", 1, 0, 0, 1));
        Assert.Equal(2, runs);
        Assert.Equal([null, 7L], observed);
        Assert.Single(cell.Accepted());
        effect.Dispose();
    }

    /// <summary>A tick INSIDE the freshness horizon reruns no readiness effect.</summary>
    [Fact]
    public void ATickInsideTheHorizonRerunsNoReadinessEffect()
    {
        var ctx = new Context();
        var cell = new IngressCell<string, long>(
            ctx,
            Defaults with { FreshnessHorizon = 100 },
            MergePolicy.Sum<long>(),
            IngressTransportKind.EventChannel,
            25);
        cell.Admit(Env("a", 1, 0, 0, 1));

        var readiness = cell.ReadinessHandle("a");
        var runs = 0;
        var effect = ctx.Effect(cx =>
        {
            runs++;
            _ = readiness.Get(cx);
            return null;
        });
        Assert.Equal(1, runs);

        cell.Tick(50);
        Assert.Equal(1, runs);
        cell.Tick(500);
        Assert.Equal(2, runs);
        Assert.Equal(IngressReadiness.Stale, cell.Readiness("a"));
        effect.Dispose();
    }

    /// <summary>An error moves retry without touching the value reader.</summary>
    [Fact]
    public void AnErrorMovesRetryWithoutTouchingTheValue()
    {
        var ctx = new Context();
        var cell = new IngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        cell.Admit(Env("a", 1, 0, 0, 9));

        var value = cell.ValueHandle("a");
        var runs = 0;
        var effect = ctx.Effect(cx =>
        {
            runs++;
            _ = value.Get(cx);
            return null;
        });

        cell.Fail("a", IngressError.TransportClosed);
        Assert.Equal(1, runs);
        Assert.Equal(1, cell.Retry("a")!.Value.Attempt);
        Assert.Equal(9L, cell.Value("a").Value);
        effect.Dispose();
    }

    /// <summary>An empty drain dirties nothing, and a drain never moves the watermark.</summary>
    [Fact]
    public void ADrainIsAnEgressNotAnAck()
    {
        var ctx = new Context();
        var cell = new IngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        cell.Admit(Env("a", 1, 0, 0, 3));

        Assert.Equal(3L, cell.Drain("a").Value);
        _ = cell.Value("a");
        Assert.True(cell.ValueHandle("a").Peek(out _));
        Assert.False(cell.Drain("a").HasValue);
        Assert.True(cell.ValueHandle("a").Peek(out _), "an empty drain invalidates nothing");
        Assert.Equal(0L, cell.View("a")!.Value.DeliveredThrough);
    }

    /// <summary>Closing one scope never invalidates another.</summary>
    [Fact]
    public void ScopesDoNotInvalidateEachOther()
    {
        var ctx = new Context();
        var cell = new IngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        cell.Admit(Env("a", 1, 0, 0, 1));

        var value = cell.ValueHandle("a");
        var runs = 0;
        var effect = ctx.Effect(cx =>
        {
            runs++;
            _ = value.Get(cx);
            return null;
        });
        Assert.Equal(1, runs);

        cell.Admit(Env("b", 1, 0, 0, 2));
        cell.Close("b");
        Assert.Equal(1, runs);
        Assert.Equal(1L, cell.Value("a").Value);
        effect.Dispose();
    }

    /// <summary>The three receipt channels are independent readers, not one log.</summary>
    [Fact]
    public void ReceiptChannelsAreIndependentReaders()
    {
        var ctx = new Context();
        var cell = new IngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        cell.Admit(Env("a", 2, 0, 0, 1));
        Assert.Single(cell.Accepted());
        Assert.Empty(cell.Dropped());
        Assert.Empty(cell.Errors());

        // A fenced zombie shows up only on the dropped channel, and only that channel's reader is
        // cleared: the accepted reader must survive with its cache intact.
        Assert.True(cell.AcceptedHandle.Peek(out _));
        cell.Admit(Env("a", 1, 0, 0, 1));
        Assert.True(cell.AcceptedHandle.Peek(out _), "a drop must not clear the accepted reader");
        Assert.False(cell.DroppedHandle.Peek(out _));
        var dropped = cell.Dropped();
        Assert.Single(dropped);
        Assert.Equal(IngressDropReason.StaleGeneration, dropped[0].Outcome.DropReason);

        cell.Fail("a", IngressError.DecodeFailed);
        Assert.Single(cell.Errors());
        Assert.Single(cell.Dropped());
        Assert.Single(cell.Accepted());
    }

    // ---------------------------------------------------------------------
    // The derives and the transport seam
    // ---------------------------------------------------------------------

    /// <summary>The schedule derives from the transport and retunes live.</summary>
    [Fact]
    public void TheScheduleDerivesFromTheTransportAndRetunesLive()
    {
        var ctx = new Context();
        var cell = new IngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        Assert.Null(cell.Schedule().PollInterval);

        cell.SetTransport(IngressTransportKind.BoundedPolling);
        Assert.Equal(25L, cell.Schedule().PollInterval);
        cell.SetPollInterval(200);
        Assert.Equal(200L, cell.Schedule().PollInterval);

        cell.SetTransport(IngressTransportKind.RpcTriggered);
        Assert.Null(cell.Schedule().PollInterval);

        // A zero interval would be an unbounded refresh loop.
        Assert.Equal(
            1L,
            IngressSchedule.ForKind(IngressTransportKind.BoundedPolling, 0).PollInterval);
    }

    /// <summary>Pump admits a batch and asks the transport to replay a surviving gap.</summary>
    [Fact]
    public void PumpAdmitsABatchAndRequestsReplayForASurvivingGap()
    {
        var ctx = new Context();
        var cell = new IngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        var transport = new InProcIngress<string, long>(IngressTransportKind.EventChannel);
        transport.Push(Env("a", 1, 0, 0, 1));
        transport.Push(Env("a", 1, 2, 0, 4));

        var outcomes = cell.Pump(transport);
        Assert.Equal(2, outcomes.Count);
        Assert.True(outcomes[0].IsDelivered);
        Assert.Equal(IngressAdmission.Buffered(1), outcomes[1]);
        Assert.Single(transport.Replays);
        Assert.Equal(new ReplayRequest(1, 1), transport.Replays[0].Value);

        transport.Push(Env("a", 1, 1, 0, 2));
        cell.Pump(transport);
        Assert.Equal(7L, cell.Value("a").Value);
        Assert.Single(transport.Replays);
    }

    /// <summary>A bounded-polling transport has no addressable history, so it cannot replay.</summary>
    [Fact]
    public void APollingTransportCannotServeAReplay()
    {
        var ctx = new Context();
        var cell = new IngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        var transport = new InProcIngress<string, long>(IngressTransportKind.BoundedPolling);
        transport.Push(Env("a", 1, 3, 0, 1));
        cell.Pump(transport);
        Assert.Empty(transport.Replays);
    }

    // ---------------------------------------------------------------------
    // The algebra's own invariants
    // ---------------------------------------------------------------------

    /// <summary>The fence outranks dedupe, so a zombie is distinguishable from a retry.</summary>
    [Fact]
    public void AStaleGenerationIsFencedBeforeItsSequenceIsConsulted()
    {
        var core = new IngressCore<string, long>(Defaults, MergePolicy.Sum<long>());
        core.Admit(Env("a", 2, 0, 0, 1));

        // Sequence 0 would be a duplicate; generation 1 is stale. The fence wins.
        var (_, admission) = core.Admit(Env("a", 1, 0, 0, 9));
        Assert.Equal(IngressAdmission.Dropped(IngressDropReason.StaleGeneration), admission);
        Assert.Equal(1L, core.Peek("a").Value);
    }

    /// <summary>A handoff is a baseline reset: buffered successors AND the window are discarded.</summary>
    [Fact]
    public void ANewerGenerationResetsTheBaseline()
    {
        var core = new IngressCore<string, long>(Defaults, MergePolicy.Sum<long>());
        core.Admit(Env("a", 1, 0, 0, 1));
        core.Admit(Env("a", 1, 7, 0, 1));
        var (_, admission) = core.Admit(Env("a", 2, 0, 0, 4));
        Assert.Equal(IngressAdmission.GenerationHandoff(1, 2), admission);

        var view = core.View("a")!.Value;
        Assert.Equal(2L, view.Generation);
        Assert.Equal(0L, view.DeliveredThrough);
        Assert.Equal(0, view.Buffered);
        Assert.Equal(4L, core.Peek("a").Value);
    }

    /// <summary>A handoff that BUFFERS still reports the baseline reset.</summary>
    /// <remarks>
    /// Reporting this as "buffered, nothing changed" would leave every reader showing the superseded
    /// generation's value forever.
    /// </remarks>
    [Fact]
    public void AHandoffThatBuffersStillReportsTheBaselineReset()
    {
        var core = new IngressCore<string, long>(Defaults, MergePolicy.Sum<long>());
        core.Admit(Env("a", 1, 0, 0, 5));
        var (change, admission) = core.Admit(Env("a", 2, 3, 0, 9));
        Assert.Equal(IngressAdmission.Buffered(0), admission);
        Assert.Equal(
            [new KeyValuePair<string, IngressScopeChange>("a", new IngressScopeChange(true, true, true, false))],
            change.Scopes);
        Assert.False(core.Peek("a").HasValue);

        // A buffered envelope under the SAME generation is still invisible.
        var (quiet, _) = core.Admit(Env("a", 2, 4, 0, 1));
        Assert.True(quiet.IsEmpty);
    }

    /// <summary>Freshness outranks ordering: an expired envelope never takes a reorder slot.</summary>
    [Fact]
    public void AnExpiredEnvelopeNeverOccupiesAReorderSlot()
    {
        var core = new IngressCore<string, long>(
            Defaults with { FreshnessHorizon = 10, ReorderWindow = 1 },
            MergePolicy.Sum<long>());
        core.Tick(100);

        var (_, admission) = core.Admit(Env("a", 1, 3, 50, 1));
        Assert.Equal(IngressAdmission.Dropped(IngressDropReason.Expired), admission);

        // A refused envelope leaves no scope behind, and the slot is still free.
        Assert.Null(core.View("a"));
        var (_, second) = core.Admit(Env("a", 1, 3, 95, 1));
        Assert.Equal(IngressAdmission.Buffered(0), second);
    }

    /// <summary><see cref="RelayOverflow.Block"/> refuses without advancing the watermark.</summary>
    [Fact]
    public void BlockOverflowRefusesLosslessly()
    {
        var core = new IngressCore<string, long>(
            Defaults with { HighWater = 1, Overflow = RelayOverflow.Block },
            MergePolicy.KeepLatest<long>());
        core.Admit(Env("a", 1, 0, 0, 5));

        var (change, admission) = core.Admit(Env("a", 1, 1, 0, 9));
        Assert.Equal(IngressAdmission.Blocked, admission);
        Assert.True(change.DroppedReceipts);
        Assert.Equal(5L, core.Peek("a").Value);
        Assert.Equal(0L, core.View("a")!.Value.DeliveredThrough);

        // The retry after a drain is in order rather than a duplicate.
        core.Drain("a");
        var (_, retry) = core.Admit(Env("a", 1, 1, 0, 9));
        Assert.Equal(IngressAdmission.Accepted(1), retry);
    }

    /// <summary>DropOldest restarts the window; DropNewest keeps it and receipts the drop.</summary>
    [Fact]
    public void TheLossyOverflowPoliciesDifferInWhatTheyLose()
    {
        var oldest = new IngressCore<string, long>(
            Defaults with { HighWater = 2, Overflow = RelayOverflow.DropOldest },
            MergePolicy.Sum<long>());
        oldest.Admit(Env("a", 1, 0, 0, 1));
        oldest.Admit(Env("a", 1, 1, 0, 2));
        Assert.Equal(IngressAdmission.Accepted(2), oldest.Admit(Env("a", 1, 2, 0, 30)).Admission);
        Assert.Equal(30L, oldest.Peek("a").Value);

        var newest = new IngressCore<string, long>(
            Defaults with { HighWater = 1, Overflow = RelayOverflow.DropNewest },
            MergePolicy.Sum<long>());
        newest.Admit(Env("a", 1, 0, 0, 5));
        var (change, admission) = newest.Admit(Env("a", 1, 1, 0, 9));
        Assert.Equal(IngressAdmission.Dropped(IngressDropReason.Backpressure), admission);
        Assert.True(change.DroppedReceipts);
        Assert.Equal(5L, newest.Peek("a").Value);
    }

    /// <summary>Construction validates the overflow choice against the merge algebra.</summary>
    [Fact]
    public void ConstructionRejectsAnUnboundedPolicyPair()
    {
        Assert.Throws<ArgumentException>(() => new IngressCore<string, IReadOnlyList<long>>(
            Defaults with { Overflow = RelayOverflow.Conflate },
            MergePolicy.RawFifo<long>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngressCore<string, long>(
            Defaults with { ReceiptCapacity = 0 },
            MergePolicy.Sum<long>()));
    }

    /// <summary>Receipts are bounded, and their offsets stay monotone across eviction.</summary>
    [Fact]
    public void ReceiptsAreBoundedAndOffsetsStayMonotone()
    {
        var core = new IngressCore<string, long>(
            Defaults with { ReceiptCapacity = 2 },
            MergePolicy.Sum<long>());
        for (var sequence = 0L; sequence < 4; sequence++) core.Admit(Env("a", 1, sequence, 0, 1));

        var accepted = core.Receipts(IngressReceiptChannel.Accepted);
        Assert.Equal(2, accepted.Count);
        Assert.Equal([2L, 3L], accepted.Select(receipt => receipt.Offset));
    }

    /// <summary>
    /// The reordering tax is paid by the BUFFER, not by the algebra: any arrival permutation of a
    /// contiguous run converges to the in-order fold.
    /// </summary>
    [Fact]
    public void OutOfOrderArrivalConvergesToTheInOrderFold()
    {
        long[][] permutations =
        [
            [0, 1, 2, 3],
            [3, 2, 1, 0],
            [1, 0, 3, 2],
            [2, 0, 1, 3],
            [0, 3, 1, 2],
        ];

        foreach (var order in permutations)
        {
            var core = new IngressCore<string, long>(Defaults, MergePolicy.Sum<long>());
            foreach (var sequence in order) core.Admit(Env("a", 1, sequence, 0, 1L << (int)sequence));
            Assert.Equal(15L, core.Peek("a").Value);
            Assert.Equal(3L, core.View("a")!.Value.DeliveredThrough);
        }
    }

    /// <summary>A suspend retains the window and the watermark; reconnect replays the gap.</summary>
    [Fact]
    public void SuspendRetainsTheWatermarkAndReconnectReplaysTheGap()
    {
        var ctx = new Context();
        var cell = new IngressCell<string, long>(
            ctx, Defaults, MergePolicy.Sum<long>(), IngressTransportKind.EventChannel, 25);
        cell.Admit(Env("a", 1, 0, 0, 1));
        cell.Admit(Env("a", 1, 1, 0, 1));

        Assert.Equal(new ReplayRequest(1, 2), cell.Suspend("a"));
        Assert.Equal(IngressReadiness.Suspended, cell.Readiness("a"));
        Assert.Equal(2L, cell.Value("a").Value);
        Assert.Null(cell.Suspend("a"));

        Assert.Equal(new ReplayRequest(1, 2), cell.Reconnect("a", 1));
        Assert.Equal(IngressReadiness.Ready, cell.Readiness("a"));
    }

    /// <summary>A reconnect at a higher generation discards the stale window.</summary>
    [Fact]
    public void ReconnectAtAHigherGenerationDiscardsTheStaleWindow()
    {
        var core = new IngressCore<string, long>(Defaults, MergePolicy.Sum<long>());
        core.Admit(Env("a", 1, 0, 0, 5));
        core.Suspend("a");
        var (change, replay) = core.Reconnect("a", 3);
        Assert.Equal(new ReplayRequest(3, 0), replay);
        Assert.Contains(change.Scopes, entry => entry.Value.Value && entry.Value.Authority);
        Assert.False(core.Peek("a").HasValue);
    }

    /// <summary>Backoff doubles per consecutive error, clamps at the ceiling, and a delivery clears it.</summary>
    [Fact]
    public void ErrorsDeepenBackoffAndADeliveryClearsIt()
    {
        var core = new IngressCore<string, long>(
            Defaults with { RetryBase = 10, RetryCeiling = 25 },
            MergePolicy.Sum<long>());
        core.Open("a", 1);
        Assert.Null(core.Retry("a"));

        core.Fail("a", IngressError.TransportClosed);
        Assert.Equal(new IngressRetry(1, 10, 0), core.Retry("a"));
        core.Fail("a", IngressError.TransportClosed);
        Assert.Equal(20L, core.Retry("a")!.Value.Backoff);
        core.Fail("a", IngressError.TransportClosed);
        Assert.Equal(25L, core.Retry("a")!.Value.Backoff);
        Assert.Equal(3, core.Receipts(IngressReceiptChannel.Error).Count);

        core.Admit(Env("a", 1, 0, 0, 1));
        Assert.Null(core.Retry("a"));
    }

    /// <summary>A closed scope admits nothing and claims no authority until reopened.</summary>
    [Fact]
    public void ClosedScopesAdmitNothingAndClaimNoAuthority()
    {
        var core = new IngressCore<string, long>(Defaults, MergePolicy.Sum<long>());
        core.Admit(Env("a", 1, 0, 0, 1));
        core.Close("a");
        Assert.Null(core.Authority("a"));
        Assert.Equal(
            IngressAdmission.Dropped(IngressDropReason.ScopeClosed),
            core.Admit(Env("a", 1, 1, 0, 1)).Admission);

        // Reopening a CLOSED scope restarts its sequence space.
        core.Open("a", 1);
        Assert.Equal(IngressAdmission.Accepted(0), core.Admit(Env("a", 1, 0, 0, 4)).Admission);
    }

    /// <summary>A zero reorder window drops every gap immediately.</summary>
    [Fact]
    public void AZeroReorderWindowDropsEveryGap()
    {
        var core = new IngressCore<string, long>(
            Defaults with { ReorderWindow = 0 },
            MergePolicy.Sum<long>());
        Assert.Equal(
            IngressAdmission.Dropped(IngressDropReason.ReorderWindowOverflow),
            core.Admit(Env("a", 1, 1, 0, 1)).Admission);
    }
}
