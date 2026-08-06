using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Lazily;
using Lazily.R3;
using R3;

BenchmarkRunner.Run<R3BridgeBenchmarks>();

[MemoryDiagnoser]
[ShortRunJob]
public class R3BridgeBenchmarks
{
    private Source<int> _nativeLazilySource = null!;
    private Effect _nativeLazilyEffect = null!;
    private ReactiveProperty<int> _nativeR3Property = null!;
    private IDisposable _nativeR3Subscription = null!;
    private Source<int> _lazilyToR3Source = null!;
    private IDisposable _lazilyToR3Subscription = null!;
    private Subject<int> _subject = null!;
    private R3StateBinding<int> _r3ToLazilyBinding = null!;
    private Effect _r3ToLazilyEffect = null!;
    private int _consumer;
    private int _value;

    [GlobalSetup]
    public void Setup()
    {
        var nativeLazilyContext = new Context();
        _nativeLazilySource = nativeLazilyContext.Source(0);
        _nativeLazilyEffect = nativeLazilyContext.Effect(c =>
        {
            _consumer = _nativeLazilySource.Get(c);
            return null;
        });

        _nativeR3Property = new ReactiveProperty<int>(0);
        _nativeR3Subscription = _nativeR3Property.Subscribe(value => _consumer = value);

        var lazilyToR3Context = new Context();
        _lazilyToR3Source = lazilyToR3Context.Source(0);
        _lazilyToR3Subscription = lazilyToR3Context
            .ToR3State(c => _lazilyToR3Source.Get(c))
            .Subscribe(value => _consumer = value);

        var r3ToLazilyContext = new Context();
        _subject = new Subject<int>();
        _r3ToLazilyBinding = r3ToLazilyContext.BindR3State(_subject, 0);
        _r3ToLazilyEffect = r3ToLazilyContext.Effect(c =>
        {
            _consumer = _r3ToLazilyBinding.State.Get(c);
            return null;
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _nativeLazilyEffect.Dispose();
        _nativeR3Subscription.Dispose();
        _nativeR3Property.Dispose();
        _lazilyToR3Subscription.Dispose();
        _r3ToLazilyEffect.Dispose();
        _r3ToLazilyBinding.Dispose();
        _subject.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void NativeLazily() => _nativeLazilySource.Set(++_value);

    [Benchmark]
    public void NativeR3() => _nativeR3Property.Value = ++_value;

    [Benchmark]
    public void LazilyToR3() => _lazilyToR3Source.Set(++_value);

    [Benchmark]
    public void R3ToLazily() => _subject.OnNext(++_value);
}
