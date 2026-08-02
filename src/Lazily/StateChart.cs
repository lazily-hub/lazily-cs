using System;
using System.Collections.Generic;
using System.Linq;

namespace Lazily;

/// <summary>What kind of node a state is.</summary>
public enum StateKind
{
    /// <summary>A leaf with no children.</summary>
    Atomic,

    /// <summary>Has children and one initial child.</summary>
    Compound,

    /// <summary>Has children that are all active simultaneously.</summary>
    Parallel,

    /// <summary>A terminal leaf.</summary>
    Final,

    /// <summary>Resumes the last DIRECT child of its region.</summary>
    HistoryShallow,

    /// <summary>Resumes the full nested configuration below its region.</summary>
    HistoryDeep,
}

/// <summary>One event-triggered transition.</summary>
/// <param name="Target">The target state id.</param>
/// <param name="Guard">A named guard that must pass, or null.</param>
/// <param name="Actions">Actions fired between exit and entry.</param>
/// <param name="Internal">Whether the transition stays inside its source (no exit/re-entry).</param>
public sealed record Transition(string Target, string? Guard, IReadOnlyList<string> Actions, bool Internal);

/// <summary>One state's definition.</summary>
public sealed class StateDef
{
    /// <summary>The parent state id, or null for the root.</summary>
    public string? Parent { get; init; }

    /// <summary>The initial child, for a compound state.</summary>
    public string? Initial { get; init; }

    /// <summary>Whether the children are parallel regions.</summary>
    public bool Parallel { get; init; }

    /// <summary>The history kind (<c>shallow</c> / <c>deep</c>), or null.</summary>
    public string? History { get; init; }

    /// <summary>A history pseudo-state's default target on first entry.</summary>
    public string? Default { get; init; }

    /// <summary>Whether this is a final state.</summary>
    public bool Final { get; init; }

    /// <summary>Actions fired on entry.</summary>
    public IReadOnlyList<string> Entry { get; init; } = [];

    /// <summary>Actions fired on exit.</summary>
    public IReadOnlyList<string> Exit { get; init; } = [];

    /// <summary>Event name to transition.</summary>
    public IReadOnlyDictionary<string, Transition> On { get; init; } =
        new Dictionary<string, Transition>(StringComparer.Ordinal);
}

/// <summary>A parsed statechart definition with the derived hierarchy indices.</summary>
public sealed class ChartDef
{
    private readonly Dictionary<string, int> _depth = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _order = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);

    /// <summary>Builds the derived indices from <paramref name="states"/>.</summary>
    /// <param name="root">The root state id.</param>
    /// <param name="states">Every state, keyed by id, in document order.</param>
    public ChartDef(string root, IReadOnlyList<KeyValuePair<string, StateDef>> states)
    {
        Guard.NotNull(states, nameof(states));
        Root = root;
        States = states.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        // Document order is load-bearing: it is the tiebreak for transition conflict
        // resolution and for the entry/exit action trace, so two charts that differ only in
        // declaration order must not silently produce the same trace.
        for (var i = 0; i < states.Count; i++)
        {
            _order[states[i].Key] = i;
            _children[states[i].Key] = [];
        }

        foreach (var kv in states)
        {
            if (kv.Value.Parent is { } parent && _children.TryGetValue(parent, out var siblings))
            {
                siblings.Add(kv.Key);
            }
        }

        ComputeDepth(Root, 0);
    }

    /// <summary>The root state id.</summary>
    public string Root { get; }

    /// <summary>Every state, keyed by id.</summary>
    public IReadOnlyDictionary<string, StateDef> States { get; }

    /// <summary>The children of <paramref name="id"/>, in document order.</summary>
    /// <param name="id">The state id.</param>
    /// <returns>The children.</returns>
    public IReadOnlyList<string> Children(string id) =>
        _children.TryGetValue(id, out var c) ? c : [];

    /// <summary>What kind of node <paramref name="id"/> is.</summary>
    /// <param name="id">The state id.</param>
    /// <returns>The kind.</returns>
    public StateKind Kind(string id)
    {
        // An id absent from `States` is a PSEUDO-ID, not a typo: `PathBelow` and the entry walk
        // both call `Kind` on region and ancestor ids the chart names only as a parent, and a node
        // with no definition has no children and no history, which is exactly Atomic. Deliberate
        // leniency, pinned by `KindOfAnUndeclaredIdIsAtomic`.
        if (!States.TryGetValue(id, out var sd)) return StateKind.Atomic;

        // `history` is a CLOSED two-value wire enum carried as a string by every binding's chart
        // fixture. It is NOT forward-compatible: a chart is authored data, and no producer
        // legitimately emits a third history kind, so an unrecognised spelling is a typo that
        // would otherwise demote a history pseudo-state to an ordinary compound state and
        // silently lose every resume. Fail closed, naming the value.
        if (sd.History is { } history)
        {
            return history switch
            {
                "shallow" => StateKind.HistoryShallow,
                "deep" => StateKind.HistoryDeep,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(id),
                    history,
                    $"State '{id}' declares an unknown history kind; expected 'shallow' or 'deep'."),
            };
        }

        if (sd.Final) return StateKind.Final;
        if (sd.Parallel) return StateKind.Parallel;
        return Children(id).Count > 0 ? StateKind.Compound : StateKind.Atomic;
    }

    /// <summary>Whether <paramref name="id"/> is a leaf (atomic or final).</summary>
    /// <param name="id">The state id.</param>
    /// <returns>Whether it is a leaf.</returns>
    public bool IsLeaf(string id) => Kind(id) is StateKind.Atomic or StateKind.Final;

    /// <summary>Whether <paramref name="id"/> is a history pseudo-state.</summary>
    /// <param name="id">The state id.</param>
    /// <returns>Whether it is a history node.</returns>
    public bool IsHistory(string id) => Kind(id) is StateKind.HistoryShallow or StateKind.HistoryDeep;

    /// <summary>The chain from <paramref name="id"/> up to the root, inclusive.</summary>
    /// <param name="id">The state id.</param>
    /// <returns>The chain, innermost first.</returns>
    public List<string> AncestorsInclusive(string id)
    {
        var chain = new List<string>();
        var cursor = id;
        while (true)
        {
            chain.Add(cursor);
            if (!States.TryGetValue(cursor, out var sd) || sd.Parent is null) break;
            cursor = sd.Parent;
            if (chain.Count > States.Count + 1) break; // cycle guard
        }

        return chain;
    }

    /// <summary>The least common ancestor of <paramref name="a"/> and <paramref name="b"/>.</summary>
    /// <param name="a">One state id.</param>
    /// <param name="b">The other.</param>
    /// <returns>The LCA, or the root.</returns>
    public string Lca(string a, string b)
    {
        var chain = AncestorsInclusive(a).ToHashSet(StringComparer.Ordinal);
        foreach (var anc in AncestorsInclusive(b))
        {
            if (chain.Contains(anc)) return anc;
        }

        return Root;
    }

    /// <summary>Whether <paramref name="desc"/> is a STRICT descendant of <paramref name="anc"/>.</summary>
    /// <param name="desc">The candidate descendant.</param>
    /// <param name="anc">The candidate ancestor.</param>
    /// <returns>Whether the relation holds.</returns>
    public bool IsProperDescendant(string desc, string anc) =>
        !string.Equals(desc, anc, StringComparison.Ordinal) &&
        AncestorsInclusive(desc).Skip(1).Contains(anc, StringComparer.Ordinal);

    /// <summary>The depth of <paramref name="id"/> below the root.</summary>
    /// <param name="id">The state id.</param>
    /// <returns>The depth.</returns>
    public int Depth(string id) => _depth.TryGetValue(id, out var d) ? d : 0;

    /// <summary>The document-order index of <paramref name="id"/>.</summary>
    /// <param name="id">The state id.</param>
    /// <returns>The order index.</returns>
    public int Order(string id) => _order.TryGetValue(id, out var o) ? o : int.MaxValue;

    /// <summary>The parent of <paramref name="id"/>, or the root.</summary>
    /// <param name="id">The state id.</param>
    /// <returns>The parent id.</returns>
    public string ParentOf(string id) =>
        States.TryGetValue(id, out var sd) && sd.Parent is { } p ? p : Root;

    private void ComputeDepth(string id, int depth)
    {
        _depth[id] = depth;
        foreach (var child in Children(id)) ComputeDepth(child, depth + 1);
    }
}

/// <summary>
/// A Harel statechart bound to a reactive context: hierarchy, guards, entry/exit actions, shallow
/// and deep history, and parallel regions.
/// </summary>
/// <remarks>
/// <para>
/// The active configuration is held behind a <see cref="Source{T}"/> keyed on the configuration
/// itself, so <see cref="Matches"/> read inside a computation subscribes the reader to REAL
/// transitions. A rejected event — no enabled transition, or every candidate's guard failed — leaves
/// the configuration untouched and therefore invalidates nobody. That distinction is the whole point
/// of putting a chart in a reactive graph: an unhandled event must not be indistinguishable from a
/// self-transition.
/// </para>
/// <para>
/// Transition selection is innermost-first per active leaf, then conflict-resolved by source depth
/// descending and document order — so a child's handler wins over its parent's, and two parallel
/// regions can both move only when their exit sets are disjoint.
/// </para>
/// </remarks>
public sealed class StateChart
{
    private readonly Dictionary<string, Recording> _history = new(StringComparer.Ordinal);
    private readonly Source<string> _config;
    private CompatSet<string> _configStates;

    /// <summary>Creates a chart that enters its initial configuration by descending from the root.</summary>
    /// <param name="ctx">The owning context.</param>
    /// <param name="def">The chart definition.</param>
    public StateChart(Context ctx, ChartDef def)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(def, nameof(def));
        Def = def;

        var enter = new CompatSet<string>(StringComparer.Ordinal);
        var actions = new List<string>();
        EnterSubtree(def.Root, enter, actions);
        _configStates = enter;
        _config = ctx.Source(ConfigKey(enter));
        LastActions = actions;
    }

    /// <summary>The parsed chart definition.</summary>
    public ChartDef Def { get; }

    /// <summary>
    /// The ordered actions fired by the initial entry or the most recent accepted
    /// <see cref="Send"/>: exit innermost-first, then the transition's own actions, then entry
    /// outermost-first.
    /// </summary>
    public IReadOnlyList<string> LastActions { get; private set; }

    /// <summary>The full active configuration — active leaves plus every active ancestor.</summary>
    /// <param name="ops">The enclosing computation, when read from inside one.</param>
    /// <returns>The active state ids.</returns>
    public IReadOnlySet<string> Configuration(IComputeOps? ops = null)
    {
        _ = ops is null ? _config.Get() : _config.Get(ops);
        return _configStates;
    }

    /// <summary>The active atomic leaves, sorted — one per parallel region.</summary>
    /// <param name="ops">The enclosing computation, when read from inside one.</param>
    /// <returns>The active leaves.</returns>
    public IReadOnlyList<string> ActiveLeaves(IComputeOps? ops = null) =>
        [.. Configuration(ops).Where(Def.IsLeaf).OrderBy(state => state, StringComparer.Ordinal)];

    /// <summary>The hierarchical "state-in" predicate.</summary>
    /// <param name="id">The state id to test.</param>
    /// <param name="ops">The enclosing computation, when read from inside one.</param>
    /// <returns>Whether <paramref name="id"/> is active.</returns>
    public bool Matches(string id, IComputeOps? ops = null) => Configuration(ops).Contains(id);

    /// <summary>Delivers an event, run-to-completion.</summary>
    /// <remarks>
    /// An unknown guard name fails CLOSED. A guard that silently passed because nobody supplied it
    /// would make a guarded transition indistinguishable from an unguarded one.
    /// </remarks>
    /// <param name="evt">The event name.</param>
    /// <param name="guards">Named guard results for this send.</param>
    /// <returns>Whether any transition was taken. False leaves the configuration and actions untouched.</returns>
    public bool Send(string evt, IReadOnlyDictionary<string, bool>? guards = null)
    {
        var def = Def;
        var config = _configStates;

        // 1. Enabled transitions: per active leaf, the innermost passing match on its chain.
        var candidates = new List<(string Source, Transition Trans, string Leaf)>();
        foreach (var leaf in config.Where(def.IsLeaf).OrderBy(state => state, StringComparer.Ordinal))
        {
            foreach (var anc in def.AncestorsInclusive(leaf))
            {
                if (!def.States.TryGetValue(anc, out var sd)) continue;
                if (!sd.On.TryGetValue(evt, out var t)) continue;
                if (!GuardPasses(t, guards)) continue;
                candidates.Add((anc, t, leaf));

                // An optimization, NOT the mechanism that enforces innermost-first. Removing it
                // leaves behaviour identical, because step 2 sorts candidates by source depth
                // descending and an outer candidate's exit set always contains the inner one's —
                // so it is skipped on overlap. Verified by mutation: dropping this break keeps the
                // whole corpus and the native tests green. It only avoids building candidates that
                // would be discarded.
                break;
            }
        }

        if (candidates.Count == 0)
        {
            LastActions = [];
            return false;
        }

        // 2. Conflict resolution: source depth descending, then document order; take greedily,
        //    skipping any candidate whose exit set overlaps one already taken.
        candidates = [.. candidates
            .OrderByDescending(c => def.Depth(c.Source))
            .ThenBy(c => def.Order(c.Source))];

        var exitUnion = new HashSet<string>(StringComparer.Ordinal);
        var enterUnion = new HashSet<string>(StringComparer.Ordinal);
        var taken = new List<Transition>();
        foreach (var cand in candidates)
        {
            var (exitSet, enterSet) = ComputeExitEnter(cand.Source, cand.Trans, cand.Leaf, config);
            if (exitSet.Overlaps(exitUnion)) continue;
            exitUnion.UnionWith(exitSet);
            enterUnion.UnionWith(enterSet);
            taken.Add(cand.Trans);
        }

        if (taken.Count == 0)
        {
            LastActions = [];
            return false;
        }

        // 3. Record history for every exited region that owns a history child — BEFORE the
        //    configuration changes, since the recording is of what was active.
        foreach (var s in exitUnion)
        {
            if (HistoryChildOf(s) is { } hChild) RecordRegion(s, hChild, config);
        }

        // 4. Action trace: exit innermost-first, then transition actions, then entry
        //    outermost-first.
        var actions = new List<string>();
        foreach (var s in exitUnion
                     .OrderByDescending(def.Depth)
                     .ThenBy(def.Order)
                     .ThenBy(s => s, StringComparer.Ordinal))
        {
            if (def.States.TryGetValue(s, out var sd)) actions.AddRange(sd.Exit);
        }

        foreach (var t in taken) actions.AddRange(t.Actions);

        foreach (var s in enterUnion
                     .OrderBy(def.Depth)
                     .ThenBy(def.Order)
                     .ThenBy(s => s, StringComparer.Ordinal))
        {
            if (def.States.TryGetValue(s, out var sd)) actions.AddRange(sd.Entry);
        }

        var next = new CompatSet<string>(config, StringComparer.Ordinal);
        next.ExceptWith(exitUnion);
        next.UnionWith(enterUnion.Where(s => !def.IsHistory(s)));
        _configStates = next;
        LastActions = actions;
        _config.Set(ConfigKey(next));
        return true;
    }

    private static bool GuardPasses(Transition t, IReadOnlyDictionary<string, bool>? guards)
    {
        if (t.Guard is null) return true;
        return guards is not null && guards.TryGetValue(t.Guard, out var ok) && ok;
    }

    private static string ConfigKey(IEnumerable<string> set) =>
        string.Join("|", set.OrderBy(state => state, StringComparer.Ordinal));

    private (HashSet<string> Exit, HashSet<string> Enter) ComputeExitEnter(
        string source,
        Transition t,
        string leaf,
        IReadOnlySet<string> config)
    {
        var def = Def;
        var target = t.Target;
        var isInternal = t.Internal &&
                         (string.Equals(target, source, StringComparison.Ordinal) ||
                          def.IsProperDescendant(target, source));
        var lca = isInternal ? source : def.Lca(leaf, target);

        var exit = config.Where(s => def.IsProperDescendant(s, lca)).ToHashSet(StringComparer.Ordinal);
        var enter = new HashSet<string>(StringComparer.Ordinal);

        if (def.IsHistory(target))
        {
            var region = def.ParentOf(target);
            foreach (var p in PathBelow(lca, region)) enter.Add(p);
            RestoreViaHistory(target, region, enter);
        }
        else
        {
            foreach (var p in PathBelow(lca, target)) enter.Add(p);
            EnterSubtree(target, enter, []);
        }

        return (exit, enter);
    }

    private void RestoreViaHistory(string hist, string region, HashSet<string> enter)
    {
        switch (_history.TryGetValue(hist, out var rec) ? rec : null)
        {
            case ShallowRecording shallow:
                enter.Add(shallow.Child);
                EnterSubtree(shallow.Child, enter, []);
                return;

            case DeepRecording deep:
                enter.UnionWith(deep.Set);
                return;

            default:
                // First entry: descend via the history node's `default`, else the region's initial.
                var start = Def.States.TryGetValue(hist, out var hsd) ? hsd.Default : null;
                if (string.IsNullOrEmpty(start))
                {
                    start = Def.States.TryGetValue(region, out var rsd) ? rsd.Initial : null;
                }

                if (string.IsNullOrEmpty(start)) return;
                foreach (var p in PathBelow(region, start)) enter.Add(p);
                EnterSubtree(start, enter, []);
                return;
        }
    }

    private void EnterSubtree(string state, HashSet<string> enter, List<string> actions)
    {
        enter.Add(state);
        if (Def.States.TryGetValue(state, out var sd)) actions.AddRange(sd.Entry);

        switch (Def.Kind(state))
        {
            case StateKind.Compound when sd?.Initial is { Length: > 0 } initial:
                EnterSubtree(initial, enter, actions);
                break;

            case StateKind.Parallel:
                foreach (var region in Def.Children(state)) EnterSubtree(region, enter, actions);
                break;

            case StateKind.Atomic:
            case StateKind.Final:
            case StateKind.Compound: // compound WITHOUT an initial child — see the guard above
            case StateKind.HistoryShallow:
            case StateKind.HistoryDeep:
            default:
                // INTENTIONAL: entering a leaf descends no further, and the two history kinds are
                // resolved by `RestoreViaHistory` before this walk ever sees them. `Kind` already
                // fails closed on an unrecognised history spelling, so the only value that can
                // reach `default` is a StateKind added to the enum without updating this walk —
                // which must degrade to "leaf", never to a throw, because entry runs on the hot
                // transition path. Pinned by `EnteringALeafDescendsNoFurther`.
                break;
        }
    }

    /// <summary>The path from just below <paramref name="lca"/> down to <paramref name="target"/>, inclusive.</summary>
    private List<string> PathBelow(string lca, string target)
    {
        var chain = Def.AncestorsInclusive(target); // [target, …, root]
        var idx = chain.IndexOf(lca);
        if (idx < 0) idx = chain.Count;
        var sub = chain.Take(idx).ToList();
        sub.Reverse();
        return sub;
    }

    private string? HistoryChildOf(string region) =>
        Def.Children(region).FirstOrDefault(Def.IsHistory);

    private void RecordRegion(string region, string histChild, IReadOnlySet<string> config)
    {
        if (Def.Kind(histChild) is StateKind.HistoryShallow)
        {
            // Shallow: the direct child of the region that was active.
            foreach (var child in Def.Children(region))
            {
                if (config.Contains(child) && !Def.IsHistory(child))
                {
                    _history[histChild] = new ShallowRecording(child);
                    return;
                }
            }

            return;
        }

        // Deep: every active state strictly below the region, so the full nested leaf resumes.
        _history[histChild] = new DeepRecording(
            [.. config.Where(s => Def.IsProperDescendant(s, region) && !Def.IsHistory(s))]);
    }

    private abstract record Recording;

    private sealed record ShallowRecording(string Child) : Recording;

    private sealed record DeepRecording(IReadOnlyList<string> Set) : Recording;
}
