using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class CrdtRegisterTests
{
    [Fact]
    public void Hlc_receive_is_causally_after_remote_and_local_clock()
    {
        var local = new Hlc(peer: 2);
        var remote = new Hlc(peer: 1);

        var localFirst = local.Send(10);
        var remoteFirst = remote.Send(12);
        var received = local.Receive(remoteFirst, nowMicros: 11);
        var localNext = local.Send(9);

        Assert.True(received.CompareTo(remoteFirst) > 0);
        Assert.True(received.CompareTo(localFirst) > 0);
        Assert.True(localNext.CompareTo(received) > 0);
        Assert.Equal(new HlcStamp(12, 2, 2), localNext);
    }

    [Fact]
    public void Lww_register_converges_by_total_stamp_order()
    {
        var lower = new LwwRegister<string>("lower", new HlcStamp(10, 0, 1));
        var higher = new LwwRegister<string>("higher", new HlcStamp(10, 0, 2));

        Assert.True(lower.MergeFrom(higher));
        Assert.False(higher.MergeFrom(lower));
        Assert.Equal("higher", lower.Value);
        Assert.Equal(higher.Stamp, lower.Stamp);

        Assert.False(lower.MergeFrom(higher));
        Assert.Equal("higher", lower.Value);
    }

    [Fact]
    public void Lww_merge_advances_newer_stamp_without_false_observable_change()
    {
        var current = new LwwRegister<string>("same", new HlcStamp(10, 0, 1));
        var newer = new LwwRegister<string>("same", new HlcStamp(11, 0, 2));

        Assert.False(current.MergeFrom(newer));
        Assert.Equal(newer.Stamp, current.Stamp);

        var between = new LwwRegister<string>("stale", new HlcStamp(10, 1, 9));
        Assert.False(current.MergeFrom(between));
        Assert.Equal("same", current.Value);
    }

    [Fact]
    public void Mv_register_retains_concurrency_then_causal_write_collapses_it()
    {
        var left = new MvRegister<string>();
        left.Set("left", peer: 1);
        var right = new MvRegister<string>();
        right.Set("right", peer: 2);

        Assert.True(left.MergeFrom(right));
        Assert.True(right.MergeFrom(left));
        Assert.Equal(["left", "right"], left.Values);
        Assert.Equal(left.Values, right.Values);

        Assert.True(left.Set("resolved", peer: 1));
        Assert.True(right.MergeFrom(left));
        Assert.Equal(["resolved"], left.Values);
        Assert.Equal(left.Values, right.Values);
        Assert.False(right.MergeFrom(left));
    }

    [Fact]
    public void Mv_register_merge_is_commutative_associative_and_idempotent()
    {
        var a = Register("a", peer: 1);
        var b = Register("b", peer: 2);
        var c = Register("c", peer: 3);

        var ab = a.Copy();
        ab.MergeFrom(b);
        var ba = b.Copy();
        ba.MergeFrom(a);
        Assert.Equal(ab.Values, ba.Values);

        var leftAssociated = ab.Copy();
        leftAssociated.MergeFrom(c);
        var rightAssociated = b.Copy();
        rightAssociated.MergeFrom(c);
        var aThenRight = a.Copy();
        aThenRight.MergeFrom(rightAssociated);
        Assert.Equal(leftAssociated.Values, aThenRight.Values);

        var before = leftAssociated.Values.ToArray();
        Assert.False(leftAssociated.MergeFrom(leftAssociated.Copy()));
        Assert.Equal(before, leftAssociated.Values);
    }

    [Fact]
    public void Pn_counter_merges_components_by_max_without_double_counting()
    {
        var left = new PnCounter();
        left.Increment(peer: 1, amount: 5);
        left.Decrement(peer: 1, amount: 2);

        var right = new PnCounter();
        right.Increment(peer: 2, amount: 4);
        right.Decrement(peer: 2);

        Assert.True(left.MergeFrom(right));
        Assert.Equal(6, left.Value);
        Assert.False(left.MergeFrom(right));
        Assert.Equal(6, left.Value);

        Assert.True(right.MergeFrom(left));
        Assert.Equal(left.Value, right.Value);
    }

    [Fact]
    public void Pn_counter_state_converges_even_when_net_value_does_not_change()
    {
        var local = new PnCounter();
        var balancedRemote = new PnCounter();
        balancedRemote.Increment(peer: 2, amount: 3);
        balancedRemote.Decrement(peer: 2, amount: 3);

        Assert.False(local.MergeFrom(balancedRemote));
        Assert.Equal(0, local.Value);

        balancedRemote.Increment(peer: 2);
        Assert.True(local.MergeFrom(balancedRemote));
        Assert.Equal(1, local.Value);
    }

    [Fact]
    public void Replicated_cell_invalidates_only_for_observable_changes()
    {
        var context = new Context();
        var localState = new PnCounter();
        var cell = new ReplicatedCell<PnCounter, long>(context, localState);
        var computes = 0;
        var doubled = context.Computed(
            compute =>
            {
                computes++;
                return cell.Handle.Get(compute) * 2;
            });

        Assert.Equal(0, doubled.Get());
        Assert.Equal(1, computes);

        var balanced = new PnCounter();
        balanced.Increment(peer: 2);
        balanced.Decrement(peer: 2);
        Assert.False(cell.MergeRemote(balanced));
        Assert.Equal(0, doubled.Get());
        Assert.Equal(1, computes);

        balanced.Increment(peer: 2, amount: 2);
        Assert.True(cell.MergeRemote(balanced));
        Assert.Equal(4, doubled.Get());
        Assert.Equal(2, computes);

        Assert.False(cell.MergeRemote(balanced));
        Assert.Equal(4, doubled.Get());
        Assert.Equal(2, computes);
    }

    [Fact]
    public void Register_mechanisms_match_their_coordination_contracts()
    {
        Assert.Equal(
            MergeMechanism.Lww,
            new LwwRegister<int>(0, new HlcStamp(0, 0, 0)).Mechanism);
        Assert.Equal(MergeMechanism.Crdt, new MvRegister<int>().Mechanism);
        Assert.Equal(MergeMechanism.Crdt, new PnCounter().Mechanism);
    }

    private static MvRegister<string> Register(string value, int peer)
    {
        var register = new MvRegister<string>();
        register.Set(value, peer);
        return register;
    }
}
