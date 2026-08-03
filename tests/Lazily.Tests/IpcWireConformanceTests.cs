using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class IpcWireConformanceTests
{
    private const int ExpectedSnapshotFixtures = 3;
    private const int ExpectedDeltaFixtures = 4;
    private const int ExpectedCrdtFrames = 4;

    [Fact]
    public void Snapshot_corpus_round_trips_and_validates_against_schema()
    {
        var fixtures = SpecCorpus.FixtureNames("")
            .Where(name => name.StartsWith("snapshot_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(ExpectedSnapshotFixtures, fixtures.Length);
        var schema = LoadSchema("snapshot.json");

        foreach (var fixture in fixtures)
        {
            using var document = SpecCorpus.Load("", fixture);
            var wire = document.RootElement.GetProperty("wire");
            var message = Assert.IsType<SnapshotMessage>(IpcWire.Deserialize(wire.GetRawText()));
            Assert.True(message.Nodes.Count > 0, fixture);
            AssertRoundTripAndSchema(wire, message, schema, fixture);
            AssertSnapshotAssertions(document.RootElement, message, fixture);
        }
    }

    /// <summary>
    /// The fixture's own <c>assertions</c> block (<c>#lznullformblind</c>).
    /// </summary>
    /// <remarks>
    /// A round-trip proves the codec agrees with itself. It says nothing about what the
    /// corpus CLAIMS, and until this existed these blocks were carried by fixtures this
    /// runner opens and bound by nothing at all — not unread, unreachable, because no
    /// tracker ever saw them. Every rung of the assertion ledger is scoped to a block a
    /// runner bound, so eight silent keys per fixture reported exactly nothing.
    /// </remarks>
    private static void AssertSnapshotAssertions(
        JsonElement root,
        SnapshotMessage message,
        string fixture)
    {
        var assertions = FixtureAssertions.Of(root, "assertions", fixture);
        assertions.AssertKey("epoch", message.Epoch);
        assertions.AssertKey("node_count", message.Nodes.Count);
        assertions.AssertKey("edge_count", message.Edges.Count);
        assertions.AssertKey("root_count", message.Roots.Count);
        assertions.TryAssertKeyWith(
            "first_node_type_tag",
            want => Assert.Equal(want.GetString(), message.Nodes[0].TypeTag));
        assertions.TryAssertKeyWith(
            "first_node_state_kind",
            want => Assert.Equal(want.GetString(), StateKind(message.Nodes[0].State)));
        assertions.TryAssertKeyWith(
            "has_opaque_node",
            want => Assert.Equal(
                want.GetBoolean(),
                message.Nodes.Any(node => node.State is NodeState.Opaque)));
        assertions.TryAssertKeyWith(
            "opaque_node_id",
            want => Assert.Equal(
                want.GetUInt64(),
                message.Nodes.Single(node => node.State is NodeState.Opaque).Node));
        // The three blob keys are one claim about one descriptor, so they are resolved
        // from the SAME node rather than from three independent lookups: a fixture whose
        // blob moved to a different node would otherwise still satisfy all three.
        if (message.Nodes[0].State is NodeState.SharedBlob shared)
        {
            assertions.TryAssertKeyWith(
                "blob_offset",
                want => Assert.Equal(want.GetUInt64(), shared.Blob.Offset));
            assertions.TryAssertKeyWith(
                "blob_len",
                want => Assert.Equal(want.GetUInt64(), shared.Blob.Length));
            assertions.TryAssertKeyWith(
                "blob_epoch",
                want => Assert.Equal(want.GetUInt64(), shared.Blob.Epoch));
        }

        assertions.Verify();
    }

    private static string StateKind(NodeState state) => state switch
    {
        NodeState.Payload => "Payload",
        NodeState.SharedBlob => "SharedBlob",
        NodeState.Opaque => "Opaque",
        _ => throw new InvalidOperationException($"unhandled node state {state.GetType().Name}"),
    };

    [Fact]
    public void Delta_corpus_round_trips_and_validates_against_schema()
    {
        var fixtures = SpecCorpus.FixtureNames("")
            .Where(name => name.StartsWith("delta_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(ExpectedDeltaFixtures, fixtures.Length);
        var schema = LoadSchema("delta.json");

        foreach (var fixture in fixtures)
        {
            using var document = SpecCorpus.Load("", fixture);
            var wire = document.RootElement.GetProperty("wire");
            var message = Assert.IsType<DeltaMessage>(IpcWire.Deserialize(wire.GetRawText()));
            Assert.True(message.Epoch > message.BaseEpoch, fixture);
            AssertRoundTripAndSchema(wire, message, schema, fixture);
            AssertDeltaAssertions(document.RootElement, message, fixture);
        }
    }

    /// <summary>The delta fixture's own <c>assertions</c> block (<c>#lznullformblind</c>).</summary>
    private static void AssertDeltaAssertions(
        JsonElement root,
        DeltaMessage message,
        string fixture)
    {
        var assertions = FixtureAssertions.Of(root, "assertions", fixture);
        assertions.AssertKey("base_epoch", message.BaseEpoch);
        assertions.AssertKey("epoch", message.Epoch);
        assertions.TryAssertKeyWith("op_count", want => Assert.Equal(want.GetInt32(), message.Ops.Count));
        assertions.TryAssertKeyWith(
            "is_sequential",
            want => Assert.Equal(want.GetBoolean(), message.Epoch == message.BaseEpoch + 1));
        assertions.TryAssertKeyWith(
            "resync_after_epoch_10",
            want => Assert.Equal(want.GetBoolean(), message.BaseEpoch > 10));
        assertions.TryAssertKeyWith(
            "has_all_op_variants",
            want => Assert.Equal(
                want.GetBoolean(),
                message.Ops.Select(op => op.GetType().Name).Distinct().Count() == message.Ops.Count));
        assertions.TryAssertKeyWith(
            "first_op_kind",
            want => Assert.Equal(want.GetString(), message.Ops[0].GetType().Name));
        assertions.TryAssertKeyWith(
            "first_op_payload_kind",
            want => Assert.Equal(want.GetString(), PayloadKind(message.Ops[0])));
        assertions.TryAssertKeyWith(
            "first_op_payload_backend",
            want => Assert.Equal(want.GetString(), PayloadBackend(message.Ops[0])));
        assertions.Verify();
    }

    private static string PayloadKind(DeltaOp op) => Payload(op) switch
    {
        IpcValue.Inline => "Inline",
        IpcValue.SharedBlob => "SharedBlob",
        _ => throw new InvalidOperationException($"op {op.GetType().Name} carries no payload"),
    };

    private static string? PayloadBackend(DeltaOp op) =>
        Payload(op) is IpcValue.SharedBlob { Blob.Backend: { } backend }
            ? backend switch
            {
                BlobBackendKind.Shm => "shm",
                BlobBackendKind.Arrow => "arrow",
                BlobBackendKind.InProcess => "in_process",
                _ => throw new InvalidOperationException($"unhandled blob backend {backend}"),
            }
            : null;

    private static IpcValue Payload(DeltaOp op) => op switch
    {
        DeltaOp.CellSet set => set.Payload,
        DeltaOp.SlotValue value => value.Payload,
        _ => throw new InvalidOperationException($"op {op.GetType().Name} carries no payload"),
    };

    [Fact]
    public void CrdtSync_corpus_round_trips_and_validates_against_schema()
    {
        var fixtures = SpecCorpus.FixtureNames("distributed")
            .Where(name => name.StartsWith("crdt_sync_", StringComparison.Ordinal))
            .ToArray();
        var fixture = Assert.Single(fixtures);
        using var document = SpecCorpus.Load("distributed", fixture);
        var frames = document.RootElement.GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(ExpectedCrdtFrames, frames.Length);
        var schema = LoadSchema("distributed.json");

        foreach (var frame in frames)
        {
            var label = frame.GetProperty("label").GetString()!;
            var wire = frame.GetProperty("wire");
            var message = Assert.IsType<CrdtSyncMessage>(IpcWire.Deserialize(wire.GetRawText()));
            AssertRoundTripAndSchema(wire, message, schema, label);

            // Each frame's own `assertions` block (#lznullformblind) — four blocks
            // carried by a fixture this runner opens and bound by nothing.
            var assertions = FixtureAssertions.Of(
                frame,
                "assertions",
                $"distributed/crdt_sync_frames.json {label}");
            assertions.TryAssertKeyWith(
                "frontier_len",
                want => Assert.Equal(want.GetInt32(), message.Frontier?.Count ?? 0));
            assertions.AssertKey("op_count", message.Ops.Count);
            assertions.TryAssertKeyWith(
                "frontier_omitted",
                // An omitted frontier decodes as absent, not as an empty list: the two are
                // the same to a runner that only counts, which is why the count above
                // cannot carry this claim.
                want => Assert.Equal(want.GetBoolean(), message.Frontier is null or { Count: 0 }));
            assertions.TryAssertKeyWith(
                "has_keyed_op",
                want => Assert.Equal(want.GetBoolean(), message.Ops.Any(op => op.Key is not null)));
            assertions.TryAssertKeyWith(
                "has_keyless_op",
                want => Assert.Equal(want.GetBoolean(), message.Ops.Any(op => op.Key is null)));
            assertions.Verify();
        }
    }

    [Fact]
    public void Delta_codec_covers_all_ten_externally_tagged_operations()
    {
        var inline = new IpcValue.Inline([1, 2, 3]);
        var message = new DeltaMessage(
            BaseEpoch: 10,
            Epoch: 11,
            Ops:
            [
                new DeltaOp.CellSet(1, inline),
                new DeltaOp.SlotValue(2, inline),
                new DeltaOp.Invalidate(3),
                new DeltaOp.NodeAdd(4, "u64", new NodeState.Payload([4]), "scores/alice"),
                new DeltaOp.NodeRemove(5),
                new DeltaOp.EdgeAdd(2, 1),
                new DeltaOp.EdgeRemove(3, 1),
                new DeltaOp.QueuePush(6, inline),
                new DeltaOp.QueuePop(6),
                new DeltaOp.QueueClose(6),
            ]);

        var json = IpcWire.Serialize(message);
        var decoded = Assert.IsType<DeltaMessage>(IpcWire.Deserialize(json));
        Assert.Equal(10, decoded.Ops.Count);
        Assert.Equal(
            [
                "CellSet",
                "SlotValue",
                "Invalidate",
                "NodeAdd",
                "NodeRemove",
                "EdgeAdd",
                "EdgeRemove",
                "QueuePush",
                "QueuePop",
                "QueueClose",
            ],
            JsonNode.Parse(json)!["Delta"]!["ops"]!.AsArray()
                .Select(operation => operation!.AsObject().Single().Key));
        AssertSchemaValid("delta.json", json, "all DeltaOp variants");
    }

    [Fact]
    public void Codec_distinguishes_omitted_optional_keys_from_required_null_crdt_key()
    {
        var snapshot = new SnapshotMessage(
            1,
            [new NodeSnapshot(1, "opaque", new NodeState.Opaque())],
            [],
            [1]);
        var snapshotJson = JsonNode.Parse(IpcWire.Serialize(snapshot))!;
        Assert.False(snapshotJson["Snapshot"]!["nodes"]![0]!.AsObject().ContainsKey("key"));

        var sync = new CrdtSyncMessage(
            [new CrdtOp(1, null, new WireStamp(2, 0, 1), new IpcValue.Inline([9]))]);
        var syncJson = JsonNode.Parse(IpcWire.Serialize(sync))!;
        Assert.True(syncJson["CrdtSync"]!["ops"]![0]!.AsObject().ContainsKey("key"));
        Assert.Null(syncJson["CrdtSync"]!["ops"]![0]!["key"]);
        Assert.False(syncJson["CrdtSync"]!.AsObject().ContainsKey("frontier"));
        AssertSchemaValid("distributed.json", syncJson.ToJsonString(), "suppressed frontier");
    }

    [Fact]
    public void Codec_writes_byte_arrays_and_blob_backend_discriminators_verbatim()
    {
        var inline = new DeltaMessage(
            0,
            1,
            [new DeltaOp.CellSet(1, new IpcValue.Inline([0, 127, 255]))]);
        var inlineJson = IpcWire.Serialize(inline);
        Assert.Contains("\"Inline\":[0,127,255]", inlineJson, StringComparison.Ordinal);
        Assert.DoesNotContain("AH//", inlineJson, StringComparison.Ordinal);

        var arrow = new DeltaMessage(
            1,
            2,
            [
                new DeltaOp.SlotValue(
                    1,
                    new IpcValue.SharedBlob(
                        new ShmBlobRef(4, 8, 1, 2, 99, BlobBackendKind.Arrow))),
            ]);
        var arrowJson = IpcWire.Serialize(arrow);
        Assert.Contains("\"backend\":\"arrow\"", arrowJson, StringComparison.Ordinal);
        AssertSchemaValid("delta.json", arrowJson, "Arrow SharedBlob");

        var explicitShm = arrow with
        {
            Ops =
            [
                new DeltaOp.SlotValue(
                    1,
                    new IpcValue.SharedBlob(
                        new ShmBlobRef(4, 8, 1, 2, 99, BlobBackendKind.Shm))),
            ],
        };
        Assert.DoesNotContain(
            "\"backend\"",
            IpcWire.Serialize(explicitShm),
            StringComparison.Ordinal);
    }

    [Fact]
    public void WireStamp_round_trips_runtime_stamp_and_rejects_negative_components()
    {
        var runtime = new HlcStamp(12, 3, 2);
        Assert.Equal(runtime, WireStamp.FromRuntime(runtime).ToRuntime());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WireStamp.FromRuntime(new HlcStamp(-1, 0, 1)));
    }

    private static void AssertRoundTripAndSchema(
        JsonElement expected,
        IpcMessage message,
        JsonSchema schema,
        string label)
    {
        var actualJson = IpcWire.Serialize(message);
        var expectedNode = JsonNode.Parse(expected.GetRawText());
        var actualNode = JsonNode.Parse(actualJson);
        Assert.True(JsonNode.DeepEquals(expectedNode, actualNode), label);

        using var actual = JsonDocument.Parse(actualJson);
        var result = schema.Evaluate(actual.RootElement);
        Assert.True(result.IsValid, $"{label}: {result}");
    }

    private static void AssertSchemaValid(string schemaName, string json, string label)
    {
        using var document = JsonDocument.Parse(json);
        var result = LoadSchema(schemaName).Evaluate(document.RootElement);
        Assert.True(result.IsValid, $"{label}: {result}");
    }

    private static JsonSchema LoadSchema(string schemaName)
    {
        var schemaRoot = SchemaRoot();
        var options = new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry(),
        };

        using (var definitions = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, "defs.json"))))
        {
            _ = JsonSchema.Build(definitions.RootElement.Clone(), options);
        }

        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, schemaName)));
        return JsonSchema.Build(schema.RootElement.Clone(), options);
    }

    private static string SchemaRoot()
    {
        var corpus = SpecCorpus.Root;
        Assert.NotNull(corpus);
        var root = Path.Combine(Path.GetDirectoryName(corpus)!, "schemas");
        Assert.True(Directory.Exists(root), $"missing lazily-spec schemas: {root}");
        return root;
    }
}
