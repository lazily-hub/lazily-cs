using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The transport-agnostic ingress contract, replayed against EVERY flavor this binding ships —
/// with a ledger the filesystem enforces rather than one a comment asserts.
/// </summary>
/// <remarks>
/// <para>
/// The flavor axis lives in the RUNNER, not the corpus: the fixtures carry a <c>model</c> naming
/// the primitive and no execution-model field, and one <see cref="IIngressModel"/> replays the same
/// JSON against <c>IngressCell</c>, <c>ThreadSafeIngressCell</c>, and <c>AsyncIngressCell</c>.
/// Nothing in the model's op surface is async-coloured, and that is the finding rather than an
/// oversight: an admission decision is a function of the fence, the watermark, the reorder buffer,
/// and the observed clock, so there is nothing to await.
/// </para>
/// <para>
/// Three things keep this suite from reporting green while testing nothing, each a failure mode
/// this family of suites has actually shipped:
/// </para>
/// <list type="number">
/// <item><c>invalidates</c> is asserted PER READER KIND in BOTH directions through a
/// cache-validity probe. A step expecting <c>false</c> fails if the shell invalidated anyway, so
/// over-invalidation is as visible as under-. Never by receipt COUNT: a stale cache recomputes to
/// the right count, so a count-only gate reports green.</item>
/// <item>Every replay returns its step count, and every flavor asserts that count is non-zero and
/// equal to the corpus total. An absence guard proves the fixtures exist on disk; only a positive
/// count proves this binary opened them.</item>
/// <item>The ledger is checked against <c>src/</c> in BOTH directions: a row claiming a flavor
/// whose type is not defined fails, and a type that exists while its row says unshipped fails and
/// names the runner to extend.</item>
/// </list>
/// </remarks>
public sealed class IngressFamilyConformanceTests
{
    private const string Corpus = "ingress";

    /// <summary>
    /// Every fixture the ingress corpus ships, named explicitly.
    /// </summary>
    /// <remarks>
    /// Named rather than globbed so a fixture added upstream and not here is a MISSING REPLAY that
    /// <see cref="TheNamedCorpusMatchesTheCanonicalDirectory"/> fails on, instead of a silently
    /// shorter run.
    /// </remarks>
    private static readonly string[] Fixtures =
    [
        "ingress_backpressure.json",
        "ingress_disconnect_replay.json",
        "ingress_freshness_and_retry.json",
        "ingress_generation_handoff.json",
        "ingress_ordered_delivery.json",
        "ingress_reorder_and_duplication.json",
        "ingress_reorder_window_overflow.json",
    ];

    /// <summary>One ledger row per (primitive, flavor) pair this binding claims.</summary>
    /// <param name="Flavor">The execution flavor.</param>
    /// <param name="TypeName">The type whose presence in <c>src/</c> proves the claim.</param>
    /// <param name="Shipped">Whether this binding claims the flavor.</param>
    private sealed record LedgerRow(string Flavor, string TypeName, bool Shipped);

    private static readonly LedgerRow[] Ledger =
    [
        new("single-threaded", "IngressCell", Shipped: true),
        new("thread-safe", "ThreadSafeIngressCell", Shipped: true),
        new("async", "AsyncIngressCell", Shipped: true),
    ];

    private static readonly (string Flavor, Func<IngressPolicy, MergePolicy<long>, IngressTransportKind, long, IIngressModel> Build)[] Flavors =
    [
        ("single-threaded", static (p, m, t, i) => new SyncIngressModel(p, m, t, i)),
        ("thread-safe", static (p, m, t, i) => new ThreadSafeIngressModel(p, m, t, i)),
        ("async", static (p, m, t, i) => new AsyncIngressModel(p, m, t, i)),
    ];

    // ---------------------------------------------------------------------
    // The gates
    // ---------------------------------------------------------------------

    /// <summary>The corpus resolves, and the named list is exactly the canonical directory.</summary>
    [Fact]
    public void TheNamedCorpusMatchesTheCanonicalDirectory()
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}");
        Assert.Equal(Fixtures.Order(StringComparer.Ordinal), SpecCorpus.FixtureNames(Corpus));
        Assert.Equal(7, Fixtures.Length);

        var total = ExpectedStepTotal();
        Assert.True(
            total >= 30,
            $"the ingress corpus replays only {total} steps; that is not the named schedule set");
    }

    /// <summary>Replays the whole corpus against the single-threaded flavor.</summary>
    [Fact]
    public void SingleThreadedFlavorReplaysTheWholeCorpus() => AssertFlavorReplays("single-threaded");

    /// <summary>Replays the whole corpus against the thread-safe flavor.</summary>
    [Fact]
    public void ThreadSafeFlavorReplaysTheWholeCorpus() => AssertFlavorReplays("thread-safe");

    /// <summary>Replays the whole corpus against the async flavor.</summary>
    /// <remarks>
    /// One test per flavor rather than one loop over all three: a loop short-circuits on the first
    /// failure, so a defect that breaks every flavor would only ever be reported against one of
    /// them, and a defect that breaks ONLY a later flavor would be masked by an earlier failure.
    /// </remarks>
    [Fact]
    public void AsyncFlavorReplaysTheWholeCorpus() => AssertFlavorReplays("async");

    private static void AssertFlavorReplays(string flavor)
    {
        Assert.NotNull(SpecCorpus.Root);
        var expected = ExpectedStepTotal();
        var build = Flavors.Single(pair =>
            string.Equals(pair.Flavor, flavor, StringComparison.Ordinal)).Build;
        var (steps, invalidationChecks) = ReplayCorpus(flavor, build);

        // A positive count is the only thing that proves this binary opened the fixtures. The
        // absence guard proves only that they exist on disk.
        Assert.True(steps > 0, $"{flavor}: replayed zero steps");
        Assert.Equal(expected, steps);

        // Seven reader kinds per step, in both directions, is the contract this corpus exists to
        // pin; a runner that stopped probing would still report the right step count.
        Assert.True(
            invalidationChecks >= 7 * expected,
            $"{flavor}: only {invalidationChecks} invalidation probes for {steps} steps");
    }

    /// <summary>The ledger cannot rot: the filesystem enforces it, in both directions.</summary>
    [Fact]
    public void UnshippedFlavorsAreReallyAbsent()
    {
        var sources = ReadLibrarySources();
        Assert.True(sources.Length > 0, "src/Lazily/*.cs was not readable from the test host");

        foreach (var row in Ledger)
        {
            // `class X<` — the definition, not a doc-comment mention. The space after `class`
            // keeps `class IngressCell<` from matching `class ThreadSafeIngressCell<`.
            var defined = sources.Contains($"class {row.TypeName}<", StringComparison.Ordinal);
            Assert.True(
                defined == row.Shipped,
                $"ledger row '{row.Flavor}' claims shipped={row.Shipped} but " +
                $"`class {row.TypeName}<` defined={defined}; fix the ledger or extend the runner");
        }
    }

    /// <summary>A ledger of nothing-shipped is not coverage.</summary>
    [Fact]
    public void TheLedgerIsNotAllSkips()
    {
        Assert.Equal(3, Ledger.Length);
        Assert.Equal(Ledger.Length, Flavors.Length);
        Assert.Contains(Ledger, row => row.Shipped);
        Assert.Equal(
            Ledger.Select(row => row.Flavor).Order(StringComparer.Ordinal),
            Flavors.Select(pair => pair.Flavor).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The corpus asserts NEGATIVE invalidation, so the probe itself must be able to fail.
    /// </summary>
    /// <remarks>
    /// This pins the probe on every flavor: reading warms the cache, an op that dirties the reader
    /// clears it, and one that does not leaves it warm. Without this, a probe hard-wired to
    /// <c>false</c> would satisfy every <c>invalidates: false</c> expectation in the corpus.
    /// </remarks>
    [Fact]
    public void TheInvalidationProbeDiscriminatesOnEveryFlavor()
    {
        // Collected rather than short-circuited, so a defect visible on one flavor cannot mask the
        // same defect on another.
        var problems = new List<string>();
        foreach (var (flavor, build) in Flavors)
        {
            using var model = build(
                new IngressPolicy(),
                MergePolicy.Sum<long>(),
                IngressTransportKind.EventChannel,
                25);
            const string key = "alpha";

            _ = model.Value(key);
            if (!model.ValueIsValid(key)) problems.Add($"{flavor}: reading must warm the cache");

            model.Admit(new IngressEnvelope<string, long>(key, 1, 0, 0, 1));
            if (model.ValueIsValid(key))
                problems.Add($"{flavor}: a delivery must invalidate the value reader");

            _ = model.Value(key);
            model.Admit(new IngressEnvelope<string, long>(key, 1, 5, 0, 1));
            if (!model.ValueIsValid(key))
                problems.Add($"{flavor}: a buffered envelope must NOT invalidate the value reader");
        }

        Assert.Empty(problems);
    }

    // ---------------------------------------------------------------------
    // The replay
    // ---------------------------------------------------------------------

    private static int ExpectedStepTotal()
    {
        var total = 0;
        foreach (var fixture in Fixtures)
        {
            using var doc = SpecCorpus.Load(Corpus, fixture);
            total += doc.RootElement.GetProperty("steps").GetArrayLength();
        }

        return total;
    }

    private static (int Steps, int InvalidationChecks) ReplayCorpus(
        string flavor,
        Func<IngressPolicy, MergePolicy<long>, IngressTransportKind, long, IIngressModel> build)
    {
        var steps = 0;
        var checks = 0;
        foreach (var fixture in Fixtures)
        {
            using var doc = SpecCorpus.Load(Corpus, fixture);
            var root = doc.RootElement;
            Assert.Equal("Ingress", root.GetProperty("kind").GetString());
            Assert.Equal("IngressCell", root.GetProperty("model").GetString());

            var (fixtureSteps, fixtureChecks) = Replay(root, $"{flavor}/{fixture}", build);
            Assert.True(fixtureSteps > 0, $"{flavor}/{fixture}: replayed zero steps");
            steps += fixtureSteps;
            checks += fixtureChecks;
        }

        return (steps, checks);
    }

    private static (int Steps, int Checks) Replay(
        JsonElement root,
        string label,
        Func<IngressPolicy, MergePolicy<long>, IngressTransportKind, long, IIngressModel> build)
    {
        using var model = build(
            ParsePolicy(root.GetProperty("policy")),
            ParseMerge(root.GetProperty("merge").GetString()!),
            ParseTransport(root.GetProperty("transport").GetString()!),
            root.GetProperty("poll_interval").GetInt64());

        var stepList = root.GetProperty("steps").EnumerateArray().ToList();
        Assert.NotEmpty(stepList);

        // Every key the fixture ever mentions, so a reader exists — and is probed — from the first
        // step. An absent reader would silently pass a `false` invalidation expectation.
        var keys = new List<string>();
        foreach (var step in stepList)
        {
            var op = step.GetProperty("op");
            if (op.TryGetProperty("key", out var opKey) && opKey.ValueKind == JsonValueKind.String)
            {
                var name = opKey.GetString()!;
                if (!keys.Contains(name, StringComparer.Ordinal)) keys.Add(name);
            }

            foreach (var scope in step.GetProperty("expected").GetProperty("scopes").EnumerateObject())
            {
                if (!keys.Contains(scope.Name, StringComparer.Ordinal)) keys.Add(scope.Name);
            }
        }

        Assert.NotEmpty(keys);
        Materialize(model, keys);

        var steps = 0;
        var checks = 0;
        for (var index = 0; index < stepList.Count; index++)
        {
            var step = stepList[index];
            var op = step.GetProperty("op");
            var where = $"{label} step {index} ({op.GetProperty("type").GetString()})";
            var before = SnapshotValidity(model, keys);

            RunOp(model, op, step, where);

            // The validity snapshot is taken before either assertion, so the order below does not
            // change what is measured. Invalidation is asserted FIRST deliberately: it is the
            // claim a receipt COUNT cannot make, and a count assertion that fired first would
            // report the symptom instead of the defect.
            var after = SnapshotValidity(model, keys);
            var expected = FixtureAssertions.Of(step, "expected", where);
            checks += AssertInvalidation(expected, before, after, where);
            AssertState(model, expected, where);
            expected.Verify();
            Materialize(model, keys);
            steps++;
        }

        return (steps, checks);
    }

    private static void RunOp(IIngressModel model, JsonElement op, JsonElement step, string where)
    {
        switch (op.GetProperty("type").GetString())
        {
            case "admit":
                {
                    var admission = model.Admit(new IngressEnvelope<string, long>(
                        op.GetProperty("key").GetString()!,
                        op.GetProperty("generation").GetInt64(),
                        op.GetProperty("sequence").GetInt64(),
                        op.GetProperty("stamped_at").GetInt64(),
                        op.GetProperty("payload").GetInt64()));
                    if (step.TryGetProperty("returns", out var expected))
                        Same(where, "returns", "admission", ParseAdmission(expected, where), admission);
                    break;
                }

            case "open":
                model.Open(op.GetProperty("key").GetString()!, op.GetProperty("generation").GetInt64());
                break;

            case "drain":
                {
                    var drained = model.Drain(op.GetProperty("key").GetString()!);
                    if (step.TryGetProperty("returns", out var expected))
                    {
                        Same(
                            where,
                            "returns",
                            "drained",
                            OptionalInt64(expected.GetProperty("drained")),
                            drained is { HasValue: true } value ? value.Value : null);
                    }

                    break;
                }

            case "suspend":
                {
                    var replay = model.Suspend(op.GetProperty("key").GetString()!);
                    if (step.TryGetProperty("returns", out var expected))
                        Same(where, "returns", "replay",
                            ParseReplay(expected.GetProperty("replay")), replay);
                    break;
                }

            case "reconnect":
                {
                    var replay = model.Reconnect(
                        op.GetProperty("key").GetString()!,
                        op.GetProperty("generation").GetInt64());
                    if (step.TryGetProperty("returns", out var expected))
                        Same(where, "returns", "replay",
                            ParseReplay(expected.GetProperty("replay")), (ReplayRequest?)replay);
                    break;
                }

            case "close":
                model.Close(op.GetProperty("key").GetString()!);
                break;

            case "fail":
                model.Fail(
                    op.GetProperty("key").GetString()!,
                    ParseError(op.GetProperty("error").GetString()!));
                break;

            case "tick":
                model.Tick(op.GetProperty("now").GetInt64());
                break;

            default:
                // An unknown op must FAIL, never skip: a runner that shrugs at an op it does not
                // know reports green while replaying less than the corpus.
                Assert.Fail($"{where}: unknown op");
                break;
        }
    }

    /// <summary>
    /// Reads every reader kind, so the caches are warm and the next step's validity probe measures
    /// THAT step's invalidation and nothing else.
    /// </summary>
    private static void Materialize(IIngressModel model, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            _ = model.Value(key);
            _ = model.Readiness(key);
            _ = model.Authority(key);
            _ = model.Retry(key);
        }

        _ = model.AcceptedLen();
        _ = model.DroppedLen();
        _ = model.ErrorsLen();
        _ = model.Schedule();
    }

    private static ValiditySnapshot SnapshotValidity(IIngressModel model, IReadOnlyList<string> keys)
    {
        var scopes = new Dictionary<string, bool[]>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            scopes[key] =
            [
                model.ValueIsValid(key),
                model.ReadinessIsValid(key),
                model.AuthorityIsValid(key),
                model.RetryIsValid(key),
            ];
        }

        return new ValiditySnapshot(
            scopes,
            [model.AcceptedIsValid(), model.DroppedIsValid(), model.ErrorsIsValid()]);
    }

    private sealed record ValiditySnapshot(
        IReadOnlyDictionary<string, bool[]> Scopes,
        bool[] Receipts);

    private static void AssertState(IIngressModel model, FixtureAssertions expected, string where)
    {
        // Descended at every level (#lzsubblockkeyset): `scopes` is an object of scope keys,
        // each scope's value is an object of state fields, and `authority`/`retry` are objects
        // again. All four key sets were read by NAME, so a field the corpus grows at any level
        // was compared by nothing. The child trackers own them now.
        expected.AssertObjectKey(
            "scopes",
            scopes =>
            {
                foreach (var scope in scopes.EnumerateObject())
                {
                    var key = scope.Name;
                    scopes.AssertObjectKey(key, want =>
                    {
                        var view = model.View(key);
                        Assert.True(view is not null, $"{where}: scope {key} absent");
                        var got = view!.Value;

                        // Every comparison carries `where`: an unlabelled "Expected 0, Actual 1"
                        // names neither the fixture, the step, nor the field, which makes a red
                        // gate useless as evidence.
                        want.AssertKeyWith("lifecycle", v => Same(where, key, "lifecycle",
                            ParseLifecycle(v.GetString()!), got.Lifecycle));
                        want.AssertKeyWith("generation", v => Same(where, key, "generation",
                            v.GetInt64(), got.Generation));
                        want.AssertKeyWith("delivered_through", v => Same(where, key, "watermark",
                            OptionalInt64(v), got.DeliveredThrough));
                        want.AssertKeyWith("buffered", v => Same(where, key, "buffered",
                            v.GetInt32(), got.Buffered));
                        want.AssertKeyWith("consecutive_errors", v => Same(where, key, "consecutive_errors",
                            v.GetInt32(), got.ConsecutiveErrors));
                        want.AssertKeyWith("window", v => Same(where, key, "window",
                            OptionalInt64(v),
                            model.Value(key) is { HasValue: true } window ? window.Value : null));
                        want.AssertKeyWith("readiness", v => Same(where, key, "readiness",
                            ParseReadiness(v.GetString()!), model.Readiness(key)));

                        if (want.GetProperty("authority").ValueKind == JsonValueKind.Null)
                        {
                            want.AssertKeyWith("authority", _ =>
                                Same<IngressAuthority?>(where, key, "authority", null, model.Authority(key)));
                        }
                        else
                        {
                            want.AssertObjectKey("authority", wantAuthority => Same(
                                where, key, "authority",
                                new IngressAuthority(
                                    wantAuthority.AssertKeyInto("generation", v => v.GetInt64()),
                                    wantAuthority.AssertKeyInto("delivered_through", OptionalInt64),
                                    wantAuthority.AssertKeyInto("stamped_at", v => v.GetInt64())),
                                model.Authority(key)));
                        }

                        if (want.GetProperty("retry").ValueKind == JsonValueKind.Null)
                        {
                            want.AssertKeyWith("retry", _ =>
                                Same<IngressRetry?>(where, key, "retry", null, model.Retry(key)));
                        }
                        else
                        {
                            want.AssertObjectKey("retry", wantRetry => Same(
                                where, key, "retry",
                                new IngressRetry(
                                    wantRetry.AssertKeyInto("attempt", v => v.GetInt32()),
                                    wantRetry.AssertKeyInto("backoff", v => v.GetInt64()),
                                    wantRetry.AssertKeyInto("resume_from", v => v.GetInt64())),
                                model.Retry(key)));
                        }
                    });
                }
            });

        expected.AssertObjectKey(
            "receipts",
            receipts =>
            {
                receipts.AssertKeyWith("accepted", v =>
                    Same(where, "receipts", "accepted", v.GetInt32(), model.AcceptedLen()));
                receipts.AssertKeyWith("dropped", v =>
                    Same(where, "receipts", "dropped", v.GetInt32(), model.DroppedLen()));
                receipts.AssertKeyWith("error", v =>
                    Same(where, "receipts", "error", v.GetInt32(), model.ErrorsLen()));
            });
    }

    private static void Same<T>(string where, string subject, string field, T want, T got) =>
        Assert.True(
            EqualityComparer<T>.Default.Equals(want, got),
            $"{where}: {subject}.{field} expected {Describe(want)}, got {Describe(got)}");

    private static string Describe<T>(T value) => value is null ? "null" : value.ToString() ?? "?";

    /// <summary>
    /// Asserts <c>invalidates</c> in both directions and returns how many probes really ran.
    /// </summary>
    /// <remarks>
    /// <c>true</c> means the reader's cache went from valid to invalid across the op; <c>false</c>
    /// means it stayed valid. Asserting only the <c>true</c> direction would let a shell that
    /// clears every reader on every op pass the whole corpus.
    /// </remarks>
    private static int AssertInvalidation(
        FixtureAssertions expected,
        ValiditySnapshot before,
        ValiditySnapshot after,
        string where)
    {
        string[] kinds = ["value", "readiness", "authority", "retry"];
        string[] channels = ["accepted", "dropped", "error"];
        var checks = 0;

        // THREE levels of descent (#lzsubblockkeyset): `invalidates` carries `scopes` and
        // `receipts`, `scopes` carries one object per scope, and each of those carries one
        // key per reader kind. Every one of those key sets was read by NAME before, so a
        // scope, a channel, or a reader kind the corpus grows was compared by nothing at any
        // of the three levels. The child trackers now own all of them.
        expected.AssertObjectKey(
            "invalidates",
            invalidates =>
            {
                invalidates.AssertObjectKey("scopes", scopes =>
                {
                    foreach (var scope in scopes.EnumerateObject())
                    {
                        var key = scope.Name;
                        scopes.AssertObjectKey(key, wantScope =>
                        {
                            var wasValid = before.Scopes[key];
                            var isValid = after.Scopes[key];
                            for (var slot = 0; slot < kinds.Length; slot++)
                            {
                                var at = slot;
                                wantScope.AssertKeyWith(kinds[at], wantKind =>
                                {
                                    var want = wantKind.GetBoolean();
                                    var invalidated = wasValid[at] && !isValid[at];
                                    Assert.True(
                                        invalidated == want,
                                        $"{where}: {key}.{kinds[at]} invalidation expected {want} " +
                                        $"(was valid={wasValid[at]}, now valid={isValid[at]})");
                                });
                                checks++;
                            }
                        });
                    }
                });

                invalidates.AssertObjectKey("receipts", wantReceipts =>
                {
                    for (var slot = 0; slot < channels.Length; slot++)
                    {
                        var at = slot;
                        wantReceipts.AssertKeyWith(channels[at], wantChannel =>
                        {
                            var want = wantChannel.GetBoolean();
                            var invalidated = before.Receipts[at] && !after.Receipts[at];
                            Assert.True(
                                invalidated == want,
                                $"{where}: receipts.{channels[at]} invalidation expected {want} " +
                                $"(was valid={before.Receipts[at]}, now valid={after.Receipts[at]})");
                        });
                        checks++;
                    }
                });
            });

        return checks;
    }

    // ---------------------------------------------------------------------
    // Fixture decoding
    // ---------------------------------------------------------------------

    private static long? OptionalInt64(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();

    private static IngressPolicy ParsePolicy(JsonElement value) => new()
    {
        ReorderWindow = value.GetProperty("reorder_window").GetInt32(),
        FreshnessHorizon = value.GetProperty("freshness_horizon").GetInt64(),
        HighWater = value.GetProperty("high_water").GetInt64(),
        Overflow = ParseOverflow(value.GetProperty("overflow").GetString()!),
        ReceiptCapacity = value.GetProperty("receipt_capacity").GetInt32(),
        RetryBase = value.GetProperty("retry_base").GetInt64(),
        RetryCeiling = value.GetProperty("retry_ceiling").GetInt64(),
    };

    private static MergePolicy<long> ParseMerge(string value) => value switch
    {
        "sum" => MergePolicy.Sum<long>(),
        "keep_latest" => MergePolicy.KeepLatest<long>(),
        "max" => MergePolicy.Max<long>(),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown merge policy"),
    };

    private static RelayOverflow ParseOverflow(string value) => value switch
    {
        "block" => RelayOverflow.Block,
        "drop_newest" => RelayOverflow.DropNewest,
        "drop_oldest" => RelayOverflow.DropOldest,
        "conflate" => RelayOverflow.Conflate,
        "spill" => RelayOverflow.Spill,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown overflow"),
    };

    private static IngressTransportKind ParseTransport(string value) => value switch
    {
        "event_channel" => IngressTransportKind.EventChannel,
        "rpc_triggered" => IngressTransportKind.RpcTriggered,
        "bounded_polling" => IngressTransportKind.BoundedPolling,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown transport"),
    };

    private static IngressError ParseError(string value) => value switch
    {
        "transport_closed" => IngressError.TransportClosed,
        "decode_failed" => IngressError.DecodeFailed,
        "authority_lost" => IngressError.AuthorityLost,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown ingress error"),
    };

    private static IngressDropReason ParseDropReason(string value) => value switch
    {
        "stale_generation" => IngressDropReason.StaleGeneration,
        "duplicate_sequence" => IngressDropReason.DuplicateSequence,
        "duplicate_buffered" => IngressDropReason.DuplicateBuffered,
        "reorder_window_overflow" => IngressDropReason.ReorderWindowOverflow,
        "expired" => IngressDropReason.Expired,
        "backpressure" => IngressDropReason.Backpressure,
        "scope_closed" => IngressDropReason.ScopeClosed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown drop reason"),
    };

    private static IngressLifecycle ParseLifecycle(string value) => value switch
    {
        "opening" => IngressLifecycle.Opening,
        "live" => IngressLifecycle.Live,
        "suspended" => IngressLifecycle.Suspended,
        "closed" => IngressLifecycle.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown lifecycle"),
    };

    private static IngressReadiness ParseReadiness(string value) => value switch
    {
        "unknown" => IngressReadiness.Unknown,
        "warming" => IngressReadiness.Warming,
        "ready" => IngressReadiness.Ready,
        "stale" => IngressReadiness.Stale,
        "suspended" => IngressReadiness.Suspended,
        "closed" => IngressReadiness.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown readiness"),
    };

    private static ReplayRequest? ParseReplay(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null
            ? null
            : new ReplayRequest(
                value.GetProperty("generation").GetInt64(),
                value.GetProperty("from_sequence").GetInt64());

    private static IngressAdmission ParseAdmission(JsonElement value, string where) =>
        value.GetProperty("admission").GetString() switch
        {
            "accepted" => IngressAdmission.Accepted(
                value.GetProperty("delivered_through").GetInt64()),
            "conflated" => IngressAdmission.Conflated(
                value.GetProperty("delivered_through").GetInt64()),
            "buffered" => IngressAdmission.Buffered(value.GetProperty("gap_from").GetInt64()),
            "generation_handoff" => IngressAdmission.GenerationHandoff(
                value.GetProperty("from").GetInt64(),
                value.GetProperty("to").GetInt64()),
            "dropped" => IngressAdmission.Dropped(
                ParseDropReason(value.GetProperty("reason").GetString()!)),
            "blocked" => IngressAdmission.Blocked,
            var other => throw new InvalidOperationException($"{where}: unknown admission '{other}'"),
        };

    // ---------------------------------------------------------------------
    // The ledger's filesystem evidence
    // ---------------------------------------------------------------------

    private static string ReadLibrarySources()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, "src", "Lazily"));
            if (Directory.Exists(candidate))
            {
                var joined = new System.Text.StringBuilder();
                foreach (var file in Directory.GetFiles(candidate, "*.cs").Order(StringComparer.Ordinal))
                {
                    joined.Append(File.ReadAllText(file));
                }

                return joined.ToString();
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return string.Empty;
    }
}

// Mutation-check record (#designimplementtransport). Each deliberate defect was introduced, the
// gate run (`dotnet test --filter FullyQualifiedName~Ingress`), and the defect reverted with an
// mtime bump — a restore that preserves mtime lets the build reuse the MUTATED assembly and report
// a false green. All seven were killed:
//
// * fence checked after dedupe -> ingress_generation_handoff step 2 fails on all three flavors:
//   `returns.admission expected ... Reason = StaleGeneration, got ... Reason = DuplicateSequence`.
// * handoff keeps the superseded window -> ingress_generation_handoff step 3 fails on all three
//   flavors: `alpha.value invalidation expected True (was valid=True, now valid=True)`, and the
//   handoff window reads 14 (5 folded into 9) instead of 9.
// * `Buffered` marks every reader dirty -> every `invalidates: false` step fails, on all three
//   flavors (first: ingress_freshness_and_retry step 4, `alpha.value expected False`).
// * `Tick` marks readiness unconditionally -> ingress_freshness_and_retry step 1 fails on all three
//   flavors: the in-horizon tick reports `alpha.readiness invalidation expected False`.
// * `Block` advances the watermark -> ingress_backpressure step 1 fails on all three flavors
//   (`alpha.watermark expected 0, got 1`), and BlockOverflowRefusesLosslessly's retry stops being
//   an in-order accept.
// * thread-safe `Apply` writes outside `Batch` -> only ThreadSafeHandoffNeverShowsANewValueWith-
//   StaleAuthority fails: two effect runs for one admission, exposing the intermediate
//   `(9, 1)` — new value, OLD authority. No corpus assertion can see this, which is why the gate
//   exists per flavor.
// * the error-receipt channel is never cleared -> ingress_disconnect_replay step 4 fails on
//   `receipts.error invalidation expected True (was valid=True, now valid=True)`. This is the
//   reason `invalidates` is asserted per CHANNEL and before the counts: the count assertion is a
//   symptom that a shell recomputing to the right total would hide.
