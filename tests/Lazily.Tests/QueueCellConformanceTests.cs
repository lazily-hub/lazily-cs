using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Replays the canonical <c>queuecell_*.json</c> fixtures against <see cref="QueueCell{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// These live in the shared <c>collections</c> corpus but need their own runner:
/// <see cref="CollectionsConformanceTests"/> dispatches map operations
/// (<c>set_value</c>/<c>insert</c>/<c>move_*</c>) and cannot execute
/// <c>push</c>/<c>pop</c>/<c>close</c>/<c>batch</c>. Removing them from that runner's
/// unsupported ledger would break it rather than extend coverage.
/// </para>
/// <para>
/// The discriminating assertion is the <c>invalidates</c> matrix, not the values. A queue
/// that woke every reader on every operation produces exactly the right head, length and
/// flags at each step; only the invalidation matrix separates "the reader kinds are
/// independent" from "everything is one signal". It is read from under <c>expected</c>,
/// where it lives — a runner looking for it at step level would silently check nothing.
/// </para>
/// </remarks>
public sealed class QueueCellConformanceTests
{
    private const string Corpus = "collections";

    private static readonly string[] Fixtures =
    [
        "queuecell_spsc_push_pop.json",
        "queuecell_popped_head_observation.json",
        "queuecell_mpsc_multi_writer.json",
        "queuecell_bounded_backpressure.json",
        "queuecell_closure_lifecycle.json",
    ];

    /// <summary>Derived readers over each queue reader kind, so invalidation is observable.</summary>
    private sealed class Readers
    {
        private readonly Computed<string?> _head;
        private readonly Computed<int> _len;
        private readonly Computed<bool> _empty;
        private readonly Computed<bool> _full;
        private readonly Computed<bool> _closed;

        internal Readers(Context ctx, QueueCell<string> q)
        {
            _head = ctx.Computed(cx => q.Head(cx));
            _len = ctx.Computed(cx => q.Len(cx));
            _empty = ctx.Computed(cx => q.IsEmpty(cx));
            _full = ctx.Computed(cx => q.IsFull(cx));
            _closed = ctx.Computed(cx => q.IsClosed(cx));
        }

        /// <summary>Materialize every reader so the next op's invalidation is observable.</summary>
        internal void Refresh()
        {
            _ = _head.Get();
            _ = _len.Get();
            _ = _empty.Get();
            _ = _full.Get();
            _ = _closed.Get();
        }

        /// <summary>True when the named kind is still cached, i.e. was NOT invalidated.</summary>
        internal bool StillValid(string kind) => kind switch
        {
            "head" => _head.Peek(out _),
            "len" => _len.Peek(out _),
            "is_empty" => _empty.Peek(out _),
            "is_full" => _full.Peek(out _),
            "closed" => _closed.Peek(out _),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown reader kind"),
        };
    }

    [Fact]
    public void ReplaysTheCanonicalQueueCorpus()
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}; " +
            "clone lazily-spec as a sibling. A skip here would report green while testing nothing.");

        var steps = 0;
        var invalidationChecks = 0;

        foreach (var fixture in Fixtures)
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

            var ctx = new Context();
            var queue = new QueueCell<string>(ctx, capacity);
            var readers = new Readers(ctx, queue);

            var i = 0;
            foreach (var step in root.GetProperty("steps").EnumerateArray())
            {
                readers.Refresh();

                var op = step.GetProperty("op");
                var kind = op.GetProperty("type").GetString();
                string? returned = null;

                switch (kind)
                {
                    case "push":
                    case "try_push":
                        returned = queue.TryPush(op.GetProperty("value").GetString()!).ToString();
                        break;
                    case "pop":
                    case "try_pop":
                        {
                            var popped = queue.TryPop();
                            returned = popped.IsValue ? popped.Value : popped.Status.ToString();
                            break;
                        }
                    case "close":
                        queue.Close();
                        break;
                    case "batch":
                        // MPSC: several producers inside one batch, which is one invalidation
                        // frontier — the point of the fixture.
                        ctx.Batch(() =>
                        {
                            foreach (var sub in op.GetProperty("ops").EnumerateArray())
                            {
                                Assert.Equal("push", sub.GetProperty("type").GetString());
                                Assert.Equal(
                                    QueuePushResult.Ok,
                                    queue.TryPush(sub.GetProperty("value").GetString()!));
                            }
                        });
                        break;
                    default:
                        throw new InvalidOperationException($"{fixture} step {i}: unhandled op '{kind}'");
                }

                var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {i}");

                // Invalidation FIRST — reading a reader revalidates it.
                var stepIndex = i;
                // Descended (#lzsubblockkeyset): the child tracker's teardown proves the loop
                // reached every probe the matrix declares, which the raw enumeration could not.
                expected.TryAssertObjectKey(
                    "invalidates",
                    invalidates =>
                    {
                        foreach (var probe in invalidates.EnumerateObject())
                        {
                            var name = probe.Name;
                            invalidates.AssertKeyWith(
                                name,
                                want =>
                                {
                                    var wasInvalidated = !readers.StillValid(name);
                                    Assert.True(
                                        wasInvalidated == want.GetBoolean(),
                                        $"{fixture} step {stepIndex}: invalidates.{name} — expected " +
                                        $"{want.GetBoolean()}, got {wasInvalidated}. Reader kinds are " +
                                        "independent: a push onto a non-empty queue must not touch head.");
                                });
                            invalidationChecks++;
                        }
                    });

                if (step.TryGetProperty("returns", out var wantReturn) &&
                    wantReturn.ValueKind == JsonValueKind.String)
                {
                    Assert.Equal(wantReturn.GetString(), returned);
                }

                expected.TryAssertKeyWith("len", want => Assert.Equal(want.GetInt32(), queue.Len()));
                expected.TryAssertKeyWith("is_empty", want => Assert.Equal(want.GetBoolean(), queue.IsEmpty()));
                expected.TryAssertKeyWith("is_full", want => Assert.Equal(want.GetBoolean(), queue.IsFull()));
                expected.TryAssertKeyWith("closed", want => Assert.Equal(want.GetBoolean(), queue.IsClosed()));
                expected.TryAssertKeyWith(
                    "head",
                    want => Assert.Equal(
                        want.ValueKind == JsonValueKind.Null ? null : want.GetString(),
                        queue.Head()));

                // `elements` pins the whole FIFO body, which the scalar reads above cannot:
                // `len` plus `head` is satisfied by a queue that reordered its tail.
                expected.TryAssertKeyWith(
                    "elements",
                    want => Assert.Equal(
                        want.EnumerateArray().Select(value => value.GetString()),
                        queue.Elements()));

                expected.Verify();
                i++;
                steps++;
            }
        }

        // Positive proof, not an absence guard: a runner that silently replayed nothing would
        // otherwise pass.
        Assert.True(steps >= 25, $"replayed only {steps} steps across {Fixtures.Length} fixtures");
        Assert.True(invalidationChecks >= 85,
            $"only {invalidationChecks} invalidation assertions — the matrix is what makes this " +
            "corpus discriminating");
    }
}
