namespace Lazily;

/// <summary>A single-threaded reactive shell over <see cref="LatestDurableProjectionCore{TKey,TValue}"/>.</summary>
public sealed class LatestDurableProjection<TKey, TValue>
    where TKey : notnull
{
    private sealed record Reader(Source<int> Version, Computed<LatestDurableEntry<TKey, TValue>> Value)
    {
        internal int Tick;
    }

    private readonly Context _ctx;
    private readonly LatestDurableProjectionCore<TKey, TValue> _core;
    private readonly Dictionary<TKey, Reader> _readers = [];
    private readonly Source<long> _generationSource;
    private readonly Computed<long> _generation;

    /// <summary>Creates a projection whose reactive views belong to <paramref name="context"/>.</summary>
    public LatestDurableProjection(Context context, long initialGeneration)
    {
        Guard.NotNull(context, nameof(context));
        _ctx = context;
        _core = new(initialGeneration);
        _generationSource = context.Source(initialGeneration);
        _generation = context.Computed(compute => compute.Get(_generationSource));
    }

    /// <summary>Returns the memoized reactive image for <paramref name="key"/>.</summary>
    public Computed<LatestDurableEntry<TKey, TValue>> Entry(TKey key)
    {
        if (_readers.TryGetValue(key, out var current)) return current.Value;
        var version = _ctx.Source(0);
        var computed = _ctx.Computed(compute =>
        {
            _ = compute.Get(version);
            return _core.Snapshot(key);
        });
        _readers.Add(key, new(version, computed));
        return computed;
    }

    /// <summary>Returns the reactive connection generation.</summary>
    public Computed<long> Generation() => _generation;

    /// <summary>Returns a non-reactive key image.</summary>
    public LatestDurableEntry<TKey, TValue> Snapshot(TKey key) => _core.Snapshot(key);

    /// <summary>Retains a newer desired projection.</summary>
    public LatestDurableUpsertResult UpsertDesired(TKey key, long epoch, TValue value) =>
        Mutate(key, () => _core.UpsertDesired(key, epoch, value));

    /// <summary>Claims one pending projection for the supplied generation.</summary>
    public LatestDurableClaimResult<TKey, TValue> Claim(TKey key, long generation) =>
        Mutate(key, () => _core.Claim(key, generation));

    /// <summary>Acknowledges one exact generation/epoch flight.</summary>
    public LatestDurableAckResult AckApplied(TKey key, long generation, long epoch) =>
        Mutate(key, () => _core.AckApplied(key, generation, epoch));

    /// <summary>Reports a retryable failure for one exact flight.</summary>
    public LatestDurableFailureResult FailRetryable(TKey key, long generation, long epoch) =>
        Mutate(key, () => _core.FailRetryable(key, generation, epoch));

    /// <summary>Fences the old connection generation and reconciles its flights.</summary>
    public LatestDurableReconnectResult Reconnect(long newGeneration)
    {
        var before = _core.KnownKeys().ToDictionary(key => key, _core.Snapshot);
        var result = _core.Reconnect(newGeneration);
        foreach (var (key, reader) in _readers)
            if (before.GetValueOrDefault(key) != _core.Snapshot(key)) reader.Version.Set(++reader.Tick);
        if (result.Kind == LatestDurableReconnectKind.Advanced)
            _generationSource.Set(_core.Generation);
        return result;
    }

    private TResult Mutate<TResult>(TKey key, Func<TResult> operation)
    {
        var before = _core.Snapshot(key);
        var result = operation();
        if (before != _core.Snapshot(key) && _readers.TryGetValue(key, out var reader))
            reader.Version.Set(++reader.Tick);
        return result;
    }
}

/// <summary>A lock-serialized reactive latest-durable projection.</summary>
public sealed class ThreadSafeLatestDurableProjection<TKey, TValue>
    where TKey : notnull
{
    private sealed record Reader(Source<int> Version, Computed<LatestDurableEntry<TKey, TValue>> Value)
    {
        internal int Tick;
    }

    private readonly ThreadSafeContext _ctx;
    private readonly object _gate = new();
    private readonly LatestDurableProjectionCore<TKey, TValue> _core;
    private readonly Dictionary<TKey, Reader> _readers = [];
    private readonly Source<long> _generationSource;
    private readonly Computed<long> _generation;

    /// <summary>Creates a projection whose reactive views belong to <paramref name="context"/>.</summary>
    public ThreadSafeLatestDurableProjection(ThreadSafeContext context, long initialGeneration)
    {
        Guard.NotNull(context, nameof(context));
        _ctx = context;
        _core = new(initialGeneration);
        Source<long>? source = null;
        Computed<long>? computed = null;
        context.WithLock(inner =>
        {
            source = inner.Source(initialGeneration);
            computed = inner.Computed(cx => cx.Get(source));
        });
        _generationSource = source!;
        _generation = computed!;
    }

    /// <summary>Returns the memoized reactive image for <paramref name="key"/>.</summary>
    public Computed<LatestDurableEntry<TKey, TValue>> Entry(TKey key)
    {
        lock (_gate)
            if (_readers.TryGetValue(key, out var current)) return current.Value;

        Source<int>? version = null;
        Computed<LatestDurableEntry<TKey, TValue>>? computed = null;
        _ctx.WithLock(inner =>
        {
            version = inner.Source(0);
            var captured = version;
            computed = inner.Computed(cx =>
            {
                _ = cx.Get(captured);
                lock (_gate) return _core.Snapshot(key);
            });
        });
        lock (_gate)
        {
            if (_readers.TryGetValue(key, out var winner)) return winner.Value;
            _readers.Add(key, new(version!, computed!));
            return computed!;
        }
    }

    /// <summary>Returns the reactive connection generation.</summary>
    public Computed<long> Generation() => _generation;

    /// <summary>Returns a lock-protected non-reactive key image.</summary>
    public LatestDurableEntry<TKey, TValue> Snapshot(TKey key)
    {
        lock (_gate) return _core.Snapshot(key);
    }

    /// <summary>Retains a newer desired projection.</summary>
    public LatestDurableUpsertResult UpsertDesired(TKey key, long epoch, TValue value) =>
        Mutate(key, () => _core.UpsertDesired(key, epoch, value));

    /// <summary>Claims one pending projection for the supplied generation.</summary>
    public LatestDurableClaimResult<TKey, TValue> Claim(TKey key, long generation) =>
        Mutate(key, () => _core.Claim(key, generation));

    /// <summary>Acknowledges one exact generation/epoch flight.</summary>
    public LatestDurableAckResult AckApplied(TKey key, long generation, long epoch) =>
        Mutate(key, () => _core.AckApplied(key, generation, epoch));

    /// <summary>Reports a retryable failure for one exact flight.</summary>
    public LatestDurableFailureResult FailRetryable(TKey key, long generation, long epoch) =>
        Mutate(key, () => _core.FailRetryable(key, generation, epoch));

    /// <summary>Fences the old connection generation and reconciles its flights.</summary>
    public LatestDurableReconnectResult Reconnect(long newGeneration)
    {
        List<(Source<int> Version, int Tick)> changed = [];
        LatestDurableReconnectResult result;
        lock (_gate)
        {
            var before = _core.KnownKeys().ToDictionary(key => key, _core.Snapshot);
            result = _core.Reconnect(newGeneration);
            foreach (var (key, reader) in _readers)
                if (before.GetValueOrDefault(key) != _core.Snapshot(key))
                    changed.Add((reader.Version, ++reader.Tick));
        }
        _ctx.Batch(() =>
        {
            foreach (var (version, tick) in changed) _ctx.Set(version, tick);
            if (result.Kind == LatestDurableReconnectKind.Advanced)
                _ctx.Set(_generationSource, result.Generation);
        });
        return result;
    }

    private TResult Mutate<TResult>(TKey key, Func<TResult> operation)
    {
        Source<int>? version = null;
        var tick = 0;
        TResult result;
        lock (_gate)
        {
            var before = _core.Snapshot(key);
            result = operation();
            if (before != _core.Snapshot(key) && _readers.TryGetValue(key, out var reader))
            {
                version = reader.Version;
                tick = ++reader.Tick;
            }
        }
        if (version is not null) _ctx.Set(version, tick);
        return result;
    }
}

/// <summary>An async-graph reactive latest-durable projection with synchronous admission operations.</summary>
public sealed class AsyncLatestDurableProjection<TKey, TValue>
    where TKey : notnull
{
    private sealed record Reader(AsyncSource<int> Version, AsyncComputed<LatestDurableEntry<TKey, TValue>> Value)
    {
        internal int Tick;
    }

    private readonly AsyncContext _ctx;
    private readonly object _gate = new();
    private readonly LatestDurableProjectionCore<TKey, TValue> _core;
    private readonly Dictionary<TKey, Reader> _readers = [];
    private readonly AsyncSource<long> _generationSource;
    private readonly AsyncComputed<long> _generation;

    /// <summary>Creates a projection whose reactive views belong to <paramref name="context"/>.</summary>
    public AsyncLatestDurableProjection(AsyncContext context, long initialGeneration)
    {
        Guard.NotNull(context, nameof(context));
        _ctx = context;
        _core = new(initialGeneration);
        _generationSource = context.Source(initialGeneration);
        _generation = context.Computed(compute => Task.FromResult(compute.Track(_generationSource)));
    }

    /// <summary>Returns the memoized async reactive image for <paramref name="key"/>.</summary>
    public AsyncComputed<LatestDurableEntry<TKey, TValue>> Entry(TKey key)
    {
        lock (_gate)
            if (_readers.TryGetValue(key, out var current)) return current.Value;
        var version = _ctx.Source(0);
        var computed = _ctx.Computed(compute =>
        {
            _ = compute.Track(version);
            lock (_gate) return Task.FromResult(_core.Snapshot(key));
        });
        lock (_gate)
        {
            if (_readers.TryGetValue(key, out var winner)) return winner.Value;
            _readers.Add(key, new(version, computed));
            return computed;
        }
    }

    /// <summary>Returns the reactive connection generation.</summary>
    public AsyncComputed<long> Generation() => _generation;

    /// <summary>Returns a lock-protected non-reactive key image.</summary>
    public LatestDurableEntry<TKey, TValue> Snapshot(TKey key)
    {
        lock (_gate) return _core.Snapshot(key);
    }

    /// <summary>Retains a newer desired projection.</summary>
    public LatestDurableUpsertResult UpsertDesired(TKey key, long epoch, TValue value) =>
        Mutate(key, () => _core.UpsertDesired(key, epoch, value));

    /// <summary>Claims one pending projection for the supplied generation.</summary>
    public LatestDurableClaimResult<TKey, TValue> Claim(TKey key, long generation) =>
        Mutate(key, () => _core.Claim(key, generation));

    /// <summary>Acknowledges one exact generation/epoch flight.</summary>
    public LatestDurableAckResult AckApplied(TKey key, long generation, long epoch) =>
        Mutate(key, () => _core.AckApplied(key, generation, epoch));

    /// <summary>Reports a retryable failure for one exact flight.</summary>
    public LatestDurableFailureResult FailRetryable(TKey key, long generation, long epoch) =>
        Mutate(key, () => _core.FailRetryable(key, generation, epoch));

    /// <summary>Fences the old connection generation and reconciles its flights.</summary>
    public LatestDurableReconnectResult Reconnect(long newGeneration)
    {
        List<(AsyncSource<int> Version, int Tick)> changed = [];
        LatestDurableReconnectResult result;
        lock (_gate)
        {
            var before = _core.KnownKeys().ToDictionary(key => key, _core.Snapshot);
            result = _core.Reconnect(newGeneration);
            foreach (var (key, reader) in _readers)
                if (before.GetValueOrDefault(key) != _core.Snapshot(key))
                    changed.Add((reader.Version, ++reader.Tick));
        }
        _ctx.Batch(() =>
        {
            foreach (var (version, tick) in changed) version.Set(tick);
            if (result.Kind == LatestDurableReconnectKind.Advanced)
                _generationSource.Set(result.Generation);
        });
        return result;
    }

    private TResult Mutate<TResult>(TKey key, Func<TResult> operation)
    {
        AsyncSource<int>? version = null;
        var tick = 0;
        TResult result;
        lock (_gate)
        {
            var before = _core.Snapshot(key);
            result = operation();
            if (before != _core.Snapshot(key) && _readers.TryGetValue(key, out var reader))
            {
                version = reader.Version;
                tick = ++reader.Tick;
            }
        }
        version?.Set(tick);
        return result;
    }
}
