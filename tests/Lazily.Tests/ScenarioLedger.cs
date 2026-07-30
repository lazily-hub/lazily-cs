using System.Globalization;
using System.Text.Json;

namespace Lazily.Tests;

/// <summary>
/// The <c>scenarios</c> array of one fixture, wrapped so that reaching a scenario RECORDS
/// it in the runtime replay ledger (<c>#lzscenariocoverage</c>).
/// </summary>
/// <remarks>
/// <para>
/// A fixture carrying several named scenarios can be PARTIALLY replayed and nothing
/// notices. The coverage guard in <see cref="SpecCorpus"/> asks only whether the FILE was
/// opened, and one scenario is enough to answer yes. The key trackers in
/// <see cref="FixtureAssertions"/> only bind blocks a runner actually reaches, so a scenario
/// that is never reached contributes no unconsumed key and no unasserted key. Skipping a
/// whole scenario is invisible to a guard that only inspects the scenarios you ran. This is
/// the rung below both of them: every scenario in the fixture was REPLAYED.
/// </para>
/// <para>
/// The ledger is a RECORDING, not a declaration. A hand-authored list of "scenarios this
/// runner covers" is the thing being guarded against — it is a claim, and a claim rots.
/// Recording happens at the point of replay: the indexer and the iterators here are the only
/// way to get a scenario out of the set, so a runner cannot reach one without the ledger
/// seeing it, and a runner that never reaches one records nothing.
/// </para>
/// <para>
/// Verification is two-directional and lives in <c>scripts/check-conformance-coverage.sh</c>,
/// beside the <c>KNOWN_UNCOVERED</c> fixture allowlist, so there is ONE place to read what
/// this binding does not prove. It compares the ledger against the scenario ids present in
/// the fixture on disk — read independently of this runner — and fails on a scenario that was
/// never replayed, on an excuse for a scenario the same run DID replay, and on an excuse
/// naming an id the fixture does not carry.
/// </para>
/// </remarks>
public sealed class ScenarioSet
{
    private readonly JsonElement[] _scenarios;
    private readonly string _fixture;

    internal ScenarioSet(JsonElement scenarios, string fixture)
    {
        _scenarios = scenarios.ValueKind == JsonValueKind.Array
            ? [.. scenarios.EnumerateArray()]
            : [];
        _fixture = fixture;
    }

    /// <summary>The corpus-relative fixture path, e.g. <c>reliable-sync/liveness_orset_lww.json</c>.</summary>
    public string FixturePath => _fixture;

    /// <summary>How many scenarios the fixture carries.</summary>
    public int Count => _scenarios.Length;

    /// <summary>
    /// Resolve a scenario's ledger id: <c>id</c>, else <c>name</c>, else the positional
    /// index spelled <c>#&lt;n&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The corpus is not uniform — 28 fixtures identify a scenario by <c>name</c>, the three
    /// <c>stdlib</c> ones by <c>id</c>, and <c>collections/mergecell_algebra.json</c> carries
    /// no identifier at all. The positional fallback exists so this guard is not blocked on a
    /// shared-corpus edit; the coverage script REPORTS every id that fell back, because that
    /// visibility is what makes the corpus gap fixable upstream later. Resolution order is
    /// fixed and identical in every binding.
    /// </remarks>
    public static string IdOf(JsonElement scenario, int index)
    {
        if (scenario.ValueKind == JsonValueKind.Object)
        {
            if (scenario.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString()!;
            }

            if (scenario.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                return name.GetString()!;
            }
        }

        return "#" + index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The ledger id of the scenario at <paramref name="index"/>, WITHOUT recording it.</summary>
    /// <remarks>
    /// For labels and messages. Naming a scenario is not replaying it, so this deliberately
    /// leaves no ledger entry.
    /// </remarks>
    public string IdAt(int index) => IdOf(_scenarios[index], index);

    /// <summary>The scenario at <paramref name="index"/>, recorded as replayed.</summary>
    public JsonElement this[int index]
    {
        get
        {
            var scenario = _scenarios[index];
            SpecCorpus.RecordScenario(_fixture, IdOf(scenario, index));
            return scenario;
        }
    }

    /// <summary>Every scenario in order, recorded one at a time as it is yielded.</summary>
    /// <remarks>
    /// Lazy on purpose. Materializing the whole array up front would record scenarios a
    /// runner then walks past — which is exactly the defect being guarded — so a consumer
    /// that stops early records only what it actually took.
    /// </remarks>
    public IEnumerable<JsonElement> All()
    {
        for (var index = 0; index < _scenarios.Length; index++)
        {
            yield return this[index];
        }
    }

    /// <summary><see cref="All"/> with each scenario's position and ledger id.</summary>
    public IEnumerable<(int Index, string Id, JsonElement Scenario)> Indexed()
    {
        for (var index = 0; index < _scenarios.Length; index++)
        {
            yield return (index, IdAt(index), this[index]);
        }
    }
}
