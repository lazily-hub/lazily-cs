namespace Lazily;

internal sealed class SequenceEqualityComparer<T> : IEqualityComparer<IReadOnlyList<T>>
{
    internal static SequenceEqualityComparer<T> Instance { get; } = new();

    public bool Equals(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < left.Count; index++)
        {
            if (!comparer.Equals(left[index], right[index])) return false;
        }
        return true;
    }

    public int GetHashCode(IReadOnlyList<T> value)
    {
        var hash = new HashCode();
        foreach (var item in value) hash.Add(item);
        return hash.ToHashCode();
    }
}

internal sealed class DictionaryEqualityComparer<TKey, TValue>
    : IEqualityComparer<IReadOnlyDictionary<TKey, TValue>>
    where TKey : notnull
{
    internal static DictionaryEqualityComparer<TKey, TValue> Instance { get; } = new();

    public bool Equals(
        IReadOnlyDictionary<TKey, TValue>? left,
        IReadOnlyDictionary<TKey, TValue>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        var comparer = EqualityComparer<TValue>.Default;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var candidate) || !comparer.Equals(value, candidate))
                return false;
        }
        return true;
    }

    public int GetHashCode(IReadOnlyDictionary<TKey, TValue> value)
    {
        var hash = new HashCode();
        foreach (var pair in value.OrderBy(pair => pair.Key))
        {
            hash.Add(pair.Key);
            hash.Add(pair.Value);
        }
        return hash.ToHashCode();
    }
}
