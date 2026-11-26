// Graph IR interpreter. A graph document parses into an expression tree, lowers
// onto sources, computeds, and effects, and emits canonical-source.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lazily;

/// <summary>A parse, check, edit, or value fault.</summary>
public sealed class GraphIrError : Exception
{
    /// <summary>Creates a fault with the given message.</summary>
    public GraphIrError(string message) : base(message) { }
}

/// <summary>Source merge policies. The constants are the wire strings.</summary>
public static class Policy
{
    /// <summary>Replace the live value. Omitted when emitted.</summary>
    public const string KeepLatest = "keep-latest";

    /// <summary>Add the operand to the live number.</summary>
    public const string Sum = "sum";

    /// <summary>Keep the greater number.</summary>
    public const string Max = "max";

    /// <summary>Keep the lesser number.</summary>
    public const string Min = "min";

    /// <summary>Boolean conjunction.</summary>
    public const string And = "and";

    /// <summary>Boolean disjunction.</summary>
    public const string Or = "or";
}

/// <summary>Read sorts before write.</summary>
public static class EdgeKind
{
    /// <summary>A read edge.</summary>
    public const string Read = "read";

    /// <summary>A write edge.</summary>
    public const string Write = "write";
}

/// <summary>A derived read or write edge.</summary>
public sealed record Edge(string From, string Kind, string To);

/// <summary>One successful effect evaluation. Removal does not erase it.</summary>
public sealed record GraphEffectRun(string Id, object? Result);

/// <summary>An expression node. <see cref="Op"/> is set for calls.</summary>
public sealed class GraphExpr
{
    internal GraphExpr(string type, object? lit = null, string? reference = null, string? op = null, List<GraphExpr>? args = null, List<GraphField>? fields = null)
    {
        Type = type;
        Lit = lit;
        Ref = reference;
        Op = op;
        Args = args ?? [];
        Fields = fields ?? [];
    }

    internal string Type { get; }
    internal object? Lit { get; }
    internal string? Ref { get; }

    /// <summary>The operator name, or null when this expression is not a call.</summary>
    public string? Op { get; }

    internal List<GraphExpr> Args { get; }
    internal List<GraphField> Fields { get; }
}

internal sealed class GraphField(string key, GraphExpr value)
{
    public string Key { get; } = key;
    public GraphExpr Value { get; } = value;
}

internal sealed class GraphStmt
{
    public GraphStmt(string type, GraphExpr? expr = null, string? target = null, List<GraphStmt>? steps = null, GraphExpr? cond = null, GraphStmt? then = null, GraphStmt? otherwise = null)
    {
        Type = type;
        Expr = expr;
        Target = target;
        Steps = steps ?? [];
        Cond = cond;
        Then = then;
        Otherwise = otherwise;
    }

    public string Type { get; }
    public GraphExpr? Expr { get; }
    public string? Target { get; }
    public List<GraphStmt> Steps { get; }
    public GraphExpr? Cond { get; }
    public GraphStmt? Then { get; }
    public GraphStmt? Otherwise { get; }
}

/// <summary>One node of a parsed graph.</summary>
public sealed class GraphIrNode
{
    internal GraphIrNode(string kind, string id, object? init = null, string policy = Policy.KeepLatest, bool eager = false, GraphExpr? expr = null, GraphStmt? body = null)
    {
        Kind = kind;
        Id = id;
        Init = init;
        PolicyName = policy;
        Eager = eager;
        Expr = expr;
        Body = body;
    }

    /// <summary>source, computed, or effect.</summary>
    public string Kind { get; }

    /// <summary>The node id.</summary>
    public string Id { get; }

    internal object? Init { get; }
    internal string PolicyName { get; }

    /// <summary>Whether a computed is eager. False for other kinds.</summary>
    public bool Eager { get; }

    /// <summary>The computed expression, when <see cref="Kind"/> is computed.</summary>
    public GraphExpr? Expr { get; }

    internal GraphStmt? Body { get; }
}

internal sealed class EditOp(string op, GraphIrNode? node = null, string? id = null)
{
    public string Op { get; } = op;
    public GraphIrNode? Node { get; } = node;
    public string? Id { get; } = id;
}

/// <summary>A parsed graph definition.</summary>
public sealed class GraphIr
{
    const string Version = "1";
    internal static readonly System.Text.RegularExpressions.Regex IdPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal static readonly HashSet<string> Unary = ["neg", "not", "len"];
    internal static readonly HashSet<string> Binary = ["add", "sub", "mul", "div", "mod", "min", "max", "eq", "ne", "lt", "le", "gt", "ge", "concat", "get", "and", "or"];
    internal static readonly HashSet<string> Policies = [Policy.KeepLatest, Policy.Sum, Policy.Max, Policy.Min, Policy.And, Policy.Or];

    readonly List<GraphIrNode> _nodes;
    readonly Dictionary<string, int> _index;

    internal GraphIr(string? id, List<GraphIrNode> nodes, Dictionary<string, int> index)
    {
        Id = id;
        _nodes = nodes;
        _index = index;
    }

    /// <summary>The document id, when the graph named one.</summary>
    public string? Id { get; }

    /// <summary>Parses a graph document.</summary>
    public static GraphIr FromJsonStr(string text) => GraphRuntime.GraphFrom(GraphRuntime.ParseGraphJson(text));

    /// <summary>The node with <paramref name="id"/>, or null.</summary>
    public GraphIrNode? Node(string id) => _index.TryGetValue(id, out var slot) ? _nodes[slot] : null;

    /// <summary>Canonical JSON for this document.</summary>
    public string ToJsonText() => GraphRuntime.CanonicalJson(ToJson());

    /// <summary>Derived read and write edges, sorted by from, kind, then to.</summary>
    public List<Edge> StaticEdges()
    {
        var edges = new List<Edge>();
        foreach (var node in _nodes)
        {
            var (reads, writes) = GraphRuntime.EdgeSets(node);
            foreach (var to in reads) edges.Add(new Edge(node.Id, EdgeKind.Read, to));
            foreach (var to in writes) edges.Add(new Edge(node.Id, EdgeKind.Write, to));
        }
        edges.Sort(static (a, b) =>
        {
            var from = string.CompareOrdinal(a.From, b.From);
            if (from != 0) return from;
            var kind = string.CompareOrdinal(a.Kind, b.Kind);
            return kind != 0 ? kind : string.CompareOrdinal(a.To, b.To);
        });
        return edges;
    }

    /// <summary>Canonical source text. Host writes do not change it.</summary>
    public string EmitSource()
    {
        var b = new StringBuilder();
        if (Id is null) b.Append("graph ir ").Append(Version).Append('\n');
        else b.Append("graph ").Append(Id).Append(" ir ").Append(Version).Append('\n');
        foreach (var node in _nodes) b.Append(GraphRuntime.EmitNode(node)).Append('\n');
        return b.ToString();
    }

    internal IReadOnlyList<GraphIrNode> Nodes => _nodes;

    internal GraphIr Clone() => new(Id, [.. _nodes], new Dictionary<string, int>(_index));

    internal void ApplyOp(EditOp op)
    {
        switch (op.Op)
        {
            case "add":
                if (_index.ContainsKey(op.Node!.Id)) throw new GraphIrError($"add requires a new id, `{op.Node.Id}` already exists");
                _nodes.Add(op.Node);
                break;
            case "replace":
                if (!_index.TryGetValue(op.Node!.Id, out var slot)) throw new GraphIrError($"replace requires an existing node `{op.Node.Id}`");
                _nodes[slot] = op.Node;
                break;
            case "remove":
                if (!_index.TryGetValue(op.Id!, out var remove)) throw new GraphIrError($"remove requires an existing node `{op.Id}`");
                _nodes.RemoveAt(remove);
                break;
        }
        Reindex();
        Check();
    }

    internal void Reindex()
    {
        _index.Clear();
        for (var slot = 0; slot < _nodes.Count; slot++)
        {
            var id = _nodes[slot].Id;
            if (_index.ContainsKey(id)) throw new GraphIrError($"duplicate node id `{id}`");
            _index[id] = slot;
        }
    }

    internal void Check()
    {
        CheckRefs();
        CheckInits();
        CheckCycles();
    }

    void CheckRefs()
    {
        foreach (var node in _nodes)
        {
            var (reads, writes) = GraphRuntime.EdgeSets(node);
            foreach (var to in reads)
            {
                var target = Node(to) ?? throw new GraphIrError($"node `{node.Id}` reads unknown node `{to}`");
                if (target.Kind == "effect") throw new GraphIrError($"node `{node.Id}` reads effect `{to}`, which has no value");
            }
            foreach (var to in writes)
            {
                var target = Node(to) ?? throw new GraphIrError($"node `{node.Id}` writes unknown node `{to}`");
                if (target.Kind != "source") throw new GraphIrError($"node `{node.Id}` writes non-source `{to}`");
            }
        }
    }

    void CheckInits()
    {
        foreach (var node in _nodes)
        {
            if (node.Kind != "source") continue;
            try { GraphRuntime.CheckPolicyInit(node.PolicyName, node.Init); }
            catch (GraphIrError err) { throw new GraphIrError($"source `{node.Id}`: {err.Message}"); }
        }
    }

    void CheckCycles()
    {
        var color = new Dictionary<string, int>();
        var stack = new List<string>();
        void Visit(string id)
        {
            switch (color.GetValueOrDefault(id))
            {
                case 1:
                    var start = stack.IndexOf(id);
                    throw new GraphIrError("cycle in read edges: " + string.Join(" -> ", stack.Skip(start).Append(id)));
                case 2:
                    return;
            }
            color[id] = 1;
            stack.Add(id);
            var target = Node(id);
            if (target is not null)
            {
                foreach (var dep in GraphRuntime.EdgeSets(target).Reads.OrderBy(static item => item))
                    if (Node(dep) is not null) Visit(dep);
            }
            stack.RemoveAt(stack.Count - 1);
            color[id] = 2;
        }
        foreach (var node in _nodes) Visit(node.Id);
    }

    Dictionary<string, object?> ToJson()
    {
        var obj = new Dictionary<string, object?> { ["kind"] = "graph", ["ir"] = Version };
        if (Id is not null) obj["id"] = Id;
        obj["nodes"] = _nodes.Select(GraphRuntime.NodeJson).ToList();
        return obj;
    }
}

/// <summary>An ordered list of add, replace, and remove ops.</summary>
public sealed class GraphEdit
{
    internal GraphEdit(List<EditOp> ops) => Ops = ops;
    internal List<EditOp> Ops { get; }

    /// <summary>Parses an edit document.</summary>
    public static GraphEdit FromJsonStr(string text) => GraphRuntime.EditFrom(GraphRuntime.ParseGraphJson(text));
}

sealed class SourceState(object? value, string policy)
{
    public object? Value { get; set; } = value;
    public string Policy { get; } = policy;
}

sealed class ComputedState(GraphExpr expr, bool eager)
{
    public GraphExpr Expr { get; } = expr;
    public bool Eager { get; } = eager;
    public object? Cache { get; set; }
    public bool HasValue { get; set; }
    public bool Dirty { get; set; } = true;
}

sealed class EffectState(GraphStmt body)
{
    public GraphStmt Body { get; } = body;
}

sealed class Snap(string kind, SourceState? source = null, ComputedState? computed = null, EffectState? effect = null, HashSet<string>? deps = null)
{
    public string Kind { get; } = kind;
    public SourceState? Source { get; } = source;
    public ComputedState? Computed { get; } = computed;
    public EffectState? Effect { get; } = effect;
    public HashSet<string> Deps { get; } = deps ?? [];
}

/// <summary>A document plus the cells it was lowered onto.</summary>
public sealed class GraphRuntime
{
    /// <summary>Default bound on effect-drain iterations.</summary>
    public const int DefaultDrainBudget = 100_000;

    readonly Dictionary<string, SourceState> _sources = [];
    readonly Dictionary<string, ComputedState> _computeds = [];
    readonly Dictionary<string, EffectState> _effects = [];
    readonly Dictionary<string, HashSet<string>> _deps = [];
    readonly Dictionary<string, HashSet<string>> _subs = [];
    readonly Dictionary<string, HashSet<string>> _observed = [];
    readonly List<GraphEffectRun> _trace = [];
    readonly List<HashSet<string>> _frames = [];
    readonly List<string?> _stack = [];
    readonly HashSet<string> _computing = [];
    readonly Queue<string> _scheduled = new();
    readonly HashSet<string> _scheduledSet = [];
    string? _current;
    bool _draining;
    string? _fault;
    GraphIr _doc;

    GraphRuntime(GraphIr doc, int drainBudget)
    {
        _doc = doc;
        DrainBudget = drainBudget;
    }

    /// <summary>The budget passed to <see cref="Lower"/>.</summary>
    public int DrainBudget { get; }

    /// <summary>The iteration count that stopped the latest drain, when one did.</summary>
    public int? LastDrainExhaustion { get; private set; }

    /// <summary>Parses and lowers a document.</summary>
    public static GraphRuntime LoadJson(string text, int? drainBudget = null) =>
        Lower(GraphIr.FromJsonStr(text), drainBudget);

    /// <summary>Installs <paramref name="doc"/>. A non-positive budget selects the default.</summary>
    public static GraphRuntime Lower(GraphIr doc, int? drainBudget = null)
    {
        doc.Check();
        var budget = drainBudget is null or <= 0 ? DefaultDrainBudget : drainBudget.Value;
        var rt = new GraphRuntime(doc.Clone(), budget);
        rt.InstallDocument();
        return rt;
    }

    /// <summary>Whether the document still contains <paramref name="id"/>.</summary>
    public bool Contains(string id) => _doc.Node(id) is not null;

    /// <summary>Whether the computed is eager.</summary>
    public bool IsEager(string id)
    {
        var node = _doc.Node(id) ?? throw new GraphIrError($"unknown node `{id}`");
        if (node.Kind != "computed") throw new GraphIrError($"`{id}` is not a computed");
        return node.Eager;
    }

    /// <summary>The source merge policy.</summary>
    public string SourcePolicy(string id)
    {
        var node = _doc.Node(id) ?? throw new GraphIrError($"unknown node `{id}`");
        if (node.Kind != "source") throw new GraphIrError($"`{id}` is not a source");
        return node.PolicyName;
    }

    /// <summary>Edges of the current document.</summary>
    public List<Edge> StaticEdges() => _doc.StaticEdges();

    /// <summary>Canonical source of the current document.</summary>
    public string EmitSource() => _doc.EmitSource();

    /// <summary>Refs actually evaluated the last time <paramref name="id"/> ran.</summary>
    public HashSet<string> ObservedReads(string id) =>
        _observed.TryGetValue(id, out var set) ? [.. set] : [];

    /// <summary>Successful effect evaluations, oldest first.</summary>
    public IReadOnlyList<GraphEffectRun> EffectTrace() => _trace.ToList();

    /// <summary>The live value of a source or computed.</summary>
    public object? Get(string id)
    {
        if (_effects.ContainsKey(id) && !_sources.ContainsKey(id) && !_computeds.ContainsKey(id))
            throw new GraphIrError($"`{id}` is an effect and has no value");
        if (_sources.TryGetValue(id, out var src)) return Freeze(src.Value);
        if (!_computeds.TryGetValue(id, out var comp)) throw new GraphIrError($"unknown node `{id}`");
        if (comp.Dirty || !comp.HasValue) Recompute(id);
        return Freeze(_computeds[id].Cache);
    }

    /// <summary>Replaces a source. An equal JSON value does not reschedule.</summary>
    public void WriteSource(string id, object? value)
    {
        SetSource(id, Freeze(value));
        Drain();
        RaiseFault();
    }

    /// <summary>Applies each op. A failed op leaves the previous document in place.</summary>
    public void Apply(GraphEdit edit)
    {
        for (var index = 0; index < edit.Ops.Count; index++)
        {
            var op = edit.Ops[index];
            var next = _doc.Clone();
            try { next.ApplyOp(op); }
            catch (GraphIrError err) { throw new GraphIrError($"edit op {index}: {err.Message}"); }
            try { InstallOp(op); }
            catch (GraphIrError err) { throw new GraphIrError($"edit op {index}: {err.Message}"); }
            _doc = next;
        }
    }

    void InstallDocument()
    {
        foreach (var node in _doc.Nodes)
            if (node.Kind == "source") InsertSource(node);
        foreach (var node in _doc.Nodes)
            if (node.Kind == "computed") InsertComputed(node);
        foreach (var node in _doc.Nodes)
            if (node.Kind == "computed" && node.Eager) Recompute(node.Id);
        foreach (var node in _doc.Nodes)
            if (node.Kind == "effect") InsertEffect(node);
        RaiseFault();
    }

    void InstallOp(EditOp op)
    {
        switch (op.Op)
        {
            case "add": AddNode(op.Node!); break;
            case "replace": ReplaceNode(op.Node!); break;
            default: RemoveNode(op.Id!); break;
        }
        Drain();
        RaiseFault();
    }

    void AddNode(GraphIrNode node)
    {
        switch (node.Kind)
        {
            case "source":
                InsertSource(node);
                break;
            case "computed":
                InsertComputed(node);
                if (node.Eager)
                {
                    try { Recompute(node.Id); }
                    catch (GraphIrError) { DropBinding(node.Id); throw; }
                }
                break;
            default:
                InsertEffect(node);
                break;
        }
    }

    void ReplaceNode(GraphIrNode node)
    {
        var snap = Snapshot(node.Id);
        DropBinding(node.Id);
        try
        {
            AddNode(node);
            Notify(node.Id);
        }
        catch (GraphIrError)
        {
            DropBinding(node.Id);
            Restore(node.Id, snap);
            throw;
        }
    }

    void RemoveNode(string id)
    {
        if (!_sources.ContainsKey(id) && !_computeds.ContainsKey(id) && !_effects.ContainsKey(id))
            throw new GraphIrError($"unknown node `{id}`");
        DropBinding(id);
        _subs.Remove(id);
    }

    void InsertSource(GraphIrNode node)
    {
        CheckPolicyInit(node.PolicyName, node.Init);
        _sources[node.Id] = new SourceState(Freeze(node.Init), node.PolicyName);
    }

    void InsertComputed(GraphIrNode node)
    {
        _computeds[node.Id] = new ComputedState(node.Expr!, node.Eager);
        _deps[node.Id] = [];
    }

    void InsertEffect(GraphIrNode node)
    {
        _effects[node.Id] = new EffectState(node.Body!);
        _deps[node.Id] = [];
        try { RunEffect(node.Id); }
        catch (GraphIrError) { DropBinding(node.Id); throw; }
    }

    Snap? Snapshot(string id)
    {
        if (_sources.TryGetValue(id, out var src)) return new Snap("source", source: new SourceState(Freeze(src.Value), src.Policy));
        if (_computeds.TryGetValue(id, out var comp))
        {
            return new Snap("computed", computed: new ComputedState(comp.Expr, comp.Eager)
            {
                Cache = comp.HasValue ? Freeze(comp.Cache) : null,
                HasValue = comp.HasValue,
                Dirty = comp.Dirty,
            }, deps: _deps.TryGetValue(id, out var deps) ? [.. deps] : []);
        }
        if (_effects.TryGetValue(id, out var effect))
            return new Snap("effect", effect: effect, deps: _deps.TryGetValue(id, out var deps) ? [.. deps] : []);
        return null;
    }

    void Restore(string id, Snap? snap)
    {
        if (snap is null) return;
        switch (snap.Kind)
        {
            case "source":
                _sources[id] = snap.Source!;
                break;
            case "computed":
                _computeds[id] = snap.Computed!;
                _deps[id] = [];
                foreach (var dep in snap.Deps) { SubAdd(dep, id); _deps[id].Add(dep); }
                break;
            case "effect":
                _effects[id] = snap.Effect!;
                _deps[id] = [];
                foreach (var dep in snap.Deps) { SubAdd(dep, id); _deps[id].Add(dep); }
                break;
        }
    }

    void DropBinding(string id)
    {
        _sources.Remove(id);
        _computeds.Remove(id);
        _effects.Remove(id);
        if (_deps.TryGetValue(id, out var deps))
            foreach (var dep in deps) _subs.GetValueOrDefault(dep)?.Remove(id);
        _deps.Remove(id);
        _observed.Remove(id);
        _scheduledSet.Remove(id);
    }

    object? SetSource(string id, object? value)
    {
        if (!_sources.TryGetValue(id, out var src))
        {
            if (_computeds.ContainsKey(id) || _effects.ContainsKey(id)) throw new GraphIrError($"`{id}` is not a source");
            throw new GraphIrError($"unknown node `{id}`");
        }
        var changed = !JsonEq(src.Value, value);
        src.Value = Freeze(value);
        if (changed) Notify(id);
        return Freeze(src.Value);
    }

    object? MergeSource(string id, object? value)
    {
        if (!_sources.TryGetValue(id, out var src))
        {
            if (_computeds.ContainsKey(id) || _effects.ContainsKey(id)) throw new GraphIrError($"`{id}` is not a source");
            throw new GraphIrError($"unknown node `{id}`");
        }
        return SetSource(id, FoldPolicy(src.Policy, src.Value, value));
    }

    void Notify(string id)
    {
        if (!_subs.TryGetValue(id, out var readers)) return;
        foreach (var dep in readers.OrderBy(static item => item))
        {
            if (_computeds.TryGetValue(dep, out var comp))
            {
                if (comp.Eager || (_subs.TryGetValue(dep, out var down) && down.Count > 0))
                {
                    if (_computing.Contains(dep)) { comp.Dirty = true; continue; }
                    Recompute(dep);
                }
                else comp.Dirty = true;
            }
            else if (_effects.ContainsKey(dep)) Schedule(dep);
        }
    }

    void Schedule(string id)
    {
        if (_effects.ContainsKey(id) && _scheduledSet.Add(id)) _scheduled.Enqueue(id);
    }

    void Drain()
    {
        if (_draining) return;
        _draining = true;
        try
        {
            var iterations = 0;
            while (_scheduled.Count > 0)
            {
                var id = _scheduled.Dequeue();
                _scheduledSet.Remove(id);
                iterations++;
                if (iterations >= DrainBudget)
                {
                    LastDrainExhaustion = iterations;
                    return;
                }
                if (!_effects.ContainsKey(id)) continue;
                try { RunEffect(id); }
                catch (GraphIrError err) { _fault = err.Message; return; }
            }
        }
        finally { _draining = false; }
    }

    void RaiseFault()
    {
        var msg = _fault;
        if (msg is null) return;
        _fault = null;
        throw new GraphIrError(msg);
    }

    void RunEffect(string id)
    {
        var state = _effects[id];
        Begin(id);
        object? result;
        try { result = EvalStmt(state.Body); }
        finally { End(id); }
        _trace.Add(new GraphEffectRun(id, Freeze(result)));
        Drain();
    }

    object? Recompute(string id)
    {
        var state = _computeds[id];
        if (!_computing.Add(id)) throw new GraphIrError($"cycle in read edges: {id}");
        try
        {
            Begin(id);
            object? value;
            try { value = EvalExpr(state.Expr); }
            finally { End(id); }
            var changed = !state.HasValue || !JsonEq(state.Cache, value);
            state.Cache = Freeze(value);
            state.HasValue = true;
            state.Dirty = false;
            if (changed) Notify(id);
            return state.Cache;
        }
        catch (GraphIrError)
        {
            state.Dirty = true;
            state.HasValue = false;
            state.Cache = null;
            throw;
        }
        finally { _computing.Remove(id); }
    }

    void Begin(string id)
    {
        if (_deps.TryGetValue(id, out var previous))
            foreach (var dep in previous) _subs.GetValueOrDefault(dep)?.Remove(id);
        _deps[id] = [];
        _frames.Add([]);
        _stack.Add(_current);
        _current = id;
    }

    void End(string id)
    {
        _observed[id] = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        _current = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
    }

    void Note(string id)
    {
        if (_frames.Count == 0 || _current is null) throw new GraphIrError("read outside a node evaluation");
        _frames[^1].Add(id);
        SubAdd(id, _current);
        _deps[_current].Add(id);
    }

    void SubAdd(string src, string reader)
    {
        if (!_subs.TryGetValue(src, out var set)) _subs[src] = set = [];
        set.Add(reader);
    }

    object? ReadRef(string id)
    {
        Note(id);
        if (_sources.TryGetValue(id, out var src)) return Freeze(src.Value);
        if (_computeds.TryGetValue(id, out var comp))
        {
            if (comp.Dirty || !comp.HasValue) return Freeze(Recompute(id));
            return Freeze(comp.Cache);
        }
        if (_effects.ContainsKey(id)) throw new GraphIrError($"`{id}` is an effect and has no value");
        throw new GraphIrError($"unknown node `{id}`");
    }

    object? EvalExpr(GraphExpr expr) => expr.Type switch
    {
        "lit" => Freeze(expr.Lit),
        "ref" => ReadRef(expr.Ref!),
        "object" => EvalObject(expr),
        "call" => expr.Op switch
        {
            "and" => EvalAnd(expr.Args),
            "or" => EvalOr(expr.Args),
            "if" => EvalIf(expr.Args),
            "array" => expr.Args.Select(EvalExpr).ToList(),
            _ => EvalValues(expr.Op!, expr.Args.Select(EvalExpr).ToList()),
        },
        _ => throw new GraphIrError($"unknown expression `{expr.Type}`"),
    };

    Dictionary<string, object?> EvalObject(GraphExpr expr)
    {
        var obj = new Dictionary<string, object?>();
        foreach (var field in expr.Fields)
        {
            if (obj.ContainsKey(field.Key)) throw new GraphIrError($"duplicate object key `{field.Key}`");
            obj[field.Key] = EvalExpr(field.Value);
        }
        return obj;
    }

    bool EvalAnd(List<GraphExpr> args)
    {
        if (args.Count != 2) throw new GraphIrError("op `and` has the wrong number of arguments");
        if (!ExpectBool(EvalExpr(args[0]))) return false;
        return ExpectBool(EvalExpr(args[1]));
    }

    bool EvalOr(List<GraphExpr> args)
    {
        if (args.Count != 2) throw new GraphIrError("op `or` has the wrong number of arguments");
        if (ExpectBool(EvalExpr(args[0]))) return true;
        return ExpectBool(EvalExpr(args[1]));
    }

    object? EvalIf(List<GraphExpr> args)
    {
        if (args.Count != 3) throw new GraphIrError("op `if` has the wrong number of arguments");
        return EvalExpr(ExpectBool(EvalExpr(args[0])) ? args[1] : args[2]);
    }

    object? EvalStmt(GraphStmt stmt) => stmt.Type switch
    {
        "eval" => EvalExpr(stmt.Expr!),
        "set" => SetSource(stmt.Target!, EvalExpr(stmt.Expr!)),
        "merge" => MergeSource(stmt.Target!, EvalExpr(stmt.Expr!)),
        "seq" => stmt.Steps.Select(EvalStmt).ToList(),
        "when" => ExpectBool(EvalExpr(stmt.Cond!))
            ? EvalStmt(stmt.Then!)
            : stmt.Otherwise is null ? null : EvalStmt(stmt.Otherwise),
        _ => throw new GraphIrError($"unknown statement `{stmt.Type}`"),
    };

    internal static object? ParseGraphJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return FromElement(doc.RootElement);
        }
        catch (GraphIrError) { throw; }
        catch (JsonException err) { throw new GraphIrError($"graph document is not JSON: {err.Message}"); }
    }

    internal static object? FromElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => Number(el),
        JsonValueKind.Array => el.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => FromElement(p.Value)),
        _ => throw new GraphIrError("value is not JSON"),
    };

    internal static object Number(JsonElement el)
    {
        var raw = el.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
        {
            var number = el.GetDouble();
            if (!double.IsFinite(number)) throw new GraphIrError($"number {raw} is outside the graph IR numeric range");
            return number;
        }
        if (el.TryGetInt64(out var integer)) return integer;
        var wide = el.GetDouble();
        if (!double.IsFinite(wide)) throw new GraphIrError($"number {raw} is outside the graph IR numeric range");
        return wide;
    }

    internal static GraphIr GraphFrom(object? value)
    {
        var obj = AsMap(value, "graph document");
        Allow(obj, "graph document", "kind", "ir", "id", "nodes");
        var kind = RequireString(obj, "kind", "graph document");
        if (kind != "graph") throw new GraphIrError($"graph document kind must be \"graph\", got `{kind}`");
        RequireVersion(obj, "graph document");
        var id = OptionalId(obj, "id", "graph id");
        if (Require(obj, "nodes", "graph document") is not List<object?> list) throw new GraphIrError("graph nodes must be an array");
        var nodes = new List<GraphIrNode>();
        var seen = new HashSet<string>();
        foreach (var item in list)
        {
            var node = ParseNode(item);
            if (!seen.Add(node.Id)) throw new GraphIrError($"duplicate node id `{node.Id}`");
            nodes.Add(node);
        }
        var doc = new GraphIr(id, nodes, []);
        doc.Reindex();
        doc.Check();
        return doc;
    }

    internal static GraphEdit EditFrom(object? value)
    {
        var obj = AsMap(value, "graph edit");
        Allow(obj, "graph edit", "kind", "ir", "ops");
        var kind = RequireString(obj, "kind", "graph edit");
        if (kind != "edit") throw new GraphIrError($"graph edit kind must be \"edit\", got `{kind}`");
        RequireVersion(obj, "graph edit");
        if (Require(obj, "ops", "graph edit") is not List<object?> list) throw new GraphIrError("graph edit ops must be an array");
        if (list.Count == 0) throw new GraphIrError("graph edit ops must not be empty");
        return new GraphEdit(list.Select(ParseEdit).ToList());
    }

    internal static Dictionary<string, object?> AsMap(object? value, string what)
    {
        if (value is Dictionary<string, object?> map) return map;
        throw new GraphIrError($"{what} must be an object");
    }

    internal static void Allow(Dictionary<string, object?> obj, string what, params string[] keys)
    {
        var allowed = new HashSet<string>(keys);
        foreach (var key in obj.Keys)
            if (!allowed.Contains(key)) throw new GraphIrError($"{what}: unknown field `{key}`");
    }

    internal static object? Require(Dictionary<string, object?> obj, string field, string what)
    {
        if (!obj.ContainsKey(field)) throw new GraphIrError($"{what} requires `{field}`");
        return obj[field];
    }

    internal static string RequireString(Dictionary<string, object?> obj, string field, string what)
    {
        var value = Require(obj, field, what);
        if (value is not string text) throw new GraphIrError($"{what} `{field}` must be a string, got {CanonicalJson(value)}");
        return text;
    }

    internal static string ParseId(string text, string what)
    {
        if (!GraphIr.IdPattern.IsMatch(text) || text.Length > 128) throw new GraphIrError($"{what} `{text}` is not a graph IR id");
        return text;
    }

    internal static void RequireVersion(Dictionary<string, object?> obj, string what)
    {
        var version = RequireString(obj, "ir", what);
        if (version != "1") throw new GraphIrError($"unsupported graph IR version `{version}`");
    }

    internal static string? OptionalId(Dictionary<string, object?> obj, string field, string what)
    {
        if (!obj.ContainsKey(field)) return null;
        if (obj[field] is not string text) throw new GraphIrError($"{what} must be a string, got {CanonicalJson(obj[field])}");
        return ParseId(text, what);
    }

    internal static GraphIrNode ParseNode(object? value)
    {
        var obj = AsMap(value, "node");
        var kind = RequireString(obj, "kind", "node");
        var id = ParseId(RequireString(obj, "id", "node"), "node id");
        switch (kind)
        {
            case "source":
                Allow(obj, "source", "id", "kind", "init", "policy");
                if (!obj.ContainsKey("init")) throw new GraphIrError($"source `{id}` requires `init`");
                var policy = ParsePolicy(obj, id);
                CheckPolicyInit(policy, obj["init"]);
                return new GraphIrNode(kind, id, Freeze(obj["init"]), policy);
            case "computed":
                Allow(obj, "computed", "id", "kind", "expr", "eager");
                return new GraphIrNode(kind, id, eager: ParseEager(obj, id), expr: ParseExpr(Require(obj, "expr", "computed")));
            case "effect":
                Allow(obj, "effect", "id", "kind", "body");
                return new GraphIrNode(kind, id, body: ParseStmt(Require(obj, "body", "effect")));
            default:
                throw new GraphIrError($"unknown node kind `{kind}`");
        }
    }

    internal static string ParsePolicy(Dictionary<string, object?> obj, string id)
    {
        if (!obj.ContainsKey("policy")) return Policy.KeepLatest;
        if (obj["policy"] is not string value) throw new GraphIrError($"source `{id}` policy must be a string");
        if (!GraphIr.Policies.Contains(value)) throw new GraphIrError($"unknown source policy `{value}`");
        return value;
    }

    internal static bool ParseEager(Dictionary<string, object?> obj, string id)
    {
        if (!obj.ContainsKey("eager")) return false;
        if (obj["eager"] is not bool value) throw new GraphIrError($"computed `{id}` eager must be a boolean");
        return value;
    }

    internal static GraphExpr ParseExpr(object? value)
    {
        var obj = AsMap(value, "expression");
        Allow(obj, "expression", "lit", "ref", "op", "args", "object");
        var forms = new[] { "lit", "ref", "op", "object" }.Count(obj.ContainsKey);
        if (forms != 1) throw new GraphIrError("expression must be exactly one of lit, ref, op, or object");
        if (obj.ContainsKey("args") && !obj.ContainsKey("op")) throw new GraphIrError("args is only valid on an op expression");
        if (obj.ContainsKey("lit")) return new GraphExpr("lit", lit: Freeze(obj["lit"]));
        if (obj.ContainsKey("ref")) return new GraphExpr("ref", reference: ParseId(RequireString(obj, "ref", "expression"), "ref"));
        if (obj.ContainsKey("object")) return ParseObject(obj["object"]);
        var op = RequireString(obj, "op", "expression");
        if (!GraphIr.Unary.Contains(op) && !GraphIr.Binary.Contains(op) && op != "if" && op != "array")
            throw new GraphIrError($"unknown op `{op}`");
        if (Require(obj, "args", "op expression") is not List<object?> raw) throw new GraphIrError("op args must be an array");
        var ok = GraphIr.Unary.Contains(op) ? raw.Count == 1 : op == "if" ? raw.Count == 3 : op == "array" || raw.Count == 2;
        if (!ok) throw new GraphIrError($"op `{op}` has the wrong number of arguments");
        return new GraphExpr("call", op: op, args: raw.Select(ParseExpr).ToList());
    }

    internal static GraphExpr ParseObject(object? value)
    {
        if (value is not List<object?> list) throw new GraphIrError("object expression must be an array");
        var fields = new List<GraphField>();
        var seen = new HashSet<string>();
        foreach (var item in list)
        {
            var obj = AsMap(item, "object field");
            Allow(obj, "object field", "key", "value");
            var key = RequireString(obj, "key", "object field");
            if (!seen.Add(key)) throw new GraphIrError($"duplicate object key `{key}`");
            fields.Add(new GraphField(key, ParseExpr(Require(obj, "value", "object field"))));
        }
        return new GraphExpr("object", fields: fields);
    }

    internal static GraphStmt ParseStmt(object? value)
    {
        var obj = AsMap(value, "statement");
        var verb = RequireString(obj, "do", "statement");
        switch (verb)
        {
            case "eval":
                Allow(obj, "eval", "do", "expr");
                return new GraphStmt("eval", expr: ParseExpr(Require(obj, "expr", "eval")));
            case "set":
            case "merge":
                Allow(obj, verb, "do", "target", "expr");
                return new GraphStmt(verb, target: ParseId(RequireString(obj, "target", verb), "write target"), expr: ParseExpr(Require(obj, "expr", verb)));
            case "seq":
                Allow(obj, "seq", "do", "steps");
                if (Require(obj, "steps", "seq") is not List<object?> raw) throw new GraphIrError("seq steps must be an array");
                if (raw.Count == 0) throw new GraphIrError("seq steps must not be empty");
                return new GraphStmt("seq", steps: raw.Select(ParseStmt).ToList());
            case "when":
                Allow(obj, "when", "do", "cond", "then", "else");
                return new GraphStmt("when", cond: ParseExpr(Require(obj, "cond", "when")), then: ParseStmt(Require(obj, "then", "when")), otherwise: obj.ContainsKey("else") ? ParseStmt(obj["else"]) : null);
            default:
                throw new GraphIrError($"unknown statement `{verb}`");
        }
    }

    internal static EditOp ParseEdit(object? value)
    {
        var obj = AsMap(value, "edit op");
        var op = RequireString(obj, "op", "edit op");
        switch (op)
        {
            case "add":
            case "replace":
                Allow(obj, op, "op", "node");
                return new EditOp(op, node: ParseNode(Require(obj, "node", op)));
            case "remove":
                Allow(obj, "remove", "op", "id");
                return new EditOp(op, id: ParseId(RequireString(obj, "id", "remove"), "remove id"));
            default:
                throw new GraphIrError($"unknown edit op `{op}`");
        }
    }

    internal static (List<string> Reads, List<string> Writes) EdgeSets(GraphIrNode node)
    {
        var reads = new HashSet<string>();
        var writes = new HashSet<string>();
        if (node.Kind == "computed") CollectExpr(node.Expr!, reads);
        if (node.Kind == "effect") CollectStmt(node.Body!, reads, writes);
        return (reads.OrderBy(static item => item).ToList(), writes.OrderBy(static item => item).ToList());
    }

    internal static void CollectExpr(GraphExpr expr, HashSet<string> reads)
    {
        switch (expr.Type)
        {
            case "ref": reads.Add(expr.Ref!); break;
            case "call": foreach (var arg in expr.Args) CollectExpr(arg, reads); break;
            case "object": foreach (var field in expr.Fields) CollectExpr(field.Value, reads); break;
        }
    }

    internal static void CollectStmt(GraphStmt stmt, HashSet<string> reads, HashSet<string> writes)
    {
        switch (stmt.Type)
        {
            case "eval": CollectExpr(stmt.Expr!, reads); break;
            case "set":
            case "merge":
                writes.Add(stmt.Target!);
                CollectExpr(stmt.Expr!, reads);
                break;
            case "seq": foreach (var step in stmt.Steps) CollectStmt(step, reads, writes); break;
            case "when":
                CollectExpr(stmt.Cond!, reads);
                CollectStmt(stmt.Then!, reads, writes);
                if (stmt.Otherwise is not null) CollectStmt(stmt.Otherwise, reads, writes);
                break;
        }
    }

    internal static string EmitNode(GraphIrNode node) => node.Kind switch
    {
        "source" => node.PolicyName == Policy.KeepLatest
            ? $"source {node.Id} = {CanonicalJson(node.Init)}"
            : $"source {node.Id} = {CanonicalJson(node.Init)} policy {node.PolicyName}",
        "computed" => $"computed {node.Id}{(node.Eager ? " eager" : "")} = {EmitExpr(node.Expr!)}",
        _ => $"effect {node.Id} = {EmitStmt(node.Body!)}",
    };

    internal static string EmitExpr(GraphExpr expr) => expr.Type switch
    {
        "lit" => $"lit({CanonicalJson(expr.Lit)})",
        "ref" => $"ref({expr.Ref})",
        "call" => $"{expr.Op}({string.Join(", ", expr.Args.Select(EmitExpr))})",
        _ => $"object({string.Join(", ", expr.Fields.Select(field => $"{CanonicalJson(field.Key)}: {EmitExpr(field.Value)}"))})",
    };

    internal static string EmitStmt(GraphStmt stmt) => stmt.Type switch
    {
        "eval" => $"eval({EmitExpr(stmt.Expr!)})",
        "set" => $"set({stmt.Target}, {EmitExpr(stmt.Expr!)})",
        "merge" => $"merge({stmt.Target}, {EmitExpr(stmt.Expr!)})",
        "seq" => $"seq({string.Join(", ", stmt.Steps.Select(EmitStmt))})",
        _ => stmt.Otherwise is null
            ? $"when({EmitExpr(stmt.Cond!)}, {EmitStmt(stmt.Then!)})"
            : $"when({EmitExpr(stmt.Cond!)}, {EmitStmt(stmt.Then!)}, {EmitStmt(stmt.Otherwise)})",
    };

    internal static Dictionary<string, object?> NodeJson(GraphIrNode node)
    {
        switch (node.Kind)
        {
            case "source":
                var source = new Dictionary<string, object?> { ["id"] = node.Id, ["kind"] = "source", ["init"] = Freeze(node.Init) };
                if (node.PolicyName != Policy.KeepLatest) source["policy"] = node.PolicyName;
                return source;
            case "computed":
                var computed = new Dictionary<string, object?> { ["id"] = node.Id, ["kind"] = "computed", ["expr"] = ExprJson(node.Expr!) };
                if (node.Eager) computed["eager"] = true;
                return computed;
            default:
                return new Dictionary<string, object?> { ["id"] = node.Id, ["kind"] = "effect", ["body"] = StmtJson(node.Body!) };
        }
    }

    internal static Dictionary<string, object?> ExprJson(GraphExpr expr) => expr.Type switch
    {
        "lit" => new() { ["lit"] = Freeze(expr.Lit) },
        "ref" => new() { ["ref"] = expr.Ref },
        "call" => new() { ["op"] = expr.Op, ["args"] = expr.Args.Select(ExprJson).ToList() },
        _ => new() { ["object"] = expr.Fields.Select(field => new Dictionary<string, object?> { ["key"] = field.Key, ["value"] = ExprJson(field.Value) }).ToList() },
    };

    internal static Dictionary<string, object?> StmtJson(GraphStmt stmt)
    {
        switch (stmt.Type)
        {
            case "eval": return new() { ["do"] = "eval", ["expr"] = ExprJson(stmt.Expr!) };
            case "set":
            case "merge": return new() { ["do"] = stmt.Type, ["target"] = stmt.Target, ["expr"] = ExprJson(stmt.Expr!) };
            case "seq": return new() { ["do"] = "seq", ["steps"] = stmt.Steps.Select(StmtJson).ToList() };
            default:
                var when = new Dictionary<string, object?> { ["do"] = "when", ["cond"] = ExprJson(stmt.Cond!), ["then"] = StmtJson(stmt.Then!) };
                if (stmt.Otherwise is not null) when["else"] = StmtJson(stmt.Otherwise);
                return when;
        }
    }

    internal static string CanonicalJson(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case bool bit: return bit ? "true" : "false";
            case long integer: return integer.ToString(CultureInfo.InvariantCulture);
            case int integer: return integer.ToString(CultureInfo.InvariantCulture);
            case double real:
                if (!double.IsFinite(real)) throw new GraphIrError("non-finite number");
                return JsonSerializer.Serialize(real);
            case string text: return JsonSerializer.Serialize(text);
            case IList<object?> list:
                return "[" + string.Join(",", list.Select(CanonicalJson)) + "]";
            case IDictionary<string, object?> map:
                var keys = map.Keys.OrderBy(static item => item).ToList();
                return "{" + string.Join(",", keys.Select(key => JsonSerializer.Serialize(key) + ":" + CanonicalJson(map[key]))) + "}";
            case System.Collections.IList raw:
                return "[" + string.Join(",", raw.Cast<object?>().Select(CanonicalJson)) + "]";
            default:
                throw new GraphIrError("value is not JSON");
        }
    }

    internal static object? Freeze(object? value) => value switch
    {
        null or bool or string or long or double => value,
        int integer => (long)integer,
        IList<object?> list => list.Select(Freeze).ToList(),
        IDictionary<string, object?> map => map.ToDictionary(pair => pair.Key, pair => Freeze(pair.Value)),
        System.Collections.IList raw => raw.Cast<object?>().Select(Freeze).ToList(),
        System.Collections.IDictionary rawMap => rawMap.Keys.Cast<object>().ToDictionary(key => key.ToString()!, key => Freeze(rawMap[key])),
        _ => throw new GraphIrError("value is not JSON"),
    };

    internal static bool JsonEq(object? left, object? right)
    {
        if (left is bool || right is bool) return left is bool lb && right is bool rb && lb == rb;
        if (left is long or int || right is long or int)
        {
            if (left is not (long or int) || right is not (long or int)) return false;
            return Convert.ToInt64(left, CultureInfo.InvariantCulture) == Convert.ToInt64(right, CultureInfo.InvariantCulture);
        }
        if (left is double || right is double) return left is double ld && right is double rd && ld == rd;
        if (left is string || right is string) return left is string ls && right is string rs && ls == rs;
        if (left is null || right is null) return left is null && right is null;
        if (AsList(left) is List<object?> llist && AsList(right) is List<object?> rlist)
        {
            if (llist.Count != rlist.Count) return false;
            for (var i = 0; i < llist.Count; i++) if (!JsonEq(llist[i], rlist[i])) return false;
            return true;
        }
        if (AsDict(left) is Dictionary<string, object?> lmap && AsDict(right) is Dictionary<string, object?> rmap)
        {
            if (lmap.Count != rmap.Count) return false;
            foreach (var key in lmap.Keys)
                if (!rmap.ContainsKey(key) || !JsonEq(lmap[key], rmap[key])) return false;
            return true;
        }
        return false;
    }

    internal static List<object?>? AsList(object? value) => value switch
    {
        List<object?> list => list,
        System.Collections.IList raw => raw.Cast<object?>().ToList(),
        _ => null,
    };

    internal static Dictionary<string, object?>? AsDict(object? value) => value switch
    {
        Dictionary<string, object?> map => map,
        _ => null,
    };

    internal static bool ExpectBool(object? value) => value is bool bit ? bit : throw new GraphIrError($"expected a boolean, got {CanonicalJson(value)}");

    internal readonly record struct Num(string Kind, long Integer, double Real);

    internal static Num AsNum(object? value) => value switch
    {
        long integer => new Num("int", integer, 0),
        int integer => new Num("int", integer, 0),
        double real when double.IsFinite(real) => new Num("float", 0, real),
        double => throw new GraphIrError("non-finite number"),
        _ => throw new GraphIrError($"expected a number, got {CanonicalJson(value)}"),
    };

    internal static void CheckPolicyInit(string policy, object? init)
    {
        switch (policy)
        {
            case Policy.Sum:
            case Policy.Max:
            case Policy.Min:
                AsNum(init);
                break;
            case Policy.And:
            case Policy.Or:
                ExpectBool(init);
                break;
        }
    }

    internal static object CanonicalF64(double number)
    {
        if (!double.IsFinite(number)) throw new GraphIrError("non-finite number");
        if (number == Math.Truncate(number) && number >= long.MinValue && number < 9223372036854775808.0)
        {
            var integer = (long)number;
            if (integer == number) return integer;
        }
        return number;
    }

    internal static long AddInt(long a, long b)
    {
        try { return checked(a + b); }
        catch (OverflowException) { throw new GraphIrError("integer overflow"); }
    }

    internal static long SubInt(long a, long b)
    {
        try { return checked(a - b); }
        catch (OverflowException) { throw new GraphIrError("integer overflow"); }
    }

    internal static long MulInt(long a, long b)
    {
        try { return checked(a * b); }
        catch (OverflowException) { throw new GraphIrError("integer overflow"); }
    }

    internal static int Ordering(object? left, object? right)
    {
        var a = AsNum(left);
        var b = AsNum(right);
        if (a.Kind == "int" && b.Kind == "int") return a.Integer.CompareTo(b.Integer);
        var lf = a.Kind == "int" ? a.Integer : a.Real;
        var rf = b.Kind == "int" ? b.Integer : b.Real;
        return lf.CompareTo(rf);
    }

    internal static object NumBin(string op, object? left, object? right)
    {
        var a = AsNum(left);
        var b = AsNum(right);
        if (op != "div" && a.Kind == "int" && b.Kind == "int")
        {
            return op switch
            {
                "add" => AddInt(a.Integer, b.Integer),
                "sub" => SubInt(a.Integer, b.Integer),
                "mul" => MulInt(a.Integer, b.Integer),
                _ => throw new GraphIrError($"op `{op}` is not an integer operator"),
            };
        }
        var lf = a.Kind == "int" ? a.Integer : a.Real;
        var rf = b.Kind == "int" ? b.Integer : b.Real;
        if (op == "div" && rf == 0) throw new GraphIrError("division by zero");
        var value = op switch
        {
            "add" => lf + rf,
            "sub" => lf - rf,
            "mul" => lf * rf,
            "div" => lf / rf,
            _ => throw new GraphIrError($"op `{op}` is not a numeric operator"),
        };
        return CanonicalF64(value);
    }

    internal static object? EvalValues(string op, List<object?> values)
    {
        object? One() => values.Count == 1 ? values[0] : throw new GraphIrError($"op `{op}` has the wrong number of arguments");
        (object? Left, object? Right) Pair() => values.Count == 2 ? (values[0], values[1]) : throw new GraphIrError($"op `{op}` has the wrong number of arguments");
        switch (op)
        {
            case "neg":
                var num = AsNum(One());
                if (num.Kind == "int")
                {
                    if (num.Integer == long.MinValue) throw new GraphIrError("integer overflow");
                    return -num.Integer;
                }
                return CanonicalF64(-num.Real);
            case "not": return !ExpectBool(One());
            case "len": return LenValue(One());
            case "add":
            case "sub":
            case "mul":
            case "div":
                var (left, right) = Pair();
                return NumBin(op, left, right);
            case "mod":
                (left, right) = Pair();
                var a = AsNum(left);
                var b = AsNum(right);
                if (a.Kind != "int" || b.Kind != "int") throw new GraphIrError("mod requires integers");
                if (b.Integer == 0) throw new GraphIrError("division by zero");
                return a.Integer % b.Integer;
            case "min":
            case "max":
                (left, right) = Pair();
                var order = Ordering(left, right);
                return op == "max" ? (order >= 0 ? left : right) : (order <= 0 ? left : right);
            case "eq":
            case "ne":
                (left, right) = Pair();
                var equal = JsonEq(left, right);
                return op == "ne" ? !equal : equal;
            case "lt":
            case "le":
            case "gt":
            case "ge":
                (left, right) = Pair();
                order = Ordering(left, right);
                return op switch { "lt" => order < 0, "le" => order <= 0, "gt" => order > 0, _ => order >= 0 };
            case "concat":
                (left, right) = Pair();
                if (left is not string ls || right is not string rs)
                    throw new GraphIrError($"concat requires strings, got {CanonicalJson(left)} and {CanonicalJson(right)}");
                return ls + rs;
            case "get":
                (left, right) = Pair();
                return EvalGet(left, right);
            default:
                throw new GraphIrError($"op `{op}` is not an eager operator");
        }
    }

    static long RuneCount(string text)
    {
        long count = 0;
        for (var index = 0; index < text.Length; count++)
            index += char.IsSurrogatePair(text, index) ? 2 : 1;
        return count;
    }

    internal static long LenValue(object? value) => value switch
    {
        string text => RuneCount(text),
        System.Collections.ICollection collection => collection.Count,
        _ => throw new GraphIrError($"len requires a string, array, or object, got {CanonicalJson(value)}"),
    };

    internal static object? EvalGet(object? target, object? key)
    {
        if (AsList(target) is List<object?> list)
        {
            var index = AsNum(key);
            if (index.Kind != "int") throw new GraphIrError("array index must be an integer");
            if (index.Integer < 0) throw new GraphIrError("array index must be >= 0");
            if (index.Integer >= list.Count) throw new GraphIrError($"index {index.Integer} out of range");
            return Freeze(list[(int)index.Integer]);
        }
        if (target is IDictionary<string, object?> map)
        {
            if (key is not string name) throw new GraphIrError($"object key must be a string, got {CanonicalJson(key)}");
            if (!map.ContainsKey(name)) throw new GraphIrError($"missing key `{name}`");
            return Freeze(map[name]);
        }
        throw new GraphIrError($"get requires an array or object, got {CanonicalJson(target)}");
    }

    internal static object? FoldPolicy(string policy, object? old, object? op) => policy switch
    {
        Policy.KeepLatest => Freeze(op),
        Policy.Sum => NumBin("add", old, op),
        Policy.Max => Freeze(Ordering(old, op) >= 0 ? old : op),
        Policy.Min => Freeze(Ordering(old, op) <= 0 ? old : op),
        Policy.And => ExpectBool(old) && ExpectBool(op),
        Policy.Or => ExpectBool(old) || ExpectBool(op),
        _ => throw new GraphIrError($"unknown source policy `{policy}`"),
    };
}
