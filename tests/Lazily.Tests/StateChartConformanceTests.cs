using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Replays the canonical <c>statechart</c> corpus against <see cref="StateChart"/>.
/// </summary>
/// <remarks>
/// Every step asserts the ACCEPTANCE flag alongside the resulting configuration, and that pairing is
/// what makes the corpus discriminating. A chart that treats an unhandled event as a self-transition
/// lands on exactly the right active states — nothing moved — and only the rejection flag separates
/// "no transition was enabled" from "a transition ran and returned here". The same holds for a
/// failed guard.
/// </remarks>
public sealed class StateChartConformanceTests
{
    private const string Corpus = "statechart";

    /// <summary>Fixtures this binding cannot execute, with the surface that blocks each.</summary>
    private static readonly Dictionary<string, string> Unsupported = [];

    /// <summary>Assertions this binding does not satisfy, keyed <c>fixture#step:key</c>.</summary>
    private static readonly Dictionary<string, string> KnownDivergences = [];
    private static readonly HashSet<string> NegativeFixtures =
        ["malformed_rejected.json"];

    [Fact]
    public void ReplaysTheWholeCorpusWithNoUnexpectedDivergence()
    {
        Assert.True(
            SpecCorpus.Root is not null,
            $"lazily-spec conformance corpus not found at {SpecCorpus.SiblingRelativePath}; " +
            "clone lazily-spec as a sibling. A skip here would report green while testing nothing.");

        var names = SpecCorpus.FixtureNames(Corpus);
        Assert.NotEmpty(names);

        var replayed = new List<string>();
        var divergences = new List<string>();
        var assertions = 0;

        foreach (var name in names)
        {
            if (NegativeFixtures.Contains(name)) continue;
            if (Unsupported.ContainsKey(name)) continue;

            using var doc = SpecCorpus.Load(Corpus, name);
            var fx = doc.RootElement;

            void Check(string key, object? got, object? want)
            {
                assertions++;
                if (!Equals(got?.ToString(), want?.ToString())) divergences.Add($"{name}:{key} — got {got}, want {want}");
            }

            var ctx = new Context();
            var chart = new StateChart(ctx, ReadChart(fx.GetProperty("chart")));

            // A real reader of the chart, counting its own runs. A rejected event must invalidate
            // NOBODY — that is the difference between "unhandled" and "self-transition", and it is
            // invisible in the configuration itself.
            var matchRuns = 0;
            var leaves = ctx.Slot(c =>
            {
                matchRuns++;
                return string.Join(",", chart.ActiveLeaves(c));
            });
            _ = leaves.Get();

            Check("initial_active", ActiveOf(chart), ExpectedActive(fx.GetProperty("initial_active")));

            if (fx.TryGetProperty("initial_actions", out var initialActions))
            {
                Check(
                    "initial_actions",
                    string.Join(",", chart.LastActions),
                    string.Join(",", initialActions.EnumerateArray().Select(x => x.GetString()!)));
            }

            var stepIndex = 0;
            foreach (var step in fx.GetProperty("steps").EnumerateArray())
            {
                var guards = step.TryGetProperty("guards", out var g)
                    ? g.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetBoolean(), StringComparer.Ordinal)
                    : null;

                var runsBefore = matchRuns;
                var accepted = chart.Send(step.GetProperty("event").GetString()!, guards);
                _ = leaves.Get();

                var where = $"#{stepIndex}";
                Check($"{where}:accepted", accepted, step.GetProperty("accepted").GetBoolean());
                Check($"{where}:active", ActiveOf(chart), ExpectedActive(step.GetProperty("active")));

                if (step.TryGetProperty("actions", out var wantActions))
                {
                    Check(
                        $"{where}:actions",
                        string.Join(",", chart.LastActions),
                        string.Join(",", wantActions.EnumerateArray().Select(x => x.GetString()!)));
                }

                if (step.TryGetProperty("matches", out var wantMatches))
                {
                    foreach (var m in wantMatches.EnumerateObject())
                    {
                        Check($"{where}:matches.{m.Name}", chart.Matches(m.Name), m.Value.GetBoolean());
                    }
                }

                // A rejected event leaves the reactive graph untouched; an accepted one moves it.
                Check($"{where}:reader_invalidated", matchRuns > runsBefore, accepted);

                stepIndex++;
            }

            replayed.Add(name);
        }

        Assert.Equal(
            names.Where(n => !Unsupported.ContainsKey(n) && !NegativeFixtures.Contains(n))
                .Order(StringComparer.Ordinal).ToArray(),
            replayed.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            KnownDivergences.Values.Order(StringComparer.Ordinal).ToArray(),
            divergences.Order(StringComparer.Ordinal).ToArray());
        Assert.NotEmpty(replayed);
        Assert.True(assertions > 0, "replayed the corpus but checked nothing");
    }

    private static string ActiveOf(StateChart chart) => string.Join(",", chart.ActiveLeaves());

    [Fact]
    public void RejectsEveryMalformedCanonicalChart()
    {
        using var doc = SpecCorpus.Load(Corpus, "malformed_rejected.json");
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.NotEmpty(cases);
        foreach (var scenario in cases)
        {
            var name = scenario.GetProperty("name").GetString();
            Assert.ThrowsAny<Exception>(
                () => ReadChart(scenario.GetProperty("chart")));
            Assert.False(string.IsNullOrWhiteSpace(name));
        }
    }

    /// <summary>The fixture writes a single leaf as a string and parallel leaves as an array.</summary>
    private static string ExpectedActive(JsonElement el) =>
        el.ValueKind is JsonValueKind.Array
            ? string.Join(",", el.EnumerateArray().Select(x => x.GetString()!).Order(StringComparer.Ordinal))
            : el.GetString()!;

    private static ChartDef ReadChart(JsonElement chart)
    {
        var topInitial = chart.GetProperty("initial").GetString()
            ?? throw new InvalidOperationException("chart.initial must be a string");
        var states = new List<KeyValuePair<string, StateDef>>();
        foreach (var s in chart.GetProperty("states").EnumerateObject())
        {
            var v = s.Value;
            var on = new Dictionary<string, Transition>(StringComparer.Ordinal);
            if (v.TryGetProperty("on", out var onEl))
            {
                foreach (var t in onEl.EnumerateObject()) on[t.Name] = ReadTransition(t.Value);
            }

            states.Add(new(s.Name, new StateDef
            {
                DeclaredKind = v.TryGetProperty("kind", out var kind) ? kind.GetString() : null,
                Parent = v.TryGetProperty("parent", out var p) ? p.GetString() : null,
                Initial = v.TryGetProperty("initial", out var i) ? i.GetString() : null,
                Parallel = v.TryGetProperty("parallel", out var par) && par.GetBoolean(),
                History = v.TryGetProperty("history", out var h) ? h.GetString() : null,
                Default = v.TryGetProperty("default", out var d) ? d.GetString() : null,
                Final = v.TryGetProperty("final", out var f) && f.GetBoolean(),
                Entry = ReadActions(v, "entry"),
                Exit = ReadActions(v, "exit"),
                On = on,
            }));
        }

        // The corpus always declares an explicit `root`; fall back to the chart's `initial` only if
        // a future fixture omits it, rather than silently picking an arbitrary state.
        var root = states.Any(s => string.Equals(s.Key, "root", StringComparison.Ordinal))
            ? "root"
            : topInitial;
        if (!states.Any(s => string.Equals(s.Key, topInitial, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"chart.initial names undeclared state '{topInitial}'");
        }
        return new ChartDef(root, states);
    }

    /// <summary>A transition is either a bare target string or an object with guard/action/internal.</summary>
    private static Transition ReadTransition(JsonElement el)
    {
        if (el.ValueKind is JsonValueKind.String) return new Transition(el.GetString()!, null, [], false);
        return new Transition(
            el.GetProperty("target").GetString()!,
            el.TryGetProperty("guard", out var g) ? g.GetString() : null,
            ReadActions(el, "action"),
            el.TryGetProperty("internal", out var i) && i.GetBoolean());
    }

    private static IReadOnlyList<string> ReadActions(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var a)) return [];
        return a.ValueKind is JsonValueKind.String
            ? [a.GetString()!]
            : [.. a.EnumerateArray().Select(x => x.GetString()!)];
    }
}
