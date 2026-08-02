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

    /// <summary>
    /// Keys that IDENTIFY or narrate a scenario rather than drive one
    /// (<c>#lzscenariobodyskip</c>).
    /// </summary>
    /// <remarks>
    /// Reading only these is looking at the LABEL, not replaying: a dispatch chain that
    /// reads <c>name</c>, matches no arm and falls through has replayed nothing, and a
    /// by-name lookup walks past every scenario ahead of its match. Booking on those reads
    /// is what let a skipped body book itself. No scenario in the corpus carries only these,
    /// so every one is reachable through some payload key.
    /// </remarks>
    public static readonly IReadOnlySet<string> LabelKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "comment", "description", "id", "label", "name", "note", "notes", "reason", "why",
    };

    /// <summary>The scenario at <paramref name="index"/>, booked when its PAYLOAD is read.</summary>
    public Scenario this[int index] => new(_scenarios[index], _fixture, IdOf(_scenarios[index], index));

    /// <summary>Every scenario in order.</summary>
    /// <remarks>
    /// Lazy on purpose, and no longer booking at the yield (<c>#lzscenariobodyskip</c>).
    /// An iterator cannot tell a loop body that ran from one that skipped past — both just
    /// come back for the next item — so booking here called a skipped scenario covered,
    /// which is the defect this rung exists for. Each <see cref="Scenario"/> books itself
    /// when the runner reads its payload.
    /// </remarks>
    public IEnumerable<Scenario> All()
    {
        for (var index = 0; index < _scenarios.Length; index++)
        {
            yield return this[index];
        }
    }

    /// <summary><see cref="All"/> with each scenario's position and ledger id.</summary>
    public IEnumerable<(int Index, string Id, Scenario Scenario)> Indexed()
    {
        for (var index = 0; index < _scenarios.Length; index++)
        {
            yield return (index, IdAt(index), this[index]);
        }
    }
}

/// <summary>
/// One scenario, booked as replayed on the first read of its PAYLOAD
/// (<c>#lzscenariobodyskip</c>).
/// </summary>
/// <remarks>
/// <para>
/// Yielding is not replaying. The ledger used to book at the point <see cref="ScenarioSet"/>
/// handed a scenario over, which cannot distinguish a loop body that ran from one that
/// skipped past. lazily-py proved that empirically against the contract's own probe: with
/// yield-time booking, a <c>continue</c> at the top of the loop body still booked the
/// scenario and the replay guard stayed silent.
/// </para>
/// <para>
/// Booking on payload access tells them apart. The <c>steps</c>/<c>ops</c>/<c>frames</c>/
/// <c>expect</c> of a scenario are read only by a runner about to replay it, while
/// <c>id</c> and <c>name</c> are what a skip reads on its way past — see
/// <see cref="ScenarioSet.LabelKeys"/>.
/// </para>
/// </remarks>
public readonly struct Scenario : IEquatable<Scenario>
{
    private readonly JsonElement _scenario;
    private readonly string _fixture;
    private readonly string _id;

    internal Scenario(JsonElement scenario, string fixture, string id)
    {
        _scenario = scenario;
        _fixture = fixture;
        _id = id;
    }

    private void Book() => SpecCorpus.RecordScenario(_fixture, _id);

    private void Touch(string key)
    {
        if (!ScenarioSet.LabelKeys.Contains(key))
        {
            Book();
        }
    }

    /// <summary>The ledger id. A label read: never books.</summary>
    public string Id => _id;

    /// <summary>The raw element WITHOUT booking, for inspecting a label before deciding to replay.</summary>
    /// <remarks>
    /// Use it only where a read really is a look. Reaching the payload through this is the
    /// skip this rung exists to catch.
    /// </remarks>
    public JsonElement Peek => _scenario;

    /// <summary>The whole scenario, BOOKED — for handing the payload to a replay helper.</summary>
    /// <remarks>
    /// Reaching for the entire object is the strongest statement a runner can make that it is
    /// about to replay this scenario, so it books unconditionally. The implicit conversion to
    /// <see cref="JsonElement"/> goes through here for the same reason.
    /// </remarks>
    public JsonElement Value
    {
        get
        {
            Book();
            return _scenario;
        }
    }

    public JsonElement this[string key]
    {
        get
        {
            Touch(key);
            return _scenario.GetProperty(key);
        }
    }

    public JsonElement GetProperty(string key)
    {
        Touch(key);
        return _scenario.GetProperty(key);
    }

    public bool TryGetProperty(string key, out JsonElement value)
    {
        Touch(key);
        return _scenario.TryGetProperty(key, out value);
    }

    public JsonValueKind ValueKind => _scenario.ValueKind;

    public static implicit operator JsonElement(Scenario scenario) => scenario.Value;

    public bool Equals(Scenario other) =>
        string.Equals(_fixture, other._fixture, StringComparison.Ordinal)
        && string.Equals(_id, other._id, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Scenario other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_fixture, _id);

    public static bool operator ==(Scenario left, Scenario right) => left.Equals(right);

    public static bool operator !=(Scenario left, Scenario right) => !left.Equals(right);
}
