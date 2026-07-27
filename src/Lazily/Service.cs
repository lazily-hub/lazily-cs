namespace Lazily;

/// <summary>The aggregate state of named liveness probes.</summary>
public enum HealthState
{
    /// <summary>Every probe is up.</summary>
    Healthy,

    /// <summary>A non-critical probe is down.</summary>
    Degraded,

    /// <summary>A critical probe is down.</summary>
    Unhealthy,
}

/// <summary>A reactive aggregate of named liveness probes.</summary>
public sealed class HealthCell
{
    private readonly Dictionary<string, (bool Up, bool Critical)> _probes =
        new(StringComparer.Ordinal);

    /// <summary>Creates an initially healthy aggregate.</summary>
    public HealthCell(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HealthStateCell = context.Source(HealthState.Healthy);
    }

    /// <summary>The reactive aggregate health state.</summary>
    public Source<HealthState> HealthStateCell { get; }

    /// <summary>The current aggregate health state.</summary>
    public HealthState Health => HealthStateCell.Get();

    /// <summary>Sets one named liveness probe.</summary>
    public void Set(string name, bool up, bool critical)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _probes[name] = (up, critical);
        HealthStateCell.Set(Evaluate());
    }

    private HealthState Evaluate()
    {
        var anyDown = false;
        foreach (var (up, critical) in _probes.Values)
        {
            if (!up && critical) return HealthState.Unhealthy;
            if (!up) anyDown = true;
        }
        return anyDown ? HealthState.Degraded : HealthState.Healthy;
    }
}

/// <summary>A reactive conjunction of named readiness conditions.</summary>
public sealed class ReadinessCell
{
    private readonly Dictionary<string, bool> _conditions = new(StringComparer.Ordinal);

    /// <summary>Creates an initially ready aggregate.</summary>
    public ReadinessCell(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ReadyCell = context.Source(true);
    }

    /// <summary>The reactive ready flag.</summary>
    public Source<bool> ReadyCell { get; }

    /// <summary>Whether every known condition is ready.</summary>
    public bool Ready => ReadyCell.Get();

    /// <summary>Sets one named readiness condition.</summary>
    public void Set(string name, bool ready)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _conditions[name] = ready;
        ReadyCell.Set(_conditions.Values.All(value => value));
    }
}

/// <summary>A reactive service-to-endpoint map tied to peer membership.</summary>
public sealed class DiscoveryCell
{
    private sealed record Entry(string Endpoint, long Peer);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Creates an empty discovery map.</summary>
    public DiscoveryCell(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        DiscoveryMapCell = context.Source<IReadOnlyDictionary<string, string>>(
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            DictionaryEqualityComparer<string, string>.Instance);
    }

    /// <summary>The reactive service-to-endpoint map.</summary>
    public Source<IReadOnlyDictionary<string, string>> DiscoveryMapCell { get; }

    /// <summary>The sorted service-to-endpoint map.</summary>
    public IReadOnlyDictionary<string, string> Discovery => DiscoveryMapCell.Get();

    /// <summary>Registers an endpoint owned by a peer.</summary>
    public void Register(string service, string endpoint, long peer)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        _entries[service] = new Entry(endpoint, peer);
        Refresh();
    }

    /// <summary>Deregisters a service.</summary>
    public void Deregister(string service)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        _entries.Remove(service);
        Refresh();
    }

    /// <summary>Evicts every endpoint owned by a departing peer.</summary>
    public void Evict(long peer)
    {
        foreach (var service in _entries
                     .Where(pair => pair.Value.Peer == peer)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.Remove(service);
        }
        Refresh();
    }

    /// <summary>Resolves one service, or returns null when absent.</summary>
    public string? Resolve(string service)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        return _entries.TryGetValue(service, out var entry) ? entry.Endpoint : null;
    }

    private void Refresh()
    {
        IReadOnlyDictionary<string, string> projection = new SortedDictionary<string, string>(
            _entries.ToDictionary(pair => pair.Key, pair => pair.Value.Endpoint),
            StringComparer.Ordinal);
        DiscoveryMapCell.Set(projection);
    }
}

/// <summary>The kind of durable service-registry operation.</summary>
public enum ServiceRegistryOperationKind
{
    /// <summary>Register or replace an endpoint.</summary>
    Register,

    /// <summary>Remove a service.</summary>
    Deregister,
}

/// <summary>One ordered durable service-registry operation.</summary>
public sealed record ServiceRegistryOperation(
    ServiceRegistryOperationKind Kind,
    string Service,
    string? Endpoint = null);

/// <summary>A durable ordered registration log with a reactive left-fold projection.</summary>
public sealed class ServiceRegistry
{
    private readonly List<ServiceRegistryOperation> _log = [];
    private Dictionary<string, string> _projection = new(StringComparer.Ordinal);

    /// <summary>Creates an empty registry.</summary>
    public ServiceRegistry(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProjectionCell = context.Source<IReadOnlyDictionary<string, string>>(
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            DictionaryEqualityComparer<string, string>.Instance);
    }

    /// <summary>The durable ordered operation log.</summary>
    public IReadOnlyList<ServiceRegistryOperation> Log => _log;

    /// <summary>The reactive registration projection.</summary>
    public Source<IReadOnlyDictionary<string, string>> ProjectionCell { get; }

    /// <summary>The sorted current registration projection.</summary>
    public IReadOnlyDictionary<string, string> Projection => ProjectionCell.Get();

    /// <summary>Appends a registration and updates the projection.</summary>
    public void Register(string service, string endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        var operation = new ServiceRegistryOperation(
            ServiceRegistryOperationKind.Register,
            service,
            endpoint);
        Apply(_projection, operation);
        _log.Add(operation);
        Refresh();
    }

    /// <summary>Appends a deregistration and updates the projection.</summary>
    public void Deregister(string service)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        var operation = new ServiceRegistryOperation(
            ServiceRegistryOperationKind.Deregister,
            service);
        Apply(_projection, operation);
        _log.Add(operation);
        Refresh();
    }

    /// <summary>Rebuilds the projection from the durable log.</summary>
    public void Replay()
    {
        var projection = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var operation in _log) Apply(projection, operation);
        _projection = projection;
        Refresh();
    }

    private static void Apply(
        IDictionary<string, string> projection,
        ServiceRegistryOperation operation)
    {
        if (operation.Kind == ServiceRegistryOperationKind.Register)
            projection[operation.Service] = operation.Endpoint!;
        else
            projection.Remove(operation.Service);
    }

    private void Refresh()
    {
        IReadOnlyDictionary<string, string> projection =
            new SortedDictionary<string, string>(_projection, StringComparer.Ordinal);
        ProjectionCell.Set(projection);
    }
}
