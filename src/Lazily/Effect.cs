namespace Lazily;

/// <summary>
/// A side-effect body that may return a cleanup callback.
/// </summary>
/// <remarks>
/// It receives the per-recompute <see cref="Compute"/> view and reads its dependencies through
/// it — the value-threaded tracking surface. The cleanup, if non-null, is invoked before the
/// next rerun and on <see cref="Effect.Dispose"/>.
/// </remarks>
/// <param name="compute">The per-run tracking view.</param>
/// <returns>An optional cleanup callback.</returns>
public delegate Action? EffectRun(Compute compute);

/// <summary>
/// A side-effect sink that reruns whenever a tracked dependency changes.
/// </summary>
/// <remarks>
/// This is the ONLY push primitive in the family: there are no observers or subscriptions on a
/// reactive handle. Any <see cref="Source{T}"/> or <see cref="Computed{T}"/> read inside the body
/// becomes a dependency; when any changes, the effect is scheduled and reruns after the current
/// cascade (or at <see cref="Context.Batch"/> exit).
/// </remarks>
public sealed class Effect : ReactiveNode
{
    private readonly EffectRun _run;
    private Action? _cleanup;
    private bool _active;
    private bool _running;

    /// <summary>
    /// Marks a scheduled effect that must rerun without a dependency freshness check, because it
    /// read the node that was actually written. An effect scheduled only transitively — reached
    /// through a guarded computed, say — clears its check at flush time instead: if every
    /// dependency refreshes to an unchanged value, it does not rerun. That is how the equality
    /// guard reaches effects.
    /// </summary>
    internal bool ForceRun;

    /// <summary>
    /// Creates and immediately runs a side-effect observer.
    /// </summary>
    /// <param name="ctx">The owning reactive scope.</param>
    /// <param name="run">The effect body.</param>
    public Effect(Context ctx, EffectRun run)
        : base(ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(run);
        _run = run;
        _active = true;
        Rerun();
    }

    /// <summary>Whether the effect is still active (not disposed).</summary>
    public bool IsActive => _active;

    /// <summary>
    /// Removes the observer: invokes the last cleanup, then unsubscribes from all dependencies.
    /// Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (!_active) return;
        _active = false;
        Disposed = true;
        Ctx.RemovePendingEffect(this);
        DetachUpstream();
        var c = _cleanup;
        _cleanup = null;
        c?.Invoke();
    }

    internal void Rerun()
    {
        if (!_active || _running) return;
        _running = true;
        try
        {
            DetachUpstream();
            var prev = _cleanup;
            _cleanup = null;
            prev?.Invoke();
            // Value-thread this effect into the compute view; the body reads through it and
            // attributes each edge by value. Kill the view when the body returns so it cannot
            // escape.
            var cv = Ctx.NewCompute(this);
            try
            {
                _cleanup = _run(cv);
            }
            finally
            {
                cv.Close();
            }
        }
        finally
        {
            _running = false;
        }
    }

    internal override void OnInvalidate() => MarkStale(force: true);

    /// <summary>
    /// Schedules this effect to rerun after the current cascade.
    /// </summary>
    /// <param name="force">Whether it read the written node directly; see <see cref="ForceRun"/>.</param>
    internal void MarkStale(bool force)
    {
        if (Ctx.Disposing > 0)
        {
            // Teardown, not a publish: mark only, never schedule. Running the effect here would
            // re-enter a compute that reads the node being disposed. The contract is that the
            // effect errors on its NEXT recompute.
            //
            // Nothing needs re-attaching: the frontier walk that reached us does not consume
            // edges, so an effect that deliberately does not rerun keeps every edge to its
            // surviving dependencies.
            return;
        }
        if (force) ForceRun = true;
        Ctx.ScheduleEffect(this);
    }

    /// <summary>
    /// Refreshes every node this effect read on its last run and reports whether any produced a
    /// different value. Used at flush time to decide whether a transitively-scheduled effect
    /// actually needs to rerun.
    /// </summary>
    internal bool DependenciesChanged()
    {
        var changed = false;
        foreach (var d in Dependencies.ToArray())
        {
            if (d.Refresh()) changed = true;
        }
        return changed;
    }
}
