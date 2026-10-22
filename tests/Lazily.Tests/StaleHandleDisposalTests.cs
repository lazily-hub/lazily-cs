using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Library-source mutation sentinels for generation- and kind-fenced disposal.
/// </summary>
/// <remarks>
/// The canonical fixture exercises real stale handles. These focused tests split the two handle
/// fields so deleting either comparison from <see cref="Context.TryRetireNode"/> independently
/// turns the suite red rather than remaining masked by the other mismatch.
/// </remarks>
public sealed class StaleHandleDisposalTests
{
    [Fact]
    public void Generation_is_checked_before_same_kind_recycled_occupant_is_mutated()
    {
        var ctx = new Context();
        var stale = ctx.Source(1);
        var staleHandle = stale.Handle;
        stale.Dispose();

        var successor = ctx.Source(20);
        Assert.True(Context.RecycledIdConfirmed(stale, successor));

        ctx.DisposeHandle(staleHandle);
        successor.Set(21);

        Assert.Equal(21, successor.Get());
        Assert.False(ctx.IsDisposed(successor));
    }

    [Fact]
    public void Kind_is_checked_before_cross_kind_recycled_occupant_is_mutated()
    {
        var ctx = new Context();
        var stale = new Effect(ctx, _ => null);
        var staleHandle = stale.Handle;
        stale.Dispose();

        var successor = ctx.Source(20);
        Assert.True(Context.RecycledIdConfirmed(stale, successor));

        // Isolate the kind guard: use the successor's live generation but the stale effect kind.
        var wrongKind = new ReactiveNodeHandle(
            staleHandle.Slot,
            successor.Handle.Generation,
            staleHandle.Kind);
        ctx.DisposeHandle(wrongKind);
        successor.Set(21);

        Assert.Equal(21, successor.Get());
        Assert.False(ctx.IsDisposed(successor));
    }

    [Fact]
    public void Stale_effect_disposal_is_idempotent_after_its_slot_is_recycled()
    {
        var ctx = new Context();
        var cleanups = 0;
        var stale = new Effect(ctx, _ => () => cleanups++);
        stale.Dispose();
        Assert.Equal(1, cleanups);

        var successor = ctx.Source(20);
        Assert.True(Context.RecycledIdConfirmed(stale, successor));
        stale.Dispose();
        successor.Set(21);

        Assert.Equal(1, cleanups);
        Assert.Equal(21, successor.Get());
    }

    [Fact]
    public void Async_stale_handle_cannot_dispose_same_kind_recycled_occupant()
    {
        var ctx = new AsyncContext();
        var stale = ctx.Source(1);
        stale.Dispose();

        var successor = ctx.Source(20);
        Assert.True(ctx.RecycledIdConfirmed(stale.GraphNode, successor.GraphNode));
        stale.Dispose();
        successor.Set(21);

        Assert.Equal(21, successor.Peek());
        Assert.False(ctx.IsDisposed(successor.GraphNode));
    }
}
