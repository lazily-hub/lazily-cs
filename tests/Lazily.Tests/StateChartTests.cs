using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Native coverage for <see cref="StateChart"/> behaviour the shared corpus does not reach.
/// </summary>
/// <remarks>
/// The corpus's guard fixture always supplies every guard it names, so the ABSENT-guard path — the
/// one that decides whether an unnamed guard fails open or closed — is never exercised by it.
/// That gap was found by mutating <c>GuardPasses</c> to fail open and watching the whole corpus stay
/// green. Conformance is necessary and documented as insufficient; this is one of the places it is
/// insufficient.
/// </remarks>
public sealed class StateChartTests
{
    /// <summary>A guarded transition whose guard is not supplied must be REJECTED, not taken.</summary>
    /// <remarks>
    /// Fail-open would make a guarded transition indistinguishable from an unguarded one for every
    /// caller that forgot to pass the guard map — the failure mode being that a door labelled
    /// "only open when allowed" opens because nobody said otherwise. Goes red if
    /// <c>GuardPasses</c> treats a missing or unknown guard name as true.
    /// </remarks>
    [Theory]
    [InlineData(null, false)]           // no guard map at all
    [InlineData("other", false)]        // a map that names a DIFFERENT guard
    [InlineData("allowed", true)]       // the guard, supplied and passing
    public void AnUnsuppliedGuardFailsClosed(string? suppliedGuard, bool expectAccepted)
    {
        var ctx = new Context();
        var chart = new StateChart(ctx, Door());

        var guards = suppliedGuard is null
            ? null
            : new Dictionary<string, bool>(StringComparer.Ordinal) { [suppliedGuard] = true };

        var accepted = chart.Send("OPEN", guards);

        Assert.Equal(expectAccepted, accepted);
        Assert.Equal(expectAccepted ? "open" : "closed", chart.ActiveLeaves().Single());
    }

    /// <summary>A guard supplied as false rejects, and rejection fires no actions at all.</summary>
    [Fact]
    public void ARejectedTransitionFiresNoActions()
    {
        var ctx = new Context();
        var chart = new StateChart(ctx, Door());

        var accepted = chart.Send("OPEN", new Dictionary<string, bool>(StringComparer.Ordinal) { ["allowed"] = false });

        Assert.False(accepted);
        Assert.Empty(chart.LastActions);
        Assert.Equal("closed", chart.ActiveLeaves().Single());
    }

    /// <summary>
    /// The innermost handler on a leaf's ancestor chain wins over an outer one for the same event.
    /// </summary>
    /// <remarks>
    /// This pins the OUTCOME, and deliberately not the mechanism. Dropping the innermost-first
    /// <c>break</c> in <c>Send</c> leaves this test green too, because conflict resolution
    /// independently sorts candidates by depth descending and skips the outer one on exit-set
    /// overlap. That is a real property of the algorithm rather than a hole in the test: the rule is
    /// enforced twice, and no behavioural test can distinguish the two. What this does catch is a
    /// selection order that picks the outer handler outright, which would land in a state the inner
    /// handler never names.
    /// </remarks>
    [Fact]
    public void TheInnermostHandlerWinsOverAnOuterOneForTheSameEvent()
    {
        var ctx = new Context();
        var chart = new StateChart(ctx, new ChartDef("root",
        [
            new("root", new StateDef { Initial = "outer" }),
            new("outer", new StateDef
            {
                Parent = "root",
                Initial = "inner",
                On = new Dictionary<string, Transition>(StringComparer.Ordinal)
                {
                    ["GO"] = new("wrong", null, [], false),
                },
            }),
            new("inner", new StateDef
            {
                Parent = "outer",
                On = new Dictionary<string, Transition>(StringComparer.Ordinal)
                {
                    ["GO"] = new("right", null, [], false),
                },
            }),
            new("right", new StateDef { Parent = "outer" }),
            new("wrong", new StateDef { Parent = "root" }),
        ]));

        Assert.Equal("inner", chart.ActiveLeaves().Single());
        Assert.True(chart.Send("GO"));
        Assert.Equal("right", chart.ActiveLeaves().Single());
    }

    private static ChartDef Door() => new("root",
    [
        new("root", new StateDef { Initial = "closed" }),
        new("closed", new StateDef
        {
            Parent = "root",
            On = new Dictionary<string, Transition>(StringComparer.Ordinal)
            {
                ["OPEN"] = new("open", "allowed", [], false),
            },
        }),
        new("open", new StateDef
        {
            Parent = "root",
            On = new Dictionary<string, Transition>(StringComparer.Ordinal)
            {
                ["CLOSE"] = new("closed", null, [], false),
            },
        }),
    ]);
}
