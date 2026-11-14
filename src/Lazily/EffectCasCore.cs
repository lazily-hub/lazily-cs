// EffectCasCore — generic keyed compare-and-swap effect authority (spec tag: lzeffectcas).
//
// The pure core owns effect identity, keyed single-flight, latest-wins coalescing, logical-clock
// retry timing, reconnect fencing, exact receipt matching, and the cursor of an ordered effect
// plan. Backends remain application-owned: they observe an opaque authority token and execute one
// atomic compare-and-apply (ICompareAndApply). The core performs no graph write and no I/O; every
// mutator reports whether reader-visible state changed (EffectCasChange), the same core/shell split
// IngressCore, QueueCore and EgressCore make.

namespace Lazily;

/// <summary>The stable identity of one desired effect. Never empty.</summary>
public readonly record struct EffectIdentity
{
    /// <summary>Creates an identity.</summary>
    /// <param name="value">The identity text; must be non-empty.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null or empty.</exception>
    public EffectIdentity(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("an effect identity must be non-empty", nameof(value));
        Value = value;
    }

    /// <summary>The identity text.</summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>An ordered, non-empty sequence of idempotent effect payloads.</summary>
/// <typeparam name="TPayload">The payload type.</typeparam>
public sealed class EffectPlan<TPayload>
{
    /// <summary>Creates a plan.</summary>
    /// <param name="steps">The ordered steps; must be non-empty.</param>
    /// <exception cref="ArgumentException"><paramref name="steps"/> is empty.</exception>
    public EffectPlan(IEnumerable<TPayload> steps)
    {
        Guard.NotNull(steps, nameof(steps));
        Steps = [.. steps];
        if (Steps.Count == 0)
            throw new ArgumentException("an effect plan must contain at least one step", nameof(steps));
    }

    /// <summary>A one-step plan.</summary>
    /// <param name="payload">The single step.</param>
    public static EffectPlan<TPayload> Single(TPayload payload) => new([payload]);

    /// <summary>The ordered steps.</summary>
    public IReadOnlyList<TPayload> Steps { get; }

    /// <summary>The number of steps (always at least one).</summary>
    public int Count => Steps.Count;

    internal bool SameAs(EffectPlan<TPayload> other, IEqualityComparer<TPayload> payloads) =>
        ReferenceEquals(this, other) || Steps.SequenceEqual(other.Steps, payloads);
}

/// <summary>An immutable desired effect. One epoch names exactly one identity and plan.</summary>
/// <param name="Identity">The stable effect identity.</param>
/// <param name="Epoch">The caller's monotone desire epoch for this key.</param>
/// <param name="Key">The single-flight key.</param>
/// <param name="Expected">The opaque authority the first step compares against.</param>
/// <param name="Plan">The ordered steps.</param>
public sealed record EffectIntent<TKey, TAuthority, TPayload>(
    EffectIdentity Identity,
    long Epoch,
    TKey Key,
    TAuthority Expected,
    EffectPlan<TPayload> Plan);

/// <summary>The exact identity of one adapter call.</summary>
public readonly record struct EffectAttemptIdentity(
    EffectIdentity Effect,
    long Generation,
    long Epoch,
    int Step,
    int Attempt);

/// <summary>A successful step receipt, retained as lineage for later plan steps.</summary>
public sealed record EffectReceipt<TReceipt, TAuthority>(
    EffectAttemptIdentity Attempt,
    TReceipt Receipt,
    TAuthority Authority);

/// <summary>The exact compare-and-apply call handed to an application adapter.</summary>
public sealed record EffectAttempt<TKey, TAuthority, TPayload, TReceipt>(
    EffectAttemptIdentity Identity,
    TKey Key,
    TAuthority Expected,
    TPayload Payload,
    IReadOnlyList<EffectReceipt<TReceipt, TAuthority>> PriorReceipts);

/// <summary>The only outcome categories an application adapter may return.</summary>
public enum EffectApplyKind
{
    /// <summary>The compare matched and the effect applied.</summary>
    Applied,
    /// <summary>The compare failed; the backend's authority moved on. Zero mutation.</summary>
    Superseded,
    /// <summary>A transient failure; retry later.</summary>
    Retryable,
    /// <summary>A permanent rejection.</summary>
    Rejected,
}

/// <summary>An adapter outcome.</summary>
public sealed record EffectApply<TReceipt, TAuthority, TError>(
    EffectApplyKind Kind,
    TReceipt? Receipt = default,
    TAuthority? Authority = default,
    TError? Error = default);

/// <summary>The narrow application-owned atomic authority boundary.</summary>
public interface ICompareAndApply<TKey, TAuthority, TPayload, TReceipt, TError>
{
    /// <summary>Observes the backend's current authority for <paramref name="key"/>.</summary>
    /// <param name="key">The key to observe.</param>
    TAuthority Observe(TKey key);

    /// <summary>Executes one atomic compare-and-apply.</summary>
    /// <param name="attempt">The exact attempt.</param>
    EffectApply<TReceipt, TAuthority, TError> CompareAndApply(
        EffectAttempt<TKey, TAuthority, TPayload, TReceipt> attempt);
}

/// <summary>Retained, not-yet-claimed work for one key.</summary>
public sealed record EffectPending<TKey, TAuthority, TPayload, TReceipt>(
    EffectIntent<TKey, TAuthority, TPayload> Intent,
    int NextStep,
    TAuthority Expected,
    IReadOnlyList<EffectReceipt<TReceipt, TAuthority>> Receipts,
    int NextAttempt,
    long? ReadyAt);

/// <summary>The most recent retryable failure for one key.</summary>
public sealed record EffectRetry<TError>(EffectAttemptIdentity Attempt, TError Error, long RetryAt);

/// <summary>Which terminal state an effect reached.</summary>
public enum EffectTerminalKind
{
    /// <summary>Every plan step applied.</summary>
    Applied,
    /// <summary>A newer desire or a moved authority made the effect obsolete.</summary>
    Superseded,
    /// <summary>The backend permanently rejected a step.</summary>
    Rejected,
}

/// <summary>Terminal evidence for one effect epoch, with the receipts it accumulated.</summary>
public sealed record EffectTerminal<TReceipt, TAuthority, TError>(
    EffectTerminalKind Kind,
    EffectIdentity Identity,
    long Epoch,
    IReadOnlyList<EffectReceipt<TReceipt, TAuthority>> Receipts,
    bool HasObserved = false,
    TAuthority? Observed = default,
    TError? Error = default);

/// <summary>Reader projection of one key.</summary>
public sealed record EffectCasKeyState<TKey, TAuthority, TPayload, TReceipt, TError>(
    EffectPending<TKey, TAuthority, TPayload, TReceipt>? Pending,
    EffectAttempt<TKey, TAuthority, TPayload, TReceipt>? Inflight,
    EffectRetry<TError>? Retry,
    long? AppliedThrough,
    EffectTerminal<TReceipt, TAuthority, TError>? Terminal);

/// <summary>Reader projection of the whole core.</summary>
public sealed record EffectCasSnapshot<TKey, TAuthority, TPayload, TReceipt, TError>(
    long Generation,
    IReadOnlyList<KeyValuePair<TKey, EffectCasKeyState<TKey, TAuthority, TPayload, TReceipt, TError>>> Keys);

/// <summary>The outcome category of <see cref="EffectCasCore{TKey,TAuthority,TPayload,TReceipt,TError}.UpsertDesired"/>.</summary>
public enum EffectCasUpsertKind
{
    /// <summary>The desire was retained (superseding any older pending one).</summary>
    Accepted,
    /// <summary>The identical intent was already retained.</summary>
    Unchanged,
    /// <summary>The epoch is at or behind the applied frontier.</summary>
    AlreadyApplied,
    /// <summary>A newer epoch is already retained, in flight, or terminal.</summary>
    StaleEpoch,
    /// <summary>The same epoch names a different intent.</summary>
    EpochConflict,
}

/// <summary>An upsert outcome.</summary>
public readonly record struct EffectCasUpsertResult(
    EffectCasUpsertKind Kind,
    long? AppliedThrough = null,
    long? Current = null);

/// <summary>The outcome category of a claim.</summary>
public enum EffectCasClaimKind
{
    /// <summary>The pending step moved in flight.</summary>
    Claimed,
    /// <summary>Nothing is pending for the key.</summary>
    Empty,
    /// <summary>The key already has an attempt in flight (single-flight).</summary>
    Busy,
    /// <summary>The retry time has not been reached on the logical clock.</summary>
    Waiting,
    /// <summary>The caller does not own the current generation.</summary>
    StaleGeneration,
}

/// <summary>A claim outcome.</summary>
public sealed record EffectCasClaimResult<TKey, TAuthority, TPayload, TReceipt>(
    EffectCasClaimKind Kind,
    EffectAttempt<TKey, TAuthority, TPayload, TReceipt>? Attempt = null,
    long? RetryAt = null,
    long? Current = null);

/// <summary>The outcome category of recording an applied step.</summary>
public enum EffectCasAppliedKind
{
    /// <summary>A non-final step applied; the cursor advanced.</summary>
    StepApplied,
    /// <summary>The final step applied; the frontier advanced.</summary>
    Completed,
    /// <summary>A newer desire superseded the effect at this step boundary.</summary>
    Superseded,
    /// <summary>The exact receipt was already recorded.</summary>
    Duplicate,
    /// <summary>No in-flight attempt matches.</summary>
    UnknownAttempt,
    /// <summary>The attempt belongs to a fenced generation.</summary>
    StaleGeneration,
}

/// <summary>An applied-step outcome.</summary>
public readonly record struct EffectCasAppliedResult(
    EffectCasAppliedKind Kind,
    int? NextStep = null,
    long? AppliedThrough = null,
    long? Current = null);

/// <summary>The outcome category of recording a failed step.</summary>
public enum EffectCasFailureKind
{
    /// <summary>The effect terminated as superseded.</summary>
    Superseded,
    /// <summary>The step is pending again at a logical retry time.</summary>
    Retrying,
    /// <summary>The effect terminated as rejected.</summary>
    Rejected,
    /// <summary>No in-flight attempt matches.</summary>
    UnknownAttempt,
    /// <summary>The attempt belongs to a fenced generation.</summary>
    StaleGeneration,
}

/// <summary>A failed-step outcome.</summary>
public readonly record struct EffectCasFailureResult(
    EffectCasFailureKind Kind,
    long? RetryAt = null,
    long? Current = null);

/// <summary>The outcome category of a reconnect.</summary>
public enum EffectCasReconnectKind
{
    /// <summary>The generation advanced; in-flight attempts were requeued or superseded.</summary>
    Advanced,
    /// <summary>The requested generation is already current.</summary>
    Unchanged,
    /// <summary>The requested generation is behind the current fence.</summary>
    StaleGeneration,
}

/// <summary>A reconnect outcome.</summary>
public readonly record struct EffectCasReconnectResult(
    EffectCasReconnectKind Kind,
    long Generation,
    int Requeued = 0,
    int Superseded = 0);

/// <summary>Whether a transition changed reader-visible state.</summary>
public readonly record struct EffectCasChange(bool State);

/// <summary>
/// Pure keyed effect-CAS orchestration authority: stable identity, opaque expected authority,
/// keyed single-flight, latest-wins supersession, logical-clock retry, reconnect, exact terminal
/// receipts, and resumable ordered plans.
/// </summary>
/// <typeparam name="TKey">The single-flight key.</typeparam>
/// <typeparam name="TAuthority">The opaque backend authority token.</typeparam>
/// <typeparam name="TPayload">A plan step payload.</typeparam>
/// <typeparam name="TReceipt">A backend receipt.</typeparam>
/// <typeparam name="TError">A backend error.</typeparam>
public sealed class EffectCasCore<TKey, TAuthority, TPayload, TReceipt, TError>
    where TKey : notnull
{
    private sealed record Inflight(
        EffectPending<TKey, TAuthority, TPayload, TReceipt> Retained,
        EffectAttempt<TKey, TAuthority, TPayload, TReceipt> Attempt);

    private sealed class Entry
    {
        internal EffectPending<TKey, TAuthority, TPayload, TReceipt>? Pending;
        internal Inflight? Inflight;
        internal EffectRetry<TError>? Retry;
        internal long? AppliedThrough;
        internal EffectTerminal<TReceipt, TAuthority, TError>? Terminal;
        internal EffectIntent<TKey, TAuthority, TPayload>? TerminalIntent;
    }

    private readonly SortedDictionary<TKey, Entry> _keys;
    private readonly IEqualityComparer<TAuthority> _authorities;
    private readonly IEqualityComparer<TPayload> _payloads;
    private readonly IEqualityComparer<TReceipt> _receipts;

    /// <summary>Builds an empty core at <paramref name="generation"/>.</summary>
    /// <param name="generation">The initial actor generation.</param>
    /// <param name="keyOrder">The key ordering. Defaults to <see cref="Comparer{T}.Default"/>.</param>
    /// <param name="authorities">Authority equality. Defaults to <see cref="EqualityComparer{T}.Default"/>.</param>
    /// <param name="payloads">Payload equality.</param>
    /// <param name="receipts">Receipt equality.</param>
    public EffectCasCore(
        long generation,
        IComparer<TKey>? keyOrder = null,
        IEqualityComparer<TAuthority>? authorities = null,
        IEqualityComparer<TPayload>? payloads = null,
        IEqualityComparer<TReceipt>? receipts = null)
    {
        Generation = generation;
        _keys = new SortedDictionary<TKey, Entry>(keyOrder ?? Comparer<TKey>.Default);
        _authorities = authorities ?? EqualityComparer<TAuthority>.Default;
        _payloads = payloads ?? EqualityComparer<TPayload>.Default;
        _receipts = receipts ?? EqualityComparer<TReceipt>.Default;
    }

    /// <summary>The current actor generation.</summary>
    public long Generation { get; private set; }

    /// <summary>Reader projection of every key, in key order.</summary>
    public EffectCasSnapshot<TKey, TAuthority, TPayload, TReceipt, TError> Snapshot() =>
        new(Generation, [.. _keys.Select(pair => KeyValuePair.Create(pair.Key, Project(pair.Value)))]);

    /// <summary>Reader projection of one key, or null when the key was never seen.</summary>
    /// <param name="key">The key to project.</param>
    public EffectCasKeyState<TKey, TAuthority, TPayload, TReceipt, TError>? State(TKey key) =>
        _keys.TryGetValue(key, out var entry) ? Project(entry) : null;

    /// <summary>Keys with claimable work at logical time <paramref name="now"/>.</summary>
    /// <param name="now">The logical clock.</param>
    public IReadOnlyList<TKey> PendingKeys(long now) =>
        [.. _keys
            .Where(pair => pair.Value.Inflight is null
                && pair.Value.Pending is { } pending
                && (pending.ReadyAt is null || pending.ReadyAt <= now))
            .Select(pair => pair.Key)];

    /// <summary>Retains a desired effect, superseding any older pending desire for its key.</summary>
    /// <param name="intent">The desired effect.</param>
    public (EffectCasChange Change, EffectCasUpsertResult Upsert) UpsertDesired(
        EffectIntent<TKey, TAuthority, TPayload> intent)
    {
        Guard.NotNull(intent, nameof(intent));
        var entry = EntryFor(intent.Key);
        if (entry.AppliedThrough is { } appliedThrough && intent.Epoch <= appliedThrough)
            return (default, new EffectCasUpsertResult(EffectCasUpsertKind.AlreadyApplied, AppliedThrough: appliedThrough));

        var pendingIntent = entry.Pending?.Intent;
        var inflightIntent = entry.Inflight?.Retained.Intent;
        long? current = null;
        foreach (var epoch in new[] { pendingIntent?.Epoch, inflightIntent?.Epoch, entry.TerminalIntent?.Epoch })
        {
            if (epoch is { } value && (current is null || value > current)) current = value;
        }

        if (current is { } currentEpoch)
        {
            if (intent.Epoch < currentEpoch)
                return (default, new EffectCasUpsertResult(EffectCasUpsertKind.StaleEpoch, Current: currentEpoch));
            if (intent.Epoch == currentEpoch)
            {
                var same = SameIntent(pendingIntent, intent)
                    || SameIntent(inflightIntent, intent)
                    || SameIntent(entry.TerminalIntent, intent);
                return (default, new EffectCasUpsertResult(
                    same ? EffectCasUpsertKind.Unchanged : EffectCasUpsertKind.EpochConflict));
            }
        }

        if (entry.Pending is { } superseded)
        {
            entry.TerminalIntent = superseded.Intent;
            entry.Terminal = new EffectTerminal<TReceipt, TAuthority, TError>(
                EffectTerminalKind.Superseded,
                superseded.Intent.Identity,
                superseded.Intent.Epoch,
                superseded.Receipts);
        }

        entry.Pending = new EffectPending<TKey, TAuthority, TPayload, TReceipt>(
            intent,
            NextStep: 0,
            Expected: intent.Expected,
            Receipts: [],
            NextAttempt: 1,
            ReadyAt: null);
        entry.Retry = null;
        return (new EffectCasChange(true), new EffectCasUpsertResult(EffectCasUpsertKind.Accepted));
    }

    /// <summary>Moves a key's pending step in flight (single-flight per key).</summary>
    /// <param name="key">The key to claim.</param>
    /// <param name="generation">The caller's actor generation.</param>
    /// <param name="now">The logical clock.</param>
    public (EffectCasChange Change, EffectCasClaimResult<TKey, TAuthority, TPayload, TReceipt> Claim) Claim(
        TKey key,
        long generation,
        long now)
    {
        if (generation != Generation)
            return (default, new(EffectCasClaimKind.StaleGeneration, Current: Generation));
        if (!_keys.TryGetValue(key, out var entry))
            return (default, new(EffectCasClaimKind.Empty));
        if (entry.Inflight is not null)
            return (default, new(EffectCasClaimKind.Busy));
        if (entry.Pending is not { } pending)
            return (default, new(EffectCasClaimKind.Empty));
        if (pending.ReadyAt is { } retryAt && now < retryAt)
            return (default, new(EffectCasClaimKind.Waiting, RetryAt: retryAt));

        entry.Pending = null;
        var attempt = new EffectAttempt<TKey, TAuthority, TPayload, TReceipt>(
            new EffectAttemptIdentity(
                pending.Intent.Identity,
                generation,
                pending.Intent.Epoch,
                pending.NextStep,
                pending.NextAttempt),
            pending.Intent.Key,
            pending.Expected,
            pending.Intent.Plan.Steps[pending.NextStep],
            pending.Receipts);
        entry.Inflight = new Inflight(pending, attempt);
        return (new EffectCasChange(true), new(EffectCasClaimKind.Claimed, attempt));
    }

    /// <summary>Records that the adapter applied an attempt.</summary>
    /// <param name="key">The attempt's key.</param>
    /// <param name="attempt">The exact attempt identity.</param>
    /// <param name="receipt">The backend receipt.</param>
    /// <param name="authority">The backend authority after the apply.</param>
    public (EffectCasChange Change, EffectCasAppliedResult Applied) RecordApplied(
        TKey key,
        EffectAttemptIdentity attempt,
        TReceipt receipt,
        TAuthority authority)
    {
        if (attempt.Generation != Generation)
            return (default, new(EffectCasAppliedKind.StaleGeneration, Current: Generation));
        if (!_keys.TryGetValue(key, out var entry))
            return (default, new(EffectCasAppliedKind.UnknownAttempt));
        if (entry.Terminal is { } terminal
            && terminal.Receipts.Any(known => known.Attempt == attempt && _receipts.Equals(known.Receipt, receipt)))
            return (default, new(EffectCasAppliedKind.Duplicate));
        if (entry.Inflight is not { } inflight || inflight.Attempt.Identity != attempt)
            return (default, new(EffectCasAppliedKind.UnknownAttempt));

        entry.Inflight = null;
        var retained = inflight.Retained with
        {
            Receipts = [.. inflight.Retained.Receipts, new EffectReceipt<TReceipt, TAuthority>(attempt, receipt, authority)],
        };
        entry.Retry = null;

        if (entry.Pending is { } newer && newer.Intent.Epoch > retained.Intent.Epoch)
        {
            entry.TerminalIntent = retained.Intent;
            entry.Terminal = new EffectTerminal<TReceipt, TAuthority, TError>(
                EffectTerminalKind.Superseded,
                retained.Intent.Identity,
                retained.Intent.Epoch,
                retained.Receipts,
                HasObserved: true,
                Observed: authority);
            return (new EffectCasChange(true), new(EffectCasAppliedKind.Superseded));
        }

        var nextStep = retained.NextStep + 1;
        if (nextStep < retained.Intent.Plan.Count)
        {
            entry.Pending = retained with
            {
                NextStep = nextStep,
                Expected = authority,
                ReadyAt = null,
                NextAttempt = 1,
            };
            return (new EffectCasChange(true), new(EffectCasAppliedKind.StepApplied, NextStep: nextStep));
        }

        var epoch = retained.Intent.Epoch;
        entry.AppliedThrough = entry.AppliedThrough is { } old ? Math.Max(old, epoch) : epoch;
        entry.TerminalIntent = retained.Intent;
        entry.Terminal = new EffectTerminal<TReceipt, TAuthority, TError>(
            EffectTerminalKind.Applied,
            retained.Intent.Identity,
            epoch,
            retained.Receipts);
        return (new EffectCasChange(true), new(EffectCasAppliedKind.Completed, AppliedThrough: entry.AppliedThrough));
    }

    /// <summary>Records that the backend's authority moved on: the effect terminates superseded.</summary>
    /// <param name="key">The attempt's key.</param>
    /// <param name="attempt">The exact attempt identity.</param>
    /// <param name="observed">The authority the backend observed.</param>
    public (EffectCasChange Change, EffectCasFailureResult Failure) RecordSuperseded(
        TKey key,
        EffectAttemptIdentity attempt,
        TAuthority observed) =>
        FinishFailed(key, attempt, FailureKind.Superseded, observed, default, 0);

    /// <summary>Records a transient failure; the step is retried at <paramref name="retryAt"/>.</summary>
    /// <param name="key">The attempt's key.</param>
    /// <param name="attempt">The exact attempt identity.</param>
    /// <param name="error">The backend error.</param>
    /// <param name="retryAt">The logical time the step becomes claimable again.</param>
    public (EffectCasChange Change, EffectCasFailureResult Failure) RecordRetryable(
        TKey key,
        EffectAttemptIdentity attempt,
        TError error,
        long retryAt) =>
        FinishFailed(key, attempt, FailureKind.Retryable, default, error, retryAt);

    /// <summary>Records a permanent rejection: the effect terminates rejected.</summary>
    /// <param name="key">The attempt's key.</param>
    /// <param name="attempt">The exact attempt identity.</param>
    /// <param name="error">The backend error.</param>
    public (EffectCasChange Change, EffectCasFailureResult Failure) RecordRejected(
        TKey key,
        EffectAttemptIdentity attempt,
        TError error) =>
        FinishFailed(key, attempt, FailureKind.Rejected, default, error, 0);

    /// <summary>
    /// Records an adapter outcome. Exactly one of <paramref name="applied"/> or
    /// <paramref name="failure"/> is meaningful, selected by <paramref name="outcome"/>'s kind.
    /// </summary>
    /// <param name="key">The attempt's key.</param>
    /// <param name="attempt">The exact attempt identity.</param>
    /// <param name="outcome">What the adapter returned.</param>
    /// <param name="retryAt">The logical retry time used when the outcome is retryable.</param>
    /// <param name="applied">The applied outcome, for an Applied adapter result.</param>
    /// <param name="failure">The failure outcome, for every other adapter result.</param>
    public EffectCasChange RecordOutcome(
        TKey key,
        EffectAttemptIdentity attempt,
        EffectApply<TReceipt, TAuthority, TError> outcome,
        long retryAt,
        out EffectCasAppliedResult? applied,
        out EffectCasFailureResult? failure)
    {
        Guard.NotNull(outcome, nameof(outcome));
        applied = null;
        failure = null;
        EffectCasChange change;
        switch (outcome.Kind)
        {
            case EffectApplyKind.Applied:
                (change, var a) = RecordApplied(key, attempt, outcome.Receipt!, outcome.Authority!);
                applied = a;
                return change;
            case EffectApplyKind.Superseded:
                (change, var s) = RecordSuperseded(key, attempt, outcome.Authority!);
                failure = s;
                return change;
            case EffectApplyKind.Retryable:
                (change, var r) = RecordRetryable(key, attempt, outcome.Error!, retryAt);
                failure = r;
                return change;
            case EffectApplyKind.Rejected:
                (change, var j) = RecordRejected(key, attempt, outcome.Error!);
                failure = j;
                return change;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Kind, "unknown adapter outcome");
        }
    }

    /// <summary>
    /// Fences the previous actor generation: every in-flight attempt is requeued under the SAME
    /// effect identity with the next attempt number, or terminated superseded when a newer desire
    /// is already pending.
    /// </summary>
    /// <param name="newGeneration">The new actor generation.</param>
    public (EffectCasChange Change, EffectCasReconnectResult Reconnect) Reconnect(long newGeneration)
    {
        if (newGeneration < Generation)
            return (default, new(EffectCasReconnectKind.StaleGeneration, Generation));
        if (newGeneration == Generation)
            return (default, new(EffectCasReconnectKind.Unchanged, Generation));

        Generation = newGeneration;
        var requeued = 0;
        var superseded = 0;
        foreach (var entry in _keys.Values)
        {
            if (entry.Inflight is not { } inflight) continue;
            entry.Inflight = null;
            var retained = inflight.Retained;
            if (entry.Pending is { } newer && newer.Intent.Epoch > retained.Intent.Epoch)
            {
                entry.TerminalIntent = retained.Intent;
                entry.Terminal = new EffectTerminal<TReceipt, TAuthority, TError>(
                    EffectTerminalKind.Superseded,
                    retained.Intent.Identity,
                    retained.Intent.Epoch,
                    retained.Receipts);
                superseded++;
            }
            else
            {
                entry.Pending = retained with { NextAttempt = Increment(retained.NextAttempt), ReadyAt = null };
                requeued++;
            }
        }

        return (new EffectCasChange(true), new(EffectCasReconnectKind.Advanced, newGeneration, requeued, superseded));
    }

    private enum FailureKind
    {
        Superseded,
        Retryable,
        Rejected,
    }

    private (EffectCasChange, EffectCasFailureResult) FinishFailed(
        TKey key,
        EffectAttemptIdentity attempt,
        FailureKind kind,
        TAuthority? observed,
        TError? error,
        long retryAt)
    {
        if (attempt.Generation != Generation)
            return (default, new(EffectCasFailureKind.StaleGeneration, Current: Generation));
        if (!_keys.TryGetValue(key, out var entry))
            return (default, new(EffectCasFailureKind.UnknownAttempt));
        if (entry.Inflight is not { } inflight || inflight.Attempt.Identity != attempt)
            return (default, new(EffectCasFailureKind.UnknownAttempt));

        entry.Inflight = null;
        var retained = inflight.Retained;
        var newerPending = entry.Pending is { } newer && newer.Intent.Epoch > retained.Intent.Epoch;
        var identity = retained.Intent.Identity;
        var epoch = retained.Intent.Epoch;
        switch (kind)
        {
            case FailureKind.Superseded:
                entry.Retry = null;
                entry.TerminalIntent = retained.Intent;
                entry.Terminal = new EffectTerminal<TReceipt, TAuthority, TError>(
                    EffectTerminalKind.Superseded, identity, epoch, retained.Receipts,
                    HasObserved: true, Observed: observed);
                return (new EffectCasChange(true), new(EffectCasFailureKind.Superseded));
            case FailureKind.Retryable when !newerPending:
                entry.Retry = new EffectRetry<TError>(attempt, error!, retryAt);
                entry.Pending = retained with { NextAttempt = Increment(retained.NextAttempt), ReadyAt = retryAt };
                return (new EffectCasChange(true), new(EffectCasFailureKind.Retrying, RetryAt: retryAt));
            case FailureKind.Retryable:
                entry.Retry = null;
                entry.TerminalIntent = retained.Intent;
                entry.Terminal = new EffectTerminal<TReceipt, TAuthority, TError>(
                    EffectTerminalKind.Superseded, identity, epoch, retained.Receipts);
                return (new EffectCasChange(true), new(EffectCasFailureKind.Superseded));
            default:
                entry.Retry = null;
                entry.TerminalIntent = retained.Intent;
                entry.Terminal = new EffectTerminal<TReceipt, TAuthority, TError>(
                    EffectTerminalKind.Rejected, identity, epoch, retained.Receipts, Error: error);
                return (new EffectCasChange(true), new(EffectCasFailureKind.Rejected));
        }
    }

    private Entry EntryFor(TKey key)
    {
        if (!_keys.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            _keys.Add(key, entry);
        }
        return entry;
    }

    private static EffectCasKeyState<TKey, TAuthority, TPayload, TReceipt, TError> Project(Entry entry) =>
        new(entry.Pending, entry.Inflight?.Attempt, entry.Retry, entry.AppliedThrough, entry.Terminal);

    private bool SameIntent(
        EffectIntent<TKey, TAuthority, TPayload>? existing,
        EffectIntent<TKey, TAuthority, TPayload> intent) =>
        existing is not null
        && existing.Identity == intent.Identity
        && existing.Epoch == intent.Epoch
        && EqualityComparer<TKey>.Default.Equals(existing.Key, intent.Key)
        && _authorities.Equals(existing.Expected, intent.Expected)
        && existing.Plan.SameAs(intent.Plan, _payloads);

    private static int Increment(int value) => value == int.MaxValue ? value : value + 1;
}
