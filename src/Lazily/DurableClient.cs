#pragma warning disable CS1591 // The compact wire model is documented as a unit below.

namespace Lazily;

public enum DurableCapabilityTier { Core, Client, DurableHost, DistributedHost, AcceleratedHost }

/// <summary>This binding is a typed client, not a native durable host.</summary>
public sealed record DurableTierDeclaration(
    bool Core = true,
    bool Client = true,
    bool DurableHost = false,
    bool DistributedHost = false,
    bool AcceleratedHost = false);

/// <summary>The exact durable-envelope-v1 transport shape; it carries no owner authority.</summary>
public sealed record DurableEnvelope(
    ulong ProtocolVersion,
    string MessageId,
    ulong SchemaVersion,
    ulong CodecVersion,
    IReadOnlyList<byte> Payload)
{
    public const ulong CurrentProtocolVersion = 1;

    public EnvelopeValidation Validate() =>
        ProtocolVersion != CurrentProtocolVersion
            ? EnvelopeValidation.UnsupportedProtocolVersion
            : string.IsNullOrEmpty(MessageId) ? EnvelopeValidation.InvalidMessageId
            : SchemaVersion is 0 or > uint.MaxValue ? EnvelopeValidation.InvalidSchemaVersion
            : CodecVersion is 0 or > uint.MaxValue ? EnvelopeValidation.InvalidCodecVersion
            : EnvelopeValidation.Accepted;

    public bool SameContent(DurableEnvelope other) =>
        ProtocolVersion == other.ProtocolVersion
        && SchemaVersion == other.SchemaVersion
        && CodecVersion == other.CodecVersion
        && Payload.SequenceEqual(other.Payload);
}

public enum EnvelopeValidation
{
    Accepted,
    UnsupportedProtocolVersion,
    InvalidMessageId,
    InvalidSchemaVersion,
    InvalidCodecVersion,
}

public enum DeliveryClassification { First, Duplicate, Conflict }

public sealed class DurableDeduplicator
{
    private readonly Dictionary<string, DurableEnvelope> _seen = [];
    public DeliveryClassification Classify(DurableEnvelope envelope)
    {
        if (!_seen.TryGetValue(envelope.MessageId, out var prior))
        {
            _seen.Add(envelope.MessageId, envelope with { Payload = envelope.Payload.ToArray() });
            return DeliveryClassification.First;
        }
        return prior.SameContent(envelope)
            ? DeliveryClassification.Duplicate
            : DeliveryClassification.Conflict;
    }
}

public sealed class DurableObservationOrder
{
    private readonly List<string> _messageIds = [];
    public IReadOnlyList<string> MessageIds => _messageIds;
    public bool OwnerOrderInferred => false;
    public void Observe(DurableEnvelope envelope) => _messageIds.Add(envelope.MessageId);
}

/// <summary>NATS publication acceptance; never evidence of a durable-owner commit.</summary>
public sealed record BrokerPubAck(string Stream, ulong Sequence, bool Duplicate);
public enum DurableHostOutcome { Committed, Duplicate, Conflict, Rejected }
public sealed record DurableHostReceipt(
    ulong ProtocolVersion,
    string ReceiptId,
    string MessageId,
    DurableHostOutcome Outcome,
    ulong OwnerPosition)
{
    public bool TransportAckEquivalent => false;
}

public enum ProjectionCapability { CompleteHistory, LatestStateOnly }
public sealed record ProjectionFingerprint(
    string ProjectionId,
    ulong SourcePosition,
    string Fingerprint,
    ProjectionCapability Completeness = ProjectionCapability.CompleteHistory)
{
    public bool EquivalentTo(ProjectionFingerprint other) =>
        ProjectionId == other.ProjectionId
        && SourcePosition == other.SourcePosition
        && Fingerprint == other.Fingerprint
        && Completeness == other.Completeness;

    public bool MayAuthorizeTransition => false;
}
public sealed record DurableProjectionUpdate<T>(
    ProjectionFingerprint Fingerprint,
    ulong ProjectionVersion,
    T Value)
{
    public bool MayAuthorizeTransition => false;
}

public enum ProjectionApplyStatus { Applied, Buffered, Duplicate, IdentityConflict, Invalid }

public interface INatsDurableClientTransport
{
    BrokerPubAck Publish(string subject, DurableEnvelope envelope);
    IDisposable Subscribe(string subject, Action<DurableEnvelope> handler);
}

public sealed record DurableObserveResult(
    EnvelopeValidation Validation,
    DeliveryClassification? Classification,
    ProjectionApplyStatus? ProjectionStatus);

public sealed class DurableProjectionClient<T>
{
    private sealed class OwnerState
    {
        public ulong SourcePosition { get; set; }
        public ulong ProjectionVersion { get; set; }
        public Dictionary<ulong, string> Fingerprints { get; } = [];
        public Dictionary<ulong, DurableProjectionUpdate<T>> Pending { get; } = [];
        public List<ulong> AppliedPositions { get; } = [];
        public DurableProjectionUpdate<T>? Latest { get; set; }
    }

    private readonly Dictionary<string, OwnerState> _owners = [];
    public bool MayAuthorizeTransition => false;

    public DurableProjectionUpdate<T>? Latest(string ownerId) =>
        _owners.TryGetValue(ownerId, out var state) ? state.Latest : null;

    public IReadOnlyList<ulong> AppliedPositions(string projectionId) =>
        _owners.TryGetValue(projectionId, out var state) ? state.AppliedPositions : [];

    public ProjectionApplyStatus Apply(DurableProjectionUpdate<T> update)
    {
        if (string.IsNullOrEmpty(update.Fingerprint.ProjectionId)
            || string.IsNullOrEmpty(update.Fingerprint.Fingerprint)
            || update.ProjectionVersion == 0
            || update.Fingerprint.Completeness != ProjectionCapability.CompleteHistory)
        {
            return ProjectionApplyStatus.Invalid;
        }

        if (!_owners.TryGetValue(update.Fingerprint.ProjectionId, out var state))
        {
            state = new OwnerState();
            _owners.Add(update.Fingerprint.ProjectionId, state);
        }
        if (state.Fingerprints.TryGetValue(update.Fingerprint.SourcePosition, out var fingerprint))
        {
            return string.Equals(fingerprint, update.Fingerprint.Fingerprint, StringComparison.Ordinal)
                ? ProjectionApplyStatus.Duplicate
                : ProjectionApplyStatus.IdentityConflict;
        }
        if (state.Pending.TryGetValue(update.Fingerprint.SourcePosition, out var buffered))
        {
            return buffered.Fingerprint.Fingerprint == update.Fingerprint.Fingerprint
                ? ProjectionApplyStatus.Duplicate
                : ProjectionApplyStatus.IdentityConflict;
        }
        if (update.Fingerprint.SourcePosition > state.SourcePosition + 1)
        {
            state.Pending.Add(update.Fingerprint.SourcePosition, update);
            return ProjectionApplyStatus.Buffered;
        }
        if (update.Fingerprint.SourcePosition <= state.SourcePosition)
        {
            return ProjectionApplyStatus.IdentityConflict;
        }
        if (update.ProjectionVersion <= state.ProjectionVersion) return ProjectionApplyStatus.Invalid;
        state.SourcePosition = update.Fingerprint.SourcePosition;
        state.ProjectionVersion = update.ProjectionVersion;
        state.Fingerprints.Add(update.Fingerprint.SourcePosition, update.Fingerprint.Fingerprint);
        state.AppliedPositions.Add(update.Fingerprint.SourcePosition);
        state.Latest = update;
        while (state.Pending.Remove(state.SourcePosition + 1, out var contiguous))
        {
            state.SourcePosition = contiguous.Fingerprint.SourcePosition;
            state.ProjectionVersion = contiguous.ProjectionVersion;
            state.Fingerprints.Add(contiguous.Fingerprint.SourcePosition, contiguous.Fingerprint.Fingerprint);
            state.AppliedPositions.Add(contiguous.Fingerprint.SourcePosition);
            state.Latest = contiguous;
        }
        return ProjectionApplyStatus.Applied;
    }
}

public sealed class DurableClient<TIngress, TProjection>
{
    private readonly INatsDurableClientTransport _transport;
    private readonly Func<TIngress, IReadOnlyList<byte>> _encode;
    private readonly Func<IReadOnlyList<byte>, DurableProjectionUpdate<TProjection>> _decode;

    public DurableClient(
        INatsDurableClientTransport transport,
        Func<TIngress, IReadOnlyList<byte>> encode,
        Func<IReadOnlyList<byte>, DurableProjectionUpdate<TProjection>> decode)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _encode = encode ?? throw new ArgumentNullException(nameof(encode));
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));
    }

    public DurableProjectionClient<TProjection> Projection { get; } = new();
    public DurableDeduplicator Deduplicator { get; } = new();
    public DurableObservationOrder ObservationOrder { get; } = new();
    public bool MayAuthorizeTransition => false;

    public BrokerPubAck Publish(
        string subject,
        string messageId,
        ulong schemaVersion,
        ulong codecVersion,
        TIngress value)
    {
        var envelope = new DurableEnvelope(1, messageId, schemaVersion, codecVersion, _encode(value));
        if (envelope.Validate() != EnvelopeValidation.Accepted)
        {
            throw new ArgumentException("Invalid durable envelope.", nameof(messageId));
        }
        return _transport.Publish(subject, envelope);
    }

    /// <summary>Validates the outer version before invoking the payload decoder.</summary>
    public DurableObserveResult Observe(DurableEnvelope envelope)
    {
        var validation = envelope.Validate();
        if (validation != EnvelopeValidation.Accepted)
            return new DurableObserveResult(validation, null, null);
        ObservationOrder.Observe(envelope);
        var classification = Deduplicator.Classify(envelope);
        if (classification != DeliveryClassification.First)
            return new DurableObserveResult(validation, classification, null);
        return new DurableObserveResult(validation, classification, Projection.Apply(_decode(envelope.Payload)));
    }

    public IDisposable Subscribe(string subject) => _transport.Subscribe(subject, ObserveAndDiscard);

    private void ObserveAndDiscard(DurableEnvelope envelope) => _ = Observe(envelope);
}

#pragma warning restore CS1591
