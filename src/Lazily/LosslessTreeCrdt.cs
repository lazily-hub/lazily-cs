using System.Text;

namespace Lazily;

/// <summary>A dotted Lamport operation identifier ordered by counter and then peer.</summary>
/// <param name="Counter">The Lamport counter.</param>
/// <param name="Peer">The originating peer.</param>
public readonly record struct TreeOpId(long Counter, long Peer) : IComparable<TreeOpId>
{
    /// <inheritdoc />
    public int CompareTo(TreeOpId other)
    {
        var counter = Counter.CompareTo(other.Counter);
        return counter != 0 ? counter : Peer.CompareTo(other.Peer);
    }
}

/// <summary>The stable identity of a tree node: the operation that created it.</summary>
/// <param name="Operation">The node's creation operation.</param>
public readonly record struct TreeNodeId(TreeOpId Operation)
{
    /// <summary>The sentinel document root.</summary>
    public static TreeNodeId Root { get; } = new(new TreeOpId(0, 0));
}

/// <summary>The exact-source role of a lossless tree leaf.</summary>
public enum LeafKind
{
    /// <summary>A syntax marker or delimiter.</summary>
    Token,

    /// <summary>Whitespace, comments, separators, or other trivia.</summary>
    Trivia,

    /// <summary>Valid source deliberately kept opaque.</summary>
    Raw,

    /// <summary>Invalid or ambiguous source that must still round-trip.</summary>
    Error,
}

/// <summary>An immutable seed for an element or exact-text leaf.</summary>
public sealed class NodeSeed : IEquatable<NodeSeed>
{
    private NodeSeed(string? elementKind, LeafKind? leafKind, string? text)
    {
        ElementKind = elementKind;
        LeafKind = leafKind;
        Text = text;
    }

    /// <summary>The element kind, or null for a leaf seed.</summary>
    public string? ElementKind { get; }

    /// <summary>The leaf kind, or null for an element seed.</summary>
    public LeafKind? LeafKind { get; }

    /// <summary>The exact leaf source, or null for an element seed.</summary>
    public string? Text { get; }

    /// <summary>Creates an internal element seed.</summary>
    public static NodeSeed Element(string kind)
    {
        Guard.NotNullOrEmpty(kind, nameof(kind));
        return new NodeSeed(kind, leafKind: null, text: null);
    }

    /// <summary>Creates an exact-source leaf seed.</summary>
    public static NodeSeed Leaf(LeafKind kind, string text)
    {
        Guard.NotNull(text, nameof(text));
        return new NodeSeed(elementKind: null, kind, text);
    }

    /// <inheritdoc />
    public bool Equals(NodeSeed? other) =>
        other is not null
        && string.Equals(ElementKind, other.ElementKind, StringComparison.Ordinal)
        && LeafKind == other.LeafKind
        && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NodeSeed other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ElementKind, LeafKind, Text);
}

/// <summary>A fractional sibling position tiebroken by its minting peer.</summary>
public sealed class TreePosition : IComparable<TreePosition>, IEquatable<TreePosition>
{
    private readonly byte[] _fractionalKey;

    internal TreePosition(IEnumerable<byte> fractionalKey, long peer)
    {
        _fractionalKey = [.. fractionalKey];
        Peer = peer;
    }

    /// <summary>The fractional-index bytes.</summary>
    public IReadOnlyList<byte> FractionalKey => Array.AsReadOnly(_fractionalKey);

    /// <summary>The peer that minted this position.</summary>
    public long Peer { get; }

    internal byte[] Bytes => _fractionalKey;

    /// <inheritdoc />
    public int CompareTo(TreePosition? other)
    {
        if (other is null) return 1;
        var common = Math.Min(_fractionalKey.Length, other._fractionalKey.Length);
        for (var index = 0; index < common; index++)
        {
            var order = _fractionalKey[index].CompareTo(other._fractionalKey[index]);
            if (order != 0) return order;
        }

        var length = _fractionalKey.Length.CompareTo(other._fractionalKey.Length);
        return length != 0 ? length : Peer.CompareTo(other.Peer);
    }

    /// <inheritdoc />
    public bool Equals(TreePosition? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TreePosition other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _fractionalKey) hash.Add(item);
        hash.Add(Peer);
        return hash.ToHashCode();
    }

    internal TreePosition Copy() => new(_fractionalKey, Peer);
}

/// <summary>The payload of a transport-ready tree operation.</summary>
public abstract record TreeOperation;

/// <summary>Creates one stable node under an existing parent.</summary>
public sealed record CreateNodeOperation(
    TreeNodeId Node,
    TreeNodeId Parent,
    TreePosition Position,
    NodeSeed Seed) : TreeOperation;

/// <summary>Applies a sticky node tombstone.</summary>
public sealed record TombstoneNodeOperation(TreeNodeId Node) : TreeOperation;

/// <summary>Applies a last-writer-wins sibling-position reassignment.</summary>
public sealed record ReorderNodeOperation(TreeNodeId Node, TreePosition Position) : TreeOperation;

/// <summary>Applies an identity-preserving text delta to one leaf.</summary>
public sealed record EditLeafOperation(
    TreeNodeId Node,
    TreeOpId Previous,
    IReadOnlyList<TextOp> Delta) : TreeOperation;

/// <summary>Splits one leaf after a scalar index, preserving total rendered text.</summary>
public sealed record SplitLeafOperation(
    TreeNodeId Node,
    TreeNodeId NewNode,
    TreePosition Position,
    int ScalarIndex,
    TreeOpId Previous) : TreeOperation;

/// <summary>Merges two adjacent leaves while tombstoning the right identity.</summary>
public sealed record MergeLeavesOperation(
    TreeNodeId Left,
    TreeNodeId Right,
    TreeOpId PreviousLeft,
    TreeOpId PreviousRight) : TreeOperation;

/// <summary>A dotted tree operation and its self-contained payload.</summary>
public sealed record TreeOp(TreeOpId Id, TreeOperation Operation)
{
    internal TreeOp Copy() =>
        new(
            Id,
            Operation switch
            {
                CreateNodeOperation create =>
                    create with { Position = create.Position.Copy() },
                ReorderNodeOperation reorder =>
                    reorder with { Position = reorder.Position.Copy() },
                EditLeafOperation edit =>
                    edit with { Delta = edit.Delta.ToArray() },
                SplitLeafOperation split =>
                    split with { Position = split.Position.Copy() },

                // Tombstone and Merge carry only value-typed ids, so identity IS the deep copy.
                TombstoneNodeOperation or MergeLeavesOperation => Operation,

                // `TreeOperation` is a public, non-sealed base. A variant this build does not know
                // may carry a mutable payload, and returning it by identity would let the
                // "defensive copy" in TreeUpdate silently alias a caller's buffer — the exact
                // aliasing this method exists to prevent.
                _ => throw new ArgumentOutOfRangeException(
                    nameof(Operation),
                    Operation.GetType().Name,
                    "Unknown tree operation variant cannot be defensively copied."),
            });
}

/// <summary>A transport batch returned by diff and accepted by update application.</summary>
public sealed class TreeUpdate
{
    /// <summary>Creates an update from operations, defensively copying their payloads.</summary>
    public TreeUpdate(IEnumerable<TreeOp> operations)
    {
        Guard.NotNull(operations, nameof(operations));
        Operations = operations.Select(operation => operation.Copy()).ToArray();
    }

    /// <summary>The dotted operations carried by this update.</summary>
    public IReadOnlyList<TreeOp> Operations { get; }

    /// <summary>Whether this update carries no operations.</summary>
    public bool IsEmpty => Operations.Count == 0;
}

/// <summary>
/// A dotted, non-contiguous frontier. Each peer retains a contiguous prefix plus sparse dots above
/// holes, so anti-entropy never mistakes a high delivered operation for all preceding operations.
/// </summary>
public sealed class TreeVersionFrontier : IEquatable<TreeVersionFrontier>
{
    private readonly SortedDictionary<long, DotRange> _dots = [];

    /// <summary>Creates an empty frontier.</summary>
    public TreeVersionFrontier()
    {
    }

    private TreeVersionFrontier(IEnumerable<KeyValuePair<long, DotRange>> dots)
    {
        foreach (var pair in dots) _dots.Add(pair.Key, pair.Value.Copy());
    }

    /// <summary>Whether the exact dotted operation has been observed.</summary>
    public bool Contains(TreeOpId id) =>
        _dots.TryGetValue(id.Peer, out var range) && range.Contains(id.Counter);

    /// <summary>Returns an independent frontier copy.</summary>
    public TreeVersionFrontier Copy() => new(_dots);

    /// <inheritdoc />
    public bool Equals(TreeVersionFrontier? other)
    {
        if (other is null || _dots.Count != other._dots.Count) return false;
        return _dots.All(
            pair =>
                other._dots.TryGetValue(pair.Key, out var range)
                && pair.Value.Equals(range));
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is TreeVersionFrontier other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var pair in _dots)
        {
            hash.Add(pair.Key);
            hash.Add(pair.Value);
        }
        return hash.ToHashCode();
    }

    internal void Observe(TreeOpId id)
    {
        if (!_dots.TryGetValue(id.Peer, out var range))
        {
            range = new DotRange();
            _dots.Add(id.Peer, range);
        }
        range.Observe(id.Counter);
    }

    private sealed class DotRange : IEquatable<DotRange>
    {
        private readonly SortedSet<long> _sparse = [];

        internal long Contiguous { get; private set; }

        internal bool Contains(long counter) =>
            counter <= Contiguous || _sparse.Contains(counter);

        internal void Observe(long counter)
        {
            if (counter <= Contiguous) return;
            _sparse.Add(counter);
            while (_sparse.Remove(checked(Contiguous + 1))) Contiguous++;
        }

        internal DotRange Copy()
        {
            var copy = new DotRange { Contiguous = Contiguous };
            copy._sparse.UnionWith(_sparse);
            return copy;
        }

        public bool Equals(DotRange? other) =>
            other is not null
            && Contiguous == other.Contiguous
            && _sparse.SetEquals(other._sparse);

        public override bool Equals(object? obj) => obj is DotRange other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Contiguous);
            foreach (var dot in _sparse) hash.Add(dot);
            return hash.ToHashCode();
        }
    }
}

/// <summary>The reason a lossless-tree mutation was rejected.</summary>
public enum TreeError
{
    /// <summary>The named node was absent.</summary>
    NotFound,

    /// <summary>The operation required a leaf but found an element.</summary>
    NotLeaf,

    /// <summary>A UTF-8 byte offset was out of range or inside a scalar.</summary>
    NonScalarBoundary,

    /// <summary>The two leaves were not adjacent live siblings.</summary>
    NotAdjacent,
}

/// <summary>An exception that rejects a mutation before it could lose source text.</summary>
public sealed class TreeException : InvalidOperationException
{
    /// <summary>Creates an exception for the rejected tree operation.</summary>
    public TreeException(TreeError error)
        : base(
            error switch
            {
                TreeError.NotFound => "node not found",
                TreeError.NotLeaf => "node is not a leaf",
                TreeError.NonScalarBoundary =>
                    "offset out of range or not on a UTF-8 scalar boundary",
                TreeError.NotAdjacent => "leaves are not adjacent live siblings",

                // INTENTIONAL: this is a MESSAGE formatter running inside an exception
                // constructor. The machine-readable verdict is `Error`, which is preserved
                // verbatim below, so nothing dispatches on this string; throwing here would
                // replace the caller's real rejection with an unrelated failure and destroy the
                // reason they were about to read. Pinned by `AnUnknownTreeErrorStillCarriesItsCode`.
                _ => "tree mutation rejected",
            })
    {
        Error = error;
    }

    /// <summary>The machine-readable rejection reason.</summary>
    public TreeError Error { get; }
}

/// <summary>
/// A rooted lossless concrete-syntax tree CRDT. Internal elements own structure only; leaves own
/// every rendered byte through embedded <see cref="TextCrdt"/> replicas.
/// </summary>
public sealed class LosslessTreeCrdt
    : ICrdtTree<LosslessTreeCrdt, TreeVersionFrontier, TreeUpdate, string>
{
    private readonly Dictionary<TreeNodeId, NodeRecord> _nodes = [];
    private readonly List<TreeOp> _log = [];
    private readonly List<TreeOp> _buffered = [];
    private readonly TreeVersionFrontier _frontier = new();
    private long _counter;

    /// <summary>Creates a fresh document containing only the sentinel root.</summary>
    public LosslessTreeCrdt(long peer)
    {
        Peer = peer;
        _nodes.Add(
            TreeNodeId.Root,
            new NodeRecord(
                parent: null,
                new TreePosition([], 0),
                new TreeOpId(0, 0),
                NodeBody.Element("root"),
                tombstone: null,
                new TreeOpId(0, 0)));
    }

    private LosslessTreeCrdt(LosslessTreeCrdt source, long peer)
    {
        Peer = peer;
        _counter = source._counter;
        foreach (var pair in source._nodes) _nodes.Add(pair.Key, pair.Value.Copy());
        foreach (var operation in source._log) _log.Add(operation.Copy());
        foreach (var operation in source._buffered) _buffered.Add(operation.Copy());
        foreach (var operation in _log) _frontier.Observe(operation.Id);
    }

    /// <summary>The peer that owns local tree operations.</summary>
    public long Peer { get; }

    /// <inheritdoc />
    public string Text => Render();

    /// <inheritdoc />
    public string Value => Render();

    /// <summary>The number of live nodes excluding the sentinel root.</summary>
    public int LiveNodeCount =>
        _nodes.Count(
            pair =>
                pair.Key != TreeNodeId.Root
                && pair.Value.Tombstone is null);

    /// <summary>Forks the full operation state under a new local peer identity.</summary>
    public LosslessTreeCrdt Fork(long peer) => new(this, peer);

    /// <summary>Returns an independent copy retaining this replica's peer identity.</summary>
    public LosslessTreeCrdt Copy() => Fork(Peer);

    /// <summary>Renders exact leaf text in converged tree order.</summary>
    public string Render()
    {
        var builder = new StringBuilder();
        RenderInto(TreeNodeId.Root, builder);
        return builder.ToString();
    }

    /// <summary>Returns the live children of an element in rendered order.</summary>
    public IReadOnlyList<TreeNodeId> Children(TreeNodeId parent) => LiveChildren(parent);

    /// <summary>Returns an element kind, or null when the node is absent or a leaf.</summary>
    public string? ElementKind(TreeNodeId node) =>
        _nodes.TryGetValue(node, out var record) && record.Body.ElementKind is { } kind
            ? kind
            : null;

    /// <summary>Returns a leaf kind, or null when the node is absent or an element.</summary>
    public LeafKind? GetLeafKind(TreeNodeId node) =>
        _nodes.TryGetValue(node, out var record) ? record.Body.LeafKind : null;

    /// <summary>Returns a leaf's exact text.</summary>
    public string LeafText(TreeNodeId node) => LeafTextCrdt(node).Text;

    /// <summary>Creates a node under a parent, immediately after an optional sibling anchor.</summary>
    public TreeNodeId CreateNode(
        TreeNodeId parent,
        Optional<TreeNodeId> after,
        NodeSeed seed)
    {
        Guard.NotNull(seed, nameof(seed));
        if (!_nodes.ContainsKey(parent)) throw new TreeException(TreeError.NotFound);
        var position = KeyAfter(parent, after);
        var operationId = NextOperationId();
        var node = new TreeNodeId(operationId);
        CommitLocal(
            new TreeOp(
                operationId,
                new CreateNodeOperation(node, parent, position, seed)));
        return node;
    }

    /// <summary>Creates a node at the front of a parent's children.</summary>
    public TreeNodeId CreateNode(TreeNodeId parent, NodeSeed seed) =>
        CreateNode(parent, Optional<TreeNodeId>.None, seed);

    /// <summary>Tombstones a node; descendants consequently render away with their ancestor.</summary>
    public void TombstoneNode(TreeNodeId node)
    {
        if (!_nodes.ContainsKey(node) || node == TreeNodeId.Root)
            throw new TreeException(TreeError.NotFound);
        var operationId = NextOperationId();
        CommitLocal(
            new TreeOp(
                operationId,
                new TombstoneNodeOperation(node)));
    }

    /// <summary>Reorders a child within its current parent with one LWW position assignment.</summary>
    public void ReorderChild(TreeNodeId node, Optional<TreeNodeId> after)
    {
        if (!_nodes.TryGetValue(node, out var record) || record.Parent is null)
            throw new TreeException(TreeError.NotFound);
        var position = KeyAfter(record.Parent.Value, after);
        var operationId = NextOperationId();
        CommitLocal(
            new TreeOp(
                operationId,
                new ReorderNodeOperation(node, position)));
    }

    /// <summary>Moves a child to the front of its current parent.</summary>
    public void ReorderChild(TreeNodeId node) =>
        ReorderChild(node, Optional<TreeNodeId>.None);

    /// <summary>
    /// Deletes and inserts text at UTF-8 byte offsets within a leaf. Both offsets must be exact
    /// Unicode scalar boundaries.
    /// </summary>
    public void EditLeaf(
        TreeNodeId node,
        int atByte,
        int deleteBytes,
        string insert)
    {
        Guard.NotNull(insert, nameof(insert));
        var source = LeafText(node);
        var start = ByteToScalarIndex(source, atByte);
        var endOffset = (long)atByte + deleteBytes;
        var end =
            endOffset is >= int.MinValue and <= int.MaxValue
                ? ByteToScalarIndex(source, (int)endOffset)
                : null;
        if (start is null || end is null || end < start)
            throw new TreeException(TreeError.NonScalarBoundary);

        var record = _nodes[node];
        record.Body.Text = record.Body.Text!.Fork(Peer);
        var version = record.Body.Text.VersionVector();
        for (var index = start.Value; index < end.Value; index++)
            record.Body.Text.Delete(start.Value);
        record.Body.Text.InsertString(start.Value, insert);
        var delta = record.Body.Text.DeltaSince(version);
        var previous = record.TextHead;
        var operationId = NextOperationId();
        CommitLocal(
            new TreeOp(
                operationId,
                new EditLeafOperation(node, previous, delta)));
    }

    /// <summary>Splits a leaf at a UTF-8 byte boundary while preserving total rendered text.</summary>
    public TreeNodeId SplitLeaf(TreeNodeId node, int atByte)
    {
        var source = LeafText(node);
        var scalarIndex = ByteToScalarIndex(source, atByte);
        if (scalarIndex is null) throw new TreeException(TreeError.NonScalarBoundary);
        var record = _nodes[node];
        if (record.Parent is null) throw new TreeException(TreeError.NotFound);
        var position = KeyAfter(record.Parent.Value, Optional<TreeNodeId>.Some(node));
        var previous = record.TextHead;
        var operationId = NextOperationId();
        var newNode = new TreeNodeId(operationId);
        CommitLocal(
            new TreeOp(
                operationId,
                new SplitLeafOperation(
                    node,
                    newNode,
                    position,
                    scalarIndex.Value,
                    previous)));
        return newNode;
    }

    /// <summary>Merges adjacent live leaf siblings without changing rendered text.</summary>
    public void MergeAdjacentLeaves(TreeNodeId left, TreeNodeId right)
    {
        LeafText(left);
        LeafText(right);
        var leftRecord = _nodes[left];
        if (leftRecord.Parent is null) throw new TreeException(TreeError.NotFound);
        var siblings = LiveChildren(leftRecord.Parent.Value);
        var leftIndex = siblings.IndexOf(left);
        if (leftIndex < 0 || leftIndex + 1 >= siblings.Count || siblings[leftIndex + 1] != right)
            throw new TreeException(TreeError.NotAdjacent);

        var operationId = NextOperationId();
        CommitLocal(
            new TreeOp(
                operationId,
                new MergeLeavesOperation(
                    left,
                    right,
                    _nodes[left].TextHead,
                    _nodes[right].TextHead)));
    }

    /// <summary>Returns this replica's dotted frontier.</summary>
    public TreeVersionFrontier Frontier() => _frontier.Copy();

    /// <inheritdoc />
    public TreeVersionFrontier VersionVector() => Frontier();

    /// <summary>Returns every held operation absent from a partner frontier.</summary>
    public TreeUpdate Diff(TreeVersionFrontier partner)
    {
        Guard.NotNull(partner, nameof(partner));
        return new TreeUpdate(
            _log
                .Where(operation => !partner.Contains(operation.Id))
                .OrderBy(operation => operation.Id));
    }

    /// <inheritdoc />
    public TreeUpdate DeltaSince(TreeVersionFrontier version) => Diff(version);

    /// <summary>
    /// Applies remote operations idempotently. Operations with missing causal dependencies remain
    /// buffered until a later update fills the gap.
    /// </summary>
    public bool ApplyUpdate(TreeUpdate update)
    {
        Guard.NotNull(update, nameof(update));
        var before = Render();
        foreach (var operation in update.Operations)
        {
            _counter = Math.Max(_counter, operation.Id.Counter);
            if (!_frontier.Contains(operation.Id)) _buffered.Add(operation.Copy());
        }

        DrainBuffered();
        return !string.Equals(before, Render(), StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool ApplyDelta(TreeUpdate delta) => ApplyUpdate(delta);

    /// <inheritdoc />
    public bool MergeFrom(LosslessTreeCrdt other)
    {
        Guard.NotNull(other, nameof(other));
        return ApplyUpdate(other.Diff(_frontier));
    }

    private TreeOpId NextOperationId()
    {
        _counter = checked(_counter + 1);
        return new TreeOpId(_counter, Peer);
    }

    private List<TreeNodeId> LiveChildren(TreeNodeId parent) =>
        _nodes
            .Where(
                pair =>
                    pair.Value.Parent == parent
                    && pair.Value.Tombstone is null)
            .OrderBy(pair => pair.Value.Position)
            .Select(pair => pair.Key)
            .ToList();

    private void RenderInto(TreeNodeId node, StringBuilder builder)
    {
        if (!_nodes.TryGetValue(node, out var record)) return;
        if (record.Body.Text is { } text)
        {
            builder.Append(text.Text);
            return;
        }

        foreach (var child in LiveChildren(node)) RenderInto(child, builder);
    }

    private TreePosition KeyAfter(TreeNodeId parent, Optional<TreeNodeId> after)
    {
        var order = LiveChildren(parent);
        TreeNodeId? lower = null;
        TreeNodeId? upper = null;
        if (!after.HasValue)
        {
            if (order.Count > 0) upper = order[0];
        }
        else
        {
            var index = order.IndexOf(after.Value);
            if (index >= 0)
            {
                lower = after.Value;
                if (index + 1 < order.Count) upper = order[index + 1];
            }
            else if (order.Count > 0)
            {
                lower = order[^1];
            }
        }

        return new TreePosition(
            KeyBetween(
                lower is { } low ? _nodes[low].Position.Bytes : null,
                upper is { } high ? _nodes[high].Position.Bytes : null),
            Peer);
    }

    private TextCrdt LeafTextCrdt(TreeNodeId node)
    {
        if (!_nodes.TryGetValue(node, out var record))
            throw new TreeException(TreeError.NotFound);
        return record.Body.Text ?? throw new TreeException(TreeError.NotLeaf);
    }

    private void CommitLocal(TreeOp operation)
    {
        ApplyOperation(operation);
        Record(operation);
    }

    private void Record(TreeOp operation)
    {
        _frontier.Observe(operation.Id);
        _log.Add(operation.Copy());
    }

    private void DrainBuffered()
    {
        while (true)
        {
            var progressed = false;
            var pending = _buffered.ToArray();
            _buffered.Clear();
            foreach (var operation in pending)
            {
                if (_frontier.Contains(operation.Id)) continue;
                if (DependenciesReady(operation))
                {
                    ApplyOperation(operation);
                    Record(operation);
                    progressed = true;
                }
                else
                {
                    _buffered.Add(operation);
                }
            }

            if (!progressed) return;
        }
    }

    private bool DependenciesReady(TreeOp operation) =>
        operation.Operation switch
        {
            CreateNodeOperation create => _nodes.ContainsKey(create.Parent),
            TombstoneNodeOperation tombstone => _nodes.ContainsKey(tombstone.Node),
            ReorderNodeOperation reorder => _nodes.ContainsKey(reorder.Node),
            EditLeafOperation edit =>
                _nodes.ContainsKey(edit.Node)
                && _frontier.Contains(edit.Previous),
            SplitLeafOperation split =>
                _nodes.ContainsKey(split.Node)
                && _frontier.Contains(split.Previous),
            MergeLeavesOperation merge =>
                _nodes.ContainsKey(merge.Left)
                && _nodes.ContainsKey(merge.Right)
                && _frontier.Contains(merge.PreviousLeft)
                && _frontier.Contains(merge.PreviousRight),

            // `false` here does NOT mean "reject": it means "buffer and retry when the frontier
            // grows". An op whose variant nobody recognises can never become ready, so the lenient
            // answer parks it in `_buffered` forever — an unbounded silent leak that also stalls
            // every later op depending on it, with no error anywhere. Fail closed.
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation.Operation.GetType().Name,
                "Unknown tree operation variant has no dependency rule."),
        };

    private void ApplyOperation(TreeOp operation)
    {
        switch (operation.Operation)
        {
            case CreateNodeOperation create:
                if (_nodes.ContainsKey(create.Node)) return;
                _nodes.Add(
                    create.Node,
                    new NodeRecord(
                        create.Parent,
                        create.Position.Copy(),
                        operation.Id,
                        create.Seed.ElementKind is { } elementKind
                            ? NodeBody.Element(elementKind)
                            : NodeBody.Leaf(
                                create.Seed.LeafKind!.Value,
                                TextCrdt.FromString(
                                    create.Node.Operation.Peer,
                                    create.Seed.Text!)),
                        tombstone: null,
                        operation.Id));
                break;

            case TombstoneNodeOperation tombstone:
                if (_nodes.TryGetValue(tombstone.Node, out var tombstoneRecord))
                {
                    tombstoneRecord.Tombstone =
                        StickyMinimum(tombstoneRecord.Tombstone, operation.Id);
                }
                break;

            case ReorderNodeOperation reorder:
                if (_nodes.TryGetValue(reorder.Node, out var reorderRecord)
                    && operation.Id.CompareTo(reorderRecord.PositionStamp) > 0)
                {
                    reorderRecord.Position = reorder.Position.Copy();
                    reorderRecord.PositionStamp = operation.Id;
                }
                break;

            case EditLeafOperation edit:
                if (_nodes.TryGetValue(edit.Node, out var editRecord)
                    && editRecord.Body.Text is { } text)
                {
                    text.ApplyDelta(edit.Delta);
                    editRecord.TextHead = operation.Id;
                }
                break;

            case SplitLeafOperation split:
                ApplySplit(split, operation.Id);
                break;

            case MergeLeavesOperation merge:
                ApplyMerge(merge, operation.Id);
                break;

            default:
                // Reached only after DependenciesReady said yes, so an unrecognised variant here
                // would be RECORDED into the frontier while applying nothing — the peer would
                // then believe the op landed and never resend it. Silent divergence is strictly
                // worse than a rejected update.
                throw new ArgumentOutOfRangeException(
                    nameof(operation),
                    operation.Operation.GetType().Name,
                    "Unknown tree operation variant cannot be applied.");
        }
    }

    private void ApplySplit(SplitLeafOperation split, TreeOpId operationId)
    {
        if (!_nodes.TryGetValue(split.Node, out var record)
            || record.Body.Text is not { } source
            || record.Body.LeafKind is not { } leafKind)
        {
            return;
        }

        var scalars = TextCrdt.ScalarStrings(source.Text);
        var index = Math.Min(Math.Max(split.ScalarIndex, 0), scalars.Count);
        var head = string.Concat(scalars.Take(index));
        var tail = string.Concat(scalars.Skip(index));
        record.Body = NodeBody.Leaf(
            leafKind,
            TextCrdt.FromString(split.Node.Operation.Peer, head));
        record.TextHead = operationId;

        if (_nodes.ContainsKey(split.NewNode)) return;
        _nodes.Add(
            split.NewNode,
            new NodeRecord(
                record.Parent,
                split.Position.Copy(),
                operationId,
                NodeBody.Leaf(
                    leafKind,
                    TextCrdt.FromString(split.NewNode.Operation.Peer, tail)),
                tombstone: null,
                operationId));
    }

    private void ApplyMerge(MergeLeavesOperation merge, TreeOpId operationId)
    {
        if (!_nodes.TryGetValue(merge.Left, out var left)
            || !_nodes.TryGetValue(merge.Right, out var right)
            || left.Body.Text is not { } leftText
            || right.Body.Text is not { } rightText
            || left.Body.LeafKind is not { } leafKind)
        {
            return;
        }

        var combined = leftText.Text + rightText.Text;
        left.Body = NodeBody.Leaf(
            leafKind,
            TextCrdt.FromString(merge.Left.Operation.Peer, combined));
        left.TextHead = operationId;
        right.Tombstone = StickyMinimum(right.Tombstone, operationId);
    }

    private static TreeOpId StickyMinimum(TreeOpId? current, TreeOpId incoming) =>
        current is { } existing && existing.CompareTo(incoming) <= 0
            ? existing
            : incoming;

    private static int? ByteToScalarIndex(string text, int byteOffset)
    {
        if (byteOffset < 0) return null;
        var bytes = 0;
        var scalarIndex = 0;
        foreach (var scalar in TextCrdt.ScalarStrings(text))
        {
            if (bytes == byteOffset) return scalarIndex;
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(scalar));
            scalarIndex++;
        }

        return bytes == byteOffset ? scalarIndex : null;
    }

    private static byte[] KeyBetween(byte[]? lower, byte[]? upper)
    {
        var result = new List<byte>();
        var index = 0;
        var capacity = (lower?.Length ?? 0) + (upper?.Length ?? 0) + 2;
        while (index <= capacity)
        {
            var low =
                lower is { } lowerValue && index < lowerValue.Length
                    ? lowerValue[index]
                    : 0;
            var high = upper switch
            {
                { } upperValue when index < upperValue.Length => upperValue[index],
                { } => 0,
                null => 256,
            };
            if (low + 1 < high)
            {
                result.Add((byte)((low + high) / 2));
                return [.. result];
            }

            result.Add((byte)low);
            index++;
            if (low < high)
            {
                var lowerTail =
                    lower is { } value && index < value.Length
                        ? value[index..]
                        : [];
                result.AddRange(KeyBetween(lowerTail, upper: null));
                return [.. result];
            }
        }

        result.Add(128);
        return [.. result];
    }

    private sealed class NodeRecord(
        TreeNodeId? parent,
        TreePosition position,
        TreeOpId positionStamp,
        NodeBody body,
        TreeOpId? tombstone,
        TreeOpId textHead)
    {
        internal TreeNodeId? Parent { get; } = parent;

        internal TreePosition Position { get; set; } = position;

        internal TreeOpId PositionStamp { get; set; } = positionStamp;

        internal NodeBody Body { get; set; } = body;

        internal TreeOpId? Tombstone { get; set; } = tombstone;

        internal TreeOpId TextHead { get; set; } = textHead;

        internal NodeRecord Copy() =>
            new(
                Parent,
                Position.Copy(),
                PositionStamp,
                Body.Copy(),
                Tombstone,
                TextHead);
    }

    private sealed class NodeBody
    {
        private NodeBody(string? elementKind, LeafKind? leafKind, TextCrdt? text)
        {
            ElementKind = elementKind;
            LeafKind = leafKind;
            Text = text;
        }

        internal string? ElementKind { get; }

        internal LeafKind? LeafKind { get; }

        internal TextCrdt? Text { get; set; }

        internal static NodeBody Element(string kind) => new(kind, leafKind: null, text: null);

        internal static NodeBody Leaf(LeafKind kind, TextCrdt text) =>
            new(elementKind: null, kind, text);

        internal NodeBody Copy() =>
            Text is { } text
                ? Leaf(LeafKind!.Value, text.Copy())
                : Element(ElementKind!);
    }
}
