using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>Replays the canonical competing-consumer work-queue fixtures.</summary>
public sealed class WorkQueueConformanceTests
{
    private static readonly string[] Fixtures =
    [
        "workqueue_competing_delivery.json",
        "workqueue_lease_deadletter.json",
    ];

    private sealed class Readers
    {
        private readonly Computed<int> _pending;
        private readonly Computed<bool> _empty;
        private readonly Computed<int> _inFlight;
        private readonly Computed<int> _deadLetters;

        internal Readers(Context ctx, WorkQueueCell<string> queue)
        {
            _pending = ctx.Computed(cx => queue.PendingLen(cx));
            _empty = ctx.Computed(cx => queue.IsEmpty(cx));
            _inFlight = ctx.Computed(cx => queue.InFlightLen(cx));
            _deadLetters = ctx.Computed(cx => queue.DeadLetterLen(cx));
        }

        internal void Refresh()
        {
            _ = _pending.Get();
            _ = _empty.Get();
            _ = _inFlight.Get();
            _ = _deadLetters.Get();
        }

        internal bool StillValid(string kind) => kind switch
        {
            "pending_len" => _pending.Peek(out _),
            "is_empty" => _empty.Peek(out _),
            "in_flight_len" => _inFlight.Peek(out _),
            "dead_letter_len" => _deadLetters.Peek(out _),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown reader kind"),
        };
    }

    [Fact]
    public void ReplaysCanonicalWorkQueueCorpus()
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}");

        var steps = 0;
        var invalidationChecks = 0;
        foreach (var fixture in Fixtures)
        {
            using var doc = SpecCorpus.Load("collections", fixture);
            var root = doc.RootElement;
            Assert.Equal("WorkQueueCell", root.GetProperty("model").GetString());
            var ctx = new Context();
            var queue = new WorkQueueCell<string>(ctx, visibilityTimeout: 10, maxDeliveries: 2);
            var readers = new Readers(ctx, queue);

            var index = 0;
            foreach (var step in root.GetProperty("steps").EnumerateArray())
            {
                readers.Refresh();
                var op = step.GetProperty("op");
                object? returned = op.GetProperty("type").GetString() switch
                {
                    "push" => queue.Push(op.GetProperty("value").GetString()!),
                    "claim" => queue.Claim(
                        op.GetProperty("worker").GetString()!,
                        op.GetProperty("now").GetInt64()),
                    "ack" => queue.Ack(
                        op.GetProperty("worker").GetString()!,
                        op.GetProperty("delivery_id").GetInt64()),
                    "nack" => queue.Nack(
                        op.GetProperty("worker").GetString()!,
                        op.GetProperty("delivery_id").GetInt64()),
                    "reap_expired" => queue.ReapExpired(op.GetProperty("now").GetInt64()),
                    var kind => throw new InvalidOperationException(
                        $"{fixture} step {index}: unhandled op '{kind}'"),
                };

                AssertReturn(fixture, index, step.GetProperty("returns"), returned);
                var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
                foreach (var probe in expected.GetProperty("invalidates").EnumerateObject())
                {
                    var invalidated = !readers.StillValid(probe.Name);
                    Assert.True(
                        invalidated == probe.Value.GetBoolean(),
                        $"{fixture} step {index}: invalidates.{probe.Name}");
                    invalidationChecks++;
                }

                Assert.Equal(
                    expected.GetProperty("reads").GetProperty("pending_len").GetInt32(),
                    queue.PendingLen());
                Assert.Equal(
                    expected.GetProperty("reads").GetProperty("is_empty").GetBoolean(),
                    queue.IsEmpty());
                Assert.Equal(
                    expected.GetProperty("reads").GetProperty("in_flight_len").GetInt32(),
                    queue.InFlightLen());
                Assert.Equal(
                    expected.GetProperty("reads").GetProperty("dead_letter_len").GetInt32(),
                    queue.DeadLetterLen());
                AssertSnapshots(expected, queue);
                expected.Verify();
                steps++;
                index++;
            }
        }

        Assert.Equal(2, Fixtures.Length);
        Assert.True(steps >= 18, $"expected at least 18 steps, got {steps}");
        Assert.True(
            invalidationChecks >= 72,
            $"expected at least 72 invalidation checks, got {invalidationChecks}");
    }

    private static void AssertReturn(string fixture, int index, JsonElement expected, object? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(actual);
            return;
        }
        if (expected.ValueKind == JsonValueKind.Number)
        {
            Assert.Equal(expected.GetInt64(), Convert.ToInt64(actual));
            return;
        }
        if (expected.ValueKind == JsonValueKind.True || expected.ValueKind == JsonValueKind.False)
        {
            Assert.Equal(expected.GetBoolean(), Assert.IsType<bool>(actual));
            return;
        }

        var delivery = Assert.IsType<WorkQueueDelivery<string>>(actual);
        AssertDelivery(expected, delivery, $"{fixture} step {index} return");
    }

    private static void AssertSnapshots(FixtureAssertions expected, WorkQueueCell<string> queue)
    {
        var pending = expected.GetProperty("pending").EnumerateArray().ToArray();
        Assert.Equal(pending.Length, queue.Pending().Count);
        for (var i = 0; i < pending.Length; i++)
        {
            Assert.Equal(pending[i].GetProperty("item_id").GetInt64(), queue.Pending()[i].ItemId);
            Assert.Equal(pending[i].GetProperty("value").GetString(), queue.Pending()[i].Value);
            Assert.Equal(pending[i].GetProperty("attempts").GetInt32(), queue.Pending()[i].Attempts);
        }

        var inFlight = expected.GetProperty("in_flight").EnumerateArray().ToArray();
        Assert.Equal(inFlight.Length, queue.InFlight().Count);
        for (var i = 0; i < inFlight.Length; i++)
            AssertDelivery(inFlight[i], queue.InFlight()[i], $"in_flight[{i}]");

        var deadLetters = expected.GetProperty("dead_letters").EnumerateArray().ToArray();
        Assert.Equal(deadLetters.Length, queue.DeadLetters().Count);
        for (var i = 0; i < deadLetters.Length; i++)
        {
            var actual = queue.DeadLetters()[i];
            Assert.Equal(deadLetters[i].GetProperty("item_id").GetInt64(), actual.ItemId);
            Assert.Equal(deadLetters[i].GetProperty("value").GetString(), actual.Value);
            Assert.Equal(deadLetters[i].GetProperty("attempts").GetInt32(), actual.Attempts);
            Assert.Equal(
                deadLetters[i].GetProperty("reason").GetString(),
                actual.Reason.ToString().ToLowerInvariant());
        }
    }

    private static void AssertDelivery(
        JsonElement expected,
        WorkQueueDelivery<string> actual,
        string label)
    {
        Assert.True(
            expected.GetProperty("delivery_id").GetInt64() == actual.DeliveryId &&
            expected.GetProperty("item_id").GetInt64() == actual.ItemId &&
            expected.GetProperty("value").GetString() == actual.Value &&
            expected.GetProperty("worker").GetString() == actual.Worker &&
            expected.GetProperty("attempt").GetInt32() == actual.Attempt &&
            expected.GetProperty("deadline").GetInt64() == actual.Deadline,
            label);
    }
}
