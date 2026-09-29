using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Lazily;

/// <summary>Identifies the execution boundary behind a consumer adapter.</summary>
public enum SimConsumerAdapterKind
{
    /// <summary>No adapter kind was declared.</summary>
    Unknown,
    /// <summary>An in-process deterministic simulation adapter.</summary>
    InMemory,
    /// <summary>A real PostgreSQL integration boundary.</summary>
    Postgres,
    /// <summary>A real NATS integration boundary.</summary>
    Nats,
    /// <summary>An independently implemented external consumer process.</summary>
    ExternalProcess,
}

/// <summary>Identifies the public port used to reach an external process.</summary>
public enum SimConsumerExternalPortKind
{
    /// <summary>No external-process port was declared.</summary>
    None,
    /// <summary>A command-line interface.</summary>
    Cli,
    /// <summary>A filesystem exchange.</summary>
    Filesystem,
    /// <summary>A local socket.</summary>
    LocalSocket,
    /// <summary>An editor replica boundary.</summary>
    EditorReplica,
}

/// <summary>Declares whether a narrow consumer port may be stubbed in simulation.</summary>
public enum SimConsumerPortDeterminism
{
    /// <summary>No determinism contract was declared.</summary>
    Unknown,
    /// <summary>The port is deterministic and cannot be stubbed.</summary>
    Deterministic,
    /// <summary>The port is nondeterministic and may be stubbed only in memory.</summary>
    Nondeterministic,
}

/// <summary>One narrow boundary shared by every adapter.</summary>
public sealed record SimConsumerPort(
    string Id,
    string Kind,
    SimConsumerPortDeterminism Determinism,
    bool Stubbed = false);

/// <summary>Selects an external consumer by stable identity and port.</summary>
public sealed record SimConsumerExternalProcessSelection(
    string AdapterId,
    SimConsumerExternalPortKind Port);

/// <summary>One immutable action in a materialized generated history.</summary>
public sealed record SimConsumerAction(
    string Id,
    string ActorId,
    string Kind,
    string Version,
    object? Payload,
    string CauseId = "");

/// <summary>An action paired with the generator command that produced it.</summary>
public sealed record SimConsumerGeneratedAction(string Command, SimConsumerAction Action);

/// <summary>A deterministic generated scenario accepted by <see cref="SimConsumerTestkit"/>.</summary>
public sealed record SimConsumerGeneratedScenario(
    string GeneratorName,
    string GeneratorVersion,
    string SeedHex,
    IReadOnlyList<SimConsumerGeneratedAction> Actions,
    int ScenarioIndex = 0,
    IReadOnlyList<string>? CoverageLabels = null);

/// <summary>One immutable entry proving an action executed through the simulation world.</summary>
public sealed record SimConsumerTraceEntry(string ActionId, string Kind);

/// <summary>
/// Narrow simulation-world evidence: stable reference identity, step count, and action trace.
/// </summary>
public sealed class SimConsumerWorldEvidence
{
    private readonly List<SimConsumerTraceEntry> _traceEntries = [];

    /// <summary>Creates evidence with a stable reference identity.</summary>
    public SimConsumerWorldEvidence(object? identity = null) => Identity = identity ?? new object();

    /// <summary>The reference identity that must remain stable for the whole run.</summary>
    public object Identity { get; }

    /// <summary>How many action steps have executed through this world.</summary>
    public ulong StepCount { get; private set; }

    /// <summary>A snapshot of immutable trace entries.</summary>
    public IReadOnlyList<SimConsumerTraceEntry> TraceEntries => _traceEntries.ToArray();

    /// <summary>Records one action execution and advances the step count.</summary>
    public void Record(string actionId, string kind = "action_applied")
    {
        Validation.RequireId(actionId, "trace action id");
        Validation.RequireId(kind, "trace kind");
        if (!kind.StartsWith("action_", StringComparison.Ordinal))
            throw new ArgumentException("Trace kind must start with 'action_'.", nameof(kind));
        _traceEntries.Add(new SimConsumerTraceEntry(actionId, kind));
        StepCount++;
    }
}

/// <summary>An interchangeable consumer execution boundary.</summary>
public sealed class SimConsumerAdapter
{
    /// <summary>The stable adapter identity.</summary>
    public required string Id { get; init; }
    /// <summary>The boundary kind.</summary>
    public required SimConsumerAdapterKind Kind { get; init; }
    /// <summary>The production reducer used by in-memory, PostgreSQL, and NATS adapters.</summary>
    public string ProductionReducerId { get; init; } = "";
    /// <summary>The shared consumer protocol identity.</summary>
    public required string ProtocolId { get; init; }
    /// <summary>The stable reducer identity.</summary>
    public required string ReducerId { get; init; }
    /// <summary>The real service identity, empty for the in-memory adapter.</summary>
    public string ServiceId { get; init; } = "";
    /// <summary>The selected external-process port, or <see cref="SimConsumerExternalPortKind.None"/>.</summary>
    public SimConsumerExternalPortKind ExternalPort { get; init; }
    /// <summary>The complete narrow-port contract.</summary>
    public required IReadOnlyList<SimConsumerPort> Ports { get; init; }
    /// <summary>Probes a real service before reset.</summary>
    public Action? Probe { get; init; }
    /// <summary>Returns narrow evidence for the in-memory world.</summary>
    public Func<SimConsumerWorldEvidence>? WorldEvidence { get; init; }
    /// <summary>Resets the adapter before replay.</summary>
    public required Action Reset { get; init; }
    /// <summary>Applies one cloned action.</summary>
    public required Action<SimConsumerAction> Apply { get; init; }
    /// <summary>Returns the current canonical observation set.</summary>
    public required Func<IReadOnlyDictionary<string, object?>> Observe { get; init; }
    /// <summary>Returns the exact accepted action history for a real adapter.</summary>
    public Func<IReadOnlyList<SimConsumerAction>>? MaterializedHistory { get; init; }
}

/// <summary>The explicitly selected topology for a consumer simulation testkit.</summary>
public sealed class SimConsumerTestkitSpec
{
    /// <summary>The stable identity of the in-memory adapter.</summary>
    public required string SimulationAdapterId { get; init; }
    /// <summary>The explicitly selected PostgreSQL and NATS kinds.</summary>
    public IReadOnlyList<SimConsumerAdapterKind> RequiredRealAdapters { get; init; } = [];
    /// <summary>The explicitly selected external consumers.</summary>
    public IReadOnlyList<SimConsumerExternalProcessSelection> RequiredExternalProcesses { get; init; } = [];
    /// <summary>All configured adapters.</summary>
    public required IReadOnlyList<SimConsumerAdapter> Adapters { get; init; }
}

/// <summary>The stable identities validated for one adapter.</summary>
public sealed record SimConsumerAdapterEvidence(
    string AdapterId,
    SimConsumerAdapterKind Kind,
    string ServiceId,
    SimConsumerExternalPortKind ExternalPort,
    string ProtocolId,
    string ReducerId,
    string ProductionReducerId);

/// <summary>One post-action canonical comparison checkpoint.</summary>
public sealed record SimConsumerCheckpoint(
    ulong Step,
    string ActionId,
    IReadOnlyDictionary<string, string> ObservationDigests);

/// <summary>The successful evidence returned by one testkit run.</summary>
public sealed record SimConsumerRunResult(
    string ScenarioDigest,
    IReadOnlyList<string> AdapterIds,
    IReadOnlyList<SimConsumerAdapterEvidence> AdapterEvidence,
    object SimulationWorldIdentity,
    ulong SimulationWorldStepCount,
    IReadOnlyList<SimConsumerTraceEntry> SimulationTraceEntries,
    IReadOnlyList<SimConsumerCheckpoint> Checkpoints);

/// <summary>A fail-closed consumer simulation configuration or execution error.</summary>
public class SimConsumerConformanceException : InvalidOperationException
{
    /// <summary>Creates an empty conformance exception.</summary>
    public SimConsumerConformanceException() { }
    /// <summary>Creates a conformance exception with a message.</summary>
    public SimConsumerConformanceException(string message) : base(message) { }
    /// <summary>Creates a conformance exception with a message and cause.</summary>
    public SimConsumerConformanceException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Localizes the first adapter divergence after an action.</summary>
public sealed class SimConsumerDivergenceException : SimConsumerConformanceException
{
    internal SimConsumerDivergenceException(
        ulong step,
        string actionId,
        string baselineAdapterId,
        string adapterId,
        string kind,
        string observationId = "",
        int? expectedPrefixLength = null,
        int? actualPrefixLength = null)
        : base($"Step {step} action '{actionId}' adapter '{adapterId}' differs from "
            + $"'{baselineAdapterId}': {kind}"
            + (observationId.Length == 0 ? "" : $" '{observationId}'"))
    {
        Step = step;
        ActionId = actionId;
        BaselineAdapterId = baselineAdapterId;
        AdapterId = adapterId;
        Kind = kind;
        ObservationId = observationId;
        ExpectedPrefixLength = expectedPrefixLength;
        ActualPrefixLength = actualPrefixLength;
    }

    /// <summary>The one-based failed step.</summary>
    public ulong Step { get; }
    /// <summary>The action at the failed step.</summary>
    public string ActionId { get; }
    /// <summary>The in-memory baseline adapter identity.</summary>
    public string BaselineAdapterId { get; }
    /// <summary>The adapter that diverged.</summary>
    public string AdapterId { get; }
    /// <summary>The stable divergence category.</summary>
    public string Kind { get; }
    /// <summary>The observation identity, when the divergence is observational.</summary>
    public string ObservationId { get; }
    /// <summary>The required materialized-history prefix length, when applicable.</summary>
    public int? ExpectedPrefixLength { get; }
    /// <summary>The observed materialized-history prefix length, when applicable.</summary>
    public int? ActualPrefixLength { get; }
}

/// <summary>
/// Replays generated actions through one narrow simulation world and selected real boundaries.
/// </summary>
public sealed class SimConsumerTestkit
{
    private readonly SimConsumerAdapter[] _adapters;
    private readonly int _baselineIndex;

    /// <summary>Validates the complete topology before any callback can run.</summary>
    public SimConsumerTestkit(SimConsumerTestkitSpec spec)
    {
        if (spec is null) throw new ArgumentNullException(nameof(spec));
        Validation.RequireId(spec.SimulationAdapterId, "simulation adapter id");
        if (spec.RequiredRealAdapters.Count == 0 && spec.RequiredExternalProcesses.Count == 0)
            Validation.Fail("Select at least one real adapter or external process.");
        if (spec.Adapters.Count < 2)
            Validation.Fail("Testkit needs an in-memory adapter and at least one real adapter.");

        var requiredKinds = new HashSet<SimConsumerAdapterKind>();
        foreach (var kind in spec.RequiredRealAdapters)
        {
            if (kind is not (SimConsumerAdapterKind.Postgres or SimConsumerAdapterKind.Nats))
                Validation.Fail($"Required kind '{kind}' is not PostgreSQL or NATS.");
            if (!requiredKinds.Add(kind)) Validation.Fail($"Duplicate required kind '{kind}'.");
        }

        var requiredExternal = new Dictionary<string, SimConsumerExternalPortKind>(StringComparer.Ordinal);
        foreach (var selection in spec.RequiredExternalProcesses)
        {
            Validation.RequireId(selection.AdapterId, "external selection adapter id");
            if (!Validation.ValidExternalPort(selection.Port))
                Validation.Fail("External selection must name a supported port.");
            if (!requiredExternal.TryAdd(selection.AdapterId, selection.Port))
                Validation.Fail($"Duplicate external selection '{selection.AdapterId}'.");
        }

        _adapters = [.. spec.Adapters.OrderBy(adapter => adapter.Id, StringComparer.Ordinal)];
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var presentKinds = new HashSet<SimConsumerAdapterKind>();
        string? protocolId = null;
        string? productionReducerId = null;
        string[]? portContract = null;
        for (var index = 0; index < _adapters.Length; index++)
        {
            var adapter = _adapters[index];
            ValidateAdapter(adapter);
            if (!seenIds.Add(adapter.Id)) Validation.Fail($"Duplicate adapter id '{adapter.Id}'.");
            presentKinds.Add(adapter.Kind);

            if (adapter.Kind is SimConsumerAdapterKind.Postgres or SimConsumerAdapterKind.Nats)
            {
                if (!requiredKinds.Contains(adapter.Kind))
                    Validation.Fail($"Real adapter '{adapter.Id}' was not explicitly selected.");
            }
            else if (adapter.Kind == SimConsumerAdapterKind.ExternalProcess)
            {
                if (!requiredExternal.TryGetValue(adapter.Id, out var selected))
                    Validation.Fail($"External adapter '{adapter.Id}' was not explicitly selected.");
                if (selected != adapter.ExternalPort)
                    Validation.Fail($"External adapter '{adapter.Id}' uses the wrong selected port.");
            }

            if (requiredExternal.ContainsKey(adapter.Id)
                && adapter.Kind != SimConsumerAdapterKind.ExternalProcess)
                Validation.Fail($"External selection '{adapter.Id}' refers to the wrong adapter kind.");

            if (adapter.Kind != SimConsumerAdapterKind.ExternalProcess)
            {
                productionReducerId ??= adapter.ProductionReducerId;
                if (!string.Equals(productionReducerId, adapter.ProductionReducerId, StringComparison.Ordinal))
                    Validation.Fail($"Adapter '{adapter.Id}' does not use the shared production reducer.");
            }
            protocolId ??= adapter.ProtocolId;
            if (!string.Equals(protocolId, adapter.ProtocolId, StringComparison.Ordinal))
                Validation.Fail($"Adapter '{adapter.Id}' does not use the shared protocol.");

            var contract = adapter.Ports.Select(Validation.PortContract)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            portContract ??= contract;
            if (!portContract.SequenceEqual(contract, StringComparer.Ordinal))
                Validation.Fail($"Adapter '{adapter.Id}' does not expose the shared narrow-port contract.");
        }

        foreach (var kind in requiredKinds)
            if (!presentKinds.Contains(kind)) Validation.Fail($"Required adapter kind '{kind}' is missing.");
        foreach (var id in requiredExternal.Keys)
            if (!seenIds.Contains(id)) Validation.Fail($"Required external adapter '{id}' is missing.");

        _baselineIndex = Array.FindIndex(_adapters, adapter => adapter.Id == spec.SimulationAdapterId);
        if (_baselineIndex < 0) Validation.Fail($"Simulation adapter '{spec.SimulationAdapterId}' is missing.");
        if (_adapters[_baselineIndex].Kind != SimConsumerAdapterKind.InMemory)
            Validation.Fail($"Simulation adapter '{spec.SimulationAdapterId}' must be in-memory.");
    }

    /// <summary>Runs one generated scenario and returns canonical checkpoint evidence.</summary>
    public SimConsumerRunResult Run(SimConsumerGeneratedScenario scenario)
    {
        if (scenario is null) throw new ArgumentNullException(nameof(scenario));
        var scenarioDigest = ValidateScenario(scenario);
        SimConsumerWorldEvidence? resetWorld = null;
        foreach (var adapter in _adapters)
        {
            try
            {
                if (Validation.IsReal(adapter.Kind)) adapter.Probe!();
                adapter.Reset();
            }
            catch (Exception error)
            {
                throw new SimConsumerConformanceException($"Failed to initialize adapter '{adapter.Id}'.", error);
            }
            if (adapter.Kind == SimConsumerAdapterKind.InMemory)
                resetWorld = adapter.WorldEvidence!()
                    ?? throw new SimConsumerConformanceException(
                        $"Reset adapter '{adapter.Id}' did not create world evidence.");
            else
                ValidateHistory(adapter, [], 0, "", _adapters[_baselineIndex].Id);
        }

        var checkpoints = new List<SimConsumerCheckpoint>(scenario.Actions.Count);
        for (var actionIndex = 0; actionIndex < scenario.Actions.Count; actionIndex++)
        {
            var generated = scenario.Actions[actionIndex];
            var step = checked((ulong)actionIndex + 1);
            var observations = new IReadOnlyDictionary<string, object?>[_adapters.Length];
            var digests = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var adapterIndex = 0; adapterIndex < _adapters.Length; adapterIndex++)
            {
                var adapter = _adapters[adapterIndex];
                SimConsumerWorldEvidence? world = null;
                ulong stepsBefore = 0;
                int traceBefore = 0;
                if (adapter.Kind == SimConsumerAdapterKind.InMemory)
                {
                    world = adapter.WorldEvidence!();
                    if (!ReferenceEquals(resetWorld, world))
                        throw Bypass(step, generated.Action.Id, adapter.Id);
                    stepsBefore = world.StepCount;
                    traceBefore = world.TraceEntries.Count;
                }

                try { adapter.Apply(CloneAction(generated.Action)); }
                catch (Exception error)
                {
                    throw new SimConsumerConformanceException(
                        $"Step {step} action '{generated.Action.Id}' failed in adapter '{adapter.Id}'.", error);
                }

                if (world is not null)
                {
                    var current = adapter.WorldEvidence!();
                    if (!ReferenceEquals(current, world) || !ReferenceEquals(current.Identity, world.Identity)
                        || current.StepCount <= stepsBefore
                        || !current.TraceEntries.Skip(traceBefore).Any(entry =>
                            entry.ActionId == generated.Action.Id
                            && entry.Kind.StartsWith("action_", StringComparison.Ordinal)))
                        throw Bypass(step, generated.Action.Id, adapter.Id);
                }
                else
                {
                    ValidateHistory(
                        adapter,
                        scenario.Actions.Take(actionIndex + 1).Select(item => item.Action).ToArray(),
                        step,
                        generated.Action.Id,
                        _adapters[_baselineIndex].Id);
                }

                IReadOnlyDictionary<string, object?> observed;
                try { observed = FreezeObservation(adapter.Observe(), adapter.Id); }
                catch (SimConsumerConformanceException) { throw; }
                catch (Exception error)
                {
                    throw new SimConsumerConformanceException(
                        $"Step {step} action '{generated.Action.Id}' could not observe adapter '{adapter.Id}'.",
                        error);
                }
                observations[adapterIndex] = observed;
                digests.Add(adapter.Id, ReplayEncoding.Digest(observed));
            }

            var baseline = observations[_baselineIndex];
            foreach (var pair in _adapters.Select((adapter, index) => (adapter, index)))
                if (pair.index != _baselineIndex)
                    CompareObservations(step, generated.Action.Id, _adapters[_baselineIndex].Id,
                        pair.adapter.Id, baseline, observations[pair.index]);
            checkpoints.Add(new SimConsumerCheckpoint(step, generated.Action.Id,
                new ReadOnlyDictionary<string, string>(digests)));
        }

        var finalWorld = _adapters[_baselineIndex].WorldEvidence!();
        return new SimConsumerRunResult(
            scenarioDigest,
            _adapters.Select(adapter => adapter.Id).ToArray(),
            _adapters.Select(adapter => new SimConsumerAdapterEvidence(
                adapter.Id, adapter.Kind, adapter.ServiceId, adapter.ExternalPort,
                adapter.ProtocolId, adapter.ReducerId, adapter.ProductionReducerId)).ToArray(),
            finalWorld.Identity,
            finalWorld.StepCount,
            finalWorld.TraceEntries,
            checkpoints);
    }

    private static void ValidateAdapter(SimConsumerAdapter adapter)
    {
        if (adapter is null) throw new ArgumentNullException(nameof(adapter));
        Validation.RequireId(adapter.Id, "adapter id");
        Validation.RequireId(adapter.ProtocolId, $"adapter '{adapter.Id}' protocol id");
        Validation.RequireId(adapter.ReducerId, $"adapter '{adapter.Id}' reducer id");
        if (!Enum.IsDefined(typeof(SimConsumerAdapterKind), adapter.Kind)
            || adapter.Kind == SimConsumerAdapterKind.Unknown)
            Validation.Fail($"Adapter '{adapter.Id}' has an unknown kind.");
        if (adapter.Reset is null || adapter.Apply is null || adapter.Observe is null)
            Validation.Fail($"Adapter '{adapter.Id}' needs reset, apply, and observe callbacks.");
        if (adapter.Ports is null || adapter.Ports.Count == 0)
            Validation.Fail($"Adapter '{adapter.Id}' needs at least one narrow port.");

        var seenPorts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in adapter.Ports)
        {
            Validation.RequireId(port.Id, $"adapter '{adapter.Id}' port id");
            Validation.RequireId(port.Kind, $"adapter '{adapter.Id}' port kind");
            if (port.Determinism == SimConsumerPortDeterminism.Unknown
                || !Enum.IsDefined(typeof(SimConsumerPortDeterminism), port.Determinism))
                Validation.Fail($"Adapter '{adapter.Id}' port '{port.Id}' must declare determinism.");
            if (!seenPorts.Add(port.Id))
                Validation.Fail($"Adapter '{adapter.Id}' has duplicate port '{port.Id}'.");
            if (port.Stubbed && port.Determinism != SimConsumerPortDeterminism.Nondeterministic)
                Validation.Fail($"Adapter '{adapter.Id}' stubs deterministic port '{port.Id}'.");
            if (Validation.IsReal(adapter.Kind) && port.Stubbed)
                Validation.Fail($"Real adapter '{adapter.Id}' cannot stub port '{port.Id}'.");
        }

        if (adapter.Kind == SimConsumerAdapterKind.InMemory)
        {
            Validation.RequireId(adapter.ProductionReducerId, $"adapter '{adapter.Id}' production reducer id");
            if (adapter.WorldEvidence is null) Validation.Fail($"Adapter '{adapter.Id}' needs world evidence.");
            if (adapter.ServiceId.Length != 0 || adapter.Probe is not null
                || adapter.MaterializedHistory is not null
                || adapter.ExternalPort != SimConsumerExternalPortKind.None)
                Validation.Fail($"In-memory adapter '{adapter.Id}' carries real-adapter fields.");
            return;
        }

        Validation.RequireId(adapter.ServiceId, $"adapter '{adapter.Id}' service id");
        if (adapter.Probe is null || adapter.MaterializedHistory is null)
            Validation.Fail($"Real adapter '{adapter.Id}' needs probe and materialized-history callbacks.");
        if (adapter.WorldEvidence is not null)
            Validation.Fail($"Real adapter '{adapter.Id}' cannot expose world evidence.");
        if (adapter.Kind == SimConsumerAdapterKind.ExternalProcess)
        {
            if (!Validation.ValidExternalPort(adapter.ExternalPort))
                Validation.Fail($"External adapter '{adapter.Id}' needs a supported port.");
            if (adapter.ProductionReducerId.Length != 0)
                Validation.Fail($"External adapter '{adapter.Id}' cannot claim a production reducer.");
        }
        else
        {
            Validation.RequireId(adapter.ProductionReducerId, $"adapter '{adapter.Id}' production reducer id");
            if (adapter.ReducerId != adapter.ProductionReducerId)
                Validation.Fail($"Adapter '{adapter.Id}' reducer differs from its production reducer.");
            if (adapter.ExternalPort != SimConsumerExternalPortKind.None)
                Validation.Fail($"Adapter '{adapter.Id}' cannot claim an external port.");
        }
    }

    private static string ValidateScenario(SimConsumerGeneratedScenario scenario)
    {
        Validation.RequireId(scenario.GeneratorName, "scenario generator name");
        if (scenario.GeneratorVersion.Length == 0) Validation.Fail("Scenario generator version is empty.");
        if (scenario.SeedHex.Length != 64
            || scenario.SeedHex.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
            Validation.Fail("Scenario seed must be 32 bytes of lowercase hexadecimal.");
        if (scenario.Actions.Count == 0) Validation.Fail("Scenario must contain at least one action.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var generated in scenario.Actions)
        {
            Validation.RequireId(generated.Command, "generator command");
            ValidateAction(generated.Action);
            if (!seen.Add(generated.Action.Id))
                Validation.Fail($"Duplicate action id '{generated.Action.Id}'.");
            if (generated.Action.CauseId.Length != 0 && !seen.Contains(generated.Action.CauseId))
                Validation.Fail($"Action '{generated.Action.Id}' has unresolved cause '{generated.Action.CauseId}'.");
        }
        return ReplayEncoding.Digest(ScenarioValue(scenario));
    }

    private static void ValidateAction(SimConsumerAction action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        Validation.RequireId(action.Id, "action id");
        Validation.RequireId(action.ActorId, "action actor id");
        Validation.RequireId(action.Kind, "action kind");
        if (action.Version.Length == 0) Validation.Fail("Action version is empty.");
        if (action.CauseId.Length != 0) Validation.RequireId(action.CauseId, "action cause id");
        _ = ReplayEncoding.Bytes(ActionValue(action));
    }

    private static IReadOnlyDictionary<string, object?> FreezeObservation(
        IReadOnlyDictionary<string, object?> values,
        string adapterId)
    {
        if (values is null || values.Count == 0)
            Validation.Fail($"Adapter '{adapterId}' returned no observations.");
        var frozen = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            Validation.RequireId(pair.Key, $"adapter '{adapterId}' observation id");
            frozen.Add(pair.Key, CloneValue(pair.Value));
        }
        _ = ReplayEncoding.Bytes(frozen);
        return new ReadOnlyDictionary<string, object?>(frozen);
    }

    private static void CompareObservations(
        ulong step,
        string actionId,
        string baselineId,
        string adapterId,
        IReadOnlyDictionary<string, object?> baseline,
        IReadOnlyDictionary<string, object?> actual)
    {
        if (baseline.Count != actual.Count)
            throw new SimConsumerDivergenceException(
                step, actionId, baselineId, adapterId, "observation_count");
        foreach (var pair in baseline.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(pair.Key, out var actualValue))
                throw new SimConsumerDivergenceException(
                    step, actionId, baselineId, adapterId, "missing_observation", pair.Key);
            if (!ReplayEncoding.Bytes(pair.Value).SequenceEqual(ReplayEncoding.Bytes(actualValue)))
                throw new SimConsumerDivergenceException(
                    step, actionId, baselineId, adapterId, "observation_divergence", pair.Key);
        }
    }

    private static void ValidateHistory(
        SimConsumerAdapter adapter,
        IReadOnlyList<SimConsumerAction> expected,
        ulong step,
        string actionId,
        string baselineId)
    {
        IReadOnlyList<SimConsumerAction> history;
        try { history = adapter.MaterializedHistory!(); }
        catch (Exception error)
        {
            throw new SimConsumerConformanceException(
                $"Adapter '{adapter.Id}' could not return materialized history.", error);
        }
        if (history.Count != expected.Count)
            throw new SimConsumerDivergenceException(
                step, actionId, baselineId, adapter.Id, "materialized_history_mismatch", "",
                expected.Count, history.Count);
        for (var index = 0; index < expected.Count; index++)
            if (!ReplayEncoding.Bytes(ActionValue(expected[index]))
                .SequenceEqual(ReplayEncoding.Bytes(ActionValue(history[index]))))
                throw new SimConsumerDivergenceException(
                    step, actionId, baselineId, adapter.Id, "materialized_history_mismatch", "",
                    expected.Count, history.Count);
    }

    private static SimConsumerDivergenceException Bypass(ulong step, string actionId, string adapterId) =>
        new(step, actionId, adapterId, adapterId, "simulation_world_bypass");

    private static SimConsumerAction CloneAction(SimConsumerAction action) => action with
    {
        Payload = CloneValue(action.Payload),
    };

    private static object? CloneValue(object? value)
    {
        if (value is null or string or bool or byte or sbyte or short or ushort or int or uint
            or long or ulong or float or double or decimal) return value;
        if (value is byte[] bytes) return bytes.ToArray();
        if (value is IDictionary dictionary)
        {
            var copy = new Dictionary<object, object?>();
            foreach (DictionaryEntry item in dictionary)
                copy.Add(item.Key, CloneValue(item.Value));
            return copy;
        }
        if (value is IEnumerable sequence)
            return sequence.Cast<object?>().Select(CloneValue).ToArray();
        _ = ReplayEncoding.Bytes(value);
        return value;
    }

    private static IReadOnlyDictionary<string, object?> ActionValue(SimConsumerAction action) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = action.Id,
            ["actor_id"] = action.ActorId,
            ["kind"] = action.Kind,
            ["version"] = action.Version,
            ["payload"] = action.Payload,
            ["cause_id"] = action.CauseId,
        };

    private static IReadOnlyDictionary<string, object?> ScenarioValue(SimConsumerGeneratedScenario scenario) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["generator_name"] = scenario.GeneratorName,
            ["generator_version"] = scenario.GeneratorVersion,
            ["scenario_index"] = scenario.ScenarioIndex,
            ["seed_hex"] = scenario.SeedHex,
            ["actions"] = scenario.Actions.Select(generated =>
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["command"] = generated.Command,
                    ["action"] = ActionValue(generated.Action),
                }).ToArray(),
            ["coverage_labels"] = scenario.CoverageLabels ?? [],
        };
}

internal static class Validation
{
    internal static void RequireId(string? value, string name)
    {
        if (value is null || value.Length is < 1 or > 128
            || value[0] is not (>= 'a' and <= 'z')
            || value.Skip(1).Any(character => character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9') and not ('.' or '_' or ':' or '-')))
            Fail($"{name} must be a stable id.");
    }

    [DoesNotReturn]
    internal static void Fail(string message) => throw new SimConsumerConformanceException(message);

    internal static bool IsReal(SimConsumerAdapterKind kind) =>
        kind is SimConsumerAdapterKind.Postgres
            or SimConsumerAdapterKind.Nats
            or SimConsumerAdapterKind.ExternalProcess;

    internal static bool ValidExternalPort(SimConsumerExternalPortKind port) =>
        port is SimConsumerExternalPortKind.Cli
            or SimConsumerExternalPortKind.Filesystem
            or SimConsumerExternalPortKind.LocalSocket
            or SimConsumerExternalPortKind.EditorReplica;

    internal static string PortContract(SimConsumerPort port) =>
        string.Join('\0', port.Id, port.Kind, DeterminismName(port.Determinism));

    private static string DeterminismName(SimConsumerPortDeterminism determinism) => determinism switch
    {
        SimConsumerPortDeterminism.Deterministic => "deterministic",
        SimConsumerPortDeterminism.Nondeterministic => "nondeterministic",
        _ => Convert.ToInt32(determinism, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
    };
}
