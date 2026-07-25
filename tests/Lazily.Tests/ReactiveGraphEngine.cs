using System.Globalization;
using System.Text.Json;

namespace Lazily.Tests;

/// <summary>
/// The fixture interpreter for the shared <c>reactive-graph</c> conformance corpus.
/// </summary>
/// <remarks>
/// It replays a fixture's ops against a real <see cref="Context"/> and records every assertion
/// that diverged. Divergences are DATA, not exceptions: the runner reports the full set so a
/// single run shows every failure, and the ledger test asserts the observed set equals the
/// declared one exactly — a new divergence fails the build, and a fixed one fails it until its
/// entry is deleted. Both directions are load-bearing.
/// </remarks>
public sealed class ReactiveGraphEngine
{
    private enum Kind { Source, Computed, Effect }

    private sealed record NodeRef(Kind Kind, ReactiveNode Handle);

    private readonly Context _ctx = new();
    private readonly Dictionary<string, NodeRef> _nodes = [];

    // Handles are kept forever so `dispose_stale_handle` can dispose through an id that has since
    // been recycled.
    private readonly Dictionary<string, NodeRef> _stale = [];
    private readonly Dictionary<string, TeardownScope> _scopes = [];

    // Cumulative per-node counters, never reset: `computes_of` / `merges_of` are running totals
    // from scenario start, so a fixture can assert that a step did NOT compute by repeating the
    // previous step's number.
    private readonly Dictionary<string, int> _computes = [];
    private readonly Dictionary<string, int> _merges = [];

    private readonly List<string> _runLog = [];
    private readonly List<string> _cleanupLog = [];

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
    /// <param name="fixture">The fixture name, used in divergence keys.</param>
    public ReactiveGraphEngine(string fixture) => _fixture = fixture;

    /// <summary>Replays a step list.</summary>
    /// <param name="steps">The fixture's <c>steps</c> array.</param>
    public void Replay(JsonElement steps)
    {
        for (var i = 0; i < steps.GetArrayLength(); i++)
        {
            _step = i;
            var step = steps[i];
            var op = step.GetProperty("op");
            var runsBefore = _runLog.Count;
            long? opValue = null;
            var opError = false;
            Ops++;

            // Measure this op's drain in isolation: exhaustion is a cumulative observable.
            _ctx.ClearDrainExhaustion();

            switch (Str(op, "type"))
            {
                case "cell":
                    Define(Str(op, "id")!, new NodeRef(Kind.Source, Own(op, NewSource(Num(op, "value") ?? 0))));
                    break;

                case "merge_cell":
                    {
                        // A merge cell is an ordinary source whose write folds under a policy. The
                        // corpus only ever merges under Sum; reject anything else loudly rather than
                        // silently folding under the wrong algebra.
                        var policy = Str(op, "policy");
                        if (policy is not null && policy != "Sum")
                            throw new NotSupportedException($"{_fixture}: unsupported merge policy {policy}");
                        var id = Str(op, "id")!;
                        _merges.TryAdd(id, 0);
                        // `merges_of` counts folds the LIBRARY performed, not calls the runner made:
                        // the counter lives inside the policy's fold, so a binding that deferred or
                        // dropped the fold reports a lower count even though the call was issued. That
                        // is exactly the discrimination the merge fixtures are written to make.
                        var counted = MergePolicy.Sum<long>() with
                        {
                            Merge = (a, b) => { _merges[id] = _merges[id] + 1; return a + b; },
                        };
                        var src = _ctx.Source(Num(op, "value") ?? 0, counted);
                        Define(id, new NodeRef(Kind.Source, Own(op, src)));
                        break;
                    }

                case "computed":
                    DefineComputed(op, eager: false);
                    break;

                case "signal":
                    DefineComputed(op, eager: true);
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

                case "set_cell":
                    SourceOf(Str(op, "id")!).Set(Num(op, "value") ?? 0);
                    break;

                case "batch":
                    {
                        var writes = Items(op, "writes");
                        var mergeOps = Items(op, "merges");
                        _ctx.Batch(() =>
                        {
                            foreach (var w in writes) SourceOf(Str(w, "id")!).Set(Num(w, "value") ?? 0);
                            // Explicit merge() calls fold SYNCHRONOUSLY inside a batch; only
                            // propagation defers. N calls produce N folds — the caller decides how
                            // many ops exist, so the count is exact.
                            foreach (var m in mergeOps) SourceOf(Str(m, "id")!).Merge(Num(m, "value") ?? 0);
                        });
                        break;
                    }

                case "dispose":
                    DisposeRef(_nodes[Str(op, "id")!]);
                    break;

                case "dispose_signal":
                    // Only the puller goes; the backing computed stays readable and reverts to lazy.
                    ((Computed<long>)_nodes[Str(op, "id")!].Handle).Lazy();
                    break;

                case "fanout":
                    {
                        // Subscribers are effects, not derived nodes: the corpus asserts
                        // `observed_count` on a publish, and in a lazy binding only an eager reader
                        // observes a publish without being pulled.
                        var prefix = Str(op, "id_prefix")!;
                        var reads = ReadsOf(op);
                        for (var k = 0; k < (Num(op, "count") ?? 0); k++)
                        {
                            var id = $"{prefix}_{k}";
                            Define(id, new NodeRef(Kind.Effect, PlainEffect(id, reads)));
                        }
                        break;
                    }

                case "dispose_fanout":
                    {
                        var prefix = Str(op, "id_prefix")!;
                        for (var k = 0; k < (Num(op, "count") ?? 0); k++)
                        {
                            if (_nodes.TryGetValue($"{prefix}_{k}", out var n)) DisposeRef(n);
                        }
                        break;
                    }

                case "churn":
                    Churn(op);
                    break;

                case "begin_scope":
                    _scopes[Str(op, "scope")!] = _ctx.Scope();
                    break;

                case "end_scope":
                    {
                        var name = Str(op, "scope")!;
                        var sc = _scopes[name];
                        _scopes.Remove(name);
                        try { sc.Close(); }
                        catch (DisposedNodeException) { opError = true; }
                        break;
                    }

                case "disarm":
                    {
                        var name = Str(op, "scope")!;
                        _scopes[name].Disarm();
                        // A disarmed scope owns nothing; a later end_scope on it is a no-op.
                        break;
                    }

                case "dispose_stale_handle":
                    {
                        var of = Str(op, "handle_of")!;
                        if (!_stale.TryGetValue(of, out var h))
                            throw new InvalidOperationException($"{_fixture}: no recorded handle for {of}");
                        var want = Str(op, "handle_kind");
                        var matches = want switch
                        {
                            "cell" => h.Kind == Kind.Source,
                            "slot" or "computed" => h.Kind == Kind.Computed,
                            "effect" => h.Kind == Kind.Effect,
                            _ => false,
                        };
                        if (!matches)
                            throw new InvalidOperationException($"{_fixture}: handle_kind {want} does not match recorded handle for {of}");
                        DisposeRef(h);
                        break;
                    }

                default:
                    throw new NotSupportedException($"{_fixture}: unknown op {Str(op, "type")}");
            }

            if (!step.TryGetProperty("expect", out var expect)) continue;
            Assert(expect, op, opValue, opError, _runLog.Skip(runsBefore).ToList());
        }
    }

    /// <summary>
    /// Replays the <c>expected</c> tail of a <c>scenarios</c>-shaped fixture and returns the
    /// observation, so two scenarios declared <c>observationally_equal</c> can be compared.
    /// </summary>
    /// <param name="expected">The fixture's <c>expected</c> object.</param>
    public IReadOnlyList<string> ReplayTail(JsonElement expected)
    {
        _step = -1; // the expected tail is not a numbered step
        var observation = new List<string> { $"cleanup_order={string.Join(",", _cleanupLog)}" };

        if (expected.TryGetProperty("final_state", out var fin))
        {
            if (fin.TryGetProperty("dependents_of", out var deps))
            {
                foreach (var p in deps.EnumerateObject())
                {
                    var got = _ctx.DependentCount(_nodes[p.Name].Handle);
                    Check($"final.dependents_of.{p.Name}", got, p.Value.GetInt32());
                    observation.Add($"dependents_of.{p.Name}={got}");
                }
            }
            if (fin.TryGetProperty("readable", out var readable))
            {
                foreach (var p in readable.EnumerateObject())
                {
                    var alive = !_nodes.TryGetValue(p.Name, out var n) ? false
                        : n.Kind == Kind.Effect ? ((Effect)n.Handle).IsActive
                        : Read(p.Name).Ok;
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
        }

        if (expected.TryGetProperty("after_publish", out var publish) &&
            publish.TryGetProperty("op", out var pop))
        {
            var before = _runLog.Count;
            SourceOf(Str(pop, "id")!).Set(Num(pop, "value") ?? 0);
            var observed = _runLog.Skip(before).ToList();
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
                    var got = _ctx.DependentCount(_nodes[p.Name].Handle);
                    Check($"after_publish.dependents_of.{p.Name}", got, p.Value.GetInt32());
                    observation.Add($"after_publish.dependents_of.{p.Name}={got}");
                }
            }
        }

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
                var got = _computes.TryGetValue(p.Name, out var c)
                    ? c
                    // "computes" of an effect are its runs, already recorded in the run log.
                    : _runLog.Count(n => n == p.Name);
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
                        if (!_merges.TryGetValue(p.Name, out var got))
                            throw new InvalidOperationException($"{_fixture}: merges_of unknown cell {p.Name}");
                        Check($"merges_of.{p.Name}", got, p.Value.GetInt32());
                    }
                    break;

                case "drain_exhausted":
                    Check("drain_exhausted", _ctx.LastDrainExhaustion is not null, prop.Value.GetBoolean());
                    break;

                case "dependents_of":
                    foreach (var p in prop.Value.EnumerateObject())
                        Check($"dependents_of.{p.Name}", _ctx.DependentCount(_nodes[p.Name].Handle), p.Value.GetInt32());
                    break;

                case "dependencies_of":
                    foreach (var p in prop.Value.EnumerateObject())
                        Check($"dependencies_of.{p.Name}", _ctx.DependencyCount(_nodes[p.Name].Handle), p.Value.GetInt32());
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
                    {
                        bool alive;
                        if (!_nodes.TryGetValue(p.Name, out var n)) alive = false;
                        // A signal is readable iff its backing computed is: disposing the puller
                        // leaves the value live, so this must NOT consult the puller.
                        else if (n.Kind == Kind.Effect) alive = ((Effect)n.Handle).IsActive;
                        else alive = Read(p.Name).Ok;
                        Check($"readable.{p.Name}", alive, p.Value.GetBoolean());
                    }
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
                            .Where(id => _stale.TryGetValue(id, out var h) && h.Kind == Kind.Effect);
                        Check("cleanup_order", string.Join(",", _cleanupLog), string.Join(",", want));
                        break;
                    }

                case "scope_owned_count":
                    foreach (var p in prop.Value.EnumerateObject())
                        Check($"scope_owned_count.{p.Name}", _scopes[p.Name].Count, p.Value.GetInt32());
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

    private void Define(string id, NodeRef n)
    {
        _nodes[id] = n;
        _stale[id] = n;
        _poisoned.Remove(id);
    }

    private Source<long> NewSource(long v) => _ctx.Source(v);

    private TNode Own<TNode>(JsonElement op, TNode node) where TNode : ReactiveNode
    {
        var scope = Str(op, "scope");
        return scope is null ? node : _scopes[scope].Own(node);
    }

    private void DefineComputed(JsonElement op, bool eager)
    {
        var id = Str(op, "id")!;
        var reads = ReadsOf(op);
        var offset = Num(op, "offset") ?? 0;
        _computes.TryAdd(id, 0);
        var node = _ctx.Computed<long>(c =>
        {
            _computes[id] = _computes.GetValueOrDefault(id) + 1;
            var sum = offset;
            foreach (var r in reads) sum += ReadTracked(r, c);
            return sum;
        }, name: id);
        Own(op, node);
        if (eager) node.Eager();
        Define(id, new NodeRef(Kind.Computed, node));
    }

    private void DefineEffect(JsonElement op)
    {
        var id = Str(op, "id")!;
        var mergesInto = Str(op, "merges_into");
        var writesOwnCone = Str(op, "writes_own_cone");

        Effect effect;
        if (mergesInto is not null)
        {
            // A feed effect reads upstream (tracked) and folds the sum into the merge cell through
            // the UNTRACKED write surface — the write is an argument, not a dependency.
            var reads = ReadsOf(op);
            var target = SourceOf(mergesInto);
            _merges.TryAdd(mergesInto, 0);
            effect = new Effect(_ctx, c =>
            {
                _runLog.Add(id);
                long acc = 0;
                foreach (var r in reads) acc += ReadTracked(r, c);
                target.Merge(acc);
                return () => _cleanupLog.Add(id);
            });
        }
        else if (writesOwnCone is not null)
        {
            // The divergent effect: read `own` (tracked, so it is a dependency) and write an
            // incremented value back into it. The write reschedules the effect through the
            // SCHEDULER — a scheduler-closed loop, not a graph cycle — which the bounded drain
            // cuts short. Lowering the budget here keeps the exhausting loop fast.
            //
            // Zero is held as a fixed point so that at creation the effect reads 0, writes 0, the
            // store guard skips the invalidation, and the loop is not kicked yet. The edge is
            // registered the instant the read runs, so a plain n+1 body would reschedule itself
            // mid-creation and exhaust before the external kick ever landed.
            var own = SourceOf(writesOwnCone);
            _ctx.DrainBudget = 256;
            effect = new Effect(_ctx, c =>
            {
                _runLog.Add(id);
                var v = own.Get(c);
                own.Set(v == 0 ? 0 : unchecked(v + 1));
                return () => _cleanupLog.Add(id);
            });
        }
        else
        {
            effect = PlainEffect(id, ReadsOf(op));
        }

        Own(op, effect);
        Define(id, new NodeRef(Kind.Effect, effect));
    }

    private Effect PlainEffect(string id, IReadOnlyList<NodeRef> reads) => new(_ctx, c =>
    {
        _runLog.Add(id);
        foreach (var r in reads) ReadTracked(r, c);
        return () => _cleanupLog.Add(id);
    });

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
                    if (_nodes.TryGetValue(id, out var existing)) DisposeRef(existing);
                    Define(id, new NodeRef(Kind.Effect, PlainEffect(id, [source])));
                }
                break;

            // One teardown scope per cycle; its subscriber is gone by the end of its own cycle.
            case "scope_per_cycle":
                {
                    var name = $"{prefix}_scoped";
                    for (var c = 0L; c < cycles; c++)
                    {
                        var sc = _ctx.Scope();
                        sc.Own(PlainEffect(name, [source]));
                        sc.Close();
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

    private long ReadTracked(NodeRef n, Compute c) => n.Kind switch
    {
        Kind.Source => ((Source<long>)n.Handle).Get(c),
        Kind.Computed => ((Computed<long>)n.Handle).Get(c),
        _ => throw new InvalidOperationException($"{_fixture}: cannot read node kind {n.Kind}"),
    };

    private (bool Ok, long Value) Read(string id)
    {
        if (_poisoned.Contains(id)) return (false, 0);
        if (!_nodes.TryGetValue(id, out var n))
            throw new InvalidOperationException($"{_fixture}: read of unknown node {id}");
        try
        {
            return n.Kind switch
            {
                Kind.Source => (true, ((Source<long>)n.Handle).Get()),
                Kind.Computed => (true, ((Computed<long>)n.Handle).Get()),
                _ => throw new InvalidOperationException($"{_fixture}: read of effect {id}"),
            };
        }
        catch (DisposedNodeException)
        {
            _poisoned.Add(id);
            return (false, 0);
        }
    }

    private Source<long> SourceOf(string id) => _nodes[id] switch
    {
        { Kind: Kind.Source, Handle: Source<long> s } => s,
        _ => throw new InvalidOperationException($"{_fixture}: {id} is not a source cell"),
    };

    private static void DisposeRef(NodeRef n)
    {
        switch (n.Handle)
        {
            case Source<long> s: s.Dispose(); break;
            case Computed<long> c: c.Dispose(); break;
            case Effect e: e.Dispose(); break;
            default: throw new InvalidOperationException($"unknown node handle {n.Handle.GetType()}");
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
