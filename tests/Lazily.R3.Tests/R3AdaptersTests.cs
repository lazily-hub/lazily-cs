using R3;
using Lazily.R3;
using Xunit;

namespace Lazily.R3.Tests;

public sealed class R3AdaptersTests
{
    [Fact]
    public void Cold_projection_emits_initial_distinct_dynamic_and_batch_updates()
    {
        var context = new Context();
        var chooseLeft = context.Source(true);
        var left = context.Source(1);
        var right = context.Source(10);
        var values = new List<int>();

        using var subscription = context
            .ToR3State(c => chooseLeft.Get(c) ? left.Get(c) : right.Get(c))
            .Subscribe(values.Add);

        left.Set(1);
        context.Batch(() =>
        {
            left.Set(2);
            left.Set(3);
        });
        chooseLeft.Set(false);
        left.Set(4);
        right.Set(11);

        Assert.Equal([1, 3, 10, 11], values);
        subscription.Dispose();
        right.Set(12);
        Assert.Equal([1, 3, 10, 11], values);
    }

    [Fact]
    public void Cold_subscriptions_own_effects_and_shared_bridge_owns_one()
    {
        var context = new Context();
        var source = context.Source(1);
        var reads = 0;
        var cold = context.ToR3State(c =>
        {
            reads++;
            return source.Get(c);
        });
        using var a = cold.Subscribe(_ => { });
        using var b = cold.Subscribe(_ => { });
        Assert.Equal(2, reads);

        using var shared = context.ToSharedR3State(c =>
        {
            reads++;
            return source.Get(c);
        });
        var first = new List<int>();
        var second = new List<int>();
        Result? completion = null;
        using var sa = shared.Observable.Subscribe(first.Add, result => completion = result);
        using var sb = shared.Observable.Subscribe(second.Add);
        Assert.Equal([1], first);
        Assert.Equal([1], second);
        Assert.Equal(3, reads);

        a.Dispose();
        b.Dispose();
        shared.Dispose();
        Assert.True(completion?.IsSuccess);
        source.Set(2);
        Assert.Equal(3, reads);
    }

    [Fact]
    public void Projection_errors_resume_and_reentrant_writes_are_serialized()
    {
        var context = new Context();
        var source = context.Source(0);
        var values = new List<int>();
        var errors = new List<Exception>();
        using var subscription = context
            .ToR3State(c =>
            {
                var value = source.Get(c);
                if (value == 1) throw new InvalidOperationException("recoverable");
                return value;
            })
            .Subscribe(
                value =>
                {
                    values.Add(value);
                    if (value == 2) source.Set(3);
                },
                errors.Add,
                _ => { });

        source.Set(1);
        source.Set(2);
        Assert.Single(errors);
        Assert.Equal([0, 2, 3], values);
        source.Set(4);
        Assert.Equal([0, 2, 3, 4], values);
    }

    [Fact]
    public void R3_binding_tracks_errors_completion_duplicates_and_disposal()
    {
        var context = new Context();
        using var subject = new Subject<int>();
        using var binding = context.BindR3State(subject, 0);
        var runs = 0;
        var effect = context.Effect(c =>
        {
            _ = binding.State.Get(c);
            runs++;
            return null;
        });

        subject.OnNext(1);
        subject.OnNext(1);
        subject.OnErrorResume(new InvalidOperationException("resume"));
        Assert.Equal(2, runs);
        Assert.NotNull(binding.LastError);
        subject.OnCompleted();
        Assert.True(binding.IsCompleted);
        Assert.True(binding.Completion?.IsSuccess);
        effect.Dispose();
    }

    [Fact]
    public void R3_binding_disposal_unsubscribes_without_disposing_projected_state()
    {
        var context = new Context();
        using var subject = new Subject<int>();
        var binding = context.BindR3State(subject, 0);

        subject.OnNext(1);
        binding.Dispose();
        subject.OnNext(2);

        Assert.Equal(1, binding.State.Get());
    }

    [Fact]
    public async Task Thread_safe_binding_accepts_cross_thread_ingress()
    {
        var context = new ThreadSafeContext();
        using var subject = new Subject<int>();
        using var binding = context.BindR3State(subject, 0);

        await Task.Run(() => subject.OnNext(7));

        Assert.Equal(7, context.WithLock(_ => binding.State.Get()));
    }

    [Fact]
    public void Plain_context_binding_rejects_cross_thread_ingress_as_recoverable_error()
    {
        var context = new Context();
        using var subject = new Subject<int>();
        using var binding = context.BindR3State(subject, 0);

        var thread = new Thread(() => subject.OnNext(7));
        thread.Start();
        thread.Join();

        Assert.Equal(0, binding.State.Get());
        var error = Assert.IsType<InvalidOperationException>(binding.LastError);
        Assert.Contains("ThreadSafeContext", error.Message);

        subject.OnNext(8);
        Assert.Equal(8, binding.State.Get());
    }
}
