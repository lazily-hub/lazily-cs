namespace Lazily;

/// <summary>
/// Supplies the value-specific observations needed by non-count relay bounds.
/// </summary>
/// <remarks>
/// A relay always maintains its operation count. Byte and key observations are supplied by the
/// application because <typeparamref name="T"/> has no canonical encoded representation or key.
/// Age is driven by a reactive logical-clock source so tracked <c>Age</c>, <c>Measure</c>, and
/// <c>IsFull</c> reads invalidate when time advances. Configure every observation that a live
/// <see cref="BackpressurePolicy.Dimension"/> may select; the relay maintains configured
/// observations even while another dimension is active.
/// </remarks>
/// <typeparam name="T">The relayed operation or coalesced-summary type.</typeparam>
public sealed class RelayMeter<T>
{
    /// <summary>Creates a set of optional non-count measurement functions.</summary>
    /// <param name="byteSize">
    /// Returns the encoded size of the current coalesced hot head. The function is evaluated after
    /// every successful merge, so KeepLatest measures the retained value rather than historical
    /// ingress traffic.
    /// </param>
    /// <param name="keySelector">
    /// Returns the logical key of one ingress operation. The relay counts distinct keys present in
    /// the current hot window.
    /// </param>
    /// <param name="logicalClock">
    /// A monotonically advanced logical-clock source. Age is the saturating difference between
    /// this source and the time at which the current hot window opened.
    /// </param>
    /// <param name="keyComparer">Optional equality comparer for logical keys.</param>
    public RelayMeter(
        Func<T, ulong>? byteSize = null,
        Func<T, object?>? keySelector = null,
        Source<ulong>? logicalClock = null,
        IEqualityComparer<object?>? keyComparer = null)
    {
        ByteSize = byteSize;
        KeySelector = keySelector;
        LogicalClock = logicalClock;
        KeyComparer = keyComparer ?? EqualityComparer<object?>.Default;
    }

    /// <summary>Encoded-size observation, when byte metering is configured.</summary>
    public Func<T, ulong>? ByteSize { get; }

    /// <summary>Ingress-key observation, when distinct-key metering is configured.</summary>
    public Func<T, object?>? KeySelector { get; }

    /// <summary>Reactive logical clock, when age metering is configured.</summary>
    public Source<ulong>? LogicalClock { get; }

    /// <summary>Equality used by the current hot window's distinct-key set.</summary>
    public IEqualityComparer<object?> KeyComparer { get; }
}
