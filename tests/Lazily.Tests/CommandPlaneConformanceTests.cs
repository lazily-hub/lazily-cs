using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Xunit;

namespace Lazily.Tests;

public sealed class CommandPlaneConformanceTests
{
    private const string Corpus = "message-passing";

    [Fact]
    public void ReplaysEveryCanonicalCommandPlaneScenario()
    {
        var names = SpecCorpus.FixtureNames(Corpus);
        Assert.Equal(8, names.Count);
        var schema = LoadSchema("message-passing.json");
        var scenarios = 0;
        var assertions = 0;

        foreach (var name in names)
        {
            using var document = SpecCorpus.Load(Corpus, name);
            var fixture = document.RootElement;
            if (fixture.TryGetProperty("scenarios", out var nested))
            {
                foreach (var scenario in nested.EnumerateArray())
                {
                    Replay(
                        scenario.GetProperty("frames"),
                        FixtureAssertions.Of(
                            scenario,
                            "expect",
                            $"{Corpus}/{name}#{scenario.GetProperty("name").GetString()}"),
                        $"{name}#{scenario.GetProperty("name").GetString()}",
                        schema,
                        ref assertions);
                    scenarios++;
                }
            }
            else
            {
                Replay(
                    fixture.GetProperty("frames"),
                    FixtureAssertions.Of(fixture, "expect", $"{Corpus}/{name}"),
                    name,
                    schema,
                    ref assertions);
                scenarios++;
            }
        }

        Assert.Equal(9, scenarios);
        Assert.True(assertions >= 70, $"only {assertions} command assertions ran");
    }

    [Fact]
    public void CommandWireRejectsUnknownEnumsAndAdditionalProperties()
    {
        const string unknownEvent =
            """{"CommandEvents":{"events":[{"event_id":"e","command_id":"c","kind":"done","generation":1,"detail":null}]}}""";
        const string extraSubmit =
            """{"CommandSubmit":{"command_id":"c","causation_id":"c","source":"s","target":"t","namespace":"n","name":"x","authority_generation":1,"idempotency_key":"k","deadline_ms":1,"policy":{"dedupe":"none","supersede":false,"cancel_on_preempt":false},"payload_type":"n.x.v1","payload_hash":"sha256:x","payload":{"Inline":[]},"required_features":[],"extra":true}}""";

        Assert.Throws<JsonException>(() => CommandWire.Deserialize(unknownEvent));
        Assert.Throws<JsonException>(() => CommandWire.Deserialize(extraSubmit));
    }

    private static void Replay(
        JsonElement frames,
        FixtureAssertions expected,
        string label,
        JsonSchema schema,
        ref int assertions)
    {
        var projection = new CommandProjection();
        var ignored = new List<int>();
        var terminalAfter = -1;
        var conflictAfter = -1;
        CommandProjectionImage? beforeConflict = null;
        string? conflictCommandId = null;

        var frameIndex = 0;
        foreach (var frame in frames.EnumerateArray())
        {
            var prior = projection.ToImage();
            var wire = frame.GetProperty("wire");
            CommandApplyStatus status;
            switch (frame.GetProperty("schema").GetString())
            {
                case "message-passing":
                    {
                        var message = CommandWire.Deserialize(wire.GetRawText());
                        var serialized = CommandWire.Serialize(message);
                        Assert.True(
                            JsonNode.DeepEquals(
                                JsonNode.Parse(wire.GetRawText()),
                                JsonNode.Parse(serialized)),
                            $"{label} frame {frameIndex} did not round-trip");
                        using var actual = JsonDocument.Parse(serialized);
                        var validation = schema.Evaluate(actual.RootElement);
                        Assert.True(validation.IsValid, $"{label} frame {frameIndex}: {validation}");
                        assertions += 2;
                        status = projection.Apply(message);
                        break;
                    }
                case "receipts":
                    {
                        var receipts = CausalReceiptWire.Deserialize(wire.GetRawText());
                        Assert.NotEmpty(receipts.Receipts);
                        status = new CommandApplyStatus.Duplicate();
                        foreach (var receipt in receipts.Receipts)
                        {
                            status = projection.Observe(receipt);
                        }

                        assertions++;
                        break;
                    }
                default:
                    throw new InvalidOperationException(
                        $"Unknown frame schema in {label} at {frameIndex}.");
            }

            if (status is CommandApplyStatus.StaleGeneration or CommandApplyStatus.Duplicate)
            {
                ignored.Add(frameIndex);
            }

            if (status is CommandApplyStatus.TerminalConflict conflict)
            {
                conflictAfter = frameIndex;
                conflictCommandId = conflict.CommandId;
                beforeConflict = prior;
            }

            if (terminalAfter < 0 && projection.ToImage().Commands.Any(command => command.Terminal))
            {
                terminalAfter = frameIndex;
            }

            if (expected.TryGetProperty("rpc", out var rpc))
            {
                var commandId = rpc.GetProperty("command_id").GetString()!;
                var unresolved = rpc.GetProperty("unresolved_after_frame_indices")
                    .EnumerateArray()
                    .Select(item => item.GetInt32())
                    .Contains(frameIndex);
                if (unresolved)
                {
                    Assert.False(projection.TryGetTerminal(commandId, out _));
                    assertions++;
                }
            }

            frameIndex++;
        }

        if (expected.TryGetProperty("projection", out var expectedProjection))
        {
            AssertProjection(expectedProjection, projection.ToImage(), label);
            assertions += expectedProjection.GetProperty("commands").GetArrayLength() * 7 + 1;
        }

        if (expected.TryGetProperty("ignored_frame_indices", out var ignoredFrames))
        {
            Assert.Equal(
                ignoredFrames.EnumerateArray().Select(item => item.GetInt32()).ToArray(),
                ignored);
            assertions++;
        }

        if (expected.TryGetProperty("terminal_after_frame_index", out var terminal))
        {
            Assert.Equal(terminal.GetInt32(), terminalAfter);
            assertions++;
        }

        if (expected.TryGetProperty("rpc", out var expectedRpc))
        {
            Assert.Equal(
                expectedRpc.GetProperty("resolves_after_frame_index").GetInt32(),
                terminalAfter);
            var terminalEntry = Assert.Single(projection.ToImage().Commands);
            Assert.Equal(
                expectedRpc.GetProperty("terminal_status").GetString(),
                StatusWire(terminalEntry.Status));
            assertions += 2;
        }

        if (expected.TryGetProperty("conflict", out var conflictExpected)
            && conflictExpected.GetBoolean())
        {
            Assert.Equal(
                expected.GetProperty("conflict_after_frame_index").GetInt32(),
                conflictAfter);
            Assert.Equal(
                expected.GetProperty("conflict_command_id").GetString(),
                conflictCommandId);
            Assert.NotNull(beforeConflict);
            AssertProjection(
                expected.GetProperty("projection_before_conflict"),
                beforeConflict!,
                label + " before conflict");
            assertions += 3;
        }

        expected.Verify();
    }

    private static void AssertProjection(
        JsonElement expected,
        CommandProjectionImage actual,
        string label)
    {
        var encoded = CommandWire.Serialize(new CommandMessage.Projection(actual));
        using var document = JsonDocument.Parse(encoded);
        var body = document.RootElement.GetProperty("CommandProjection");
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(expected.GetRawText()),
                JsonNode.Parse(body.GetRawText())),
            label);
    }

    private static string StatusWire(CommandStatus status) =>
        status switch
        {
            CommandStatus.Submitted => "submitted",
            CommandStatus.Accepted => "accepted",
            CommandStatus.Running => "running",
            CommandStatus.Applied => "applied",
            CommandStatus.Rejected => "rejected",
            CommandStatus.Cancelled => "cancelled",
            CommandStatus.Superseded => "superseded",
            CommandStatus.TimedOut => "timed_out",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static JsonSchema LoadSchema(string schemaName)
    {
        var schemaRoot = SchemaRoot();
        var options = new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry(),
        };

        using (var definitions =
               JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, "defs.json"))))
        {
            _ = JsonSchema.Build(definitions.RootElement.Clone(), options);
        }

        using var schema =
            JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, schemaName)));
        return JsonSchema.Build(schema.RootElement.Clone(), options);
    }

    private static string SchemaRoot()
    {
        Assert.NotNull(SpecCorpus.Root);
        var root = Path.Combine(Path.GetDirectoryName(SpecCorpus.Root)!, "schemas");
        Assert.True(Directory.Exists(root), $"missing lazily-spec schemas: {root}");
        return root;
    }
}
