using Xunit;

namespace Lazily.Tests;

public class GraphIrTests
{
    const string Counter = """
    {
      "kind": "graph", "ir": "1", "id": "counter",
      "nodes": [
        {"id": "count", "kind": "source", "init": 1},
        {"id": "doubled", "kind": "computed", "eager": true,
          "expr": {"op": "mul", "args": [{"ref": "count"}, {"lit": 2}]}},
        {"id": "log", "kind": "effect", "body": {"do": "eval", "expr": {"ref": "doubled"}}}
      ]
    }
    """;

    const string CounterEdit = """
    {
      "kind": "edit", "ir": "1",
      "ops": [
        {"op": "replace", "node": {"id": "doubled", "kind": "computed",
          "expr": {"op": "add", "args": [{"ref": "count"}, {"lit": 10}]}}},
        {"op": "add", "node": {"id": "label", "kind": "source", "init": "n"}},
        {"op": "add", "node": {"id": "shown", "kind": "computed",
          "expr": {"op": "concat", "args": [{"ref": "label"}, {"lit": ":"}]}}},
        {"op": "remove", "id": "log"}
      ]
    }
    """;

    const string CounterSource =
        "graph counter ir 1\n" +
        "source count = 1\n" +
        "computed doubled eager = mul(ref(count), lit(2))\n" +
        "effect log = eval(ref(doubled))\n";

    static List<Edge> Reads(IEnumerable<Edge> edges) => edges.Select(edge => new Edge(edge.From, EdgeKind.Read, edge.To)).ToList();

    static void Fails(string fragment, Action body)
    {
        var err = Assert.Throws<GraphIrError>(body);
        Assert.Contains(fragment, err.Message);
    }

    [Fact]
    public void CounterLowersThenAnEditReplacesTheGraph()
    {
        var doc = GraphIr.FromJsonStr(Counter);
        Assert.Equal("counter", doc.Id);
        var doubled = doc.Node("doubled");
        Assert.NotNull(doubled);
        Assert.True(doubled!.Eager);
        Assert.Equal("mul", doubled.Expr!.Op);
        Assert.Equal(
            new[] { new Edge("doubled", EdgeKind.Read, "count"), new Edge("log", EdgeKind.Read, "doubled") },
            Reads(doc.StaticEdges()));
        Assert.Equal(CounterSource, doc.EmitSource());
        Assert.Equal(CounterSource, GraphIr.FromJsonStr(doc.ToJsonText()).EmitSource());

        var rt = GraphRuntime.Lower(doc);
        Assert.Equal(2L, rt.Get("doubled"));
        Assert.Equal("log", Assert.Single(rt.EffectTrace()).Id);
        Assert.Equal(2L, rt.EffectTrace()[0].Result);
        Assert.Equal(new[] { "count" }, rt.ObservedReads("doubled").Order());
        Assert.Equal(new[] { "doubled" }, rt.ObservedReads("log").Order());
        Assert.True(rt.IsEager("doubled"));
        Assert.Equal(Policy.KeepLatest, rt.SourcePolicy("count"));

        var before = rt.EmitSource();
        rt.WriteSource("count", 4L);
        Assert.Equal(8L, rt.Get("doubled"));
        Assert.Equal(2, rt.EffectTrace().Count);
        Assert.Equal(8L, rt.EffectTrace()[1].Result);
        Assert.Equal(before, rt.EmitSource());

        rt.Apply(GraphEdit.FromJsonStr(CounterEdit));
        Assert.Equal(14L, rt.Get("doubled"));
        Assert.Equal("n:", rt.Get("shown"));
        Assert.False(rt.Contains("log"));
        Assert.False(rt.IsEager("doubled"));
        var traceLen = rt.EffectTrace().Count;
        rt.WriteSource("count", 5L);
        Assert.Equal(15L, rt.Get("doubled"));
        Assert.Equal(traceLen, rt.EffectTrace().Count);
        Assert.Equal(
            "graph counter ir 1\n" +
            "source count = 1\n" +
            "computed doubled = add(ref(count), lit(10))\n" +
            "source label = \"n\"\n" +
            "computed shown = concat(ref(label), lit(\":\"))\n",
            rt.EmitSource());
    }

    [Fact]
    public void DynamicIfObservesOnlyTheTakenBranch()
    {
        var rt = GraphRuntime.LoadJson("""
        {"kind":"graph","ir":"1","nodes":[
          {"id":"flag","kind":"source","init":false},
          {"id":"left","kind":"source","init":"L"},
          {"id":"right","kind":"source","init":"R"},
          {"id":"picked","kind":"computed","expr":{"op":"if","args":[{"ref":"flag"},{"ref":"left"},{"ref":"right"}]}}
        ]}
        """);
        Assert.Equal(
            new[]
            {
                new Edge("picked", EdgeKind.Read, "flag"),
                new Edge("picked", EdgeKind.Read, "left"),
                new Edge("picked", EdgeKind.Read, "right"),
            },
            Reads(rt.StaticEdges()));
        Assert.Equal("R", rt.Get("picked"));
        Assert.Equal(new[] { "flag", "right" }, rt.ObservedReads("picked").Order());
        rt.WriteSource("flag", true);
        Assert.Equal("L", rt.Get("picked"));
        Assert.Equal(new[] { "flag", "left" }, rt.ObservedReads("picked").Order());
    }

    [Fact]
    public void SumPolicyFoldsAndSkipsEqualWrite()
    {
        var rt = GraphRuntime.LoadJson("""
        {"kind":"graph","ir":"1","nodes":[
          {"id":"tick","kind":"source","init":0},
          {"id":"total","kind":"source","init":0,"policy":"sum"},
          {"id":"bump","kind":"effect","body":{"do":"seq","steps":[
            {"do":"eval","expr":{"ref":"tick"}},
            {"do":"merge","target":"total","expr":{"lit":1}}
          ]}}
        ]}
        """);
        Assert.Contains("policy sum", rt.EmitSource());
        Assert.Contains("merge(total, lit(1))", rt.EmitSource());
        Assert.Equal(1L, rt.Get("total"));
        Assert.Equal("bump", rt.EffectTrace()[0].Id);
        Assert.Equal(new object?[] { 0L, 1L }, Assert.IsAssignableFrom<IList<object?>>(rt.EffectTrace()[0].Result));
        Assert.Equal(Policy.Sum, rt.SourcePolicy("total"));
        rt.WriteSource("tick", 1L);
        Assert.Equal(2L, rt.Get("total"));
        Assert.Equal(2, rt.EffectTrace().Count);
        rt.WriteSource("tick", 1L);
        Assert.Equal(2, rt.EffectTrace().Count);
        Assert.Equal(2L, rt.Get("total"));
    }

    [Fact]
    public void StructuresReadFieldsLengthAndObjects()
    {
        var rt = GraphRuntime.LoadJson("""
        {"kind":"graph","ir":"1","nodes":[
          {"id":"row","kind":"source","init":{"name":"ada","n":2}},
          {"id":"name","kind":"computed","expr":{"op":"get","args":[{"ref":"row"},{"lit":"name"}]}},
          {"id":"n","kind":"computed","expr":{"op":"get","args":[{"ref":"row"},{"lit":"n"}]}},
          {"id":"tag","kind":"computed","expr":{"op":"concat","args":[{"ref":"name"},{"lit":"#"}]}},
          {"id":"width","kind":"computed","expr":{"op":"len","args":[{"ref":"name"}]}},
          {"id":"pair","kind":"computed","expr":{"op":"array","args":[{"ref":"name"},{"ref":"n"}]}},
          {"id":"wrapped","kind":"computed","expr":{"object":[{"key":"name","value":{"ref":"name"}}]}}
        ]}
        """);
        Assert.Equal("ada", rt.Get("name"));
        Assert.Equal(2L, rt.Get("n"));
        Assert.Equal("ada#", rt.Get("tag"));
        Assert.Equal(3L, rt.Get("width"));
        Assert.Equal(new object?[] { "ada", 2L }, Assert.IsAssignableFrom<IList<object?>>(rt.Get("pair")));
        var wrapped = Assert.IsAssignableFrom<IDictionary<string, object?>>(rt.Get("wrapped"));
        Assert.Equal("ada", wrapped["name"]);
        Assert.Single(wrapped);
    }

    [Fact]
    public void CycleAndSnapshotAreRejected()
    {
        Fails("cycle in read edges", () => GraphIr.FromJsonStr(
            """{"kind":"graph","ir":"1","nodes":[{"id":"a","kind":"computed","expr":{"ref":"b"}},{"id":"b","kind":"computed","expr":{"ref":"a"}}]}"""));
        Fails("graph", () => GraphIr.FromJsonStr("""{"Snapshot":{"epoch":0,"nodes":[],"edges":[],"roots":[]}}"""));
    }

    [Fact]
    public void NullInitAndOrderedBooleanPolicies()
    {
        var rt = GraphRuntime.LoadJson("""
        {"kind":"graph","ir":"1","nodes":[
          {"id":"empty","kind":"source","init":null},
          {"id":"hi","kind":"source","init":3,"policy":"max"},
          {"id":"lo","kind":"source","init":3,"policy":"min"},
          {"id":"both","kind":"source","init":true,"policy":"and"},
          {"id":"either","kind":"source","init":false,"policy":"or"},
          {"id":"fold","kind":"effect","body":{"do":"seq","steps":[
            {"do":"merge","target":"hi","expr":{"lit":1}},
            {"do":"merge","target":"hi","expr":{"lit":5}},
            {"do":"merge","target":"hi","expr":{"lit":5}},
            {"do":"merge","target":"lo","expr":{"lit":1}},
            {"do":"merge","target":"both","expr":{"lit":false}},
            {"do":"merge","target":"either","expr":{"lit":true}}
          ]}}
        ]}
        """);
        Assert.Null(rt.Get("empty"));
        Assert.Equal(5L, rt.Get("hi"));
        Assert.Equal(1L, rt.Get("lo"));
        Assert.Equal(false, rt.Get("both"));
        Assert.Equal(true, rt.Get("either"));
        Assert.Equal(Policy.Max, rt.SourcePolicy("hi"));
        Assert.Equal(Policy.Min, rt.SourcePolicy("lo"));
        Assert.Equal(Policy.And, rt.SourcePolicy("both"));
        Assert.Equal(Policy.Or, rt.SourcePolicy("either"));
    }

    [Fact]
    public void AndShortCircuitAndDivByZero()
    {
        var rt = GraphRuntime.LoadJson("""
        {"kind":"graph","ir":"1","nodes":[
          {"id":"flag","kind":"source","init":false},
          {"id":"n","kind":"source","init":1},
          {"id":"bad","kind":"computed","expr":{"op":"div","args":[{"ref":"n"},{"lit":0}]}},
          {"id":"gate","kind":"computed","expr":{"op":"and","args":[{"ref":"flag"},{"ref":"bad"}]}}
        ]}
        """);
        Assert.Equal(false, rt.Get("gate"));
        Assert.Equal(new[] { "flag" }, rt.ObservedReads("gate").Order());
        Fails("division by zero", () => rt.Get("bad"));
    }

    [Fact]
    public void FailedEditsLeaveThePreviousGraph()
    {
        var rt = GraphRuntime.LoadJson(
            """{"kind":"graph","ir":"1","nodes":[{"id":"a","kind":"source","init":1},{"id":"b","kind":"computed","expr":{"ref":"a"}}]}""");
        Fails("edit op 0", () => rt.Apply(GraphEdit.FromJsonStr("""{"kind":"edit","ir":"1","ops":[{"op":"remove","id":"a"}]}""")));
        Assert.True(rt.Contains("a") && rt.Contains("b"));
        Assert.Equal(1L, rt.Get("b"));

        var empty = GraphRuntime.LoadJson("""{"kind":"graph","ir":"1","nodes":[]}""");
        Fails("edit op 0", () => empty.Apply(GraphEdit.FromJsonStr(
            """{"kind":"edit","ir":"1","ops":[{"op":"add","node":{"id":"a","kind":"computed","expr":{"ref":"b"}}},{"op":"add","node":{"id":"b","kind":"source","init":1}}]}""")));
        Assert.False(empty.Contains("a") || empty.Contains("b"));
        Assert.Equal("graph ir 1\n", empty.EmitSource());
    }

    [Fact]
    public void ReplaceSourceResetsLiveValueAndWhenRecordsNull()
    {
        var rt = GraphRuntime.LoadJson("""{"kind":"graph","ir":"1","id":"g","nodes":[{"id":"count","kind":"source","init":1}]}""");
        rt.WriteSource("count", 9L);
        Assert.Equal(9L, rt.Get("count"));
        Assert.Equal("graph g ir 1\nsource count = 1\n", rt.EmitSource());
        rt.Apply(GraphEdit.FromJsonStr("""{"kind":"edit","ir":"1","ops":[{"op":"replace","node":{"id":"count","kind":"source","init":0}}]}"""));
        Assert.Equal(0L, rt.Get("count"));
        Assert.Equal("graph g ir 1\nsource count = 0\n", rt.EmitSource());

        var on = GraphRuntime.LoadJson("""{"kind":"graph","ir":"1","nodes":[{"id":"on","kind":"source","init":true},{"id":"value","kind":"source","init":0},{"id":"apply","kind":"effect","body":{"do":"when","cond":{"ref":"on"},"then":{"do":"set","target":"value","expr":{"lit":7}}}}]}""");
        Assert.Equal(7L, on.Get("value"));
        Assert.Equal(7L, on.EffectTrace()[0].Result);
        var off = GraphRuntime.LoadJson("""{"kind":"graph","ir":"1","nodes":[{"id":"on","kind":"source","init":false},{"id":"value","kind":"source","init":0},{"id":"apply","kind":"effect","body":{"do":"when","cond":{"ref":"on"},"then":{"do":"set","target":"value","expr":{"lit":7}}}}]}""");
        Assert.Equal(0L, off.Get("value"));
        Assert.Null(off.EffectTrace()[0].Result);
    }

    [Fact]
    public void EmptyGraphAcceptsAddAndRejectionsFailClosed()
    {
        var rt = GraphRuntime.LoadJson("""{"kind":"graph","ir":"1","nodes":[]}""");
        rt.Apply(GraphEdit.FromJsonStr("""{"kind":"edit","ir":"1","ops":[{"op":"add","node":{"id":"x","kind":"source","init":1}}]}"""));
        Assert.Equal(1L, rt.Get("x"));
        Assert.Equal("graph ir 1\nsource x = 1\n", rt.EmitSource());
        foreach (var text in new[]
        {
            """{"kind":"graph","ir":"2","nodes":[]}""",
            """{"kind":"graph","ir":"1","nodes":[{"id":"1count","kind":"source","init":0}]}""",
            """{"kind":"graph","ir":"1","nodes":[{"id":"c","kind":"computed","expr":{"op":"pow","args":[{"lit":2},{"lit":3}]}}]}""",
            """{"kind":"graph","ir":"1","nodes":[{"id":"e","kind":"effect","body":"log"}]}""",
        })
        {
            Assert.Throws<GraphIrError>(() => GraphIr.FromJsonStr(text));
        }
        Assert.Throws<GraphIrError>(() => GraphEdit.FromJsonStr("""{"kind":"edit","ir":"1","ops":[]}"""));
    }

    [Fact]
    public void FeedbackStopsAtTheDrainBudget()
    {
        var doc = GraphIr.FromJsonStr("""{"kind":"graph","ir":"1","nodes":[{"id":"n","kind":"source","init":0},{"id":"loop","kind":"effect","body":{"do":"set","target":"n","expr":{"op":"add","args":[{"ref":"n"},{"lit":1}]}}}]}""");
        var rt = GraphRuntime.Lower(doc, 4);
        Assert.Equal(4, rt.LastDrainExhaustion);
        Assert.Equal(4L, rt.Get("n"));
    }
}
