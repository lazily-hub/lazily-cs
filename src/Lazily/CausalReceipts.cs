using System.Text;
using System.Text.Json;

namespace Lazily;

/// <summary>The generic outcome vocabulary for causally linked work.</summary>
public enum ReceiptOutcome
{
    /// <summary>The causation was seen.</summary>
    Observed,

    /// <summary>The causation was accepted or queued.</summary>
    Accepted,

    /// <summary>The causation was successfully applied.</summary>
    Applied,

    /// <summary>The causation was terminally rejected.</summary>
    Rejected,
}

/// <summary>Receipt outcome helpers.</summary>
public static class ReceiptOutcomeExtensions
{
    /// <summary>Returns whether the outcome is terminal authority for its causation.</summary>
    public static bool IsTerminal(this ReceiptOutcome outcome) =>
        outcome is ReceiptOutcome.Applied or ReceiptOutcome.Rejected;
}

/// <summary>One idempotent receipt event for a command or effect causation.</summary>
public sealed record CausalReceipt(
    string ReceiptId,
    string CausationId,
    string Observer,
    ulong Generation,
    ReceiptOutcome Outcome,
    string? Reason = null,
    string? PayloadHash = null)
{
    /// <summary>Constructs a non-terminal observed receipt.</summary>
    public static CausalReceipt Observed(
        string receiptId,
        string causationId,
        string observer,
        ulong generation) =>
        new(receiptId, causationId, observer, generation, ReceiptOutcome.Observed);

    /// <summary>Constructs a non-terminal accepted receipt.</summary>
    public static CausalReceipt Accepted(
        string receiptId,
        string causationId,
        string observer,
        ulong generation) =>
        new(receiptId, causationId, observer, generation, ReceiptOutcome.Accepted);

    /// <summary>Constructs a terminal applied receipt.</summary>
    public static CausalReceipt Applied(
        string receiptId,
        string causationId,
        string observer,
        ulong generation) =>
        new(receiptId, causationId, observer, generation, ReceiptOutcome.Applied);

    /// <summary>Constructs a terminal rejected receipt.</summary>
    public static CausalReceipt Rejected(
        string receiptId,
        string causationId,
        string observer,
        ulong generation) =>
        new(receiptId, causationId, observer, generation, ReceiptOutcome.Rejected);
}

/// <summary>A defensively owned batch carried by the CausalReceipts wire envelope.</summary>
public sealed record CausalReceipts
{
    /// <summary>Copies a receipt sequence into an immutable batch boundary.</summary>
    public CausalReceipts(IEnumerable<CausalReceipt> receipts)
    {
        Guard.NotNull(receipts, nameof(receipts));
        Receipts = receipts.ToArray();
    }

    /// <summary>The ordered receipt events in the envelope.</summary>
    public IReadOnlyList<CausalReceipt> Receipts { get; }
}

/// <summary>The result of folding one receipt into a projection.</summary>
public abstract record ReceiptApplyStatus
{
    private ReceiptApplyStatus()
    {
    }

    /// <summary>The receipt was recorded.</summary>
    public sealed record Recorded : ReceiptApplyStatus;

    /// <summary>The receipt id had already been observed.</summary>
    public sealed record Duplicate : ReceiptApplyStatus;

    /// <summary>The receipt did not match the current authority generation.</summary>
    public sealed record StaleGeneration(ulong Expected, ulong Actual) : ReceiptApplyStatus;

    /// <summary>A different terminal outcome already exists for the causation.</summary>
    public sealed record TerminalConflict(
        string CausationId,
        ReceiptOutcome Existing,
        ReceiptOutcome Incoming) : ReceiptApplyStatus;
}

/// <summary>
/// Idempotent, generation-guarded projection of receipt events.
/// Transport acknowledgement is deliberately outside this type.
/// </summary>
public sealed class ReceiptProjection
{
    private readonly Dictionary<string, CausalReceipt> _receiptsById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CausalReceipt> _latestByCausation =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CausalReceipt> _terminalByCausation =
        new(StringComparer.Ordinal);
    private readonly SortedSet<string> _staleReceiptIds = new(StringComparer.Ordinal);

    /// <summary>Number of unique current and stale receipt ids observed.</summary>
    public int ReceiptCount => _receiptsById.Count + _staleReceiptIds.Count;

    /// <summary>Sorted snapshot of receipt ids ignored by the generation guard.</summary>
    public IReadOnlyList<string> StaleReceiptIds => _staleReceiptIds.ToArray();

    /// <summary>Fold one receipt into the current projection.</summary>
    public ReceiptApplyStatus Observe(ulong? currentGeneration, CausalReceipt receipt)
    {
        Guard.NotNull(receipt, nameof(receipt));

        if (_receiptsById.ContainsKey(receipt.ReceiptId)
            || _staleReceiptIds.Contains(receipt.ReceiptId))
        {
            return new ReceiptApplyStatus.Duplicate();
        }

        if (currentGeneration is { } expected && receipt.Generation != expected)
        {
            _staleReceiptIds.Add(receipt.ReceiptId);
            return new ReceiptApplyStatus.StaleGeneration(expected, receipt.Generation);
        }

        if (receipt.Outcome.IsTerminal()
            && _terminalByCausation.TryGetValue(receipt.CausationId, out var existing)
            && existing.Outcome != receipt.Outcome)
        {
            return new ReceiptApplyStatus.TerminalConflict(
                receipt.CausationId,
                existing.Outcome,
                receipt.Outcome);
        }

        if (receipt.Outcome.IsTerminal())
        {
            _terminalByCausation.TryAdd(receipt.CausationId, receipt);
        }

        _latestByCausation[receipt.CausationId] = receipt;
        _receiptsById.Add(receipt.ReceiptId, receipt);
        return new ReceiptApplyStatus.Recorded();
    }

    /// <summary>Returns the latest recorded receipt for a causation id.</summary>
    public CausalReceipt? LatestFor(string causationId) =>
        _latestByCausation.GetValueOrDefault(causationId);

    /// <summary>Returns the terminal receipt for a causation id, when present.</summary>
    public CausalReceipt? TerminalFor(string causationId) =>
        _terminalByCausation.GetValueOrDefault(causationId);

    /// <summary>Returns whether a current or stale receipt id has been observed.</summary>
    public bool ContainsReceipt(string receiptId) =>
        _receiptsById.ContainsKey(receiptId) || _staleReceiptIds.Contains(receiptId);
}

/// <summary>Exact externally tagged codec for the lazily receipts schema.</summary>
public static class CausalReceiptWire
{
    /// <summary>Deserializes and structurally validates a CausalReceipts envelope.</summary>
    public static CausalReceipts Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireObjectWithProperties(root, "CausalReceipts");
        var body = root.GetProperty("CausalReceipts");
        RequireObjectWithProperties(body, "receipts");
        var receiptsElement = body.GetProperty("receipts");
        if (receiptsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("CausalReceipts.receipts must be an array.");
        }

        var receipts = new List<CausalReceipt>();
        foreach (var item in receiptsElement.EnumerateArray())
        {
            RequireObjectWithProperties(
                item,
                "receipt_id",
                "causation_id",
                "observer",
                "generation",
                "outcome",
                "reason",
                "payload_hash");
            receipts.Add(
                new CausalReceipt(
                    RequireString(item, "receipt_id"),
                    RequireString(item, "causation_id"),
                    RequireString(item, "observer"),
                    RequireUInt64(item, "generation"),
                    ParseOutcome(RequireString(item, "outcome")),
                    RequireNullableString(item, "reason"),
                    RequireNullableString(item, "payload_hash")));
        }

        return new CausalReceipts(receipts);
    }

    /// <summary>Serializes a batch in the exact externally tagged schema shape.</summary>
    public static string Serialize(CausalReceipts message)
    {
        Guard.NotNull(message, nameof(message));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("CausalReceipts");
            writer.WriteStartObject();
            writer.WritePropertyName("receipts");
            writer.WriteStartArray();
            foreach (var receipt in message.Receipts)
            {
                writer.WriteStartObject();
                writer.WriteString("receipt_id", receipt.ReceiptId);
                writer.WriteString("causation_id", receipt.CausationId);
                writer.WriteString("observer", receipt.Observer);
                writer.WriteNumber("generation", receipt.Generation);
                writer.WriteString("outcome", FormatOutcome(receipt.Outcome));
                WriteNullableString(writer, "reason", receipt.Reason);
                WriteNullableString(writer, "payload_hash", receipt.PayloadHash);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void RequireObjectWithProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected an object.");
        }

        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length
            || names.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
        {
            throw new JsonException($"Expected properties: {string.Join(", ", names)}.");
        }
    }

    private static string RequireString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"{name} must be a string.");
        }

        var result = value.GetString()!;
        if (result.Length == 0)
        {
            throw new JsonException($"{name} must not be empty.");
        }

        return result;
    }

    private static ulong RequireUInt64(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var result))
        {
            throw new JsonException($"{name} must be a non-negative integer.");
        }

        return result;
    }

    private static string? RequireNullableString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new JsonException($"{name} must be a string or null."),
        };
    }

    private static ReceiptOutcome ParseOutcome(string outcome) =>
        outcome switch
        {
            "observed" => ReceiptOutcome.Observed,
            "accepted" => ReceiptOutcome.Accepted,
            "applied" => ReceiptOutcome.Applied,
            "rejected" => ReceiptOutcome.Rejected,
            _ => throw new JsonException($"Unknown receipt outcome: {outcome}."),
        };

    private static string FormatOutcome(ReceiptOutcome outcome) =>
        outcome switch
        {
            ReceiptOutcome.Observed => "observed",
            ReceiptOutcome.Accepted => "accepted",
            ReceiptOutcome.Applied => "applied",
            ReceiptOutcome.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
