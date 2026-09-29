using System.Text.Json;
using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class SimConsumerTestkitTests
{
    private const string Corpus = "simulation";
    private const string Fixture = "consumer_testkit.json";

    [Fact]
    public void ReplaysCanonicalConsumerSimulationCorpus()
    {
        using var document = SpecCorpus.Load(Corpus, Fixture);
        var root = document.RootElement;
        var actions = root.GetProperty("actions").EnumerateArray().Select(Action).ToArray();
        var ports = root.GetProperty("ports").EnumerateArray().Select(Port).ToArray();
        var scenarios = SpecCorpus.Scenarios(root, Corpus, Fixture);

        Assert.Equal(5, scenarios.Count);
        foreach (var scenario in scenarios.All())
        {
            var value = scenario.Value;
            var expected = FixtureAssertions.Wrap(
                value.GetProperty("expected"), $"{Corpus}/{Fixture} {scenario.Id} expected");
            var built = Build(root, value, actions, ports);
            SimConsumerRunResult? result = null;
            SimConsumerDivergenceException? divergence = null;
            try
            {
                result = built.Testkit.Run(GeneratedScenario(root, actions));
            }
            catch (SimConsumerDivergenceException error)
            {
                divergence = error;
            }

            var outcome = divergence?.Kind ?? "success";
            expected.AssertKey("outcome", outcome);
            if (result is not null)
            {
                expected.AssertKey("adapter_ids", result.AdapterIds.Cast<string?>());
                expected.AssertKey("checkpoint_steps", result.Checkpoints.Select(item => (long)item.Step));
                expected.TryAssertKeyWith("checkpoint_action_ids", want =>
                    want.AssertEqual(item => item.EnumerateArray().Select(value => value.GetString()),
                        result.Checkpoints.Select(item => item.ActionId)));
                expected.AssertKey("checkpoint_values", built.BaselineValues);
                expected.TryAssertKeyWith("observation_relation", want =>
                    want.AssertEqual(item => item.GetString(), ObservationRelation(result)));
                expected.TryAssertKeyWith("materialized_history_relation", want =>
                    want.AssertEqual(item => item.GetString(), HistoryRelation(built, actions.Length)));
                expected.TryAssertKeyWith("probe_relation", want =>
                    want.AssertEqual(item => item.GetString(), ProbeRelation(built)));

                var external = result.AdapterEvidence.SingleOrDefault(item =>
                    item.Kind == SimConsumerAdapterKind.ExternalProcess);
                expected.TryAssertKeyWith("external_adapter_id", want =>
                    want.AssertEqual(item => item.GetString(), external?.AdapterId));
                expected.TryAssertKeyWith("external_port", want =>
                    want.AssertEqual(item => item.GetString(), ExternalPortName(external!.ExternalPort)));
                expected.TryAssertKeyWith("external_protocol_id", want =>
                    want.AssertEqual(item => item.GetString(), external?.ProtocolId));
                expected.TryAssertKeyWith("external_reducer_id", want =>
                    want.AssertEqual(item => item.GetString(), external?.ReducerId));
                expected.TryAssertKeyWith("external_production_reducer_id", want =>
                    want.AssertEqual(item => item.GetString(), external?.ProductionReducerId));
            }
            else
            {
                Assert.NotNull(divergence);
                expected.AssertKey("step", (long)divergence.Step);
                expected.AssertKey("action_id", divergence.ActionId);
                expected.AssertKey("adapter_id", divergence.AdapterId);
                expected.TryAssertKeyWith("observation_id", want =>
                    want.AssertEqual(item => item.GetString(), divergence.ObservationId));
                expected.TryAssertKeyWith("expected_prefix_length", want =>
                    want.AssertEqual(item => item.GetInt32(), divergence.ExpectedPrefixLength));
                expected.TryAssertKeyWith("actual_prefix_length", want =>
                    want.AssertEqual(item => item.GetInt32(), divergence.ActualPrefixLength));
            }
            expected.Verify();
        }
    }

    [Fact]
    public void ConstructorRejectsInvalidTopologyBeforeCallbacks()
    {
        var pair = ValidPair();
        Assert.Throws<SimConsumerConformanceException>(() => new SimConsumerTestkit(new()
        {
            SimulationAdapterId = "memory",
            RequiredRealAdapters = [SimConsumerAdapterKind.Nats],
            Adapters = pair.Adapters,
        }));
        Assert.Equal(0, pair.ProbeCount());

        var deterministicStub = pair.Adapters[0].WithAdapterPorts(
            [new SimConsumerPort("state.store", "storage", SimConsumerPortDeterminism.Deterministic, true)]);
        Assert.Throws<SimConsumerConformanceException>(() => new SimConsumerTestkit(new()
        {
            SimulationAdapterId = "memory",
            RequiredRealAdapters = [SimConsumerAdapterKind.Postgres],
            Adapters = [deterministicStub, pair.Adapters[1]],
        }));
    }

    [Fact]
    public void RunRejectsInvalidSeedAndDuplicateActions()
    {
        var pair = ValidPair();
        var testkit = new SimConsumerTestkit(new()
        {
            SimulationAdapterId = "memory",
            RequiredRealAdapters = [SimConsumerAdapterKind.Postgres],
            Adapters = pair.Adapters,
        });
        var action = new SimConsumerAction("increment.0", "consumer", "counter.increment", "1", 1L);
        Assert.Throws<SimConsumerConformanceException>(() => testkit.Run(new(
            "consumer_simulation", "1", "ABC", [new("apply", action)])));
        Assert.Throws<SimConsumerConformanceException>(() => testkit.Run(new(
            "consumer_simulation", "1", new string('0', 64),
            [new("apply", action), new("apply", action)])));
    }

    private static BuiltKit Build(
        JsonElement root,
        JsonElement scenario,
        IReadOnlyList<SimConsumerAction> actions,
        IReadOnlyList<SimConsumerPort> ports)
    {
        var states = new Dictionary<string, AdapterState>(StringComparer.Ordinal);
        var adapters = new List<SimConsumerAdapter>();
        var baselineValues = new List<long>();
        var simulationId = scenario.GetProperty("simulation_adapter_id").GetString()!;
        foreach (var item in scenario.GetProperty("adapters").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var kind = AdapterKind(item.GetProperty("kind").GetString()!);
            var state = new AdapterState();
            states.Add(id, state);
            var executionMode = item.GetProperty("execution_mode").GetString()!;
            var historyMode = item.GetProperty("history_mode").GetString()!;
            var bias = item.GetProperty("delta_bias").GetInt64();
            var adapterPorts = ports.Select(port => port with
            {
                Stubbed = kind == SimConsumerAdapterKind.InMemory
                    && port.Id == "logical.clock"
                    && item.GetProperty("clock_stub").GetString() == "stubbed",
            }).ToArray();
            adapters.Add(new SimConsumerAdapter
            {
                Id = id,
                Kind = kind,
                ServiceId = item.GetProperty("service_id").GetString()!,
                ProtocolId = item.GetProperty("protocol_id").GetString()!,
                ReducerId = item.GetProperty("reducer_id").GetString()!,
                ProductionReducerId = item.GetProperty("production_reducer_id").GetString()!,
                ExternalPort = item.TryGetProperty("external_port", out var externalPort)
                    ? ExternalPort(externalPort.GetString()!) : SimConsumerExternalPortKind.None,
                Ports = adapterPorts,
                Probe = kind == SimConsumerAdapterKind.InMemory ? null : () => state.ProbeCount++,
                WorldEvidence = kind == SimConsumerAdapterKind.InMemory ? () => state.World! : null,
                Reset = () =>
                {
                    state.Value = 0;
                    state.History.Clear();
                    if (kind == SimConsumerAdapterKind.InMemory)
                        state.World = new SimConsumerWorldEvidence();
                },
                Apply = action =>
                {
                    state.Value += Convert.ToInt64(action.Payload) + bias;
                    if (kind == SimConsumerAdapterKind.InMemory)
                    {
                        if (executionMode == "sim_world") state.World!.Record(action.Id);
                    }
                    else if (historyMode == "exact")
                    {
                        state.History.Add(action);
                    }
                },
                Observe = () =>
                {
                    if (id == simulationId) baselineValues.Add(state.Value);
                    return new Dictionary<string, object?> { ["consumer.value"] = state.Value };
                },
                MaterializedHistory = kind == SimConsumerAdapterKind.InMemory
                    ? null : () => state.History.ToArray(),
            });
        }

        var requiredKinds = scenario.GetProperty("required_real_adapters").EnumerateArray()
            .Select(item => AdapterKind(item.GetString()!)).ToArray();
        var requiredExternal = scenario.GetProperty("required_external_processes").EnumerateArray()
            .Select(item => new SimConsumerExternalProcessSelection(
                item.GetProperty("adapter_id").GetString()!,
                ExternalPort(item.GetProperty("port").GetString()!))).ToArray();
        return new BuiltKit(
            new SimConsumerTestkit(new SimConsumerTestkitSpec
            {
                SimulationAdapterId = simulationId,
                RequiredRealAdapters = requiredKinds,
                RequiredExternalProcesses = requiredExternal,
                Adapters = adapters,
            }), states, baselineValues);
    }

    private static SimConsumerGeneratedScenario GeneratedScenario(
        JsonElement root,
        IReadOnlyList<SimConsumerAction> actions) => new(
            "consumer_simulation",
            root.GetProperty("generator").GetProperty("version").GetString()!,
            root.GetProperty("seed").GetString()!,
            actions.Select(action => new SimConsumerGeneratedAction("apply", action)).ToArray());

    private static SimConsumerAction Action(JsonElement item) => new(
        item.GetProperty("id").GetString()!,
        item.GetProperty("actor_id").GetString()!,
        item.GetProperty("kind").GetString()!,
        item.GetProperty("version").GetString()!,
        Value(item.GetProperty("payload")),
        item.TryGetProperty("cause_id", out var cause) ? cause.GetString()! : "");

    private static object? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer : value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray().Select(Value).ToArray(),
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(
            item => item.Name, item => Value(item.Value), StringComparer.Ordinal),
        _ => throw new InvalidOperationException($"Unsupported JSON value {value.ValueKind}."),
    };

    private static SimConsumerPort Port(JsonElement item) => new(
        item.GetProperty("id").GetString()!,
        item.GetProperty("kind").GetString()!,
        item.GetProperty("determinism").GetString() == "deterministic"
            ? SimConsumerPortDeterminism.Deterministic
            : SimConsumerPortDeterminism.Nondeterministic);

    private static SimConsumerAdapterKind AdapterKind(string value) => value switch
    {
        "in_memory" => SimConsumerAdapterKind.InMemory,
        "postgres" => SimConsumerAdapterKind.Postgres,
        "nats" => SimConsumerAdapterKind.Nats,
        "external_process" => SimConsumerAdapterKind.ExternalProcess,
        _ => throw new InvalidOperationException($"Unknown adapter kind '{value}'."),
    };

    private static SimConsumerExternalPortKind ExternalPort(string value) => value switch
    {
        "cli" => SimConsumerExternalPortKind.Cli,
        "filesystem" => SimConsumerExternalPortKind.Filesystem,
        "local_socket" => SimConsumerExternalPortKind.LocalSocket,
        "editor_replica" => SimConsumerExternalPortKind.EditorReplica,
        _ => throw new InvalidOperationException($"Unknown external port '{value}'."),
    };

    private static string ExternalPortName(SimConsumerExternalPortKind value) => value switch
    {
        SimConsumerExternalPortKind.Cli => "cli",
        SimConsumerExternalPortKind.Filesystem => "filesystem",
        SimConsumerExternalPortKind.LocalSocket => "local_socket",
        SimConsumerExternalPortKind.EditorReplica => "editor_replica",
        _ => "",
    };

    private static string ObservationRelation(SimConsumerRunResult result) =>
        result.Checkpoints.All(checkpoint => checkpoint.ObservationDigests.Values.Distinct().Count() == 1)
            ? "all_equal_at_every_checkpoint" : "diverged";

    private static string HistoryRelation(BuiltKit built, int expectedCount) =>
        built.States.Values.Where(state => state.ProbeCount > 0)
            .All(state => state.History.Count == expectedCount)
                ? "exact_prefix_at_every_checkpoint" : "mismatch";

    private static string ProbeRelation(BuiltKit built) =>
        built.States.Values.Where(state => state.World is null).All(state => state.ProbeCount == 1)
            ? "every_real_adapter_once" : "probe_mismatch";

    private static ValidPairResult ValidPair()
    {
        var probeCount = 0;
        var world = new SimConsumerWorldEvidence();
        var ports = new[]
        {
            new SimConsumerPort("state.store", "storage", SimConsumerPortDeterminism.Deterministic),
        };
        var memory = new SimConsumerAdapter
        {
            Id = "memory",
            Kind = SimConsumerAdapterKind.InMemory,
            ProtocolId = "counter.protocol.v1",
            ReducerId = "counter.reducer.v1",
            ProductionReducerId = "counter.reducer.v1",
            Ports = ports,
            WorldEvidence = () => world,
            Reset = () => { },
            Apply = action => world.Record(action.Id),
            Observe = () => new Dictionary<string, object?> { ["consumer.value"] = 0L },
        };
        var postgres = new SimConsumerAdapter
        {
            Id = "postgres.integration",
            Kind = SimConsumerAdapterKind.Postgres,
            ProtocolId = "counter.protocol.v1",
            ReducerId = "counter.reducer.v1",
            ProductionReducerId = "counter.reducer.v1",
            ServiceId = "postgres.service",
            Ports = ports,
            Probe = () => probeCount++,
            Reset = () => { },
            Apply = _ => { },
            Observe = () => new Dictionary<string, object?> { ["consumer.value"] = 0L },
            MaterializedHistory = () => [],
        };
        return new ValidPairResult([memory, postgres], () => probeCount);
    }

    private sealed class AdapterState
    {
        public long Value { get; set; }
        public int ProbeCount { get; set; }
        public SimConsumerWorldEvidence? World { get; set; }
        public List<SimConsumerAction> History { get; } = [];
    }

    private sealed record BuiltKit(
        SimConsumerTestkit Testkit,
        IReadOnlyDictionary<string, AdapterState> States,
        IReadOnlyList<long> BaselineValues);

    private sealed record ValidPairResult(
        IReadOnlyList<SimConsumerAdapter> Adapters,
        Func<int> ProbeCount);
}

file static class SimConsumerAdapterTestExtensions
{
    public static SimConsumerAdapter WithAdapterPorts(
        this SimConsumerAdapter adapter,
        IReadOnlyList<SimConsumerPort> ports) => new()
        {
            Id = adapter.Id,
            Kind = adapter.Kind,
            ProductionReducerId = adapter.ProductionReducerId,
            ProtocolId = adapter.ProtocolId,
            ReducerId = adapter.ReducerId,
            ServiceId = adapter.ServiceId,
            ExternalPort = adapter.ExternalPort,
            Ports = ports,
            Probe = adapter.Probe,
            WorldEvidence = adapter.WorldEvidence,
            Reset = adapter.Reset,
            Apply = adapter.Apply,
            Observe = adapter.Observe,
            MaterializedHistory = adapter.MaterializedHistory,
        };
}
