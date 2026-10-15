using System.Reflection;

[assembly: AssemblyMetadata("LazilyCoreApiGeneration", "1")]

namespace Lazily;

/// <summary>
/// Compile-only generation-1 contract consumed by the Lazily.R3 oldest-supported lane.
/// The normalized manifests, not these throwing bodies, are the compatibility authority.
/// </summary>
public sealed class Context;

public sealed class Compute;

public sealed class Source<T>
{
    public void Set(T value) => throw new NotSupportedException("compile-only compatibility contract");
}

public delegate Action? EffectRun(Compute compute);

public sealed class Effect : IDisposable
{
    public void Dispose() { }
}

public static class Reactive
{
    public static Source<T> Source<T>(
        this Context context,
        T initial,
        IEqualityComparer<T>? comparer = null) =>
        throw new NotSupportedException("compile-only compatibility contract");

    public static Effect Effect(this Context context, EffectRun run) =>
        throw new NotSupportedException("compile-only compatibility contract");
}

public sealed class ThreadSafeContext
{
    public void WithLock(Action<Context> fn) =>
        throw new NotSupportedException("compile-only compatibility contract");

    public void Set<T>(Source<T> cell, T value) =>
        throw new NotSupportedException("compile-only compatibility contract");
}
