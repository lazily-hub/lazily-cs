using System.Text;
using System.Text.Json;

namespace Lazily;

/// <summary>How a controller deduplicates submitted commands.</summary>
public enum DedupePolicy
{
    /// <summary>Do not deduplicate.</summary>
    None,

    /// <summary>Deduplicate commands that share an idempotency key.</summary>
    SameIdempotencyKey,

    /// <summary>Deduplicate commands that share a command id.</summary>
    SameCommandId,
}

/// <summary>Per-command admission policy.</summary>
public sealed record CommandPolicy(
    DedupePolicy Dedupe,
    bool Supersede,
    bool CancelOnPreempt);

/// <summary>A command envelope whose payload remains namespace-owned opaque bytes.</summary>
public sealed record CommandSubmit(
    string CommandId,
    string CausationId,
    string Source,
    string Target,
    string Namespace,
    string Name,
    ulong AuthorityGeneration,
    string IdempotencyKey,
    ulong DeadlineMs,
    CommandPolicy Policy,
    string PayloadType,
    string PayloadHash,
    IpcValue Payload,
    IReadOnlyList<string> RequiredFeatures);

/// <summary>A request to preempt a still non-terminal command.</summary>
public sealed record CommandCancel(
    string CommandId,
    string CausationId,
    string Source,
    ulong AuthorityGeneration,
    string? Reason);

/// <summary>Progress kinds. None of these is terminal proof.</summary>
public enum CommandEventKind
{
    /// <summary>The command was observed.</summary>
    Observed,

    /// <summary>The command was admitted.</summary>
    Accepted,

    /// <summary>Execution started.</summary>
    Started,

    /// <summary>Execution reported progress.</summary>
    Progress,

    /// <summary>Cancellation was requested or observed.</summary>
    Cancelled,

    /// <summary>Supersession was requested or observed.</summary>
    Superseded,

    /// <summary>A timeout was observed.</summary>
    TimedOut,
}

/// <summary>One idempotent command progress event.</summary>
public sealed record CommandEvent(
    string EventId,
    string CommandId,
    CommandEventKind Kind,
    ulong Generation,
    string? Detail);

/// <summary>A batch of progress events.</summary>
public sealed record CommandEvents(IReadOnlyList<CommandEvent> Events);

/// <summary>Folded command status.</summary>
public enum CommandStatus
{
    /// <summary>The submit frame was admitted into the projection.</summary>
    Submitted,

    /// <summary>The command was observed or accepted.</summary>
    Accepted,

    /// <summary>The command started or reported progress.</summary>
    Running,

    /// <summary>A causal receipt proved the effect applied.</summary>
    Applied,

    /// <summary>A causal receipt terminally rejected the effect.</summary>
    Rejected,

    /// <summary>A rejected receipt proved cancellation.</summary>
    Cancelled,

    /// <summary>A rejected receipt proved supersession.</summary>
    Superseded,

    /// <summary>A rejected receipt proved timeout.</summary>
    TimedOut,
}

/// <summary>Command-status helpers.</summary>
public static class CommandStatusExtensions
{
    /// <summary>Returns whether a status is backed by terminal receipt authority.</summary>
    public static bool IsTerminal(this CommandStatus status) =>
        status is CommandStatus.Applied
            or CommandStatus.Rejected
            or CommandStatus.Cancelled
            or CommandStatus.Superseded
            or CommandStatus.TimedOut;
}

/// <summary>The queryable image of one command.</summary>
public sealed record CommandProjectionEntry(
    string CommandId,
    CommandStatus Status,
    bool Terminal,
    ulong Generation,
    string? Reason,
    string? TerminalReceiptId,
    string? LastEventId);

/// <summary>A reconnect-resync image of all known command state.</summary>
public sealed record CommandProjectionImage(
    ulong Generation,
    IReadOnlyList<CommandProjectionEntry> Commands);

/// <summary>The four externally tagged command-plane frames.</summary>
public abstract record CommandMessage
{
    private CommandMessage()
    {
    }

    /// <summary>A command submit frame.</summary>
    public sealed record Submit(CommandSubmit Value) : CommandMessage;

    /// <summary>A command cancel frame.</summary>
    public sealed record Cancel(CommandCancel Value) : CommandMessage;

    /// <summary>A command event-batch frame.</summary>
    public sealed record Events(CommandEvents Value) : CommandMessage;

    /// <summary>A reconnect projection frame.</summary>
    public sealed record Projection(CommandProjectionImage Value) : CommandMessage;
}

/// <summary>The result of folding a command frame or receipt.</summary>
public abstract record CommandApplyStatus
{
    private CommandApplyStatus()
    {
    }

    /// <summary>The projection changed or recorded new audit identity.</summary>
    public sealed record Recorded : CommandApplyStatus;

    /// <summary>The frame id was already observed.</summary>
    public sealed record Duplicate : CommandApplyStatus;

    /// <summary>The command id is not currently known.</summary>
    public sealed record UnknownCommand(string CommandId) : CommandApplyStatus;

    /// <summary>The frame did not match the command's authority generation.</summary>
    public sealed record StaleGeneration(ulong Expected, ulong Actual) : CommandApplyStatus;

    /// <summary>Two terminal receipts disagreed and the reducer failed closed.</summary>
    public sealed record TerminalConflict(
        string CommandId,
        CommandStatus Existing,
        CommandStatus Incoming) : CommandApplyStatus;
}

/// <summary>
/// Idempotent, generation-guarded reducer for command frames and causal receipts.
/// </summary>
public sealed class CommandProjection
{
    private readonly Dictionary<string, CommandProjectionEntry> _entries =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _receiptIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _conflicts = new(StringComparer.Ordinal);

    /// <summary>The highest authority generation admitted into this image.</summary>
    public ulong Generation { get; private set; }

    /// <summary>Folds any command-plane frame.</summary>
    public CommandApplyStatus Apply(CommandMessage message)
    {
        Guard.NotNull(message, nameof(message));
        return message switch
        {
            CommandMessage.Submit submit => Apply(submit.Value),
            CommandMessage.Cancel cancel => Apply(cancel.Value),
            CommandMessage.Events events => Apply(events.Value),
            CommandMessage.Projection projection => Apply(projection.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(message)),
        };
    }

    /// <summary>Admits a submit. Replayed command ids are no-ops.</summary>
    public CommandApplyStatus Apply(CommandSubmit submit)
    {
        Guard.NotNull(submit, nameof(submit));
        if (_entries.ContainsKey(submit.CommandId)) return Duplicate();

        Generation = Math.Max(Generation, submit.AuthorityGeneration);
        _entries.Add(
            submit.CommandId,
            new CommandProjectionEntry(
                submit.CommandId,
                CommandStatus.Submitted,
                Terminal: false,
                submit.AuthorityGeneration,
                Reason: null,
                TerminalReceiptId: null,
                LastEventId: null));
        return Recorded();
    }

    /// <summary>Records a cancel request without treating it as terminal proof.</summary>
    public CommandApplyStatus Apply(CommandCancel cancel)
    {
        Guard.NotNull(cancel, nameof(cancel));
        if (_cancelIds.Contains(cancel.CausationId)) return Duplicate();
        if (!_entries.TryGetValue(cancel.CommandId, out var entry))
        {
            return new CommandApplyStatus.UnknownCommand(cancel.CommandId);
        }

        if (cancel.AuthorityGeneration != entry.Generation)
        {
            _cancelIds.Add(cancel.CausationId);
            LazilyMetrics.CommandStale();
            return new CommandApplyStatus.StaleGeneration(
                entry.Generation,
                cancel.AuthorityGeneration);
        }

        if (entry.Terminal) return Duplicate();
        _cancelIds.Add(cancel.CausationId);
        return Recorded();
    }

    /// <summary>Folds an event batch in order.</summary>
    public CommandApplyStatus Apply(CommandEvents events)
    {
        Guard.NotNull(events, nameof(events));
        CommandApplyStatus status = new CommandApplyStatus.Duplicate();
        foreach (var commandEvent in events.Events)
        {
            status = Apply(commandEvent);
            if (status is CommandApplyStatus.TerminalConflict) return status;
        }

        return status;
    }

    /// <summary>
    /// Folds a reconnect image. Older images are ignored; a current or newer image is authoritative.
    /// </summary>
    public CommandApplyStatus Apply(CommandProjectionImage image)
    {
        Guard.NotNull(image, nameof(image));
        if (image.Generation < Generation)
        {
            LazilyMetrics.CommandStale();
            return new CommandApplyStatus.StaleGeneration(Generation, image.Generation);
        }

        var next = new Dictionary<string, CommandProjectionEntry>(StringComparer.Ordinal);
        foreach (var entry in image.Commands)
        {
            if (entry.Terminal != entry.Status.IsTerminal())
            {
                throw new InvalidOperationException(
                    $"Command {entry.CommandId} has inconsistent terminal/status fields.");
            }

            if (!next.TryAdd(entry.CommandId, entry))
            {
                throw new InvalidOperationException(
                    $"Projection contains duplicate command id {entry.CommandId}.");
            }
        }

        _entries.Clear();
        foreach (var (commandId, entry) in next) _entries.Add(commandId, entry);
        _eventIds.Clear();
        _receiptIds.Clear();
        _cancelIds.Clear();
        _conflicts.Clear();
        foreach (var entry in image.Commands)
        {
            if (entry.LastEventId is not null) _eventIds.Add(entry.LastEventId);
            if (entry.TerminalReceiptId is not null) _receiptIds.Add(entry.TerminalReceiptId);
        }

        Generation = image.Generation;
        return Recorded();
    }

    /// <summary>Folds one progress event. Event-only cancellation remains non-terminal.</summary>
    public CommandApplyStatus Apply(CommandEvent commandEvent)
    {
        Guard.NotNull(commandEvent, nameof(commandEvent));
        if (_eventIds.Contains(commandEvent.EventId)) return Duplicate();
        if (!_entries.TryGetValue(commandEvent.CommandId, out var entry))
        {
            return new CommandApplyStatus.UnknownCommand(commandEvent.CommandId);
        }

        _eventIds.Add(commandEvent.EventId);
        if (commandEvent.Generation != entry.Generation)
        {
            LazilyMetrics.CommandStale();
            return new CommandApplyStatus.StaleGeneration(
                entry.Generation,
                commandEvent.Generation);
        }

        var status = ProgressStatus(commandEvent.Kind);
        _entries[commandEvent.CommandId] = entry with
        {
            Status =
                !entry.Terminal
                && status is { } next
                && PhaseRank(next) >= PhaseRank(entry.Status)
                    ? next
                    : entry.Status,
            LastEventId = commandEvent.EventId,
        };
        return Recorded();
    }

    /// <summary>Folds a receipt, the sole terminal authority for a command.</summary>
    public CommandApplyStatus Observe(CausalReceipt receipt)
    {
        Guard.NotNull(receipt, nameof(receipt));
        if (_receiptIds.Contains(receipt.ReceiptId)) return Duplicate();
        if (!_entries.TryGetValue(receipt.CausationId, out var entry))
        {
            return new CommandApplyStatus.UnknownCommand(receipt.CausationId);
        }

        _receiptIds.Add(receipt.ReceiptId);
        if (receipt.Generation != entry.Generation)
        {
            LazilyMetrics.CommandStale();
            return new CommandApplyStatus.StaleGeneration(
                entry.Generation,
                receipt.Generation);
        }

        if (!receipt.Outcome.IsTerminal())
        {
            if (!entry.Terminal && PhaseRank(CommandStatus.Accepted) >= PhaseRank(entry.Status))
            {
                _entries[receipt.CausationId] = entry with
                {
                    Status = CommandStatus.Accepted,
                };
            }

            return Recorded();
        }

        var incoming = TerminalStatus(receipt);
        if (entry.Terminal)
        {
            if (entry.Status == incoming) return Recorded();
            _conflicts.Add(receipt.CausationId);
            LazilyMetrics.CommandConflict();
            return new CommandApplyStatus.TerminalConflict(
                receipt.CausationId,
                entry.Status,
                incoming);
        }

        _entries[receipt.CausationId] = entry with
        {
            Status = incoming,
            Terminal = true,
            Reason = receipt.Reason,
            TerminalReceiptId = receipt.ReceiptId,
        };
        LazilyMetrics.CommandTerminal();
        return Recorded();
    }

    /// <summary>Looks up one command image.</summary>
    public bool TryGet(string commandId, out CommandProjectionEntry entry) =>
        _entries.TryGetValue(commandId, out entry!);

    /// <summary>Returns a terminal command image when available.</summary>
    public bool TryGetTerminal(string commandId, out CommandProjectionEntry entry)
    {
        if (_entries.TryGetValue(commandId, out entry!) && entry.Terminal) return true;
        entry = null!;
        return false;
    }

    /// <summary>Returns whether a terminal conflict was observed.</summary>
    public bool HasConflict(string commandId) => _conflicts.Contains(commandId);

    /// <summary>Builds a reconnect image sorted by command id.</summary>
    public CommandProjectionImage ToImage() =>
        new(
            Generation,
            [.. _entries.Values.OrderBy(entry => entry.CommandId, StringComparer.Ordinal)]);

    private static CommandApplyStatus Recorded()
    {
        LazilyMetrics.CommandFrameRecorded();
        return new CommandApplyStatus.Recorded();
    }

    private static CommandApplyStatus Duplicate()
    {
        LazilyMetrics.CommandDuplicate();
        return new CommandApplyStatus.Duplicate();
    }

    private static CommandStatus? ProgressStatus(CommandEventKind kind) =>
        kind switch
        {
            CommandEventKind.Observed or CommandEventKind.Accepted => CommandStatus.Accepted,
            CommandEventKind.Started or CommandEventKind.Progress => CommandStatus.Running,
            CommandEventKind.Cancelled
                or CommandEventKind.Superseded
                or CommandEventKind.TimedOut => null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static int PhaseRank(CommandStatus status) =>
        status switch
        {
            CommandStatus.Submitted => 0,
            CommandStatus.Accepted => 1,
            CommandStatus.Running => 2,
            _ => 3,
        };

    private static CommandStatus TerminalStatus(CausalReceipt receipt) =>
        receipt.Outcome switch
        {
            ReceiptOutcome.Applied => CommandStatus.Applied,
            ReceiptOutcome.Rejected when receipt.Reason == "cancelled" => CommandStatus.Cancelled,
            ReceiptOutcome.Rejected when receipt.Reason == "superseded" =>
                CommandStatus.Superseded,
            ReceiptOutcome.Rejected when receipt.Reason == "timed_out" => CommandStatus.TimedOut,
            ReceiptOutcome.Rejected => CommandStatus.Rejected,
            _ => throw new ArgumentOutOfRangeException(nameof(receipt)),
        };
}

/// <summary>Exact JSON codec for the externally tagged command-plane family.</summary>
public static class CommandWire
{
    /// <summary>Serializes one command frame in canonical field order.</summary>
    public static string Serialize(CommandMessage message)
    {
        Guard.NotNull(message, nameof(message));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            switch (message)
            {
                case CommandMessage.Submit submit:
                    writer.WritePropertyName("CommandSubmit");
                    WriteSubmit(writer, submit.Value);
                    break;
                case CommandMessage.Cancel cancel:
                    writer.WritePropertyName("CommandCancel");
                    WriteCancel(writer, cancel.Value);
                    break;
                case CommandMessage.Events events:
                    writer.WritePropertyName("CommandEvents");
                    WriteEvents(writer, events.Value);
                    break;
                case CommandMessage.Projection projection:
                    writer.WritePropertyName("CommandProjection");
                    WriteProjection(writer, projection.Value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(message));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Deserializes and structurally validates one command frame.</summary>
    public static CommandMessage Deserialize(string json)
    {
        Guard.NotNullOrWhiteSpace(json, nameof(json));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireObject(root, "command envelope");
        var property = SingleProperty(root, "command envelope");
        return property.Name switch
        {
            "CommandSubmit" => new CommandMessage.Submit(ReadSubmit(property.Value)),
            "CommandCancel" => new CommandMessage.Cancel(ReadCancel(property.Value)),
            "CommandEvents" => new CommandMessage.Events(ReadEvents(property.Value)),
            "CommandProjection" => new CommandMessage.Projection(
                ReadProjection(property.Value)),
            _ => throw new JsonException($"Unknown command variant '{property.Name}'."),
        };
    }

    private static CommandSubmit ReadSubmit(JsonElement body)
    {
        RequireExactProperties(
            body,
            "CommandSubmit",
            "command_id",
            "causation_id",
            "source",
            "target",
            "namespace",
            "name",
            "authority_generation",
            "idempotency_key",
            "deadline_ms",
            "policy",
            "payload_type",
            "payload_hash",
            "payload",
            "required_features");
        var policy = Required(body, "policy");
        RequireExactProperties(
            policy,
            "CommandPolicy",
            "dedupe",
            "supersede",
            "cancel_on_preempt");
        return new CommandSubmit(
            RequireString(body, "command_id"),
            RequireString(body, "causation_id"),
            RequireString(body, "source"),
            RequireString(body, "target"),
            RequireString(body, "namespace"),
            RequireString(body, "name"),
            RequireUInt64(body, "authority_generation"),
            RequireString(body, "idempotency_key"),
            RequireUInt64(body, "deadline_ms"),
            new CommandPolicy(
                ParseDedupe(RequireString(policy, "dedupe")),
                RequireBoolean(policy, "supersede"),
                RequireBoolean(policy, "cancel_on_preempt")),
            RequireString(body, "payload_type"),
            RequireString(body, "payload_hash"),
            IpcMessageJsonConverter.ReadIpcValue(Required(body, "payload")),
            ReadStringArray(body, "required_features"));
    }

    private static CommandCancel ReadCancel(JsonElement body)
    {
        RequireExactProperties(
            body,
            "CommandCancel",
            "command_id",
            "causation_id",
            "source",
            "authority_generation",
            "reason");
        return new CommandCancel(
            RequireString(body, "command_id"),
            RequireString(body, "causation_id"),
            RequireString(body, "source"),
            RequireUInt64(body, "authority_generation"),
            RequireNullableString(body, "reason"));
    }

    private static CommandEvents ReadEvents(JsonElement body)
    {
        RequireExactProperties(body, "CommandEvents", "events");
        var events = Required(body, "events");
        if (events.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("CommandEvents.events must be an array.");
        }

        return new CommandEvents(
            events.EnumerateArray()
                .Select(item =>
                {
                    RequireExactProperties(
                        item,
                        "CommandEvent",
                        "event_id",
                        "command_id",
                        "kind",
                        "generation",
                        "detail");
                    return new CommandEvent(
                        RequireString(item, "event_id"),
                        RequireString(item, "command_id"),
                        ParseEventKind(RequireString(item, "kind")),
                        RequireUInt64(item, "generation"),
                        RequireNullableString(item, "detail"));
                })
                .ToArray());
    }

    private static CommandProjectionImage ReadProjection(JsonElement body)
    {
        RequireExactProperties(body, "CommandProjection", "generation", "commands");
        var commands = Required(body, "commands");
        if (commands.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("CommandProjection.commands must be an array.");
        }

        return new CommandProjectionImage(
            RequireUInt64(body, "generation"),
            commands.EnumerateArray()
                .Select(item =>
                {
                    RequireExactProperties(
                        item,
                        "CommandProjectionEntry",
                        "command_id",
                        "status",
                        "terminal",
                        "generation",
                        "reason",
                        "terminal_receipt_id",
                        "last_event_id");
                    return new CommandProjectionEntry(
                        RequireString(item, "command_id"),
                        ParseStatus(RequireString(item, "status")),
                        RequireBoolean(item, "terminal"),
                        RequireUInt64(item, "generation"),
                        RequireNullableString(item, "reason"),
                        RequireNullableString(item, "terminal_receipt_id"),
                        RequireNullableString(item, "last_event_id"));
                })
                .ToArray());
    }

    private static void WriteSubmit(Utf8JsonWriter writer, CommandSubmit submit)
    {
        writer.WriteStartObject();
        writer.WriteString("command_id", submit.CommandId);
        writer.WriteString("causation_id", submit.CausationId);
        writer.WriteString("source", submit.Source);
        writer.WriteString("target", submit.Target);
        writer.WriteString("namespace", submit.Namespace);
        writer.WriteString("name", submit.Name);
        writer.WriteNumber("authority_generation", submit.AuthorityGeneration);
        writer.WriteString("idempotency_key", submit.IdempotencyKey);
        writer.WriteNumber("deadline_ms", submit.DeadlineMs);
        writer.WritePropertyName("policy");
        writer.WriteStartObject();
        writer.WriteString("dedupe", FormatDedupe(submit.Policy.Dedupe));
        writer.WriteBoolean("supersede", submit.Policy.Supersede);
        writer.WriteBoolean("cancel_on_preempt", submit.Policy.CancelOnPreempt);
        writer.WriteEndObject();
        writer.WriteString("payload_type", submit.PayloadType);
        writer.WriteString("payload_hash", submit.PayloadHash);
        writer.WritePropertyName("payload");
        IpcMessageJsonConverter.WriteIpcValue(writer, submit.Payload);
        writer.WritePropertyName("required_features");
        writer.WriteStartArray();
        foreach (var feature in submit.RequiredFeatures) writer.WriteStringValue(feature);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteCancel(Utf8JsonWriter writer, CommandCancel cancel)
    {
        writer.WriteStartObject();
        writer.WriteString("command_id", cancel.CommandId);
        writer.WriteString("causation_id", cancel.CausationId);
        writer.WriteString("source", cancel.Source);
        writer.WriteNumber("authority_generation", cancel.AuthorityGeneration);
        WriteNullableString(writer, "reason", cancel.Reason);
        writer.WriteEndObject();
    }

    private static void WriteEvents(Utf8JsonWriter writer, CommandEvents events)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("events");
        writer.WriteStartArray();
        foreach (var commandEvent in events.Events)
        {
            writer.WriteStartObject();
            writer.WriteString("event_id", commandEvent.EventId);
            writer.WriteString("command_id", commandEvent.CommandId);
            writer.WriteString("kind", FormatEventKind(commandEvent.Kind));
            writer.WriteNumber("generation", commandEvent.Generation);
            WriteNullableString(writer, "detail", commandEvent.Detail);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteProjection(
        Utf8JsonWriter writer,
        CommandProjectionImage projection)
    {
        writer.WriteStartObject();
        writer.WriteNumber("generation", projection.Generation);
        writer.WritePropertyName("commands");
        writer.WriteStartArray();
        foreach (var entry in projection.Commands)
        {
            writer.WriteStartObject();
            writer.WriteString("command_id", entry.CommandId);
            writer.WriteString("status", FormatStatus(entry.Status));
            writer.WriteBoolean("terminal", entry.Terminal);
            writer.WriteNumber("generation", entry.Generation);
            WriteNullableString(writer, "reason", entry.Reason);
            WriteNullableString(writer, "terminal_receipt_id", entry.TerminalReceiptId);
            WriteNullableString(writer, "last_event_id", entry.LastEventId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        var value = Required(element, name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{name} must be an array.");
        }

        return value.EnumerateArray()
            .Select((item, index) =>
                item.ValueKind == JsonValueKind.String
                    ? item.GetString()!
                    : throw new JsonException($"{name}[{index}] must be a string."))
            .ToArray();
    }

    private static DedupePolicy ParseDedupe(string value) =>
        value switch
        {
            "none" => DedupePolicy.None,
            "same_idempotency_key" => DedupePolicy.SameIdempotencyKey,
            "same_command_id" => DedupePolicy.SameCommandId,
            _ => throw new JsonException($"Unknown dedupe policy '{value}'."),
        };

    private static string FormatDedupe(DedupePolicy value) =>
        value switch
        {
            DedupePolicy.None => "none",
            DedupePolicy.SameIdempotencyKey => "same_idempotency_key",
            DedupePolicy.SameCommandId => "same_command_id",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static CommandEventKind ParseEventKind(string value) =>
        value switch
        {
            "observed" => CommandEventKind.Observed,
            "accepted" => CommandEventKind.Accepted,
            "started" => CommandEventKind.Started,
            "progress" => CommandEventKind.Progress,
            "cancelled" => CommandEventKind.Cancelled,
            "superseded" => CommandEventKind.Superseded,
            "timed_out" => CommandEventKind.TimedOut,
            _ => throw new JsonException($"Unknown command event kind '{value}'."),
        };

    private static string FormatEventKind(CommandEventKind value) =>
        value switch
        {
            CommandEventKind.Observed => "observed",
            CommandEventKind.Accepted => "accepted",
            CommandEventKind.Started => "started",
            CommandEventKind.Progress => "progress",
            CommandEventKind.Cancelled => "cancelled",
            CommandEventKind.Superseded => "superseded",
            CommandEventKind.TimedOut => "timed_out",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static CommandStatus ParseStatus(string value) =>
        value switch
        {
            "submitted" => CommandStatus.Submitted,
            "accepted" => CommandStatus.Accepted,
            "running" => CommandStatus.Running,
            "applied" => CommandStatus.Applied,
            "rejected" => CommandStatus.Rejected,
            "cancelled" => CommandStatus.Cancelled,
            "superseded" => CommandStatus.Superseded,
            "timed_out" => CommandStatus.TimedOut,
            _ => throw new JsonException($"Unknown command status '{value}'."),
        };

    private static string FormatStatus(CommandStatus value) =>
        value switch
        {
            CommandStatus.Submitted => "submitted",
            CommandStatus.Accepted => "accepted",
            CommandStatus.Running => "running",
            CommandStatus.Applied => "applied",
            CommandStatus.Rejected => "rejected",
            CommandStatus.Cancelled => "cancelled",
            CommandStatus.Superseded => "superseded",
            CommandStatus.TimedOut => "timed_out",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw new JsonException($"Missing required property '{name}'.");

    private static string RequireString(JsonElement element, string name)
    {
        var value = Required(element, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
        {
            throw new JsonException($"{name} must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static string? RequireNullableString(JsonElement element, string name)
    {
        var value = Required(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new JsonException($"{name} must be a string or null."),
        };
    }

    private static ulong RequireUInt64(JsonElement element, string name)
    {
        var value = Required(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var result))
        {
            throw new JsonException($"{name} must be a non-negative integer.");
        }

        return result;
    }

    private static bool RequireBoolean(JsonElement element, string name)
    {
        var value = Required(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException($"{name} must be a boolean."),
        };
    }

    private static JsonProperty SingleProperty(JsonElement element, string context)
    {
        var properties = element.EnumerateObject().ToArray();
        return properties.Length == 1
            ? properties[0]
            : throw new JsonException($"{context} must contain exactly one property.");
    }

    private static void RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{context} must be an object.");
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        string context,
        params string[] names)
    {
        RequireObject(element, context);
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length
            || names.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
        {
            throw new JsonException(
                $"{context} must contain exactly: {string.Join(", ", names)}.");
        }
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }
}

/// <summary>Outbound transport for command frames.</summary>
public interface ICommandTransport
{
    /// <summary>Sends one command frame.</summary>
    void Send(CommandMessage message);
}

/// <summary>Unary RPC completion states.</summary>
public enum CommandCallStateKind
{
    /// <summary>No terminal receipt has folded in.</summary>
    Pending,

    /// <summary>A terminal receipt completed the command.</summary>
    Resolved,

    /// <summary>Terminal receipts conflicted.</summary>
    Conflict,
}

/// <summary>The current unary call state.</summary>
public sealed record CommandCallState(
    CommandCallStateKind Kind,
    CommandProjectionEntry? Entry = null);

/// <summary>A unary call failed closed on conflicting terminal receipts.</summary>
public sealed class CommandTerminalConflictException(string commandId)
    : InvalidOperationException($"Command '{commandId}' has conflicting terminal receipts.")
{
    /// <summary>The conflicted command id.</summary>
    public string CommandId { get; } = commandId;
}

/// <summary>
/// RPC facade over command frames. Construction requires a negotiated command-plane-v1 session.
/// </summary>
public sealed class CommandRpcClient
{
    private readonly ICommandTransport _transport;
    private readonly Dictionary<string, List<TaskCompletionSource<CommandProjectionEntry>>> _waiters =
        new(StringComparer.Ordinal);

    /// <summary>Creates a gated RPC facade.</summary>
    public CommandRpcClient(ICommandTransport transport, NegotiatedSession session)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Guard.NotNull(session, nameof(session));
        session.RequireCommandPlane();
    }

    /// <summary>The client's folded command projection.</summary>
    public CommandProjection Projection { get; } = new();

    /// <summary>Sends a submit and immediately returns its command id.</summary>
    public string Submit(CommandSubmit submit)
    {
        var message = new CommandMessage.Submit(submit);
        _transport.Send(message);
        Projection.Apply(message);
        return submit.CommandId;
    }

    /// <summary>Sends a cancellation request and returns the resulting projection status.</summary>
    public CommandApplyStatus Cancel(CommandCancel cancel)
    {
        var message = new CommandMessage.Cancel(cancel);
        _transport.Send(message);
        return Projection.Apply(message);
    }

    /// <summary>Folds an inbound command frame.</summary>
    public CommandApplyStatus Ingest(CommandMessage message)
    {
        var result = Projection.Apply(message);
        CompleteWaiters(message switch
        {
            CommandMessage.Submit submit => submit.Value.CommandId,
            CommandMessage.Cancel cancel => cancel.Value.CommandId,
            _ => null,
        });
        return result;
    }

    /// <summary>Folds an inbound receipt.</summary>
    public CommandApplyStatus Ingest(CausalReceipt receipt)
    {
        var result = Projection.Observe(receipt);
        CompleteWaiters(receipt.CausationId);
        return result;
    }

    /// <summary>Returns the current unary call state without waiting.</summary>
    public CommandCallState Poll(string commandId)
    {
        if (Projection.HasConflict(commandId))
        {
            return new CommandCallState(CommandCallStateKind.Conflict);
        }

        return Projection.TryGetTerminal(commandId, out var entry)
            ? new CommandCallState(CommandCallStateKind.Resolved, entry)
            : new CommandCallState(CommandCallStateKind.Pending);
    }

    /// <summary>
    /// Sends a submit and completes only after a terminal causal receipt folds in.
    /// </summary>
    public Task<CommandProjectionEntry> CallAsync(
        CommandSubmit submit,
        CancellationToken cancellationToken = default)
    {
        Submit(submit);
        if (Projection.TryGetTerminal(submit.CommandId, out var terminal))
        {
            return Task.FromResult(terminal);
        }

        var completion = new TaskCompletionSource<CommandProjectionEntry>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_waiters.TryGetValue(submit.CommandId, out var waiters))
        {
            waiters = [];
            _waiters.Add(submit.CommandId, waiters);
        }

        waiters.Add(completion);
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(
                static state =>
                {
                    var tuple =
                        (Tuple<TaskCompletionSource<CommandProjectionEntry>, CancellationToken>)state!;
                    tuple.Item1.TrySetCanceled(tuple.Item2);
                },
                Tuple.Create(completion, cancellationToken));
        }

        return completion.Task;
    }

    private void CompleteWaiters(string? commandId)
    {
        if (commandId is null || !_waiters.Remove(commandId, out var waiters)) return;
        if (Projection.HasConflict(commandId))
        {
            foreach (var waiter in waiters)
            {
                waiter.TrySetException(new CommandTerminalConflictException(commandId));
            }

            return;
        }

        if (!Projection.TryGetTerminal(commandId, out var terminal))
        {
            _waiters.Add(commandId, waiters);
            return;
        }

        foreach (var waiter in waiters) waiter.TrySetResult(terminal);
    }
}
