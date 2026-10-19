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

    public bool FramedValueEquals(RowCanonicalColumn other) => other is not null && FramedValue.Span.SequenceEqual(other.FramedValue.Span);
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
