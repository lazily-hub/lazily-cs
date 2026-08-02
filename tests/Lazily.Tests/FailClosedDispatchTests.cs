using System.Text;
using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// Pins the per-site verdict of the library's dispatch audit: every place the library used to
/// absorb a value it did not recognise is now either a THROW naming the value, or a documented
/// leniency with the lenient outcome asserted here.
/// </summary>
/// <remarks>
/// The two verdicts are indistinguishable from outside the source without these tests. A default
/// arm that nobody wrote a test for cannot be told apart from a default arm somebody meant, so a
/// leniency with no pinning test is a leniency nobody decided on. Each test names the site it
/// covers and the mutation that reddens it.
///
/// The probe types below are variants NOTHING in the library constructs. That is deliberate: the
/// question every one of these sites answers is "what happens to a value from a producer this
/// build does not know", and only a synthetic variant asks it. A probe aimed at a value the
/// library itself emits would take a live arm and prove nothing about the default.
/// </remarks>
public sealed class FailClosedDispatchTests
{
    /// <summary>A wire frame kind this build does not know, as a newer peer's frame would be.</summary>
    private sealed record UnknownFrame(ulong Epoch) : IpcMessage;

    /// <summary>A delta op this build does not know.</summary>
    private sealed record UnknownDeltaOp(ulong Node) : DeltaOp;

    /// <summary>A tree operation variant this build does not know.</summary>
    private sealed record UnknownTreeOperation(TreeNodeId Node) : TreeOperation;

    // ------------------------------------------------------------------ FAIL CLOSED

    /// <summary>
    /// A chart declaring a history kind that is neither <c>shallow</c> nor <c>deep</c> is rejected,
    /// naming the offending spelling.
    /// </summary>
    /// <remarks>
    /// Site: <c>ChartDef.Kind</c>. This was the shape a <c>switch</c> scan misses entirely — two
    /// <c>is "literal"</c> constant patterns in a guard-clause chain whose unguarded tail absorbed
    /// everything else. A capitalised or pluralised spelling silently demoted a history
    /// pseudo-state to an ordinary compound state, and every resume it existed for was lost with
    /// no error. Goes red if the throw is replaced by a fall-through to the Final/Parallel chain.
    /// </remarks>
    [Theory]
    [InlineData("Shallow")]
    [InlineData("DEEP")]
    [InlineData("full")]
    [InlineData("")]
    public void AnUnknownHistoryKindIsRejected(string history)
    {
        var def = new ChartDef("root",
        [
            new("root", new StateDef { Initial = "region" }),
            new("region", new StateDef { Parent = "root", Initial = "a" }),
            new("a", new StateDef { Parent = "region" }),
            new("hist", new StateDef { Parent = "region", History = history, Default = "a" }),
        ]);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => def.Kind("hist"));
        Assert.Equal(history, error.ActualValue);
    }

    /// <summary>Both known history spellings still resolve, so the throw is not a blanket refusal.</summary>
    [Theory]
    [InlineData("shallow", StateKind.HistoryShallow)]
    [InlineData("deep", StateKind.HistoryDeep)]
    public void TheTwoKnownHistoryKindsStillResolve(string history, StateKind expected)
    {
        var def = new ChartDef("root",
        [
            new("root", new StateDef { Initial = "a" }),
            new("a", new StateDef { Parent = "root" }),
            new("hist", new StateDef { Parent = "root", History = history, Default = "a" }),
        ]);

        Assert.Equal(expected, def.Kind("hist"));
    }

    /// <summary>
    /// A journal record carrying an op this build does not support fails the scan, naming the op.
    /// </summary>
    /// <remarks>
    /// Site: <c>FileOutboxStore.ScanAfter</c>. The switch carried NO default at all, so an op a
    /// newer writer appended was skipped in silence. The dangerous direction is a prune: a
    /// <c>delete_after</c>-shaped op this build ignores resurrects the suffix it removed, and the
    /// caller replays entries the acknowledged cursor already retired. Goes red if the default arm
    /// is removed or softened to a <c>break</c>.
    /// </remarks>
    [Fact]
    public void AnUnsupportedJournalRecordIsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazily-outbox-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new FileOutboxStore(path);
            store.Put(1, "one"u8.ToArray());
            File.AppendAllText(
                path,
                "{\"op\":\"truncate_before\",\"epoch\":7,\"frame\":null,\"cursor\":null}\n");

            var error = Assert.Throws<InvalidDataException>(() => store.ScanAfter(0));
            Assert.Contains("truncate_before", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A <c>put</c> that lost its frame is a truncated journal, not an ignorable record.</summary>
    /// <remarks>
    /// Site: the same switch. Its <c>when record.Frame is not null</c> guard meant a malformed
    /// known op fell through the same silent hole as an unknown one.
    /// </remarks>
    [Fact]
    public void AKnownJournalRecordMissingItsPayloadIsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazily-outbox-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new FileOutboxStore(path);
            store.Put(1, "one"u8.ToArray());
            File.AppendAllText(path, "{\"op\":\"put\",\"epoch\":2,\"frame\":null,\"cursor\":null}\n");

            Assert.Throws<InvalidDataException>(() => store.ScanAfter(0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A tree update carrying an unknown operation variant is refused, not aliased.</summary>
    /// <remarks>
    /// Site: <c>TreeOp.Copy</c>. <c>TreeOperation</c> is a public, non-sealed base, so an unknown
    /// variant is constructible by a caller. The old identity arm returned it uncopied, which made
    /// the defensive copy in <c>TreeUpdate</c> silently alias whatever mutable payload it carried.
    /// Goes red if the arm returns <c>Operation</c> again.
    /// </remarks>
    [Fact]
    public void AnUnknownTreeOperationVariantIsRefused()
    {
        var op = new TreeOp(
            new TreeOpId(1, 1),
            new UnknownTreeOperation(TreeNodeId.Root));

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new TreeUpdate([op]));
        Assert.Equal(nameof(UnknownTreeOperation), error.ActualValue);
    }

    /// <summary>A known tree operation still copies, so the refusal is not a blanket one.</summary>
    [Fact]
    public void AKnownTreeOperationStillCopies()
    {
        var tree = new LosslessTreeCrdt(peer: 1);
        tree.CreateNode(TreeNodeId.Root, NodeSeed.Element("p"));

        var update = tree.Diff(new TreeVersionFrontier());

        Assert.NotEmpty(update.Operations);
        Assert.All(update.Operations, op => Assert.IsType<CreateNodeOperation>(op.Operation));
    }

    /// <summary>An unknown insert position is refused rather than silently appended.</summary>
    /// <remarks>
    /// Site: <c>SourceMap.Insert</c> / <c>AsyncSourceMap.Insert</c>. <c>InsertAt</c> survives an
    /// unchecked cast from <c>int</c>, and the old <c>default: break</c> placed such a value at the
    /// END while returning <c>true</c> — a wrong ORDER reported as a successful insert. Ordering is
    /// the only thing these maps add over a dictionary.
    /// </remarks>
    [Fact]
    public void AnUnknownInsertPositionIsRefused()
    {
        var map = new SourceMap<string, int>(new Context());
        map.Insert("a", 1);

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => map.Insert("b", 2, (InsertAt)99));
        Assert.Equal((InsertAt)99, error.ActualValue);
    }

    /// <summary>An anchored insert with no anchor is refused rather than silently appended.</summary>
    /// <remarks>
    /// Site: the <c>when anchor is not null</c> guards on the same switch. This is the shape that
    /// absorbs an ordinary caller mistake rather than a future wire value: <c>InsertAt.Before</c>
    /// with a forgotten anchor produced a correct membership set at a wrong index.
    /// </remarks>
    [Theory]
    [InlineData(InsertAt.Before)]
    [InlineData(InsertAt.After)]
    public void AnAnchorlessRelativeInsertIsRefused(InsertAt at)
    {
        var map = new SourceMap<string, int>(new Context());
        map.Insert("a", 1);

        Assert.Throws<ArgumentNullException>(() => map.Insert("b", 2, at));
        Assert.Equal(["a"], map.PresentKeys());
    }

    /// <summary>The async flavor makes the same two refusals, so the planes cannot disagree.</summary>
    [Fact]
    public void TheAsyncMapMakesTheSameInsertRefusals()
    {
        var map = new AsyncSourceMap<string, int>(new AsyncContext());
        map.Insert("a", 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => map.Insert("b", 2, (InsertAt)99));
        Assert.Throws<ArgumentNullException>(() => map.Insert("b", 2, InsertAt.Before));
        Assert.Equal(["a"], map.PresentKeys());
    }

    /// <summary>An anchored insert WITH its anchor still places relatively.</summary>
    [Fact]
    public void AnAnchoredInsertStillPlacesRelatively()
    {
        var map = new SourceMap<string, int>(new Context());
        map.Insert("a", 1);
        map.Insert("c", 3);
        map.Insert("b", 2, InsertAt.Before, anchor: "c");

        Assert.Equal(["a", "b", "c"], map.PresentKeys());
    }

    /// <summary>An unknown overflow policy is refused by the live-policy legality gate.</summary>
    /// <remarks>
    /// Site: <c>RelayCell.OverflowIsLegal</c>. Its old catch-all answered LEGAL for anything it did
    /// not recognise, which is exactly backwards for an admission gate: an unknown overflow is the
    /// one value whose requirements cannot be checked. <c>RelayCell.Ingress</c> already failed
    /// closed on the same enum, so the two disagreed.
    /// </remarks>
    [Fact]
    public void AnUnknownRelayOverflowPolicyIsRefused()
    {
        var ctx = new Context();
        var policy = new BackpressurePolicy(
            ctx, BoundDimension.Count, highWater: 4, lowWater: 0, RelayOverflow.Block);
        var relay = new RelayCell<int>(ctx, policy, MergePolicy.KeepLatest<int>());
        Assert.True(relay.OverflowIsLegal());

        policy.Overflow.Set((RelayOverflow)99);
        Assert.Throws<ArgumentOutOfRangeException>(() => relay.OverflowIsLegal());
    }

    /// <summary>An unknown overflow policy is refused on the ingress plane's backpressure branch.</summary>
    /// <remarks>
    /// Site: <c>IngressCore.Decide</c>. The old <c>default: break</c> absorbed an unknown policy
    /// into "conflate", which silently DISABLES the bound the caller configured: the scope merges
    /// without limit and reports no backpressure at all.
    /// </remarks>
    [Fact]
    public void AnUnknownIngressOverflowPolicyIsRefused()
    {
        var core = new IngressCore<string, int>(
            new IngressPolicy { HighWater = 1, Overflow = (RelayOverflow)99 },
            MergePolicy.KeepLatest<int>());

        core.Admit(new IngressEnvelope<string, int>("s", 0, 0, 0, 1));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => core.Admit(new IngressEnvelope<string, int>("s", 0, 1, 0, 2)));
    }

    /// <summary>A lifecycle this build does not know has no readiness answer.</summary>
    /// <remarks>
    /// Site: <c>IngressScopeView.Readiness</c>. Three lifecycles were named and the catch-all
    /// carried the Live rule, so any lifecycle added later would be answered READY on the strength
    /// of a watermark — the one answer a consumer must never be given wrongly.
    /// </remarks>
    [Fact]
    public void AnUnknownIngressLifecycleHasNoReadiness()
    {
        var view = new IngressScopeView(
            (IngressLifecycle)99, 0, DeliveredThrough: 5, 0, 0, 0, 0, 0, new IngressPolicy());

        Assert.Throws<ArgumentOutOfRangeException>(() => view.Readiness);
    }

    /// <summary>Live still resolves through the same expression, so the throw is not a blanket one.</summary>
    [Fact]
    public void ALiveScopeStillDerivesItsReadiness()
    {
        var live = new IngressScopeView(
            IngressLifecycle.Live, 0, DeliveredThrough: 5, 0, 0, 0, 0, 0, new IngressPolicy());
        Assert.Equal(IngressReadiness.Ready, live.Readiness);

        var warming = new IngressScopeView(
            IngressLifecycle.Live, 0, DeliveredThrough: null, 0, 0, 0, 0, 0, new IngressPolicy());
        Assert.Equal(IngressReadiness.Warming, warming.Readiness);
    }

    /// <summary>An unknown receipt channel dirties no reader; it is refused.</summary>
    /// <remarks>
    /// Site: <c>IngressChange.MarkChannel</c>, reached through <c>InternalsVisibleTo</c>. Folding
    /// an unrecognised channel into <c>Error</c> dirties the WRONG reader: the receipt lands on a
    /// channel whose version source never bumped, so that channel's reader serves a cached list
    /// missing it, permanently.
    /// </remarks>
    [Fact]
    public void AnUnknownReceiptChannelIsRefused()
    {
        var change = new IngressChange<string>();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => change.MarkChannel((IngressReceiptChannel)99));

        change.MarkChannel(IngressReceiptChannel.Error);
        Assert.True(change.ErrorReceipts);
        Assert.False(change.AcceptedReceipts);
        Assert.False(change.DroppedReceipts);
    }

    // ------------------------------------------------------------------ INTENTIONAL

    /// <summary>
    /// A delta op this build does not know is withheld from every peer, rather than forwarded on
    /// an unchecked guess.
    /// </summary>
    /// <remarks>
    /// Site: <c>PeerPermissions.IsReadable</c>. INTENTIONAL leniency, and the only safe direction:
    /// the op is decoded from a frame a REMOTE peer wrote, so version skew across a mesh is normal
    /// and expected. <c>false</c> means DENY, so leniency about the op is strictness about the
    /// disclosure — and a peer cannot take the plane down by sending one frame from a newer build.
    /// Goes red if the arm is changed to <c>true</c> or to a throw.
    /// </remarks>
    [Fact]
    public void AnUnknownDeltaOpIsNotReadableByAnyPeer()
    {
        var permissions = new PeerPermissions();
        permissions.Allow(peer: 7, RemoteOp.Read(1));
        permissions.Allow(peer: 7, RemoteOp.Read(2));

        var filtered = permissions.FilterReadable(
            7,
            new DeltaMessage(0, 1,
            [
                new DeltaOp.Invalidate(1),
                new UnknownDeltaOp(2),
            ]));

        Assert.Equal([new DeltaOp.Invalidate(1)], filtered.Ops);
    }

    /// <summary>
    /// A frame carrying no epoch is ignored by the resync cursor, and ignoring it suppresses no gap.
    /// </summary>
    /// <remarks>
    /// Site: <c>ResyncCoordinator.Ingest(IpcMessage)</c>. INTENTIONAL: this coordinator owns the
    /// epoch cursor on ONE plane, and a shared socket multiplexes several. "Ignore" is the complete
    /// answer for a frame with no epoch to fold, not a guess. The second half of the assertion is
    /// the load-bearing one — the ignored frame must not have moved <c>LastEpoch</c>, or the next
    /// delta's gap detection would be poisoned by it. Goes red if the arm folds an epoch or throws.
    /// </remarks>
    [Fact]
    public void AnUnknownIpcFrameIsIgnoredNotFolded()
    {
        var coordinator = new ResyncCoordinator();
        Assert.Equal(ResyncAction.Apply, coordinator.Ingest(new SnapshotMessage(4, [], [], [])).Action);

        Assert.Equal(ResyncAction.Ignore, coordinator.Ingest(new UnknownFrame(99)).Action);
        Assert.Equal(4UL, coordinator.LastEpoch);

        // The gap the ignored frame must not have hidden.
        Assert.Equal(
            ResyncAction.RequestSnapshot,
            coordinator.Ingest(new DeltaMessage(9, 10, [])).Action);
    }

    /// <summary>A frame with no spillable payload is forwarded byte-identical, not refused.</summary>
    /// <remarks>
    /// Site: <c>BlobTransport.SpillMessage</c>. INTENTIONAL: spilling is a SIZE optimization, so an
    /// unspilled frame is still exactly the frame the caller handed in and stays decodable by every
    /// peer. Failing closed would make this transport refuse to send control frames. Goes red if
    /// the arm throws or rewrites the message.
    /// </remarks>
    [Fact]
    public void AnUnknownFrameIsForwardedUnspilled()
    {
        var backend = new InProcessBackend();
        var frame = new UnknownFrame(3);

        var result = BlobTransport.SpillMessage(frame, backend, threshold: 1);

        Assert.Same(frame, result.Message);
        Assert.Equal(0UL, result.BytesSpilled);
    }

    /// <summary>A delta op with no spillable payload is forwarded unchanged inside its frame.</summary>
    /// <remarks>
    /// Site: <c>BlobTransport.SpillOperation</c>. Same contract as the frame arm above, and the
    /// same reason: an op that carries only ids has nothing to page out, so identity is exact.
    /// </remarks>
    [Fact]
    public void AnUnknownDeltaOpIsForwardedUnspilled()
    {
        var backend = new InProcessBackend();
        var unknown = new UnknownDeltaOp(5);
        var payload = new byte[64];

        var result = BlobTransport.SpillMessage(
            new DeltaMessage(0, 1,
            [
                unknown,
                new DeltaOp.CellSet(1, new IpcValue.Inline(payload)),
            ]),
            backend,
            threshold: 8);

        var delta = Assert.IsType<DeltaMessage>(result.Message);
        Assert.Same(unknown, delta.Ops[0]);
        Assert.IsType<IpcValue.SharedBlob>(Assert.IsType<DeltaOp.CellSet>(delta.Ops[1]).Payload);
        Assert.Equal(64UL, result.BytesSpilled);
    }

    /// <summary>A torn trailing journal record is skipped; every fsynced record before it survives.</summary>
    /// <remarks>
    /// Site: <c>FileOutboxStore.ReadRecords</c>'s <c>catch (JsonException)</c>. INTENTIONAL, and the
    /// ONE decode failure this store absorbs: a crash between the append and the flush leaves
    /// exactly one torn TRAILING line, and refusing the whole journal over it would turn a
    /// recoverable crash into permanent data loss. Note what it does NOT absorb — a WELL-FORMED
    /// record naming an op this build does not know reaches the fail-closed default instead, which
    /// <see cref="AnUnsupportedJournalRecordIsRejected"/> pins. Goes red if the catch rethrows.
    /// </remarks>
    [Fact]
    public void ATornTrailingJournalRecordIsSkipped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazily-outbox-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new FileOutboxStore(path);
            store.Put(1, "one"u8.ToArray());
            store.Put(2, "two"u8.ToArray());
            File.AppendAllText(path, "{\"op\":\"put\",\"epoch\":3,\"fra");

            var entries = store.ScanAfter(0);

            Assert.Equal([1UL, 2UL], entries.Select(entry => entry.Epoch));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>An id the chart never declares is Atomic, because a pseudo-id has no children.</summary>
    /// <remarks>
    /// Site: <c>ChartDef.Kind</c>'s first guard. INTENTIONAL: the entry walk and <c>PathBelow</c>
    /// both ask for the kind of ids the chart names only as a parent. Goes red if the guard throws.
    /// </remarks>
    [Fact]
    public void KindOfAnUndeclaredIdIsAtomic()
    {
        var def = new ChartDef("root",
        [
            new("root", new StateDef { Initial = "a" }),
            new("a", new StateDef { Parent = "root" }),
        ]);

        Assert.Equal(StateKind.Atomic, def.Kind("never-declared"));
        Assert.True(def.IsLeaf("never-declared"));
    }

    /// <summary>Entering a leaf descends no further and fires only that leaf's entry actions.</summary>
    /// <remarks>
    /// Site: <c>StateChart.EnterSubtree</c>'s default. INTENTIONAL: entry runs on the hot
    /// transition path, and "leaf" is the correct degradation. Goes red if the default arm starts
    /// descending into children or throws.
    /// </remarks>
    [Fact]
    public void EnteringALeafDescendsNoFurther()
    {
        var ctx = new Context();
        var chart = new StateChart(ctx, new ChartDef("root",
        [
            new("root", new StateDef
            {
                Initial = "a",
                On = new Dictionary<string, Transition>(StringComparer.Ordinal)
                {
                    ["GO"] = new("b", null, [], false),
                },
            }),
            new("a", new StateDef { Parent = "root" }),
            new("b", new StateDef { Parent = "root", Final = true, Entry = ["enterB"] }),
        ]));

        Assert.True(chart.Send("GO"));
        Assert.Equal("b", chart.ActiveLeaves().Single());
        Assert.Equal(["enterB"], chart.LastActions);
    }

    /// <summary>An unrecognised tree error still carries its machine-readable code.</summary>
    /// <remarks>
    /// Site: <c>TreeException</c>'s message formatter. INTENTIONAL: this runs inside an exception
    /// constructor, and the verdict callers dispatch on is <c>Error</c>, which is preserved
    /// verbatim. Throwing here would replace the caller's real rejection with an unrelated failure
    /// and destroy the reason they were about to read. Goes red if the arm throws.
    /// </remarks>
    [Fact]
    public void AnUnknownTreeErrorStillCarriesItsCode()
    {
        var error = new TreeException((TreeError)99);

        Assert.Equal((TreeError)99, error.Error);
        Assert.Equal("tree mutation rejected", error.Message);
    }

    /// <summary>A frame this build cannot decode classifies as Unknown, never as a crash.</summary>
    /// <remarks>
    /// Site: <c>LazilyFfi.Kind</c> / <c>ClassifyJson</c>. INTENTIONAL, and the FFI's published
    /// contract: <c>LazilyFfiMessageKind</c> ships an <c>Unknown</c> member so a foreign caller can
    /// classify a frame this build does not name without the call unwinding a .NET exception toward
    /// an ABI with no channel for one. Goes red if either path rethrows.
    /// </remarks>
    [Fact]
    public void AnUnrecognisedFrameClassifiesAsUnknownRatherThanThrowing()
    {
        var classification = LazilyFfi.ClassifyJson(
            Encoding.UTF8.GetBytes("{\"Nonesuch\":{\"epoch\":1}}"));

        Assert.Equal(LazilyFfiStatus.InvalidMessage, classification.Status);
        Assert.Equal(LazilyFfiMessageKind.Unknown, classification.Kind);
    }

    /// <summary>Ordinary JSON that is not a frame at all classifies the same way.</summary>
    [Fact]
    public void MalformedJsonAlsoClassifiesAsUnknown()
    {
        var classification = LazilyFfi.ClassifyJson(Encoding.UTF8.GetBytes("{"));

        Assert.Equal(LazilyFfiStatus.InvalidMessage, classification.Status);
        Assert.Equal(LazilyFfiMessageKind.Unknown, classification.Kind);
    }

    /// <summary>Serialization round-trips still classify by their real kind.</summary>
    [Fact]
    public void AKnownFrameStillClassifiesByItsKind()
    {
        var json = IpcWire.Serialize(new SnapshotMessage(1, [], [], []));
        var classification = LazilyFfi.ClassifyJson(Encoding.UTF8.GetBytes(json));

        Assert.Equal(LazilyFfiStatus.Ok, classification.Status);
        Assert.Equal(LazilyFfiMessageKind.Snapshot, classification.Kind);
    }
}
