namespace Lazily;

/// <summary>
/// A value-bearing node that can be read through an <see cref="IComputeOps"/> surface.
/// </summary>
/// <remarks>
/// Both <see cref="Computed{T}"/> and <see cref="Source{T}"/> implement it, so a single
/// <c>Get</c> serves computed cells and source cells alike. The read core is
/// <c>internal</c>, so this interface cannot be implemented outside this assembly.
/// </remarks>
/// <typeparam name="T">The value type.</typeparam>
public interface ITrackable<out T>
{
    /// <summary>
    /// Reads the value, registering an edge against <paramref name="parent"/>
    /// (null = untracked).
    /// </summary>
    internal T GetVia(ReactiveNode? parent);
}
