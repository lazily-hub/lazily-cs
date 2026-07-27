using System;
using System.Collections.Generic;
using System.Linq;

namespace Lazily;

/// <summary>How spilled relay windows are laid out on the durable tail.</summary>
public enum SpillMode
{
    /// <summary>Merge summaries into the open page until its operation-count limit is reached.</summary>
    CompactOnWrite,

    /// <summary>Append each coalesced window as an immutable page.</summary>
    AppendCompact,
}

/// <summary>One immutable cold-page summary.</summary>
public sealed record SpillPage<T>(ulong Id, T Summary, ulong Bytes);

/// <summary>Bounded metadata for one live spill page.</summary>
public sealed record SpillManifestEntry(ulong Id, ulong Bytes);

/// <summary>
/// In-memory reference backend for a paged durable relay tail with ack-before-reclaim.
/// </summary>
public sealed class SpillStore<T>
{
    private readonly MergePolicy<T> _merge;
    private readonly SpillMode _mode;
    private readonly ulong _pageSize;
    private readonly List<SpillPage<T>> _pages = [];
    private ulong _openFill;
    private ulong _nextId;
    private int _acked;

    /// <summary>Creates a spill store with a positive operation-count page size.</summary>
    public SpillStore(SpillMode mode, ulong pageSize, MergePolicy<T> merge)
    {
        ArgumentNullException.ThrowIfNull(merge);
        if (pageSize == 0)
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                "spill page size must be positive");
        _mode = mode;
        _pageSize = pageSize;
        _merge = merge;
    }

    /// <summary>
    /// Writes one coalesced hot-window summary to the durable tail.
    /// </summary>
    public void Spill(T window, ulong bytes)
    {
        if (_mode == SpillMode.AppendCompact)
        {
            PushPage(window, bytes);
            return;
        }

        if (_pages.Count == 0 || _openFill >= _pageSize)
        {
            PushPage(window, bytes);
            _openFill = 1;
            return;
        }

        var last = _pages[^1];
        _pages[^1] = last with
        {
            Summary = _merge.Merge(last.Summary, window),
            Bytes = checked(last.Bytes + bytes),
        };
        _openFill++;
    }

    /// <summary>Defensive manifest snapshot for every live page.</summary>
    public IReadOnlyList<SpillManifestEntry> Manifest() =>
        _pages.Select(page => new SpillManifestEntry(page.Id, page.Bytes)).ToArray();

    /// <summary>Defensive snapshot of pages at or after the egress ack cursor.</summary>
    public IReadOnlyList<SpillPage<T>> PendingPages() => _pages.Skip(_acked).ToArray();

    /// <summary>Number of live pages, including acknowledged pages not yet reclaimed.</summary>
    public int PageCount => _pages.Count;

    /// <summary>Number of pages acknowledged but not yet reclaimed.</summary>
    public int AcknowledgedCount => _acked;

    /// <summary>Advances the egress cursor through an existing page id, inclusive.</summary>
    public void AcknowledgeThrough(ulong id)
    {
        while (_acked < _pages.Count && _pages[_acked].Id <= id) _acked++;
    }

    /// <summary>Drops acknowledged pages while preserving pending order and identities.</summary>
    public void Reclaim()
    {
        if (_acked == 0) return;
        _pages.RemoveRange(0, _acked);
        _acked = 0;
        if (_pages.Count == 0) _openFill = 0;
    }

    /// <summary>Folds every live cold page oldest-first into an initial state.</summary>
    public T FoldPages(T initial)
    {
        var accumulated = initial;
        foreach (var page in _pages)
            accumulated = _merge.Merge(accumulated, page.Summary);
        return accumulated;
    }

    /// <summary>
    /// Reconstructs the flat relay fold from cold pages followed by an optional hot head.
    /// </summary>
    public T Reconstruct(T initial, T? hot, bool hasHot)
    {
        var cold = FoldPages(initial);
        return hasHot ? _merge.Merge(cold, hot!) : cold;
    }

    /// <summary>
    /// Replays every unacknowledged page into downstream. Idempotent policies converge when the
    /// last delivered-but-unacknowledged page is replayed after a crash.
    /// </summary>
    public T ReplayUnacknowledged(T downstream)
    {
        var accumulated = downstream;
        foreach (var page in _pages.Skip(_acked))
            accumulated = _merge.Merge(accumulated, page.Summary);
        return accumulated;
    }

    private void PushPage(T summary, ulong bytes)
    {
        _pages.Add(new SpillPage<T>(_nextId, summary, bytes));
        _nextId = checked(_nextId + 1);
    }
}
