using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lazily.Tests;

/// <summary>Replays the canonical portable stdlib fixtures through production APIs.</summary>
public sealed class PortableStdlibConformanceTests
{
    private static readonly string[] Fixtures =
    [
        "timer.json",
        "timeout.json",
        "revision_barrier.json",
    ];

    [Fact]
    public void ReplaysCanonicalPortableStdlibCorpus()
    {
        foreach (var fixtureName in Fixtures)
        {
            using var document = SpecCorpus.Load("stdlib", fixtureName);
            var fixture = document.RootElement;
            AssertFixtureBookkeeping(fixture);
            foreach (var scenario in SpecCorpus.Scenarios(fixture, "stdlib", fixtureName).All())
            {
                switch (fixture.GetProperty("feature").GetString())
                {
                    case "stdlib_timer_v1":
                        ReplayTimer(scenario);
                        break;
                    case "stdlib_timeout_v1":
                        ReplayTimeout(scenario);
                        break;
                    case "stdlib_revision_barrier_v1":
                        ReplayBarrier(scenario);
                        break;
                    default:
                        throw new InvalidOperationException("unsupported stdlib feature");
                }
            }
        }
        Assert.Equal(3, Fixtures.Length);
    }

    [Fact]
    public void CheckedUnsignedDeadlineRejectsOverflow()
    {
        var failure = Assert.Throws<StdlibUnavailableException>(
            () => PortableClock.CheckedDeadline(ulong.MaxValue - 1, 2));
        Assert.Equal(StdlibUnavailableReason.DeadlineOverflow, failure.Reason);
    }

    [Fact]
    public async Task TaskAdaptersShareTheCallerDrivenCore()
    {
        var timer = new Lazily.Timer(0, 2);
        Assert.Equal(TimerOutcome.Pending, (await timer.ObserveAsync(1)).Outcome);
        Assert.Equal(TimerOutcome.Fired, (await timer.ObserveAsync(2)).Outcome);

        var operationCalls = 0;
        var cancellationCalls = 0;
        var timeout = new Timeout<string>(0, 10);
        var completed = await timeout.PollAsync(
            3,
            () =>
            {
                operationCalls++;
                return Task.FromResult(TimeoutOperation<string>.Completed("future"));
            },
            () =>
            {
                cancellationCalls++;
                return Task.FromResult(TimeoutCancellation.Cancelled);
            });
        Assert.Equal(TimeoutOutcome.Completed, completed.Outcome);
        Assert.Equal("future", completed.Value);
        Assert.Equal(1, operationCalls);
        Assert.Equal(1, cancellationCalls);

        _ = await timeout.PollAsync(
            9,
            () =>
            {
                operationCalls++;
                return Task.FromResult(TimeoutOperation<string>.Unavailable());
            },
            () =>
            {
                cancellationCalls++;
                return Task.FromResult(TimeoutCancellation.Cancelled);
            });
        Assert.Equal(1, operationCalls);
        Assert.Equal(1, cancellationCalls);

        var barrier = new RevisionBarrier(0, 1, null);
        Assert.Equal(
            RevisionBarrierOutcome.Satisfied,
            barrier.Advance(1, predicate: true).Outcome);
        var barrierCancellationCalls = 0;
        _ = await barrier.ObserveAsync(
            7,
            predicate: false,
            () =>
            {
                barrierCancellationCalls++;
                return Task.FromResult(TimeoutCancellation.Cancelled);
            });
        Assert.Equal(0, barrierCancellationCalls);
    }

    [Fact]
    public void RevisionBarrierRejectsClockRegressionBeforeMutationOrCancellation()
    {
        var observed = new RevisionBarrier(0, 1, null);
        var cancellationCalls = 0;
        Assert.Equal(
            RevisionBarrierOutcome.Pending,
            observed.Observe(
                10,
                predicate: false,
                () =>
                {
                    cancellationCalls++;
                    return TimeoutCancellation.Pending;
                }).Outcome);
        cancellationCalls = 0;
        var regression = observed.Observe(
            9,
            predicate: true,
            () =>
            {
                cancellationCalls++;
                return TimeoutCancellation.Cancelled;
            });
        Assert.Equal(RevisionBarrierOutcome.Unavailable, regression.Outcome);
        Assert.Equal(StdlibUnavailableReason.ClockRegression, regression.Reason);
        Assert.Equal(0UL, regression.Revision);
        Assert.Equal(0UL, regression.Generation);
        Assert.Equal(0, cancellationCalls);

        var registered = new RevisionBarrier(0, 1, null);
        _ = registered.RegisterRecheck(10, observedRevision: 0, predicate: false);
        var registerRegression =
            registered.RegisterRecheck(9, observedRevision: 7, predicate: true);
        Assert.Equal(RevisionBarrierOutcome.Unavailable, registerRegression.Outcome);
        Assert.Equal(StdlibUnavailableReason.ClockRegression, registerRegression.Reason);
        Assert.Equal(0UL, registerRegression.Revision);
        Assert.Equal(0UL, registerRegression.Generation);
    }

    [Fact]
    public async Task RevisionBarrierPreservesFirstTerminalAcrossReentrantAndTaskCancellation()
    {
        var reentrant = new RevisionBarrier(0, 1, null);
        var reentrantResult = reentrant.Observe(
            0,
            predicate: false,
            () =>
            {
                _ = reentrant.Dispose();
                return TimeoutCancellation.Cancelled;
            });
        Assert.Equal(RevisionBarrierOutcome.Disposed, reentrantResult.Outcome);

        var asynchronous = new RevisionBarrier(0, 1, null);
        var cancellation = new TaskCompletionSource<TimeoutCancellation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = asynchronous.ObserveAsync(
            0,
            predicate: false,
            () => cancellation.Task);
        _ = asynchronous.Dispose();
        cancellation.SetResult(TimeoutCancellation.Cancelled);
        Assert.Equal(RevisionBarrierOutcome.Disposed, (await observation).Outcome);
    }

    private static void AssertFixtureBookkeeping(JsonElement fixture)
    {
        var scenarios = fixture.GetProperty("scenarios").EnumerateArray().ToArray();
        var scenarioIds = scenarios
            .Select(scenario => scenario.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);
        var assertions = scenarios.Sum(
            scenario => scenario.GetProperty("steps").EnumerateArray().Sum(
                step => step.GetProperty("expect").EnumerateObject().Count()));
        var mutations = fixture.GetProperty("mutations").EnumerateArray().ToArray();

        Assert.True(scenarios.Length >= fixture.GetProperty("scenario_floor").GetInt32());
        Assert.True(assertions >= fixture.GetProperty("assertion_floor").GetInt32());
        Assert.True(mutations.Length >= fixture.GetProperty("mutation_floor").GetInt32());
        foreach (var mutation in mutations)
        {
            var kills = mutation.GetProperty("must_fail").EnumerateArray().ToArray();
            Assert.NotEmpty(kills);
            foreach (var kill in kills)
            {
                Assert.Contains(kill.GetString(), scenarioIds);
            }
        }
    }

    private static void ReplayTimer(JsonElement scenario)
    {
        Lazily.Timer? timer = null;
        var index = 0;
        foreach (var step in scenario.GetProperty("steps").EnumerateArray())
        {
            JsonObject actual;
            switch (step.GetProperty("op").GetString())
            {
                case "start":
                    try
                    {
                        timer = new Lazily.Timer(
                            step.GetProperty("now").GetUInt64(),
                            step.GetProperty("duration").GetUInt64());
                        actual = new()
                        {
                            ["outcome"] = "pending",
                            ["deadline"] = timer.Deadline,
                        };
                    }
                    catch (StdlibUnavailableException failure)
                    {
                        timer = null;
                        actual = new()
                        {
                            ["outcome"] = "unavailable",
                            ["reason"] = failure.Reason.Name(),
                        };
                    }
                    break;
                case "observe":
                    actual = TimerJson(
                        (timer ?? throw new InvalidOperationException("observe before start"))
                        .Observe(step.GetProperty("now").GetUInt64()));
                    break;
                default:
                    throw new InvalidOperationException("unsupported timer op");
            }
            AssertStep(scenario, index++, step, actual);
        }
    }

    private static void ReplayTimeout(JsonElement scenario)
    {
        Timeout<string>? timeout = null;
        var index = 0;
        foreach (var step in scenario.GetProperty("steps").EnumerateArray())
        {
            JsonObject actual;
            switch (step.GetProperty("op").GetString())
            {
                case "start":
                    try
                    {
                        timeout = new Timeout<string>(
                            step.GetProperty("now").GetUInt64(),
                            step.GetProperty("duration").GetUInt64());
                        actual = new()
                        {
                            ["outcome"] = "pending",
                            ["deadline"] = timeout.Deadline,
                        };
                    }
                    catch (StdlibUnavailableException failure)
                    {
                        timeout = null;
                        actual = new()
                        {
                            ["outcome"] = "unavailable",
                            ["reason"] = failure.Reason.Name(),
                        };
                    }
                    break;
                case "poll":
                    var operationCalls = 0;
                    var cancellationCalls = 0;
                    var observation = (timeout
                        ?? throw new InvalidOperationException("poll before start")).Poll(
                        step.GetProperty("now").GetUInt64(),
                        () =>
                        {
                            operationCalls++;
                            return step.GetProperty("operation").GetString() switch
                            {
                                "pending" => TimeoutOperation<string>.Pending(),
                                "completed" => TimeoutOperation<string>.Completed(
                                    step.GetProperty("value").GetString()
                                    ?? throw new JsonException("value must be a string")),
                                "unavailable" => TimeoutOperation<string>.Unavailable(),
                                _ => throw new JsonException("unsupported operation"),
                            };
                        },
                        () =>
                        {
                            cancellationCalls++;
                            return Cancellation(step);
                        });
                    actual = TimeoutJson(observation, operationCalls, cancellationCalls);
                    break;
                default:
                    throw new InvalidOperationException("unsupported timeout op");
            }
            AssertStep(scenario, index++, step, actual);
        }
    }

    private static void ReplayBarrier(JsonElement scenario)
    {
        RevisionBarrier? barrier = null;
        var index = 0;
        foreach (var step in scenario.GetProperty("steps").EnumerateArray())
        {
            var cancellationCalls = 0;
            var operation = step.GetProperty("op").GetString();
            var observation = operation switch
            {
                "start" => (barrier = new RevisionBarrier(
                    step.GetProperty("revision").GetUInt64(),
                    step.GetProperty("required_revision").GetUInt64(),
                    step.GetProperty("deadline").ValueKind == JsonValueKind.Null
                        ? null
                        : step.GetProperty("deadline").GetUInt64())).Receipt(string.Empty),
                "observe" => (barrier
                    ?? throw new InvalidOperationException("observe before start")).Observe(
                    step.GetProperty("now").GetUInt64(),
                    step.GetProperty("predicate").GetBoolean(),
                    () =>
                    {
                        cancellationCalls++;
                        return Cancellation(step);
                    }),
                "register_recheck" => (barrier
                    ?? throw new InvalidOperationException("register before start")).RegisterRecheck(
                    step.GetProperty("now").GetUInt64(),
                    step.GetProperty("observed_revision").GetUInt64(),
                    step.GetProperty("predicate").GetBoolean()),
                "advance" => (barrier
                    ?? throw new InvalidOperationException("advance before start")).Advance(
                    step.GetProperty("revision").GetUInt64(),
                    step.GetProperty("predicate").GetBoolean()),
                "dispose" => (barrier
                    ?? throw new InvalidOperationException("dispose before start")).Dispose(),
                "receipt" => (barrier
                    ?? throw new InvalidOperationException("receipt before start")).Receipt(
                    step.GetProperty("key").GetString() ?? string.Empty),
                _ => throw new InvalidOperationException("unsupported barrier op"),
            };
            var actual = BarrierJson(
                observation,
                operation == "observe" ? cancellationCalls : null);
            AssertStep(scenario, index++, step, actual);
        }
    }

    private static TimeoutCancellation Cancellation(JsonElement step) =>
        step.GetProperty("cancellation").GetString() switch
        {
            "pending" => TimeoutCancellation.Pending,
            "cancelled" => TimeoutCancellation.Cancelled,
            "unavailable" => TimeoutCancellation.Unavailable,
            _ => throw new JsonException("unsupported cancellation"),
        };

    private static JsonObject TimerJson(TimerObservation observation)
    {
        var result = new JsonObject { ["outcome"] = observation.Outcome.Name() };
        if (observation.Deadline is { } deadline) result["deadline"] = deadline;
        if (observation.FiredAt is { } firedAt) result["fired_at"] = firedAt;
        if (observation.Reason is { } reason) result["reason"] = reason.Name();
        return result;
    }

    private static JsonObject TimeoutJson(
        TimeoutObservation<string> observation,
        int operationCalls,
        int cancellationCalls)
    {
        var result = new JsonObject
        {
            ["outcome"] = observation.Outcome.Name(),
            ["operation_calls"] = operationCalls,
            ["cancellation_calls"] = cancellationCalls,
        };
        if (observation.Deadline is { } deadline) result["deadline"] = deadline;
        if (observation.Outcome == TimeoutOutcome.Completed) result["value"] = observation.Value;
        if (observation.Reason is { } reason) result["reason"] = reason.Name();
        return result;
    }

    private static JsonObject BarrierJson(
        RevisionBarrierObservation observation,
        int? cancellationCalls)
    {
        var result = new JsonObject
        {
            ["outcome"] = observation.Outcome.Name(),
            ["revision"] = observation.Revision,
            ["generation"] = observation.Generation,
        };
        if (observation.Reason is { } reason) result["reason"] = reason.Name();
        if (cancellationCalls is { } calls) result["cancellation_calls"] = calls;
        return result;
    }

    // Each step's `expect` is compared STRUCTURALLY, which covers its key set in both
    // directions — but until #lzunboundblockguard it was compared by a bare
    // `JsonNode.DeepEquals` that bound no tracker, so all 54 of these blocks were
    // "carried by an opened fixture and bound by nothing" as far as rung 0 could see.
    // `FixtureAssertions.Deep` performs the same comparison through the ledger.
    private static void AssertStep(
        JsonElement scenario,
        int index,
        JsonElement step,
        JsonObject actual) =>
        FixtureAssertions.Deep(
            step.GetProperty("expect"),
            $"stdlib/{scenario.GetProperty("id").GetString()} step {index}",
            actual);
}
