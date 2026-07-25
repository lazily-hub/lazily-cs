using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The lock-backed context's own properties — the ones a single-threaded corpus replay cannot
/// reach.
/// </summary>
/// <remarks>
/// The reactive-graph corpus replayed through <see cref="ThreadSafeGraphModel"/> already pins the
/// refinement claim fixture-for-fixture. What it cannot do is RACE, so everything below either
/// runs real concurrent writers or exercises the pure batch-flush kernel that makes the
/// coalescing law checkable without a graph.
/// </remarks>
public sealed class ThreadSafeContextTests
{
    [Fact]
    public void ConcurrentWritersAreSerializedAndEveryWriteLands()
    {
        var ts = new ThreadSafeContext();
        Source<long> cell = null!;
        ts.WithLock(ctx => cell = ctx.Source(0L));

        const int threads = 8;
        const int perThread = 500;
        var ready = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            ready.SignalAndWait();
            for (var i = 0; i < perThread; i++) ts.WithLock(_ => cell.Set(cell.Peek() + 1));
        })).ToList();

        foreach (var t in workers) t.Start();
        foreach (var t in workers) t.Join();

        // Read-modify-write under the lock is atomic, so no increment is lost. Without the lock
        // this is the classic lost-update race and the total comes in low.
        Assert.Equal(threads * perThread, ts.WithLock(_ => cell.Peek()));
    }

    [Fact]
    public void ASingleWriteSectionIsObservationallyAPlainSet()
    {
        // The refinement claim in its smallest form: a one-write critical section must be exactly
        // Source.Set — same value, same single cascade — not a degenerate batch that defers.
        var ts = new ThreadSafeContext();
        var runs = 0;
        Source<long> cell = null!;
        ts.WithLock(ctx =>
        {
            cell = ctx.Source(0L);
            _ = new Effect(ctx, c =>
            {
                runs++;
                cell.Get(c);
                return null;
            });
        });

        Assert.Equal(1, runs);
        ts.Set(cell, 1);
        Assert.Equal(2, runs);
        ts.Set(cell, 1); // equal write: not a write
        Assert.Equal(2, runs);
    }

    [Fact]
    public void ABatchCoalescesConcurrentWritesIntoOneCascade()
    {
        var ts = new ThreadSafeContext();
        var runs = 0;
        Source<long> a = null!, b = null!, c = null!;
        ts.WithLock(ctx =>
        {
            a = ctx.Source(0L);
            b = ctx.Source(0L);
            c = ctx.Source(0L);
            _ = new Effect(ctx, cv =>
            {
                runs++;
                a.Get(cv);
                b.Get(cv);
                c.Get(cv);
                return null;
            });
        });
        Assert.Equal(1, runs);

        ts.Batch(() =>
        {
            ts.Set(a, 1);
            ts.Set(b, 2);
            ts.Set(c, 3);
        });

        // Three writes, one cascade — the property a serialized concurrent writer depends on.
        Assert.Equal(2, runs);
    }

    [Fact]
    public void TheLockIsReentrant()
    {
        var ts = new ThreadSafeContext();
        Assert.False(ts.IsHeldByCurrentThread);
        ts.WithLock(_ =>
        {
            Assert.True(ts.IsHeldByCurrentThread);
            ts.Batch(() => ts.WithLock(_ => Assert.True(ts.IsHeldByCurrentThread)));
        });
        Assert.False(ts.IsHeldByCurrentThread);
    }

    // --- the pure kernel (the Lean ThreadSafe model's executable counterpart) ----------------

    [Fact]
    public void ApplyBatchGuardsEqualWrites()
    {
        var nodes = new Dictionary<object, NodeEntry>
        {
            ["a"] = new(1, NodeEntry.Clean),
            ["b"] = new(2, NodeEntry.Clean),
        };

        var (next, changed) = ThreadSafeKernel.ApplyBatch(nodes, [new BatchWrite("a", 1), new BatchWrite("b", 9)]);

        // "a" was written its own value, so it is not a change: it stays clean and contributes no
        // frontier root. Only "b" moved.
        Assert.Equal(["b"], changed);
        Assert.Equal(NodeEntry.Clean, next["a"].State);
        Assert.Equal(NodeEntry.Dirty, next["b"].State);
        Assert.Equal(9, next["b"].Value);
    }

    [Fact]
    public void ApplyBatchListsEachChangedSourceOnce()
    {
        var nodes = new Dictionary<object, NodeEntry> { ["a"] = new(0, NodeEntry.Clean) };
        var (_, changed) = ThreadSafeKernel.ApplyBatch(
            nodes, [new BatchWrite("a", 1), new BatchWrite("a", 2), new BatchWrite("a", 3)]);
        Assert.Equal(["a"], changed);
    }

    [Fact]
    public void FlushBatchCoalescesTheFrontier()
    {
        var nodes = new Dictionary<object, NodeEntry>
        {
            ["a"] = new(0, NodeEntry.Clean),
            ["b"] = new(0, NodeEntry.Clean),
            ["shared"] = new(0, NodeEntry.Clean),
            ["untouched"] = new(0, NodeEntry.Clean),
        };
        var dependents = new Dictionary<object, IReadOnlyList<object>>
        {
            ["a"] = new object[] { "shared" },
            ["b"] = new object[] { "shared" },
        };

        var next = ThreadSafeKernel.FlushBatch(
            nodes, dependents, [new BatchWrite("a", 1), new BatchWrite("b", 1)]);

        // "shared" is reachable from both written sources and is dirtied in ONE pass — the
        // coalescing property. A node outside the cone is untouched.
        Assert.Equal(NodeEntry.Dirty, next["shared"].State);
        Assert.Equal(NodeEntry.Clean, next["untouched"].State);
    }

    [Fact]
    public void FlushBatchIsAFunctionOfTheSerializedWriteList()
    {
        // Why the lock is enough: whatever interleaving concurrent writers take, the lock
        // serializes them to SOME list, and the post-flush table depends only on that list. Two
        // runs of the same list therefore agree — the result is independent of the interleaving
        // the lock happened to pick.
        var nodes = new Dictionary<object, NodeEntry>
        {
            ["a"] = new(0, NodeEntry.Clean),
            ["b"] = new(0, NodeEntry.Clean),
            ["d"] = new(0, NodeEntry.Clean),
        };
        var dependents = new Dictionary<object, IReadOnlyList<object>>
        {
            ["a"] = new object[] { "d" },
            ["b"] = new object[] { "d" },
        };
        List<BatchWrite> writes = [new BatchWrite("a", 1), new BatchWrite("b", 2)];

        var first = ThreadSafeKernel.FlushBatch(nodes, dependents, writes);
        var second = ThreadSafeKernel.FlushBatch(nodes, dependents, writes);

        Assert.Equal(first.Keys.OrderBy(k => (string)k), second.Keys.OrderBy(k => (string)k));
        foreach (var k in first.Keys) Assert.Equal(first[k], second[k]);
    }
}
