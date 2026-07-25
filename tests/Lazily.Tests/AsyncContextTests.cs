using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The async plane's own contract — the slot state machine, revision tracking, the five
/// cancellation properties, the re-resolve loop, and the effect trigger/ordering rules
/// (lazily-spec docs/async.md § Conformance).
/// </summary>
/// <remarks>
/// The reactive-graph corpus replayed through <see cref="AsyncGraphModel"/> already pins the
/// graph semantics this plane shares with the synchronous kernel. Everything below is what only
/// exists BECAUSE the plane is async: in-flight state, stale completion, waiter cancellation, and
/// the cleanup trigger. Concurrency windows are pinned by targeted deterministic gates rather
/// than by racing, because a race that passes proves nothing about the window it did not hit.
/// </remarks>
public sealed class AsyncContextTests
{
    private static void Settled(AsyncContext ctx) =>
        Assert.True(ctx.Settle(TimeSpan.FromSeconds(10)), "the async graph never reached quiescence");

    // --- slot state machine -------------------------------------------------

    [Fact]
    public async Task TheSlotWalksEmptyComputingResolvedAndBackToComputing()
    {
        await using var ctx = new AsyncContext();
        var a = ctx.Source(1L);
        var slot = ctx.Computed(cc => Task.FromResult(cc.Track(a)));

        Assert.Equal(AsyncSlotState.Empty, slot.State);
        Assert.False(slot.TryGet(out _));

        Assert.Equal(1L, await slot.GetAsync());
        Assert.Equal(AsyncSlotState.Resolved, slot.State);
        Assert.True(slot.TryGet(out var cached));
        Assert.Equal(1L, cached);

        var revision = slot.Revision;
        a.Set(2);

        // Invalidation advances the revision and leaves the slot computing, and the synchronous
        // fast path must NOT keep serving the stale cache.
        Assert.True(slot.Revision > revision);
        Assert.Equal(AsyncSlotState.Computing, slot.State);
        Assert.False(slot.TryGet(out _));

        Assert.Equal(2L, await slot.GetAsync());
    }

    [Fact]
    public async Task AFailedComputeEntersErrorAndTheNextReadRetries()
    {
        await using var ctx = new AsyncContext();
        var fail = true;
        var attempts = 0;
        var slot = ctx.Computed<long>(_ =>
        {
            attempts++;
            return fail
                ? Task.FromException<long>(new InvalidOperationException("boom"))
                : Task.FromResult(7L);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => slot.GetAsync());
        Assert.Equal(AsyncSlotState.Error, slot.State);

        // Error -> Computing on the next read: a transient failure must not be permanent.
        fail = false;
        Assert.Equal(7L, await slot.GetAsync());
        Assert.Equal(2, attempts);
        Assert.Equal(AsyncSlotState.Resolved, slot.State);
    }

    // --- revision tracking / stale completion -------------------------------

    [Fact]
    public async Task AStaleCompletionIsDiscardedNotPublished()
    {
        await using var ctx = new AsyncContext();
        var a = ctx.Source(1L);
        var hold = new TaskCompletionSource();
        var starts = 0;
        var slot = ctx.Computed(async cc =>
        {
            var v = cc.Track(a);
            // Hold only the FIRST run open, so the write below lands while it is suspended.
            if (Interlocked.Increment(ref starts) == 1) await hold.Task;
            return v;
        });

        var pull = slot.GetAsync();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref starts) == 1, TimeSpan.FromSeconds(10)));

        a.Set(2);            // supersedes the in-flight revision
        hold.SetResult();    // the stale run now completes, carrying the value 1

        // The stale run's token was replaced, so its completion is discarded. If the identity
        // gate were missing this would be 1 — a value the graph has already moved past.
        Assert.Equal(2L, await pull);
        Assert.Equal(2L, await slot.GetAsync());
    }

    // --- cancellation contract ----------------------------------------------

    [Fact]
    public async Task DroppingOneWaiterNeitherCancelsTheComputeNorDuplicatesIt()
    {
        await using var ctx = new AsyncContext();
        var release = new TaskCompletionSource();
        var runs = 0;
        var slot = ctx.Computed<long>(async _ =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
            return 42L;
        });

        using var cts = new CancellationTokenSource();
        var doomed = slot.GetAsync(cts.Token);
        var survivor = slot.GetAsync();

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref runs) == 1, TimeSpan.FromSeconds(10)));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed);

        release.SetResult();

        // The shared computation kept running for the waiter that remained, and there was only
        // ever one of it: two concurrent readers, one in-flight compute.
        Assert.Equal(42L, await survivor);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task DisposingTheContextCancelsInFlightWorkAndUnblocksWaiters()
    {
        var ctx = new AsyncContext();
        var observedCancellation = new TaskCompletionSource<bool>();
        var started = new TaskCompletionSource();
        var slot = ctx.Computed<long>(async cc =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cc.Token);
            }
            catch (OperationCanceledException)
            {
                observedCancellation.TrySetResult(true);
                throw;
            }
            return 0L;
        });

        var pull = slot.GetAsync();
        await started.Task;
        await ctx.DisposeAsync();

        Assert.True(await observedCancellation.Task);
        await Assert.ThrowsAsync<AsyncContextDisposedException>(() => pull);
        await Assert.ThrowsAsync<AsyncContextDisposedException>(() => slot.GetAsync());
    }

    [Fact]
    public async Task DisposingTheContextAwaitsEveryActiveEffectCleanup()
    {
        var ctx = new AsyncContext();
        var released = false;
        _ = ctx.Effect(_ => Task.FromResult<Func<Task>?>(async () =>
        {
            await Task.Delay(30);
            released = true;
        }));
        Settled(ctx);

        await ctx.DisposeAsync();

        // Disposal AWAITS the cleanup; returning before it completes would let a caller tear down
        // the resources the cleanup is about to touch.
        Assert.True(released);
    }

    // --- effects ------------------------------------------------------------

    [Fact]
    public async Task CleanupRunsOnRerunAndDisposeAndAtNoOtherTime()
    {
        await using var ctx = new AsyncContext();
        var log = new List<string>();
        var a = ctx.Source(0L);
        var effect = ctx.Effect(async cc =>
        {
            lock (log) log.Add("body");
            cc.Track(a);
            await Task.Yield();
            return () =>
            {
                lock (log) log.Add("cleanup");
                return Task.CompletedTask;
            };
        });

        Settled(ctx);
        // The flush that ran the body has ended. A binding that runs cleanup at flush end passes
        // every ORDERING assertion vacuously — there is no next body to be ordered against — and
        // still releases a resource the live effect is about to use again.
        lock (log) Assert.Equal(["body"], log);

        a.Set(1);
        Settled(ctx);
        lock (log) Assert.Equal(["body", "cleanup", "body"], log);

        await effect.DisposeAsync();
        lock (log) Assert.Equal(["body", "cleanup", "body", "cleanup"], log);
        Assert.False(effect.IsActive);
    }

    [Fact]
    public async Task RerunsAreSerializedPerEffect()
    {
        await using var ctx = new AsyncContext();
        var a = ctx.Source(0L);
        var concurrent = 0;
        var maxConcurrent = 0;
        var bodies = 0;
        _ = ctx.Effect(async cc =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, now);
            Interlocked.Increment(ref bodies);
            cc.Track(a);
            await Task.Delay(5);
            Interlocked.Decrement(ref concurrent);
            return null;
        });

        // Settle first: until the initial body has run there is no edge to `a`, so writes issued
        // before it would invalidate nothing and the test would prove only that.
        Settled(ctx);
        Assert.Equal(1, bodies);

        // Ten writes land while the rerun they triggered is mid-body, so every one after the
        // first can only be absorbed as a pending rerun.
        for (var i = 1; i <= 10; i++) a.Set(i);
        Settled(ctx);

        // A rerun never starts while the previous body is still running, so the bodies of one
        // effect never overlap however fast the writes arrive.
        Assert.Equal(1, maxConcurrent);
        Assert.True(bodies >= 2, $"the effect never reran ({bodies} bodies)");
    }

    [Fact]
    public async Task BatchIsSynchronousAtTheMutationBoundaryAndDefersTheReruns()
    {
        await using var ctx = new AsyncContext();
        var a = ctx.Source(0L);
        var b = ctx.Source(0L);
        var bodies = 0;
        _ = ctx.Effect(cc =>
        {
            Interlocked.Increment(ref bodies);
            cc.Track(a);
            cc.Track(b);
            return Task.FromResult<Func<Task>?>(null);
        });
        Settled(ctx);
        Assert.Equal(1, bodies);

        ctx.Batch(() =>
        {
            a.Set(1);
            b.Set(2);
            // Writes inside a batch queue their roots; nothing is scheduled, so nothing can have
            // run inside the callback.
            Assert.Equal(1, Volatile.Read(ref bodies));
        });

        Settled(ctx);
        Assert.Equal(2, bodies); // two writes, one coalesced rerun
    }

    [Fact]
    public async Task DependencyEdgesRegisterBeforeTheAwaitedRead()
    {
        await using var ctx = new AsyncContext();
        var a = ctx.Source(1L);
        var inner = ctx.Computed(cc => Task.FromResult(cc.Track(a) * 10));
        var outer = ctx.Computed(async cc => await cc.TrackAsync(inner) + 1);

        Assert.Equal(11L, await outer.GetAsync());

        // The edge exists because the read went through the compute view, not because anything
        // ambient was consulted — there is no ambient stack on this plane to consult.
        Assert.Equal(1, ctx.DependentCount(a.GraphNode));
        Assert.Equal(1, ctx.DependentCount(inner.GraphNode));
        Assert.Equal(1, ctx.DependencyCount(outer.GraphNode));

        a.Set(2);
        Assert.Equal(21L, await outer.GetAsync());
    }

    [Fact]
    public async Task AnUntrackedPeekFormsNoEdge()
    {
        await using var ctx = new AsyncContext();
        var a = ctx.Source(1L);
        var slot = ctx.Computed(_ => Task.FromResult(a.Peek()));

        Assert.Equal(1L, await slot.GetAsync());
        Assert.Equal(0, ctx.DependentCount(a.GraphNode));

        a.Set(2);
        // No edge, so no invalidation: the slot keeps serving its cache. This is the explicit
        // escape from tracking, and it must be explicit.
        Assert.Equal(1L, await slot.GetAsync());
    }

    [Fact]
    public async Task RepeatedReadsFormOneEdge()
    {
        await using var ctx = new AsyncContext();
        var a = ctx.Source(1L);
        var slot = ctx.Computed(cc => Task.FromResult(cc.Track(a) + cc.Track(a) + cc.Track(a)));

        Assert.Equal(3L, await slot.GetAsync());
        Assert.Equal(1, ctx.DependentCount(a.GraphNode));
        Assert.Equal(1, ctx.DependencyCount(slot.GraphNode));
    }

    [Fact]
    public async Task TheMemoGuardSuppressesTheDownstreamCascade()
    {
        await using var ctx = new AsyncContext();
        var a = ctx.Source(1L);
        var computes = 0;
        var guarded = ctx.Computed(cc =>
        {
            Interlocked.Increment(ref computes);
            return Task.FromResult(cc.Track(a) % 2);
        });

        Assert.Equal(1L, await guarded.GetAsync());
        a.Set(3); // a moved, but `a % 2` did not
        Assert.Equal(1L, await guarded.GetAsync());
        Assert.Equal(2, computes);
    }

    [Fact]
    public async Task DisposingASlotErrorsItsReadersAndDetachesBothDirections()
    {
        await using var ctx = new AsyncContext();
        var a = ctx.Source(1L);
        var slot = ctx.Computed(cc => Task.FromResult(cc.Track(a)));
        Assert.Equal(1L, await slot.GetAsync());
        Assert.Equal(1, ctx.DependentCount(a.GraphNode));

        slot.Dispose();

        Assert.True(ctx.IsDisposed(slot.GraphNode));
        Assert.Equal(0, ctx.DependentCount(a.GraphNode));
        Assert.Equal(0, ctx.DependencyCount(slot.GraphNode));
        await Assert.ThrowsAsync<DisposedNodeException>(() => slot.GetAsync());
    }

    [Fact]
    public async Task AScopeTearsDownItsMembersInReverseCreationOrder()
    {
        await using var ctx = new AsyncContext();
        var log = new List<string>();
        var scope = ctx.Scope();
        foreach (var id in (string[])["first", "second", "third"])
        {
            scope.Own(ctx.Effect(_ => Task.FromResult<Func<Task>?>(() =>
            {
                lock (log) log.Add(id);
                return Task.CompletedTask;
            })));
        }
        Settled(ctx);
        Assert.Equal(3, scope.Count);

        await scope.CloseAsync();
        lock (log) Assert.Equal(["third", "second", "first"], log);
    }

    [Fact]
    public async Task DisarmReleasesOwnershipWithoutDisposing()
    {
        await using var ctx = new AsyncContext();
        var scope = ctx.Scope();
        var effect = scope.Own(ctx.Effect(_ => Task.FromResult<Func<Task>?>(null)));
        Settled(ctx);

        scope.Disarm();
        Assert.Equal(0, scope.Count);
        await scope.CloseAsync();

        Assert.True(effect.IsActive);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value)
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
        }
    }
}
