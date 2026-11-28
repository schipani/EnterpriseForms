using System.Buffers.Binary;
using System.Text;

namespace EnterpriseForms.Integrity.Canonical;

/// <summary>One decoded column of a canonical row: its name, type tag (null for the explicit NULL marker) and the raw value bytes as framed (tag included).</summary>
public sealed record RowCanonicalColumn(string Name, RowColumnType? Type, ReadOnlyMemory<byte> FramedValue)
{
    public bool IsNull => Type is null;

    /// <summary>The UUID value, or null when the column is NULL or not a UUID.</summary>
    public Guid? AsUuid() => Type == RowColumnType.Uuid ? new Guid(FramedValue.Span.Slice(1, 16), bigEndian: true) : null;

    /// <summary>The text value, or null when the column is NULL or not text.</summary>
    public string? AsText() => Type == RowColumnType.Text ? Encoding.UTF8.GetString(FramedValue.Span[5..]) : null;

    /// <summary>The byte-string value (without its length prefix), or null when the column is NULL or not bytes.</summary>
    public ReadOnlyMemory<byte>? AsBytes() => Type == RowColumnType.Bytes ? FramedValue[5..] : null;

    public bool FramedValueEquals(RowCanonicalColumn other) => other is not null && FramedValue.Span.SequenceEqual(other.FramedValue.Span);

    /// <summary>C10 WP8: the 32-bit integer value, or null when the column is NULL or not Int32 (decoding only; the format is unchanged).</summary>
    public int? AsInt32() => Type == RowColumnType.Int32 ? BinaryPrimitives.ReadInt32BigEndian(FramedValue.Span[1..]) : null;

    /// <summary>IX-1: the 64-bit integer value, or null when the column is NULL or not Int64.</summary>
    public long? AsInt64() => Type == RowColumnType.Int64 ? BinaryPrimitives.ReadInt64BigEndian(FramedValue.Span[1..]) : null;

    /// <summary>IX-1: the boolean value, or null when the column is NULL or not Boolean. Any byte other than 0 or 1 is a format error.</summary>
    public bool? AsBoolean() => Type != RowColumnType.Boolean ? null : FramedValue.Span[1] switch
    {
        0 => false,
        1 => true,
        _ => throw new IntegrityFormatException(IntegrityFormatErrorCode.TypeMismatch, Name + ": boolean byte out of range"),
    };

    /// <summary>IX-1: the date value (days since 1970-01-01), or null when the column is NULL or not Date.</summary>
    public DateOnly? AsDate() => Type == RowColumnType.Date ? DateOnly.FromDayNumber(new DateOnly(1970, 1, 1).DayNumber + BinaryPrimitives.ReadInt32BigEndian(FramedValue.Span[1..])) : null;

    /// <summary>IX-1: the UTC instant (microseconds since the Unix epoch), or null when the column is NULL or not TimestampUtc.</summary>
    public DateTimeOffset? AsTimestamp() => Type == RowColumnType.TimestampUtc ? DateTimeOffset.UnixEpoch.AddTicks(checked(BinaryPrimitives.ReadInt64BigEndian(FramedValue.Span[1..]) * 10)) : null;

    /// <summary>
    /// IX-1: the normalized decimal (scale, big-endian two's-complement coefficient), or null when the column is NULL or not
    /// Decimal. Returned as the encoded pair so no precision is lost; <see cref="AsDecimalValue"/> converts when it fits.
    /// </summary>
    public (int Scale, System.Numerics.BigInteger Coefficient)? AsDecimalParts()
    {
        if (Type != RowColumnType.Decimal)
        {
            return null;
        }

        var span = FramedValue.Span[1..];
        var scale = BinaryPrimitives.ReadInt32BigEndian(span);
        var length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(4, 2));
        return (scale, new System.Numerics.BigInteger(span.Slice(6, length), isUnsigned: false, isBigEndian: true));
    }

    /// <summary>IX-1: the decimal value; a value outside <see cref="decimal"/> range is a format error (fail closed).</summary>
    public decimal? AsDecimalValue()
    {
        if (AsDecimalParts() is not { } parts)
        {
            return null;
        }

        if (parts.Scale is < 0 or > 28 || System.Numerics.BigInteger.Abs(parts.Coefficient) > new System.Numerics.BigInteger(decimal.MaxValue))
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.DecimalRange, Name + ": decimal out of range");
        }

        // Exact construction from the 96-bit magnitude and the scale (no division, no rounding).
        var magnitude = System.Numerics.BigInteger.Abs(parts.Coefficient);
        var mask = new System.Numerics.BigInteger(uint.MaxValue);
        return new decimal(
            unchecked((int)(uint)(magnitude & mask)),
            unchecked((int)(uint)((magnitude >> 32) & mask)),
            unchecked((int)(uint)((magnitude >> 64) & mask)),
            parts.Coefficient.Sign < 0,
            (byte)parts.Scale);
    }
}

/// <summary>A fully decoded canonical row: header plus columns in registry order.</summary>
public sealed record RowCanonicalDocument(RowCanonicalHeader Header, IReadOnlyList<RowCanonicalColumn> Columns)
{
    public RowCanonicalColumn? Column(string name) => Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// Decoder for <c>DynamicForms/RowHmac/v1</c> canonical bytes (the inverse of <see cref="RowCanonicalizerV1.Encode"/> framing).
/// Used by the authority to compare two signed rows column by column without the business column registry: the framing
/// carries every column name and type tag, so equality of named columns is decidable from the bytes alone. Any framing
/// deviation is a format error (fail closed).
/// </summary>
public static class RowCanonicalDecoder
{
    public static RowCanonicalDocument Decode(ReadOnlyMemory<byte> canonicalBytes)
    {
        var header = RowCanonicalHeader.Parse(canonicalBytes.Span);
        var span = canonicalBytes.Span;
        var offset = 1 + 2 + Encoding.ASCII.GetByteCount(RowCanonicalizerV1.Domain) + 2 + 4 + 2 + Encoding.ASCII.GetByteCount(header.EntityKind) + 16 + 16 + 8 + 4;
        var columns = new List<RowCanonicalColumn>((int)Math.Min(header.ColumnCount, 1024));
        for (var i = 0; i < header.ColumnCount; i++)
        {
            var nameLength = ReadUInt16(span, ref offset);
            Require(span, offset, nameLength);
            var name = Encoding.ASCII.GetString(span.Slice(offset, nameLength));
            offset += nameLength;
            var start = offset;
            Require(span, offset, 1);
            var tag = span[offset++];
            RowColumnType? type = null;
            if (tag != RowCanonicalizerV1.NullTag)
            {
                if (!Enum.IsDefined(typeof(RowColumnType), tag))
                {
                    throw new IntegrityFormatException(IntegrityFormatErrorCode.TypeMismatch, $"{name}: unknown type tag {tag}");
                }

                type = (RowColumnType)tag;
                offset += ValueLength(span, offset, type.Value, name);
            }

            columns.Add(new RowCanonicalColumn(name, type, canonicalBytes.Slice(start, offset - start)));
            if (i > 0 && string.CompareOrdinal(columns[i - 1].Name, name) >= 0)
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.RegistryNotSorted, name);
            }
        }

        if (offset != span.Length)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.UnknownSchemaVersion, "trailing bytes after the last column");
        }

        return new RowCanonicalDocument(header, columns);
    }

    private static int ValueLength(ReadOnlySpan<byte> span, int offset, RowColumnType type, string name)
    {
        switch (type)
        {
            case RowColumnType.Boolean:
                Require(span, offset, 1);
                return 1;
            case RowColumnType.Int32:
            case RowColumnType.Date:
                Require(span, offset, 4);
                return 4;
            case RowColumnType.Int64:
            case RowColumnType.TimestampUtc:
                Require(span, offset, 8);
                return 8;
            case RowColumnType.Uuid:
                Require(span, offset, 16);
                return 16;
            case RowColumnType.Decimal:
                Require(span, offset, 6);
                var coefficientLength = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 4, 2));
                Require(span, offset + 6, coefficientLength);
                return 6 + coefficientLength;
            case RowColumnType.Text:
            case RowColumnType.Bytes:
            case RowColumnType.Json:
                Require(span, offset, 4);
                var length = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4));
                if (length > int.MaxValue - 8)
                {
                    throw new IntegrityFormatException(IntegrityFormatErrorCode.UnknownSchemaVersion, name + ": length out of range");
                }

                Require(span, offset + 4, (int)length);
                return 4 + (int)length;
            default:
                throw new IntegrityFormatException(IntegrityFormatErrorCode.TypeMismatch, name);
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, ref int offset)
    {
        Require(bytes, offset, 2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
        offset += 2;
        return value;
    }

    private static void Require(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (length < 0 || offset + length > bytes.Length)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.UnknownSchemaVersion, "canonical bytes truncated");
        }
    }
}
