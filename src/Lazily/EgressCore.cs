// EgressCore — the transport-agnostic reactive egress delivery authority (spec tag: lzegress).
//
// The core owns delivery authority: sequence assignment, the unacknowledged window, a monotone
// acknowledgement watermark, the retry budget, and the producer-generation fence. It owns no graph
// handles and performs no I/O. DurableOutbox/SpillStore remain the persistence layer; RelayCell
// remains the conflation/backpressure layer.
//
// Like IngressCore and QueueCore, every mutator returns WHICH reader kinds it dirtied
// (EgressChange) and performs no graph write. The three shells (EgressCell,
// ThreadSafeEgressCell, AsyncEgressCell) bump exactly those version sources inside one batch,
// which is what makes "the three flavors obey one contract" structural rather than three copies
// agreeing by hand.

namespace Lazily;

/// <summary>Egress bounds. <see cref="RetryBudget"/> counts retries AFTER the first attempt.</summary>
/// <param name="InflightLimit">The largest unacknowledged window; must be positive.</param>
/// <param name="RetryBudget">Retries allowed after the first attempt.</param>
/// <param name="RetryBase">The first retry's backoff; must be positive.</param>
/// <param name="RetryCeiling">The largest backoff; must be at least <paramref name="RetryBase"/>.</param>
public readonly record struct EgressPolicy(
    int InflightLimit,
    int RetryBudget,
    long RetryBase,
    long RetryCeiling)
{
    /// <summary>The reference default: window 64, budget 3, backoff 10 doubling to 1000.</summary>
    public static EgressPolicy Default => new(64, 3, 10, 1_000);
}

/// <summary>Why an <see cref="EgressPolicy"/> was refused.</summary>
public enum EgressConfigError
{
    /// <summary><see cref="EgressPolicy.InflightLimit"/> was zero or negative.</summary>
    ZeroInflightLimit,
    /// <summary><see cref="EgressPolicy.RetryBase"/> was zero or negative.</summary>
    ZeroRetryBase,
    /// <summary><see cref="EgressPolicy.RetryCeiling"/> was below the base.</summary>
    RetryCeilingBelowBase,
}

/// <summary>Thrown when an egress is built over an invalid <see cref="EgressPolicy"/>.</summary>
public sealed class EgressConfigException : ArgumentException
{
    /// <summary>Creates the exception for <paramref name="error"/>.</summary>
    /// <param name="error">Which bound was violated.</param>
    public EgressConfigException(EgressConfigError error)
        : base($"invalid egress policy: {error}") => Error = error;

    /// <summary>Which bound was violated.</summary>
    public EgressConfigError Error { get; }
}

/// <summary>
/// One send attempt. The <c>(Generation, Sequence)</c> pair is the transport idempotency identity.
/// </summary>
/// <param name="Generation">The producer incarnation the attempt was claimed under.</param>
/// <param name="Sequence">The monotone sequence assigned at enqueue.</param>
/// <param name="Attempt">The 1-based attempt number (0 while never claimed).</param>
/// <param name="Payload">The value being delivered.</param>
public sealed record EgressEnvelope<T>(long Generation, long Sequence, int Attempt, T Payload);

/// <summary>The retry decision recorded by the most recent failure.</summary>
/// <param name="Sequence">The failed sequence.</param>
/// <param name="Attempt">The attempt that failed.</param>
/// <param name="Backoff">The derived backoff before the record may be retried.</param>
/// <param name="Exhausted">Whether the budget is spent; an exhausted record is terminal.</param>
public readonly record struct EgressRetry(long Sequence, int Attempt, long Backoff, bool Exhausted);

/// <summary>The outcome category of <see cref="EgressCore{T}.Claim"/>.</summary>
public enum EgressClaimKind
{
    /// <summary>A pending record moved into the unacknowledged window.</summary>
    Claimed,
    /// <summary>Nothing is pending.</summary>
    Empty,
    /// <summary>The unacknowledged window is full.</summary>
    WindowFull,
    /// <summary>The caller's attachment belongs to a superseded incarnation.</summary>
    StaleGeneration,
}

/// <summary>A claim outcome, carrying the envelope or the current generation when applicable.</summary>
public sealed record EgressClaimResult<T>(
    EgressClaimKind Kind,
    EgressEnvelope<T>? Envelope = null,
    long? Current = null);

/// <summary>The outcome category of <see cref="EgressCore{T}.Ack"/>.</summary>
public enum EgressAckKind
{
    /// <summary>The watermark advanced.</summary>
    Advanced,
    /// <summary>The watermark already covered the acknowledgement.</summary>
    Unchanged,
    /// <summary>The caller's attachment belongs to a superseded incarnation.</summary>
    StaleGeneration,
}

/// <summary>An acknowledgement outcome.</summary>
/// <param name="Kind">The outcome category.</param>
/// <param name="Through">The watermark after the call (Advanced/Unchanged).</param>
/// <param name="Current">The current generation (StaleGeneration).</param>
public readonly record struct EgressAckResult(EgressAckKind Kind, long? Through = null, long? Current = null);

/// <summary>The outcome category of <see cref="EgressCore{T}.Fail"/>.</summary>
public enum EgressFailureKind
{
    /// <summary>The record is parked awaiting <see cref="EgressCore{T}.RetryNow"/>.</summary>
    Retrying,
    /// <summary>The retry budget is spent; the record is dropped.</summary>
    Exhausted,
    /// <summary>No in-flight record carries that sequence.</summary>
    UnknownSequence,
    /// <summary>The caller's attachment belongs to a superseded incarnation.</summary>
    StaleGeneration,
}

/// <summary>A failure outcome.</summary>
public readonly record struct EgressFailureResult(
    EgressFailureKind Kind,
    EgressRetry? Retry = null,
    long? Current = null);

/// <summary>The outcome category of <see cref="EgressCore{T}.RetryNow"/>.</summary>
public enum EgressRetryActionKind
{
    /// <summary>The parked record is pending again, in sequence order.</summary>
    Scheduled,
    /// <summary>No parked record carries that sequence.</summary>
    UnknownSequence,
    /// <summary>The caller's attachment belongs to a superseded incarnation.</summary>
    StaleGeneration,
}

/// <summary>A retry-scheduling outcome.</summary>
public readonly record struct EgressRetryActionResult(
    EgressRetryActionKind Kind,
    long? Sequence = null,
    long? Current = null);

/// <summary>The outcome category of <see cref="EgressCore{T}.Reconnect"/>.</summary>
public enum EgressReconnectKind
{
    /// <summary>The generation advanced and unacknowledged records were replayed.</summary>
    Advanced,
    /// <summary>The requested generation is already current.</summary>
    Unchanged,
    /// <summary>The requested generation is behind the current fence.</summary>
    StaleGeneration,
}

/// <summary>A reconnect outcome.</summary>
/// <param name="Kind">The outcome category.</param>
/// <param name="Generation">The current generation after the call.</param>
/// <param name="Replayed">How many unacknowledged records were replayed (Advanced).</param>
public readonly record struct EgressReconnectResult(EgressReconnectKind Kind, long Generation, int Replayed = 0);

/// <summary>The reader kinds one transition invalidated.</summary>
public readonly record struct EgressChange(bool Pending, bool Inflight, bool AckedThrough, bool Retry)
{
    /// <summary>Whether the transition invalidated nothing.</summary>
    public bool IsEmpty => !Pending && !Inflight && !AckedThrough && !Retry;
}

/// <summary>
/// Pure egress delivery authority: monotone sequences, a bounded unacknowledged window, a cumulative
/// acknowledgement watermark, a bounded retry budget, and a producer-generation fence.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class EgressCore<T>
{
    private sealed class Record
    {
        internal long Generation;
        internal long Sequence;
        internal int Attempt;
        internal required T Payload;

        internal EgressEnvelope<T> Envelope() => new(Generation, Sequence, Attempt, Payload);
    }

    private readonly List<Record> _pending = [];
    private readonly SortedDictionary<long, Record> _inflight = [];
    private readonly SortedDictionary<long, Record> _failed = [];
    private EgressRetry? _retry;

    /// <summary>Builds a core at <paramref name="generation"/> under <paramref name="policy"/>.</summary>
    /// <param name="generation">The initial producer incarnation.</param>
    /// <param name="policy">The bounds in force.</param>
    /// <exception cref="EgressConfigException">The policy is invalid.</exception>
    public EgressCore(long generation, EgressPolicy policy)
    {
        if (policy.InflightLimit <= 0) throw new EgressConfigException(EgressConfigError.ZeroInflightLimit);
        if (policy.RetryBase <= 0) throw new EgressConfigException(EgressConfigError.ZeroRetryBase);
        if (policy.RetryCeiling < policy.RetryBase)
            throw new EgressConfigException(EgressConfigError.RetryCeilingBelowBase);
        Policy = policy;
        Generation = generation;
    }

    /// <summary>The bounds in force.</summary>
    public EgressPolicy Policy { get; }

    /// <summary>The current producer incarnation.</summary>
    public long Generation { get; private set; }

    /// <summary>The sequence the next enqueue will be assigned.</summary>
    public long NextSequence { get; private set; }

    /// <summary>The cumulative acknowledgement watermark, or null before the first ack.</summary>
    public long? AckedThrough { get; private set; }

    /// <summary>Records awaiting a claim, in sequence order.</summary>
    public IReadOnlyList<EgressEnvelope<T>> Pending() => [.. _pending.Select(record => record.Envelope())];

    /// <summary>The unacknowledged window, in sequence order.</summary>
    public IReadOnlyList<EgressEnvelope<T>> Inflight() =>
        [.. _inflight.Values.Select(record => record.Envelope())];

    /// <summary>The most recent retry decision, cleared once its record moves on.</summary>
    public EgressRetry? Retry() => _retry;

    /// <summary>Assigns the next sequence and places the value in the pending projection.</summary>
    /// <param name="payload">The value to deliver.</param>
    public (EgressChange Change, long Sequence) Enqueue(T payload)
    {
        var sequence = NextSequence;
        NextSequence = sequence == long.MaxValue ? sequence : sequence + 1;
        _pending.Add(new Record { Generation = Generation, Sequence = sequence, Attempt = 0, Payload = payload });
        return (new EgressChange(Pending: true, Inflight: false, AckedThrough: false, Retry: false), sequence);
    }

    /// <summary>Moves one pending value into the unacknowledged window.</summary>
    /// <remarks>
    /// The caller supplies the generation captured by its transport attachment. An attachment from
    /// a superseded incarnation cannot claim or replay.
    /// </remarks>
    /// <param name="generation">The attachment's captured generation.</param>
    public (EgressChange Change, EgressClaimResult<T> Claim) Claim(long generation)
    {
        if (generation != Generation)
            return (default, new EgressClaimResult<T>(EgressClaimKind.StaleGeneration, Current: Generation));
        if (_inflight.Count >= Policy.InflightLimit)
            return (default, new EgressClaimResult<T>(EgressClaimKind.WindowFull));
        if (_pending.Count == 0)
            return (default, new EgressClaimResult<T>(EgressClaimKind.Empty));

        var record = _pending[0];
        _pending.RemoveAt(0);
        record.Generation = Generation;
        record.Attempt = record.Attempt == int.MaxValue ? record.Attempt : record.Attempt + 1;
        var envelope = record.Envelope();
        _inflight[record.Sequence] = record;
        var retryChanged = _retry is { } retry && retry.Sequence == envelope.Sequence;
        if (retryChanged) _retry = null;
        return (
            new EgressChange(Pending: true, Inflight: true, AckedThrough: false, Retry: retryChanged),
            new EgressClaimResult<T>(EgressClaimKind.Claimed, envelope));
    }

    /// <summary>Advances the acknowledgement watermark and prunes acknowledged records.</summary>
    /// <param name="generation">The attachment's captured generation.</param>
    /// <param name="through">The highest sequence the receiver has durably accepted.</param>
    public (EgressChange Change, EgressAckResult Ack) Ack(long generation, long through)
    {
        if (generation != Generation)
            return (default, new EgressAckResult(EgressAckKind.StaleGeneration, Current: Generation));
        if (AckedThrough is { } current && through <= current)
            return (default, new EgressAckResult(EgressAckKind.Unchanged, Through: AckedThrough));

        var inflightBefore = _inflight.Count;
        var pendingBefore = _pending.Count;
        AckedThrough = through;
        Prune(_inflight, through);
        Prune(_failed, through);
        _pending.RemoveAll(record => record.Sequence <= through);
        var retryChanged = _retry is { } retry && retry.Sequence <= through;
        if (retryChanged) _retry = null;
        return (
            new EgressChange(
                Pending: _pending.Count != pendingBefore,
                Inflight: _inflight.Count != inflightBefore,
                AckedThrough: true,
                Retry: retryChanged),
            new EgressAckResult(EgressAckKind.Advanced, Through: through));
    }

    /// <summary>Parks a failed in-flight record while budget remains; drops it once exhausted.</summary>
    /// <param name="generation">The attachment's captured generation.</param>
    /// <param name="sequence">The sequence whose send failed.</param>
    public (EgressChange Change, EgressFailureResult Failure) Fail(long generation, long sequence)
    {
        if (generation != Generation)
            return (default, new EgressFailureResult(EgressFailureKind.StaleGeneration, Current: Generation));
        if (!_inflight.Remove(sequence, out var record))
            return (default, new EgressFailureResult(EgressFailureKind.UnknownSequence));

        var exhausted = record.Attempt > Policy.RetryBudget;
        var retry = new EgressRetry(sequence, record.Attempt, Backoff(record.Attempt), exhausted);
        _retry = retry;
        if (!exhausted) _failed[sequence] = record;
        return (
            new EgressChange(Pending: false, Inflight: true, AckedThrough: false, Retry: true),
            new EgressFailureResult(exhausted ? EgressFailureKind.Exhausted : EgressFailureKind.Retrying, retry));
    }

    /// <summary>Makes a parked failed record eligible again, in sequence order.</summary>
    /// <remarks>The caller decides WHEN, from <see cref="EgressRetry.Backoff"/>; the core keeps no clock.</remarks>
    /// <param name="generation">The attachment's captured generation.</param>
    /// <param name="sequence">The parked sequence.</param>
    public (EgressChange Change, EgressRetryActionResult Action) RetryNow(long generation, long sequence)
    {
        if (generation != Generation)
            return (default, new EgressRetryActionResult(EgressRetryActionKind.StaleGeneration, Current: Generation));
        if (!_failed.Remove(sequence, out var record))
            return (default, new EgressRetryActionResult(EgressRetryActionKind.UnknownSequence));

        InsertPendingOrdered(record);
        var retryChanged = _retry is { } retry && retry.Sequence == sequence;
        if (retryChanged) _retry = null;
        return (
            new EgressChange(Pending: true, Inflight: false, AckedThrough: false, Retry: retryChanged),
            new EgressRetryActionResult(EgressRetryActionKind.Scheduled, Sequence: sequence));
    }

    /// <summary>
    /// Fences the previous attachment and replays every unacknowledged record through the new
    /// generation. The ack watermark and sequence space survive; only the producer incarnation
    /// changes.
    /// </summary>
    /// <param name="generation">The new producer incarnation.</param>
    public (EgressChange Change, EgressReconnectResult Reconnect) Reconnect(long generation)
    {
        if (generation < Generation)
            return (default, new EgressReconnectResult(EgressReconnectKind.StaleGeneration, Generation));
        if (generation == Generation)
            return (default, new EgressReconnectResult(EgressReconnectKind.Unchanged, Generation));

        Generation = generation;
        var replayed = _inflight.Count + _failed.Count;
        foreach (var record in _inflight.Values) InsertPendingOrdered(record);
        _inflight.Clear();
        foreach (var record in _failed.Values) InsertPendingOrdered(record);
        _failed.Clear();
        foreach (var record in _pending) record.Generation = generation;
        var retryChanged = _retry is not null;
        _retry = null;
        return (
            new EgressChange(Pending: replayed > 0, Inflight: replayed > 0, AckedThrough: false, Retry: retryChanged),
            new EgressReconnectResult(EgressReconnectKind.Advanced, generation, replayed));
    }

    private void InsertPendingOrdered(Record record)
    {
        var position = _pending.FindIndex(existing => existing.Sequence > record.Sequence);
        _pending.Insert(position < 0 ? _pending.Count : position, record);
    }

    private static void Prune(SortedDictionary<long, Record> records, long through)
    {
        foreach (var sequence in records.Keys.Where(sequence => sequence <= through).ToList())
            records.Remove(sequence);
    }

    private long Backoff(int attempt)
    {
        var shift = Math.Min(Math.Max(attempt - 1, 0), 62);
        var multiplier = 1L << shift;
        var backoff = Policy.RetryBase > long.MaxValue / multiplier
            ? long.MaxValue
            : Policy.RetryBase * multiplier;
        return Math.Min(backoff, Policy.RetryCeiling);
    }
}

/// <summary>Structural equality for an egress projection, so an unchanged read ripples nothing.</summary>
internal sealed class EgressEnvelopeListComparer<T> : IEqualityComparer<IReadOnlyList<EgressEnvelope<T>>>
{
    internal static readonly EgressEnvelopeListComparer<T> Instance = new();

    public bool Equals(IReadOnlyList<EgressEnvelope<T>>? x, IReadOnlyList<EgressEnvelope<T>>? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y));

    public int GetHashCode(IReadOnlyList<EgressEnvelope<T>> obj) => obj.Count;
}
