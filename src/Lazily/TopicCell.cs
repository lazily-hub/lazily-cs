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
/// Broadcast log whose subscribers own independent, non-destructive reactive cursors.
/// </summary>
public sealed class TopicCell<T>
{
    private sealed class Subscription(
        long cursor,
        TopicDurability durability,
        bool connected)
    {
        internal long Cursor { get; set; } = cursor;
        internal TopicDurability Durability { get; } = durability;
        internal bool Connected { get; set; } = connected;
    }

    private readonly Context _ctx;
    private readonly List<T> _retained;
    private readonly Dictionary<string, Subscription> _subscriptions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Source<int>> _readerVersions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _readerVersionNumbers =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Computed<IReadOnlyList<T>>> _readers =
        new(StringComparer.Ordinal);
    private long _baseOffset;

    /// <summary>Creates an empty topic.</summary>
    public TopicCell(Context ctx)
        : this(ctx, new TopicSnapshot<T>())
    {
    }

    /// <summary>Recreates a topic from a durable/live-state snapshot.</summary>
    public TopicCell(Context ctx, TopicSnapshot<T> initial)
    {
        Guard.NotNull(ctx, nameof(ctx));
        Guard.NotNull(initial, nameof(initial));
        if (initial.BaseOffset < 0)
            throw new ArgumentOutOfRangeException(
                nameof(initial),
                initial.BaseOffset,
                "topic base offset must be non-negative");

        _ctx = ctx;
        _baseOffset = initial.BaseOffset;
        _retained = [.. initial.Elements];
        var end = EndOffset;
        foreach (var (id, snapshot) in initial.Subscriptions)
        {
            Guard.NotNullOrEmpty(id, nameof(id));
            Guard.NotNull(snapshot, nameof(snapshot));
            if (snapshot.Cursor < _baseOffset || snapshot.Cursor > end)
                throw new ArgumentOutOfRangeException(
                    nameof(initial),
                    snapshot.Cursor,
                    $"topic cursor for '{id}' must be within the retained absolute offset range");
            if (snapshot.Durability == TopicDurability.Ephemeral && !snapshot.Connected)
                throw new ArgumentException(
                    $"disconnected ephemeral topic subscription '{id}' must be removed",
                    nameof(initial));

            _subscriptions.Add(
                id,
                new Subscription(snapshot.Cursor, snapshot.Durability, snapshot.Connected));
            EnsureReader(id);
        }
    }

    /// <summary>Absolute offset represented by the first retained element.</summary>
    public long BaseOffset => _baseOffset;

    /// <summary>Absolute offset immediately after the retained log.</summary>
    public long EndOffset => checked(_baseOffset + _retained.Count);

    /// <summary>
    /// Creates a cursor at the current tail, or reconnects an existing durable identity
    /// without moving its cursor.
    /// </summary>
    public TopicSubscribeOutcome Subscribe(string id, TopicDurability durability)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        if (_subscriptions.TryGetValue(id, out var existing))
        {
            if (existing.Connected) return TopicSubscribeOutcome.AlreadyConnected;
            existing.Connected = true;
            InvalidateReaders([id]);
            return TopicSubscribeOutcome.Reconnected;
        }

        _subscriptions.Add(id, new Subscription(EndOffset, durability, connected: true));
        EnsureReader(id);
        return TopicSubscribeOutcome.Created;
    }

    /// <summary>Reconnects a durable identity, creating it at the current tail when unknown.</summary>
    public TopicSubscribeOutcome Reconnect(string id) => Subscribe(id, TopicDurability.Durable);

    /// <summary>
    /// Disconnects one subscriber. Durable state remains offline; ephemeral state is removed.
    /// </summary>
    public bool Disconnect(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        if (!_subscriptions.TryGetValue(id, out var subscription) || !subscription.Connected)
            return false;

        InvalidateReaders([id]);
        if (subscription.Durability == TopicDurability.Ephemeral)
        {
            _subscriptions.Remove(id);
            _readers.Remove(id);
            _readerVersions.Remove(id);
            _readerVersionNumbers.Remove(id);
        }
        else
        {
            subscription.Connected = false;
        }

        return true;
    }

    /// <summary>Appends one element, leaving every cursor unchanged.</summary>
    public long Publish(T value)
    {
        var offset = EndOffset;
        _retained.Add(value);
        InvalidateReaders(
            _subscriptions
                .Where(pair => pair.Value.Connected && pair.Value.Cursor <= offset)
                .Select(pair => pair.Key));
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
        Guard.NotNullOrEmpty(id, nameof(id));
        if (!_subscriptions.TryGetValue(id, out var subscription) ||
            !subscription.Connected ||
            subscription.Cursor >= EndOffset)
            return default;

        var value = _retained[checked((int)(subscription.Cursor - _baseOffset))];
        subscription.Cursor++;
        InvalidateReaders([id]);
        return value;
    }

    /// <summary>
    /// Removes the prefix below the minimum durable cursor, or everything when no durable
    /// subscription exists. Absolute cursors remain unchanged.
    /// </summary>
    public int CollectGarbage()
    {
        var durableCursors = _subscriptions.Values
            .Where(subscription => subscription.Durability == TopicDurability.Durable)
            .Select(subscription => subscription.Cursor)
            .ToArray();
        var frontier = durableCursors.Length == 0 ? EndOffset : durableCursors.Min();
        var remove = checked((int)(frontier - _baseOffset));
        if (remove > 0) _retained.RemoveRange(0, remove);
        _baseOffset = frontier;
        return remove;
    }

    /// <summary>Non-reactive retained-log snapshot.</summary>
    public IReadOnlyList<T> Elements() => _retained.ToArray();

    /// <summary>Subscriber identities in stable ordinal order.</summary>
    public IReadOnlyList<string> SubscriptionIds() =>
        _subscriptions.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();

    /// <summary>Non-reactive state for one stable subscriber.</summary>
    public TopicSubscriptionSnapshot? SubscriptionState(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        return _subscriptions.TryGetValue(id, out var subscription)
            ? new TopicSubscriptionSnapshot(
                subscription.Cursor,
                subscription.Durability,
                subscription.Connected)
            : null;
    }

    /// <summary>Handle to one subscriber's demand-driven unread suffix.</summary>
    public Computed<IReadOnlyList<T>>? ReaderHandle(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        return _readers.GetValueOrDefault(id);
    }

    /// <summary>Creates an atomic defensive snapshot suitable for restart.</summary>
    public TopicSnapshot<T> Snapshot() =>
        new(
            _baseOffset,
            _retained,
            _subscriptions.ToDictionary(
                pair => pair.Key,
                pair => new TopicSubscriptionSnapshot(
                    pair.Value.Cursor,
                    pair.Value.Durability,
                    pair.Value.Connected),
                StringComparer.Ordinal));

    private Computed<IReadOnlyList<T>> EnsureReader(string id)
    {
        if (_readers.TryGetValue(id, out var existing)) return existing;

        var version = _ctx.Source(0);
        _readerVersions.Add(id, version);
        _readerVersionNumbers.Add(id, 0);
        var reader = _ctx.Computed<IReadOnlyList<T>>(cx =>
        {
            cx.Get(version);
            if (!_subscriptions.TryGetValue(id, out var subscription) ||
                !subscription.Connected)
                return Array.Empty<T>();

            var start = checked((int)(subscription.Cursor - _baseOffset));
            return _retained.Skip(start).ToArray();
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
