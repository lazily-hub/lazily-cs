namespace Lazily;

/// <summary>
/// The node constructors and the value-threaded read helper.
/// </summary>
/// <remarks>
/// These are extension methods on <see cref="Context"/> rather than instance methods so the
/// factory names can match the family vocabulary (<c>Source</c>, <c>Computed</c>, <c>Slot</c>,
/// <c>Effect</c>) without colliding with the type names they return.
/// </remarks>
public static class Reactive
{
    /// <summary>
    /// Creates a mutable source cell under the default keep-latest policy — a plain cell.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="ctx">The owning scope.</param>
    /// <param name="initial">The initial value.</param>
    /// <param name="comparer">
    /// The write guard's equality. Defaults to <see cref="EqualityComparer{T}.Default"/>.
    /// </param>
    public static Source<T> Source<T>(this Context ctx, T initial, IEqualityComparer<T>? comparer = null)
    {
        Guard.NotNull(ctx, nameof(ctx));
        return new Source<T>(ctx, initial, MergePolicy.KeepLatest<T>(), comparer);
    }

    /// <summary>
    /// Creates a source cell whose <c>Merge</c> folds under <paramref name="policy"/>. With
    /// keep-latest it is a plain cell; with sum/max it is a merge cell.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="ctx">The owning scope.</param>
    /// <param name="initial">The initial value.</param>
    /// <param name="policy">The merge policy.</param>
    /// <param name="comparer">
    /// The write guard's equality. Defaults to <see cref="EqualityComparer{T}.Default"/>.
    /// </param>
    public static Source<T> Source<T>(
        this Context ctx, T initial, MergePolicy<T> policy, IEqualityComparer<T>? comparer = null)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(policy, nameof(policy));
        return new Source<T>(ctx, initial, policy, comparer);
    }

    /// <summary>
    /// Creates a lazy, UNGUARDED derived node. Every recompute counts as a change, so its
    /// dependents always cascade. This is the bound-free storage-sense primitive; prefer
    /// <see cref="Computed{T}(Context, Func{Compute, T}, IEqualityComparer{T}?, string?)"/>.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="ctx">The owning scope.</param>
    /// <param name="compute">The compute body; it reads its dependencies through the view.</param>
    /// <param name="name">An optional debug name.</param>
    public static Computed<T> Slot<T>(this Context ctx, Func<Compute, T> compute, string? name = null)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(compute, nameof(compute));
        return new Computed<T>(ctx, compute, equals: null, name);
    }

    /// <summary>
    /// Creates a GUARDED derived node — the default derived kind. A recompute yielding a value
    /// equal to the previous one suppresses the downstream cascade. The guard is a pull-time
    /// check, so it recomputes nothing during invalidation.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="ctx">The owning scope.</param>
    /// <param name="compute">The compute body; it reads its dependencies through the view.</param>
    /// <param name="comparer">
    /// The guard's equality. Defaults to <see cref="EqualityComparer{T}.Default"/>.
    /// </param>
    /// <param name="name">An optional debug name.</param>
    public static Computed<T> Computed<T>(
        this Context ctx,
        Func<Compute, T> compute,
        IEqualityComparer<T>? comparer = null,
        string? name = null)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(compute, nameof(compute));
        var cmp = comparer ?? EqualityComparer<T>.Default;
        return new Computed<T>(ctx, compute, cmp.Equals, name);
    }

    /// <summary>
    /// Creates a guarded derived node with an explicit change predicate.
    /// </summary>
    /// <remarks>
    /// <paramref name="changed"/> returns true to PROPAGATE the recompute to dependents and false
    /// to SUPPRESS it. So <c>Computed(f)</c> is exactly
    /// <c>ComputedRippleWhen(f, (o, n) =&gt; !Equals(o, n))</c>, and <c>Slot(f)</c> is
    /// <c>ComputedRippleWhen(f, (_, _) =&gt; true)</c>.
    /// <para>
    /// The value is ALWAYS computed (the predicate needs the new one); the predicate gates only
    /// the downstream cascade. It MUST be a pure function of (old, next) — reading value-carried
    /// state (version/counter/sequence) is fine and stays deterministic; capturing external
    /// mutable state is not, because it keys off recompute frequency under laziness.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="ctx">The owning scope.</param>
    /// <param name="compute">The compute body.</param>
    /// <param name="changed">The propagate-when predicate.</param>
    /// <param name="name">An optional debug name.</param>
    public static Computed<T> ComputedRippleWhen<T>(
        this Context ctx,
        Func<Compute, T> compute,
        Func<T, T, bool> changed,
        string? name = null)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(compute, nameof(compute));
        Guard.NotNull(changed, nameof(changed));
        return new Computed<T>(ctx, compute, (prev, next) => !changed(prev, next), name);
    }

    /// <summary>Creates and immediately runs an Effect.</summary>
    /// <param name="ctx">The owning scope.</param>
    /// <param name="run">The effect body; it may return a cleanup callback.</param>
    public static Effect Effect(this Context ctx, EffectRun run)
    {
        Guard.NotNull(ctx, nameof(ctx));
        return new Effect(ctx, run);
    }

    /// <summary>
    /// Reads a reactive handle through a compute surface.
    /// </summary>
    /// <remarks>
    /// When <paramref name="ops"/> is a <see cref="Compute"/>, the read registers a dependency
    /// edge against the recomputing node; when it is a <see cref="Context"/> (or
    /// <c>Untracked()</c>), it registers none. This is the value-threaded replacement for an
    /// ambient zero-argument read: the node to attribute to is threaded through
    /// <paramref name="ops"/>, never read from a shared stack.
    /// </remarks>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="ops">The read surface.</param>
    /// <param name="handle">The handle to read.</param>
    public static T Get<T>(this IComputeOps ops, ITrackable<T> handle)
    {
        Guard.NotNull(ops, nameof(ops));
        Guard.NotNull(handle, nameof(handle));
        return handle.GetVia(ops.TrackNode);
    }
}
