using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Replays the canonical queue-family corpus against ALL THREE execution flavors.
/// </summary>
/// <remarks>
/// <para>
/// Before <c>lzqueuefamilyflavors</c> this binding shipped only the single-threaded flavor, and
/// the canonical <c>coverage.json</c> honestly recorded six absent cells. Both other flavors now
/// exist, so the corpus has to be driven against every one of them — a ledger flip with no replay
/// behind it is exactly the false green the ledger exists to prevent.
/// </para>
/// <para>
/// Three things keep this from reporting green while testing nothing:
/// </para>
/// <list type="number">
/// <item><description>
/// Invalidation is asserted PER READER KIND and in BOTH directions, against the cell's own reader
/// node. A step whose fixture says <c>invalidates.head: false</c> fails if the flavor invalidated
/// anyway, so over-invalidation is as visible as under-invalidation.
/// </description></item>
/// <item><description>
/// Every replay returns its step count and the totals are asserted exactly. An absence guard
/// proves the corpus resolved; only an exact count proves this process drove it.
/// </description></item>
/// <item><description>
/// The 3x3 ledger is checked against the flavors the replay actually runs, so a row cannot claim
/// a flavor the runner never touched.
/// </description></item>
/// </list>
/// </remarks>
public sealed class QueueFamilyConformanceTests
{
    private const string Corpus = "collections";

    private static readonly string[] QueueFixtures =
    [
        "queuecell_spsc_push_pop.json",
        "queuecell_popped_head_observation.json",
        "queuecell_mpsc_multi_writer.json",
        "queuecell_bounded_backpressure.json",
        "queuecell_closure_lifecycle.json",
    ];

    private static readonly string[] TopicFixtures =
    [
        "topiccell_broadcast_cursor_isolation.json",
        "topiccell_durable_replay_gc.json",
        "topiccell_ephemeral_lifecycle.json",
        "topiccell_offline_tail_bounds.json",
    ];

    private static readonly string[] WorkQueueFixtures =
    [
        "workqueue_competing_delivery.json",
        "workqueue_lease_deadletter.json",
    ];

    // Pinned, so a fixture losing steps upstream cannot silently shrink the gate.
    private const int ExpectedQueueSteps = 31;
    private const int ExpectedTopicSteps = 29;
    private const int ExpectedWorkQueueSteps = 18;

    // Every reader kind the corpus declares, per flavor. Exact, not a floor: a fixture that
    // stopped declaring a matrix would otherwise shrink the gate silently.
    private const int ExpectedInvalidationChecks = 198;

    /// <summary>One row of the 3x3 flavor ledger.</summary>
    private sealed record LedgerRow(string Primitive, string Flavor, Type Implementation);

    private static readonly LedgerRow[] Ledger =
    [
        new("QueueCell", "single-threaded", typeof(QueueCell<>)),
        new("QueueCell", "thread-safe", typeof(ThreadSafeQueueCell<>)),
        new("QueueCell", "async", typeof(AsyncQueueCell<>)),
        new("TopicCell", "single-threaded", typeof(TopicCell<>)),
        new("TopicCell", "thread-safe", typeof(ThreadSafeTopicCell<>)),
        new("TopicCell", "async", typeof(AsyncTopicCell<>)),
        new("WorkQueueCell", "single-threaded", typeof(WorkQueueCell<>)),
        new("WorkQueueCell", "thread-safe", typeof(ThreadSafeWorkQueueCell<>)),
        new("WorkQueueCell", "async", typeof(AsyncWorkQueueCell<>)),
    ];

    // --- flavor surface -------------------------------------------------------
    //
    // The flavor axis lives in the runner, never in the corpus.

    /// <summary>One reader node, probed for cache validity in both directions.</summary>
    private interface IReaderProbe
    {
        /// <summary>Materialize the reader so the next op's invalidation is observable.</summary>
        void Refresh();

        /// <summary>True when the node is still cached, i.e. was NOT invalidated.</summary>
        bool StillValid();
    }

    private interface IQueueRunner
    {
        string Push(string value);
        string Pop();
        void Close();
        void BatchPush(IReadOnlyList<string> values);
        IReaderProbe Probe(string kind);
        IReadOnlyList<string> Kinds { get; }
        string? Head();
        int Len();
        bool IsEmpty();
        bool IsFull();
        bool IsClosed();
    }

    private interface ITopicRunner
    {
        long BaseOffset { get; }
        IReadOnlyList<string> Elements();
        IReadOnlyList<string> SubscriptionIds();
        TopicSubscriptionSnapshot? SubscriptionState(string id);
        TopicSubscribeOutcome Subscribe(string id, TopicDurability durability);
        TopicSubscribeOutcome Reconnect(string id);
        bool Disconnect(string id);
        long Publish(string value);
        string? Advance(string id);
        int CollectGarbage();
        IReadOnlyList<string> ReadStream(string id);
        IReaderProbe? Probe(string id);
    }

    private interface IWorkQueueRunner
    {
        long Push(string value);
        WorkQueueDelivery<string>? Claim(string worker, long now);
        bool Ack(string worker, long deliveryId);
        bool Nack(string worker, long deliveryId);
        int ReapExpired(long now);
        IReadOnlyList<WorkQueueItem<string>> Pending();
        IReadOnlyList<WorkQueueDelivery<string>> InFlight();
        IReadOnlyList<WorkQueueDeadLetter<string>> DeadLetters();
        int PendingLen();
        bool IsEmpty();
        int InFlightLen();
        int DeadLetterLen();
        IReaderProbe Probe(string kind);
        IReadOnlyList<string> Kinds { get; }
    }

    private interface IQueueFlavor
    {
        string Name { get; }
        IQueueRunner Queue(int? capacity);
        ITopicRunner Topic(TopicSnapshot<string> initial);
        IWorkQueueRunner WorkQueue(long visibilityTimeout, int maxDeliveries);
    }

    private static readonly string[] QueueKinds = ["head", "len", "is_empty", "is_full", "closed"];
    private static readonly string[] WorkQueueKinds =
        ["pending_len", "is_empty", "in_flight_len", "dead_letter_len"];

    // --- single-threaded flavor ------------------------------------------------

    private sealed class SyncProbe<T>(Context ctx, Computed<T> node) : IReaderProbe
    {
        public void Refresh() => _ = node.Get();
        public bool StillValid() => node.Peek(out _);
        internal Context Ctx => ctx;
    }

    private sealed class SyncFlavor : IQueueFlavor
    {
        public string Name => "single-threaded";

        public IQueueRunner Queue(int? capacity) => new Runner(capacity);

        public ITopicRunner Topic(TopicSnapshot<string> initial) => new TopicRunner(initial);

        public IWorkQueueRunner WorkQueue(long visibilityTimeout, int maxDeliveries) =>
            new WorkRunner(visibilityTimeout, maxDeliveries);

        private sealed class Runner : IQueueRunner
        {
            private readonly Context _ctx = new();
            private readonly QueueCell<string> _q;
            private readonly Dictionary<string, IReaderProbe> _probes;

            internal Runner(int? capacity)
            {
                _q = new QueueCell<string>(_ctx, capacity);
                var h = _q.ReaderHandles();
                _probes = new Dictionary<string, IReaderProbe>(StringComparer.Ordinal)
                {
                    ["head"] = new SyncProbe<string?>(_ctx, h.Head),
                    ["len"] = new SyncProbe<int>(_ctx, h.Len),
                    ["is_empty"] = new SyncProbe<bool>(_ctx, h.IsEmpty),
                    ["is_full"] = new SyncProbe<bool>(_ctx, h.IsFull),
                    ["closed"] = new SyncProbe<bool>(_ctx, h.IsClosed),
                };
            }

            public IReadOnlyList<string> Kinds => QueueKinds;
            public IReaderProbe Probe(string kind) => _probes[kind];
            public string Push(string value) => _q.TryPush(value).ToString();
            public string Pop()
            {
                var popped = _q.TryPop();
                return popped.IsValue ? popped.Value! : popped.Status.ToString();
            }
            public void Close() => _q.Close();
            public void BatchPush(IReadOnlyList<string> values) => _ctx.Batch(() =>
            {
                foreach (var value in values) Assert.Equal(QueuePushResult.Ok, _q.TryPush(value));
            });
            public string? Head() => _q.Head();
            public int Len() => _q.Len();
            public bool IsEmpty() => _q.IsEmpty();
            public bool IsFull() => _q.IsFull();
            public bool IsClosed() => _q.IsClosed();
        }

        private sealed class TopicRunner(TopicSnapshot<string> initial) : ITopicRunner
        {
            private readonly Context _ctx = new();
            private TopicCell<string>? _topic;

            private TopicCell<string> Cell => _topic ??= new TopicCell<string>(_ctx, initial);

            public long BaseOffset => Cell.BaseOffset;
            public IReadOnlyList<string> Elements() => Cell.Elements();
            public IReadOnlyList<string> SubscriptionIds() => Cell.SubscriptionIds();
            public TopicSubscriptionSnapshot? SubscriptionState(string id) =>
                Cell.SubscriptionState(id);
            public TopicSubscribeOutcome Subscribe(string id, TopicDurability durability) =>
                Cell.Subscribe(id, durability);
            public TopicSubscribeOutcome Reconnect(string id) => Cell.Reconnect(id);
            public bool Disconnect(string id) => Cell.Disconnect(id);
            public long Publish(string value) => Cell.Publish(value);
            public string? Advance(string id) => Cell.Advance(id);
            public int CollectGarbage() => Cell.CollectGarbage();
            public IReadOnlyList<string> ReadStream(string id) => Cell.ReadStream(id);
            public IReaderProbe? Probe(string id)
            {
                var handle = Cell.ReaderHandle(id);
                return handle is null ? null : new SyncProbe<IReadOnlyList<string>>(_ctx, handle);
            }
        }

        private sealed class WorkRunner : IWorkQueueRunner
        {
            private readonly Context _ctx = new();
            private readonly WorkQueueCell<string> _q;
            private readonly Dictionary<string, IReaderProbe> _probes;

            internal WorkRunner(long visibilityTimeout, int maxDeliveries)
            {
                _q = new WorkQueueCell<string>(_ctx, visibilityTimeout, maxDeliveries);
                var h = _q.ReaderHandles();
                _probes = new Dictionary<string, IReaderProbe>(StringComparer.Ordinal)
                {
                    ["pending_len"] = new SyncProbe<int>(_ctx, h.PendingLen),
                    ["is_empty"] = new SyncProbe<bool>(_ctx, h.IsEmpty),
                    ["in_flight_len"] = new SyncProbe<int>(_ctx, h.InFlightLen),
                    ["dead_letter_len"] = new SyncProbe<int>(_ctx, h.DeadLetterLen),
                };
            }

            public IReadOnlyList<string> Kinds => WorkQueueKinds;
            public IReaderProbe Probe(string kind) => _probes[kind];
            public long Push(string value) => _q.Push(value);
            public WorkQueueDelivery<string>? Claim(string worker, long now) => _q.Claim(worker, now);
            public bool Ack(string worker, long deliveryId) => _q.Ack(worker, deliveryId);
            public bool Nack(string worker, long deliveryId) => _q.Nack(worker, deliveryId);
            public int ReapExpired(long now) => _q.ReapExpired(now);
            public IReadOnlyList<WorkQueueItem<string>> Pending() => _q.Pending();
            public IReadOnlyList<WorkQueueDelivery<string>> InFlight() => _q.InFlight();
            public IReadOnlyList<WorkQueueDeadLetter<string>> DeadLetters() => _q.DeadLetters();
            public int PendingLen() => _q.PendingLen();
            public bool IsEmpty() => _q.IsEmpty();
            public int InFlightLen() => _q.InFlightLen();
            public int DeadLetterLen() => _q.DeadLetterLen();
        }
    }

    // --- thread-safe flavor ----------------------------------------------------

    private sealed class ThreadSafeProbe<T>(ThreadSafeContext ctx, Computed<T> node) : IReaderProbe
    {
        public void Refresh() => ctx.WithLock(inner => node.Get(inner));
        public bool StillValid() => ctx.WithLock(_ => node.Peek(out T _));
    }

    private sealed class ThreadSafeFlavor : IQueueFlavor
    {
        public string Name => "thread-safe";

        public IQueueRunner Queue(int? capacity) => new Runner(capacity);

        public ITopicRunner Topic(TopicSnapshot<string> initial) => new TopicRunner(initial);

        public IWorkQueueRunner WorkQueue(long visibilityTimeout, int maxDeliveries) =>
            new WorkRunner(visibilityTimeout, maxDeliveries);

        private sealed class Runner : IQueueRunner
        {
            private readonly ThreadSafeContext _ctx = new();
            private readonly ThreadSafeQueueCell<string> _q;
            private readonly Dictionary<string, IReaderProbe> _probes;

            internal Runner(int? capacity)
            {
                _q = new ThreadSafeQueueCell<string>(_ctx, capacity);
                var h = _q.ReaderHandles();
                _probes = new Dictionary<string, IReaderProbe>(StringComparer.Ordinal)
                {
                    ["head"] = new ThreadSafeProbe<string?>(_ctx, h.Head),
                    ["len"] = new ThreadSafeProbe<int>(_ctx, h.Len),
                    ["is_empty"] = new ThreadSafeProbe<bool>(_ctx, h.IsEmpty),
                    ["is_full"] = new ThreadSafeProbe<bool>(_ctx, h.IsFull),
                    ["closed"] = new ThreadSafeProbe<bool>(_ctx, h.IsClosed),
                };
            }

            public IReadOnlyList<string> Kinds => QueueKinds;
            public IReaderProbe Probe(string kind) => _probes[kind];
            public string Push(string value) => _q.TryPush(value).ToString();
            public string Pop()
            {
                var popped = _q.TryPop();
                return popped.IsValue ? popped.Value! : popped.Status.ToString();
            }
            public void Close() => _q.Close();
            public void BatchPush(IReadOnlyList<string> values) => _ctx.Batch(() =>
            {
                foreach (var value in values) Assert.Equal(QueuePushResult.Ok, _q.TryPush(value));
            });
            public string? Head() => _q.Head();
            public int Len() => _q.Len();
            public bool IsEmpty() => _q.IsEmpty();
            public bool IsFull() => _q.IsFull();
            public bool IsClosed() => _q.IsClosed();
        }

        private sealed class TopicRunner(TopicSnapshot<string> initial) : ITopicRunner
        {
            private readonly ThreadSafeContext _ctx = new();
            private ThreadSafeTopicCell<string>? _topic;

            private ThreadSafeTopicCell<string> Cell =>
                _topic ??= new ThreadSafeTopicCell<string>(_ctx, initial);

            public long BaseOffset => Cell.BaseOffset;
            public IReadOnlyList<string> Elements() => Cell.Elements();
            public IReadOnlyList<string> SubscriptionIds() => Cell.SubscriptionIds();
            public TopicSubscriptionSnapshot? SubscriptionState(string id) =>
                Cell.SubscriptionState(id);
            public TopicSubscribeOutcome Subscribe(string id, TopicDurability durability) =>
                Cell.Subscribe(id, durability);
            public TopicSubscribeOutcome Reconnect(string id) => Cell.Reconnect(id);
            public bool Disconnect(string id) => Cell.Disconnect(id);
            public long Publish(string value) => Cell.Publish(value);
            public string? Advance(string id) => Cell.Advance(id);
            public int CollectGarbage() => Cell.CollectGarbage();
            public IReadOnlyList<string> ReadStream(string id) => Cell.ReadStream(id);
            public IReaderProbe? Probe(string id)
            {
                var handle = Cell.ReaderHandle(id);
                return handle is null
                    ? null
                    : new ThreadSafeProbe<IReadOnlyList<string>>(_ctx, handle);
            }
        }

        private sealed class WorkRunner : IWorkQueueRunner
        {
            private readonly ThreadSafeContext _ctx = new();
            private readonly ThreadSafeWorkQueueCell<string> _q;
            private readonly Dictionary<string, IReaderProbe> _probes;

            internal WorkRunner(long visibilityTimeout, int maxDeliveries)
            {
                _q = new ThreadSafeWorkQueueCell<string>(_ctx, visibilityTimeout, maxDeliveries);
                var h = _q.ReaderHandles();
                _probes = new Dictionary<string, IReaderProbe>(StringComparer.Ordinal)
                {
                    ["pending_len"] = new ThreadSafeProbe<int>(_ctx, h.PendingLen),
                    ["is_empty"] = new ThreadSafeProbe<bool>(_ctx, h.IsEmpty),
                    ["in_flight_len"] = new ThreadSafeProbe<int>(_ctx, h.InFlightLen),
                    ["dead_letter_len"] = new ThreadSafeProbe<int>(_ctx, h.DeadLetterLen),
                };
            }

            public IReadOnlyList<string> Kinds => WorkQueueKinds;
            public IReaderProbe Probe(string kind) => _probes[kind];
            public long Push(string value) => _q.Push(value);
            public WorkQueueDelivery<string>? Claim(string worker, long now) => _q.Claim(worker, now);
            public bool Ack(string worker, long deliveryId) => _q.Ack(worker, deliveryId);
            public bool Nack(string worker, long deliveryId) => _q.Nack(worker, deliveryId);
            public int ReapExpired(long now) => _q.ReapExpired(now);
            public IReadOnlyList<WorkQueueItem<string>> Pending() => _q.Pending();
            public IReadOnlyList<WorkQueueDelivery<string>> InFlight() => _q.InFlight();
            public IReadOnlyList<WorkQueueDeadLetter<string>> DeadLetters() => _q.DeadLetters();
            public int PendingLen() => _q.PendingLen();
            public bool IsEmpty() => _q.IsEmpty();
            public int InFlightLen() => _q.InFlightLen();
            public int DeadLetterLen() => _q.DeadLetterLen();
        }
    }

    // --- async flavor ----------------------------------------------------------
    //
    // Reads are Task-typed because an AsyncContext slot read is Task-typed by construction, not
    // because ordering is async-coloured: every op below is synchronous.

    private sealed class AsyncProbe<T>(AsyncComputed<T> node) : IReaderProbe
    {
        public void Refresh() => _ = node.GetAsync().GetAwaiter().GetResult();
        public bool StillValid() => node.TryGet(out _);
    }

    private sealed class AsyncFlavor : IQueueFlavor
    {
        public string Name => "async";

        public IQueueRunner Queue(int? capacity) => new Runner(capacity);

        public ITopicRunner Topic(TopicSnapshot<string> initial) => new TopicRunner(initial);

        public IWorkQueueRunner WorkQueue(long visibilityTimeout, int maxDeliveries) =>
            new WorkRunner(visibilityTimeout, maxDeliveries);

        private sealed class Runner : IQueueRunner
        {
            private readonly AsyncContext _ctx = new();
            private readonly AsyncQueueCell<string> _q;
            private readonly Dictionary<string, IReaderProbe> _probes;

            internal Runner(int? capacity)
            {
                _q = new AsyncQueueCell<string>(_ctx, capacity);
                var h = _q.ReaderHandles();
                _probes = new Dictionary<string, IReaderProbe>(StringComparer.Ordinal)
                {
                    ["head"] = new AsyncProbe<string?>(h.Head),
                    ["len"] = new AsyncProbe<int>(h.Len),
                    ["is_empty"] = new AsyncProbe<bool>(h.IsEmpty),
                    ["is_full"] = new AsyncProbe<bool>(h.IsFull),
                    ["closed"] = new AsyncProbe<bool>(h.IsClosed),
                };
            }

            public IReadOnlyList<string> Kinds => QueueKinds;
            public IReaderProbe Probe(string kind) => _probes[kind];
            public string Push(string value) => _q.TryPush(value).ToString();
            public string Pop()
            {
                var popped = _q.TryPop();
                return popped.IsValue ? popped.Value! : popped.Status.ToString();
            }
            public void Close() => _q.Close();
            public void BatchPush(IReadOnlyList<string> values) => _ctx.Batch(() =>
            {
                foreach (var value in values) Assert.Equal(QueuePushResult.Ok, _q.TryPush(value));
            });
            public string? Head() => _q.HeadAsync().GetAwaiter().GetResult();
            public int Len() => _q.LenAsync().GetAwaiter().GetResult();
            public bool IsEmpty() => _q.IsEmptyAsync().GetAwaiter().GetResult();
            public bool IsFull() => _q.IsFullAsync().GetAwaiter().GetResult();
            public bool IsClosed() => _q.IsClosedAsync().GetAwaiter().GetResult();
        }

        private sealed class TopicRunner(TopicSnapshot<string> initial) : ITopicRunner
        {
            private readonly AsyncContext _ctx = new();
            private AsyncTopicCell<string>? _topic;

            private AsyncTopicCell<string> Cell =>
                _topic ??= new AsyncTopicCell<string>(_ctx, initial);

            public long BaseOffset => Cell.BaseOffset;
            public IReadOnlyList<string> Elements() => Cell.Elements();
            public IReadOnlyList<string> SubscriptionIds() => Cell.SubscriptionIds();
            public TopicSubscriptionSnapshot? SubscriptionState(string id) =>
                Cell.SubscriptionState(id);
            public TopicSubscribeOutcome Subscribe(string id, TopicDurability durability) =>
                Cell.Subscribe(id, durability);
            public TopicSubscribeOutcome Reconnect(string id) => Cell.Reconnect(id);
            public bool Disconnect(string id) => Cell.Disconnect(id);
            public long Publish(string value) => Cell.Publish(value);
            public string? Advance(string id) => Cell.Advance(id);
            public int CollectGarbage() => Cell.CollectGarbage();
            public IReadOnlyList<string> ReadStream(string id) =>
                Cell.ReadStreamAsync(id).GetAwaiter().GetResult();
            public IReaderProbe? Probe(string id)
            {
                var handle = Cell.ReaderHandle(id);
                return handle is null ? null : new AsyncProbe<IReadOnlyList<string>>(handle);
            }
        }

        private sealed class WorkRunner : IWorkQueueRunner
        {
            private readonly AsyncContext _ctx = new();
            private readonly AsyncWorkQueueCell<string> _q;
            private readonly Dictionary<string, IReaderProbe> _probes;

            internal WorkRunner(long visibilityTimeout, int maxDeliveries)
            {
                _q = new AsyncWorkQueueCell<string>(_ctx, visibilityTimeout, maxDeliveries);
                var h = _q.ReaderHandles();
                _probes = new Dictionary<string, IReaderProbe>(StringComparer.Ordinal)
                {
                    ["pending_len"] = new AsyncProbe<int>(h.PendingLen),
                    ["is_empty"] = new AsyncProbe<bool>(h.IsEmpty),
                    ["in_flight_len"] = new AsyncProbe<int>(h.InFlightLen),
                    ["dead_letter_len"] = new AsyncProbe<int>(h.DeadLetterLen),
                };
            }

            public IReadOnlyList<string> Kinds => WorkQueueKinds;
            public IReaderProbe Probe(string kind) => _probes[kind];
            public long Push(string value) => _q.Push(value);
            public WorkQueueDelivery<string>? Claim(string worker, long now) => _q.Claim(worker, now);
            public bool Ack(string worker, long deliveryId) => _q.Ack(worker, deliveryId);
            public bool Nack(string worker, long deliveryId) => _q.Nack(worker, deliveryId);
            public int ReapExpired(long now) => _q.ReapExpired(now);
            public IReadOnlyList<WorkQueueItem<string>> Pending() => _q.Pending();
            public IReadOnlyList<WorkQueueDelivery<string>> InFlight() => _q.InFlight();
            public IReadOnlyList<WorkQueueDeadLetter<string>> DeadLetters() => _q.DeadLetters();
            public int PendingLen() => _q.PendingLenAsync().GetAwaiter().GetResult();
            public bool IsEmpty() => _q.IsEmptyAsync().GetAwaiter().GetResult();
            public int InFlightLen() => _q.InFlightLenAsync().GetAwaiter().GetResult();
            public int DeadLetterLen() => _q.DeadLetterLenAsync().GetAwaiter().GetResult();
        }
    }

    private static IQueueFlavor[] Flavors() => [new SyncFlavor(), new ThreadSafeFlavor(), new AsyncFlavor()];

    public static TheoryData<string> FlavorNames()
    {
        var data = new TheoryData<string>();
        foreach (var flavor in Flavors()) data.Add(flavor.Name);
        return data;
    }

    private static IQueueFlavor FlavorNamed(string name) =>
        Flavors().Single(flavor => StringComparer.Ordinal.Equals(flavor.Name, name));

    // --- replays ---------------------------------------------------------------

    private static void AssertCorpusPresent() =>
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}; " +
            "clone lazily-spec as a sibling. A skip here would report green while testing nothing.");

    private static (int Steps, int Checks) ReplayQueue(IQueueFlavor flavor, string fixture)
    {
        using var doc = SpecCorpus.Load(Corpus, fixture);
        var root = doc.RootElement;
        Assert.Equal("QueueCell", root.GetProperty("model").GetString());

        var initial = root.GetProperty("initial");
        int? capacity = initial.TryGetProperty("capacity", out var cap) && cap.ValueKind == JsonValueKind.Number
            ? cap.GetInt32()
            : null;
        Assert.True(
            !initial.TryGetProperty("elements", out var seed) || seed.GetArrayLength() == 0,
            $"{fixture}: this runner does not seed initial.elements; a fixture needing one must " +
            "extend the runner rather than be skipped");

        var q = flavor.Queue(capacity);
        var steps = 0;
        var checks = 0;
        var index = 0;

        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            foreach (var kind in q.Kinds) q.Probe(kind).Refresh();

            var op = step.GetProperty("op");
            var type = op.GetProperty("type").GetString();
            string? returned = null;

            switch (type)
            {
                case "push":
                case "try_push":
                    returned = q.Push(op.GetProperty("value").GetString()!);
                    break;
                case "pop":
                case "try_pop":
                    returned = q.Pop();
                    break;
                case "close":
                    q.Close();
                    break;
                case "batch":
                    // MPSC: several producers inside one batch, which is one invalidation
                    // frontier — the point of the fixture.
                    q.BatchPush(op.GetProperty("ops").EnumerateArray()
                        .Select(sub =>
                        {
                            Assert.Equal("push", sub.GetProperty("type").GetString());
                            return sub.GetProperty("value").GetString()!;
                        })
                        .ToArray());
                    break;
                default:
                    throw new InvalidOperationException(
                        $"{flavor.Name} {fixture} step {index}: unhandled op '{type}'");
            }

            var expected = step.GetProperty("expected");

            // Invalidation FIRST — reading a reader revalidates it.
            if (expected.TryGetProperty("invalidates", out var invalidates))
            {
                foreach (var probe in invalidates.EnumerateObject())
                {
                    var wasInvalidated = !q.Probe(probe.Name).StillValid();
                    Assert.True(
                        wasInvalidated == probe.Value.GetBoolean(),
                        $"{flavor.Name} {fixture} step {index}: invalidates.{probe.Name} — expected " +
                        $"{probe.Value.GetBoolean()}, got {wasInvalidated}. Reader kinds are " +
                        "independent: a push onto a non-empty queue must not touch head.");
                    checks++;
                }
            }

            if (step.TryGetProperty("returns", out var wantReturn) &&
                wantReturn.ValueKind == JsonValueKind.String)
            {
                Assert.Equal(wantReturn.GetString(), returned);
            }

            if (expected.TryGetProperty("len", out var wantLen))
                Assert.Equal(wantLen.GetInt32(), q.Len());
            if (expected.TryGetProperty("is_empty", out var wantEmpty))
                Assert.Equal(wantEmpty.GetBoolean(), q.IsEmpty());
            if (expected.TryGetProperty("is_full", out var wantFull))
                Assert.Equal(wantFull.GetBoolean(), q.IsFull());
            if (expected.TryGetProperty("closed", out var wantClosed))
                Assert.Equal(wantClosed.GetBoolean(), q.IsClosed());
            if (expected.TryGetProperty("head", out var wantHead))
            {
                Assert.Equal(
                    wantHead.ValueKind == JsonValueKind.Null ? null : wantHead.GetString(),
                    q.Head());
            }

            index++;
            steps++;
        }

        return (steps, checks);
    }

    private static (int Steps, int Checks) ReplayTopic(IQueueFlavor flavor, string fixture)
    {
        using var doc = SpecCorpus.Load(Corpus, fixture);
        var root = doc.RootElement;
        Assert.Equal("TopicCell", root.GetProperty("model").GetString());

        var topic = flavor.Topic(ReadTopicSnapshot(root.GetProperty("initial")));
        var steps = 0;
        var checks = 0;
        var index = 0;

        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var expected = step.GetProperty("expected");
            var invalidates = expected.GetProperty("invalidates");
            // Capture the handles BEFORE the op: a removed ephemeral subscriber must still be
            // able to report its own final transition.
            var before = invalidates.EnumerateObject()
                .ToDictionary(probe => probe.Name, probe => topic.Probe(probe.Name), StringComparer.Ordinal);
            foreach (var probe in before.Values) probe?.Refresh();

            var op = step.GetProperty("op");
            var type = op.GetProperty("type").GetString();
            object? returned = null;

            switch (type)
            {
                case "publish":
                    topic.Publish(op.GetProperty("value").GetString()!);
                    break;
                case "advance":
                    returned = topic.Advance(op.GetProperty("subscriber").GetString()!);
                    break;
                case "subscribe":
                    returned = topic.Subscribe(
                        op.GetProperty("subscriber").GetString()!,
                        ReadDurability(op.GetProperty("durability").GetString()!)).ToString();
                    break;
                case "reconnect":
                    returned = topic.Reconnect(op.GetProperty("subscriber").GetString()!).ToString();
                    break;
                case "disconnect":
                    topic.Disconnect(op.GetProperty("subscriber").GetString()!);
                    break;
                case "restart":
                    // Process restart is observational: persisted durable state is unchanged.
                    break;
                case "gc":
                    returned = topic.CollectGarbage();
                    break;
                default:
                    throw new InvalidOperationException(
                        $"{flavor.Name} {fixture} step {index}: unhandled op '{type}'");
            }

            // Invalidation FIRST — reading a reader revalidates it.
            foreach (var probe in invalidates.EnumerateObject())
            {
                var handle = before[probe.Name] ?? topic.Probe(probe.Name);
                Assert.True(
                    handle is not null,
                    $"{flavor.Name} {fixture} step {index}: no reader for '{probe.Name}'");
                var invalidated = !handle!.StillValid();
                Assert.True(
                    invalidated == probe.Value.GetBoolean(),
                    $"{flavor.Name} {fixture} step {index}: invalidates.{probe.Name} — expected " +
                    $"{probe.Value.GetBoolean()}, got {invalidated}. Cursors are independent: " +
                    "advancing one subscriber must not wake another.");
                checks++;
            }

            Assert.Equal(expected.GetProperty("base_offset").GetInt64(), topic.BaseOffset);
            Assert.Equal(
                expected.GetProperty("elements").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                topic.Elements());
            AssertSubscriptions(flavor, fixture, index, topic, expected.GetProperty("subscriptions"));

            if (expected.TryGetProperty("reads", out var reads))
            {
                foreach (var read in reads.EnumerateObject())
                {
                    Assert.Equal(
                        read.Value.EnumerateArray().Select(e => e.GetString()!).ToArray(),
                        topic.ReadStream(read.Name));
                }
            }

            AssertReturns(flavor, fixture, index, step, returned);

            index++;
            steps++;
        }

        return (steps, checks);
    }

    private static (int Steps, int Checks) ReplayWorkQueue(IQueueFlavor flavor, string fixture)
    {
        using var doc = SpecCorpus.Load(Corpus, fixture);
        var root = doc.RootElement;
        Assert.Equal("WorkQueueCell", root.GetProperty("model").GetString());

        // The lease configuration is a fixture field, not a runner constant: a binding that
        // hardcoded it could not notice the corpus changing under it.
        var config = root.GetProperty("config");
        var queue = flavor.WorkQueue(
            config.GetProperty("visibility_timeout").GetInt64(),
            config.GetProperty("max_deliveries").GetInt32());

        var steps = 0;
        var checks = 0;
        var index = 0;

        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            foreach (var kind in queue.Kinds) queue.Probe(kind).Refresh();

            var op = step.GetProperty("op");
            var type = op.GetProperty("type").GetString();
            object? returned = null;

            switch (type)
            {
                case "push":
                    returned = queue.Push(op.GetProperty("value").GetString()!);
                    break;
                case "claim":
                    returned = queue.Claim(
                        op.GetProperty("worker").GetString()!, op.GetProperty("now").GetInt64());
                    break;
                case "ack":
                    returned = queue.Ack(
                        op.GetProperty("worker").GetString()!,
                        op.GetProperty("delivery_id").GetInt64());
                    break;
                case "nack":
                    returned = queue.Nack(
                        op.GetProperty("worker").GetString()!,
                        op.GetProperty("delivery_id").GetInt64());
                    break;
                case "reap_expired":
                    returned = queue.ReapExpired(op.GetProperty("now").GetInt64());
                    break;
                default:
                    throw new InvalidOperationException(
                        $"{flavor.Name} {fixture} step {index}: unhandled op '{type}'");
            }

            var expected = step.GetProperty("expected");

            // Invalidation FIRST — reading a reader revalidates it.
            foreach (var probe in expected.GetProperty("invalidates").EnumerateObject())
            {
                var invalidated = !queue.Probe(probe.Name).StillValid();
                Assert.True(
                    invalidated == probe.Value.GetBoolean(),
                    $"{flavor.Name} {fixture} step {index}: invalidates.{probe.Name} — expected " +
                    $"{probe.Value.GetBoolean()}, got {invalidated}");
                checks++;
            }

            AssertReturns(flavor, fixture, index, step, returned);
            AssertPending(flavor, fixture, index, queue, expected.GetProperty("pending"));
            AssertInFlight(flavor, fixture, index, queue, expected.GetProperty("in_flight"));
            AssertDeadLetters(flavor, fixture, index, queue, expected.GetProperty("dead_letters"));

            var wantReads = expected.GetProperty("reads");
            Assert.Equal(wantReads.GetProperty("pending_len").GetInt32(), queue.PendingLen());
            Assert.Equal(wantReads.GetProperty("is_empty").GetBoolean(), queue.IsEmpty());
            Assert.Equal(wantReads.GetProperty("in_flight_len").GetInt32(), queue.InFlightLen());
            Assert.Equal(wantReads.GetProperty("dead_letter_len").GetInt32(), queue.DeadLetterLen());

            index++;
            steps++;
        }

        return (steps, checks);
    }

    // --- the gate ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(FlavorNames))]
    public void ReplaysTheWholeQueueFamilyCorpus(string flavorName)
    {
        AssertCorpusPresent();
        var flavor = FlavorNamed(flavorName);

        var queueSteps = 0;
        var topicSteps = 0;
        var workSteps = 0;
        var checks = 0;

        foreach (var fixture in QueueFixtures)
        {
            var (steps, c) = ReplayQueue(flavor, fixture);
            queueSteps += steps;
            checks += c;
        }
        foreach (var fixture in TopicFixtures)
        {
            var (steps, c) = ReplayTopic(flavor, fixture);
            topicSteps += steps;
            checks += c;
        }
        foreach (var fixture in WorkQueueFixtures)
        {
            var (steps, c) = ReplayWorkQueue(flavor, fixture);
            workSteps += steps;
            checks += c;
        }

        // Exact counts, not floors: a runner that silently replayed less would otherwise pass.
        Assert.Equal(ExpectedQueueSteps, queueSteps);
        Assert.Equal(ExpectedTopicSteps, topicSteps);
        Assert.Equal(ExpectedWorkQueueSteps, workSteps);
        Assert.True(
            checks == ExpectedInvalidationChecks,
            $"{flavorName}: {checks} invalidation assertions, expected {ExpectedInvalidationChecks} — " +
            "the per-reader-kind matrix is what makes this corpus discriminating");
    }

    [Fact]
    public void TheCorpusHoldsThePinnedNumberOfSteps()
    {
        AssertCorpusPresent();
        Assert.Equal(ExpectedQueueSteps, CountSteps(QueueFixtures));
        Assert.Equal(ExpectedTopicSteps, CountSteps(TopicFixtures));
        Assert.Equal(ExpectedWorkQueueSteps, CountSteps(WorkQueueFixtures));
    }

    [Theory]
    [MemberData(nameof(FlavorNames))]
    public void TheInvalidationProbeDiscriminates(string flavorName)
    {
        // The whole corpus leans on "the reader node stopped being cached" meaning "the library
        // invalidated it". Pin that the probe can fail in BOTH directions.
        var q = FlavorNamed(flavorName).Queue(null);
        Assert.Equal(QueuePushResult.Ok, Enum.Parse<QueuePushResult>(q.Push("a")));
        foreach (var kind in q.Kinds) q.Probe(kind).Refresh();

        // A push onto a NON-empty queue leaves head alone — reader-kind independence.
        q.Push("b");
        Assert.True(
            q.Probe("head").StillValid(),
            $"{flavorName}: head was invalidated by a push onto a non-empty queue");

        // A pop always advances head.
        foreach (var kind in q.Kinds) q.Probe(kind).Refresh();
        q.Pop();
        Assert.False(
            q.Probe("head").StillValid(),
            $"{flavorName}: head survived a pop — the probe cannot fail, so every invalidation " +
            "assertion in this file would be vacuous");
    }

    [Fact]
    public void TheLedgerIs3x3AndEveryRowIsReplayed()
    {
        // In a summary line, "skipped" and "passed" are indistinguishable.
        Assert.Equal(9, Ledger.Length);
        Assert.Equal(3, Ledger.Select(row => row.Primitive).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, Ledger.Select(row => row.Flavor).Distinct(StringComparer.Ordinal).Count());

        // A row cannot claim a flavor the replay never drives, and a driven flavor cannot be
        // missing from the ledger.
        Assert.Equal(
            Ledger.Select(row => row.Flavor).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToArray(),
            Flavors().Select(flavor => flavor.Name).OrderBy(f => f, StringComparer.Ordinal).ToArray());

        // And a row cannot claim a type that does not exist: these are real open generic types
        // resolved by the compiler, so a deleted class fails the build rather than the assertion.
        foreach (var row in Ledger)
        {
            Assert.True(
                row.Implementation.IsGenericTypeDefinition,
                $"{row.Primitive} / {row.Flavor}: ledger entry is not a generic cell type");
        }
    }

    [Fact]
    public void FixturesNestTheMatrixUnderExpectedNotOnTheStep()
    {
        AssertCorpusPresent();
        var matrices = 0;
        foreach (var fixture in QueueFixtures.Concat(TopicFixtures).Concat(WorkQueueFixtures))
        {
            using var doc = SpecCorpus.Load(Corpus, fixture);
            var index = 0;
            foreach (var step in doc.RootElement.GetProperty("steps").EnumerateArray())
            {
                Assert.False(
                    step.TryGetProperty("invalidates", out _),
                    $"{fixture} step {index}: `invalidates` appears at STEP level; the runners read " +
                    "expected.invalidates, so a step-level copy is silently ignored");
                Assert.True(step.TryGetProperty("expected", out var expected), $"{fixture} step {index}");
                if (expected.TryGetProperty("invalidates", out _)) matrices++;
                index++;
            }
        }
        Assert.True(matrices > 0, "no fixture carried an expected.invalidates matrix");
    }

    // --- helpers ----------------------------------------------------------------

    private static int CountSteps(IEnumerable<string> fixtures)
    {
        var total = 0;
        foreach (var fixture in fixtures)
        {
            using var doc = SpecCorpus.Load(Corpus, fixture);
            total += doc.RootElement.GetProperty("steps").GetArrayLength();
        }
        return total;
    }

    private static TopicDurability ReadDurability(string raw) => raw switch
    {
        "durable" => TopicDurability.Durable,
        "ephemeral" => TopicDurability.Ephemeral,
        _ => throw new InvalidOperationException($"unknown topic durability '{raw}'"),
    };

    private static TopicSnapshot<string> ReadTopicSnapshot(JsonElement initial)
    {
        var baseOffset = initial.TryGetProperty("base_offset", out var b) ? b.GetInt64() : 0;
        var elements = initial.TryGetProperty("elements", out var e)
            ? e.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : [];
        var subscriptions = new Dictionary<string, TopicSubscriptionSnapshot>(StringComparer.Ordinal);
        if (initial.TryGetProperty("subscriptions", out var subs))
        {
            foreach (var sub in subs.EnumerateObject())
            {
                subscriptions.Add(sub.Name, new TopicSubscriptionSnapshot(
                    sub.Value.GetProperty("cursor").GetInt64(),
                    ReadDurability(sub.Value.GetProperty("durability").GetString()!),
                    sub.Value.GetProperty("connected").GetBoolean()));
            }
        }
        return new TopicSnapshot<string>(baseOffset, elements, subscriptions);
    }

    private static void AssertSubscriptions(
        IQueueFlavor flavor, string fixture, int index, ITopicRunner topic, JsonElement expected)
    {
        var wanted = expected.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(wanted, topic.SubscriptionIds());
        foreach (var sub in expected.EnumerateObject())
        {
            var state = topic.SubscriptionState(sub.Name);
            Assert.True(state is not null, $"{flavor.Name} {fixture} step {index}: no subscription {sub.Name}");
            Assert.Equal(sub.Value.GetProperty("cursor").GetInt64(), state!.Cursor);
            Assert.Equal(ReadDurability(sub.Value.GetProperty("durability").GetString()!), state.Durability);
            Assert.Equal(sub.Value.GetProperty("connected").GetBoolean(), state.Connected);
        }
    }

    private static void AssertReturns(
        IQueueFlavor flavor, string fixture, int index, JsonElement step, object? returned)
    {
        if (!step.TryGetProperty("returns", out var want)) return;
        var where = $"{flavor.Name} {fixture} step {index}: returns";
        switch (want.ValueKind)
        {
            case JsonValueKind.Null:
                Assert.True(returned is null, where);
                break;
            case JsonValueKind.Number:
                Assert.Equal(want.GetInt64(), Convert.ToInt64(returned));
                break;
            case JsonValueKind.String:
                Assert.Equal(want.GetString(), (string?)returned);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(want.GetBoolean(), (bool)returned!);
                break;
            case JsonValueKind.Object:
                {
                    var delivery = Assert.IsType<WorkQueueDelivery<string>>(returned);
                    Assert.Equal(want.GetProperty("delivery_id").GetInt64(), delivery.DeliveryId);
                    Assert.Equal(want.GetProperty("item_id").GetInt64(), delivery.ItemId);
                    Assert.Equal(want.GetProperty("value").GetString(), delivery.Value);
                    Assert.Equal(want.GetProperty("worker").GetString(), delivery.Worker);
                    Assert.Equal(want.GetProperty("attempt").GetInt32(), delivery.Attempt);
                    Assert.Equal(want.GetProperty("deadline").GetInt64(), delivery.Deadline);
                    break;
                }
            default:
                throw new InvalidOperationException($"{where}: unhandled expectation kind {want.ValueKind}");
        }
    }

    private static void AssertPending(
        IQueueFlavor flavor, string fixture, int index, IWorkQueueRunner queue, JsonElement expected)
    {
        var actual = queue.Pending();
        var wanted = expected.EnumerateArray().ToArray();
        Assert.True(wanted.Length == actual.Count,
            $"{flavor.Name} {fixture} step {index}: pending count {actual.Count} != {wanted.Length}");
        for (var i = 0; i < wanted.Length; i++)
        {
            Assert.Equal(wanted[i].GetProperty("item_id").GetInt64(), actual[i].ItemId);
            Assert.Equal(wanted[i].GetProperty("value").GetString(), actual[i].Value);
            Assert.Equal(wanted[i].GetProperty("attempts").GetInt32(), actual[i].Attempts);
        }
    }

    private static void AssertInFlight(
        IQueueFlavor flavor, string fixture, int index, IWorkQueueRunner queue, JsonElement expected)
    {
        var actual = queue.InFlight();
        var wanted = expected.EnumerateArray().ToArray();
        Assert.True(wanted.Length == actual.Count,
            $"{flavor.Name} {fixture} step {index}: in_flight count {actual.Count} != {wanted.Length}");
        for (var i = 0; i < wanted.Length; i++)
        {
            Assert.Equal(wanted[i].GetProperty("delivery_id").GetInt64(), actual[i].DeliveryId);
            Assert.Equal(wanted[i].GetProperty("item_id").GetInt64(), actual[i].ItemId);
            Assert.Equal(wanted[i].GetProperty("value").GetString(), actual[i].Value);
            Assert.Equal(wanted[i].GetProperty("worker").GetString(), actual[i].Worker);
            Assert.Equal(wanted[i].GetProperty("attempt").GetInt32(), actual[i].Attempt);
            Assert.Equal(wanted[i].GetProperty("deadline").GetInt64(), actual[i].Deadline);
        }
    }

    private static void AssertDeadLetters(
        IQueueFlavor flavor, string fixture, int index, IWorkQueueRunner queue, JsonElement expected)
    {
        var actual = queue.DeadLetters();
        var wanted = expected.EnumerateArray().ToArray();
        Assert.True(wanted.Length == actual.Count,
            $"{flavor.Name} {fixture} step {index}: dead_letters count {actual.Count} != {wanted.Length}");
        for (var i = 0; i < wanted.Length; i++)
        {
            Assert.Equal(wanted[i].GetProperty("item_id").GetInt64(), actual[i].ItemId);
            Assert.Equal(wanted[i].GetProperty("value").GetString(), actual[i].Value);
            Assert.Equal(wanted[i].GetProperty("attempts").GetInt32(), actual[i].Attempts);
            Assert.Equal(
                wanted[i].GetProperty("reason").GetString() switch
                {
                    "nack" => WorkQueueDeadLetterReason.Nack,
                    "expired" => WorkQueueDeadLetterReason.Expired,
                    var other => throw new InvalidOperationException($"unknown reason '{other}'"),
                },
                actual[i].Reason);
        }
    }
}
