using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The canonical generic effect-CAS corpus (<c>#lzeffectcas</c>), replayed against
/// <see cref="EffectCasCore{TKey,TAuthority,TPayload,TReceipt,TError}"/>.
/// </summary>
/// <remarks>
/// Every scenario is reached through <see cref="SpecCorpus.Scenarios"/> so the scenario ledger
/// books it on the first PAYLOAD read; every step's <c>returns</c> and every scenario's
/// <c>final</c> are compared structurally against what the core actually returned and holds, in
/// both directions. Executed steps must equal loaded steps exactly, and an unknown op fails.
/// </remarks>
public sealed class EffectCasConformanceTests
{
    private const string Corpus = "egress";
    private const string Fixture = "effect_cas.json";

    [Fact]
    public void CoreReplaysCanonicalEffectCasCorpus()
    {
        using var document = SpecCorpus.Load(Corpus, Fixture);
        var root = document.RootElement;
        Assert.Equal("EffectCas", root.GetProperty("kind").GetString());
        Assert.Equal("EffectCasCore", root.GetProperty("model").GetString());

        var loaded = 0;
        var executed = 0;
        var scenarios = 0;
        foreach (var scenario in SpecCorpus.Scenarios(root, Corpus, Fixture).All())
        {
            var steps = scenario.GetProperty("steps");
            loaded += steps.GetArrayLength();
            var core = new EffectCasCore<string, long, string, string, string>(
                scenario.GetProperty("generation").GetInt64());
            var attempts = new Dictionary<string, EffectAttemptIdentity>(StringComparer.Ordinal);
            var index = 0;
            foreach (var step in steps.EnumerateArray())
            {
                var where = $"{Corpus}/{Fixture} scenario {scenarios} step {index}";
                FixtureAssertions.Deep(
                    step.GetProperty("returns"),
                    $"{where} returns",
                    Run(core, attempts, step, where));
                executed++;
                index++;
            }

            FixtureAssertions.Deep(scenario.GetProperty("final"), $"{Corpus}/{Fixture} scenario {scenarios} final", Final(core));
            scenarios++;
        }

        Assert.True(scenarios > 0, "effect_cas.json replayed zero scenarios");
        CorpusSteps.AssertAllExecuted($"{Corpus}/{Fixture}", loaded, executed);
    }

    /// <summary>
    /// Pins the part of the contract the corpus only reaches through scenario shapes: a retry is
    /// gated by the LOGICAL clock, and a newer desire wins over a waiting retry.
    /// </summary>
    [Fact]
    public void RetryUsesTheLogicalClockAndNewerDesireWins()
    {
        var core = new EffectCasCore<string, long, string, string, string>(1);
        core.UpsertDesired(Intent("old", 1, "doc", 1, "write"));
        var old = core.Claim("doc", 1, 0).Claim.Attempt!;
        Assert.Equal(
            new EffectCasFailureResult(EffectCasFailureKind.Retrying, RetryAt: 9),
            core.RecordRetryable("doc", old.Identity, "busy", 9).Failure);
        var waiting = core.Claim("doc", 1, 8).Claim;
        Assert.Equal(EffectCasClaimKind.Waiting, waiting.Kind);
        Assert.Equal(9, waiting.RetryAt);
        Assert.Empty(core.PendingKeys(8));
        Assert.Equal(["doc"], core.PendingKeys(9));

        core.UpsertDesired(Intent("new", 2, "doc", 2, "write"));
        Assert.Equal("new", core.Claim("doc", 1, 8).Claim.Attempt!.Identity.Effect.Value);
    }

    /// <summary>Upsert conflict outcomes the corpus does not exercise.</summary>
    [Fact]
    public void UpsertRefusesStaleConflictingAndAlreadyAppliedEpochs()
    {
        var core = new EffectCasCore<string, long, string, string, string>(1);
        Assert.Equal(EffectCasUpsertKind.Accepted, core.UpsertDesired(Intent("a", 2, "doc", 0, "w")).Upsert.Kind);
        Assert.Equal(EffectCasUpsertKind.Unchanged, core.UpsertDesired(Intent("a", 2, "doc", 0, "w")).Upsert.Kind);
        Assert.Equal(EffectCasUpsertKind.EpochConflict, core.UpsertDesired(Intent("b", 2, "doc", 0, "w")).Upsert.Kind);
        Assert.Equal(
            new EffectCasUpsertResult(EffectCasUpsertKind.StaleEpoch, Current: 2),
            core.UpsertDesired(Intent("c", 1, "doc", 0, "w")).Upsert);

        var attempt = core.Claim("doc", 1, 0).Claim.Attempt!;
        Assert.Equal(EffectCasAppliedKind.Completed, core.RecordApplied("doc", attempt.Identity, "r", 1).Applied.Kind);
        Assert.Equal(
            new EffectCasUpsertResult(EffectCasUpsertKind.AlreadyApplied, AppliedThrough: 2),
            core.UpsertDesired(Intent("d", 2, "doc", 0, "w")).Upsert);
        Assert.Throws<ArgumentException>(() => new EffectPlan<string>([]));
        Assert.Throws<ArgumentException>(() => new EffectIdentity(""));
    }

    /// <summary>
    /// A re-sent intent is compared with the ORIGINAL submitted intent, never with the expected
    /// authority a completed plan step advanced.
    /// </summary>
    [Fact]
    public void SameEpochResendComparesTheOriginalIntentNotTheAdvancedAuthority()
    {
        var core = new EffectCasCore<string, long, string, string, string>(1);
        core.UpsertDesired(Intent("plan", 1, "doc", 10, "canonical", "snapshot"));
        var first = core.Claim("doc", 1, 0).Claim.Attempt!;
        Assert.Equal(EffectCasAppliedKind.StepApplied, core.RecordApplied("doc", first.Identity, "r0", 11).Applied.Kind);
        Assert.Equal(11, core.State("doc")!.Pending!.Expected);

        Assert.Equal(EffectCasUpsertKind.Unchanged, core.UpsertDesired(Intent("plan", 1, "doc", 10, "canonical", "snapshot")).Upsert.Kind);
        Assert.Equal(EffectCasUpsertKind.EpochConflict, core.UpsertDesired(Intent("plan", 1, "doc", 11, "canonical", "snapshot")).Upsert.Kind);

        _ = core.Claim("doc", 1, 0);
        Assert.Equal(EffectCasUpsertKind.Unchanged, core.UpsertDesired(Intent("plan", 1, "doc", 10, "canonical", "snapshot")).Upsert.Kind);
    }

    private static EffectIntent<string, long, string> Intent(
        string identity, long epoch, string key, long expected, params string[] plan) =>
        new(new EffectIdentity(identity), epoch, key, expected, new EffectPlan<string>(plan));

    private static JsonObject Run(
        EffectCasCore<string, long, string, string, string> core,
        Dictionary<string, EffectAttemptIdentity> attempts,
        JsonElement step,
        string where)
    {
        var op = step.GetProperty("op");
        string Key() => op.GetProperty("key").GetString()!;
        EffectAttemptIdentity Saved()
        {
            var label = op.GetProperty("attempt").GetString()!;
            Assert.True(attempts.TryGetValue(label, out var identity), $"{where}: no saved attempt `{label}`");
            return identity;
        }

        switch (op.GetProperty("type").GetString())
        {
            case "upsert":
                return Upsert(core.UpsertDesired(new EffectIntent<string, long, string>(
                    new EffectIdentity(op.GetProperty("identity").GetString()!),
                    op.GetProperty("epoch").GetInt64(),
                    Key(),
                    op.GetProperty("expected").GetInt64(),
                    new EffectPlan<string>(op.GetProperty("plan").EnumerateArray().Select(p => p.GetString()!)))).Upsert);
            case "claim":
                {
                    var claim = core.Claim(
                        Key(),
                        op.GetProperty("generation").GetInt64(),
                        op.GetProperty("now").GetInt64()).Claim;
                    if (step.TryGetProperty("save", out var save))
                    {
                        Assert.True(claim.Attempt is not null, $"{where}: a saved claim must succeed");
                        attempts[save.GetString()!] = claim.Attempt!.Identity;
                    }

                    return Claim(claim);
                }

            case "applied":
                return Applied(core.RecordApplied(
                    Key(),
                    Saved(),
                    op.GetProperty("receipt").GetString()!,
                    op.GetProperty("authority").GetInt64()).Applied);
            case "superseded":
                {
                    // The fixture asserts the backend saw ZERO mutation on an authority mismatch.
                    // The backend here is the fixture's own model of it, so the claim is checked
                    // as the fixture states it rather than skipped.
                    Assert.Equal(
                        op.GetProperty("backend_before").GetInt64(),
                        op.GetProperty("backend_after").GetInt64());
                    return Failure(core.RecordSuperseded(Key(), Saved(), op.GetProperty("observed").GetInt64()).Failure);
                }

            case "retryable":
                return Failure(core.RecordRetryable(
                    Key(),
                    Saved(),
                    op.GetProperty("error").GetString()!,
                    op.GetProperty("retry_at").GetInt64()).Failure);
            case "rejected":
                return Failure(core.RecordRejected(Key(), Saved(), op.GetProperty("error").GetString()!).Failure);
            case "reconnect":
                return Reconnect(core.Reconnect(op.GetProperty("generation").GetInt64()).Reconnect);
            case var other:
                throw new Xunit.Sdk.XunitException($"{where}: unknown effect-CAS op `{other}`");
        }
    }

    private static JsonObject Final(EffectCasCore<string, long, string, string, string> core)
    {
        var output = new JsonObject();
        foreach (var (key, state) in core.Snapshot().Keys)
        {
            output[key] = new JsonObject
            {
                ["pending_identity"] = state.Pending?.Intent.Identity.Value,
                ["inflight_identity"] = state.Inflight?.Identity.Effect.Value,
                ["applied_through"] = state.AppliedThrough,
                ["terminal"] = state.Terminal?.Kind switch
                {
                    EffectTerminalKind.Applied => "applied",
                    EffectTerminalKind.Superseded => "superseded",
                    EffectTerminalKind.Rejected => "rejected",
                    _ => null,
                },
                ["receipt_count"] = state.Terminal?.Receipts.Count ?? 0,
                ["retry_at"] = state.Retry?.RetryAt,
            };
        }

        return output;
    }

    private static JsonObject Upsert(EffectCasUpsertResult result) => result.Kind switch
    {
        EffectCasUpsertKind.Accepted => new() { ["upsert"] = "accepted" },
        EffectCasUpsertKind.Unchanged => new() { ["upsert"] = "unchanged" },
        EffectCasUpsertKind.AlreadyApplied => new() { ["upsert"] = "already_applied", ["applied_through"] = result.AppliedThrough },
        EffectCasUpsertKind.StaleEpoch => new() { ["upsert"] = "stale_epoch", ["current"] = result.Current },
        EffectCasUpsertKind.EpochConflict => new() { ["upsert"] = "epoch_conflict" },
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static JsonObject Claim(EffectCasClaimResult<string, long, string, string> result) => result.Kind switch
    {
        EffectCasClaimKind.Claimed => new()
        {
            ["claim"] = "claimed",
            ["identity"] = result.Attempt!.Identity.Effect.Value,
            ["step"] = result.Attempt.Identity.Step,
            ["attempt"] = result.Attempt.Identity.Attempt,
            ["expected"] = result.Attempt.Expected,
            ["payload"] = result.Attempt.Payload,
        },
        EffectCasClaimKind.Empty => new() { ["claim"] = "empty" },
        EffectCasClaimKind.Busy => new() { ["claim"] = "busy" },
        EffectCasClaimKind.Waiting => new() { ["claim"] = "waiting", ["retry_at"] = result.RetryAt },
        EffectCasClaimKind.StaleGeneration => new() { ["claim"] = "stale_generation", ["current"] = result.Current },
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static JsonObject Applied(EffectCasAppliedResult result) => result.Kind switch
    {
        EffectCasAppliedKind.StepApplied => new() { ["applied"] = "step_applied", ["next_step"] = result.NextStep },
        EffectCasAppliedKind.Completed => new() { ["applied"] = "completed", ["applied_through"] = result.AppliedThrough },
        EffectCasAppliedKind.Superseded => new() { ["applied"] = "superseded" },
        EffectCasAppliedKind.Duplicate => new() { ["applied"] = "duplicate" },
        EffectCasAppliedKind.UnknownAttempt => new() { ["applied"] = "unknown_attempt" },
        EffectCasAppliedKind.StaleGeneration => new() { ["applied"] = "stale_generation", ["current"] = result.Current },
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static JsonObject Failure(EffectCasFailureResult result) => result.Kind switch
    {
        EffectCasFailureKind.Superseded => new() { ["failure"] = "superseded" },
        EffectCasFailureKind.Retrying => new() { ["failure"] = "retrying", ["retry_at"] = result.RetryAt },
        EffectCasFailureKind.Rejected => new() { ["failure"] = "rejected" },
        EffectCasFailureKind.UnknownAttempt => new() { ["failure"] = "unknown_attempt" },
        EffectCasFailureKind.StaleGeneration => new() { ["failure"] = "stale_generation", ["current"] = result.Current },
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static JsonObject Reconnect(EffectCasReconnectResult result) => result.Kind switch
    {
        EffectCasReconnectKind.Advanced => new()
        {
            ["reconnect"] = "advanced",
            ["generation"] = result.Generation,
            ["requeued"] = result.Requeued,
            ["superseded"] = result.Superseded,
        },
        EffectCasReconnectKind.Unchanged => new() { ["reconnect"] = "unchanged", ["generation"] = result.Generation },
        EffectCasReconnectKind.StaleGeneration => new() { ["reconnect"] = "stale_generation", ["current"] = result.Generation },
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };
}
