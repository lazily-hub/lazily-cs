using System.Globalization;
using System.Text.Json;

namespace Lazily.Tests;

/// <summary>
/// The fixture interpreter for the shared <c>reactive-graph</c> conformance corpus.
/// </summary>
/// <remarks>
/// It replays a fixture's ops against an <see cref="IGraphModel"/> — one of this binding's
/// execution models — and records every assertion that diverged. Divergences are DATA, not
/// exceptions: the runner reports the full set so a single run shows every failure, and the
/// ledger test asserts the observed set equals the declared one exactly. A new divergence fails
/// the build, and a fixed one fails it until its entry is deleted. Both directions are
/// load-bearing.
/// <para>
/// The engine knows nothing about which context it is driving. That is deliberate: the op stream
/// is the specification, and a defect that is correct on one execution model and broken on
/// another only shows up when the identical stream runs on both.
/// </para>
/// </remarks>
public sealed class ReactiveGraphEngine
{
    private readonly IGraphModel _model;
    private readonly Dictionary<string, NodeRef> _nodes = [];

    // Handles are kept forever so `dispose_stale_handle` can dispose through an id that has since
    // been recycled.
    private readonly Dictionary<string, NodeRef> _stale = [];
    private readonly Dictionary<string, IScopeModel> _scopes = [];

    // A reader that still names a disposed dependency errors on its next recompute and stays
    // broken until it is itself rebuilt.
    private readonly HashSet<string> _poisoned = [];

    private readonly string _fixture;

    /// <summary>Every assertion that diverged, in step order.</summary>
    public List<string> Divergences { get; } = [];

    /// <summary>How many ops were executed.</summary>
    public int Ops { get; private set; }

    /// <summary>How many assertions were checked.</summary>
    public int Checks { get; private set; }

    private int _step;

    /// <summary>Creates an engine for one fixture (or one scenario within it).</summary>
    /// <param name="model">The execution model to replay against.</param>
    /// <param name="fixture">The fixture name, used in divergence keys.</param>
    public ReactiveGraphEngine(IGraphModel model, string fixture)
    {
        _model = model;
        _fixture = fixture;
    }

    /// <summary>Replays a step list.</summary>
    /// <param name="steps">The fixture's <c>steps</c> array.</param>
    public void Replay(JsonElement steps)
    {
        for (var i = 0; i < steps.GetArrayLength(); i++)
        {
            _step = i;
            var step = steps[i];
            var op = step.GetProperty("op");
            var runsBefore = _model.RunLog.Count;
            long? opValue = null;
            var opError = false;
            Ops++;

            // Measure this op's drain in isolation: exhaustion is a cumulative observable.
            _model.ClearDrainExhaustion();

            switch (Str(op, "type"))
            {
                case "cell":
                    Define(Factory(op).Cell(Str(op, "id")!, Num(op, "value") ?? 0));
                    break;

                case "merge_cell":
                    {
                        // The corpus only ever merges under Sum; reject anything else loudly
                        // rather than silently folding under the wrong algebra.
                        var policy = Str(op, "policy");
                        if (policy is not null && policy != "Sum")
                            throw new NotSupportedException($"{_fixture}: unsupported merge policy {policy}");
                        Define(Factory(op).MergeCell(Str(op, "id")!, Num(op, "value") ?? 0));
                        break;
                    }

                case "computed":
                    Define(Factory(op).Computed(Str(op, "id")!, ReadsOf(op), Num(op, "offset") ?? 0));
                    break;

                case "signal":
                    // Never created inside a teardown scope by the corpus, so it lives on the
                    // model rather than the shared node factory.
                    Define(_model.Signal(Str(op, "id")!, ReadsOf(op), Num(op, "offset") ?? 0));
                    break;

                case "effect":
                    DefineEffect(op);
                    break;

                case "read":
                    {
                        var r = Read(Str(op, "id")!);
                        if (r.Ok) opValue = r.Value; else opError = true;
                        break;
                    }

                case "fail_next":
                    // Arms the next N computes of an existing node to throw. It creates nothing
                    // and touches no dependency set.
                    _model.FailNext(Str(op, "id")!, (int)(Num(op, "count") ?? 1));
                    break;

                case "set_cell":
                    _model.SetCell(_nodes[Str(op, "id")!], Num(op, "value") ?? 0);
                    break;

                case "batch":
                    {
                        var writes = Items(op, "writes");
                        var mergeOps = Items(op, "merges");
                        _model.Batch(() =>
                        {
                            foreach (var w in writes) _model.SetCell(_nodes[Str(w, "id")!], Num(w, "value") ?? 0);
                            // Explicit merge() calls fold SYNCHRONOUSLY inside a batch; only
                            // propagation defers. N calls produce N folds — the caller decides how
                            // many ops exist, so the count is exact.
                            foreach (var m in mergeOps) _model.MergeInto(_nodes[Str(m, "id")!], Num(m, "value") ?? 0);
                        });
                        break;
                    }

                case "dispose":
                    _model.DisposeNode(_nodes[Str(op, "id")!]);
                    break;

                case "dispose_signal":
                    // Only the puller goes; the backing computed stays readable and reverts to lazy.
                    _model.DisposeSignal(_nodes[Str(op, "id")!]);
                    break;

                case "fanout":
                    {
                        // Subscribers are effects, not derived nodes: the corpus asserts
                        // `observed_count` on a publish, and in a lazy binding only an eager reader
                        // observes a publish without being pulled.
                        var prefix = Str(op, "id_prefix")!;
                        var reads = ReadsOf(op);
                        for (var k = 0; k < (Num(op, "count") ?? 0); k++)
                            Define(_model.Effect($"{prefix}_{k}", reads));
                        break;
                    }

                case "dispose_fanout":
                    {
                        var prefix = Str(op, "id_prefix")!;
                        for (var k = 0; k < (Num(op, "count") ?? 0); k++)
                        {
                            if (_nodes.TryGetValue($"{prefix}_{k}", out var n)) _model.DisposeNode(n);
                        }
                        break;
                    }

                case "churn":
                    Churn(op);
                    break;

                case "begin_scope":
                    _scopes[Str(op, "scope")!] = _model.Scope();
                    break;

                case "end_scope":
                    {
                        var name = Str(op, "scope")!;
                        var sc = _scopes[name];
                        _scopes.Remove(name);
                        try { sc.CloseScope(); }
                        catch (DisposedNodeException) { opError = true; }
                        break;
                    }

                case "disarm":
                    // A disarmed scope owns nothing; a later end_scope on it is a no-op.
                    _scopes[Str(op, "scope")!].Disarm();
                    break;

                case "dispose_stale_handle":
                    {
                        var of = Str(op, "handle_of")!;
                        if (!_stale.TryGetValue(of, out var h))
                            throw new InvalidOperationException($"{_fixture}: no recorded handle for {of}");
                        var want = Str(op, "handle_kind");
                        var matches = want switch
                        {
                            "cell" => h.Kind == NodeKind.Cell,
                            "slot" or "computed" => h.Kind is NodeKind.Computed or NodeKind.Signal,
                            "effect" => h.Kind == NodeKind.Effect,
                            _ => false,
                        };
                        if (!matches)
                            throw new InvalidOperationException($"{_fixture}: handle_kind {want} does not match recorded handle for {of}");
                        _model.DisposeNode(h);
                        break;
                    }

                default:
                    throw new NotSupportedException($"{_fixture}: unknown op {Str(op, "type")}");
            }

            // Async computes and effect bodies are spawned, so every observable below is
            // meaningless until the model has run them. Synchronous models are already quiescent.
            _model.Settle();

            if (!step.TryGetProperty("expect", out var expect)) continue;
            Assert(expect, op, opValue, opError, _model.RunLog.Skip(runsBefore).ToList());
        }
    }

    /// <summary>
    /// Replays the <c>expected</c> tail of a <c>scenarios</c>-shaped fixture and returns the
    /// observation, so two scenarios declared <c>observationally_equal</c> can be compared.
    /// </summary>
    /// <param name="expected">The fixture's <c>expected</c> object.</param>
    public IReadOnlyList<string> ReplayTail(FixtureAssertions expected)
    {
        _step = -1; // the expected tail is not a numbered step
        _model.Settle();
        var observation = new List<string> { $"cleanup_order={string.Join(",", _model.CleanupLog)}" };

        expected.TryAssertKeyWith(
            "final_state",
            fin =>
            {
                if (fin.TryGetProperty("dependents_of", out var deps))
                {
                    foreach (var p in deps.EnumerateObject())
                    {
                        var got = _model.DependentCount(_nodes[p.Name]);
                        Check($"final.dependents_of.{p.Name}", got, p.Value.GetInt32());
                        observation.Add($"dependents_of.{p.Name}={got}");
                    }
                }
                if (fin.TryGetProperty("readable", out var readable))
                {
                    foreach (var p in readable.EnumerateObject())
                    {
                        var alive = Readable(p.Name);
                        Check($"final.readable.{p.Name}", alive, p.Value.GetBoolean());
                        observation.Add($"readable.{p.Name}={alive}");
                    }
                }
                if (fin.TryGetProperty("read", out var reads))
                {
                    foreach (var p in reads.EnumerateObject())
                    {
                        var r = Read(p.Name);
                        Check<long?>($"final.read.{p.Name}", r.Ok ? r.Value : null, p.Value.GetInt64());
                        observation.Add($"read.{p.Name}={(r.Ok ? r.Value : null)}");
                    }
                }
            });

        expected.TryAssertKeyWith(
            "after_publish",
            publish =>
            {
                if (!publish.TryGetProperty("op", out var pop)) return;
                var before = _model.RunLog.Count;
                _model.SetCell(_nodes[Str(pop, "id")!], Num(pop, "value") ?? 0);
                _model.Settle();
                var observed = _model.RunLog.Skip(before).ToList();
                observation.Add($"after_publish.observed_by={string.Join(",", observed)}");
                if (publish.TryGetProperty("observed_by", out var wantObserved))
                    Check("after_publish.observed_by", string.Join(",", observed), string.Join(",", Strings(wantObserved)));
                if (publish.TryGetProperty("read", out var pReads))
                {
                    foreach (var p in pReads.EnumerateObject())
                    {
                        var r = Read(p.Name);
                        Check<long?>($"after_publish.read.{p.Name}", r.Ok ? r.Value : null, p.Value.GetInt64());
                        observation.Add($"after_publish.read.{p.Name}={(r.Ok ? r.Value : null)}");
                    }
                }
                if (publish.TryGetProperty("dependents_of", out var pDeps))
                {
                    foreach (var p in pDeps.EnumerateObject())
                    {
                        var got = _model.DependentCount(_nodes[p.Name]);
                        Check($"after_publish.dependents_of.{p.Name}", got, p.Value.GetInt32());
                        observation.Add($"after_publish.dependents_of.{p.Name}={got}");
                    }
                }
            });

        expected.Verify();
        return observation;
    }

    // -----------------------------------------------------------------------
    // Assertions
    // -----------------------------------------------------------------------

    private void Assert(JsonElement expect, JsonElement op, long? opValue, bool opError, List<string> observed)
    {
        // `computes_of` is evaluated BEFORE every other key, and deliberately. A step asserting
        // `computes_of` alongside `value`/`read`/`readable` is asserting a count that a read would
        // change: on a de-eagered signal the read triggers the lazy recompute, so evaluating the
        // read first would raise the count to the number a CONFORMING binding shows and make a
        // non-conforming one agree with it.
        if (expect.TryGetProperty("computes_of", out var computesOf))
        {
            foreach (var p in computesOf.EnumerateObject())
            {
                var got = _model.ComputesOf(p.Name);
                // "computes" of an effect are its runs, already recorded in the run log.
                if (got == 0 && _nodes.TryGetValue(p.Name, out var n) && n.Kind == NodeKind.Effect)
                    got = _model.RunLog.Count(x => x == p.Name);
                Check($"computes_of.{p.Name}", got, p.Value.GetInt32());
            }
        }

        foreach (var prop in expect.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "note":
                case "computes_of":
                    break;

                case "merges_of":
                    foreach (var p in prop.Value.EnumerateObject())
                    {
                        if (!_model.KnowsMerge(p.Name))
                            throw new InvalidOperationException($"{_fixture}: merges_of unknown cell {p.Name}");
                        Check($"merges_of.{p.Name}", _model.MergesOf(p.Name), p.Value.GetInt32());
                    }
                    break;

                case "drain_exhausted":
                    Check("drain_exhausted", _model.DrainExhausted, prop.Value.GetBoolean());
                    break;

                case "dependents_of":
                    foreach (var p in prop.Value.EnumerateObject())
                        Check($"dependents_of.{p.Name}", _model.DependentCount(_nodes[p.Name]), p.Value.GetInt32());
                    break;

                case "dependencies_of":
                    foreach (var p in prop.Value.EnumerateObject())
                        Check($"dependencies_of.{p.Name}", _model.DependencyCount(_nodes[p.Name]), p.Value.GetInt32());
                    break;

                case "error":
                    Check("error", opError, prop.Value.ValueKind is not JsonValueKind.Null);
                    break;

                case "value":
                    {
                        if (expect.TryGetProperty("error", out var e) && e.ValueKind is not JsonValueKind.Null) break;
                        // A feed effect's op id is the effect (unreadable), so its `value` assertion
                        // targets the merge cell it feeds. Otherwise (signal creation) the op id is
                        // itself the readable node. The read is issued AFTER `computes_of` has been
                        // evaluated, so it cannot mask a deferred materialization.
                        var target = Str(op, "merges_into") ?? Str(op, "id");
                        long? got = opValue;
                        if (got is null && target is not null)
                        {
                            var r = Read(target);
                            got = r.Ok ? r.Value : null;
                        }
                        Check<long?>("value", got, prop.Value.GetInt64());
                        break;
                    }

                case "read":
                    foreach (var p in prop.Value.EnumerateObject())
                    {
                        var r = Read(p.Name);
                        Check<long?>($"read.{p.Name}", r.Ok ? r.Value : null, p.Value.GetInt64());
                    }
                    break;

                case "readable":
                    foreach (var p in prop.Value.EnumerateObject())
                        Check($"readable.{p.Name}", Readable(p.Name), p.Value.GetBoolean());
                    break;

                case "observed_by":
                    Check("observed_by", string.Join(",", observed), string.Join(",", Strings(prop.Value)));
                    break;

                case "observed_count":
                    Check("observed_count", observed.Count, prop.Value.GetInt32());
                    break;

                case "cleanup_order":
                    {
                        // Only effects run a cleanup callback, so the expected order is projected onto
                        // its effect entries. `cleanup_order` is cumulative, not per-step.
                        var want = Strings(prop.Value)
                            .Where(id => _stale.TryGetValue(id, out var h) && h.Kind == NodeKind.Effect);
                        Check("cleanup_order", string.Join(",", _model.CleanupLog), string.Join(",", want));
                        break;
                    }

                case "scope_owned_count":
                    foreach (var p in prop.Value.EnumerateObject())
                        Check($"scope_owned_count.{p.Name}", _scopes[p.Name].Owned, p.Value.GetInt32());
                    break;

                default:
                    throw new NotSupportedException($"{_fixture}: unknown expectation {prop.Name}");
            }
        }
    }

    private void Check<T>(string key, T got, T want)
    {
        Checks++;
        if (EqualityComparer<T>.Default.Equals(got, want)) return;
        Divergences.Add(string.Create(
            CultureInfo.InvariantCulture, $"{_fixture}#{_step}:{key} — got {got}, want {want}"));
    }

    // -----------------------------------------------------------------------
    // Node construction
    // -----------------------------------------------------------------------

    private void Define(NodeRef n)
    {
        _nodes[n.Id] = n;
        _stale[n.Id] = n;
        _poisoned.Remove(n.Id);
    }

    /// <summary>
    /// The construction surface for an op: the named teardown scope when the op declares one,
    /// otherwise the model itself. One code path covers both.
    /// </summary>
    private INodeFactory Factory(JsonElement op)
    {
        var scope = Str(op, "scope");
        return scope is null ? _model : _scopes[scope];
    }

    private void DefineEffect(JsonElement op)
    {
        var id = Str(op, "id")!;
        var mergesInto = Str(op, "merges_into");
        var writesOwnCone = Str(op, "writes_own_cone");

        // A feed effect reads upstream (tracked) and folds the sum into a merge cell through the
        // UNTRACKED write surface; the divergent effect reads and writes the same cell, closing a
        // feedback loop through the SCHEDULER rather than through the graph.
        var node = mergesInto is not null
            ? _model.FeedEffect(id, ReadsOf(op), _nodes[mergesInto])
            : writesOwnCone is not null
                ? _model.SelfWritingEffect(id, _nodes[writesOwnCone])
                : Factory(op).Effect(id, ReadsOf(op));
        Define(node);
    }

    private void Churn(JsonElement op)
    {
        var source = _nodes[Str(op, "source")!];
        var prefix = Str(op, "id_prefix")!;
        var width = Num(op, "live_width") ?? 1;
        var cycles = Num(op, "cycles") ?? 0;
        switch (Str(op, "mode"))
        {
            // Hold `live_width` subscribers; each cycle disposes one and creates its replacement,
            // so the live count is invariant.
            case "dispose_then_create":
                for (var c = 0L; c < cycles; c++)
                {
                    var id = $"{prefix}_{c % width}";
                    if (_nodes.TryGetValue(id, out var existing)) _model.DisposeNode(existing);
                    Define(_model.Effect(id, [source]));
                }
                break;

            // One teardown scope per cycle; its subscriber is gone by the end of its own cycle.
            case "scope_per_cycle":
                {
                    var name = $"{prefix}_scoped";
                    for (var c = 0L; c < cycles; c++)
                    {
                        var sc = _model.Scope();
                        sc.Effect(name, [source]);
                        sc.CloseScope();
                    }
                    break;
                }

            default:
                throw new NotSupportedException($"{_fixture}: unknown churn mode {Str(op, "mode")}");
        }
    }

    // -----------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------

    private bool Readable(string id)
    {
        if (!_nodes.TryGetValue(id, out var n)) return false;
        // A signal is readable iff its backing computed is: disposing the puller leaves the value
        // live, so this must NOT consult the puller.
        return n.Kind == NodeKind.Effect ? _model.IsEffectActive(n) : Read(id).Ok;
    }

    private (bool Ok, long Value) Read(string id)
    {
        if (_poisoned.Contains(id)) return (false, 0);
        if (!_nodes.TryGetValue(id, out var n))
            throw new InvalidOperationException($"{_fixture}: read of unknown node {id}");
        try
        {
            var r = _model.Read(n);
            if (!r.Ok) _poisoned.Add(id);
            return r;
        }
        catch (ComputeFailedException)
        {
            // A failed read that must NOT latch: disposal is permanent by contract, a `fail_next`
            // compute failure is recoverable by contract — the next read re-runs the body.
            // Latching would make the engine report the very defect
            // `failed_compute_is_never_cached.json` exists to catch.
            return (false, 0);
        }
    }

    private IReadOnlyList<NodeRef> ReadsOf(JsonElement op)
    {
        if (!op.TryGetProperty("reads", out var reads) || reads.ValueKind is not JsonValueKind.Array) return [];
        var list = new List<NodeRef>(reads.GetArrayLength());
        foreach (var r in reads.EnumerateArray())
        {
            var id = r.GetString()!;
            if (!_nodes.TryGetValue(id, out var n))
                throw new InvalidOperationException($"{_fixture}: op reads unknown node {id}");
            list.Add(n);
        }
        return list;
    }

    // -----------------------------------------------------------------------
    // JSON helpers
    // -----------------------------------------------------------------------

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;

    private static long? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetInt64() : null;

    private static IReadOnlyList<JsonElement> Items(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Array
            ? [.. v.EnumerateArray()]
            : [];

    private static IReadOnlyList<string> Strings(JsonElement e) =>
        e.ValueKind is JsonValueKind.Array ? [.. e.EnumerateArray().Select(x => x.GetString()!)] : [];
}
