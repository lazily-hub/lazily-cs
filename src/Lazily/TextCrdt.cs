namespace Lazily;

/// <summary>
/// A globally unique, totally ordered identifier for a text insertion or deletion.
/// </summary>
/// <param name="Counter">The Lamport counter.</param>
/// <param name="Peer">The originating peer.</param>
public readonly record struct TextOpId(long Counter, long Peer) : IComparable<TextOpId>
{
    /// <inheritdoc />
    public int CompareTo(TextOpId other)
    {
        var counter = Counter.CompareTo(other.Counter);
        return counter != 0 ? counter : Peer.CompareTo(other.Peer);
    }
}

/// <summary>
/// The transport form of one character element, including its insertion identity and optional
/// deletion identity.
/// </summary>
/// <param name="Id">The insertion operation identifier.</param>
/// <param name="Character">Exactly one Unicode scalar value.</param>
/// <param name="Origin">The element after which this character was inserted.</param>
/// <param name="Deleted">The deletion operation identifier, when tombstoned.</param>
public sealed record TextOp(
    TextOpId Id,
    string Character,
    TextOpId? Origin,
    TextOpId? Deleted);

/// <summary>
/// A lossless document CRDT contract with an identity-preserving snapshot/delta representation.
/// </summary>
/// <typeparam name="TSelf">The concrete CRDT type.</typeparam>
/// <typeparam name="TVersion">The compact replication frontier.</typeparam>
/// <typeparam name="TDelta">The transport delta type.</typeparam>
/// <typeparam name="TValue">The materialized document value.</typeparam>
public interface ICrdtTree<TSelf, TVersion, TDelta, TValue>
    : ICellCrdt<TSelf, TValue>
    where TSelf : ICrdtTree<TSelf, TVersion, TDelta, TValue>
{
    /// <summary>The replication frontier observed by this replica.</summary>
    TVersion VersionVector();

    /// <summary>Returns the operations not represented by <paramref name="version"/>.</summary>
    TDelta DeltaSince(TVersion version);

    /// <summary>Applies an identity-preserving transport delta.</summary>
    bool ApplyDelta(TDelta delta);

    /// <summary>Returns the exact materialized document text.</summary>
    string Text { get; }
}

/// <summary>
/// A Fugue/RGA-style character CRDT. Elements form a left-origin tree whose same-origin siblings
/// are ordered by descending <see cref="TextOpId"/>. Deletes are sticky tombstones.
/// </summary>
public sealed class TextCrdt
    : ICrdtTree<
        TextCrdt,
        IReadOnlyDictionary<long, long>,
        IReadOnlyList<TextOp>,
        string>
{
    private readonly Dictionary<TextOpId, Element> _elements = [];
    private long _counter;

    /// <summary>Creates an empty replica owned by <paramref name="peer"/>.</summary>
    public TextCrdt(long peer)
    {
        Peer = peer;
    }

    private TextCrdt(long peer, long counter, IEnumerable<KeyValuePair<TextOpId, Element>> elements)
    {
        Peer = peer;
        _counter = counter;
        foreach (var pair in elements) _elements.Add(pair.Key, pair.Value.Copy());
    }

    /// <summary>The peer that owns local operations created by this replica.</summary>
    public long Peer { get; }

    /// <summary>The current visible text.</summary>
    public string Text =>
        string.Concat(
            OrderedIds(includeDeleted: false)
                .Select(id => _elements[id].Character));

    /// <inheritdoc />
    public string Value => Text;

    /// <summary>The number of visible Unicode scalar values.</summary>
    public int Length => _elements.Values.Count(element => element.Deleted is null);

    /// <summary>The number of deleted elements retained as tombstones.</summary>
    public int TombstoneCount => _elements.Values.Count(element => element.Deleted is not null);

    /// <summary>Whether the replica contains no visible characters.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Creates a seeded replica whose characters form one linear origin chain.</summary>
    public static TextCrdt FromString(long peer, string text)
    {
        Guard.NotNull(text, nameof(text));
        var result = new TextCrdt(peer);
        result.AppendRootChain(text);
        return result;
    }

    /// <summary>
    /// Forks the complete element state under a new peer identity without minting new operation
    /// identifiers.
    /// </summary>
    public TextCrdt Fork(long peer) => new(peer, _counter, _elements);

    /// <summary>Returns an independent copy that retains this replica's peer identity.</summary>
    public TextCrdt Copy() => Fork(Peer);

    /// <summary>Inserts one UTF-16 character at a visible scalar index.</summary>
    public bool Insert(int index, char character) => Insert(index, character.ToString());

    /// <summary>Inserts one Unicode scalar value at a visible scalar index.</summary>
    public bool Insert(int index, string character)
    {
        Guard.NotNull(character, nameof(character));
        if (ScalarStrings(character).Count != 1)
            throw new ArgumentException(
                "A text CRDT element must contain exactly one Unicode scalar value.",
                nameof(character));

        var visible = OrderedIds(includeDeleted: false);
        if (index < 0 || index > visible.Count) return false;
        TextOpId? origin = index == 0 ? null : visible[index - 1];
        var id = NextId();
        _elements.Add(id, new Element(character, origin, deleted: null));
        return true;
    }

    /// <summary>Inserts every Unicode scalar in <paramref name="text"/> at a visible index.</summary>
    public bool InsertString(int index, string text)
    {
        Guard.NotNull(text, nameof(text));
        var scalars = ScalarStrings(text);
        var visible = OrderedIds(includeDeleted: false);
        if (index < 0 || index > visible.Count) return false;

        TextOpId? origin = index == 0 ? null : visible[index - 1];
        foreach (var scalar in scalars)
        {
            var id = NextId();
            _elements.Add(id, new Element(scalar, origin, deleted: null));
            origin = id;
        }

        return scalars.Count > 0;
    }

    /// <summary>Tombstones the visible scalar at <paramref name="index"/>.</summary>
    public bool Delete(int index) => DeleteRange(index, 1) > 0;

    /// <summary>Tombstones up to <paramref name="count"/> visible scalars.</summary>
    public int DeleteRange(int index, int count)
    {
        if (index < 0 || count <= 0) return 0;
        var visible = OrderedIds(includeDeleted: false);
        if (index >= visible.Count) return 0;

        var end = Math.Min(visible.Count, checked(index + count));
        var deleted = 0;
        for (var cursor = index; cursor < end; cursor++)
        {
            var id = visible[cursor];
            var element = _elements[id];
            if (element.Deleted is not null) continue;
            element.Deleted = NextId();
            deleted++;
        }

        return deleted;
    }

    /// <summary>
    /// Replaces the visible document while retaining the old elements as tombstones. This is the
    /// explicit whole-document rewrite floor, not an identity-preserving snapshot.
    /// </summary>
    public bool ReplaceAll(string text)
    {
        Guard.NotNull(text, nameof(text));
        var before = Text;
        foreach (var id in _elements
                     .Where(pair => pair.Value.Deleted is null)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _elements[id].Deleted = NextId();
        }

        AppendRootChain(text);
        return !string.Equals(before, Text, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool MergeFrom(TextCrdt other)
    {
        Guard.NotNull(other, nameof(other));
        var before = Text;
        foreach (var (id, incoming) in other._elements)
        {
            Observe(id);
            if (incoming.Deleted is { } deleteId) Observe(deleteId);

            if (_elements.TryGetValue(id, out var current))
            {
                current.Deleted = StickyDelete(current.Deleted, incoming.Deleted);
            }
            else
            {
                _elements.Add(id, incoming.Copy());
            }
        }

        return !string.Equals(before, Text, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<long, long> VersionVector()
    {
        var vector = new SortedDictionary<long, long>();
        foreach (var (id, element) in _elements)
        {
            Bump(vector, id);
            if (element.Deleted is { } deleted) Bump(vector, deleted);
        }

        return vector;
    }

    /// <inheritdoc />
    public IReadOnlyList<TextOp> DeltaSince(IReadOnlyDictionary<long, long> version)
    {
        Guard.NotNull(version, nameof(version));
        var delta = new List<TextOp>();
        foreach (var (id, element) in _elements.OrderBy(pair => pair.Key))
        {
            var insertNew = !Seen(version, id);
            var deleteNew = element.Deleted is { } deleted && !Seen(version, deleted);
            if (!insertNew && !deleteNew) continue;
            delta.Add(new TextOp(id, element.Character, element.Origin, element.Deleted));
        }

        return delta;
    }

    /// <inheritdoc />
    public bool ApplyDelta(IReadOnlyList<TextOp> delta)
    {
        Guard.NotNull(delta, nameof(delta));
        var before = Text;
        foreach (var operation in delta)
        {
            Guard.NotNull(operation, nameof(delta));
            Observe(operation.Id);
            if (operation.Deleted is { } deleteId) Observe(deleteId);

            if (_elements.TryGetValue(operation.Id, out var current))
            {
                current.Deleted = StickyDelete(current.Deleted, operation.Deleted);
            }
            else
            {
                if (ScalarStrings(operation.Character).Count != 1)
                    throw new ArgumentException(
                        "A text delta character must contain exactly one Unicode scalar value.",
                        nameof(delta));
                _elements.Add(
                    operation.Id,
                    new Element(
                        operation.Character,
                        operation.Origin,
                        operation.Deleted));
            }
        }

        return !string.Equals(before, Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Collects stable deleted leaves that are not the origin of another retained element.
    /// Interior tombstones become eligible after their descendants are collected.
    /// </summary>
    public int GarbageCollect(Func<TextOpId, bool> isStable)
    {
        Guard.NotNull(isStable, nameof(isStable));
        var referenced = new HashSet<TextOpId>(
            _elements.Values
                .Where(element => element.Origin is not null)
                .Select(element => element.Origin!.Value));
        var removable = _elements
            .Where(
                pair =>
                    pair.Value.Deleted is { } deleted
                    && isStable(deleted)
                    && !referenced.Contains(pair.Key))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var id in removable) _elements.Remove(id);
        return removable.Length;
    }

    /// <summary>Collects tombstones whose deletion id is at or below a stable watermark.</summary>
    public int GarbageCollect(TextOpId watermark) =>
        GarbageCollect(delete => delete.CompareTo(watermark) <= 0);

    private static bool Seen(IReadOnlyDictionary<long, long> vector, TextOpId id) =>
        vector.TryGetValue(id.Peer, out var counter) && id.Counter <= counter;

    private static void Bump(IDictionary<long, long> vector, TextOpId id)
    {
        vector.TryGetValue(id.Peer, out var current);
        vector[id.Peer] = Math.Max(current, id.Counter);
    }

    private static TextOpId? StickyDelete(TextOpId? left, TextOpId? right) =>
        (left, right) switch
        {
            ({ } a, { } b) => a.CompareTo(b) <= 0 ? a : b,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };

    private TextOpId NextId()
    {
        _counter = checked(_counter + 1);
        return new TextOpId(_counter, Peer);
    }

    private void Observe(TextOpId id)
    {
        _counter = Math.Max(_counter, id.Counter);
    }

    private void AppendRootChain(string text)
    {
        TextOpId? origin = null;
        foreach (var scalar in ScalarStrings(text))
        {
            var id = NextId();
            _elements.Add(id, new Element(scalar, origin, deleted: null));
            origin = id;
        }
    }

    private List<TextOpId> OrderedIds(bool includeDeleted)
    {
        var roots = new List<TextOpId>();
        var children = new Dictionary<TextOpId, List<TextOpId>>();
        foreach (var (id, element) in _elements)
        {
            if (element.Origin is { } origin)
            {
                if (!children.TryGetValue(origin, out var list))
                {
                    list = [];
                    children.Add(origin, list);
                }
                list.Add(id);
            }
            else
            {
                roots.Add(id);
            }
        }

        static void Descending(List<TextOpId> ids) =>
            ids.Sort((left, right) => right.CompareTo(left));

        Descending(roots);
        foreach (var list in children.Values) Descending(list);

        var ordered = new List<TextOpId>(_elements.Count);
        var stack = new Stack<TextOpId>();
        PushReverse(stack, roots);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            var element = _elements[id];
            if (includeDeleted || element.Deleted is null) ordered.Add(id);
            if (children.TryGetValue(id, out var descendants)) PushReverse(stack, descendants);
        }

        return ordered;
    }

    private static void PushReverse(Stack<TextOpId> stack, IReadOnlyList<TextOpId> ids)
    {
        for (var index = ids.Count - 1; index >= 0; index--) stack.Push(ids[index]);
    }

    internal static List<string> ScalarStrings(string text)
    {
        var scalars = new List<string>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var length =
                char.IsHighSurrogate(text[index])
                && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1])
                    ? 2
                    : 1;
            scalars.Add(text.Substring(index, length));
            index += length - 1;
        }

        return scalars;
    }

    private sealed class Element(string character, TextOpId? origin, TextOpId? deleted)
    {
        internal string Character { get; } = character;

        internal TextOpId? Origin { get; } = origin;

        internal TextOpId? Deleted { get; set; } = deleted;

        internal Element Copy() => new(Character, Origin, Deleted);
    }
}
