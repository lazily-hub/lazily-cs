using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>Replays the canonical broadcast-topic fixtures against the production TopicCell.</summary>
public sealed class TopicCellConformanceTests
{
    private static readonly string[] Fixtures =
    [
        "topiccell_broadcast_cursor_isolation.json",
        "topiccell_durable_replay_gc.json",
        "topiccell_ephemeral_lifecycle.json",
        "topiccell_offline_tail_bounds.json",
    ];

    [Fact]
    public void ReplaysCanonicalTopicCellCorpus()
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
            Assert.Equal("TopicCell", root.GetProperty("model").GetString());
            var ctx = new Context();
            var topic = new TopicCell<string>(ctx, ParseInitial(root.GetProperty("initial")));
            PrimeAll(topic);

            var index = 0;
            foreach (var step in root.GetProperty("steps").EnumerateArray())
            {
                var expected = step.GetProperty("expected");
                var invalidates = expected.GetProperty("invalidates");
                var before = invalidates.EnumerateObject().ToDictionary(
                    probe => probe.Name,
                    probe => topic.ReaderHandle(probe.Name),
                    StringComparer.Ordinal);
                foreach (var handle in before.Values)
                {
                    if (handle is not null) _ = handle.Get();
                }

                var op = step.GetProperty("op");
                object? returned = op.GetProperty("type").GetString() switch
                {
                    "publish" => topic.Publish(op.GetProperty("value").GetString()!),
                    "subscribe" => topic.Subscribe(
                        op.GetProperty("subscriber").GetString()!,
                        ParseDurability(op.GetProperty("durability").GetString()!)),
                    "reconnect" => topic.Reconnect(op.GetProperty("subscriber").GetString()!),
                    "restart" => Restart(ctx, ref topic),
                    "disconnect" => topic.Disconnect(op.GetProperty("subscriber").GetString()!),
                    "advance" => topic.Advance(op.GetProperty("subscriber").GetString()!),
                    "gc" => topic.CollectGarbage(),
                    var kind => throw new InvalidOperationException(
                        $"{fixture} step {index}: unhandled op '{kind}'"),
                };

                if (step.TryGetProperty("returns", out var expectedReturn))
                    AssertReturn(fixture, index, expectedReturn, returned);

                foreach (var probe in invalidates.EnumerateObject())
                {
                    var handle = before[probe.Name] ?? topic.ReaderHandle(probe.Name);
                    Assert.NotNull(handle);
                    var invalidated = !handle!.Peek(out _);
                    Assert.True(
                        invalidated == probe.Value.GetBoolean(),
                        $"{fixture} step {index}: invalidates.{probe.Name}");
                    invalidationChecks++;
                }

                AssertState(fixture, index, expected, topic);
                PrimeAll(topic);
                steps++;
                index++;
            }
        }

        Assert.Equal(4, Fixtures.Length);
        Assert.True(steps >= 29, $"expected at least 29 steps, got {steps}");
        Assert.True(
            invalidationChecks >= 35,
            $"expected at least 35 invalidation checks, got {invalidationChecks}");
    }

    private static object? Restart(Context ctx, ref TopicCell<string> topic)
    {
        topic = new TopicCell<string>(ctx, topic.Snapshot());
        return null;
    }

    private static TopicSnapshot<string> ParseInitial(JsonElement initial)
    {
        var subscriptions = initial.GetProperty("subscriptions")
            .EnumerateObject()
            .ToDictionary(
                pair => pair.Name,
                pair =>
                {
                    var value = pair.Value;
                    return new TopicSubscriptionSnapshot(
                        value.GetProperty("cursor").GetInt64(),
                        ParseDurability(value.GetProperty("durability").GetString()!),
                        value.GetProperty("connected").GetBoolean());
                },
                StringComparer.Ordinal);
        return new TopicSnapshot<string>(
            initial.GetProperty("base_offset").GetInt64(),
            initial.GetProperty("elements").EnumerateArray().Select(value => value.GetString()!),
            subscriptions);
    }

    private static TopicDurability ParseDurability(string value) => value switch
    {
        "durable" => TopicDurability.Durable,
        "ephemeral" => TopicDurability.Ephemeral,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown topic durability"),
    };

    private static void AssertReturn(
        string fixture,
        int index,
        JsonElement expected,
        object? actual)
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

        Assert.Equal(expected.GetString(), Assert.IsType<string>(actual));
    }

    private static void AssertState(
        string fixture,
        int index,
        JsonElement expected,
        TopicCell<string> topic)
    {
        Assert.Equal(expected.GetProperty("base_offset").GetInt64(), topic.BaseOffset);
        Assert.Equal(
            expected.GetProperty("elements").EnumerateArray().Select(value => value.GetString()),
            topic.Elements());

        var expectedSubscriptions = expected.GetProperty("subscriptions");
        Assert.Equal(
            expectedSubscriptions.EnumerateObject().Select(pair => pair.Name).Order(),
            topic.SubscriptionIds());
        foreach (var pair in expectedSubscriptions.EnumerateObject())
        {
            var actual = topic.SubscriptionState(pair.Name);
            Assert.NotNull(actual);
            Assert.Equal(pair.Value.GetProperty("cursor").GetInt64(), actual!.Cursor);
            Assert.Equal(
                ParseDurability(pair.Value.GetProperty("durability").GetString()!),
                actual.Durability);
            Assert.Equal(pair.Value.GetProperty("connected").GetBoolean(), actual.Connected);
        }

        foreach (var read in expected.GetProperty("reads").EnumerateObject())
        {
            Assert.Equal(
                read.Value.EnumerateArray().Select(value => value.GetString()),
                topic.ReadStream(read.Name));
        }

        Assert.True(topic.EndOffset == topic.BaseOffset + topic.Elements().Count);
    }

    private static void PrimeAll(TopicCell<string> topic)
    {
        foreach (var id in topic.SubscriptionIds())
        {
            var handle = topic.ReaderHandle(id);
            Assert.NotNull(handle);
            _ = handle!.Get();
        }
    }
}
