using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>Replays membership, coordination, presence, resilience, and service corpora.</summary>
public sealed class ClusterConformanceTests
{
    private static readonly string[] MembershipFixtures = ["membership_lifecycle.json"];

    private static readonly string[] CoordinationFixtures =
    [
        "leader.json",
        "lease.json",
        "lock.json",
        "quorum.json",
        "semaphore.json",
    ];

    private static readonly string[] PresenceFixtures =
    [
        "awareness.json",
        "ephemeral.json",
        "presence.json",
    ];

    private static readonly string[] ResilienceFixtures =
    [
        "bulkhead.json",
        "circuit_breaker.json",
        "retry.json",
        "timeout.json",
    ];

    private static readonly string[] ServiceFixtures =
    [
        "discovery.json",
        "health.json",
        "readiness.json",
        "service_registry.json",
    ];

    [Fact]
    public void ReplaysCanonicalMembershipCorpus()
    {
        AssertCorpusPresent("membership", MembershipFixtures);
        using var document = SpecCorpus.Load("membership", MembershipFixtures[0]);
        var root = document.RootElement;
        Assert.Equal("Membership", root.GetProperty("kind").GetString());
        var config = root.GetProperty("config");
        var context = new Context();
        var cell = new MembershipCell(
            context,
            new MembershipConfig(
                config.GetProperty("phi_threshold").GetDouble(),
                config.GetProperty("suspect_timeout").GetInt64(),
                config.GetProperty("max_samples").GetInt32(),
                config.GetProperty("min_std").GetDouble()));
        var probe = context.Computed(ops => cell.PeerSetCell.Get(ops).Count);
        _ = probe.Get();

        var steps = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            var now = operation.GetProperty("now").GetInt64();
            var type = operation.GetProperty("type").GetString();
            if (type == "tick")
            {
                cell.Tick(now);
            }
            else
            {
                var peer = operation.GetProperty("peer").GetInt64();
                _ = type switch
                {
                    "join" => cell.Join(peer, now),
                    "heartbeat" => cell.Heartbeat(peer, now),
                    "leave" => cell.Leave(peer, now),
                    _ => throw new InvalidOperationException($"unknown membership operation {type}"),
                };
            }

            var expected = FixtureAssertions.Of(
                step,
                "expected",
                $"membership/{MembershipFixtures[0]} step {steps}");
            AssertInvalidation(expected, "invalidates", probe);
            Assert.Equal(
                expected.GetProperty("alive_set").EnumerateArray().Select(value => value.GetInt64()),
                cell.PeerSet);
            foreach (var state in expected.GetProperty("states").EnumerateObject())
            {
                Assert.Equal(
                    Enum.Parse<PeerState>(state.Value.GetString()!),
                    cell.State(long.Parse(state.Name, System.Globalization.CultureInfo.InvariantCulture)));
            }
            _ = probe.Get();
            expected.Verify();
            steps++;
        }
        Assert.Equal(9, steps);
    }

    [Fact]
    public void ReplaysCanonicalCoordinationCorpus()
    {
        AssertCorpusPresent("coordination", CoordinationFixtures);
        var steps = 0;
        foreach (var fixture in CoordinationFixtures)
        {
            using var document = SpecCorpus.Load("coordination", fixture);
            var root = document.RootElement;
            Assert.Equal("Coordination", root.GetProperty("kind").GetString());
            steps += root.GetProperty("model").GetString() switch
            {
                "LeaseCell" => ReplayLease(root, fixture),
                "LeaderCell" => ReplayLeader(root, fixture),
                "LockCell" => ReplayLock(root, fixture),
                "QuorumCell" => ReplayQuorum(root, fixture),
                "SemaphoreCell" => ReplaySemaphore(root, fixture),
                _ => throw new InvalidOperationException($"{fixture}: unknown coordination model"),
            };
        }
        Assert.Equal(29, steps);
    }

    [Fact]
    public void ReplaysCanonicalPresenceCorpus()
    {
        AssertCorpusPresent("presence", PresenceFixtures);
        var steps = 0;
        foreach (var fixture in PresenceFixtures)
        {
            using var document = SpecCorpus.Load("presence", fixture);
            var root = document.RootElement;
            Assert.Equal("Presence", root.GetProperty("kind").GetString());
            steps += root.GetProperty("model").GetString() switch
            {
                "EphemeralCell" => ReplayEphemeral(root, fixture),
                "PresenceCell" => ReplayPresence(root, fixture),
                "AwarenessCell" => ReplayAwareness(root, fixture),
                _ => throw new InvalidOperationException($"{fixture}: unknown presence model"),
            };
        }
        Assert.Equal(16, steps);
    }

    [Fact]
    public void ReplaysCanonicalResilienceCorpus()
    {
        AssertCorpusPresent("resilience", ResilienceFixtures);
        var steps = 0;
        foreach (var fixture in ResilienceFixtures)
        {
            using var document = SpecCorpus.Load("resilience", fixture);
            var root = document.RootElement;
            Assert.Equal("Resilience", root.GetProperty("kind").GetString());
            steps += root.GetProperty("model").GetString() switch
            {
                "CircuitBreakerCell" => ReplayCircuitBreaker(root, fixture),
                "RetryPolicyCell" => ReplayRetry(root, fixture),
                "BulkheadCell" => ReplayBulkhead(root, fixture),
                "TimeoutCell" => ReplayTimeout(root, fixture),
                _ => throw new InvalidOperationException($"{fixture}: unknown resilience model"),
            };
        }
        Assert.Equal(21, steps);
    }

    [Fact]
    public void ReplaysCanonicalServiceCorpus()
    {
        AssertCorpusPresent("service", ServiceFixtures);
        var steps = 0;
        foreach (var fixture in ServiceFixtures)
        {
            using var document = SpecCorpus.Load("service", fixture);
            var root = document.RootElement;
            Assert.Equal("Service", root.GetProperty("kind").GetString());
            steps += root.GetProperty("model").GetString() switch
            {
                "HealthCell" => ReplayHealth(root, fixture),
                "ReadinessCell" => ReplayReadiness(root, fixture),
                "DiscoveryCell" => ReplayDiscovery(root, fixture),
                "ServiceRegistry" => ReplayServiceRegistry(root, fixture),
                _ => throw new InvalidOperationException($"{fixture}: unknown service model"),
            };
        }
        Assert.Equal(20, steps);
    }

    private static int ReplayLease(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new LeaseCell(context);
        var probe = context.Computed(ops => cell.HolderCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            var now = operation.GetProperty("now").GetInt64();
            switch (operation.GetProperty("type").GetString())
            {
                case "acquire":
                    AssertNullableLong(
                        step.GetProperty("returns"),
                        cell.Acquire(
                            operation.GetProperty("peer").GetInt64(),
                            now,
                            operation.GetProperty("ttl").GetInt64()));
                    break;
                case "renew":
                    Assert.Equal(
                        step.GetProperty("returns").GetBoolean(),
                        cell.Renew(
                            operation.GetProperty("peer").GetInt64(),
                            now,
                            operation.GetProperty("ttl").GetInt64()));
                    break;
                case "tick":
                    Assert.Equal(step.GetProperty("returns").GetBoolean(), cell.Tick(now));
                    break;
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "holder", probe);
            AssertNullableLong(expected.GetProperty("holder"), cell.Holder(now));
            Assert.Equal(expected.GetProperty("held").GetBoolean(), cell.IsHeld(now));
            Assert.Equal(expected.GetProperty("fence").GetInt64(), cell.Fence);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayLeader(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new LeaderCell(context, root.GetProperty("config").GetProperty("me").GetInt64());
        var probe = context.Computed(ops => cell.CurrentLeaderCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            var now = operation.GetProperty("now").GetInt64();
            _ = operation.GetProperty("type").GetString() switch
            {
                "campaign" => cell.Campaign(now, operation.GetProperty("ttl").GetInt64()),
                "contend" => cell.Contend(
                    operation.GetProperty("peer").GetInt64(),
                    now,
                    operation.GetProperty("ttl").GetInt64()),
                "tick" => cell.Tick(now),
                _ => throw new InvalidOperationException("unknown leader operation"),
            };
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "current_leader", probe);
            AssertNullableLong(expected.GetProperty("current_leader"), cell.CurrentLeader(now));
            Assert.Equal(Enum.Parse<LeaderRole>(expected.GetProperty("role").GetString()!), cell.Role(now));
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayLock(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new LockCell(context);
        var probe = context.Computed(ops => cell.IsLockedCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            var now = operation.GetProperty("now").GetInt64();
            switch (operation.GetProperty("type").GetString())
            {
                case "acquire":
                    AssertNullableLong(
                        step.GetProperty("returns"),
                        cell.Acquire(
                            operation.GetProperty("peer").GetInt64(),
                            now,
                            operation.GetProperty("ttl").GetInt64()));
                    break;
                case "validate":
                    Assert.Equal(
                        step.GetProperty("returns").GetBoolean(),
                        cell.Validate(operation.GetProperty("fence").GetInt64(), now));
                    break;
                case "tick":
                    Assert.Equal(step.GetProperty("returns").GetBoolean(), cell.Tick(now));
                    break;
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "is_locked", probe);
            Assert.Equal(expected.GetProperty("is_locked").GetBoolean(), cell.IsLocked(now));
            Assert.Equal(expected.GetProperty("fence").GetInt64(), cell.Fence);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayQuorum(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = BarrierCell.Quorum(
            context,
            root.GetProperty("config").GetProperty("total").GetInt32());
        var probe = context.Computed(ops => cell.IsOpenCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            Assert.Equal(
                step.GetProperty("returns").GetBoolean(),
                cell.Arrive(operation.GetProperty("peer").GetInt64()));
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "is_open", probe);
            Assert.Equal(expected.GetProperty("votes").GetInt32(), cell.Count);
            Assert.Equal(expected.GetProperty("is_open").GetBoolean(), cell.IsOpen);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplaySemaphore(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new SemaphoreCell(
            context,
            root.GetProperty("config").GetProperty("capacity").GetInt32());
        var probe = context.Computed(ops => cell.PermitsAvailableCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            if (operation.GetProperty("type").GetString() == "acquire")
                Assert.Equal(step.GetProperty("returns").GetBoolean(), cell.Acquire());
            else
            {
                Assert.Equal(JsonValueKind.Null, step.GetProperty("returns").ValueKind);
                cell.Release();
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "permits_available", probe);
            Assert.Equal(expected.GetProperty("permits_available").GetInt32(), cell.PermitsAvailable);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayEphemeral(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new EphemeralCell<string>(context);
        var probe = context.Computed(ops => cell.ValueCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            if (operation.GetProperty("type").GetString() == "set")
            {
                cell.Set(
                    operation.GetProperty("value").GetString()!,
                    operation.GetProperty("now").GetInt64(),
                    operation.GetProperty("ttl").GetInt64());
            }
            else
            {
                cell.Tick(operation.GetProperty("now").GetInt64());
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "value", probe);
            AssertOptionalString(expected.GetProperty("value"), cell.Value);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayPresence(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new PresenceCell<string>(
            context,
            root.GetProperty("config").GetProperty("ttl").GetInt64());
        var probe = context.Computed(ops => cell.PresentCell.Get(ops).Count);
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            var now = operation.GetProperty("now").GetInt64();
            switch (operation.GetProperty("type").GetString())
            {
                case "heartbeat":
                    cell.Heartbeat(
                        operation.GetProperty("peer").GetInt64(),
                        operation.GetProperty("value").GetString()!,
                        now);
                    break;
                case "evict":
                    cell.Evict(operation.GetProperty("peer").GetInt64(), now);
                    break;
                case "tick":
                    cell.Tick(now);
                    break;
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "present", probe);
            AssertLongStringMap(expected.GetProperty("present"), cell.Present);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayAwareness(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new AwarenessCell<string>(
            context,
            root.GetProperty("config").GetProperty("ttl").GetInt64());
        var probe = context.Computed(ops => cell.PresentCell.Get(ops).Count);
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            var now = operation.GetProperty("now").GetInt64();
            if (operation.GetProperty("type").GetString() == "set")
            {
                cell.Set(
                    operation.GetProperty("peer").GetInt64(),
                    operation.GetProperty("value").GetString()!,
                    now);
            }
            else
            {
                cell.Tick(now);
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "present", probe);
            AssertLongStringMap(expected.GetProperty("present"), cell.Present);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayCircuitBreaker(JsonElement root, string fixture)
    {
        var config = root.GetProperty("config");
        var context = new Context();
        var cell = new CircuitBreakerCell(
            context,
            config.GetProperty("window").GetInt32(),
            config.GetProperty("failure_threshold").GetInt32(),
            config.GetProperty("reset_timeout").GetInt64());
        var probe = context.Computed(ops => cell.StateCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            var now = operation.GetProperty("now").GetInt64();
            if (operation.GetProperty("type").GetString() == "allow")
                Assert.Equal(step.GetProperty("returns").GetBoolean(), cell.Allow(now));
            else
                cell.Record(operation.GetProperty("success").GetBoolean(), now);
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "state", probe);
            Assert.Equal(
                Enum.Parse<BreakerState>(expected.GetProperty("state").GetString()!),
                cell.State);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayRetry(JsonElement root, string fixture)
    {
        var config = root.GetProperty("config");
        var context = new Context();
        var cell = new RetryPolicyCell(
            context,
            config.GetProperty("base").GetInt64(),
            config.GetProperty("cap").GetInt64());
        var probe = context.Computed(ops => cell.DelayCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            Assert.Equal(step.GetProperty("returns").GetInt64(), cell.NextDelay());
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "delay", probe);
            Assert.Equal(expected.GetProperty("delay").GetInt64(), cell.Delay);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayBulkhead(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new BulkheadCell(
            context,
            root.GetProperty("config").GetProperty("capacity").GetInt32());
        var probe = context.Computed(ops => cell.InUseCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            if (operation.GetProperty("type").GetString() == "acquire")
                Assert.Equal(step.GetProperty("returns").GetBoolean(), cell.Acquire());
            else
            {
                Assert.Equal(JsonValueKind.Null, step.GetProperty("returns").ValueKind);
                cell.Release();
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "in_use", probe);
            Assert.Equal(expected.GetProperty("in_use").GetInt32(), cell.InUse);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayTimeout(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new TimeoutCell(context);
        var probe = context.Computed(ops => cell.IsTimedOutCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            var now = operation.GetProperty("now").GetInt64();
            var returned = operation.GetProperty("type").GetString() == "arm"
                ? cell.Arm(now, operation.GetProperty("timeout").GetInt64())
                : cell.Tick(now);
            Assert.Equal(step.GetProperty("returns").GetBoolean(), returned);
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "is_timed_out", probe);
            Assert.Equal(expected.GetProperty("is_timed_out").GetBoolean(), cell.IsTimedOut);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayHealth(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new HealthCell(context);
        var probe = context.Computed(ops => cell.HealthStateCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            cell.Set(
                operation.GetProperty("name").GetString()!,
                operation.GetProperty("up").GetBoolean(),
                operation.GetProperty("critical").GetBoolean());
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "health", probe);
            Assert.Equal(
                Enum.Parse<HealthState>(expected.GetProperty("health").GetString()!),
                cell.Health);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayReadiness(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new ReadinessCell(context);
        var probe = context.Computed(ops => cell.ReadyCell.Get(ops));
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            cell.Set(
                operation.GetProperty("name").GetString()!,
                operation.GetProperty("ready").GetBoolean());
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "ready", probe);
            Assert.Equal(expected.GetProperty("ready").GetBoolean(), cell.Ready);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayDiscovery(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new DiscoveryCell(context);
        var probe = context.Computed(ops => cell.DiscoveryMapCell.Get(ops).Count);
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            switch (operation.GetProperty("type").GetString())
            {
                case "register":
                    cell.Register(
                        operation.GetProperty("service").GetString()!,
                        operation.GetProperty("endpoint").GetString()!,
                        operation.GetProperty("peer").GetInt64());
                    break;
                case "deregister":
                    cell.Deregister(operation.GetProperty("service").GetString()!);
                    break;
                case "evict":
                    cell.Evict(operation.GetProperty("peer").GetInt64());
                    break;
                case "resolve":
                    Assert.Equal(
                        step.GetProperty("returns").GetString(),
                        cell.Resolve(operation.GetProperty("service").GetString()!));
                    break;
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "discovery", probe);
            AssertStringMap(expected.GetProperty("discovery"), cell.Discovery);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        return index;
    }

    private static int ReplayServiceRegistry(JsonElement root, string fixture)
    {
        var context = new Context();
        var cell = new ServiceRegistry(context);
        var probe = context.Computed(ops => cell.ProjectionCell.Get(ops).Count);
        _ = probe.Get();
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("op");
            switch (operation.GetProperty("type").GetString())
            {
                case "register":
                    cell.Register(
                        operation.GetProperty("service").GetString()!,
                        operation.GetProperty("endpoint").GetString()!);
                    break;
                case "deregister":
                    cell.Deregister(operation.GetProperty("service").GetString()!);
                    break;
                case "replay":
                    cell.Replay();
                    break;
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "projection", probe);
            AssertStringMap(expected.GetProperty("projection"), cell.Projection);
            _ = probe.Get();
            expected.Verify();
            index++;
        }
        Assert.Equal(4, cell.Log.Count);
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
        FixtureAssertions expected,
        string projection,
        Computed<T> probe)
    {
        var actual = !probe.Peek(out _);
        var invalidates = expected.GetProperty("invalidates");
        var wanted = invalidates.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? invalidates.GetBoolean()
            : invalidates.GetProperty(projection).GetBoolean();
        Assert.Equal(wanted, actual);
    }

    private static void AssertNullableLong(JsonElement expected, long? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
            Assert.Null(actual);
        else
            Assert.Equal(expected.GetInt64(), actual);
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

    private static void AssertLongStringMap(
        JsonElement expected,
        IReadOnlyDictionary<long, string> actual)
    {
        Assert.Equal(expected.EnumerateObject().Count(), actual.Count);
        foreach (var property in expected.EnumerateObject())
        {
            var key = long.Parse(
                property.Name,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(actual.TryGetValue(key, out var value));
            Assert.Equal(property.Value.GetString(), value);
        }
    }

    private static void AssertStringMap(
        JsonElement expected,
        IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.EnumerateObject().Count(), actual.Count);
        foreach (var property in expected.EnumerateObject())
        {
            Assert.True(actual.TryGetValue(property.Name, out var value));
            Assert.Equal(property.Value.GetString(), value);
        }
    }
}
