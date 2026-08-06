using R3;

namespace Lazily.R3;

/// <summary>Optional bridges between Lazily state and R3 observables.</summary>
public static class R3Adapters
{
    /// <summary>
    /// Projects Lazily state as a cold R3 observable. Every subscription owns one Effect and
    /// emits the current value immediately, then distinct updates.
    /// </summary>
    public static Observable<T> ToR3State<T>(
        this Context context,
        Func<Compute, T> read,
        IEqualityComparer<T>? comparer = null) =>
        new LazilyStateObservable<T>(context, read, comparer ?? EqualityComparer<T>.Default);

    /// <summary>
    /// Projects Lazily state through one explicitly shared Effect. The latest value is replayed
    /// to late subscribers.
    /// </summary>
    public static SharedR3State<T> ToSharedR3State<T>(
        this Context context,
        Func<Compute, T> read,
        IEqualityComparer<T>? comparer = null) =>
        new(context, read, comparer ?? EqualityComparer<T>.Default);

    /// <summary>
    /// Binds an R3 stream to a Lazily source on the creating thread. Cross-thread ingress is
    /// rejected; use the ThreadSafeContext overload for concurrent producers.
    /// </summary>
    public static R3StateBinding<T> BindR3State<T>(
        this Context context,
        Observable<T> observable,
        T initial,
        IEqualityComparer<T>? comparer = null) =>
        R3StateBinding<T>.ForContext(context, observable, initial, comparer);

    /// <summary>Binds an R3 stream to a lock-serialized Lazily source.</summary>
    public static R3StateBinding<T> BindR3State<T>(
        this ThreadSafeContext context,
        Observable<T> observable,
        T initial,
        IEqualityComparer<T>? comparer = null) =>
        R3StateBinding<T>.ForThreadSafeContext(context, observable, initial, comparer);
}

internal sealed class LazilyStateObservable<T>(
    Context context,
    Func<Compute, T> read,
    IEqualityComparer<T> comparer) : Observable<T>
{
    protected override IDisposable SubscribeCore(Observer<T> observer)
    {
        var hasValue = false;
        var last = default(T)!;
        var effect = context.Effect(compute =>
        {
            try
            {
                var next = read(compute);
                if (!hasValue || !comparer.Equals(last, next))
                {
                    hasValue = true;
                    last = next;
                    observer.OnNext(next);
                }
            }
            catch (Exception error)
            {
                // R3 errors are recoverable; the Effect remains attached to dependencies read
                // before the exception and may produce a later value.
                observer.OnErrorResume(error);
            }
            return null;
        });
        return global::R3.Disposable.Create(effect, static owned => owned.Dispose());
    }
}

/// <summary>One-effect shared Lazily-to-R3 state projection.</summary>
public sealed class SharedR3State<T> : IDisposable
{
    private readonly ReplaySubject<T> _subject = new(1);
    private readonly Effect _effect;
    private bool _disposed;

    internal SharedR3State(
        Context context,
        Func<Compute, T> read,
        IEqualityComparer<T> comparer)
    {
        var hasValue = false;
        var last = default(T)!;
        _effect = context.Effect(compute =>
        {
            try
            {
                var next = read(compute);
                if (!hasValue || !comparer.Equals(last, next))
                {
                    hasValue = true;
                    last = next;
                    _subject.OnNext(next);
                }
            }
            catch (Exception error)
            {
                _subject.OnErrorResume(error);
            }
            return null;
        });
    }

    /// <summary>The shared replaying observable.</summary>
    public Observable<T> Observable => _subject;

    /// <summary>Stops the owned Effect and completes current subscribers.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _effect.Dispose();
        _subject.OnCompleted();
    }
}

/// <summary>An owned R3 subscription and its projected Lazily source state.</summary>
public sealed class R3StateBinding<T> : IDisposable
{
    private IDisposable? _subscription;

    private R3StateBinding(Source<T> state)
    {
        State = state;
    }

    /// <summary>The equality-guarded Lazily state cell.</summary>
    public Source<T> State { get; }

    /// <summary>The most recent recoverable R3 error, if any.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Whether R3 delivered terminal completion.</summary>
    public bool IsCompleted { get; private set; }

    /// <summary>The terminal completion result, if completed.</summary>
    public Result? Completion { get; private set; }

    internal static R3StateBinding<T> ForContext(
        Context context,
        Observable<T> observable,
        T initial,
        IEqualityComparer<T>? comparer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observable);
        var ownerThread = Environment.CurrentManagedThreadId;
        var binding = new R3StateBinding<T>(context.Source(initial, comparer));
        binding._subscription = observable.Subscribe(
            value =>
            {
                if (Environment.CurrentManagedThreadId != ownerThread)
                {
                    throw new InvalidOperationException(
                        "Cross-thread R3 ingress requires ThreadSafeContext.");
                }
                binding.State.Set(value);
            },
            error => binding.LastError = error,
            result =>
            {
                binding.Completion = result;
                binding.IsCompleted = true;
                if (result.IsFailure) binding.LastError = result.Exception;
            });
        return binding;
    }

    internal static R3StateBinding<T> ForThreadSafeContext(
        ThreadSafeContext context,
        Observable<T> observable,
        T initial,
        IEqualityComparer<T>? comparer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observable);
        Source<T>? state = null;
        context.WithLock(inner => state = inner.Source(initial, comparer));
        var binding = new R3StateBinding<T>(state!);
        binding._subscription = observable.Subscribe(
            value => context.Set(binding.State, value),
            error => binding.LastError = error,
            result =>
            {
                binding.Completion = result;
                binding.IsCompleted = true;
                if (result.IsFailure) binding.LastError = result.Exception;
            });
        return binding;
    }

    /// <summary>Unsubscribes from R3; the last projected state remains readable.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
    }
}
