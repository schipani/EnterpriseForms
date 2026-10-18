using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EnterpriseForms.Integrity.Canonical;

/// <summary>Trusted identifiers bound into every canonical row (supplied by the authority, never by the caller DTO).</summary>
public sealed record RowSigningContext(uint KeyVersion, Guid OrganizationId, Guid PrimaryKeyId, ulong RowRevision);

/// <summary>
/// Canonical row byte encoding <c>DynamicForms/RowHmac/v1</c>. Normative text: docs/dynamic-forms/row-integrity-format.md.
/// Conformance vectors: trust_authority/row-integrity-v1.test-vectors.json.
/// </summary>
public static class RowCanonicalizerV1
{
    public const string Domain = "DynamicForms/RowHmac/v1";
    public const byte FramingMarker = 0x10;
    public const byte NullTag = 0x00;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly DateOnly EpochDate = new(1970, 1, 1);
    private const long MaxSafeJsonInteger = (1L << 53) - 1;

    public static byte[] Encode(RowSchemaDefinition schema, RowSigningContext context, IReadOnlyDictionary<string, RowValue?> values)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(values);

        if (context.KeyVersion == 0)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.KeyVersionRange, "key_version must be >= 1");
        }

        if (context.RowRevision == 0)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.RowRevisionRange, "row_revision must be >= 1");
        }

        foreach (var supplied in values.Keys)
        {
            if (!schema.Columns.Any(c => string.Equals(c.ColumnName, supplied, StringComparison.Ordinal)))
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.UnregisteredColumn, supplied);
            }
        }

        using var stream = new MemoryStream(512);
        stream.WriteByte(FramingMarker);
        WriteAsciiLen16(stream, Domain);
        WriteUInt16(stream, (ushort)schema.SchemaVersion);
        WriteUInt32(stream, context.KeyVersion);
        WriteAsciiLen16(stream, schema.EntityKind);
        WriteUuid(stream, context.OrganizationId);
        WriteUuid(stream, context.PrimaryKeyId);
        WriteUInt64(stream, context.RowRevision);
        WriteUInt32(stream, (uint)schema.Columns.Count);

        foreach (var column in schema.Columns)
        {
            if (!values.TryGetValue(column.ColumnName, out var value))
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.MissingColumn, column.ColumnName);
            }

            WriteAsciiLen16(stream, column.ColumnName);
            WriteValue(stream, column, value);
        }

        return stream.ToArray();
    }

    public static byte[] CanonicalPayloadDigest(ReadOnlySpan<byte> canonicalBytes) => SHA256.HashData(canonicalBytes);

    private static void WriteValue(Stream stream, RowSignedColumn column, RowValue? value)
    {
        if (value is null)
        {
            if (!column.Nullable)
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.NullNotPermitted, column.ColumnName);
            }

            stream.WriteByte(NullTag);
            return;
        }

        if (value.Type != column.Type)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.TypeMismatch, $"{column.ColumnName}: expected {column.Type}, got {value.Type}");
        }

        stream.WriteByte((byte)column.Type);
        switch (value)
        {
            case RowValue.BooleanValue b:
                stream.WriteByte(b.Value ? (byte)1 : (byte)0);
                break;
            case RowValue.Int32Value i:
                WriteInt32(stream, i.Value);
                break;
            case RowValue.Int64Value l:
                WriteInt64(stream, l.Value);
                break;
            case RowValue.DecimalValue d:
                WriteDecimal(stream, d);
                break;
            case RowValue.DateValue date:
                WriteInt32(stream, date.Value.DayNumber - EpochDate.DayNumber);
                break;
            case RowValue.TimestampValue ts:
                WriteTimestamp(stream, ts.Value);
                break;
            case RowValue.UuidValue u:
                WriteUuid(stream, u.Value);
                break;
            case RowValue.TextValue t:
                WriteText(stream, t.Value);
                break;
            case RowValue.BytesValue bytes:
                WriteUInt32(stream, (uint)bytes.Value.Length);
                stream.Write(bytes.Value.Span);
                break;
            case RowValue.JsonValue json:
                WriteJson(stream, json.Value);
                break;
            default:
                throw new IntegrityFormatException(IntegrityFormatErrorCode.TypeMismatch, value.GetType().Name);
        }
    }

    private static void WriteDecimal(Stream stream, RowValue.DecimalValue value)
    {
        if (value.Scale < 0)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.DecimalRange, "scale must be non-negative after normalization");
        }

        var coefficient = value.Coefficient.ToByteArray(isUnsigned: false, isBigEndian: true);
        if (coefficient.Length > ushort.MaxValue)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.DecimalRange, "coefficient too large");
        }

        WriteInt32(stream, value.Scale);
        WriteUInt16(stream, (ushort)coefficient.Length);
        stream.Write(coefficient);
    }

    private static void WriteTimestamp(Stream stream, DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        if (utcTicks % 10 != 0)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.TimestampPrecision, "timestamps are signed at microsecond precision; sub-microsecond ticks are rejected");
        }

        WriteInt64(stream, utcTicks / 10);
    }

    private static void WriteText(Stream stream, string value)
    {
        byte[] encoded;
        try
        {
            encoded = StrictUtf8.GetBytes(value.Normalize(NormalizationForm.FormC));
        }
        catch (EncoderFallbackException ex)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.TextInvalidUnicode, ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.TextInvalidUnicode, ex.Message);
        }

        WriteUInt32(stream, (uint)encoded.Length);
        stream.Write(encoded);
    }

    private static void WriteJson(Stream stream, JsonElement element)
    {
        var builder = new StringBuilder();
        AppendCanonicalJson(builder, element);
        var encoded = StrictUtf8.GetBytes(builder.ToString());
        WriteUInt32(stream, (uint)encoded.Length);
        stream.Write(encoded);
    }

    /// <summary>RFC 8785 subset: ASCII keys sorted ordinally, integers within ±(2^53−1), no floats.</summary>
    private static void AppendCanonicalJson(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                if (raw.IndexOfAny(['.', 'e', 'E']) >= 0 || !element.TryGetInt64(out var integer))
                {
                    throw new IntegrityFormatException(IntegrityFormatErrorCode.JsonFloatNotPermitted, raw);
                }

                if (Math.Abs(integer) > MaxSafeJsonInteger)
                {
                    throw new IntegrityFormatException(IntegrityFormatErrorCode.JsonIntegerRange, raw);
                }

                builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.String:
                AppendJsonString(builder, element.GetString()!);
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    AppendCanonicalJson(builder, item);
                }

                builder.Append(']');
                break;
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                foreach (var property in properties)
                {
                    if (!Ascii.IsValid(property.Name))
                    {
                        throw new IntegrityFormatException(IntegrityFormatErrorCode.JsonKeyNotAscii, property.Name);
                    }
                }

                properties.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
                builder.Append('{');
                for (var i = 0; i < properties.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    AppendJsonString(builder, properties[i].Name);
                    builder.Append(':');
                    AppendCanonicalJson(builder, properties[i].Value);
                }

                builder.Append('}');
                break;
            default:
                throw new IntegrityFormatException(IntegrityFormatErrorCode.JsonUnsupportedNode, element.ValueKind.ToString());
        }
    }

    private static void AppendJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case < ' ':
                    builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        builder.Append('"');
    }

    private static void WriteAsciiLen16(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.IdentifierTooLong, value);
        }

        WriteUInt16(stream, (ushort)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteUuid(Stream stream, Guid value)
    {
        Span<byte> buffer = stackalloc byte[16];
        value.TryWriteBytes(buffer, bigEndian: true, out _);
        stream.Write(buffer);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
