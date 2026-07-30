using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Lazily;

/// <summary>Whether a topic subscription survives disconnect and holds the retention frontier.</summary>
public enum TopicDurability
{
    /// <summary>The cursor persists while disconnected and participates in safe log collection.</summary>
    Durable,

    /// <summary>The session is removed on disconnect and never participates in safe log collection.</summary>
    Ephemeral,
}

/// <summary>Public state for one stable topic subscriber.</summary>
public sealed record TopicSubscriptionSnapshot(
    long Cursor,
    TopicDurability Durability,
    bool Connected);

/// <summary>Atomic state required to recreate a topic without moving its cursors.</summary>
public sealed record TopicSnapshot<T>
{
    /// <summary>Creates an empty topic snapshot.</summary>
    public TopicSnapshot()
        : this(
            0,
            Array.Empty<T>(),
            new ReadOnlyDictionary<string, TopicSubscriptionSnapshot>(
                new Dictionary<string, TopicSubscriptionSnapshot>(StringComparer.Ordinal)))
    {
    }

    /// <summary>Creates a defensive snapshot copy.</summary>
    public TopicSnapshot(
        long baseOffset,
        IEnumerable<T> elements,
        IReadOnlyDictionary<string, TopicSubscriptionSnapshot> subscriptions)
    {
        Guard.NotNull(elements, nameof(elements));
        Guard.NotNull(subscriptions, nameof(subscriptions));
        BaseOffset = baseOffset;
        Elements = elements.ToArray();
        Subscriptions = new ReadOnlyDictionary<string, TopicSubscriptionSnapshot>(
            new Dictionary<string, TopicSubscriptionSnapshot>(
                subscriptions,
                StringComparer.Ordinal));
    }

    /// <summary>Absolute offset represented by the first retained element.</summary>
    public long BaseOffset { get; }

    /// <summary>Retained log elements.</summary>
    public IReadOnlyList<T> Elements { get; }

    /// <summary>Stable subscriber cursor state.</summary>
    public IReadOnlyDictionary<string, TopicSubscriptionSnapshot> Subscriptions { get; }
}

/// <summary>Result of subscribing one stable identity.</summary>
public enum TopicSubscribeOutcome
{
    /// <summary>A new cursor was created at the current tail.</summary>
    Created,

    /// <summary>An offline durable cursor was reconnected without moving it.</summary>
    Reconnected,

    /// <summary>The identity was already connected and no state changed.</summary>
    AlreadyConnected,
}

/// <summary>
/// Broadcast log whose subscribers own independent, non-destructive reactive cursors — the
/// single-threaded flavor.
/// </summary>
/// <remarks>
/// The log algebra lives in <see cref="TopicCore{T}"/> and is shared verbatim with
/// <see cref="ThreadSafeTopicCell{T}"/> and <see cref="AsyncTopicCell{T}"/>. This shell owns only
/// the per-subscriber reader nodes and their version sources.
/// </remarks>
public sealed class TopicCell<T>
{
    private readonly Context _ctx;
    private readonly TopicCore<T> _core;
    private readonly Dictionary<string, Source<int>> _readerVersions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _readerVersionNumbers =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Computed<IReadOnlyList<T>>> _readers =
        new(StringComparer.Ordinal);

    /// <summary>Creates an empty topic.</summary>
    public TopicCell(Context ctx)
        : this(ctx, new TopicSnapshot<T>())
    {
    }

    /// <summary>Recreates a topic from a durable/live-state snapshot.</summary>
    public TopicCell(Context ctx, TopicSnapshot<T> initial)
    {
        Guard.NotNull(ctx, nameof(ctx));
        _core = new TopicCore<T>(initial);
        _ctx = ctx;
        foreach (var id in _core.SubscriptionIds()) EnsureReader(id);
    }

    /// <summary>Absolute offset represented by the first retained element.</summary>
    public long BaseOffset => _core.BaseOffset;

    /// <summary>Absolute offset immediately after the retained log.</summary>
    public long EndOffset => _core.EndOffset;

    /// <summary>
    /// Creates a cursor at the current tail, or reconnects an existing durable identity
    /// without moving its cursor.
    /// </summary>
    public TopicSubscribeOutcome Subscribe(string id, TopicDurability durability)
    {
        var (outcome, invalidated, created) = _core.Subscribe(id, durability);
        if (created) EnsureReader(id);
        InvalidateReaders(invalidated);
        return outcome;
    }

    /// <summary>Reconnects a durable identity, creating it at the current tail when unknown.</summary>
    public TopicSubscribeOutcome Reconnect(string id) => Subscribe(id, TopicDurability.Durable);

    /// <summary>
    /// Disconnects one subscriber. Durable state remains offline; ephemeral state is removed.
    /// </summary>
    public bool Disconnect(string id)
    {
        var (disconnected, invalidated, removed) = _core.Disconnect(id);
        if (!disconnected) return false;
        // Invalidate BEFORE dropping the reader, so a removed ephemeral subscriber still
        // reports its own final transition.
        InvalidateReaders(invalidated);
        if (removed)
        {
            _readers.Remove(id);
            _readerVersions.Remove(id);
            _readerVersionNumbers.Remove(id);
        }
        return true;
    }

    /// <summary>Appends one element, leaving every cursor unchanged.</summary>
    public long Publish(T value)
    {
        var (offset, invalidated) = _core.Publish(value);
        InvalidateReaders(invalidated);
        return offset;
    }

    /// <summary>Reactive unread suffix for one connected subscriber.</summary>
    public IReadOnlyList<T> ReadStream(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        return _readers.TryGetValue(id, out var reader)
            ? reader.Get()
            : Array.Empty<T>();
    }

    /// <summary>Reactive unread suffix read through a compute view.</summary>
    public IReadOnlyList<T> ReadStream(string id, IComputeOps ops)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        Guard.NotNull(ops, nameof(ops));
        return _readers.TryGetValue(id, out var reader)
            ? reader.Get(ops)
            : Array.Empty<T>();
    }

    /// <summary>Reactive element at the subscriber cursor, or default at the tail/offline.</summary>
    public T? Read(string id) => ReadStream(id).FirstOrDefault();

    /// <summary>Advances only the named subscriber and returns the element it passed.</summary>
    public T? Advance(string id)
    {
        var (value, invalidated) = _core.Advance(id);
        InvalidateReaders(invalidated);
        return value;
    }

    /// <summary>
    /// Removes the prefix below the minimum durable cursor, or everything when no durable
    /// subscription exists. Absolute cursors remain unchanged.
    /// </summary>
    public int CollectGarbage() => _core.CollectGarbage();

    /// <summary>Non-reactive retained-log snapshot.</summary>
    public IReadOnlyList<T> Elements() => _core.Elements();

    /// <summary>Subscriber identities in stable ordinal order.</summary>
    public IReadOnlyList<string> SubscriptionIds() => _core.SubscriptionIds();

    /// <summary>Non-reactive state for one stable subscriber.</summary>
    public TopicSubscriptionSnapshot? SubscriptionState(string id) => _core.SubscriptionState(id);

    /// <summary>Handle to one subscriber's demand-driven unread suffix.</summary>
    public Computed<IReadOnlyList<T>>? ReaderHandle(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        return _readers.GetValueOrDefault(id);
    }

    /// <summary>Creates an atomic defensive snapshot suitable for restart.</summary>
    public TopicSnapshot<T> Snapshot() => _core.Snapshot();

    private Computed<IReadOnlyList<T>> EnsureReader(string id)
    {
        if (_readers.TryGetValue(id, out var existing)) return existing;

        var version = _ctx.Source(0);
        _readerVersions.Add(id, version);
        _readerVersionNumbers.Add(id, 0);
        var reader = _ctx.Computed<IReadOnlyList<T>>(cx =>
        {
            cx.Get(version);
            return _core.ReadStream(id);
        });
        _readers.Add(id, reader);
        return reader;
    }

    private void InvalidateReaders(IEnumerable<string> ids)
    {
        var targets = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length == 0) return;
        _ctx.Batch(() =>
        {
            foreach (var id in targets)
            {
                if (!_readerVersions.TryGetValue(id, out var source)) continue;
                var next = _readerVersionNumbers[id] + 1;
                _readerVersionNumbers[id] = next;
                source.Set(next);
            }
        });
    }
}
