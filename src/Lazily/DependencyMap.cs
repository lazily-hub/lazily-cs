namespace Lazily;

/// <summary>The exact-key state of a dependency publication.</summary>
/// <remarks>
/// Unavailable is a value of a stable reactive source, not absence from a map.
/// Publishing is therefore a normal source transition rather than a
/// membership epoch or request/ack handshake.
/// </remarks>
/// <typeparam name="TValue">The published value type.</typeparam>
public sealed record DependencyAvailability<TValue>(bool IsAvailable, TValue? Value)
{
    /// <summary>The dependency has no current publication.</summary>
    public static DependencyAvailability<TValue> Unavailable { get; } = new(false, default);

    /// <summary>Creates an available dependency state.</summary>
    /// <param name="value">The published value.</param>
    /// <returns>An available state.</returns>
    public static DependencyAvailability<TValue> Available(TValue value) => new(true, value);
}

/// <summary>Single-threaded exact-key reactive dependency publication.</summary>
public sealed class DependencyMap<TKey, TValue>
where TKey : notnull
{
    private readonly SourceMap<TKey, DependencyAvailability<TValue>> _sources;

    /// <summary>Creates an empty dependency map.</summary>
    public DependencyMap(Context context) =>
        _sources = new SourceMap<TKey, DependencyAvailability<TValue>>(context);

    /// <summary>
    /// Observes one exact dependency, materializing its stable unavailable
    /// source on first access.
    /// </summary>
    public DependencyAvailability<TValue> ObserveDependency(TKey key, IComputeOps? ops = null)
    {
        var source = _sources.Entry(key, DependencyAvailability<TValue>.Unavailable);
        return ops is null ? source.Get() : source.Get(ops);
    }

    /// <summary>Publishes a value through the exact-key source.</summary>
    public void Publish(TKey key, TValue value) =>
        _sources.Set(key, DependencyAvailability<TValue>.Available(value));

    /// <summary>Transitions the exact-key source back to unavailable.</summary>
    public void Unpublish(TKey key) =>
        _sources.Set(key, DependencyAvailability<TValue>.Unavailable);

    /// <summary>Returns the stable exact-key source if materialized.</summary>
    public bool TryGetHandle(
        TKey key,
        out Source<DependencyAvailability<TValue>> handle) =>
        _sources.TryGetHandle(key, out handle);

    /// <summary>How many exact-key sources have been materialized.</summary>
    public int PresentCount => _sources.PresentCount;
}

/// <summary>Thread-safe exact-key reactive dependency publication.</summary>
public sealed class ThreadSafeDependencyMap<TKey, TValue>
where TKey : notnull
{
    private readonly ThreadSafeContext _context;
    private readonly ThreadSafeSourceMap<TKey, DependencyAvailability<TValue>> _sources;

    /// <summary>Creates an empty thread-safe dependency map.</summary>
    public ThreadSafeDependencyMap(ThreadSafeContext context)
    {
        Guard.NotNull(context, nameof(context));
        _context = context;
        _sources =
            new ThreadSafeSourceMap<TKey, DependencyAvailability<TValue>>(context);
    }

    /// <summary>Observes one exact dependency through its stable source.</summary>
    public DependencyAvailability<TValue> ObserveDependency(
        TKey key,
        IComputeOps? ops = null) =>
        _context.WithLock(_ =>
        {
            var source = _sources.Entry(key, DependencyAvailability<TValue>.Unavailable);
            return ops is null ? source.Get() : source.Get(ops);
        });

    /// <summary>Publishes a value through the exact-key source.</summary>
    public void Publish(TKey key, TValue value) =>
        _sources.Set(key, DependencyAvailability<TValue>.Available(value));

    /// <summary>Transitions the exact-key source back to unavailable.</summary>
    public void Unpublish(TKey key) =>
        _sources.Set(key, DependencyAvailability<TValue>.Unavailable);

    /// <summary>Returns the stable exact-key source if materialized.</summary>
    public bool TryGetHandle(
        TKey key,
        out Source<DependencyAvailability<TValue>> handle) =>
        _sources.TryGetHandle(key, out handle);

    /// <summary>How many exact-key sources have been materialized.</summary>
    public int PresentCount => _sources.PresentCount;
}

/// <summary>Async-flavor exact-key reactive dependency publication.</summary>
public sealed class AsyncDependencyMap<TKey, TValue>
where TKey : notnull
{
    private readonly AsyncSourceMap<TKey, DependencyAvailability<TValue>> _sources;

    /// <summary>Creates an empty async dependency map.</summary>
    public AsyncDependencyMap(AsyncContext context) =>
        _sources = new AsyncSourceMap<TKey, DependencyAvailability<TValue>>(context);

    /// <summary>Observes one exact dependency through its stable async source.</summary>
    public DependencyAvailability<TValue> ObserveDependency(
        TKey key,
        AsyncCompute? compute = null)
    {
        var source = _sources.Entry(key, DependencyAvailability<TValue>.Unavailable);
        return compute is null ? source.Peek() : compute.Track(source);
    }

    /// <summary>Publishes a value through the exact-key async source.</summary>
    public void Publish(TKey key, TValue value) =>
        _sources.Set(key, DependencyAvailability<TValue>.Available(value));

    /// <summary>Transitions the exact-key async source back to unavailable.</summary>
    public void Unpublish(TKey key) =>
        _sources.Set(key, DependencyAvailability<TValue>.Unavailable);

    /// <summary>Returns the stable exact-key async source if materialized.</summary>
    public bool TryGetHandle(
        TKey key,
        out AsyncSource<DependencyAvailability<TValue>> handle) =>
        _sources.TryGetHandle(key, out handle);

    /// <summary>How many exact-key sources have been materialized.</summary>
    public int PresentCount => _sources.PresentCount;
}
