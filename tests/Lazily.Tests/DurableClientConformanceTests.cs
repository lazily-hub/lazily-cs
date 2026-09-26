using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class DurableClientConformanceTests
{
    [Fact]
    public void ReplaysCanonicalDurableClientCorpus()
    {
        using var document = SpecCorpus.Load("durable-client", "envelope_v1.json");
        var root = document.RootElement;
        Assert.False(root.GetProperty("owner_authority").GetBoolean());

        foreach (var vector in root.GetProperty("envelope_vectors").EnumerateArray())
        {
            var envelope = Envelope(vector.GetProperty("envelope"));
            var expected = FixtureAssertions.Wrap(
                vector.GetProperty("expected"), "durable-client/envelope_v1.json envelope expected");
            var validation = envelope.Validate();
            expected.AssertKey("accepted", validation == EnvelopeValidation.Accepted);
            expected.AssertKey("reason", ValidationName(validation));
            expected.AssertKey("payload_decoded", validation == EnvelopeValidation.Accepted);
            expected.Verify();
        }

        foreach (var vector in root.GetProperty("ordering_vectors").EnumerateArray())
        {
            var order = new DurableObservationOrder();
            foreach (var id in vector.GetProperty("observed_message_ids").EnumerateArray())
                order.Observe(new DurableEnvelope(1, id.GetString()!, 1, 1, []));
            Assert.Equal(vector.GetProperty("expected_delivery_order").EnumerateArray().Select(x => x.GetString()), order.MessageIds);
            Assert.Equal(vector.GetProperty("owner_order_inferred").GetBoolean(), order.OwnerOrderInferred);
        }

        foreach (var vector in root.GetProperty("projection_ordering_vectors").EnumerateArray())
        {
            var order = new DurableProjectionClient<string>();
            var actual = new List<string>();
            foreach (var position in vector.GetProperty("observed_source_positions").EnumerateArray())
                actual.Add(ProjectionStatusName(order.Apply(Projection(position.GetUInt64(), $"source-{position.GetUInt64()}"))));
            Assert.Equal(vector.GetProperty("expected_delivery_classification").EnumerateArray().Select(x => x.GetString()), actual);
            Assert.Equal(vector.GetProperty("expected_applied_positions").EnumerateArray().Select(x => x.GetUInt64()),
                order.AppliedPositions("owner-1"));
            Assert.False(order.MayAuthorizeTransition);
        }

        foreach (var vector in root.GetProperty("dedup_vectors").EnumerateArray())
        {
            var dedup = new DurableDeduplicator();
            var actual = vector.GetProperty("deliveries").EnumerateArray()
                .Select(x => dedup.Classify(Envelope(x)).ToString().ToLowerInvariant());
            Assert.Equal(vector.GetProperty("expected_classification").EnumerateArray().Select(x => x.GetString()), actual);
        }

        foreach (var vector in root.GetProperty("receipt_vectors").EnumerateArray())
        {
            var receipt = Receipt(vector.GetProperty("receipt"));
            Assert.Equal(Receipt(vector.GetProperty("expected_round_trip")), receipt);
            Assert.Equal(vector.GetProperty("transport_ack_equivalent").GetBoolean(), receipt.TransportAckEquivalent);
        }

        foreach (var vector in root.GetProperty("projection_fingerprint_vectors").EnumerateArray())
        {
            var left = Fingerprint(vector.GetProperty("left"));
            var right = Fingerprint(vector.GetProperty("right"));
            var expected = FixtureAssertions.Wrap(
                vector.GetProperty("expected"), "durable-client/envelope_v1.json projection fingerprint expected");
            expected.AssertKey("same_source", left.SourcePosition == right.SourcePosition);
            expected.AssertKey("same_fingerprint", left.Fingerprint == right.Fingerprint);
            expected.AssertKey("same_completeness", left.Completeness == right.Completeness);
            expected.AssertKey("equivalent", left.EquivalentTo(right));
            expected.Verify();
            Assert.False(left.MayAuthorizeTransition || right.MayAuthorizeTransition);
        }
    }

    [Fact]
    public void EnvelopeV1AndClientOnlyTierFailClosed()
    {
        var tiers = new DurableTierDeclaration();
        Assert.True(tiers.Core && tiers.Client);
        Assert.False(tiers.DurableHost || tiers.DistributedHost || tiers.AcceleratedHost);
        Assert.Equal(EnvelopeValidation.Accepted,
            new DurableEnvelope(1, "sample-owner/message-1", 7, 11, [0, 1, 127, 128, 255]).Validate());
        Assert.Equal(EnvelopeValidation.UnsupportedProtocolVersion,
            new DurableEnvelope(2, "sample-owner/message-2", 7, 11, [255]).Validate());
        Assert.Equal(EnvelopeValidation.InvalidMessageId, new DurableEnvelope(1, "", 7, 11, []).Validate());
        Assert.Equal(EnvelopeValidation.InvalidSchemaVersion,
            new DurableEnvelope(1, "sample-owner/message-4", 0, 11, []).Validate());

        var dedup = new DurableDeduplicator();
        var first = new DurableEnvelope(1, "sample-owner/message-4", 7, 11, [65]);
        Assert.Equal(DeliveryClassification.First, dedup.Classify(first));
        Assert.Equal(DeliveryClassification.Duplicate, dedup.Classify(first));
        Assert.Equal(DeliveryClassification.Conflict, dedup.Classify(first with { Payload = new byte[] { 66 } }));
    }

    [Fact]
    public void ProjectionIsOrderedDeduplicatedAndAdvisory()
    {
        var client = new DurableProjectionClient<string>();
        Assert.Equal(ProjectionApplyStatus.Buffered, client.Apply(Projection(2, "source-2")));
        Assert.Equal(ProjectionApplyStatus.Applied, client.Apply(Projection(1, "source-1")));
        Assert.Equal(ProjectionApplyStatus.Duplicate, client.Apply(Projection(2, "source-2")));
        Assert.Equal(ProjectionApplyStatus.IdentityConflict, client.Apply(Projection(1, "changed")));
        Assert.False(client.MayAuthorizeTransition);
        Assert.False(client.Latest("owner-1")!.MayAuthorizeTransition);
    }

    [Fact]
    public void UnsupportedProtocolDoesNotDecodeAndPubAckIsNotHostReceipt()
    {
        var decoded = false;
        var client = new DurableClient<string, string>(
            new FakeTransport(),
            value => System.Text.Encoding.UTF8.GetBytes(value),
            bytes => { decoded = true; return Projection(1, "source-1"); });
        Assert.Equal(EnvelopeValidation.UnsupportedProtocolVersion,
            client.Observe(new DurableEnvelope(2, "message-1", 7, 11, [255])).Validation);
        Assert.False(decoded);
        var ack = client.Publish("owners.commands", "message-1", 7, 11, "go");
        Assert.Equal((ulong)9, ack.Sequence);
        Assert.False(new DurableHostReceipt(1, "receipt-1", "message-1",
            DurableHostOutcome.Committed, 1).TransportAckEquivalent);
    }

    private static DurableProjectionUpdate<string> Projection(ulong position, string fingerprint) =>
        new(new ProjectionFingerprint("owner-1", position, fingerprint), position, "value");

    private static DurableEnvelope Envelope(System.Text.Json.JsonElement value) => new(
        value.GetProperty("protocol_version").GetUInt64(),
        value.GetProperty("message_id").GetString()!,
        value.GetProperty("schema_version").GetUInt64(),
        value.GetProperty("codec_version").GetUInt64(),
        value.GetProperty("payload").EnumerateArray().Select(x => x.GetByte()).ToArray());

    private static string ValidationName(EnvelopeValidation value) => value switch
    {
        EnvelopeValidation.Accepted => "accepted",
        EnvelopeValidation.UnsupportedProtocolVersion => "unsupported_protocol_version",
        EnvelopeValidation.InvalidMessageId => "invalid_message_id",
        EnvelopeValidation.InvalidSchemaVersion => "invalid_schema_version",
        EnvelopeValidation.InvalidCodecVersion => "invalid_codec_version",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ProjectionStatusName(ProjectionApplyStatus value) => value switch
    {
        ProjectionApplyStatus.Buffered => "buffered",
        ProjectionApplyStatus.Applied => "applied",
        ProjectionApplyStatus.Duplicate => "duplicate",
        ProjectionApplyStatus.IdentityConflict => "conflict",
        _ => throw new InvalidOperationException($"unexpected projection status {value}"),
    };

    private static DurableHostReceipt Receipt(System.Text.Json.JsonElement value) => new(
        value.GetProperty("protocol_version").GetUInt64(),
        value.GetProperty("receipt_id").GetString()!,
        value.GetProperty("message_id").GetString()!,
        Enum.Parse<DurableHostOutcome>(value.GetProperty("outcome").GetString()!, ignoreCase: true),
        value.GetProperty("owner_position").GetUInt64());

    private static ProjectionFingerprint Fingerprint(System.Text.Json.JsonElement value) => new(
        value.GetProperty("projection_id").GetString()!,
        value.GetProperty("source_position").GetUInt64(),
        value.GetProperty("fingerprint").GetString()!,
        value.GetProperty("completeness").GetString() == "complete_history"
            ? ProjectionCapability.CompleteHistory
            : ProjectionCapability.LatestStateOnly);

    private sealed class FakeTransport : INatsDurableClientTransport
    {
        public BrokerPubAck Publish(string subject, DurableEnvelope envelope) => new("OWNER", 9, false);
        public IDisposable Subscribe(string subject, Action<DurableEnvelope> handler) => new Subscription();
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
