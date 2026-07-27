using System;
using System.Linq;
using Xunit;

namespace Lazily.Tests;

/// <summary>Normative paged-spill tests matching lazily-spec/docs/relaycell.md.</summary>
public sealed class SpillStoreTests
{
    [Theory]
    [InlineData(SpillMode.CompactOnWrite)]
    [InlineData(SpillMode.AppendCompact)]
    public void ReconstructionIsLosslessAcrossPageLayouts(SpillMode mode)
    {
        var store = new SpillStore<int>(mode, pageSize: 2, MergePolicy.Sum<int>());
        var windows = new[] { 1, 2, 3, 4, 5 };
        foreach (var window in windows) store.Spill(window, bytes: 1);

        var hot = 10;
        Assert.Equal(windows.Sum() + hot, store.Reconstruct(0, hot, hasHot: true));
        Assert.Equal(windows.Sum(), store.Reconstruct(0, hot: 0, hasHot: false));
    }

    [Fact]
    public void CompactOnWriteUsesBoundedImmutablePageSummaries()
    {
        var store = new SpillStore<int>(
            SpillMode.CompactOnWrite,
            pageSize: 2,
            MergePolicy.Sum<int>());
        store.Spill(1, 10);
        store.Spill(2, 20);
        store.Spill(3, 30);
        store.Spill(4, 40);
        store.Spill(5, 50);

        Assert.Equal(new ulong[] { 0, 1, 2 }, store.Manifest().Select(entry => entry.Id));
        Assert.Equal(new ulong[] { 30, 70, 50 }, store.Manifest().Select(entry => entry.Bytes));
        Assert.Equal(new[] { 3, 7, 5 }, store.PendingPages().Select(page => page.Summary));
    }

    [Fact]
    public void AppendCompactPreservesEveryWindowAsItsOwnPage()
    {
        var store = new SpillStore<int>(
            SpillMode.AppendCompact,
            pageSize: 8,
            MergePolicy.Sum<int>());
        store.Spill(2, 5);
        store.Spill(3, 7);

        Assert.Equal(2, store.PageCount);
        Assert.Equal(new[] { 2, 3 }, store.PendingPages().Select(page => page.Summary));
        Assert.Equal(new ulong[] { 5, 7 }, store.Manifest().Select(entry => entry.Bytes));
    }

    [Fact]
    public void AckCursorAdvancesBeforeReclaimAndReplayStartsAtCursor()
    {
        var store = new SpillStore<int>(
            SpillMode.AppendCompact,
            pageSize: 1,
            MergePolicy.Sum<int>());
        foreach (var window in new[] { 1, 2, 3 }) store.Spill(window, 1);

        store.AcknowledgeThrough(1);
        Assert.Equal(2, store.AcknowledgedCount);
        Assert.Equal(new[] { 3 }, store.PendingPages().Select(page => page.Summary));
        Assert.Equal(13, store.ReplayUnacknowledged(10));
        Assert.Equal(3, store.PageCount);

        store.Reclaim();
        Assert.Equal(0, store.AcknowledgedCount);
        Assert.Equal(1, store.PageCount);
        Assert.Equal(2UL, store.Manifest()[0].Id);
    }

    [Fact]
    public void CrashReplayConvergesForAnIdempotentPolicy()
    {
        var store = new SpillStore<int>(
            SpillMode.AppendCompact,
            pageSize: 1,
            MergePolicy.Max<int>());
        foreach (var window in new[] { 3, 7, 5 }) store.Spill(window, 1);

        var once = store.ReplayUnacknowledged(0);
        var twice = store.ReplayUnacknowledged(once);
        Assert.Equal(7, once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void ManifestAndPageSnapshotsCannotMutateStoreState()
    {
        var store = new SpillStore<int>(
            SpillMode.AppendCompact,
            pageSize: 1,
            MergePolicy.Sum<int>());
        store.Spill(4, 9);

        var manifest = store.Manifest();
        var pending = store.PendingPages();
        Assert.IsType<SpillManifestEntry[]>(manifest);
        Assert.IsType<SpillPage<int>[]>(pending);
        Assert.Single(store.Manifest());
        Assert.Single(store.PendingPages());
    }

    [Fact]
    public void ZeroPageSizeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SpillStore<int>(
                SpillMode.AppendCompact,
                pageSize: 0,
                MergePolicy.Sum<int>()));
    }
}
