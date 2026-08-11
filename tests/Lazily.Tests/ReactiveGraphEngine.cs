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

        // Two levels of descent (#lzsubblockkeyset). Both blocks were consumed by a chain of
        // `TryGetProperty` probes, so a key the corpus adds to either — or to any of their
        // per-node maps — was read by nothing and reported by nothing. Each level now has a
        // tracker that reports an unrecognised member.
        expected.TryAssertObjectKey(
            "final_state",
            fin =>
            {
                fin.TryAssertObjectKey("dependents_of", deps =>
                {
                    foreach (var p in deps.EnumerateObject())
                    {
                        var name = p.Name;
                        deps.AssertKeyWith(name, want =>
                        {
                            var got = _model.DependentCount(_nodes[name]);
                            want.Against(got, (expect, actual) =>
                                Check($"final.dependents_of.{name}", actual, expect.GetInt32()));
                            observation.Add($"dependents_of.{name}={got}");
                        });
                    }
                });
                fin.TryAssertObjectKey("readable", readable =>
                {
                    foreach (var p in readable.EnumerateObject())
                    {
                        var name = p.Name;
                        readable.AssertKeyWith(name, want =>
                        {
                            var alive = Readable(name);
                            want.Against(alive, (expect, actual) =>
                                Check($"final.readable.{name}", actual, expect.GetBoolean()));
                            observation.Add($"readable.{name}={alive}");
                        });
                    }
                });
                fin.TryAssertObjectKey("read", reads =>
                {
                    foreach (var p in reads.EnumerateObject())
                    {
                        var name = p.Name;
                        reads.AssertKeyWith(name, want =>
                        {
                            var r = Read(name);
                            want.Against<long?>(r.Ok ? r.Value : null, (expect, actual) =>
                                Check($"final.read.{name}", actual, expect.GetInt64()));
                            observation.Add($"read.{name}={(r.Ok ? r.Value : null)}");
                        });
                    }
                });
            });

        expected.TryAssertObjectKey(
            "after_publish",
            publish =>
            {
                if (!publish.TryAssertObjectKey("op", pop =>
                {
                    // `type` used to be read by nothing: the runner assumed `set_cell` and a
                    // fixture that changed the op would have been replayed as the old one.
                    pop.AssertKey("type", "set_cell");
                    // INPUTS, not observations (#lzcsuncomparedvalues): these two name the cell
                    // the publish writes and the value it writes, so no comparison against them is
                    // possible here. What they imply IS asserted — every `after_publish.read`
                    // below reads the graph this write produced.
                    pop.ExcuseKey(
                        "id",
                        "input: names the cell after_publish writes; its effect is asserted as "
                        + "after_publish.read.<id>");
                    pop.ExcuseKey(
                        "value",
                        "input: the value after_publish writes; its effect is asserted as "
                        + "after_publish.read.<id>");
                    var before = _model.RunLog.Count;
                    _model.SetCell(
                        _nodes[pop.GetProperty("id").GetString()!],
                        pop.GetProperty("value").GetInt64());
                    _model.Settle();
                    var observed = _model.RunLog.Skip(before).ToList();
                    observation.Add($"after_publish.observed_by={string.Join(",", observed)}");
                    publish.TryAssertKeyWith(
                        "observed_by",
                        wantObserved => wantObserved.Against(
                            string.Join(",", observed),
                            (expect, actual) => Check(
                                "after_publish.observed_by",
                                actual,
                                string.Join(",", Strings(expect)))));
                }))
                {
                    return;
                }

                publish.TryAssertObjectKey("read", pReads =>
                {
                    foreach (var p in pReads.EnumerateObject())
                    {
                        var name = p.Name;
                        pReads.AssertKeyWith(name, want =>
                        {
                            var r = Read(name);
                            want.Against<long?>(r.Ok ? r.Value : null, (expect, actual) =>
                                Check($"after_publish.read.{name}", actual, expect.GetInt64()));
                            observation.Add($"after_publish.read.{name}={(r.Ok ? r.Value : null)}");
                        });
                    }
                });
                publish.TryAssertObjectKey("dependents_of", pDeps =>
                {
                    foreach (var p in pDeps.EnumerateObject())
                    {
                        var name = p.Name;
                        pDeps.AssertKeyWith(name, want =>
                        {
                            var got = _model.DependentCount(_nodes[name]);
                            want.Against(got, (expect, actual) =>
                                Check($"after_publish.dependents_of.{name}", actual, expect.GetInt32()));
                            observation.Add($"after_publish.dependents_of.{name}={got}");
                        });
                    }
                });
            });

        expected.Verify();
        return observation;
    }

    // -----------------------------------------------------------------------
    // Assertions
    // -----------------------------------------------------------------------

    /// <remarks>
    /// The whole `expect` block is BOUND to a tracker (<c>#lzunboundblockguard</c>). It used to
    /// be read straight off the <see cref="JsonElement"/>: every key was evaluated and an
    /// unknown one threw, so the keys were covered — but rung 0 asks a different question, and
    /// to it these 113 per-step blocks were indistinguishable from blocks nobody checks. The
    /// switch below is unchanged in what it compares; each arm now receives the fixture's value
    /// THROUGH the tracker, which is what books the block as bound and lets
    /// <see cref="FixtureAssertions.Verify"/> raise the key-level rungs here too.
    /// </remarks>
    private void Assert(JsonElement expect, JsonElement op, long? opValue, bool opError, List<string> observed)
    {
        var tracked = FixtureAssertions.Wrap(expect, $"{_fixture}#{_step}.expect");

        // `computes_of` is evaluated BEFORE every other key, and deliberately. A step asserting
        // `computes_of` alongside `value`/`read`/`readable` is asserting a count that a read would
        // change: on a de-eagered signal the read triggers the lazy recompute, so evaluating the
        // read first would raise the count to the number a CONFORMING binding shows and make a
        // non-conforming one agree with it.
        tracked.TryAssertObjectKey("computes_of", computesOf =>
        {
            foreach (var p in computesOf.EnumerateObject())
            {
                var name = p.Name;
                computesOf.AssertKeyWith(name, want =>
                {
                    var got = _model.ComputesOf(name);
                    // "computes" of an effect are its runs, already recorded in the run log.
                    if (got == 0 && _nodes.TryGetValue(name, out var n) && n.Kind == NodeKind.Effect)
                        got = _model.RunLog.Count(x => x == name);
                    want.Against(got, (expect, actual) =>
                        Check($"computes_of.{name}", actual, expect.GetInt32()));
                });
            }
        });

        foreach (var prop in expect.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "note":
                case "computes_of":
                    break;

                case "merges_of":
                    tracked.AssertObjectKey("merges_of", merges =>
                    {
                        foreach (var p in merges.EnumerateObject())
                        {
                            var name = p.Name;
                            merges.AssertKeyWith(name, want =>
                            {
                                if (!_model.KnowsMerge(name))
                                    throw new InvalidOperationException($"{_fixture}: merges_of unknown cell {name}");
                                want.Against(_model.MergesOf(name), (expect, actual) =>
                                    Check($"merges_of.{name}", actual, expect.GetInt32()));
                            });
                        }
                    });
                    break;

                case "drain_exhausted":
                    tracked.AssertKeyWith(
                        "drain_exhausted",
                        want => want.Against(_model.DrainExhausted, (expect, actual) =>
                            Check("drain_exhausted", actual, expect.GetBoolean())));
                    break;

                case "dependents_of":
                    tracked.AssertObjectKey("dependents_of", deps =>
                    {
                        foreach (var p in deps.EnumerateObject())
                        {
                            var name = p.Name;
                            deps.AssertKeyWith(name, want => want.Against(
                                _model.DependentCount(_nodes[name]),
                                (expect, actual) => Check($"dependents_of.{name}", actual, expect.GetInt32())));
                        }
                    });
                    break;

                case "dependencies_of":
                    tracked.AssertObjectKey("dependencies_of", deps =>
                    {
                        foreach (var p in deps.EnumerateObject())
                        {
                            var name = p.Name;
                            deps.AssertKeyWith(name, want => want.Against(
                                _model.DependencyCount(_nodes[name]),
                                (expect, actual) => Check($"dependencies_of.{name}", actual, expect.GetInt32())));
                        }
                    });
                    break;

                case "error":
                    tracked.AssertKeyWith(
                        "error",
                        want => want.Against(opError, (expect, actual) =>
                            Check("error", actual, expect.ValueKind is not JsonValueKind.Null)));
                    break;

                case "value":
                    {
                        if (expect.TryGetProperty("error", out var e) && e.ValueKind is not JsonValueKind.Null)
                        {
                            // The step also asserts a non-null `error`: the op failed, so there is
                            // no value it could have produced, and `error` carries the whole
                            // assertion. Excused rather than skipped, so the claim is written down
                            // and fails as stale the day a step asserts both.
                            tracked.ExcuseKey(
                                "value",
                                "the same step asserts a non-null `error`; a failed op produces no "
                                + "value, and the `error` assertion is what this step proves");
                            break;
                        }

                        // A feed effect's op id is the effect (unreadable), so its `value` assertion
                        // targets the merge cell it feeds. Otherwise (signal creation) the op id is
                        // itself the readable node. The read is issued AFTER `computes_of` has been
                        // evaluated, so it cannot mask a deferred materialization.
                        tracked.AssertKeyWith("value", want =>
                        {
                            var target = Str(op, "merges_into") ?? Str(op, "id");
                            long? got = opValue;
                            if (got is null && target is not null)
                            {
                                var r = Read(target);
                                got = r.Ok ? r.Value : null;
                            }
                            want.Against(got, (expect, actual) =>
                                Check("value", actual, expect.GetInt64()));
                        });
                        break;
                    }

                case "read":
                    tracked.AssertObjectKey("read", reads =>
                    {
                        foreach (var p in reads.EnumerateObject())
                        {
                            var name = p.Name;
                            reads.AssertKeyWith(name, want =>
                            {
                                var r = Read(name);
                                want.Against<long?>(r.Ok ? r.Value : null, (expect, actual) =>
                                    Check($"read.{name}", actual, expect.GetInt64()));
                            });
                        }
                    });
                    break;

                case "readable":
                    tracked.AssertObjectKey("readable", readable =>
                    {
                        foreach (var p in readable.EnumerateObject())
                        {
                            var name = p.Name;
                            readable.AssertKeyWith(
                                name,
                                want => want.Against(Readable(name), (expect, actual) =>
                                    Check($"readable.{name}", actual, expect.GetBoolean())));
                        }
                    });
                    break;

                case "observed_by":
                    tracked.AssertKeyWith("observed_by", want => want.Against(
                        string.Join(",", observed),
                        (expect, actual) => Check("observed_by", actual, string.Join(",", Strings(expect)))));
                    break;

                case "observed_count":
                    tracked.AssertKeyWith(
                        "observed_count",
                        want => want.Against(observed.Count, (expect, actual) =>
                            Check("observed_count", actual, expect.GetInt32())));
                    break;

                case "cleanup_order":
                    // Only effects run a cleanup callback, so the expected order is projected onto
                    // its effect entries. `cleanup_order` is cumulative, not per-step.
                    tracked.AssertKeyWith("cleanup_order", value => value.Against(
                        string.Join(",", _model.CleanupLog),
                        (expect, actual) =>
                        {
                            var want = Strings(expect)
                                .Where(id => _stale.TryGetValue(id, out var h) && h.Kind == NodeKind.Effect);
                            Check("cleanup_order", actual, string.Join(",", want));
                        }));
                    break;

                case "scope_owned_count":
                    tracked.AssertObjectKey("scope_owned_count", owned =>
                    {
                        foreach (var p in owned.EnumerateObject())
                        {
                            var name = p.Name;
                            owned.AssertKeyWith(
                                name,
                                want => want.Against(_scopes[name].Owned, (expect, actual) =>
                                    Check($"scope_owned_count.{name}", actual, expect.GetInt32())));
                        }
                    });
                    break;

                default:
                    throw new NotSupportedException($"{_fixture}: unknown expectation {prop.Name}");
            }
        }

        tracked.Verify();
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
