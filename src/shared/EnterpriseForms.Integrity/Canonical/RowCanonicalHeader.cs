using System.Buffers.Binary;
using System.Text;

namespace EnterpriseForms.Integrity.Canonical;

/// <summary>
/// The trusted-binding prefix of a <c>DynamicForms/RowHmac/v1</c> canonical row. The signing authority parses this
/// from candidate bytes supplied by the business runtime so it can enforce that every item it signs is bound to the
/// organization, key version and entity kind it approved — without needing the business column registry.
/// </summary>
public sealed record RowCanonicalHeader(
    int SchemaVersion,
    uint KeyVersion,
    string EntityKind,
    Guid OrganizationId,
    Guid PrimaryKeyId,
    ulong RowRevision,
    uint ColumnCount)
{
    /// <summary>Parses the framing prefix; any deviation from the v1 framing is a format error (fail closed).</summary>
    public static RowCanonicalHeader Parse(ReadOnlySpan<byte> canonicalBytes)
    {
        var offset = 0;
        if (canonicalBytes.Length < 1 || canonicalBytes[offset++] != RowCanonicalizerV1.FramingMarker)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.UnknownSchemaVersion, "missing RowHmac v1 framing marker");
        }

        var domain = ReadAsciiLen16(canonicalBytes, ref offset);
        if (!string.Equals(domain, RowCanonicalizerV1.Domain, StringComparison.Ordinal))
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.UnknownSchemaVersion, $"unexpected domain '{domain}'");
        }

        var schemaVersion = ReadUInt16(canonicalBytes, ref offset);
        var keyVersion = ReadUInt32(canonicalBytes, ref offset);
        var entityKind = ReadAsciiLen16(canonicalBytes, ref offset);
        var organizationId = ReadUuid(canonicalBytes, ref offset);
        var primaryKeyId = ReadUuid(canonicalBytes, ref offset);
        var rowRevision = ReadUInt64(canonicalBytes, ref offset);
        var columnCount = ReadUInt32(canonicalBytes, ref offset);
        if (schemaVersion == 0 || keyVersion == 0 || rowRevision == 0)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.SchemaVersionRange, "schema_version, key_version and row_revision must be >= 1");
        }

        return new RowCanonicalHeader(schemaVersion, keyVersion, entityKind, organizationId, primaryKeyId, rowRevision, columnCount);
    }

    private static string ReadAsciiLen16(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var length = ReadUInt16(bytes, ref offset);
        Require(bytes, offset, length);
        var text = Encoding.ASCII.GetString(bytes.Slice(offset, length));
        offset += length;
        return text;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, ref int offset)
    {
        Require(bytes, offset, 2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
        offset += 2;
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, ref int offset)
    {
        Require(bytes, offset, 4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, ref int offset)
    {
        Require(bytes, offset, 8);
        var value = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(offset, 8));
        offset += 8;
        return value;
    }

    private static Guid ReadUuid(ReadOnlySpan<byte> bytes, ref int offset)
    {
        Require(bytes, offset, 16);
        var value = new Guid(bytes.Slice(offset, 16), bigEndian: true);
        offset += 16;
        return value;
    }

    private static void Require(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (offset + length > bytes.Length)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.UnknownSchemaVersion, "canonical bytes truncated");
        }
    }
}
