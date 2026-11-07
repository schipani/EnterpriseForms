using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnterpriseForms.Integrity.Canonical;

namespace EnterpriseForms.Integrity.Search;

/// <summary>A value carried by an indexed posting, as bound by the seal. Each case encodes with a <see cref="RowColumnType"/> tag.</summary>
public abstract record SearchSealValue
{
    private SearchSealValue()
    {
    }

    public abstract RowColumnType Tag { get; }

    /// <summary>The normalized string (<see cref="SearchValueNormalizationV1"/>), never the raw value.</summary>
    public sealed record NormalizedString(string Value) : SearchSealValue
    {
        public override RowColumnType Tag => RowColumnType.Text;
    }

    public sealed record IntegerValue(long Value) : SearchSealValue
    {
        public override RowColumnType Tag => RowColumnType.Int64;
    }

    /// <summary>The canonical normalized decimal pair exactly as signed in the row (scale ≥ 0, minimal coefficient).</summary>
    public sealed record DecimalParts(int Scale, BigInteger Coefficient) : SearchSealValue
    {
        public override RowColumnType Tag => RowColumnType.Decimal;
    }

    public sealed record DateValue(DateOnly Value) : SearchSealValue
    {
        public override RowColumnType Tag => RowColumnType.Date;
    }

    public sealed record Timestamp(DateTimeOffset Value) : SearchSealValue
    {
        public override RowColumnType Tag => RowColumnType.TimestampUtc;
    }

    public sealed record BooleanValue(bool Value) : SearchSealValue
    {
        public override RowColumnType Tag => RowColumnType.Boolean;
    }

    public sealed record OptionValue(Guid FieldOptionId) : SearchSealValue
    {
        public override RowColumnType Tag => RowColumnType.Uuid;
    }
}

/// <summary>One Active searchability approval (coverage) the entry was derived under, with its approval-time bindings.</summary>
public sealed record SearchSealCoverage(Guid FieldDefinitionId, Guid FieldSearchApprovalId, string ValueType, string Classification, string ProtectionMode, IReadOnlyList<string> Operators);

/// <summary>One posting: the field, the manifest item it came from, the SHA-256 of the item's verified canonical bytes, and the derived value.</summary>
public sealed record SearchSealPosting(Guid FieldDefinitionId, Guid FieldSearchApprovalId, ReadOnlyMemory<byte> ManifestItemKey, Guid ManifestItemId, string EntityKind, string ValueType, ReadOnlyMemory<byte> SourceCanonicalDigest, SearchSealValue Value);

/// <summary>An approved field that carried something that must never be indexed on this record (for example an encrypted leaf).</summary>
public sealed record SearchSealAnomaly(Guid FieldDefinitionId, string AnomalyKind);

public sealed record SearchEntrySealInput(
    string KeyName,
    int KeyVersion,
    Guid OrganizationId,
    Guid FormDefinitionId,
    Guid FormRecordId,
    Guid CommittedOperationId,
    long CommittedContentRevision,
    ReadOnlyMemory<byte> CommittedManifestRoot,
    string RecordStatusName,
    IReadOnlyList<SearchSealCoverage> Coverages,
    IReadOnlyList<SearchSealPosting> Postings,
    IReadOnlyList<SearchSealAnomaly> Anomalies);

/// <summary>
/// IX-1 (C-126): the canonical message of an indexed-search entry seal, <c>DynamicForms/SearchEntrySeal/v1</c>
/// (docs/dynamic-forms/search-entry-seal-format.md). It reuses the RowHmac v1 encoding rules (big-endian integers,
/// big-endian UUIDs, length-prefixed ASCII identifiers and strict UTF-8 text, type-tagged values) under its own framing
/// marker and domain label; only the message layout is new. Lists are sorted by the encoder so the bytes do not depend on
/// input order, and duplicates are format errors. The message is signed ES256 (ECDSA P-256, SHA-256) by the Authority.
/// </summary>
public static class SearchEntrySealV1
{
    public const string Domain = "DynamicForms/SearchEntrySeal/v1";
    public const byte FramingMarker = 0x11;
    public const ushort SealSchemaVersion = 1;
    public const ushort DerivationVersion = 1;
    public const int ManifestRootLength = 32;
    public const int SourceDigestLength = 32;
    public const int ManifestItemKeyLength = 17;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly DateOnly EpochDate = new(1970, 1, 1);

    public static byte[] Encode(SearchEntrySealInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.KeyVersion < 1)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.KeyVersionRange, "seal key_version must be >= 1");
        }

        if (input.CommittedContentRevision < 1)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.RowRevisionRange, "committed_content_revision must be >= 1");
        }

        if (input.CommittedManifestRoot.Length != ManifestRootLength)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.InvalidDigestLength, "committed manifest root must be 32 bytes");
        }

        using var stream = new MemoryStream(1024);
        stream.WriteByte(FramingMarker);
        WriteAscii(stream, Domain);
        WriteUInt16(stream, SealSchemaVersion);
        WriteUInt16(stream, DerivationVersion);
        WriteAscii(stream, input.KeyName);
        WriteUInt32(stream, (uint)input.KeyVersion);
        WriteUuid(stream, input.OrganizationId);
        WriteUuid(stream, input.FormDefinitionId);
        WriteUuid(stream, input.FormRecordId);
        WriteUuid(stream, input.CommittedOperationId);
        WriteUInt64(stream, (ulong)input.CommittedContentRevision);
        WriteBytes(stream, input.CommittedManifestRoot.Span);
        WriteText(stream, input.RecordStatusName);

        var coverages = input.Coverages.OrderBy(c => c.FieldDefinitionId.ToByteArray(bigEndian: true), ByteOrder.Instance).ThenBy(c => c.FieldSearchApprovalId.ToByteArray(bigEndian: true), ByteOrder.Instance).ToList();
        for (var i = 1; i < coverages.Count; i++)
        {
            if (coverages[i].FieldDefinitionId == coverages[i - 1].FieldDefinitionId)
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.DuplicateItemKey, "two coverages for one field");
            }
        }

        WriteUInt32(stream, (uint)coverages.Count);
        foreach (var coverage in coverages)
        {
            WriteUuid(stream, coverage.FieldDefinitionId);
            WriteUuid(stream, coverage.FieldSearchApprovalId);
            WriteAscii(stream, coverage.ValueType);
            WriteAscii(stream, coverage.Classification);
            WriteAscii(stream, coverage.ProtectionMode);
            var operators = coverage.Operators.Distinct(StringComparer.Ordinal).OrderBy(o => o, StringComparer.Ordinal).ToList();
            if (operators.Count != coverage.Operators.Count)
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.DuplicateItemKey, "duplicate approved operator");
            }

            WriteUInt32(stream, (uint)operators.Count);
            foreach (var op in operators)
            {
                WriteAscii(stream, op);
            }
        }

        var postings = input.Postings.OrderBy(p => p.FieldDefinitionId.ToByteArray(bigEndian: true), ByteOrder.Instance).ThenBy(p => p.ManifestItemKey.ToArray(), ByteOrder.Instance).ToList();
        for (var i = 1; i < postings.Count; i++)
        {
            if (postings[i].FieldDefinitionId == postings[i - 1].FieldDefinitionId && postings[i].ManifestItemKey.Span.SequenceEqual(postings[i - 1].ManifestItemKey.Span))
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.DuplicateItemKey, "two postings for one manifest item");
            }
        }

        WriteUInt32(stream, (uint)postings.Count);
        foreach (var posting in postings)
        {
            if (posting.ManifestItemKey.Length != ManifestItemKeyLength)
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.KindCodeMismatch, "manifest item key must be 17 bytes");
            }

            if (posting.SourceCanonicalDigest.Length != SourceDigestLength)
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.InvalidDigestLength, "source digest must be 32 bytes");
            }

            if (!coverages.Any(c => c.FieldDefinitionId == posting.FieldDefinitionId && c.FieldSearchApprovalId == posting.FieldSearchApprovalId))
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.UnregisteredColumn, "posting without a coverage for its field and approval");
            }

            WriteUuid(stream, posting.FieldDefinitionId);
            WriteUuid(stream, posting.FieldSearchApprovalId);
            WriteBytes(stream, posting.ManifestItemKey.Span);
            WriteUuid(stream, posting.ManifestItemId);
            WriteAscii(stream, posting.EntityKind);
            WriteAscii(stream, posting.ValueType);
            WriteBytes(stream, posting.SourceCanonicalDigest.Span);
            WriteValue(stream, posting.Value);
        }

        var anomalies = input.Anomalies.OrderBy(a => a.FieldDefinitionId.ToByteArray(bigEndian: true), ByteOrder.Instance).ThenBy(a => a.AnomalyKind, StringComparer.Ordinal).ToList();
        for (var i = 1; i < anomalies.Count; i++)
        {
            if (anomalies[i].FieldDefinitionId == anomalies[i - 1].FieldDefinitionId && string.Equals(anomalies[i].AnomalyKind, anomalies[i - 1].AnomalyKind, StringComparison.Ordinal))
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.DuplicateItemKey, "duplicate anomaly");
            }
        }

        WriteUInt32(stream, (uint)anomalies.Count);
        foreach (var anomaly in anomalies)
        {
            WriteUuid(stream, anomaly.FieldDefinitionId);
            WriteAscii(stream, anomaly.AnomalyKind);
        }

        return stream.ToArray();
    }

    private static void WriteValue(Stream stream, SearchSealValue value)
    {
        stream.WriteByte((byte)value.Tag);
        switch (value)
        {
            case SearchSealValue.NormalizedString s:
                WriteText(stream, s.Value);
                break;
            case SearchSealValue.IntegerValue i:
                Span<byte> int64 = stackalloc byte[8];
                BinaryPrimitives.WriteInt64BigEndian(int64, i.Value);
                stream.Write(int64);
                break;
            case SearchSealValue.DecimalParts d:
                if (d.Scale < 0)
                {
                    throw new IntegrityFormatException(IntegrityFormatErrorCode.DecimalRange, "scale must be non-negative");
                }

                var coefficient = d.Coefficient.ToByteArray(isUnsigned: false, isBigEndian: true);
                if (coefficient.Length > ushort.MaxValue)
                {
                    throw new IntegrityFormatException(IntegrityFormatErrorCode.DecimalRange, "coefficient too large");
                }

                WriteInt32(stream, d.Scale);
                WriteUInt16(stream, (ushort)coefficient.Length);
                stream.Write(coefficient);
                break;
            case SearchSealValue.DateValue date:
                WriteInt32(stream, date.Value.DayNumber - EpochDate.DayNumber);
                break;
            case SearchSealValue.Timestamp ts:
                var ticks = ts.Value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
                if (ticks % 10 != 0)
                {
                    throw new IntegrityFormatException(IntegrityFormatErrorCode.TimestampPrecision, "timestamps are bound at microsecond precision");
                }

                Span<byte> micros = stackalloc byte[8];
                BinaryPrimitives.WriteInt64BigEndian(micros, ticks / 10);
                stream.Write(micros);
                break;
            case SearchSealValue.BooleanValue b:
                stream.WriteByte(b.Value ? (byte)1 : (byte)0);
                break;
            case SearchSealValue.OptionValue o:
                WriteUuid(stream, o.FieldOptionId);
                break;
            default:
                throw new IntegrityFormatException(IntegrityFormatErrorCode.TypeMismatch, value.GetType().Name);
        }
    }

    private static void WriteAscii(Stream stream, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 200 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/'))
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.InvalidColumnName, "identifier must be 1..200 ASCII [A-Za-z0-9-_./]");
        }

        WriteUInt16(stream, (ushort)value.Length);
        stream.Write(Encoding.ASCII.GetBytes(value));
    }

    private static void WriteText(Stream stream, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] encoded;
        try
        {
            encoded = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.TextInvalidUnicode, ex.Message);
        }

        WriteUInt32(stream, (uint)encoded.Length);
        stream.Write(encoded);
    }

    private static void WriteBytes(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    private static void WriteUuid(Stream stream, Guid value) => stream.Write(value.ToByteArray(bigEndian: true));

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    /// <summary>Unsigned lexicographic byte order (the same order as the Merkle item keys).</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);
            return x.AsSpan().SequenceCompareTo(y);
        }
    }
}
