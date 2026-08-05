using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lazily.Tests;

public sealed class BoundaryIngressAdapterConformanceTests
{
    private const string Corpus = "ingress";
    private const string Fixture = "boundary_ingress_adapter.json";

    private sealed class Delivery(string id, IEnumerable<string> targets)
    {
        public string Id { get; } = id;
        public HashSet<string> Targets { get; } = new(targets, StringComparer.Ordinal);
        public HashSet<string> Acked { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Model(int maxBuffered, long freshnessHorizon)
    {
        private string _phase = "detached";
        private long _generation;
        private long? _cursor;
        private readonly SortedDictionary<long, JsonElement> _buffered = [];
        private HashSet<string> _sourceKeys = new(StringComparer.Ordinal);
        private HashSet<string> _members = new(StringComparer.Ordinal);
        private string _validation = "valid";
        private long? _replayFrom;
        private long _staleEvents;
        private Delivery? _delivery;
        private long? _lastStampedAt;
        private long _now;
        private long _revision;

        private void Changed() => _revision++;

        private void ApplyPayload(JsonElement op)
        {
            switch (op.GetProperty("action").GetString())
            {
                case "upsert":
                    _sourceKeys.Add(op.GetProperty("key").GetString()!);
                    break;
                case "remove":
                    _sourceKeys.Remove(op.GetProperty("key").GetString()!);
                    break;
                case "validate":
                    _validation = op.GetProperty("validation").GetString()!;
                    break;
                default:
                    throw new InvalidOperationException("unknown boundary event action");
            }

            _cursor = op.GetProperty("cursor").GetInt64();
            _lastStampedAt = op.GetProperty("stamped_at").GetInt64();
            _phase = _validation == "valid" ? "live" : "invalid";
            _replayFrom = null;
        }

        private void Drain()
        {
            while (_cursor is { } cursor && _buffered.Remove(cursor + 1, out var next))
            {
                ApplyPayload(next);
            }

            if (_buffered.Count > 0)
            {
                _phase = "replay_required";
                _replayFrom = _cursor + 1;
            }
        }

        public void Apply(JsonElement op)
        {
            switch (op.GetProperty("type").GetString())
            {
                case "subscribe":
                    {
                        var generation = op.GetProperty("generation").GetInt64();
                        if (generation < _generation) return;
                        _generation = generation;
                        _cursor = null;
                        _buffered.Clear();
                        _sourceKeys.Clear();
                        _members.Clear();
                        _validation = "valid";
                        _replayFrom = null;
                        _phase = "bootstrapping";
                        Changed();
                        return;
                    }
                case "snapshot":
                    {
                        var generation = op.GetProperty("generation").GetInt64();
                        if (generation < _generation)
                        {
                            _staleEvents++;
                            Changed();
                            return;
                        }
                        if (generation > _generation)
                        {
                            _generation = generation;
                            _buffered.Clear();
                        }
                        _cursor = op.GetProperty("cursor").GetInt64();
                        _lastStampedAt = op.GetProperty("stamped_at").GetInt64();
                        _sourceKeys = Strings(op.GetProperty("source_keys"));
                        _members = Strings(op.GetProperty("members"));
                        _validation = op.GetProperty("validation").GetString()!;
                        _phase = _validation == "valid" ? "live" : "invalid";
                        _replayFrom = null;
                        foreach (var cursor in _buffered.Keys.Where(cursor => cursor <= _cursor).ToArray())
                        {
                            _buffered.Remove(cursor);
                        }
                        Drain();
                        Changed();
                        return;
                    }
                case "event":
                    {
                        var generation = op.GetProperty("generation").GetInt64();
                        var cursor = op.GetProperty("cursor").GetInt64();
                        if (generation < _generation)
                        {
                            _staleEvents++;
                            Changed();
                            return;
                        }
                        if (generation > _generation)
                        {
                            _generation = generation;
                            _cursor = null;
                            _buffered.Clear();
                            _sourceKeys.Clear();
                            _members.Clear();
                            _phase = "bootstrapping";
                            _replayFrom = null;
                        }
                        if (_cursor is null)
                        {
                            if (_buffered.Count >= maxBuffered && !_buffered.ContainsKey(cursor))
                            {
                                _phase = "backpressured";
                                _replayFrom = 0;
                                Changed();
                                return;
                            }
                            if (_buffered.TryAdd(cursor, op)) Changed();
                            return;
                        }
                        if (cursor <= _cursor || _buffered.ContainsKey(cursor)) return;
                        if (cursor == _cursor + 1)
                        {
                            ApplyPayload(op);
                            Drain();
                            Changed();
                            return;
                        }
                        if (_buffered.Count >= maxBuffered)
                        {
                            _phase = "backpressured";
                            _replayFrom = _cursor + 1;
                            Changed();
                            return;
                        }
                        _buffered[cursor] = op;
                        _phase = "replay_required";
                        _replayFrom = _cursor + 1;
                        Changed();
                        return;
                    }
                case "member_join":
                    {
                        var member = op.GetProperty("member").GetString()!;
                        if (!_members.Add(member)) return;
                        if (_delivery is { Targets.Count: 0 }) _delivery.Targets.Add(member);
                        Changed();
                        return;
                    }
                case "member_leave":
                    if (_members.Remove(op.GetProperty("member").GetString()!)) Changed();
                    return;
                case "open_receipt":
                    _delivery = new Delivery(op.GetProperty("receipt_id").GetString()!, _members);
                    Changed();
                    return;
                case "ack":
                    {
                        if (_delivery is null ||
                            _delivery.Id != op.GetProperty("receipt_id").GetString()) return;
                        var member = op.GetProperty("member").GetString()!;
                        if (_delivery.Targets.Contains(member) && _delivery.Acked.Add(member)) Changed();
                        return;
                    }
                case "tick":
                    {
                        var before = Fresh;
                        _now = op.GetProperty("now").GetInt64();
                        if (Fresh != before) Changed();
                        return;
                    }
                default:
                    throw new InvalidOperationException("unknown boundary ingress op");
            }
        }

        private bool Fresh =>
            _lastStampedAt is { } stampedAt && _now - stampedAt <= freshnessHorizon;

        public JsonObject Projection()
        {
            JsonNode? delivery = null;
            if (_delivery is not null)
            {
                delivery = new JsonObject
                {
                    ["receipt_id"] = _delivery.Id,
                    ["targets"] = Array(_delivery.Targets),
                    ["acked"] = Array(_delivery.Acked),
                    ["converged"] = _delivery.Targets.Count > 0 &&
                        _delivery.Targets.IsSubsetOf(_delivery.Acked),
                };
            }

            return new JsonObject
            {
                ["phase"] = _phase,
                ["generation"] = _generation,
                ["cursor"] = _cursor,
                ["buffered_cursors"] = new JsonArray(
                    _buffered.Keys.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["source_keys"] = Array(_sourceKeys),
                ["members"] = Array(_members),
                ["validation"] = _validation,
                ["replay_from"] = _replayFrom,
                ["stale_events"] = _staleEvents,
                ["delivery"] = delivery,
                ["ready"] = _phase == "live" && _validation == "valid",
                ["fresh"] = Fresh,
                ["observation_revision"] = _revision,
                ["revision"] = _revision,
            };
        }

        private static HashSet<string> Strings(JsonElement array) =>
            new(array.EnumerateArray().Select(value => value.GetString()!), StringComparer.Ordinal);

        private static JsonArray Array(IEnumerable<string> values) =>
            new(values.Order(StringComparer.Ordinal)
                .Select(value => (JsonNode?)JsonValue.Create(value))
                .ToArray());
    }

    [Fact]
    public void ReplaysCanonicalBoundaryIngressAdapterContract()
    {
        using var document = SpecCorpus.Load(Corpus, Fixture);
        var root = document.RootElement;
        var basePolicy = root.GetProperty("policy");
        var replayed = 0;

        foreach (var scenario in SpecCorpus.Scenarios(root, Corpus, Fixture).All())
        {
            var maxBuffered = scenario.TryGetProperty("policy", out var policy)
                ? policy.GetProperty("max_buffered").GetInt32()
                : basePolicy.GetProperty("max_buffered").GetInt32();
            var model = new Model(
                maxBuffered,
                basePolicy.GetProperty("freshness_horizon").GetInt64());
            var index = 0;
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                model.Apply(step.GetProperty("op"));
                var actual = model.Projection();
                var expected = FixtureAssertions.Of(
                    step,
                    "expected",
                    $"{Fixture} {scenario.GetProperty("id").GetString()} step {index}");
                foreach (var property in expected.Element.EnumerateObject())
                {
                    expected.AssertKeyDeep(property.Name, actual[property.Name]);
                }
                expected.Verify();
                replayed++;
                index++;
            }
        }

        Assert.True(replayed > 0);
    }
}
