using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Native coverage for <see cref="CellTree{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// The shared corpus does not reach the tree — the <c>collections</c> fixtures exercise
/// <c>SourceMap</c> directly — so these tests carry the tree's own guarantee, and each names the
/// mutation it goes red under. Conformance here is necessary and documented as insufficient.
/// </remarks>
public sealed class CellTreeTests
{
    /// <summary>
    /// Reordering siblings keeps the moved child's identity, its value, and its whole subtree, and
    /// does not invalidate readers beneath it.
    /// </summary>
    /// <remarks>
    /// This is the entire reason the tree is not just nested maps. Asserted on a recompute counter
    /// inside a real reader's body rather than on the value, because a tree that re-minted the moved
    /// subtree returns exactly the same numbers while invalidating every reader under it.
    /// Goes red if <c>MoveTo</c> is implemented as remove + re-mint, or if a move bumps membership.
    /// </remarks>
    [Fact]
    public void MovingAChildKeepsItsIdentityAndLeavesItsSubtreeUntouched()
    {
        var ctx = new Context();
        var root = new CellTree<string, int>(ctx, "root", 0);
        var a = root.InsertChild("a", 1);
        var b = root.InsertChild("b", 2);
        var c = root.InsertChild("c", 3);
        var grandchild = b.InsertChild("b1", 20);

        var subtreeReads = 0;
        var subtree = ctx.Slot(cx =>
        {
            subtreeReads++;
            return grandchild.Get(cx);
        });

        var membershipReads = 0;
        var membership = ctx.Slot(cx =>
        {
            membershipReads++;
            return root.ChildCount(cx);
        });

        var orderReads = 0;
        var order = ctx.Slot(cx =>
        {
            orderReads++;
            return string.Join(",", root.ChildIds(cx));
        });

        Assert.Equal(20, subtree.Get());
        Assert.Equal(3, membership.Get());
        Assert.Equal("a,b,c", order.Get());

        var (subtreeBefore, membershipBefore, orderBefore) = (subtreeReads, membershipReads, orderReads);

        Assert.True(root.MoveChildTo("b", 2));

        Assert.Equal("a,c,b", order.Get());
        Assert.Equal(3, membership.Get());
        Assert.Equal(20, subtree.Get());

        // Order is the ONLY plane a move touches.
        Assert.True(orderReads > orderBefore, "a move must invalidate order readers");
        Assert.Equal(membershipBefore, membershipReads);
        Assert.Equal(subtreeBefore, subtreeReads);

        // The moved node is the same object, still carrying its subtree.
        Assert.Same(b, root.Child("b"));
        Assert.Same(grandchild, root.Child("b")!.Child("b1"));
        Assert.Same(a, root.Child("a"));
        Assert.Same(c, root.Child("c"));
    }

    /// <summary>Writing one node's value invalidates that node's readers and nobody else's.</summary>
    /// <remarks>Goes red if the tree shares one signal across nodes instead of one cell per node.</remarks>
    [Fact]
    public void WritingANodeDoesNotDisturbItsSiblingsOrItsParentsMembership()
    {
        var ctx = new Context();
        var root = new CellTree<string, int>(ctx, "root", 0);
        var a = root.InsertChild("a", 1);
        var b = root.InsertChild("b", 2);

        var aReads = 0;
        var readA = ctx.Slot(cx => { aReads++; return a.Get(cx); });
        var bReads = 0;
        var readB = ctx.Slot(cx => { bReads++; return b.Get(cx); });
        var membershipReads = 0;
        var membership = ctx.Slot(cx => { membershipReads++; return root.ChildCount(cx); });

        Assert.Equal(1, readA.Get());
        Assert.Equal(2, readB.Get());
        Assert.Equal(2, membership.Get());
        var (aBefore, bBefore, mBefore) = (aReads, bReads, membershipReads);

        a.Set(11);

        Assert.Equal(11, readA.Get());
        Assert.Equal(2, readB.Get());
        Assert.Equal(2, membership.Get());

        Assert.True(aReads > aBefore, "writing a node must invalidate its own readers");
        Assert.Equal(bBefore, bReads);
        Assert.Equal(mBefore, membershipReads);
    }

    /// <summary>Adding and removing children bumps membership; the surviving siblings' values do not.</summary>
    [Fact]
    public void ChildInsertAndRemoveBumpMembershipWithoutTouchingSurvivingValues()
    {
        var ctx = new Context();
        var root = new CellTree<string, int>(ctx, "root", 0);
        var a = root.InsertChild("a", 1);

        var aReads = 0;
        var readA = ctx.Slot(cx => { aReads++; return a.Get(cx); });
        var membershipReads = 0;
        var membership = ctx.Slot(cx => { membershipReads++; return root.ChildCount(cx); });

        Assert.Equal(1, readA.Get());
        Assert.Equal(1, membership.Get());
        var (aBefore, mBefore) = (aReads, membershipReads);

        root.InsertChild("z", 26);
        Assert.Equal(2, membership.Get());
        Assert.True(membershipReads > mBefore);
        Assert.Equal(1, readA.Get());
        Assert.Equal(aBefore, aReads);

        mBefore = membershipReads;
        Assert.True(root.RemoveChild("z"));
        Assert.Equal(1, membership.Get());
        Assert.True(membershipReads > mBefore);
        Assert.Equal(1, readA.Get());
        Assert.Equal(aBefore, aReads);
        Assert.False(root.HasChild("z"));
    }
}
