#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class IsExternalInit : Attribute;

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property)]
    internal sealed class RequiredMemberAttribute : Attribute;

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public string FeatureName { get; } = featureName;
        public bool IsOptional { get; init; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute;
}

namespace System.Collections.Generic
{
    /// <summary>A read-only set surface supplied for the netstandard2.1 target.</summary>
    public interface IReadOnlySet<T> : IReadOnlyCollection<T>
    {
        /// <summary>Whether this set contains <paramref name="item"/>.</summary>
        bool Contains(T item);

        /// <summary>Whether this set is a proper subset of <paramref name="other"/>.</summary>
        bool IsProperSubsetOf(IEnumerable<T> other);

        /// <summary>Whether this set is a proper superset of <paramref name="other"/>.</summary>
        bool IsProperSupersetOf(IEnumerable<T> other);

        /// <summary>Whether this set is a subset of <paramref name="other"/>.</summary>
        bool IsSubsetOf(IEnumerable<T> other);

        /// <summary>Whether this set is a superset of <paramref name="other"/>.</summary>
        bool IsSupersetOf(IEnumerable<T> other);

        /// <summary>Whether this set overlaps <paramref name="other"/>.</summary>
        bool Overlaps(IEnumerable<T> other);

        /// <summary>Whether this set and <paramref name="other"/> contain equal elements.</summary>
        bool SetEquals(IEnumerable<T> other);
    }
}
#endif

namespace Lazily
{
    internal static class Guard
    {
        internal static void NotNull<T>(T? value, string parameter)
        {
            if (value is null) throw new ArgumentNullException(parameter);
        }

        internal static void NotNullOrEmpty(string? value, string parameter)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("Value cannot be null or empty.", parameter);
        }

        internal static void NotNullOrWhiteSpace(string? value, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Value cannot be null or whitespace.", parameter);
        }
    }

    internal sealed class CompatSet<T> : HashSet<T>, IReadOnlySet<T>
    {
        internal CompatSet()
        {
        }

        internal CompatSet(IEqualityComparer<T> comparer)
            : base(comparer)
        {
        }

        internal CompatSet(IEnumerable<T> values)
            : base(values)
        {
        }

        internal CompatSet(IEnumerable<T> values, IEqualityComparer<T> comparer)
            : base(values, comparer)
        {
        }
    }
}
