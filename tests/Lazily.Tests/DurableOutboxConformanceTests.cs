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
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(4, scenarios.Length);

        foreach (var scenario in scenarios)
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
    public void DurableOutbox_crash_replay_corpus_is_at_least_once_in_epoch_order()
    {
        var fixture = Assert.Single(
            SpecCorpus.FixtureNames("reliable-sync"),
            name => name.StartsWith("outbox_replay_", StringComparison.Ordinal));
        using var document = SpecCorpus.Load("reliable-sync", fixture);
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(2, scenarios.Length);

        foreach (var scenario in scenarios)
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
            var expected = scenario.GetProperty("expect");
            if (expected.TryGetProperty("retained_after_ack", out var retainedAfterAck))
            {
                Assert.Equal(Epochs(retainedAfterAck), restarted.RetainedEpochs);
                Assert.Equal(
                    Epochs(expected.GetProperty("replayed_from_cursor")),
                    restarted.ReplayFrom(scenario.GetProperty("reconnect_cursor").GetUInt64())
                        .Select(entry => entry.Epoch));
            }
            else
            {
                Assert.True(expected.GetProperty("frame_retained_after_failed_send").GetBoolean());
                Assert.Equal(Epochs(expected.GetProperty("retained")), restarted.RetainedEpochs);
                Assert.Equal(
                    Epochs(expected.GetProperty("resent_on_next_tick")),
                    restarted.ReplayFrom(0).Select(entry => entry.Epoch));
            }
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
        var expected = scenario.GetProperty("expect");
        var restarted = new DurableOutbox<FileOutboxStore>(new FileOutboxStore(path));

        if (expected.TryGetProperty("epochs", out var epochs))
        {
            var cursor = scenario.GetProperty("scan_after").GetUInt64();
            Assert.Equal(
                Epochs(epochs),
                restarted.Store.ScanAfter(cursor).Select(entry => entry.Epoch));
        }

        if (expected.TryGetProperty("cursor", out var cursorExpected))
        {
            Assert.Equal(cursorExpected.GetUInt64(), restarted.AckedThrough);
            Assert.Equal(
                Epochs(expected.GetProperty("retained")),
                restarted.RetainedEpochs);
            Assert.Equal(
                Epochs(expected.GetProperty("replay_from_zero")),
                restarted.ReplayFrom(0).Select(entry => entry.Epoch));
        }

        if (expected.TryGetProperty("loaded_cursor", out var loaded))
        {
            Assert.Equal(loaded.GetUInt64(), restarted.AckedThrough);
        }

        if (expected.TryGetProperty("replay", out var replay))
        {
            Assert.Equal(Epochs(replay), restarted.ReplayFrom(0).Select(entry => entry.Epoch));
        }
    }

    private static IEnumerable<JsonElement> OptionalArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var array)
            ? array.EnumerateArray().ToArray()
            : [];

    private static ulong[] Epochs(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetUInt64()).ToArray();

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
