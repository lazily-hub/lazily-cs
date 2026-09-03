namespace Lazily;

/// <summary>A pending latest-value projection for one key.</summary>
/// <param name="Epoch">The monotone key epoch.</param>
/// <param name="Value">The value desired at that epoch.</param>
public sealed record LatestDurableDesired<TValue>(long Epoch, TValue Value);

/// <summary>A generation-fenced sink attempt claimed from the pending projection.</summary>
/// <param name="Generation">The connection generation that owns the attempt.</param>
/// <param name="Key">The projection key.</param>
/// <param name="Epoch">The key epoch.</param>
/// <param name="Value">The projected value.</param>
public sealed record LatestDurableEnvelope<TKey, TValue>(long Generation, TKey Key, long Epoch, TValue Value);

/// <summary>An immutable diagnostic image of one keyed projection.</summary>
/// <param name="Key">The projection key.</param>
/// <param name="Desired">The latest unclaimed value, if any.</param>
/// <param name="Inflight">The single claimed attempt, if any.</param>
/// <param name="DurableThrough">The monotone durable frontier, if any value has been acknowledged.</param>
public sealed record LatestDurableEntry<TKey, TValue>(
    TKey Key,
    LatestDurableDesired<TValue>? Desired,
    LatestDurableEnvelope<TKey, TValue>? Inflight,
    long? DurableThrough);

/// <summary>The outcome of <see cref="LatestDurableProjectionCore{TKey,TValue}.UpsertDesired"/>.</summary>
public enum LatestDurableUpsertKind
{
    /// <summary>The desired epoch was retained.</summary>
    Accepted,
    /// <summary>The identical epoch/value pair was already retained.</summary>
    Unchanged,
    /// <summary>The epoch is at or behind the durable frontier.</summary>
    AlreadyDurable,
    /// <summary>A newer desired or in-flight epoch exists.</summary>
    StaleEpoch,
    /// <summary>The retained epoch has a different value.</summary>
    EpochConflict,
}

/// <summary>An upsert outcome and the frontier that explained a rejection, when applicable.</summary>
/// <param name="Kind">The outcome category.</param>
/// <param name="Current">The current durable or retained epoch, when reported.</param>
public readonly record struct LatestDurableUpsertResult(LatestDurableUpsertKind Kind, long? Current = null);

/// <summary>The outcome of <see cref="LatestDurableProjectionCore{TKey,TValue}.Claim"/>.</summary>
public enum LatestDurableClaimKind
{
    /// <summary>The desired value moved to the flight slot.</summary>
    Claimed,
    /// <summary>No desired value exists.</summary>
    Empty,
    /// <summary>The key already has a flight.</summary>
    Busy,
    /// <summary>The caller does not own the current generation.</summary>
    StaleGeneration,
}

/// <summary>A claim outcome, carrying the claimed envelope or current generation when applicable.</summary>
public sealed record LatestDurableClaimResult<TKey, TValue>(
    LatestDurableClaimKind Kind,
    LatestDurableEnvelope<TKey, TValue>? Envelope = null,
    long? Current = null);

/// <summary>The outcome of <see cref="LatestDurableProjectionCore{TKey,TValue}.AckApplied"/>.</summary>
public enum LatestDurableAckKind
{
    /// <summary>The durable frontier advanced.</summary>
    Advanced,
    /// <summary>The epoch was already durable.</summary>
    Unchanged,
    /// <summary>No matching flight or durable epoch exists.</summary>
    UnknownEpoch,
    /// <summary>The caller does not own the current generation.</summary>
    StaleGeneration,
}

/// <summary>An acknowledgement outcome and its current frontier, when applicable.</summary>
public readonly record struct LatestDurableAckResult(
    LatestDurableAckKind Kind,
    long? DurableThrough = null,
    long? Current = null);

/// <summary>The outcome of <see cref="LatestDurableProjectionCore{TKey,TValue}.FailRetryable"/>.</summary>
public enum LatestDurableFailureKind
{
    /// <summary>The failed attempt returned to desired state.</summary>
    Pending,
    /// <summary>A newer desired epoch made the failed attempt obsolete.</summary>
    Superseded,
    /// <summary>No exact current flight exists.</summary>
    UnknownEpoch,
    /// <summary>The caller does not own the current generation.</summary>
    StaleGeneration,
}

/// <summary>A retryable-failure outcome and the current generation for a fenced caller.</summary>
public readonly record struct LatestDurableFailureResult(
    LatestDurableFailureKind Kind,
    long? Current = null);

/// <summary>The outcome of <see cref="LatestDurableProjectionCore{TKey,TValue}.Reconnect"/>.</summary>
public enum LatestDurableReconnectKind
{
    /// <summary>The generation advanced and old flights were reconciled.</summary>
    Advanced,
    /// <summary>The requested generation already is current.</summary>
    Unchanged,
    /// <summary>The requested generation is behind the current fence.</summary>
    StaleGeneration,
}

/// <summary>A reconnect outcome, including how old flights were reconciled.</summary>
public readonly record struct LatestDurableReconnectResult(
    LatestDurableReconnectKind Kind,
    long Generation,
    int Requeued = 0,
    int Superseded = 0);

/// <summary>
/// Pure per-key latest-durable projection state machine from lazily-spec v0.38.0.
/// </summary>
/// <remarks>
/// This is not FIFO egress. A newer desired epoch replaces only pending state; a claimed
/// attempt stays in flight until its exact generation/epoch token succeeds, fails, or is
/// fenced by reconnect. The core performs no graph writes and no sink I/O.
/// </remarks>
public sealed class LatestDurableProjectionCore<TKey, TValue>
    where TKey : notnull
{
    private sealed class State
    {
        internal LatestDurableDesired<TValue>? Desired;
        internal LatestDurableEnvelope<TKey, TValue>? Inflight;
        internal long? DurableThrough;
    }

    private readonly Dictionary<TKey, State> _entries = [];
    private readonly IEqualityComparer<TValue> _values;

    /// <summary>Creates an empty projection at the supplied connection generation.</summary>
    public LatestDurableProjectionCore(
        long initialGeneration,
        IEqualityComparer<TValue>? valueComparer = null)
    {
        Generation = initialGeneration;
        _values = valueComparer ?? EqualityComparer<TValue>.Default;
    }

    /// <summary>The current connection generation.</summary>
    public long Generation { get; private set; }

    /// <summary>Every key for which state has been accepted.</summary>
    public IReadOnlyList<TKey> KnownKeys() => [.. _entries.Keys];

    /// <summary>Returns an immutable image without creating state for an unknown key.</summary>
    public LatestDurableEntry<TKey, TValue> Snapshot(TKey key) =>
        _entries.TryGetValue(key, out var state)
            ? new(key, state.Desired, state.Inflight, state.DurableThrough)
            : new(key, null, null, null);

    /// <summary>Returns immutable images of every known key.</summary>
    public IReadOnlyList<LatestDurableEntry<TKey, TValue>> Snapshots() =>
        [.. _entries.Keys.Select(Snapshot)];

    /// <summary>Returns the durable frontier for a key, if any.</summary>
    public long? DurableThrough(TKey key) =>
        _entries.TryGetValue(key, out var state) ? state.DurableThrough : null;

    /// <summary>Retains <paramref name="value"/> when its epoch is newer than all known state.</summary>
    public LatestDurableUpsertResult UpsertDesired(TKey key, long epoch, TValue value)
    {
        if (!_entries.TryGetValue(key, out var state))
        {
            state = new State();
            _entries.Add(key, state);
        }

        if (state.DurableThrough is long durable && epoch <= durable)
            return new(LatestDurableUpsertKind.AlreadyDurable, durable);

        var desiredEpoch = state.Desired?.Epoch;
        var inflightEpoch = state.Inflight?.Epoch;
        var newest = desiredEpoch is null
            ? inflightEpoch
            : inflightEpoch is null ? desiredEpoch : Math.Max(desiredEpoch.Value, inflightEpoch.Value);
        if (newest is long retained)
        {
            if (epoch < retained) return new(LatestDurableUpsertKind.StaleEpoch, retained);
            if (epoch == retained)
            {
                var retainedValue = state.Desired is { Epoch: var desired } && desired == epoch
                    ? state.Desired.Value
                    : state.Inflight!.Value;
                return new(_values.Equals(value, retainedValue)
                    ? LatestDurableUpsertKind.Unchanged
                    : LatestDurableUpsertKind.EpochConflict);
            }
        }

        state.Desired = new(epoch, value);
        return new(LatestDurableUpsertKind.Accepted);
    }

    /// <summary>Moves one desired value into the per-key single-flight slot.</summary>
    public LatestDurableClaimResult<TKey, TValue> Claim(TKey key, long generation)
    {
        if (generation != Generation)
            return new(LatestDurableClaimKind.StaleGeneration, Current: Generation);
        if (!_entries.TryGetValue(key, out var state) || state.Desired is null)
            return state?.Inflight is not null
                ? new(LatestDurableClaimKind.Busy)
                : new(LatestDurableClaimKind.Empty);
        if (state.Inflight is not null) return new(LatestDurableClaimKind.Busy);

        var desired = state.Desired;
        var envelope = new LatestDurableEnvelope<TKey, TValue>(
            generation, key, desired.Epoch, desired.Value);
        state.Desired = null;
        state.Inflight = envelope;
        return new(LatestDurableClaimKind.Claimed, envelope);
    }

    /// <summary>Acknowledges only the exact current generation/epoch flight.</summary>
    public LatestDurableAckResult AckApplied(TKey key, long generation, long epoch)
    {
        if (generation != Generation)
            return new(LatestDurableAckKind.StaleGeneration, Current: Generation);
        if (!_entries.TryGetValue(key, out var state)
            || state.Inflight is null
            || state.Inflight.Epoch != epoch)
        {
            var durable = state?.DurableThrough;
            return durable is not null && epoch <= durable
                ? new(LatestDurableAckKind.Unchanged, durable)
                : new(LatestDurableAckKind.UnknownEpoch);
        }

        state.Inflight = null;
        var previous = state.DurableThrough;
        var frontier = Math.Max(previous ?? epoch, epoch);
        state.DurableThrough = frontier;
        return previous is null || epoch > previous
            ? new(LatestDurableAckKind.Advanced, frontier)
            : new(LatestDurableAckKind.Unchanged, frontier);
    }

    /// <summary>Requeues an exact failed flight unless a newer desired value superseded it.</summary>
    public LatestDurableFailureResult FailRetryable(TKey key, long generation, long epoch)
    {
        if (generation != Generation)
            return new(LatestDurableFailureKind.StaleGeneration, Generation);
        if (!_entries.TryGetValue(key, out var state)
            || state.Inflight is null
            || state.Inflight.Epoch != epoch)
            return new(LatestDurableFailureKind.UnknownEpoch);

        var failed = state.Inflight;
        state.Inflight = null;
        if (state.Desired is { } desired && desired.Epoch > failed.Epoch)
            return new(LatestDurableFailureKind.Superseded);
        state.Desired = new(failed.Epoch, failed.Value);
        return new(LatestDurableFailureKind.Pending);
    }

    /// <summary>Advances the generation and reconciles every old flight back to pending state.</summary>
    public LatestDurableReconnectResult Reconnect(long newGeneration)
    {
        if (newGeneration < Generation)
            return new(LatestDurableReconnectKind.StaleGeneration, Generation);
        if (newGeneration == Generation)
            return new(LatestDurableReconnectKind.Unchanged, Generation);

        var requeued = 0;
        var superseded = 0;
        foreach (var state in _entries.Values)
        {
            if (state.Inflight is not { } flight) continue;
            if (state.Desired is { } desired && desired.Epoch > flight.Epoch)
                superseded++;
            else
            {
                state.Desired = new(flight.Epoch, flight.Value);
                requeued++;
            }
            state.Inflight = null;
        }

        Generation = newGeneration;
        return new(LatestDurableReconnectKind.Advanced, Generation, requeued, superseded);
    }
}
