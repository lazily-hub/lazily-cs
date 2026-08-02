using System.Text;
using System.Text.Json;

namespace Lazily;

/// <summary>
/// Exact <c>msgpack</c> codec for the externally tagged IPC family — the
/// CROSS-LANGUAGE BINARY DEFAULT of protocol.md § Frame codecs
/// (<c>#lzmsgpackparity</c>, <c>#lzmsgpackseven</c>).
/// </summary>
/// <remarks>
/// <para>
/// Shipping <em>a</em> MessagePack codec is not implementing <c>msgpack</c>. The codec
/// token names ONE wire: the externally tagged frame (<c>{"Snapshot": …}</c> — the variant
/// NAME, never an integer discriminator and never an internally tagged
/// <c>{"type": 0, "value": …}</c>) over named-field maps whose keys are the <c>json</c>
/// field names, with the same omit-when-absent rule for optional fields. A codec that
/// packs the same data positionally, or behind integer <c>kind</c> discriminators, is a
/// private framing that happens to use MessagePack — a peer that negotiated <c>msgpack</c>
/// with it could not decode its frames.
/// </para>
/// <para>
/// This codec is built ON <see cref="IpcWire"/> rather than beside it, and that is the
/// point: the two codecs differ only in how a value tree is serialized, never in the SHAPE
/// of that tree. Deriving the msgpack frame from the json codec's tree makes the external
/// tags, the field names, and both NodeKey rules (<c>NodeSnapshot</c>/<c>NodeAdd</c> omit an
/// absent <c>key</c>; <c>CrdtOp</c> ALWAYS writes it, <c>null</c> when unset) identical by
/// construction. A second hand-written transcription of the same shape is exactly the drift
/// that produced a divergent private framing in another binding.
/// </para>
/// <para>
/// Byte payloads are ARRAYS OF INTEGERS, not MessagePack <c>bin</c>. That is what the
/// reference encoder produces (<c>rmp_serde</c> serializes <c>Vec&lt;u8&gt;</c> through
/// serde's default seq impl) and what its decoder accepts, so emitting or accepting
/// <c>bin</c> would put this binding outside the wire it claims to speak. Note that
/// MessagePack-CSharp encodes <c>byte[]</c> as <c>bin</c> by default — that is NOT this
/// wire, which is one reason this codec is hand-rolled rather than delegated to a package.
/// </para>
/// <para>
/// NOT byte-canonical (§ Frame codecs): a MessagePack map's key order is encoder-defined,
/// so conformance is <c>decode(encode(m)) == m</c> plus a decode of a peer's frame, never a
/// golden byte string. This encoder happens to be deterministic — allowed, but not a
/// property any peer may rely on.
/// </para>
/// <para>
/// Failure mode matches <see cref="IpcWire"/>: <see cref="JsonException"/>, so a caller can
/// handle both MUST-level codecs through one catch.
/// </para>
/// </remarks>
public static class MsgPackWire
{
    /// <summary>The deepest container nesting a frame may carry before it is rejected.</summary>
    /// <remarks>
    /// The IPC family nests six levels at most. A hostile frame can otherwise drive the
    /// recursive reader into a stack overflow, which is not a catchable failure.
    /// </remarks>
    private const int MaxDepth = 64;

    /// <summary>Encodes one message as a <c>msgpack</c> frame.</summary>
    /// <param name="message">The message to encode.</param>
    /// <returns>The externally tagged, named-field MessagePack frame.</returns>
    public static byte[] Serialize(IpcMessage message)
    {
        Guard.NotNull(message, nameof(message));
        using var document = JsonDocument.Parse(IpcWire.Serialize(message));
        var output = new List<byte>(256);
        Pack(output, document.RootElement, 0);
        return [.. output];
    }

    /// <summary>Decodes one <c>msgpack</c> frame.</summary>
    /// <param name="frame">The frame bytes.</param>
    /// <returns>The decoded message.</returns>
    public static IpcMessage Deserialize(byte[] frame) => IpcWire.Deserialize(ToJsonText(frame));

    /// <summary>
    /// Schema-less view of a frame's bytes, as the equivalent JSON value tree.
    /// </summary>
    /// <param name="frame">The frame bytes.</param>
    /// <returns>The generic map/list structure the frame encodes; the caller owns it.</returns>
    /// <remarks>
    /// The named-field rule is a property of the ENCODING, so it is invisible to any
    /// assertion over a decoded <see cref="IpcMessage"/>: a positional encoder round-trips
    /// every value correctly and is still non-conforming. Conformance runners introspect
    /// through this, which is the only way to see the rule at all.
    /// </remarks>
    public static JsonDocument Inspect(byte[] frame) => JsonDocument.Parse(ToJsonText(frame));

    private static string ToJsonText(byte[] frame)
    {
        Guard.NotNull(frame, nameof(frame));
        var reader = new MsgPackReader(frame);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            reader.Transcribe(writer, 0);
        }

        reader.RequireEnd();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // -- json value tree -> MessagePack -------------------------------------------

    private static void Pack(List<byte> output, JsonElement value, int depth)
    {
        if (depth > MaxDepth) throw new JsonException("msgpack frame nests too deeply.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var members = 0;
                foreach (var _ in value.EnumerateObject()) members += 1;
                WriteHeader(output, members, 0x80, 0xde, 0xdf);
                foreach (var member in value.EnumerateObject())
                {
                    PackString(output, member.Name);
                    Pack(output, member.Value, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                var items = 0;
                foreach (var _ in value.EnumerateArray()) items += 1;
                WriteHeader(output, items, 0x90, 0xdc, 0xdd);
                foreach (var item in value.EnumerateArray()) Pack(output, item, depth + 1);
                break;
            case JsonValueKind.String:
                PackString(output, value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                PackNumber(output, value);
                break;
            case JsonValueKind.True:
                output.Add(0xc3);
                break;
            case JsonValueKind.False:
                output.Add(0xc2);
                break;
            case JsonValueKind.Null:
                output.Add(0xc0);
                break;
            default:
                throw new JsonException($"msgpack cannot encode a {value.ValueKind} value.");
        }
    }

    private static void PackNumber(List<byte> output, JsonElement value)
    {
        if (value.TryGetUInt64(out var unsigned)) { PackUInt64(output, unsigned); return; }
        if (value.TryGetInt64(out var signed)) { PackInt64(output, signed); return; }

        // No IpcMessage field is floating point (§ IpcMessage: every field is an integer, a
        // string, or a byte sequence). Refusing here keeps a future double-valued field from
        // silently acquiring a wire form nothing agreed on, rather than encoding one this
        // codec's reader deliberately rejects.
        throw new JsonException(
            "msgpack frames carry no floating-point field; every IpcMessage field is an "
            + "integer, a string, or a byte sequence.");
    }

    private static void PackUInt64(List<byte> output, ulong value)
    {
        if (value <= 0x7f) { output.Add((byte)value); return; }
        if (value <= byte.MaxValue) { output.Add(0xcc); output.Add((byte)value); return; }
        if (value <= ushort.MaxValue) { output.Add(0xcd); WriteBigEndian(output, value, 2); return; }
        if (value <= uint.MaxValue) { output.Add(0xce); WriteBigEndian(output, value, 4); return; }
        output.Add(0xcf);
        WriteBigEndian(output, value, 8);
    }

    private static void PackInt64(List<byte> output, long value)
    {
        if (value >= 0) { PackUInt64(output, (ulong)value); return; }
        if (value >= -32) { output.Add((byte)(0xe0 | (value + 32))); return; }
        if (value >= sbyte.MinValue) { output.Add(0xd0); output.Add((byte)(sbyte)value); return; }
        if (value >= short.MinValue) { output.Add(0xd1); WriteBigEndian(output, (ulong)value, 2); return; }
        if (value >= int.MinValue) { output.Add(0xd2); WriteBigEndian(output, (ulong)value, 4); return; }
        output.Add(0xd3);
        WriteBigEndian(output, (ulong)value, 8);
    }

    private static void PackString(List<byte> output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length < 32) output.Add((byte)(0xa0 | bytes.Length));
        else if (bytes.Length <= byte.MaxValue) { output.Add(0xd9); output.Add((byte)bytes.Length); }
        else if (bytes.Length <= ushort.MaxValue) { output.Add(0xda); WriteBigEndian(output, (ulong)bytes.Length, 2); }
        else { output.Add(0xdb); WriteBigEndian(output, (ulong)bytes.Length, 4); }
        output.AddRange(bytes);
    }

    private static void WriteHeader(List<byte> output, int count, byte fixMask, byte code16, byte code32)
    {
        if (count < 16) { output.Add((byte)(fixMask | count)); return; }
        if (count <= ushort.MaxValue) { output.Add(code16); WriteBigEndian(output, (ulong)count, 2); return; }
        output.Add(code32);
        WriteBigEndian(output, (ulong)count, 4);
    }

    private static void WriteBigEndian(List<byte> output, ulong value, int width)
    {
        for (var shift = (width - 1) * 8; shift >= 0; shift -= 8)
        {
            output.Add((byte)(value >> shift));
        }
    }

    // -- MessagePack -> json value tree -------------------------------------------

    private sealed class MsgPackReader
    {
        private readonly byte[] _data;
        private int _position;

        internal MsgPackReader(byte[] data) => _data = data;

        internal void RequireEnd()
        {
            if (_position != _data.Length)
            {
                throw new JsonException("msgpack frame has trailing content.");
            }
        }

        internal void Transcribe(Utf8JsonWriter writer, int depth)
        {
            if (depth > MaxDepth) throw new JsonException("msgpack frame nests too deeply.");
            var tag = Next();
            if (tag <= 0x7f) { writer.WriteNumberValue((ulong)tag); return; }
            if (tag >= 0xe0) { writer.WriteNumberValue((long)(sbyte)tag); return; }
            if ((tag & 0xf0) == 0x80) { TranscribeMap(writer, tag & 0x0f, depth); return; }
            if ((tag & 0xf0) == 0x90) { TranscribeArray(writer, tag & 0x0f, depth); return; }
            if ((tag & 0xe0) == 0xa0) { writer.WriteStringValue(ReadUtf8(tag & 0x1f)); return; }

            switch (tag)
            {
                case 0xc0: writer.WriteNullValue(); return;
                case 0xc2: writer.WriteBooleanValue(false); return;
                case 0xc3: writer.WriteBooleanValue(true); return;
                case 0xcc: writer.WriteNumberValue((ulong)Next()); return;
                case 0xcd: writer.WriteNumberValue(ReadBigEndian(2)); return;
                case 0xce: writer.WriteNumberValue(ReadBigEndian(4)); return;
                case 0xcf: writer.WriteNumberValue(ReadBigEndian(8)); return;
                case 0xd0: writer.WriteNumberValue((long)(sbyte)Next()); return;
                case 0xd1: writer.WriteNumberValue(unchecked((short)ReadBigEndian(2))); return;
                case 0xd2: writer.WriteNumberValue(unchecked((int)ReadBigEndian(4))); return;
                case 0xd3: writer.WriteNumberValue(unchecked((long)ReadBigEndian(8))); return;
                case 0xd9: writer.WriteStringValue(ReadUtf8(Next())); return;
                case 0xda: writer.WriteStringValue(ReadUtf8(ReadLength(2))); return;
                case 0xdb: writer.WriteStringValue(ReadUtf8(ReadLength(4))); return;
                case 0xdc: TranscribeArray(writer, ReadLength(2), depth); return;
                case 0xdd: TranscribeArray(writer, ReadLength(4), depth); return;
                case 0xde: TranscribeMap(writer, ReadLength(2), depth); return;
                case 0xdf: TranscribeMap(writer, ReadLength(4), depth); return;

                // A byte payload arrives as an ARRAY OF INTEGERS on this wire. The reference
                // decoder rejects `bin` in the same position, so accepting it here would make
                // this binding read frames no conforming peer produces and write none it can
                // read — a private extension wearing the `msgpack` token.
                case 0xc4:
                case 0xc5:
                case 0xc6:
                    throw new JsonException(
                        "msgpack `bin` is not this wire: byte payloads are arrays of integers.");

                case 0xca:
                case 0xcb:
                    throw new JsonException(
                        "msgpack frames carry no floating-point field; every IpcMessage field "
                        + "is an integer, a string, or a byte sequence.");

                default:
                    throw new JsonException(
                        $"msgpack tag 0x{tag:x2} is not part of the lazily wire.");
            }
        }

        private void TranscribeArray(Utf8JsonWriter writer, int count, int depth)
        {
            writer.WriteStartArray();
            for (var i = 0; i < count; i++) Transcribe(writer, depth + 1);
            writer.WriteEndArray();
        }

        private void TranscribeMap(Utf8JsonWriter writer, int count, int depth)
        {
            writer.WriteStartObject();
            for (var i = 0; i < count; i++)
            {
                writer.WritePropertyName(ReadMapKey());
                Transcribe(writer, depth + 1);
            }
            writer.WriteEndObject();
        }

        private string ReadMapKey()
        {
            var tag = Next();
            if ((tag & 0xe0) == 0xa0) return ReadUtf8(tag & 0x1f);
            return tag switch
            {
                0xd9 => ReadUtf8(Next()),
                0xda => ReadUtf8(ReadLength(2)),
                0xdb => ReadUtf8(ReadLength(4)),

                // Every struct on this wire is a map keyed by the JSON FIELD NAME. An integer
                // key is the positional/discriminator framing protocol.md excludes outright.
                _ => throw new JsonException(
                    "msgpack map keys on this wire are the json field names, as strings."),
            };
        }

        private byte Next()
        {
            if (_position >= _data.Length) throw new JsonException("msgpack frame is truncated.");
            return _data[_position++];
        }

        private int ReadLength(int width)
        {
            var value = ReadBigEndian(width);
            if (value > int.MaxValue) throw new JsonException("msgpack frame declares an impossible length.");
            return (int)value;
        }

        private ulong ReadBigEndian(int width)
        {
            ulong value = 0;
            for (var i = 0; i < width; i++) value = (value << 8) | Next();
            return value;
        }

        private string ReadUtf8(int length)
        {
            if (length < 0 || _position + length > _data.Length)
            {
                throw new JsonException("msgpack frame is truncated.");
            }

            var text = Encoding.UTF8.GetString(_data, _position, length);
            _position += length;
            return text;
        }
    }
}
