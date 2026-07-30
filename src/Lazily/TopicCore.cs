// TopicCore — the graph-agnostic broadcast-log algebra shared by all three topic flavors
// (spec tag: lzqueuefamilyflavors).
//
// A topic's reader set IS its subscriber set, so a transition reports the subscriber ids it
// dirtied rather than a fixed reader-kind matrix. The core performs no graph write; each shell
// bumps exactly those subscribers' version sources on its own graph.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Lazily;

/// <summary>
/// The graph-agnostic broadcast log: retained elements, a retention frontier, and independent
/// non-destructive subscriber cursors.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class TopicCore<T>
{
    private sealed class Subscription(long cursor, TopicDurability durability, bool connected)
    {
        internal long Cursor { get; set; } = cursor;
        internal TopicDurability Durability { get; } = durability;
        internal bool Connected { get; set; } = connected;
    }

    private static readonly string[] NoIds = [];

    private readonly List<T> _retained;
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private long _baseOffset;

    /// <summary>Recreates a topic core from a durable/live-state snapshot.</summary>
    /// <param name="initial">The snapshot to restore.</param>
    public TopicCore(TopicSnapshot<T> initial)
    {
        Guard.NotNull(initial, nameof(initial));
        if (initial.BaseOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initial), initial.BaseOffset, "topic base offset must be non-negative");
        }

        _baseOffset = initial.BaseOffset;
        _retained = [.. initial.Elements];
        var end = EndOffset;
        foreach (var (id, snapshot) in initial.Subscriptions)
        {
            Guard.NotNullOrEmpty(id, nameof(id));
            Guard.NotNull(snapshot, nameof(snapshot));
            if (snapshot.Cursor < _baseOffset || snapshot.Cursor > end)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initial),
                    snapshot.Cursor,
                    $"topic cursor for '{id}' must be within the retained absolute offset range");
            }
            if (snapshot.Durability == TopicDurability.Ephemeral && !snapshot.Connected)
            {
                throw new ArgumentException(
                    $"disconnected ephemeral topic subscription '{id}' must be removed",
                    nameof(initial));
            }

            _subscriptions.Add(
                id, new Subscription(snapshot.Cursor, snapshot.Durability, snapshot.Connected));
        }
    }

    /// <summary>Absolute offset represented by the first retained element.</summary>
    public long BaseOffset => _baseOffset;

    /// <summary>Absolute offset immediately after the retained log.</summary>
    public long EndOffset => checked(_baseOffset + _retained.Count);

    /// <summary>Subscriber identities present at construction, in stable ordinal order.</summary>
    public IReadOnlyList<string> SubscriptionIds() =>
        _subscriptions.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Creates a cursor at the current tail, or reconnects an existing durable identity without
    /// moving it. A newly created identity is reported as dirtied so the shell mints its reader.
    /// </summary>
    /// <param name="id">The stable subscriber identity.</param>
    /// <param name="durability">Whether the cursor survives disconnect.</param>
    public (TopicSubscribeOutcome Outcome, IReadOnlyList<string> Invalidated, bool Created) Subscribe(
        string id, TopicDurability durability)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        if (_subscriptions.TryGetValue(id, out var existing))
        {
            if (existing.Connected)
            {
                return (TopicSubscribeOutcome.AlreadyConnected, NoIds, false);
            }
            existing.Connected = true;
            return (TopicSubscribeOutcome.Reconnected, [id], false);
        }

        _subscriptions.Add(id, new Subscription(EndOffset, durability, connected: true));
        return (TopicSubscribeOutcome.Created, NoIds, true);
    }

    /// <summary>Reconnects a durable identity, creating it at the current tail when unknown.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public (TopicSubscribeOutcome Outcome, IReadOnlyList<string> Invalidated, bool Created) Reconnect(
        string id) => Subscribe(id, TopicDurability.Durable);

    /// <summary>
    /// Disconnects one subscriber. Durable state remains offline; ephemeral state is removed —
    /// and the removed identity still reports its own final transition.
    /// </summary>
    /// <param name="id">The stable subscriber identity.</param>
    public (bool Disconnected, IReadOnlyList<string> Invalidated, bool Removed) Disconnect(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        if (!_subscriptions.TryGetValue(id, out var subscription) || !subscription.Connected)
        {
            return (false, NoIds, false);
        }

        if (subscription.Durability == TopicDurability.Ephemeral)
        {
            _subscriptions.Remove(id);
            return (true, [id], true);
        }

        subscription.Connected = false;
        return (true, [id], false);
    }

    /// <summary>Appends one element, leaving every cursor unchanged.</summary>
    /// <param name="value">The element to append.</param>
    public (long Offset, IReadOnlyList<string> Invalidated) Publish(T value)
    {
        var offset = EndOffset;
        _retained.Add(value);
        var dirtied = _subscriptions
            .Where(pair => pair.Value.Connected && pair.Value.Cursor <= offset)
            .Select(pair => pair.Key)
            .ToArray();
        return (offset, dirtied);
    }

    /// <summary>Advances only the named subscriber and returns the element it passed.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public (T? Value, IReadOnlyList<string> Invalidated) Advance(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        if (!_subscriptions.TryGetValue(id, out var subscription) ||
            !subscription.Connected ||
            subscription.Cursor >= EndOffset)
        {
            return (default, NoIds);
        }

        var value = _retained[checked((int)(subscription.Cursor - _baseOffset))];
        subscription.Cursor++;
        return (value, [id]);
    }

    /// <summary>
    /// Removes the prefix below the minimum durable cursor, or everything when no durable
    /// subscription exists. Absolute cursors remain unchanged, so nothing is dirtied.
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

    /// <summary>The unread suffix for one connected subscriber; empty when offline or unknown.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public IReadOnlyList<T> ReadStream(string id)
    {
        if (!_subscriptions.TryGetValue(id, out var subscription) || !subscription.Connected)
        {
            return Array.Empty<T>();
        }
        var start = checked((int)(subscription.Cursor - _baseOffset));
        return _retained.Skip(start).ToArray();
    }

    /// <summary>Non-reactive retained-log snapshot.</summary>
    public IReadOnlyList<T> Elements() => _retained.ToArray();

    /// <summary>Non-reactive state for one stable subscriber.</summary>
    /// <param name="id">The stable subscriber identity.</param>
    public TopicSubscriptionSnapshot? SubscriptionState(string id)
    {
        Guard.NotNullOrEmpty(id, nameof(id));
        return _subscriptions.TryGetValue(id, out var subscription)
            ? new TopicSubscriptionSnapshot(
                subscription.Cursor, subscription.Durability, subscription.Connected)
            : null;
    }

    /// <summary>Creates an atomic defensive snapshot suitable for restart.</summary>
    public TopicSnapshot<T> Snapshot() =>
        new(
            _baseOffset,
            _retained,
            _subscriptions.ToDictionary(
                pair => pair.Key,
                pair => new TopicSubscriptionSnapshot(
                    pair.Value.Cursor, pair.Value.Durability, pair.Value.Connected),
                StringComparer.Ordinal));
}
