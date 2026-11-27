using System.Text.Json;

namespace Lazily;

/// <summary>
/// Host-neutral view compute document. Signals are sources, standing expressions
/// are guarded computeds, and the optional chart is one statechart cell.
/// The host owns rendering and submits semantic events.
/// </summary>
public sealed class View
{
    private readonly Context _ctx;
    private readonly string _root;
    private readonly Dictionary<string, Node> _nodes;
    private readonly Dictionary<string, Source<ViewValue>> _signals;
    private readonly Dictionary<string, Computed<ViewValue>> _exprs;
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _effects;
    private readonly StateChart? _chart;
    private ViewValue? _payload;

    private View(
        Context ctx,
        string root,
        Dictionary<string, Node> nodes,
        Dictionary<string, Source<ViewValue>> signals,
        Dictionary<string, Computed<ViewValue>> exprs,
        Dictionary<string, Dictionary<string, JsonElement>> effects,
        StateChart? chart,
        Effect subscription)
    {
        _ctx = ctx;
        _root = root;
        _nodes = nodes;
        _signals = signals;
        _exprs = exprs;
        _effects = effects;
        _chart = chart;
        Subscription = subscription;
    }

    internal Effect Subscription { get; }

    private sealed class Node(string role, List<string> children, string? flow)
    {
        public string Role { get; } = role;
        public List<string> Children { get; } = children;
        public string? Flow { get; } = flow;
    }

    internal enum Kind { Null, Bool, Int, Str }

    internal readonly record struct ViewValue(Kind K, bool B, long N, string S)
    {
        internal object? ToJson() => K switch
        {
            Kind.Bool => B,
            Kind.Int => N,
            Kind.Str => S,
            _ => null,
        };
    }

    private static readonly HashSet<string> Roles = ["text", "button", "field", "list", "region"];
    private static readonly HashSet<string> Flows = ["row", "column", "stack"];
    private static readonly HashSet<string> Aligns = ["start", "center", "end", "stretch"];
    private static readonly HashSet<string> Events = ["activate", "change", "submit"];
    private static readonly HashSet<string> ExprOps = ["lit", "ref", "call", "if", "eq", "concat", "trim"];
    private static readonly HashSet<string> EffectOps = ["set", "batch", "send"];
    private static readonly HashSet<string> Calls = ["event", "not", "add", "len"];

    /// <summary>Install a portable View document on the given reactive context.</summary>
    public static View Install(Context ctx, JsonElement doc)
    {
        var obj = Obj(doc, "view");
        Allow(obj, "view", "id", "root", "signals", "nodes", "chart");
        var id = Str(Require(obj, "id"), "view id");
        if (id.Length == 0) throw new InvalidOperationException("view id must be a non-empty string");
        var root = Str(Require(obj, "root"), "view root");
        if (root.Length == 0) throw new InvalidOperationException("view root must be a non-empty string");
        var signalsDoc = Obj(Require(obj, "signals"), "view signals");
        var initials = new Dictionary<string, ViewValue>(StringComparer.Ordinal);
        foreach (var sig in signalsDoc.EnumerateObject())
        {
            if (sig.Name.Length == 0) throw new InvalidOperationException("signal id must be a non-empty string");
            var body = Obj(sig.Value, "signal " + sig.Name);
            Allow(body, "signal " + sig.Name, "initial");
            initials[sig.Name] = Literal(Require(body, "initial"));
        }

        var hasChart = obj.TryGetProperty("chart", out var chartEl);
        ChartDef? chartDef = null;
        if (hasChart)
        {
            if (chartEl.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("chart must be a statechart document");
            chartDef = ReadChart(chartEl);
        }

        var nodesDoc = Obj(Require(obj, "nodes"), "view nodes");
        if (!nodesDoc.EnumerateObject().MoveNext()) throw new InvalidOperationException("view nodes must contain at least one node");
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        var effects = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        var exprDocs = new List<(string Key, JsonElement Expr)>();
        foreach (var entry in nodesDoc.EnumerateObject())
        {
            var nid = entry.Name;
            if (nid.Length == 0) throw new InvalidOperationException("node id must be a non-empty string");
            var node = Obj(entry.Value, "node " + nid);
            Allow(node, "node " + nid, "role", "children", "layout", "text", "bind", "hidden", "disabled", "effects");
            var role = Str(Require(node, "role"), "node " + nid + " role");
            if (!Roles.Contains(role)) throw new InvalidOperationException("unknown role `" + role + "`");
            var children = node.TryGetProperty("children", out var rawChildren) ? Children(nid, rawChildren) : [];
            string? flow = node.TryGetProperty("layout", out var rawLayout) ? Layout(nid, rawLayout) : null;
            if (node.TryGetProperty("bind", out var rawBind))
            {
                var bind = Str(rawBind, "bind");
                if (bind.Length == 0 || !initials.ContainsKey(bind)) throw new InvalidOperationException("bind names unknown signal `" + bind + "`");
            }

            if (node.TryGetProperty("text", out var rawText)) exprDocs.Add((nid + ".text", StandingText(rawText, initials.Keys)));
            if (node.TryGetProperty("hidden", out var rawHidden)) exprDocs.Add((nid + ".hidden", StandingFlag("hidden", rawHidden, initials.Keys)));
            if (node.TryGetProperty("disabled", out var rawDisabled)) exprDocs.Add((nid + ".disabled", StandingFlag("disabled", rawDisabled, initials.Keys)));
            if (node.TryGetProperty("effects", out var rawEffects))
            {
                var effectMap = Obj(rawEffects, "node " + nid + " effects");
                Allow(effectMap, "node " + nid + " effects", "activate", "change", "submit");
                var stored = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var effect in effectMap.EnumerateObject())
                {
                    ValidateEffect(effect.Value, initials.Keys, hasChart);
                    stored[effect.Name] = effect.Value.Clone();
                }

                if (stored.Count > 0) effects[nid] = stored;
            }

            nodes[nid] = new Node(role, children, flow);
        }

        if (!nodes.ContainsKey(root)) throw new InvalidOperationException("root `" + root + "` is not a node");
        CheckTree(root, nodes);
        var signals = new Dictionary<string, Source<ViewValue>>(StringComparer.Ordinal);
        foreach (var pair in initials) signals[pair.Key] = ctx.Source(pair.Value);
        foreach (var docExpr in exprDocs)
        {
            try
            {
                EvalExpr(docExpr.Expr, sid => signals[sid].Get(), null);
            }
            catch (InvalidOperationException err)
            {
                throw new InvalidOperationException(docExpr.Key + ": " + err.Message, err);
            }
        }

        var exprs = new Dictionary<string, Computed<ViewValue>>(StringComparer.Ordinal);
        foreach (var docExpr in exprDocs)
        {
            var expr = docExpr.Expr.Clone();
            exprs[docExpr.Key] = ctx.Computed<ViewValue>(cx => EvalExpr(expr, sid => cx.Get(signals[sid]), null));
        }

        StateChart? chart = chartDef is null ? null : new StateChart(ctx, chartDef);
        var watchedExprs = exprs.Values.ToArray();
        var watchedSignals = signals.Values.ToArray();
        var subscription = ctx.Effect(cx =>
        {
            if (watchedExprs.Length > 0)
            {
                foreach (var expr in watchedExprs) _ = cx.Get(expr);
            }
            else
            {
                foreach (var src in watchedSignals) _ = cx.Get(src);
            }

            return null;
        });
        return new View(ctx, root, nodes, signals, exprs, effects, chart, subscription);
    }

    /// <summary>Return a snapshot of the signal values.</summary>
    public Dictionary<string, object?> SignalsJson()
    {
        var outMap = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in _signals) outMap[pair.Key] = _ctx.Get(pair.Value).ToJson();
        return outMap;
    }

    /// <summary>Return the current standing expression values by node and field.</summary>
    public Dictionary<string, object?> ExpressionsJson()
    {
        var outMap = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in _exprs) outMap[pair.Key] = _ctx.Get(pair.Value).ToJson();
        return outMap;
    }

    /// <summary>Return the abstract node layout in preorder.</summary>
    public List<Dictionary<string, object?>> LayoutJson()
    {
        var outList = new List<Dictionary<string, object?>>();
        void Walk(string id)
        {
            var node = _nodes[id];
            outList.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["role"] = node.Role,
                ["children"] = node.Children.ToList(),
                ["flow"] = node.Flow,
            });
            foreach (var child in node.Children) Walk(child);
        }

        Walk(_root);
        return outList;
    }

    /// <summary>Return the active chart leaves, or null when the view has no chart.</summary>
    public List<string>? ChartJson() => _chart is null ? null : [.. _chart.ActiveLeaves()];

    /// <summary>Deliver a semantic event and return its ordered effect trace.</summary>
    public List<Dictionary<string, object?>> DispatchJson(string node, string evt, JsonElement value = default)
    {
        if (!Events.Contains(evt)) throw new InvalidOperationException("unknown event `" + evt + "`");
        if (!_nodes.ContainsKey(node)) throw new InvalidOperationException("unknown node `" + node + "`");
        if (!_effects.TryGetValue(node, out var byEvent) || !byEvent.TryGetValue(evt, out var effect)) return [];
        _payload = Literal(value);
        try
        {
            var trace = new List<Dictionary<string, object?>>();
            Exec(effect, trace);
            return trace;
        }
        finally
        {
            _payload = null;
        }
    }

    private void Exec(JsonElement effect, List<Dictionary<string, object?>> trace)
    {
        var obj = Obj(effect, "effect");
        var op = Str(Require(obj, "op"), "effect op");
        var args = Arr(Require(obj, "args"), "effect args");
        switch (op)
        {
            case "set":
                {
                    var sid = Str(args[0], "set signal");
                    if (!_signals.TryGetValue(sid, out var src)) throw new InvalidOperationException("unknown signal `" + sid + "`");
                    var next = EvalArg(args[1], id => _signals[id].Get(), _payload);
                    src.Set(next);
                    trace.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["op"] = "set",
                        ["signal"] = sid,
                        ["value"] = next.ToJson(),
                    });
                    break;
                }
            case "send":
                {
                    if (_chart is null) throw new InvalidOperationException("send requires a chart");
                    var name = Str(args[0], "send event");
                    var record = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["op"] = "send",
                        ["event"] = name,
                    };
                    if (args.Count == 2) record["payload"] = EvalArg(args[1], id => _signals[id].Get(), _payload).ToJson();
                    _chart.Send(name, new Dictionary<string, bool>());
                    trace.Add(record);
                    break;
                }
            case "batch":
                foreach (var nested in args) Exec(nested, trace);
                break;
            default:
                throw new InvalidOperationException("unknown effect op `" + op + "`");
        }
    }

    private static JsonElement Require(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) ? value : throw new InvalidOperationException("missing " + key);

    private static JsonElement Obj(JsonElement value, string what) =>
        value.ValueKind == JsonValueKind.Object ? value : throw new InvalidOperationException(what + " must be an object");

    private static List<JsonElement> Arr(JsonElement value, string what)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidOperationException(what + " must be an array");
        return [.. value.EnumerateArray()];
    }

    private static void Allow(JsonElement obj, string what, params string[] keys)
    {
        var allowed = new HashSet<string>(keys, StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name)) throw new InvalidOperationException(what + " has unknown field `" + prop.Name + "`");
        }
    }

    private static string Str(JsonElement value, string what) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidOperationException(what + " must be a string");

    private static ViewValue Literal(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => new ViewValue(Kind.Null, false, 0, ""),
        JsonValueKind.True => new ViewValue(Kind.Bool, true, 0, ""),
        JsonValueKind.False => new ViewValue(Kind.Bool, false, 0, ""),
        JsonValueKind.String => new ViewValue(Kind.Str, false, 0, value.GetString()!),
        JsonValueKind.Number when value.TryGetInt64(out var n) && !value.GetRawText().Contains('.') && !value.GetRawText().Contains('e') && !value.GetRawText().Contains('E')
            => new ViewValue(Kind.Int, false, n, ""),
        _ => throw new InvalidOperationException("value must be null, boolean, integer, or string"),
    };

    private static List<string> Children(string nid, JsonElement value)
    {
        var arr = Arr(value, "node " + nid + " children");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outList = new List<string>();
        foreach (var child in arr)
        {
            var text = Str(child, "node " + nid + " children");
            if (text.Length == 0) throw new InvalidOperationException("node `" + nid + "` children must be non-empty strings");
            if (!seen.Add(text)) throw new InvalidOperationException("node `" + nid + "` repeats child `" + text + "`");
            outList.Add(text);
        }

        return outList;
    }

    private static string? Layout(string nid, JsonElement value)
    {
        var obj = Obj(value, "node " + nid + " layout");
        Allow(obj, "node " + nid + " layout", "flow", "align", "grow");
        string? flow = null;
        if (obj.TryGetProperty("flow", out var rawFlow))
        {
            flow = Str(rawFlow, "flow");
            if (!Flows.Contains(flow)) throw new InvalidOperationException("unknown flow `" + flow + "`");
        }

        if (obj.TryGetProperty("align", out var rawAlign))
        {
            var align = Str(rawAlign, "align");
            if (!Aligns.Contains(align)) throw new InvalidOperationException("unknown align `" + align + "`");
        }

        if (obj.TryGetProperty("grow", out var rawGrow))
        {
            var grow = Literal(rawGrow);
            if (grow.K != Kind.Int || grow.N < 0) throw new InvalidOperationException("node `" + nid + "` layout.grow must be an integer >= 0");
        }

        return flow;
    }

    private static JsonElement StandingText(JsonElement value, IEnumerable<string> signals)
    {
        if (value.ValueKind == JsonValueKind.String) return JsonDocument.Parse("{\"op\":\"lit\",\"args\":[" + JsonSerializer.Serialize(value.GetString()) + "]}").RootElement.Clone();
        if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateExpr(value, signals, false);
            return value.Clone();
        }

        throw new InvalidOperationException("text must be a string or an expression");
    }

    private static JsonElement StandingFlag(string field, JsonElement value, IEnumerable<string> signals)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return JsonDocument.Parse("{\"op\":\"lit\",\"args\":[" + (value.GetBoolean() ? "true" : "false") + "]}").RootElement.Clone();
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateExpr(value, signals, false);
            return value.Clone();
        }

        throw new InvalidOperationException(field + " must be a boolean or an expression");
    }

    private static void ValidateEffect(JsonElement effect, IEnumerable<string> signalIds, bool hasChart)
    {
        var signals = signalIds as ISet<string> ?? new HashSet<string>(signalIds, StringComparer.Ordinal);
        var obj = Obj(effect, "effect");
        Allow(obj, "effect", "op", "args");
        var op = Str(Require(obj, "op"), "effect op");
        if (!EffectOps.Contains(op)) throw new InvalidOperationException("unknown effect op `" + op + "`");
        var args = Arr(Require(obj, "args"), "effect args");
        switch (op)
        {
            case "set":
                if (args.Count != 2) throw new InvalidOperationException("set takes a signal id and a value");
                var sid = Str(args[0], "set signal");
                if (!signals.Contains(sid)) throw new InvalidOperationException("unknown signal `" + sid + "`");
                ValidateArg(args[1], signals, true);
                break;
            case "send":
                if (!hasChart) throw new InvalidOperationException("send requires a chart");
                if (args.Count is not 1 and not 2) throw new InvalidOperationException("send takes an event name and an optional payload");
                _ = Str(args[0], "send event");
                if (args.Count == 2) ValidateArg(args[1], signals, true);
                break;
            case "batch":
                if (args.Count == 0) throw new InvalidOperationException("batch takes one or more effects");
                foreach (var nested in args) ValidateEffect(nested, signals, hasChart);
                break;
        }
    }

    private static void ValidateArg(JsonElement value, ISet<string> signals, bool inEffect)
    {
        if (value.ValueKind == JsonValueKind.Object) ValidateExpr(value, signals, inEffect);
        else _ = Literal(value);
    }

    private static void ValidateExpr(JsonElement expr, IEnumerable<string> signalIds, bool inEffect)
    {
        var signals = signalIds as ISet<string> ?? new HashSet<string>(signalIds, StringComparer.Ordinal);
        var obj = Obj(expr, "expression");
        Allow(obj, "expression", "op", "args");
        var op = Str(Require(obj, "op"), "expression op");
        if (!ExprOps.Contains(op)) throw new InvalidOperationException("unknown expression op `" + op + "`");
        var args = Arr(Require(obj, "args"), "expression args");
        switch (op)
        {
            case "lit":
                if (args.Count != 1) throw new InvalidOperationException("lit takes one literal");
                _ = Literal(args[0]);
                break;
            case "ref":
                if (args.Count != 1) throw new InvalidOperationException("ref takes one signal id");
                var sid = Str(args[0], "ref");
                if (!signals.Contains(sid)) throw new InvalidOperationException("unknown signal `" + sid + "`");
                break;
            case "call":
                ValidateCall(args, signals, inEffect);
                break;
            case "if":
                if (args.Count != 3) throw new InvalidOperationException("if takes three arguments");
                foreach (var arg in args) ValidateArg(arg, signals, inEffect);
                break;
            case "eq":
                if (args.Count != 2) throw new InvalidOperationException("eq takes two arguments");
                foreach (var arg in args) ValidateArg(arg, signals, inEffect);
                break;
            case "concat":
                foreach (var arg in args) ValidateArg(arg, signals, inEffect);
                break;
            case "trim":
                if (args.Count != 1) throw new InvalidOperationException("trim takes one value");
                ValidateArg(args[0], signals, inEffect);
                break;
        }
    }

    private static void ValidateCall(List<JsonElement> args, ISet<string> signals, bool inEffect)
    {
        if (args.Count == 0) throw new InvalidOperationException("call takes a name");
        var name = Str(args[0], "call");
        if (!Calls.Contains(name)) throw new InvalidOperationException("unknown call `" + name + "`");
        var rest = args.Skip(1).ToList();
        switch (name)
        {
            case "event":
                if (rest.Count != 0) throw new InvalidOperationException("event takes no arguments");
                if (!inEffect) throw new InvalidOperationException("event is illegal outside an effect");
                break;
            case "not":
                if (rest.Count != 1) throw new InvalidOperationException("not takes one boolean");
                break;
            case "add":
                if (rest.Count < 2) throw new InvalidOperationException("add takes two or more integers");
                break;
            case "len":
                if (rest.Count != 1) throw new InvalidOperationException("len takes one string");
                break;
        }

        if (name != "event")
        {
            foreach (var arg in rest) ValidateArg(arg, signals, inEffect);
        }
    }

    private static void CheckTree(string root, Dictionary<string, Node> nodes)
    {
        var stack = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string id)
        {
            if (!stack.Add(id)) throw new InvalidOperationException("children cycle at `" + id + "`");
            if (!done.Add(id)) throw new InvalidOperationException("node `" + id + "` has more than one parent");
            done.Remove(id);
            foreach (var child in nodes[id].Children)
            {
                if (!nodes.ContainsKey(child)) throw new InvalidOperationException("children name unknown node `" + child + "`");
                Walk(child);
            }

            stack.Remove(id);
            done.Add(id);
        }

        Walk(root);
        if (done.Count != nodes.Count) throw new InvalidOperationException("a node is not reachable from root");
    }

    private static ViewValue EvalExpr(JsonElement expr, Func<string, ViewValue> read, ViewValue? payload)
    {
        var obj = Obj(expr, "expression");
        var op = Str(Require(obj, "op"), "expression op");
        var args = Arr(Require(obj, "args"), "expression args");
        return op switch
        {
            "lit" => Literal(args[0]),
            "ref" => read(Str(args[0], "ref")),
            "call" => EvalCall(args, read, payload),
            "if" => EvalIf(args, read, payload),
            "eq" => new ViewValue(Kind.Bool, EvalArg(args[0], read, payload).Equals(EvalArg(args[1], read, payload)), 0, ""),
            "concat" => new ViewValue(Kind.Str, false, 0, string.Concat(args.Select(arg => Stringify(EvalArg(arg, read, payload))))),
            "trim" => new ViewValue(Kind.Str, false, 0, Stringify(EvalArg(args[0], read, payload)).Trim()),
            _ => throw new InvalidOperationException("unknown expression op `" + op + "`"),
        };
    }

    private static ViewValue EvalIf(List<JsonElement> args, Func<string, ViewValue> read, ViewValue? payload)
    {
        var cond = EvalArg(args[0], read, payload);
        if (cond.K != Kind.Bool) throw new InvalidOperationException("if condition must be a boolean");
        return EvalArg(cond.B ? args[1] : args[2], read, payload);
    }

    private static ViewValue EvalCall(List<JsonElement> args, Func<string, ViewValue> read, ViewValue? payload)
    {
        var name = Str(args[0], "call");
        var rest = args.Skip(1).ToList();
        switch (name)
        {
            case "event":
                return payload ?? throw new InvalidOperationException("event is illegal outside an effect");
            case "not":
                var bit = EvalArg(rest[0], read, payload);
                if (bit.K != Kind.Bool) throw new InvalidOperationException("not expects a boolean");
                return new ViewValue(Kind.Bool, !bit.B, 0, "");
            case "add":
                long sum = 0;
                foreach (var arg in rest)
                {
                    var value = EvalArg(arg, read, payload);
                    if (value.K != Kind.Int) throw new InvalidOperationException("add expects integers");
                    var next = sum + value.N;
                    if ((value.N > 0 && next < sum) || (value.N < 0 && next > sum)) throw new InvalidOperationException("add overflow");
                    sum = next;
                }

                return new ViewValue(Kind.Int, false, sum, "");
            case "len":
                var text = EvalArg(rest[0], read, payload);
                if (text.K != Kind.Str) throw new InvalidOperationException("len expects a string");
                return new ViewValue(Kind.Int, false, RuneCount(text.S), "");
            default:
                throw new InvalidOperationException("unknown call `" + name + "`");
        }
    }

    private static ViewValue EvalArg(JsonElement value, Func<string, ViewValue> read, ViewValue? payload) =>
        value.ValueKind == JsonValueKind.Object ? EvalExpr(value, read, payload) : Literal(value);

    private static int RuneCount(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i])) i++;
            count++;
        }

        return count;
    }

    private static string Stringify(ViewValue value) => value.K switch
    {
        Kind.Bool => value.B ? "true" : "false",
        Kind.Int => value.N.ToString(),
        Kind.Str => value.S,
        _ => "",
    };

    private static ChartDef ReadChart(JsonElement chart)
    {
        var topInitial = Str(Require(chart, "initial"), "chart.initial");
        var states = new List<KeyValuePair<string, StateDef>>();
        foreach (var s in Obj(Require(chart, "states"), "chart.states").EnumerateObject())
        {
            var v = s.Value;
            var on = new Dictionary<string, Transition>(StringComparer.Ordinal);
            if (v.TryGetProperty("on", out var onEl))
            {
                foreach (var t in onEl.EnumerateObject()) on[t.Name] = ReadTransition(t.Value);
            }

            states.Add(new(s.Name, new StateDef
            {
                DeclaredKind = v.TryGetProperty("kind", out var kind) ? kind.GetString() : null,
                Parent = v.TryGetProperty("parent", out var p) ? p.GetString() : null,
                Initial = v.TryGetProperty("initial", out var i) ? i.GetString() : null,
                Parallel = v.TryGetProperty("parallel", out var par) && par.ValueKind == JsonValueKind.True,
                History = v.TryGetProperty("history", out var h) ? h.GetString() : null,
                Default = v.TryGetProperty("default", out var d) ? d.GetString() : null,
                Final = v.TryGetProperty("final", out var f) && f.ValueKind == JsonValueKind.True,
                Entry = ReadActions(v, "entry"),
                Exit = ReadActions(v, "exit"),
                On = on,
            }));
        }

        if (!states.Any(s => string.Equals(s.Key, topInitial, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("chart.initial names undeclared state '" + topInitial + "'");
        }

        var roots = states.Where(s => s.Value.Parent is null).Select(s => s.Key).ToList();
        if (roots.Count != 1) throw new InvalidOperationException("chart must have one root");
        return new ChartDef(roots[0], states);
    }

    private static Transition ReadTransition(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String) return new Transition(el.GetString()!, null, [], false);
        return new Transition(
            Str(Require(el, "target"), "target"),
            el.TryGetProperty("guard", out var g) ? g.GetString() : null,
            ReadActions(el, "action"),
            el.TryGetProperty("internal", out var i) && i.ValueKind == JsonValueKind.True);
    }

    private static IReadOnlyList<string> ReadActions(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var a)) return [];
        return a.ValueKind == JsonValueKind.String ? [a.GetString()!] : [.. a.EnumerateArray().Select(x => x.GetString()!)];
    }
}
