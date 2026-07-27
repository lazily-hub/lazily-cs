namespace Lazily;

/// <summary>An explicit optional value that works uniformly for value and reference types.</summary>
/// <typeparam name="T">The carried value type.</typeparam>
public readonly struct Optional<T> : IEquatable<Optional<T>>
{
    private readonly T? _value;

    private Optional(T value)
    {
        _value = value;
        HasValue = true;
    }

    /// <summary>Whether this optional carries a value.</summary>
    public bool HasValue { get; }

    /// <summary>The carried value.</summary>
    /// <exception cref="InvalidOperationException">This optional is empty.</exception>
    public T Value => HasValue
        ? _value!
        : throw new InvalidOperationException("optional value is absent");

    /// <summary>The empty optional.</summary>
    public static Optional<T> None => default;

    /// <summary>Constructs a present optional.</summary>
    public static Optional<T> Some(T value)
    {
        Guard.NotNull(value, nameof(value));
        return new Optional<T>(value);
    }

    /// <inheritdoc />
    public bool Equals(Optional<T> other) =>
        HasValue == other.HasValue
        && (!HasValue || EqualityComparer<T>.Default.Equals(_value!, other._value!));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Optional<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HasValue ? HashCode.Combine(true, EqualityComparer<T>.Default.GetHashCode(_value!)) : 0;

    /// <summary>Compares two optionals by presence and carried value.</summary>
    public static bool operator ==(Optional<T> left, Optional<T> right) => left.Equals(right);

    /// <summary>Compares two optionals by presence and carried value.</summary>
    public static bool operator !=(Optional<T> left, Optional<T> right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => HasValue ? $"Some({Value})" : "None";
}
