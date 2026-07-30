using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Replays the <c>Collection</c>-kind fixtures of the canonical <c>collections</c> corpus against
/// <see cref="SourceMap{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The subject is REACTIVITY INDEPENDENCE, so the load-bearing observation is which readers were
/// invalidated — not which values came out. Every fixture's <c>invalidates</c> block is asserted by
/// standing three real reader slots on the map (one per plane: a value reader per key, a membership
/// reader over <c>Len</c>/<c>ContainsKey</c>, an order reader over <c>Keys</c>) and counting how
/// many times each recomputes. Those counters live in the reader's own compute body, so they record
/// work the library actually performed rather than what the runner intended.
/// </para>
/// <para>
/// This is what makes the fixtures discriminating: a map that bumps one shared signal for every
/// mutation returns exactly the right order, values, and membership at every step, and gets every
/// <c>invalidates</c> assertion wrong.
/// </para>
/// </remarks>
public sealed class CollectionsConformanceTests
{
    private const string Corpus = "collections";

    /// <summary>
    /// Fixtures in this corpus that belong to planes this binding has not ported yet, with the
    /// surface that blocks each.
    /// </summary>
    /// <remarks>
    /// The <c>collections</c> directory is shared across several phases: the queue, topic, work-queue,
    /// merge, CRDT, and semantic-tree fixtures are other rows. They are named individually rather
    /// than filtered by prefix, so a new fixture arriving upstream fails the completeness assertion
    /// below instead of being silently swept into a pattern.
    /// </remarks>
    private static readonly Dictionary<string, string> Unsupported = new(StringComparer.Ordinal)
    {
    };

    /// <summary>
    /// Fixtures in this corpus that a DIFFERENT runner in this suite replays.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="Unsupported"/>: that is a ledger of gaps in the
    /// binding, and folding "another runner owns this" into it would quietly overstate what is
    /// missing. These fixtures are shaped differently (scenario-based rather than step- or
    /// reconcile-based) and have their own replay, so they are excluded here and covered there.
    /// </remarks>
    private static readonly Dictionary<string, string> HandledElsewhere = new(StringComparer.Ordinal)
    {
        ["mergecell_algebra.json"] = nameof(MergeCellConformanceTests),
        ["queuecell_bounded_backpressure.json"] = nameof(QueueCellConformanceTests),
        ["queuecell_closure_lifecycle.json"] = nameof(QueueCellConformanceTests),
        ["queuecell_mpsc_multi_writer.json"] = nameof(QueueCellConformanceTests),
        ["queuecell_popped_head_observation.json"] = nameof(QueueCellConformanceTests),
        ["queuecell_spsc_push_pop.json"] = nameof(QueueCellConformanceTests),
        ["semtree_incremental.json"] = nameof(SemTreeConformanceTests),
        ["seqcrdt_convergence.json"] = nameof(CrdtConformanceTests),
        ["stableid_alignment.json"] = nameof(StableIdConformanceTests),
        ["textcrdt_convergence.json"] = nameof(CrdtConformanceTests),
        ["textcrdt_delta_sync.json"] = nameof(CrdtConformanceTests),
        ["topiccell_broadcast_cursor_isolation.json"] = nameof(TopicCellConformanceTests),
        ["topiccell_durable_replay_gc.json"] = nameof(TopicCellConformanceTests),
        ["topiccell_ephemeral_lifecycle.json"] = nameof(TopicCellConformanceTests),
        ["topiccell_offline_tail_bounds.json"] = nameof(TopicCellConformanceTests),
        ["workqueue_competing_delivery.json"] = nameof(WorkQueueConformanceTests),
        ["workqueue_lease_deadletter.json"] = nameof(WorkQueueConformanceTests),
    };

    /// <summary>Assertions this binding does not satisfy, keyed <c>fixture#step:key</c>.</summary>
    private static readonly Dictionary<string, string> KnownDivergences = [];

    [Fact]
    public void ReplaysTheCollectionFixturesWithNoUnexpectedDivergence()
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}; " +
            "clone lazily-spec as a sibling. A skip here would report green while testing nothing.");

        var names = SpecCorpus.FixtureNames(Corpus);
        Assert.NotEmpty(names);

        var replayed = new List<string>();
        var divergences = new List<string>();
        var assertions = 0;

        foreach (var name in names)
        {
            if (Unsupported.ContainsKey(name) || HandledElsewhere.ContainsKey(name)) continue;

            using var doc = SpecCorpus.Load(Corpus, name);
            var fx = doc.RootElement;

            void Check(string key, object? got, object? want)
            {
                assertions++;
                if (!Equals(got?.ToString(), want?.ToString())) divergences.Add($"{name}:{key} — got {got}, want {want}");
            }

            if (fx.TryGetProperty("reconcile", out var reconcile))
            {
                var reconcileExpected = FixtureAssertions.Of(fx, "expected", name);
                ReplayReconcile(reconcile, reconcileExpected, Check);
                reconcileExpected.Verify();
            }
            else
            {
                ReplaySteps(fx, Check);
            }

            replayed.Add(name);
        }

        Assert.Equal(
            names.Where(n => !Unsupported.ContainsKey(n) && !HandledElsewhere.ContainsKey(n))
                .Order(StringComparer.Ordinal).ToArray(),
            replayed.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            KnownDivergences.Values.Order(StringComparer.Ordinal).ToArray(),
            divergences.Order(StringComparer.Ordinal).ToArray());
        Assert.NotEmpty(replayed);
        Assert.True(assertions > 0, "replayed the corpus but checked nothing");
    }

    /// <summary>Replays a step-shaped fixture, asserting the invalidation of each plane per step.</summary>
    private static void ReplaySteps(JsonElement fx, Action<string, object?, object?> check)
    {
        var ctx = new Context();
        var map = new SourceMap<string, int>(ctx);

        var initial = fx.GetProperty("initial");
        var order = initial.GetProperty("order").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var values = initial.GetProperty("values");
        foreach (var key in order) map.Entry(key, values.GetProperty(key).GetInt32());

        var probes = new PlaneProbes(ctx, map, order);
        probes.Prime();

        var stepIndex = 0;
        foreach (var step in fx.GetProperty("steps").EnumerateArray())
        {
            var op = step.GetProperty("op");
            var kind = op.GetProperty("type").GetString();
            var key = op.TryGetProperty("key", out var k) ? k.GetString()! : "";

            // Captured BEFORE the op so `handle_stable` compares identity across the mutation: a
            // remove-plus-re-mint would satisfy every order and value assertion while failing here.
            var handleBefore = map.TryGetHandle(key, out var h) ? h : null;

            probes.ResetCounts();
            switch (kind)
            {
                case "set_value": map.Set(key, op.GetProperty("value").GetInt32()); break;
                case "insert": map.Insert(key, op.GetProperty("value").GetInt32()); break;
                case "remove": map.Remove(key); break;
                case "move_to": map.MoveTo(key, op.GetProperty("index").GetInt32()); break;
                case "move_before": map.MoveBefore(key, op.GetProperty("before").GetString()!); break;
                case "move_after": map.MoveAfter(key, op.GetProperty("after").GetString()!); break;
                default: throw new InvalidOperationException($"unknown collection op {kind}");
            }

            var recomputed = probes.Recomputed();
            var where = $"#{stepIndex}";
            var expected = FixtureAssertions.Of(step, "expected", where);

            expected.AssertKeyWith(
                "order",
                want => check(
                    $"{where}:order",
                    string.Join(",", map.PresentKeys()),
                    string.Join(",", want.EnumerateArray().Select(x => x.GetString()!))));

            expected.TryAssertKeyWith(
                "membership",
                membership => check(
                    $"{where}:membership",
                    string.Join(",", map.PresentKeys().Order(StringComparer.Ordinal)),
                    string.Join(",", membership.EnumerateArray().Select(x => x.GetString()!).Order(StringComparer.Ordinal))));

            expected.TryAssertKeyWith(
                "values",
                wantValues =>
                {
                    foreach (var v in wantValues.EnumerateObject())
                    {
                        check($"{where}:value.{v.Name}", map.TryObserve(v.Name, out var got) ? got : null, v.Value.GetInt32());
                    }
                });

            expected.AssertKeyWith(
                "invalidates",
                invalidates =>
                {
                    check(
                        $"{where}:invalidates.membership",
                        recomputed.Membership,
                        invalidates.GetProperty("membership").GetBoolean());
                    check(
                        $"{where}:invalidates.order",
                        recomputed.Order,
                        invalidates.GetProperty("order").GetBoolean());
                    check(
                        $"{where}:invalidates.value",
                        string.Join(",", recomputed.Values.Order(StringComparer.Ordinal)),
                        string.Join(",", invalidates.GetProperty("value").EnumerateArray().Select(x => x.GetString()!).Order(StringComparer.Ordinal)));
                });

            expected.TryAssertKeyWith(
                "handle_stable",
                stable =>
                {
                    foreach (var s in stable.EnumerateObject())
                    {
                        var same = map.TryGetHandle(s.Name, out var after) && ReferenceEquals(handleBefore, after);
                        check($"{where}:handle_stable.{s.Name}", same, s.Value.GetBoolean());
                    }
                });

            expected.Verify();
            probes.Rearm();
            stepIndex++;
        }
    }

    /// <summary>Replays a reconcile-shaped fixture, asserting the emitted op set and stable-key quiescence.</summary>
    private static void ReplayReconcile(JsonElement reconcile, FixtureAssertions expected, Action<string, object?, object?> check)
    {
        var ctx = new Context();
        var map = new SourceMap<string, int>(ctx);

        var prior = reconcile.GetProperty("prior");
        var priorOrder = prior.GetProperty("order").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var priorValues = prior.GetProperty("values");
        foreach (var key in priorOrder) map.Entry(key, priorValues.GetProperty(key).GetInt32());

        var probes = new PlaneProbes(ctx, map, priorOrder);
        probes.Prime();
        probes.ResetCounts();

        var target = reconcile.GetProperty("target");
        var targetOrder = target.GetProperty("order").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var targetValues = target.GetProperty("values")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);

        var ops = map.Reconcile(targetOrder, targetValues);

        expected.AssertKeyWith(
            "ops",
            want => check(
                "ops",
                string.Join(";", ops.Select(Describe)),
                string.Join(";", want.EnumerateArray().Select(DescribeExpected))));

        expected.AssertKeyWith(
            "result_order",
            want => check(
                "result_order",
                string.Join(",", map.PresentKeys()),
                string.Join(",", want.EnumerateArray().Select(x => x.GetString()!))));

        // The LIS keys must not have been touched at all. Asserted on the reader counters, because
        // their VALUES are identical whether or not they were invalidated — a binding that re-minted
        // every key would return the same numbers here and recompute every reader.
        var recomputed = probes.Recomputed();
        expected.AssertKeyWith(
            "stable_keys_not_invalidated",
            want =>
            {
                foreach (var key in want.EnumerateArray().Select(x => x.GetString()!))
                {
                    check($"stable_keys_not_invalidated.{key}", recomputed.Values.Contains(key), false);
                }
            });
    }

    private static string Describe<TKey, TValue>(DiffOp<TKey, TValue> op)
        where TKey : notnull => op switch
        {
            DiffOpRemove<TKey, TValue> r => $"remove:{r.Key}",
            DiffOpMove<TKey, TValue> m => $"move:{m.Key}",
            DiffOpInsert<TKey, TValue> i => $"insert:{i.Key}",
            DiffOpUpdate<TKey, TValue> u => $"update:{u.Key}",
            _ => "?",
        };

    private static string DescribeExpected(JsonElement op) =>
        $"{op.GetProperty("type").GetString()}:{op.GetProperty("key").GetString()}";

    /// <summary>
    /// One real reader per reactive plane, each counting its own recomputes from inside its compute
    /// body.
    /// </summary>
    private sealed class PlaneProbes
    {
        private readonly Computed<int> _membership;
        private readonly Computed<int> _order;
        private readonly Dictionary<string, Computed<int>> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _baseline = new(StringComparer.Ordinal);
        private readonly SourceMap<string, int> _map;

        internal PlaneProbes(Context ctx, SourceMap<string, int> map, IEnumerable<string> keys)
        {
            _map = map;
            _membership = ctx.Slot(c =>
            {
                Bump("membership");
                return map.Len(c);
            });
            _order = ctx.Slot(c =>
            {
                Bump("order");
                return map.Keys(c).Count;
            });
            foreach (var key in keys) AddValueProbe(ctx, key);
        }

        private void AddValueProbe(Context ctx, string key) =>
            _values[key] = ctx.Slot(c =>
            {
                Bump($"value:{key}");
                return _map.TryGetHandle(key, out var h) ? h.Get(c) : 0;
            });

        private void Bump(string tag) => _counts[tag] = _counts.GetValueOrDefault(tag) + 1;

        /// <summary>Materializes every probe so later recomputes are attributable to an op.</summary>
        internal void Prime() => Rearm();

        /// <summary>Re-reads every probe, so the next op's invalidation is what makes it recompute.</summary>
        internal void Rearm()
        {
            _ = _membership.Get();
            _ = _order.Get();
            foreach (var v in _values.Values) _ = v.Get();
        }

        internal void ResetCounts()
        {
            _baseline.Clear();
            foreach (var kv in _counts) _baseline[kv.Key] = kv.Value;
        }

        /// <summary>Which probes recomputed since <see cref="ResetCounts"/>, forcing a pull first.</summary>
        /// <remarks>
        /// Scoped to keys still PRESENT. A removed key's own reader is invalidated by design — the
        /// entry's cell is cleared so readers learn their source is gone rather than keeping a stale
        /// cache — and the corpus's `value` list is about UNRELATED readers, so counting the removed
        /// key here would report the intended behaviour as a divergence.
        /// </remarks>
        internal (bool Membership, bool Order, IReadOnlyList<string> Values) Recomputed()
        {
            Rearm();
            var values = _values.Keys.Where(k => _map.IsPresent(k) && Moved($"value:{k}")).ToList();
            return (Moved("membership"), Moved("order"), values);
        }

        private bool Moved(string tag) => _counts.GetValueOrDefault(tag) > _baseline.GetValueOrDefault(tag);
    }
}
