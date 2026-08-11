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
            if (SpecCorpus.TryScenarios(fixture, Corpus, name, out var nested))
            {
                foreach (var scenario in nested.All())
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
        ref int assertionsOut)
    {
        var assertions = 0;
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

            var atFrame = frameIndex;
            expected.TryAssertObjectKey(
                "rpc",
                rpc =>
                {
                    var commandId = rpc.AssertKeyInto("command_id", v => v.GetString()!);
                    var unresolved = rpc.AssertKeyInto(
                        "unresolved_after_frame_indices",
                        v => v.EnumerateArray().Select(item => item.GetInt32()).Contains(atFrame));
                    if (unresolved)
                    {
                        Assert.False(projection.TryGetTerminal(commandId, out _));
                        assertions++;
                    }

                    // The two keys below belong to the same block and are asserted by the
                    // terminal-resolution pass; naming them here would compare the fixture to
                    // itself (#lzsubblockkeyset requires the SET be accounted for, not that
                    // every member be re-asserted at every call site).
                    rpc.ExcuseKey(
                        "resolves_after_frame_index",
                        "asserted where the terminal is observed, in the resolution pass below");
                    rpc.ExcuseKey(
                        "terminal_status",
                        "asserted where the terminal is observed, in the resolution pass below");
                });

            frameIndex++;
        }

        if (expected.TryAssertKeyDeep(
            "projection",
            () => ProjectionBody(projection.ToImage())))
        {
            assertions += expected.Element.GetProperty("projection")
                .GetProperty("commands").GetArrayLength() * 7 + 1;
        }

        expected.TryAssertKeyWith(
            "ignored_frame_indices",
            ignoredFrames =>
            {
                Assert.Equal(
                    ignoredFrames.EnumerateArray().Select(item => item.GetInt32()).ToArray(),
                    ignored);
                assertions++;
            });

        expected.TryAssertKeyWith(
            "terminal_after_frame_index",
            terminal =>
            {
                Assert.Equal(terminal.GetInt32(), terminalAfter);
                assertions++;
            });

        expected.TryAssertObjectKey(
            "rpc",
            expectedRpc =>
            {
                expectedRpc.AssertKey("resolves_after_frame_index", terminalAfter);
                var terminalEntry = Assert.Single(projection.ToImage().Commands);
                expectedRpc.AssertKey("terminal_status", StatusWire(terminalEntry.Status));
                expectedRpc.ExcuseKey(
                    "command_id",
                    "the id under test drives this replay rather than being compared to it");
                expectedRpc.ExcuseKey(
                    "unresolved_after_frame_indices",
                    "asserted per frame in the non-terminal pass above");
                assertions += 2;
            });

        // `conflict` used to GATE the three assertions below and never be asserted itself,
        // so a fixture flipped to false silently retired all four checks. It is now an
        // observation in its own right: did a terminal conflict actually happen?
        var sawConflict = conflictAfter >= 0;
        var declaresConflict = expected.TryAssertKeyWith(
            "conflict",
            want =>
            {
                Assert.Equal(want.GetBoolean(), sawConflict);
                assertions++;
            });

        if (declaresConflict && sawConflict)
        {
            expected.AssertKey("conflict_after_frame_index", conflictAfter);
            expected.AssertKey("conflict_command_id", conflictCommandId);
            Assert.NotNull(beforeConflict);
            expected.AssertKeyDeep(
                "projection_before_conflict",
                ProjectionBody(beforeConflict!));
            assertions += 3;
        }

        assertionsOut += assertions;
        expected.Verify();
    }

    /// <remarks>
    /// The projection is compared by DEEP EQUALITY, which covers its key set at every depth —
    /// so it discharges <c>#lzsubblockkeyset</c> through <c>AssertKeyDeep</c> rather than
    /// through a descent.
    /// </remarks>
    private static JsonNode? ProjectionBody(CommandProjectionImage actual)
    {
        var encoded = CommandWire.Serialize(new CommandMessage.Projection(actual));
        using var document = JsonDocument.Parse(encoded);
        return JsonNode.Parse(document.RootElement.GetProperty("CommandProjection").GetRawText());
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

    // #lzspecschemasoverride: resolved independently of the corpus root.
    private static string SchemaRoot() => SpecCorpus.RequireSchemasRoot();
}
