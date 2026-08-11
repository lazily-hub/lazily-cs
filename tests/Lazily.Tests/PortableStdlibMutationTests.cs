using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Sdk;

namespace Lazily.Tests;

/// <summary>
/// Applies every mutation operator the portable-stdlib corpus declares
/// (<c>#lzstdlibmutantsallbindings</c>).
/// </summary>
/// <remarks>
/// <para>
/// THE DEFECT THIS CLOSES. Each stdlib fixture carries a <c>mutations</c> ledger — "mutate the
/// implementation THIS named way and exactly these scenarios must fail".
/// <c>PortableStdlibConformanceTests.AssertFixtureBookkeeping</c> read that ledger and checked it
/// against the fixture's OWN scenario ids and its OWN <c>mutation_floor</c>: a claim satisfied by
/// its own bookkeeping, which is the shape
/// <c>feedback_conformance_tests_drive_real_behavior_not_runner_bookkeeping</c> names. No operator
/// was ever applied, so rebinding an entry's <c>must_fail</c> to a scenario that operator does not
/// break stayed green, and the ledger's central claim was tested by nothing.
/// </para>
/// <para>
/// THE SHAPE. lazily-py <c>tests/test_stdlib_conformance.py</c> (landed as ed812ab) and lazily-rs
/// <c>tests/stdlib_conformance.rs</c> (<c>independent_failures</c>). The operator perturbs an
/// INDEPENDENT model of the feature, never the shipped <c>Lazily.Timer</c> /
/// <c>Timeout&lt;T&gt;</c> / <c>RevisionBarrier</c>. Mutating production code to test the corpus
/// would test the mutation harness, would need the library to carry seams that exist only for
/// tests, and would say nothing about whether the corpus can TELL a correct implementation from a
/// wrong one — which is the only thing the ledger claims. The production replay is
/// <c>PortableStdlibConformanceTests.ReplaysCanonicalPortableStdlibCorpus</c>, and it stays exactly
/// what it is.
/// </para>
/// <para>
/// THE TRACKER IS DELIBERATELY BYPASSED. <see cref="LoadPlain"/> reads the same bytes WITHOUT
/// <see cref="SpecCorpus.Load"/> and without binding any <see cref="FixtureAssertions"/> block. The
/// interpreter replays every scenario several times — once unperturbed and once per declared
/// operator — and most of those replays are EXPECTED to diverge. Feeding those comparisons through
/// the assertion-key tracker would book keys as compared/asserted on the strength of a run whose
/// whole point is that it does not conform, and the comparison seam
/// (<c>#lzcsuncomparedvalues</c>) would be booking a comparison against a model rather than
/// against the library. lazily-py makes the same split, for the same reason, with its
/// <c>load_plain()</c>. The tracked reading of these three fixtures is the canonical replay named
/// above, which binds every <c>expect</c> block through <c>FixtureAssertions.Deep</c>.
/// </para>
/// </remarks>
public sealed class PortableStdlibMutationTests
{
    /// <summary>The three stdlib fixtures, in the order the canonical runner replays them.</summary>
    private static readonly string[] Fixtures =
    [
        "timer.json",
        "timeout.json",
        "revision_barrier.json",
    ];

    /// <summary>
    /// The floor on (operator, scenario) pairs this suite applies: timer 4 + timeout 5 +
    /// revision_barrier 6.
    /// </summary>
    /// <remarks>
    /// A floor, not an equality: the corpus may grow pairs, and this run must never apply fewer
    /// than it does today. It is the number the run itself produces, not old-value-plus-a-delta.
    /// </remarks>
    private const int DeclaredPairs = 15;

    /// <summary>
    /// The non-vacuity control: unperturbed, the model reproduces every scenario exactly.
    /// </summary>
    /// <remarks>
    /// Without this a mutation proves nothing — a scenario that fails whether or not the operator
    /// is applied is not evidence that the operator broke it.
    /// </remarks>
    [Fact]
    public void IndependentModelAgreesWithTheUnperturbedCorpus()
    {
        foreach (var name in Fixtures)
        {
            using var document = LoadPlain(name);
            var fixture = document.RootElement;
            Assert.NotEmpty(fixture.GetProperty("scenarios").EnumerateArray().ToArray());
            var failed = IndependentFailures(fixture, operatorName: null, out _);
            Assert.True(
                failed.Count == 0,
                $"stdlib/{name}: the independent model diverged from the canonical corpus with NO "
                + $"operator applied, on [{string.Join(", ", failed)}]");
        }
    }

    /// <summary>
    /// Every declared (operator, scenario) pair: the scenario passes with no operator applied, and
    /// fails with it.
    /// </summary>
    [Fact]
    public void EveryDeclaredMutationIsObservedByTheIndependentInterpreter()
    {
        var pairs = 0;
        foreach (var name in Fixtures)
        {
            using var document = LoadPlain(name);
            var fixture = document.RootElement;
            var baseline = IndependentFailures(fixture, operatorName: null, out _);
            Assert.True(
                baseline.Count == 0,
                $"stdlib/{name}: unperturbed replay already fails on "
                + $"[{string.Join(", ", baseline)}]");

            var mutations = fixture.GetProperty("mutations").EnumerateArray().ToArray();
            Assert.NotEmpty(mutations);
            var fixturePairs = 0;
            foreach (var mutation in mutations)
            {
                var operatorName = mutation.GetProperty("operator").GetString()
                    ?? throw new JsonException("mutation operator must be a string");
                var mustFail = mutation.GetProperty("must_fail").EnumerateArray()
                    .Select(id => id.GetString() ?? throw new JsonException("scenario id"))
                    .ToHashSet(StringComparer.Ordinal);
                Assert.True(
                    mustFail.Count > 0,
                    $"stdlib/{name}: mutation '{operatorName}' names no scenario");

                var failed = IndependentFailures(fixture, operatorName, out var consulted);

                // An operator with no interpreter arm is a HARD failure, never a skip: a silently
                // unimplemented operator is the same vacuity as a ledger checked against itself.
                // The set it is checked against is DERIVED from the branches this replay really
                // evaluated — a hand-maintained registry of "operators this file implements" is
                // one more piece of bookkeeping to drift, which is the defect one level up.
                Assert.True(
                    consulted.Contains(operatorName),
                    $"stdlib/{name}: mutation operator '{operatorName}' is declared by the corpus "
                    + "but no arm of the independent interpreter implements it; the replay "
                    + $"consulted [{string.Join(", ", consulted)}]");

                var escaped = mustFail.Where(id => !failed.Contains(id)).Order(StringComparer.Ordinal).ToArray();
                Assert.True(
                    escaped.Length == 0,
                    $"stdlib/{name}: mutation '{operatorName}' did NOT break "
                    + $"[{string.Join(", ", escaped)}] — the ledger claims those scenarios detect it");

                // Redundant given `baseline.Count == 0`, but it names the PAIR rather than the
                // fixture when it fires.
                var stillGreen = mustFail.Where(baseline.Contains).Order(StringComparer.Ordinal).ToArray();
                Assert.True(
                    stillGreen.Length == 0,
                    $"stdlib/{name}: '{operatorName}'/[{string.Join(", ", stillGreen)}] fail with "
                    + "the operator applied AND without it, so the mutation proves nothing");

                fixturePairs += mustFail.Count;
            }

            // Every entry contributes at least one (operator, scenario) pair, so the corpus's own
            // `mutation_floor` is also a floor on what this run APPLIED — as opposed to the floor
            // in `AssertFixtureBookkeeping`, which bounds the ledger's SIZE and can do no more.
            var floor = fixture.GetProperty("mutation_floor").GetInt32();
            Assert.True(
                fixturePairs >= floor,
                $"stdlib/{name}: applied {fixturePairs} (operator, scenario) pairs, below the "
                + $"declared mutation_floor {floor}");
            pairs += fixturePairs;
        }

        Assert.True(
            pairs >= DeclaredPairs,
            $"applied only {pairs} (operator, scenario) pairs, below the {DeclaredPairs} this "
            + "corpus declares");
    }

    /// <summary>
    /// Some operators break scenarios their ledger entry does not name, so the complement is NOT
    /// asserted.
    /// </summary>
    /// <remarks>
    /// The obvious complement — "a scenario NOT named in <c>must_fail</c> survives the operator" —
    /// is FALSE for this corpus, and asserting it would invent a claim the fixtures never make.
    /// <c>deadline_strict_greater</c> on <c>timer.json</c> also breaks
    /// <c>clock_regression_is_rejected_without_state_change</c>, whose final step observes exactly
    /// at the deadline. <c>must_fail</c> is a LOWER BOUND on detection ("these scenarios catch
    /// it"), not a partition; lazily-rs makes the same choice (<c>must_fail.is_subset(&amp;failed)</c>,
    /// never equality), and 4 of the 13 entries here break scenarios they do not name.
    /// Recorded as a test rather than a comment so the day the corpus DOES become a partition this
    /// stops being true and someone has to decide deliberately whether to tighten the assertion
    /// above.
    /// </remarks>
    [Fact]
    public void TheComplementIsNotAssertedBecauseTheCorpusDoesNotSupportIt()
    {
        using var document = LoadPlain("timer.json");
        var fixture = document.RootElement;
        var failed = IndependentFailures(fixture, "deadline_strict_greater", out _);
        var named = fixture.GetProperty("mutations").EnumerateArray()
            .Single(mutation => mutation.GetProperty("operator").GetString() == "deadline_strict_greater")
            .GetProperty("must_fail").EnumerateArray()
            .Select(id => id.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            ["clock_regression_is_rejected_without_state_change"],
            failed.Where(id => !named.Contains(id)).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The same bytes as <see cref="SpecCorpus.Load"/> reads, WITHOUT the assertion-key tracker
    /// and without the manifest record.
    /// </summary>
    /// <remarks>
    /// Resolved through <see cref="SpecCorpus.Root"/> so <c>LAZILY_SPEC_CONFORMANCE_DIR</c> still
    /// redirects this runner — a corpus-perturbation probe that cannot move the fixture under the
    /// interpreter cannot show the interpreter reads it. Fails closed on an absent corpus, on the
    /// same terms as every other runner here: a skip-if-absent replay is worse than no replay.
    /// </remarks>
    private static JsonDocument LoadPlain(string fixtureName)
    {
        var root = SpecCorpus.Root ?? throw new DirectoryNotFoundException(
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}; "
            + $"clone lazily-spec beside this repo or set {SpecCorpus.DirOverrideVar}.");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "stdlib", fixtureName)));
    }

    /// <summary>
    /// Replay every scenario through the model, perturbed by <paramref name="operatorName"/>.
    /// </summary>
    /// <returns>The scenario ids that DIVERGED from their declared <c>expect</c>.</returns>
    /// <param name="consulted">
    /// The operator names the replay's branches actually evaluated — derived, never declared.
    /// </param>
    private static SortedSet<string> IndependentFailures(
        JsonElement fixture,
        string? operatorName,
        out SortedSet<string> consulted)
    {
        var feature = fixture.GetProperty("feature").GetString();
        Func<ModelState, JsonElement, Mutation, JsonObject> model = feature switch
        {
            "stdlib_timer_v1" => ModelTimer,
            "stdlib_timeout_v1" => ModelTimeout,
            "stdlib_revision_barrier_v1" => ModelBarrier,
            _ => throw new XunitException($"unknown stdlib feature '{feature}'"),
        };

        var mutated = new Mutation(operatorName);
        var failed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var scenario in fixture.GetProperty("scenarios").EnumerateArray())
        {
            var id = scenario.GetProperty("id").GetString()
                ?? throw new JsonException("scenario id must be a string");
            var state = new ModelState();
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                var want = JsonNode.Parse(step.GetProperty("expect").GetRawText());
                if (!JsonNode.DeepEquals(want, model(state, step, mutated))) failed.Add(id);
            }
        }

        consulted = mutated.Consulted;
        return failed;
    }

    // -----------------------------------------------------------------------
    // The independent interpreter.
    // -----------------------------------------------------------------------

    private static JsonObject ModelTimer(ModelState state, JsonElement step, Mutation mutated)
    {
        if (ModelOp(step, "start", "observe") == "start")
        {
            var start = U64(step, "now");
            var duration = U64(step, "duration");
            if (duration > ulong.MaxValue - start)
            {
                state.Status = "unavailable";
                state.Reason = "deadline_overflow";
                return Terminal(state, adapterCounts: false);
            }

            state.Status = "pending";
            state.Deadline = start + duration;
            state.LastNow = start;
            return new JsonObject { ["outcome"] = "pending", ["deadline"] = state.Deadline };
        }

        RequireStarted(state, step);
        if (mutated.Applies("fixture_bookkeeping"))
            return new JsonObject { ["outcome"] = "pending", ["deadline"] = state.Deadline };

        var latched = mutated.Applies("terminal_not_latched");
        if (state.Status != "pending" && !latched) return Terminal(state, adapterCounts: false);
        if (latched) state.Status = "pending";

        var now = U64(step, "now");
        if (state.LastNow is { } lastNow && now < lastNow)
        {
            return new JsonObject
            {
                ["outcome"] = "unavailable",
                ["reason"] = "clock_regression",
                ["deadline"] = state.Deadline,
            };
        }

        state.LastNow = now;
        var deadline = state.Deadline ?? throw new XunitException("timer observed with no deadline");
        var reached = mutated.Applies("deadline_strict_greater") ? now > deadline : now >= deadline;
        if (!reached) return new JsonObject { ["outcome"] = "pending", ["deadline"] = deadline };

        state.Status = "fired";
        state.FiredAt = now;
        return Terminal(state, adapterCounts: false);
    }

    private static JsonObject ModelTimeout(ModelState state, JsonElement step, Mutation mutated)
    {
        if (ModelOp(step, "start", "poll") == "start")
        {
            var start = U64(step, "now");
            var duration = U64(step, "duration");
            if (duration > ulong.MaxValue - start)
            {
                state.Status = "unavailable";
                state.Reason = "deadline_overflow";
                return Terminal(state, adapterCounts: false);
            }

            state.Status = "pending";
            state.Deadline = start + duration;
            state.LastNow = start;
            return new JsonObject { ["outcome"] = "pending", ["deadline"] = state.Deadline };
        }

        RequireStarted(state, step);
        if (mutated.Applies("fixture_bookkeeping"))
        {
            return new JsonObject
            {
                ["outcome"] = "pending",
                ["deadline"] = state.Deadline,
                ["operation_calls"] = 0,
                ["cancellation_calls"] = 0,
            };
        }

        var latched = mutated.Applies("terminal_not_latched");
        if (state.Status != "pending" && !latched) return Terminal(state, adapterCounts: true);
        if (latched) state.Status = "pending";

        var now = U64(step, "now");
        var deadline = state.Deadline ?? throw new XunitException("timeout polled with no deadline");
        if (state.LastNow is { } lastNow && now < lastNow)
        {
            state.Status = "unavailable";
            state.Reason = "clock_regression";
            return new JsonObject
            {
                ["outcome"] = "unavailable",
                ["reason"] = "clock_regression",
                ["operation_calls"] = 0,
                ["cancellation_calls"] = 0,
            };
        }

        state.LastNow = now;
        var reached = mutated.Applies("deadline_strict_greater") ? now > deadline : now >= deadline;
        if (reached)
        {
            state.Status = "timed_out";
            return new JsonObject
            {
                ["outcome"] = "timed_out",
                ["operation_calls"] = 0,
                ["cancellation_calls"] = 0,
            };
        }

        // Both drive if-chains whose tail ASSUMES `pending`; validate the spelling so an unknown
        // one names itself instead of quietly meaning "pending" (#lzscenariobodyskip).
        var operation = Str(step, "operation");
        if (operation is not ("completed" or "pending" or "unavailable"))
            throw new XunitException($"unknown operation '{operation}' in {step.GetRawText()}");
        var cancellation = Str(step, "cancellation");
        if (cancellation is not ("cancelled" or "pending" or "unavailable"))
            throw new XunitException($"unknown cancellation '{cancellation}' in {step.GetRawText()}");

        if (mutated.Applies("cancellation_before_completion") && cancellation == "cancelled")
        {
            state.Status = "cancelled";
            return Polled(new JsonObject { ["outcome"] = "cancelled" });
        }

        if (operation == "completed")
        {
            state.Status = "completed";
            state.Value = Str(step, "value");
            return Polled(new JsonObject
            {
                ["outcome"] = "completed",
                ["value"] = state.Value,
            });
        }

        if (operation == "unavailable")
        {
            state.Status = "unavailable";
            state.Reason = "operation_unavailable";
            return Polled(new JsonObject
            {
                ["outcome"] = "unavailable",
                ["reason"] = "operation_unavailable",
            });
        }

        if (cancellation == "cancelled")
        {
            state.Status = "cancelled";
            return Polled(new JsonObject { ["outcome"] = "cancelled" });
        }

        if (cancellation == "unavailable")
        {
            state.Status = "unavailable";
            state.Reason = "cancellation_unavailable";
            return Polled(new JsonObject
            {
                ["outcome"] = "unavailable",
                ["reason"] = "cancellation_unavailable",
            });
        }

        return Polled(new JsonObject { ["outcome"] = "pending", ["deadline"] = deadline });
    }

    private static JsonObject ModelBarrier(ModelState state, JsonElement step, Mutation mutated)
    {
        var op = ModelOp(
            step,
            "start",
            "register_recheck",
            "advance",
            "observe",
            "dispose",
            "receipt");
        if (op == "start")
        {
            state.Status = "pending";
            state.Revision = U64(step, "revision");
            state.Generation = 0;
            state.Required = U64(step, "required_revision");
            var deadline = step.GetProperty("deadline");
            state.Deadline = deadline.ValueKind == JsonValueKind.Null ? null : deadline.GetUInt64();
            state.LastNow = null;
            return BarrierObservation(state);
        }

        RequireStarted(state, step);
        if (mutated.Applies("fixture_bookkeeping"))
        {
            state.Status = "pending";
            return BarrierObservation(state);
        }

        var latched = mutated.Applies("terminal_not_latched");
        if (state.Status != "pending" && !latched)
        {
            var terminal = BarrierObservation(state);
            if (op == "observe") terminal["cancellation_calls"] = 0;
            return terminal;
        }

        if (latched) state.Status = "pending";

        if (op == "dispose")
        {
            state.Status = "disposed";
            return BarrierObservation(state);
        }

        if (op == "receipt")
        {
            // An application-owned effect receipt is NOT barrier authority: it wakes the waiter and
            // changes no revision. The operator makes it authority.
            _ = Str(step, "key");
            if (mutated.Applies("receipt_is_authority"))
            {
                state.Revision = state.Required;
                state.Generation += 1;
                state.Status = "satisfied";
            }

            return BarrierObservation(state);
        }

        if (op == "advance")
        {
            state.Revision = Math.Max(state.Revision, U64(step, "revision"));
            state.Generation += 1;
            if (state.Revision >= state.Required && step.GetProperty("predicate").GetBoolean())
                state.Status = "satisfied";
            return BarrierObservation(state);
        }

        var now = U64(step, "now");
        var regressed = state.LastNow is { } lastNow && now < lastNow;
        if (regressed && !mutated.Applies("barrier_accept_clock_regression"))
        {
            state.Status = "unavailable";
            state.Reason = "clock_regression";
            var rejected = BarrierObservation(state);
            if (op == "observe") rejected["cancellation_calls"] = 0;
            return rejected;
        }

        state.LastNow = now;
        if (op == "register_recheck")
        {
            state.Generation += 1;
            if (!mutated.Applies("barrier_skip_post_registration_recheck"))
            {
                state.Revision = Math.Max(state.Revision, U64(step, "observed_revision"));
                if (state.Revision >= state.Required && step.GetProperty("predicate").GetBoolean())
                    state.Status = "satisfied";
            }

            return BarrierObservation(state);
        }

        var reached = state.Deadline is { } limit
            && (mutated.Applies("deadline_strict_greater") ? now > limit : now >= limit);
        if (reached)
        {
            state.Status = "timed_out";
            var timedOut = BarrierObservation(state);
            timedOut["cancellation_calls"] = 0;
            return timedOut;
        }

        if (state.Revision >= state.Required && step.GetProperty("predicate").GetBoolean())
        {
            state.Status = "satisfied";
            var satisfied = BarrierObservation(state);
            satisfied["cancellation_calls"] = 0;
            return satisfied;
        }

        // Fail-closed tail (#lzscenariobodyskip): a cancellation spelling this model does not know
        // must not behave like `pending`.
        switch (Str(step, "cancellation"))
        {
            case "cancelled":
                state.Status = "cancelled";
                break;
            case "unavailable":
                state.Status = "unavailable";
                state.Reason = "cancellation_unavailable";
                break;
            case "pending":
                break;
            default:
                throw new XunitException(
                    $"unknown cancellation in {step.GetRawText()}");
        }

        var observed = BarrierObservation(state);
        observed["cancellation_calls"] = 1;
        return observed;
    }

    /// <summary>The latched observation: whatever this feature carries, plus no adapter calls.</summary>
    private static JsonObject Terminal(ModelState state, bool adapterCounts)
    {
        var result = new JsonObject { ["outcome"] = state.Status };
        if (state.FiredAt is { } firedAt) result["fired_at"] = firedAt;
        if (state.Value is { } value) result["value"] = value;
        if (state.Reason is { } reason) result["reason"] = reason;
        if (adapterCounts)
        {
            result["operation_calls"] = 0;
            result["cancellation_calls"] = 0;
        }

        return result;
    }

    /// <summary>A poll that reached both adapters: exactly one call each.</summary>
    private static JsonObject Polled(JsonObject result)
    {
        result["operation_calls"] = 1;
        result["cancellation_calls"] = 1;
        return result;
    }

    private static JsonObject BarrierObservation(ModelState state)
    {
        var result = new JsonObject
        {
            ["outcome"] = state.Status,
            ["revision"] = state.Revision,
            ["generation"] = state.Generation,
        };
        if (state.Reason is { } reason) result["reason"] = reason;
        return result;
    }

    private static string ModelOp(JsonElement step, params string[] known)
    {
        var op = step.TryGetProperty("op", out var value) ? value.GetString() : null;
        if (op is null || !known.Contains(op, StringComparer.Ordinal))
        {
            throw new XunitException(
                $"unknown model op '{op}' (known: {string.Join(", ", known)}) in {step.GetRawText()}");
        }

        return op;
    }

    private static void RequireStarted(ModelState state, JsonElement step)
    {
        if (state.Status.Length == 0)
            throw new XunitException($"step before start: {step.GetRawText()}");
    }

    private static ulong U64(JsonElement step, string name) => step.GetProperty(name).GetUInt64();

    private static string Str(JsonElement step, string name) =>
        step.GetProperty(name).GetString()
        ?? throw new JsonException($"step field '{name}' must be a string");

    /// <summary>The operator under test, consulted BY NAME at every perturbable branch.</summary>
    /// <remarks>
    /// <see cref="Consulted"/> is what makes an unimplemented operator loud rather than silent. A
    /// registry of "operators this file implements" would be one more piece of bookkeeping to
    /// drift; this set is produced by the branches the replay really evaluated, so an operator no
    /// arm knows about ends the run naming itself.
    /// </remarks>
    private sealed class Mutation(string? operatorName)
    {
        internal SortedSet<string> Consulted { get; } = new(StringComparer.Ordinal);

        internal bool Applies(string name)
        {
            Consulted.Add(name);
            return string.Equals(operatorName, name, StringComparison.Ordinal);
        }
    }

    /// <summary>The independent model's whole state, shared by all three features.</summary>
    private sealed class ModelState
    {
        /// <summary>Empty until the scenario's `start` step runs.</summary>
        internal string Status { get; set; } = string.Empty;

        internal ulong? Deadline { get; set; }

        internal ulong? LastNow { get; set; }

        internal ulong? FiredAt { get; set; }

        internal string? Value { get; set; }

        internal string? Reason { get; set; }

        internal ulong Revision { get; set; }

        internal ulong Generation { get; set; }

        internal ulong Required { get; set; }
    }
}
