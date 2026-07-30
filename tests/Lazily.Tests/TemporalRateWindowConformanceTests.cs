using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>Replays the canonical temporal, rate-shaping, and windowing corpora.</summary>
public sealed class TemporalRateWindowConformanceTests
{
    private static readonly string[] TemporalFixtures =
    [
        "cron_pattern.json",
        "deadline_expiry.json",
        "interval_periodic.json",
        "timer_single_shot.json",
    ];

    private static readonly string[] RateShapeFixtures =
    [
        "debounce.json",
        "probabilistic_sample.json",
        "sample_count.json",
        "sample_time.json",
        "throttle_leading.json",
        "throttle_trailing.json",
    ];

    private static readonly string[] WindowingFixtures =
    [
        "session.json",
        "sliding_count.json",
        "tumbling_count.json",
        "tumbling_time.json",
    ];

    [Fact]
    public void ReplaysCanonicalTemporalCorpus()
    {
        AssertCorpusPresent("temporal", TemporalFixtures);
        var steps = 0;
        foreach (var fixture in TemporalFixtures)
        {
            using var document = SpecCorpus.Load("temporal", fixture);
            steps += ReplayTemporal(fixture, document.RootElement);
        }

        Assert.Equal(4, TemporalFixtures.Length);
        Assert.True(steps >= 16, $"expected at least 16 temporal steps, got {steps}");
    }

    [Fact]
    public void ReplaysCanonicalRateShapeCorpus()
    {
        AssertCorpusPresent("rateshape", RateShapeFixtures);
        var steps = 0;
        foreach (var fixture in RateShapeFixtures)
        {
            using var document = SpecCorpus.Load("rateshape", fixture);
            steps += ReplayRateShape(fixture, document.RootElement);
        }

        Assert.Equal(6, RateShapeFixtures.Length);
        Assert.True(steps >= 31, $"expected at least 31 rate-shape steps, got {steps}");
    }

    [Fact]
    public void ReplaysCanonicalWindowingCorpus()
    {
        AssertCorpusPresent("windowing", WindowingFixtures);
        var steps = 0;
        foreach (var fixture in WindowingFixtures)
        {
            using var document = SpecCorpus.Load("windowing", fixture);
            steps += ReplayWindow(fixture, document.RootElement);
        }

        Assert.Equal(4, WindowingFixtures.Length);
        Assert.True(steps >= 22, $"expected at least 22 windowing steps, got {steps}");
    }

    [Fact]
    public void LogicalClockAndIntervalRemainMonotoneAcrossLargeJumps()
    {
        var clock = new ManualLogicalClock();
        Assert.Equal(10, clock.Advance(10));
        Assert.Equal(10, clock.Advance(3));

        var interval = new IntervalCell(new Context(), 7);
        Assert.True(interval.Tick(clock.Advance(1_000_000)));
        Assert.Equal(142_857, interval.Count);
        Assert.Equal(1_000_006, interval.NextFire);
    }

    [Fact]
    public void WindowAggregationRunsThroughTheProductionMergePolicy()
    {
        var folds = 0;
        var policy = new MergePolicy<int>(
            "CountedSum",
            (left, right) =>
            {
                folds++;
                return left + right;
            },
            Commutative: true,
            Idempotent: false,
            Conflates: true);
        var window = new SlidingWindow<int>(new Context(), 3, 1, policy);

        Assert.Equal(1, window.Push(1).Value);
        Assert.Equal(3, window.Push(2).Value);
        Assert.Equal(6, window.Push(3).Value);
        Assert.Equal(9, window.Push(4).Value);
        Assert.Equal(5, folds);
    }

    private static int ReplayTemporal(string fixture, JsonElement root)
    {
        Assert.Equal("Temporal", root.GetProperty("kind").GetString());
        var context = new Context();
        var initial = root.GetProperty("initial");
        Func<long, bool> tick;
        Func<IComputeOps, long> reactiveRead;
        Action<FixtureAssertions> assertState;
        Func<long?> nextFire;
        string invalidationName;

        switch (root.GetProperty("model").GetString())
        {
            case "TimerCell":
                {
                    var cell = new TimerCell(context, initial.GetProperty("fire_at").GetInt64());
                    tick = cell.Tick;
                    reactiveRead = ops => cell.FiredCell.Get(ops) ? 1 : 0;
                    nextFire = () => cell.NextFire;
                    invalidationName = "fired";
                    assertState = expected =>
                    {
                        Assert.Equal(expected.GetProperty("fired").GetBoolean(), cell.HasFired);
                        var value = expected.GetProperty("value");
                        if (value.ValueKind == JsonValueKind.Null)
                        {
                            Assert.False(cell.Value.HasValue);
                        }
                        else
                        {
                            Assert.True(cell.Value.HasValue);
                            Assert.Equal("()", value.GetString());
                        }
                    };
                    break;
                }
            case "IntervalCell":
                {
                    var cell = new IntervalCell(context, initial.GetProperty("period").GetInt64());
                    tick = cell.Tick;
                    reactiveRead = ops => cell.CountCell.Get(ops);
                    nextFire = () => cell.NextFire;
                    invalidationName = "count";
                    assertState = expected =>
                        Assert.Equal(expected.GetProperty("count").GetInt64(), cell.Count);
                    break;
                }
            case "CronCell":
                {
                    var cell = new CronCell(
                        context,
                        initial.GetProperty("cycle").GetInt64(),
                        initial.GetProperty("offsets").EnumerateArray().Select(value => value.GetInt64()));
                    tick = cell.Tick;
                    reactiveRead = ops => cell.CountCell.Get(ops);
                    nextFire = () => cell.NextFire;
                    invalidationName = "count";
                    assertState = expected =>
                        Assert.Equal(expected.GetProperty("count").GetInt64(), cell.Count);
                    break;
                }
            case "DeadlineCell":
                {
                    var cell = new DeadlineCell<string>(
                        context,
                        initial.GetProperty("value").GetString()!,
                        initial.GetProperty("deadline").GetInt64());
                    tick = cell.Tick;
                    reactiveRead = ops => cell.ExpiredCell.Get(ops) ? 1 : 0;
                    nextFire = () => cell.NextFire;
                    invalidationName = "state";
                    assertState = expected =>
                    {
                        Assert.Equal(
                            expected.GetProperty("state").GetString() == "Expired"
                                ? DeadlinePhase.Expired
                                : DeadlinePhase.Live,
                            cell.State.Phase);
                        Assert.Equal(expected.GetProperty("value").GetString(), cell.State.Value);
                    };
                    break;
                }
            default:
                throw new InvalidOperationException($"{fixture}: unknown temporal model");
        }

        var probe = context.Computed(reactiveRead);
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            Assert.Equal("tick", operation.GetProperty("type").GetString());
            var returned = tick(operation.GetProperty("now").GetInt64());
            Assert.Equal(step.GetProperty("returns").GetBoolean(), returned);

            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(fixture, index, expected, invalidationName, probe);
            assertState(expected);
            if (expected.TryGetProperty("next_fire", out var expectedNext))
            {
                Assert.Equal(
                    expectedNext.ValueKind == JsonValueKind.Null ? null : expectedNext.GetInt64(),
                    nextFire());
            }
            expected.Verify();
            _ = probe.Get();
            index++;
        }
        return index;
    }

    private static int ReplayRateShape(string fixture, JsonElement root)
    {
        Assert.Equal("RateShape", root.GetProperty("kind").GetString());
        var context = new Context();
        var initial = root.GetProperty("initial");
        Func<JsonElement, Optional<string>> apply;
        Source<Optional<string>> outputCell;

        switch (root.GetProperty("model").GetString())
        {
            case "DebounceCell":
                {
                    var cell = new DebounceCell<string>(context, initial.GetProperty("quiet").GetInt64());
                    outputCell = cell.OutputCell;
                    apply = operation =>
                    {
                        if (operation.GetProperty("type").GetString() == "input")
                        {
                            cell.Input(
                                operation.GetProperty("now").GetInt64(),
                                operation.GetProperty("value").GetString()!);
                            return Optional<string>.None;
                        }
                        return cell.Tick(operation.GetProperty("now").GetInt64());
                    };
                    break;
                }
            case "ThrottleCell":
                {
                    var edge = initial.GetProperty("edge").GetString() == "Leading"
                        ? ThrottleEdge.Leading
                        : ThrottleEdge.Trailing;
                    var cell = new ThrottleCell<string>(
                        context,
                        edge,
                        initial.GetProperty("window").GetInt64());
                    outputCell = cell.OutputCell;
                    apply = operation => operation.GetProperty("type").GetString() == "input"
                        ? cell.Input(
                            operation.GetProperty("now").GetInt64(),
                            operation.GetProperty("value").GetString()!)
                        : cell.Tick(operation.GetProperty("now").GetInt64());
                    break;
                }
            case "SampleCell":
                {
                    var mode = initial.GetProperty("mode").GetString() == "Count"
                        ? SampleMode.Count(initial.GetProperty("n").GetInt64())
                        : SampleMode.Time(initial.GetProperty("period").GetInt64());
                    var cell = new SampleCell<string>(context, mode);
                    outputCell = cell.OutputCell;
                    apply = operation => operation.GetProperty("type").GetString() == "input"
                        ? cell.Input(operation.GetProperty("value").GetString()!)
                        : cell.Tick(operation.GetProperty("now").GetInt64());
                    break;
                }
            case "ProbabilisticSampleCell":
                {
                    var cell = new ProbabilisticSampleCell<string>(
                        context,
                        initial.GetProperty("rate").GetDouble(),
                        new SplitMix64Random(42));
                    outputCell = cell.OutputCell;
                    apply = operation => cell.InputWithDraw(
                        operation.GetProperty("value").GetString()!,
                        operation.GetProperty("draw").GetDouble());
                    break;
                }
            default:
                throw new InvalidOperationException($"{fixture}: unknown rate-shape model");
        }

        var probe = context.Computed(ops => outputCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var returned = apply(step.GetProperty("op"));
            AssertOptionalString(step.GetProperty("returns"), returned);
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(fixture, index, expected, "output", probe);
            AssertOptionalString(expected.GetProperty("output"), outputCell.Get());
            expected.Verify();
            _ = probe.Get();
            index++;
        }
        return index;
    }

    private static int ReplayWindow(string fixture, JsonElement root)
    {
        Assert.Equal("Windowing", root.GetProperty("kind").GetString());
        var context = new Context();
        var config = root.GetProperty("config");
        Assert.Equal("Sum", config.GetProperty("aggregate").GetString());
        var sum = MergePolicy.Sum<int>();
        Func<JsonElement, Optional<int>> apply;
        Source<Optional<int>> outputCell;

        switch (root.GetProperty("model").GetString())
        {
            case "TumblingWindow" when config.GetProperty("mode").GetString() == "count":
                {
                    var window = new TumblingCountWindow<int>(
                        context,
                        config.GetProperty("n").GetInt64(),
                        sum);
                    outputCell = window.OutputCell;
                    apply = operation => window.Push(operation.GetProperty("value").GetInt32());
                    break;
                }
            case "TumblingWindow":
                {
                    var window = new TumblingTimeWindow<int>(
                        context,
                        config.GetProperty("period").GetInt64(),
                        sum);
                    outputCell = window.OutputCell;
                    apply = operation =>
                    {
                        if (operation.GetProperty("type").GetString() == "push")
                        {
                            window.Push(
                                operation.GetProperty("now").GetInt64(),
                                operation.GetProperty("value").GetInt32());
                            return Optional<int>.None;
                        }
                        return window.Tick(operation.GetProperty("now").GetInt64());
                    };
                    break;
                }
            case "SlidingWindow":
                {
                    var window = new SlidingWindow<int>(
                        context,
                        config.GetProperty("size").GetInt32(),
                        config.GetProperty("slide").GetInt64(),
                        sum);
                    outputCell = window.OutputCell;
                    apply = operation => window.Push(operation.GetProperty("value").GetInt32());
                    break;
                }
            case "SessionWindow":
                {
                    var window = new SessionWindow<int>(
                        context,
                        config.GetProperty("gap").GetInt64(),
                        sum);
                    outputCell = window.OutputCell;
                    apply = operation => operation.GetProperty("type").GetString() == "push"
                        ? window.Push(
                            operation.GetProperty("now").GetInt64(),
                            operation.GetProperty("value").GetInt32())
                        : window.Flush(operation.GetProperty("now").GetInt64());
                    break;
                }
            default:
                throw new InvalidOperationException($"{fixture}: unknown windowing model");
        }

        var probe = context.Computed(ops => outputCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var returned = apply(step.GetProperty("op"));
            AssertOptionalInt(step.GetProperty("returns"), returned);
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(fixture, index, expected, "output", probe);
            AssertOptionalInt(expected.GetProperty("output"), outputCell.Get());
            expected.Verify();
            _ = probe.Get();
            index++;
        }
        return index;
    }

    private static void AssertCorpusPresent(string corpus, IReadOnlyList<string> expected)
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}");
        Assert.Equal(expected, SpecCorpus.FixtureNames(corpus));
    }

    private static void AssertInvalidation<T>(
        string fixture,
        int index,
        FixtureAssertions expected,
        string name,
        Computed<T> probe)
    {
        var actual = !probe.Peek(out _);
        var wanted = expected.GetProperty("invalidates").GetProperty(name).GetBoolean();
        Assert.True(actual == wanted, $"{fixture} step {index}: invalidates.{name}");
    }

    private static void AssertOptionalString(JsonElement expected, Optional<string> actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.False(actual.HasValue);
            return;
        }
        Assert.True(actual.HasValue);
        Assert.Equal(expected.GetString(), actual.Value);
    }

    private static void AssertOptionalInt(JsonElement expected, Optional<int> actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.False(actual.HasValue);
            return;
        }
        Assert.True(actual.HasValue);
        Assert.Equal(expected.GetInt32(), actual.Value);
    }
}
