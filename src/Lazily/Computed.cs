namespace Lazily;

/// <summary>
/// A lazy, cached, dependency-tracking computation — the derived cell kind.
/// </summary>
/// <remarks>
/// <see cref="Get()"/> returns the cached value if present; otherwise it computes the value
/// (tracking every handle read during computation as a dependency), caches it, and returns it.
/// When any dependency changes the cached value is invalidated and the next read recomputes.
/// <para>
/// The cached value lives on the <see cref="Computed{T}"/> itself, not in a shared context map
/// — so a read is a direct field read on the node you already hold, and read latency does not
/// grow with the total number of nodes. The cache is three-state, which pull-time checking
/// requires and a lone <c>cached</c> flag cannot express:
/// </para>
/// <list type="bullet">
/// <item><c>!HasValue</c> — no value has ever been computed.</item>
/// <item><c>HasValue &amp;&amp; !Cached</c> — a PREVIOUS value is still held but stale. It is kept
/// precisely so a recompute can compare against it (the equality guard) instead of blindly
/// cascading.</item>
/// <item><c>HasValue &amp;&amp; Cached</c> — the value is current.</item>
/// </list>
/// </remarks>
/// <typeparam name="T">The computed value type.</typeparam>
public sealed class Computed<T> : ReactiveNode, ITrackable<T>, ICacheable, IDisposable
{
    private readonly Func<Compute, T> _compute;
    private T _value = default!;
    private bool _cached;
    private bool _hasValue;

    /// <summary>
    /// The optional equality guard. When set and a recompute yields a value equal to the
    /// previous one, the node reports "unchanged" and its dependents keep their caches. Null
    /// (a plain unguarded slot) means every recompute counts as a change.
    /// </summary>
    private readonly Func<T, T, bool>? _equals;

    /// <summary>Guards the pull-time dependency walk against a cyclic graph re-entering this node.</summary>
    private bool _refreshing;

    private bool _eager;

    internal Computed(Context ctx, Func<Compute, T> compute, Func<T, T, bool>? equals, string? name)
        : base(ctx)
    {
        _compute = compute;
        _equals = equals;
        Name = name;
        ctx.RegisterSlot(this);
    }

    /// <summary>An optional debug name.</summary>
    public string? Name { get; }

    /// <summary>
    /// Reads (and caches if needed) the value. This is an UNTRACKED external read; use
    /// <c>Get(compute)</c> inside a computation to register a dependency edge.
    /// </summary>
    /// <exception cref="DisposedNodeException">This node has been disposed.</exception>
    public T Get() => GetVia(null);

    /// <summary>
    /// Reads the value through <paramref name="ops"/>. When <paramref name="ops"/> is a
    /// <see cref="Compute"/>, the read registers a dependency edge against the recomputing
    /// node; when it is a <see cref="Context"/>, it registers none.
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
        if (Disposed) throw new DisposedNodeException(Name, "computed");
        TrackTo(parent);
        if (_cached) return _value;
        Refresh();
        return _value;
    }

    /// <summary>
    /// Brings this node up to date and reports whether its value CHANGED.
    /// </summary>
    /// <remarks>
    /// The pull half of the model. A stale node first refreshes its dependencies. If none of
    /// them actually changed value, the node is marked clean WITHOUT recomputing — that is the
    /// suppression an equality guard buys its whole downstream cone, and it is decided
    /// per-dependency, so a node with a second, genuinely-changed dependency still recomputes.
    /// Only a FORCED node (one that directly read the written node) skips the dependency check.
    /// </remarks>
    internal override bool Refresh()
    {
        if (Disposed || _cached) return false;
        if (_refreshing) return false; // cyclic graph: treat the back-edge as unchanged

        if (_hasValue && !ForceRecompute)
        {
            var deps = Dependencies.ToArray();
            _refreshing = true;
            var dependencyChanged = false;
            try
            {
                foreach (var d in deps)
                {
                    if (d.Refresh()) dependencyChanged = true;
                }
            }
            finally
            {
                _refreshing = false;
            }
            if (!dependencyChanged)
            {
                // Nothing upstream moved: the cached value is still correct.
                MarkClean();
                return false;
            }
        }
        return RecomputeNow();
    }

    /// <summary>Records that the existing value is current again, without running the compute.</summary>
    private void MarkClean()
    {
        Dirty = false;
        ForceRecompute = false;
        if (!_cached)
        {
            _cached = true;
            Ctx.CachedCountInc();
        }
    }

    /// <summary>
    /// Runs the compute, refreshes the dependency edges, and reports whether the value changed.
    /// A first-ever computation reports false: nothing downstream can be holding a stale value
    /// of a node that never had one.
    /// </summary>
    private bool RecomputeNow()
    {
        DetachUpstream();
        // Value-thread the recomputing node into the compute view; the body reads through it
        // and attributes each edge by value. The view is killed the instant compute returns so
        // a stored/escaped view fails the fortification guard.
        var cv = Ctx.NewCompute(this);
        T v;
        try
        {
            v = _compute(cv);
        }
        finally
        {
            cv.Close();
        }

        var hadValue = _hasValue;
        var unchanged = hadValue && _equals is not null && _equals(_value, v);
        MarkClean();
        if (unchanged) return false;

        _value = v;
        _hasValue = true;
        if (!hadValue) return false;

        // The value really moved, so every dependent must recompute rather than merely
        // re-check. Raising them from dirty to forced here is what makes the pull walk
        // order-independent: a dependent that refreshes its dependencies in an unlucky order —
        // reaching this node after some other path already refreshed and cleaned it — would
        // otherwise see "no dependency changed" and keep a stale cache.
        MarkDependents(scheduleEffects: false);
        return true;
    }

    /// <summary>
    /// Returns the cached value without recomputing, and whether it was cached.
    /// </summary>
    /// <param name="value">The cached value, or <c>default</c>.</param>
    /// <returns>True when a current cached value was returned.</returns>
    public bool Peek(out T value)
    {
        if (_cached)
        {
            value = _value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>
    /// Marks this node stale. The previous value is deliberately KEPT so a later recompute can
    /// compare against it; only its currency is dropped.
    /// </summary>
    internal override void OnInvalidate()
    {
        Dirty = true;
        if (_cached)
        {
            _cached = false;
            Ctx.CachedCountDec();
        }
    }

    /// <summary>
    /// Drops the memoized value outright. Unlike invalidation this discards the value itself,
    /// so the next read must recompute rather than compare.
    /// </summary>
    void ICacheable.ClearCache()
    {
        _cached = false;
        _hasValue = false;
        Dirty = true;
        ForceRecompute = true;
        _value = default!;
    }

    bool ICacheable.CachedNow => _cached;

    /// <summary>
    /// Makes this computed eager and returns the same handle.
    /// </summary>
    /// <remarks>
    /// Eager is a STATE a computed is in, not a separate kind. It attaches a puller
    /// <see cref="Effect"/> that reads the computed now — materializing its value and its
    /// dependency edges immediately — and again after every invalidation, from inside the
    /// invalidating write's effect flush. Because the puller is an ordinary effect and effects
    /// are scheduled rather than inline, N invalidations inside a <see cref="Context.Batch"/>
    /// coalesce into a single scheduled pull at the flush. Idempotent.
    /// </remarks>
    public Computed<T> Eager()
    {
        if (Disposed || _eager) return this;
        _eager = true;
        var puller = new Effect(Ctx, c =>
        {
            Get(c);
            return null;
        });
        Ctx.EagerBy[this] = puller;
        return this;
    }

    /// <summary>
    /// Reverts an eager computed to lazy: disposes the puller effect and clears the eager bit.
    /// The value remains readable and recomputes on demand. Idempotent.
    /// </summary>
    public void Lazy()
    {
        if (!_eager) return;
        if (Ctx.EagerBy.Remove(this, out var puller)) puller.Dispose();
        _eager = false;
    }

    /// <summary>Whether this computed is eager (has a live puller).</summary>
    public bool IsEager => _eager;

    /// <summary>
    /// Tears down this node: detaches both edge directions, drops the cached value, and dirties
    /// the surviving dependent cone. Idempotent.
    /// </summary>
    /// <remarks>
    /// Callers must ensure nothing still reads it in a live compute. A reader that still names it
    /// throws <see cref="DisposedNodeException"/> on its next recompute.
    /// </remarks>
    public void Dispose() => Ctx.DisposeNode(this);

    /// <summary>
    /// Reads the value, reporting a <see cref="DisposedNodeException"/> instead of throwing when
    /// this node — or any node it reads while recomputing — has been disposed. This is the
    /// boundary form: use it where a read may race a teardown.
    /// </summary>
    /// <param name="value">The value, or <c>default</c> on failure.</param>
    /// <param name="error">The disposal error, or null on success.</param>
    /// <returns>True when a value was read.</returns>
    public bool TryGet(out T value, out DisposedNodeException? error)
    {
        try
        {
            value = Get();
            error = null;
            return true;
        }
        catch (DisposedNodeException ex)
        {
            value = default!;
            error = ex;
            return false;
        }
    }
}
