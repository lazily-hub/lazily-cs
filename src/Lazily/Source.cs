namespace Lazily;

/// <summary>
/// A value written from outside the graph; it invalidates its dependents when it changes.
/// </summary>
/// <remarks>
/// This is the source kind of the cell genus: the only kind that carries
/// <see cref="Set"/>/<see cref="Merge"/>. A <see cref="Computed{T}"/> computes from upstream and
/// has neither, so write protection lives in the type rather than in a runtime gate.
/// <para>
/// A source folds writes under a <see cref="MergePolicy{T}"/>. The default policy is
/// <see cref="MergePolicy.KeepLatest{T}"/>, so a plain source is exactly a plain cell; a source
/// with a non-KeepLatest policy is a merge cell. One kind, the policy in a field.
/// </para>
/// <para>
/// <b>Divergence from Go/Rust.</b> Those bindings bound the source value to a comparable type
/// so the write guard can use <c>==</c>. C# has no such bound; the guard uses
/// <see cref="EqualityComparer{T}.Default"/>, which is <c>==</c>-equivalent for primitives and
/// records and reference-equality for types that do not override <c>Equals</c>. Pass an explicit
/// comparer to opt into structural equality for a type whose default is reference equality.
/// </para>
/// </remarks>
/// <typeparam name="T">The value type.</typeparam>
public sealed class Source<T> : ReactiveNode, ITrackable<T>, IDisposable
{
    private T _value;
    private readonly IEqualityComparer<T> _comparer;

    internal Source(Context ctx, T initial, MergePolicy<T> policy, IEqualityComparer<T>? comparer)
        : base(ctx)
    {
        _value = initial;
        Policy = policy;
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    /// <summary>This source's merge policy.</summary>
    public MergePolicy<T> Policy { get; }

    /// <summary>
    /// Reads the value. This is an UNTRACKED external read; use <c>Get(compute)</c> inside a
    /// computation to register a dependency edge.
    /// </summary>
    /// <exception cref="DisposedNodeException">This node has been disposed.</exception>
    public T Get() => GetVia(null);

    /// <summary>
    /// Reads the value through <paramref name="ops"/>. When <paramref name="ops"/> is a
    /// <see cref="Compute"/>, the read registers a dependency edge against the recomputing node.
    /// </summary>
    /// <param name="ops">The read surface.</param>
    /// <exception cref="DisposedNodeException">This node has been disposed.</exception>
    public T Get(IComputeOps ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        return GetVia(ops.TrackNode);
    }

    T ITrackable<T>.GetVia(ReactiveNode? parent) => GetVia(parent);

    private T GetVia(ReactiveNode? parent)
    {
        if (Disposed) throw new DisposedNodeException(null, "source");
        TrackTo(parent);
        return _value;
    }

    /// <summary>Returns the current value without registering a dependency.</summary>
    public T Peek() => _value;

    /// <summary>
    /// Assigns a new value. If it differs from the old one, dependents are invalidated.
    /// Writing a disposed source is a no-op: it has no dependents left to notify.
    /// </summary>
    /// <param name="newValue">The value to store.</param>
    public void Set(T newValue)
    {
        if (Disposed) return;
        if (_comparer.Equals(newValue, _value)) return;
        _value = newValue;
        Ctx.SourceChanged(this);
    }

    /// <summary>
    /// Folds <paramref name="op"/> into the current value under this source's policy and writes
    /// the result through the equality-guarded <see cref="Set"/>, so an idempotent policy's
    /// no-op merge fires no cascade (free dedup).
    /// </summary>
    /// <param name="op">The operand to fold in.</param>
    public void Merge(T op) => Set(Policy.Merge(_value, op));

    /// <summary>
    /// Force-invalidates this source's dependents without changing the value. Used by collection
    /// layers when an entry is removed.
    /// </summary>
    public void Invalidate()
    {
        InvalidateCone();
        Ctx.FlushEffects();
    }

    /// <summary>Sources hold their value directly, so there is nothing to drop on invalidation.</summary>
    internal override void OnInvalidate() { }

    /// <summary>
    /// Tears down this source: detaches its dependents and dirties the surviving cone. Sources
    /// have no dependencies, so only downstream edges need detaching. Idempotent.
    /// </summary>
    public void Dispose() => Ctx.DisposeNode(this);

    /// <summary>
    /// Reads the value, reporting a <see cref="DisposedNodeException"/> instead of throwing when
    /// this source has been disposed.
    /// </summary>
    /// <param name="value">The value, or <c>default</c> on failure.</param>
    /// <param name="error">The disposal error, or null on success.</param>
    /// <returns>True when a value was read.</returns>
    public bool TryGet(out T value, out DisposedNodeException? error)
    {
        if (Disposed)
        {
            value = default!;
            error = new DisposedNodeException(null, "source");
            return false;
        }
        value = Get();
        error = null;
        return true;
    }
}
