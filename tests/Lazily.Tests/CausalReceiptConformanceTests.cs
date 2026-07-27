using System.Text.Json.Nodes;
using Json.Schema;
using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class CausalReceiptConformanceTests
{
    [Fact]
    public void Canonical_receipt_corpus_round_trips_and_folds_by_generation()
    {
        var fixture = Assert.Single(SpecCorpus.FixtureNames("receipts"));
        using var document = SpecCorpus.Load("receipts", fixture);
        var root = document.RootElement;
        var expected = root.GetProperty("assertions");
        var wire = root.GetProperty("wire");
        var message = CausalReceiptWire.Deserialize(wire.GetRawText());
        var projection = new ReceiptProjection();
        var statuses = message.Receipts
            .Select(
                receipt => projection.Observe(
                    expected.GetProperty("current_generation").GetUInt64(),
                    receipt))
            .ToArray();

        Assert.Equal(expected.GetProperty("receipt_count").GetInt32(), projection.ReceiptCount);
        Assert.Equal(3, statuses.Count(status => status is ReceiptApplyStatus.Recorded));
        Assert.IsType<ReceiptApplyStatus.StaleGeneration>(statuses[3]);
        Assert.Equal(
            expected.GetProperty("terminal_outcome").GetString(),
            OutcomeName(projection.TerminalFor(expected.GetProperty("causation_id").GetString()!)!.Outcome));
        Assert.Equal(
            expected.GetProperty("stale_receipt_ids").EnumerateArray().Select(item => item.GetString()),
            projection.StaleReceiptIds);
        Assert.All(
            expected.GetProperty("nonterminal_outcomes").EnumerateArray(),
            item => Assert.False(ParseOutcome(item.GetString()!).IsTerminal()));

        var actualJson = CausalReceiptWire.Serialize(message);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(wire.GetRawText()), JsonNode.Parse(actualJson)),
            fixture);
        AssertSchemaValid(actualJson);
    }

    [Fact]
    public void Duplicate_ids_are_noops_including_after_stale_generation()
    {
        var projection = new ReceiptProjection();
        var accepted = CausalReceipt.Accepted("receipt-1", "command-1", "peer-a", 7);
        var stale = CausalReceipt.Rejected("receipt-stale", "command-1", "peer-a", 6);

        Assert.IsType<ReceiptApplyStatus.Recorded>(projection.Observe(7, accepted));
        Assert.IsType<ReceiptApplyStatus.Duplicate>(projection.Observe(7, accepted));
        Assert.IsType<ReceiptApplyStatus.StaleGeneration>(projection.Observe(7, stale));
        Assert.IsType<ReceiptApplyStatus.Duplicate>(projection.Observe(7, stale));
        Assert.Equal(2, projection.ReceiptCount);
    }

    [Fact]
    public void Conflicting_terminal_outcomes_fail_closed()
    {
        var projection = new ReceiptProjection();
        var applied = CausalReceipt.Applied("receipt-applied", "command-1", "peer-a", 7);
        var rejected = CausalReceipt.Rejected("receipt-rejected", "command-1", "peer-b", 7);

        Assert.IsType<ReceiptApplyStatus.Recorded>(projection.Observe(7, applied));
        var conflict = Assert.IsType<ReceiptApplyStatus.TerminalConflict>(
            projection.Observe(7, rejected));
        Assert.Equal(ReceiptOutcome.Applied, conflict.Existing);
        Assert.Equal(ReceiptOutcome.Rejected, conflict.Incoming);
        Assert.Same(applied, projection.TerminalFor("command-1"));
        Assert.False(projection.ContainsReceipt("receipt-rejected"));
    }

    [Fact]
    public void Batch_owns_its_receipt_sequence_and_wire_requires_exact_shape()
    {
        var mutable = new List<CausalReceipt>
        {
            CausalReceipt.Observed("receipt-1", "command-1", "peer-a", 7),
        };
        var batch = new CausalReceipts(mutable);
        mutable.Clear();

        Assert.Single(batch.Receipts);
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => CausalReceiptWire.Deserialize(
                """
                {"CausalReceipts":{"receipts":[],"unexpected":true}}
                """));
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => CausalReceiptWire.Deserialize(
                """
                {"CausalReceipts":{"receipts":[{"receipt_id":"r","causation_id":"c","observer":"p","generation":0,"outcome":"delivered","reason":null,"payload_hash":null}]}}
                """));
    }

    private static ReceiptOutcome ParseOutcome(string outcome) =>
        outcome switch
        {
            "observed" => ReceiptOutcome.Observed,
            "accepted" => ReceiptOutcome.Accepted,
            "applied" => ReceiptOutcome.Applied,
            "rejected" => ReceiptOutcome.Rejected,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

    private static string OutcomeName(ReceiptOutcome outcome) =>
        outcome.ToString().ToLowerInvariant();

    private static void AssertSchemaValid(string json)
    {
        ArgumentNullException.ThrowIfNull(SpecCorpus.Root);
        var schemaPath = Path.GetFullPath(
            Path.Combine(SpecCorpus.Root, "..", "schemas", "receipts.json"));
        var schema = JsonSchema.FromText(File.ReadAllText(schemaPath));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement);
        Assert.True(result.IsValid, result.ToString());
    }
}
