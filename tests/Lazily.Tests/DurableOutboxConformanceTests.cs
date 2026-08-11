using System.Text;
using System.Text.Json;
using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class DurableOutboxConformanceTests
{
    [Fact]
    public void OutboxStore_protocol_corpus_replays_against_file_journal()
    {
        var fixture = Assert.Single(
            SpecCorpus.FixtureNames("reliable-sync"),
            name => name.StartsWith("outbox_store_", StringComparison.Ordinal));
        using var document = SpecCorpus.Load("reliable-sync", fixture);
        var scenarios = SpecCorpus.Scenarios(document.RootElement, "reliable-sync", fixture);
        Assert.Equal(4, scenarios.Count);

        foreach (var scenario in scenarios.All())
        {
            var path = TemporaryJournal();
            var store = new FileOutboxStore(path);
            var outbox = new DurableOutbox<FileOutboxStore>(store);

            foreach (var epoch in OptionalArray(scenario, "put_epochs"))
            {
                outbox.Append(epoch.GetUInt64(), Frame(epoch.GetUInt64()));
            }

            if (scenario.TryGetProperty("ack_through", out var acknowledgements))
            {
                foreach (var epoch in acknowledgements.EnumerateArray())
                {
                    outbox.AckThrough(epoch.GetUInt64());
                }
            }

            if (scenario.TryGetProperty("save_cursor", out var saves))
            {
                var handles = new Dictionary<string, FileOutboxStore>(StringComparer.Ordinal)
                {
                    ["stale"] = new FileOutboxStore(path),
                    ["current"] = new FileOutboxStore(path),
                };
                foreach (var save in saves.EnumerateArray())
                {
                    handles[save.GetProperty("handle").GetString()!]
                        .SaveCursor(save.GetProperty("epoch").GetUInt64());
                }
            }

            AssertScenario(path, scenario);
        }
    }

    [Fact]
    public void File_journal_decode_corpus_pins_unknown_and_torn_opposites()
    {
        const string fixture = "outbox_journal_decode.json";
        using var document = SpecCorpus.Load("reliable-sync", fixture);
        var scenarios = SpecCorpus.Scenarios(document.RootElement, "reliable-sync", fixture);
        Assert.Equal(2, scenarios.Count);

        foreach (var scenario in scenarios.All())
        {
            var path = TemporaryJournal();
            var store = new FileOutboxStore(path);
            foreach (var record in scenario.GetProperty("records").EnumerateArray())
            {
                var op = record.GetProperty("op").GetString()!;
                var epoch = record.GetProperty("epoch").GetUInt64();
                switch (op)
                {
                    case "put":
                        store.Put(epoch, Bytes(record.GetProperty("frame")));
                        break;
                    case "delete":
                        store.DeleteThrough(epoch);
                        break;
                    case "cursor":
                        store.SaveCursor(epoch);
                        break;
                    default:
                        AppendRawRecord(path, record);
                        break;
                }
            }

            if (scenario.TryGetProperty("tail_fault", out var tail))
            {
                Assert.Equal("torn_record", tail.GetProperty("kind").GetString());
                var encoded = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["op"] = tail.GetProperty("op").GetString(),
                    ["epoch"] = tail.GetProperty("epoch").GetUInt64(),
                    ["frame"] = Bytes(tail.GetProperty("frame")),
                });
                var keepBytes = tail.GetProperty("keep_bytes").GetInt32();
                Assert.InRange(keepBytes, 1, Encoding.UTF8.GetByteCount(encoded) - 1);
                File.AppendAllText(path, encoded[..keepBytes], Encoding.UTF8);
            }

            IReadOnlyList<StoredOutboxEntry>? entries = null;
            var error = Record.Exception(() =>
                entries = store.ScanAfter(scenario.GetProperty("scan_after").GetUInt64()));
            var expected = FixtureAssertions.Of(
                scenario,
                "expect",
                $"reliable-sync/{fixture} scenario {scenario.GetProperty("name").GetString()}");
            expected.AssertKey("outcome", error is null ? "accept" : "reject");
            if (error is null)
            {
                Assert.NotNull(entries);
            }
            else
            {
                Assert.IsType<InvalidDataException>(error);
            }

            expected.TryAssertKeyWith(
                "retained_epochs",
                want => want.AssertEqual(w => Epochs(w), entries!.Select(entry => entry.Epoch)));
            expected.TryAssertKeyWith("retained_frames", want => want.Against(entries!, (expect, got) =>
            {
                var frames = expect.EnumerateArray().ToArray();
                Assert.Equal(frames.Length, got.Count);
                for (var index = 0; index < frames.Length; index++)
                {
                    Assert.Equal(Bytes(frames[index]), got[index].Frame);
                }
            }));
            expected.Verify();
        }
    }

    [Fact]
    public void DurableOutbox_crash_replay_corpus_is_at_least_once_in_epoch_order()
    {
        var fixture = Assert.Single(
            SpecCorpus.FixtureNames("reliable-sync"),
            name => name.StartsWith("outbox_replay_", StringComparison.Ordinal));
        using var document = SpecCorpus.Load("reliable-sync", fixture);
        var scenarios = SpecCorpus.Scenarios(document.RootElement, "reliable-sync", fixture);
        Assert.Equal(2, scenarios.Count);

        foreach (var scenario in scenarios.All())
        {
            var path = TemporaryJournal();
            var outbox = new DurableOutbox<FileOutboxStore>(new FileOutboxStore(path));
            foreach (var appended in scenario.GetProperty("appended").EnumerateArray())
            {
                outbox.Append(
                    appended.GetProperty("epoch").GetUInt64(),
                    IpcWire.Deserialize(appended.GetProperty("frame").GetRawText()));
            }

            if (scenario.TryGetProperty("ack_through", out var ack)
                && ack.ValueKind != JsonValueKind.Null)
            {
                outbox.AckThrough(ack.GetUInt64());
            }

            var restarted = new DurableOutbox<FileOutboxStore>(new FileOutboxStore(path));
            var expected = FixtureAssertions.Of(
                scenario,
                "expect",
                $"reliable-sync/{fixture} scenario {scenario.GetProperty("name").GetString()}");
            if (expected.TryGetProperty("retained_after_ack", out _))
            {
                expected.AssertKeyWith(
                    "retained_after_ack",
                    want => want.AssertEqual(w => Epochs(w), restarted.RetainedEpochs));
                var cursor = scenario.GetProperty("reconnect_cursor").GetUInt64();
                var replayed = restarted.ReplayFrom(cursor).Select(entry => entry.Epoch).ToArray();
                expected.AssertKeyWith(
                    "replayed_from_cursor",
                    want => want.AssertEqual(w => Epochs(w), replayed));
                // `replay_order` is not a duplicate of `replayed_from_cursor`: the SET can be
                // right while the order is wrong, and a receiver that folds a later epoch
                // first sees a gap it can never close.
                expected.AssertKeyWith("replay_order", want => want.AssertEqual(w => Epochs(w), replayed));

                // The receiver half: at-least-once on the wire, exactly-once in effect.
                var coordinator = new ResyncCoordinator(cursor);
                var applied = new List<ulong>();
                foreach (var entry in restarted.ReplayFrom(cursor))
                {
                    if (coordinator.Ingest(entry.Message).Action == ResyncAction.Apply)
                    {
                        applied.Add(coordinator.LastEpoch);
                    }
                }

                expected.AssertKeyWith("receiver_applies", want => want.AssertEqual(w => Epochs(w), applied.ToArray()));
                expected.AssertKey("receiver_last_epoch_after", coordinator.LastEpoch);

                var ackedThrough = scenario.GetProperty("ack_through").GetUInt64();
                var owed = scenario.GetProperty("appended")
                    .EnumerateArray()
                    .Select(entry => entry.GetProperty("epoch").GetUInt64())
                    .Where(epoch => epoch > ackedThrough)
                    .ToArray();
                var lost = owed.Count(epoch => !applied.Contains(epoch));
                var doubled = applied.Count - applied.Distinct().Count();
                expected.AssertKey("ops_lost", lost);
                expected.AssertKey("ops_doubled", doubled);
                expected.AssertKey("exactly_once_effect", lost == 0 && doubled == 0);
            }
            else
            {
                // This used to read the key and assert it against the literal `true` — the
                // fixture's own value, which is true by construction. It is now compared
                // against whether the journal ACTUALLY still holds the unsent frame.
                expected.AssertKey(
                    "frame_retained_after_failed_send",
                    restarted.RetainedEpochs.Any());
                expected.AssertKeyWith(
                    "retained",
                    want => want.AssertEqual(w => Epochs(w), restarted.RetainedEpochs));
                var resent = restarted.ReplayFrom(0).Select(entry => entry.Epoch).ToArray();
                expected.AssertKeyWith(
                    "resent_on_next_tick",
                    want => want.AssertEqual(w => Epochs(w), resent));
                // A retained frame is a DELAY, not a hole: the next tick replays it, so the
                // receiver never sees an epoch it can no longer obtain.
                expected.AssertKey("permanent_gap", resent.Length == 0);
            }

            expected.Verify();
        }
    }

    [Fact]
    public void File_journal_ignores_an_incomplete_crash_tail()
    {
        var path = TemporaryJournal();
        var outbox = new DurableOutbox<FileOutboxStore>(new FileOutboxStore(path));
        outbox.Append(1, Frame(1));
        File.AppendAllText(path, "{\"op\":\"put\",\"epoch\":2", Encoding.UTF8);

        var restarted = new DurableOutbox<FileOutboxStore>(new FileOutboxStore(path));
        Assert.Equal([1UL], restarted.RetainedEpochs);
        Assert.Equal([1UL], restarted.ReplayFrom(0).Select(entry => entry.Epoch));
    }

    [Fact]
    public void In_memory_store_clones_frames_at_both_boundaries()
    {
        var store = new InMemoryOutboxStore();
        byte[] frame = [1, 2, 3];
        store.Put(1, frame);
        frame[0] = 9;

        var read = Assert.Single(store.ScanAfter(0));
        Assert.Equal([1, 2, 3], read.Frame);
        read.Frame[1] = 9;
        Assert.Equal([1, 2, 3], Assert.Single(store.ScanAfter(0)).Frame);
    }

    private static void AssertScenario(string path, JsonElement scenario)
    {
        var expected = FixtureAssertions.Of(
            scenario,
            "expect",
            $"reliable-sync/outbox_store_protocol.json scenario {scenario.GetProperty("name").GetString()}");
        var restarted = new DurableOutbox<FileOutboxStore>(new FileOutboxStore(path));

        expected.TryAssertKeyWith(
            "epochs",
            epochs => epochs.Against(
                restarted.Store
                    .ScanAfter(scenario.GetProperty("scan_after").GetUInt64())
                    .Select(entry => entry.Epoch)
                    .ToArray(),
                (expect, got) => Assert.Equal(Epochs(expect), got)));

        if (expected.TryAssertKeyWith(
                "cursor",
                cursorExpected => cursorExpected.AssertEqual(w => w.GetUInt64(), restarted.AckedThrough)))
        {
            expected.AssertKeyWith(
                "replay_from_zero",
                want => want.AssertEqual(w =>
                    Epochs(w),
                    restarted.ReplayFrom(0).Select(entry => entry.Epoch)));
        }

        expected.TryAssertKeyWith(
            "loaded_cursor",
            loaded => loaded.AssertEqual(w => w.GetUInt64(), restarted.AckedThrough));

        // `retained` is asserted for EVERY scenario that carries it. Reading it only inside
        // the `cursor` branch above meant the restart scenario — the one whose whole point
        // is that the unacked suffix survives a reopen — never checked the suffix at all.
        expected.TryAssertKeyWith(
            "retained",
            retained => retained.AssertEqual(w => Epochs(w), restarted.RetainedEpochs));

        expected.TryAssertKeyWith(
            "replay",
            replay => replay.AssertEqual(w =>
                Epochs(w),
                restarted.ReplayFrom(0).Select(entry => entry.Epoch)));

        expected.Verify();
    }

    private static IEnumerable<JsonElement> OptionalArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var array)
            ? array.EnumerateArray().ToArray()
            : [];

    private static ulong[] Epochs(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetUInt64()).ToArray();

    private static byte[] Bytes(JsonElement array) =>
        array.EnumerateArray().Select(item => (byte)item.GetInt32()).ToArray();

    private static void AppendRawRecord(string path, JsonElement record) =>
        File.AppendAllText(path, record.GetRawText() + "\n", Encoding.UTF8);

    private static DeltaMessage Frame(ulong epoch) =>
        new(
            epoch == 0 ? 0 : epoch - 1,
            epoch,
            [new DeltaOp.CellSet(epoch, new IpcValue.Inline([(byte)(epoch % 256)]))]);

    private static string TemporaryJournal()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lazily-cs-outbox-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return System.IO.Path.Combine(directory, "outbox.jsonl");
    }
}
