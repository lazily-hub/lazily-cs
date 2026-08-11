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
            expected.AssertKey("alive_set", cell.PeerSet);
            expected.AssertObjectKey(
                "states",
                want =>
                {
                    foreach (var state in want.EnumerateObject())
                    {
                        var name = state.Name;
                        want.AssertKeyWith(name, wantState => wantState.AssertEqual(
                            w => Enum.Parse<PeerState>(w.GetString()!),
                            cell.State(long.Parse(name, System.Globalization.CultureInfo.InvariantCulture))));
                    }
                });
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
                // Fail closed (#lzscenariobodyskip): a C# switch STATEMENT over a string is not
                // exhaustiveness-checked, so an unmatched op used to run NOTHING while the ledger
                // still booked the step as replayed — and `expected` was then compared against
                // state the fixture never asked anyone to reach.
                default:
                    throw new InvalidOperationException(
                        $"{fixture}: unknown lease op in fixture: {operation.GetProperty("type").GetString()}");
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "holder", probe);
            expected.AssertKeyWith("holder", want => want.Against(
                cell.Holder(now),
                (expect, got) => AssertNullableLong(expect, got)));
            expected.AssertKey("held", cell.IsHeld(now));
            expected.AssertKey("fence", cell.Fence);
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
            expected.AssertKeyWith(
                "current_leader",
                want => want.Against(
                    cell.CurrentLeader(now),
                    (expect, got) => AssertNullableLong(expect, got)));
            expected.AssertKeyWith(
                "role",
                want => want.AssertEqual(w => Enum.Parse<LeaderRole>(w.GetString()!), cell.Role(now)));
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
                // Fail closed (#lzscenariobodyskip): unmatched op ran nothing and the step still
                // booked as replayed.
                default:
                    throw new InvalidOperationException(
                        $"{fixture}: unknown lock op in fixture: {operation.GetProperty("type").GetString()}");
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "is_locked", probe);
            expected.AssertKey("is_locked", cell.IsLocked(now));
            expected.AssertKey("fence", cell.Fence);
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
            // Fail closed (#lzscenariobodyskip): this driver replayed EVERY step as `vote` while
            // `op.type` sat unread in the fixture — the discriminator nobody read. A corpus that
            // grows a second quorum op would have been silently replayed as an arrival.
            AssertOpType(fixture, index, operation, "vote");
            Assert.Equal(
                step.GetProperty("returns").GetBoolean(),
                cell.Arrive(operation.GetProperty("peer").GetInt64()));
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "is_open", probe);
            expected.AssertKey("votes", cell.Count);
            expected.AssertKey("is_open", cell.IsOpen);
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
            // Fail closed (#lzscenariobodyskip): the final `else` ASSUMED release without checking
            // it, so any op that was not `acquire` — including a misspelling — released a permit.
            var semaphoreOp = operation.GetProperty("type").GetString();
            if (semaphoreOp == "acquire")
                Assert.Equal(step.GetProperty("returns").GetBoolean(), cell.Acquire());
            else if (semaphoreOp == "release")
            {
                Assert.Equal(JsonValueKind.Null, step.GetProperty("returns").ValueKind);
                cell.Release();
            }
            else
                throw new InvalidOperationException(
                    $"{fixture} step {index}: unknown semaphore op in fixture: {semaphoreOp}");
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "permits_available", probe);
            expected.AssertKey("permits_available", cell.PermitsAvailable);
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
            // Fail closed (#lzscenariobodyskip): the final `else` ASSUMED tick without checking it.
            var ephemeralOp = operation.GetProperty("type").GetString();
            if (ephemeralOp == "set")
            {
                cell.Set(
                    operation.GetProperty("value").GetString()!,
                    operation.GetProperty("now").GetInt64(),
                    operation.GetProperty("ttl").GetInt64());
            }
            else if (ephemeralOp == "tick")
            {
                cell.Tick(operation.GetProperty("now").GetInt64());
            }
            else
            {
                throw new InvalidOperationException(
                    $"{fixture} step {index}: unknown ephemeral op in fixture: {ephemeralOp}");
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "value", probe);
            expected.AssertKeyWith("value", want => want.Against(
                cell.Value,
                (expect, got) => AssertOptionalString(expect, got)));
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
                // Fail closed (#lzscenariobodyskip): unmatched op ran nothing and the step still
                // booked as replayed.
                default:
                    throw new InvalidOperationException(
                        $"{fixture}: unknown presence op in fixture: {operation.GetProperty("type").GetString()}");
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "present", probe);
            expected.AssertObjectKey("present", want => AssertLongStringMap(want, cell.Present));
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
            // Fail closed (#lzscenariobodyskip): the final `else` ASSUMED tick without checking it.
            var awarenessOp = operation.GetProperty("type").GetString();
            if (awarenessOp == "set")
            {
                cell.Set(
                    operation.GetProperty("peer").GetInt64(),
                    operation.GetProperty("value").GetString()!,
                    now);
            }
            else if (awarenessOp == "tick")
            {
                cell.Tick(now);
            }
            else
            {
                throw new InvalidOperationException(
                    $"{fixture} step {index}: unknown awareness op in fixture: {awarenessOp}");
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "present", probe);
            expected.AssertObjectKey("present", want => AssertLongStringMap(want, cell.Present));
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
            // Fail closed (#lzscenariobodyskip): the final `else` ASSUMED record without checking
            // it, so an unrecognised op was fed to the breaker as a success/failure sample.
            var breakerOp = operation.GetProperty("type").GetString();
            if (breakerOp == "allow")
                Assert.Equal(step.GetProperty("returns").GetBoolean(), cell.Allow(now));
            else if (breakerOp == "record")
                cell.Record(operation.GetProperty("success").GetBoolean(), now);
            else
                throw new InvalidOperationException(
                    $"{fixture} step {index}: unknown circuit-breaker op in fixture: {breakerOp}");
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "state", probe);
            expected.AssertKeyWith(
                "state",
                want => want.AssertEqual(w => Enum.Parse<BreakerState>(w.GetString()!), cell.State));
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
            // Fail closed (#lzscenariobodyskip): this driver replayed EVERY step as `next` and
            // never opened `op` at all — the discriminator nobody read.
            AssertOpType(fixture, index, step.GetProperty("op"), "next");
            Assert.Equal(step.GetProperty("returns").GetInt64(), cell.NextDelay());
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "delay", probe);
            expected.AssertKey("delay", cell.Delay);
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
            // Fail closed (#lzscenariobodyskip): the final `else` ASSUMED release without checking.
            var bulkheadOp = operation.GetProperty("type").GetString();
            if (bulkheadOp == "acquire")
                Assert.Equal(step.GetProperty("returns").GetBoolean(), cell.Acquire());
            else if (bulkheadOp == "release")
            {
                Assert.Equal(JsonValueKind.Null, step.GetProperty("returns").ValueKind);
                cell.Release();
            }
            else
                throw new InvalidOperationException(
                    $"{fixture} step {index}: unknown bulkhead op in fixture: {bulkheadOp}");
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "in_use", probe);
            expected.AssertKey("in_use", cell.InUse);
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
            // Fail closed (#lzscenariobodyskip): the ternary's false arm ASSUMED tick, so any op
            // other than `arm` — including a misspelling — advanced the clock instead.
            var timeoutOp = operation.GetProperty("type").GetString();
            var returned = timeoutOp switch
            {
                "arm" => cell.Arm(now, operation.GetProperty("timeout").GetInt64()),
                "tick" => cell.Tick(now),
                _ => throw new InvalidOperationException(
                    $"{fixture} step {index}: unknown timeout op in fixture: {timeoutOp}"),
            };
            Assert.Equal(step.GetProperty("returns").GetBoolean(), returned);
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "is_timed_out", probe);
            expected.AssertKey("is_timed_out", cell.IsTimedOut);
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
            // Fail closed (#lzscenariobodyskip): this driver replayed EVERY step as `set` while
            // `op.type` sat unread in the fixture — the discriminator nobody read.
            AssertOpType(fixture, index, operation, "set");
            cell.Set(
                operation.GetProperty("name").GetString()!,
                operation.GetProperty("up").GetBoolean(),
                operation.GetProperty("critical").GetBoolean());
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "health", probe);
            expected.AssertKeyWith(
                "health",
                want => want.AssertEqual(w => Enum.Parse<HealthState>(w.GetString()!), cell.Health));
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
            // Fail closed (#lzscenariobodyskip): this driver replayed EVERY step as `set` while
            // `op.type` sat unread in the fixture — the discriminator nobody read.
            AssertOpType(fixture, index, operation, "set");
            cell.Set(
                operation.GetProperty("name").GetString()!,
                operation.GetProperty("ready").GetBoolean());
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "ready", probe);
            expected.AssertKey("ready", cell.Ready);
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
                // Fail closed (#lzscenariobodyskip): unmatched op ran nothing and the step still
                // booked as replayed.
                default:
                    throw new InvalidOperationException(
                        $"{fixture}: unknown discovery op in fixture: {operation.GetProperty("type").GetString()}");
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "discovery", probe);
            expected.AssertObjectKey("discovery", want => AssertStringMap(want, cell.Discovery));
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
                // Fail closed (#lzscenariobodyskip): unmatched op ran nothing and the step still
                // booked as replayed.
                default:
                    throw new InvalidOperationException(
                        $"{fixture}: unknown service-registry op in fixture: {operation.GetProperty("type").GetString()}");
            }
            var expected = FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            AssertInvalidation(expected, "projection", probe);
            expected.AssertObjectKey("projection", want => AssertStringMap(want, cell.Projection));
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

    /// <summary>
    /// Fail closed (#lzscenariobodyskip) for a driver whose corpus currently carries exactly ONE
    /// op shape. Such a driver has no dispatch to give a default to, so the discriminator goes
    /// unread and a corpus that grows a second op is replayed as the first — silently, with the
    /// scenario ledger still booking the step. This is the assertion that turns that into a red.
    /// </summary>
    private static void AssertOpType(string fixture, int index, JsonElement operation, string only)
    {
        var actual = operation.GetProperty("type").GetString();
        if (!string.Equals(actual, only, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{fixture} step {index}: unknown op in fixture: {actual} (this driver replays only {only})");
    }

    private static void AssertInvalidation<T>(
        FixtureAssertions expected,
        string projection,
        Computed<T> probe)
    {
        var actual = !probe.Peek(out _);
        if (expected.GetProperty("invalidates").ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            expected.AssertKey("invalidates", actual);
            return;
        }

        // The matrix form. This site used to read ONE named projection out of the object and
        // ignore every other key in it (#lzsubblockkeyset) — a fixture saying
        // `{"value": true, "membership": false}` had `membership` compared by nothing.
        // Descending fixes it structurally: these fixtures carry exactly the projection this
        // probe observes, and a reader class the corpus adds later reports as unconsumed
        // rather than being silently skipped.
        expected.AssertObjectKey(
            "invalidates",
            invalidates => invalidates.AssertKey(projection, actual));
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

    /// <summary>
    /// Compare a fixture map keyed by peer id against <paramref name="actual"/>, routing every
    /// entry through the child tracker.
    /// </summary>
    /// <remarks>
    /// The count comparison stays — it is what catches an entry the runtime grew — but the
    /// per-entry assertions now go through <paramref name="expected"/> so the child's teardown
    /// reports a fixture entry this loop never reached (<c>#lzsubblockkeyset</c>).
    /// </remarks>
    private static void AssertLongStringMap(
        FixtureAssertions expected,
        IReadOnlyDictionary<long, string> actual)
    {
        Assert.Equal(expected.EnumerateObject().Count(), actual.Count);
        foreach (var property in expected.EnumerateObject())
        {
            var name = property.Name;
            expected.AssertKeyWith(name, want =>
            {
                var key = long.Parse(name, System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(actual.TryGetValue(key, out var value));
                want.AssertEqual(w => w.GetString(), value);
            });
        }
    }

    /// <inheritdoc cref="AssertLongStringMap"/>
    private static void AssertStringMap(
        FixtureAssertions expected,
        IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.EnumerateObject().Count(), actual.Count);
        foreach (var property in expected.EnumerateObject())
        {
            var name = property.Name;
            expected.AssertKeyWith(name, want =>
            {
                Assert.True(actual.TryGetValue(name, out var value));
                want.AssertEqual(w => w.GetString(), value);
            });
        }
    }
}
