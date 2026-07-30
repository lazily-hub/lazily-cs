namespace Lazily.Tests;

/// <summary>
/// What every ingress flavor must be able to do for the shared corpus to replay against it.
/// </summary>
/// <remarks>
/// The reader-kind probes (<c>*IsValid</c>) are the whole reason this is an interface rather than
/// three copies of the runner: <c>invalidates</c> is a claim about the GRAPH, and only the shell can
/// answer it. Nothing in the op surface is async-coloured — an admission decision awaits nothing —
/// so the async model bridges only its READS, which are Task-typed by construction on that plane.
/// </remarks>
public interface IIngressModel : IDisposable
{
    /// <summary>The flavor's name, used in assertion messages.</summary>
    string Name { get; }

    /// <summary>Opens (or reopens) a keyed scope.</summary>
    /// <param name="key">The scope.</param>
    /// <param name="generation">The producer incarnation.</param>
    void Open(string key, long generation);

    /// <summary>Admits one decoded envelope.</summary>
    /// <param name="envelope">The envelope.</param>
    IngressAdmission Admit(IngressEnvelope<string, long> envelope);

    /// <summary>Suspends a scope, retaining its watermark.</summary>
    /// <param name="key">The scope.</param>
    ReplayRequest? Suspend(string key);

    /// <summary>Reconnects a scope, clearing its error streak.</summary>
    /// <param name="key">The scope.</param>
    /// <param name="generation">The producer incarnation to resume under.</param>
    ReplayRequest Reconnect(string key, long generation);

    /// <summary>Closes a scope.</summary>
    /// <param name="key">The scope.</param>
    void Close(string key);

    /// <summary>Records a transport/decode failure.</summary>
    /// <param name="key">The scope.</param>
    /// <param name="error">What went wrong.</param>
    void Fail(string key, IngressError error);

    /// <summary>Advances logical time.</summary>
    /// <param name="now">The new logical now.</param>
    void Tick(long now);

    /// <summary>Drains a scope's coalesced window.</summary>
    /// <param name="key">The scope.</param>
    Optional<long> Drain(string key);

    /// <summary>Reads the coalesced window, warming its reader cache.</summary>
    /// <param name="key">The scope.</param>
    Optional<long> Value(string key);

    /// <summary>Reads derived readiness, warming its reader cache.</summary>
    /// <param name="key">The scope.</param>
    IngressReadiness Readiness(string key);

    /// <summary>Reads derived authority, warming its reader cache.</summary>
    /// <param name="key">The scope.</param>
    IngressAuthority? Authority(string key);

    /// <summary>Reads the derived retry decision, warming its reader cache.</summary>
    /// <param name="key">The scope.</param>
    IngressRetry? Retry(string key);

    /// <summary>Reads the accepted-receipt channel's length, warming its reader cache.</summary>
    int AcceptedLen();

    /// <summary>Reads the dropped-receipt channel's length, warming its reader cache.</summary>
    int DroppedLen();

    /// <summary>Reads the error-receipt channel's length, warming its reader cache.</summary>
    int ErrorsLen();

    /// <summary>Reads the derived schedule, warming its reader cache.</summary>
    IngressSchedule Schedule();

    /// <summary>Whether the value reader's cache is current.</summary>
    /// <param name="key">The scope.</param>
    bool ValueIsValid(string key);

    /// <summary>Whether the readiness reader's cache is current.</summary>
    /// <param name="key">The scope.</param>
    bool ReadinessIsValid(string key);

    /// <summary>Whether the authority reader's cache is current.</summary>
    /// <param name="key">The scope.</param>
    bool AuthorityIsValid(string key);

    /// <summary>Whether the retry reader's cache is current.</summary>
    /// <param name="key">The scope.</param>
    bool RetryIsValid(string key);

    /// <summary>Whether the accepted-receipt reader's cache is current.</summary>
    bool AcceptedIsValid();

    /// <summary>Whether the dropped-receipt reader's cache is current.</summary>
    bool DroppedIsValid();

    /// <summary>Whether the error-receipt reader's cache is current.</summary>
    bool ErrorsIsValid();

    /// <summary>Non-reactive projection of a scope.</summary>
    /// <param name="key">The scope.</param>
    IngressScopeView? View(string key);
}

/// <summary>The corpus replayed against the single-threaded <see cref="IngressCell{TKey,TValue}"/>.</summary>
public sealed class SyncIngressModel : IIngressModel
{
    private readonly IngressCell<string, long> _cell;

    /// <summary>Builds the single-threaded flavor.</summary>
    /// <param name="policy">The bounds in force.</param>
    /// <param name="merge">The merge algebra.</param>
    /// <param name="transport">How envelopes arrive.</param>
    /// <param name="pollInterval">The bounded poll period.</param>
    public SyncIngressModel(
        IngressPolicy policy,
        MergePolicy<long> merge,
        IngressTransportKind transport,
        long pollInterval)
    {
        var ctx = new Context();
        _cell = new IngressCell<string, long>(ctx, policy, merge, transport, pollInterval);
    }

    /// <inheritdoc/>
    public string Name => "IngressCell";

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public void Open(string key, long generation) => _cell.Open(key, generation);

    /// <inheritdoc/>
    public IngressAdmission Admit(IngressEnvelope<string, long> envelope) => _cell.Admit(envelope);

    /// <inheritdoc/>
    public ReplayRequest? Suspend(string key) => _cell.Suspend(key);

    /// <inheritdoc/>
    public ReplayRequest Reconnect(string key, long generation) => _cell.Reconnect(key, generation);

    /// <inheritdoc/>
    public void Close(string key) => _cell.Close(key);

    /// <inheritdoc/>
    public void Fail(string key, IngressError error) => _cell.Fail(key, error);

    /// <inheritdoc/>
    public void Tick(long now) => _cell.Tick(now);

    /// <inheritdoc/>
    public Optional<long> Drain(string key) => _cell.Drain(key);

    /// <inheritdoc/>
    public Optional<long> Value(string key) => _cell.Value(key);

    /// <inheritdoc/>
    public IngressReadiness Readiness(string key) => _cell.Readiness(key);

    /// <inheritdoc/>
    public IngressAuthority? Authority(string key) => _cell.Authority(key);

    /// <inheritdoc/>
    public IngressRetry? Retry(string key) => _cell.Retry(key);

    /// <inheritdoc/>
    public int AcceptedLen() => _cell.Accepted().Count;

    /// <inheritdoc/>
    public int DroppedLen() => _cell.Dropped().Count;

    /// <inheritdoc/>
    public int ErrorsLen() => _cell.Errors().Count;

    /// <inheritdoc/>
    public IngressSchedule Schedule() => _cell.Schedule();

    /// <inheritdoc/>
    public bool ValueIsValid(string key) => _cell.ValueHandle(key).Peek(out _);

    /// <inheritdoc/>
    public bool ReadinessIsValid(string key) => _cell.ReadinessHandle(key).Peek(out _);

    /// <inheritdoc/>
    public bool AuthorityIsValid(string key) => _cell.AuthorityHandle(key).Peek(out _);

    /// <inheritdoc/>
    public bool RetryIsValid(string key) => _cell.RetryHandle(key).Peek(out _);

    /// <inheritdoc/>
    public bool AcceptedIsValid() => _cell.AcceptedHandle.Peek(out _);

    /// <inheritdoc/>
    public bool DroppedIsValid() => _cell.DroppedHandle.Peek(out _);

    /// <inheritdoc/>
    public bool ErrorsIsValid() => _cell.ErrorsHandle.Peek(out _);

    /// <inheritdoc/>
    public IngressScopeView? View(string key) => _cell.View(key);
}

/// <summary>
/// The corpus replayed against <see cref="ThreadSafeIngressCell{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// The flavor whose invalidation runs OUTSIDE the core lock and fans out through one batch. A shell
/// that cleared each root separately would still pass every value assertion in the corpus and only
/// fail the frontier-walk gate in <c>IngressCellTests</c>.
/// </remarks>
public sealed class ThreadSafeIngressModel : IIngressModel
{
    private readonly ThreadSafeContext _ctx = new();
    private readonly ThreadSafeIngressCell<string, long> _cell;

    /// <summary>Builds the thread-safe flavor.</summary>
    /// <param name="policy">The bounds in force.</param>
    /// <param name="merge">The merge algebra.</param>
    /// <param name="transport">How envelopes arrive.</param>
    /// <param name="pollInterval">The bounded poll period.</param>
    public ThreadSafeIngressModel(
        IngressPolicy policy,
        MergePolicy<long> merge,
        IngressTransportKind transport,
        long pollInterval) =>
        _cell = new ThreadSafeIngressCell<string, long>(
            _ctx, policy, merge, transport, pollInterval);

    /// <inheritdoc/>
    public string Name => "ThreadSafeIngressCell";

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public void Open(string key, long generation) => _cell.Open(key, generation);

    /// <inheritdoc/>
    public IngressAdmission Admit(IngressEnvelope<string, long> envelope) => _cell.Admit(envelope);

    /// <inheritdoc/>
    public ReplayRequest? Suspend(string key) => _cell.Suspend(key);

    /// <inheritdoc/>
    public ReplayRequest Reconnect(string key, long generation) => _cell.Reconnect(key, generation);

    /// <inheritdoc/>
    public void Close(string key) => _cell.Close(key);

    /// <inheritdoc/>
    public void Fail(string key, IngressError error) => _cell.Fail(key, error);

    /// <inheritdoc/>
    public void Tick(long now) => _cell.Tick(now);

    /// <inheritdoc/>
    public Optional<long> Drain(string key) => _cell.Drain(key);

    /// <inheritdoc/>
    public Optional<long> Value(string key) => _cell.Value(key);

    /// <inheritdoc/>
    public IngressReadiness Readiness(string key) => _cell.Readiness(key);

    /// <inheritdoc/>
    public IngressAuthority? Authority(string key) => _cell.Authority(key);

    /// <inheritdoc/>
    public IngressRetry? Retry(string key) => _cell.Retry(key);

    /// <inheritdoc/>
    public int AcceptedLen() => _cell.Accepted().Count;

    /// <inheritdoc/>
    public int DroppedLen() => _cell.Dropped().Count;

    /// <inheritdoc/>
    public int ErrorsLen() => _cell.Errors().Count;

    /// <inheritdoc/>
    public IngressSchedule Schedule() => _cell.Schedule();

    /// <inheritdoc/>
    public bool ValueIsValid(string key) => Probe(_cell.ValueHandle(key));

    /// <inheritdoc/>
    public bool ReadinessIsValid(string key) => Probe(_cell.ReadinessHandle(key));

    /// <inheritdoc/>
    public bool AuthorityIsValid(string key) => Probe(_cell.AuthorityHandle(key));

    /// <inheritdoc/>
    public bool RetryIsValid(string key) => Probe(_cell.RetryHandle(key));

    /// <inheritdoc/>
    public bool AcceptedIsValid() => Probe(_cell.AcceptedHandle);

    /// <inheritdoc/>
    public bool DroppedIsValid() => Probe(_cell.DroppedHandle);

    /// <inheritdoc/>
    public bool ErrorsIsValid() => Probe(_cell.ErrorsHandle);

    /// <inheritdoc/>
    public IngressScopeView? View(string key) => _cell.View(key);

    // The handle is resolved BEFORE the lock: minting a reader takes the table lock and then the
    // context lock, so asking for it from inside the context lock would invert that order.
    private bool Probe<T>(Computed<T> handle) => _ctx.WithLock(inner =>
    {
        _ = inner;
        return handle.Peek(out _);
    });
}

/// <summary>The corpus replayed against <see cref="AsyncIngressCell{TKey,TValue}"/>.</summary>
/// <remarks>
/// The plane where an invalidation cascade that stops one level below the write is invisible to a
/// synchronous replay: an async slot read short-circuits on <c>Resolved</c>, so a reader left
/// resolved serves its cached value forever and no pull chain rescues it. The reads block here
/// exactly as <see cref="AsyncGraphModel"/> does — the ingress algebra awaits nothing, so there is
/// no settle step in the op path at all.
/// </remarks>
public sealed class AsyncIngressModel : IIngressModel
{
    private readonly AsyncContext _ctx = new();
    private readonly AsyncIngressCell<string, long> _cell;

    /// <summary>Builds the async flavor.</summary>
    /// <param name="policy">The bounds in force.</param>
    /// <param name="merge">The merge algebra.</param>
    /// <param name="transport">How envelopes arrive.</param>
    /// <param name="pollInterval">The bounded poll period.</param>
    public AsyncIngressModel(
        IngressPolicy policy,
        MergePolicy<long> merge,
        IngressTransportKind transport,
        long pollInterval) =>
        _cell = new AsyncIngressCell<string, long>(_ctx, policy, merge, transport, pollInterval);

    /// <inheritdoc/>
    public string Name => "AsyncIngressCell";

    /// <inheritdoc/>
    public void Dispose()
    {
        _ctx.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public void Open(string key, long generation) => _cell.Open(key, generation);

    /// <inheritdoc/>
    public IngressAdmission Admit(IngressEnvelope<string, long> envelope) => _cell.Admit(envelope);

    /// <inheritdoc/>
    public ReplayRequest? Suspend(string key) => _cell.Suspend(key);

    /// <inheritdoc/>
    public ReplayRequest Reconnect(string key, long generation) => _cell.Reconnect(key, generation);

    /// <inheritdoc/>
    public void Close(string key) => _cell.Close(key);

    /// <inheritdoc/>
    public void Fail(string key, IngressError error) => _cell.Fail(key, error);

    /// <inheritdoc/>
    public void Tick(long now) => _cell.Tick(now);

    /// <inheritdoc/>
    public Optional<long> Drain(string key) => _cell.Drain(key);

    /// <inheritdoc/>
    public Optional<long> Value(string key) => Block(_cell.ValueAsync(key));

    /// <inheritdoc/>
    public IngressReadiness Readiness(string key) => Block(_cell.ReadinessAsync(key));

    /// <inheritdoc/>
    public IngressAuthority? Authority(string key) => Block(_cell.AuthorityAsync(key));

    /// <inheritdoc/>
    public IngressRetry? Retry(string key) => Block(_cell.RetryAsync(key));

    /// <inheritdoc/>
    public int AcceptedLen() => Block(_cell.AcceptedAsync()).Count;

    /// <inheritdoc/>
    public int DroppedLen() => Block(_cell.DroppedAsync()).Count;

    /// <inheritdoc/>
    public int ErrorsLen() => Block(_cell.ErrorsAsync()).Count;

    /// <inheritdoc/>
    public IngressSchedule Schedule() => Block(_cell.ScheduleAsync());

    /// <inheritdoc/>
    public bool ValueIsValid(string key) => _cell.ValueHandle(key).TryGet(out _);

    /// <inheritdoc/>
    public bool ReadinessIsValid(string key) => _cell.ReadinessHandle(key).TryGet(out _);

    /// <inheritdoc/>
    public bool AuthorityIsValid(string key) => _cell.AuthorityHandle(key).TryGet(out _);

    /// <inheritdoc/>
    public bool RetryIsValid(string key) => _cell.RetryHandle(key).TryGet(out _);

    /// <inheritdoc/>
    public bool AcceptedIsValid() => _cell.AcceptedHandle.TryGet(out _);

    /// <inheritdoc/>
    public bool DroppedIsValid() => _cell.DroppedHandle.TryGet(out _);

    /// <inheritdoc/>
    public bool ErrorsIsValid() => _cell.ErrorsHandle.TryGet(out _);

    /// <inheritdoc/>
    public IngressScopeView? View(string key) => _cell.View(key);

    private static T Block<T>(Task<T> task) => task.GetAwaiter().GetResult();
}
