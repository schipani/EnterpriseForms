using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;

namespace EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2;

/// <summary>
/// The commitment input H0 of an Envelope v2 header (§3.3 fields 1–11), validated on construction:
/// <code>
/// "EFE2" ‖ u16 2 ‖ u8 kind ‖ u8 aead(1) ‖ u8 kdf(1) ‖ u8 wrap ‖ u8 flags(0) ‖ organization(16)
///        ‖ u8 len ‖ purpose token ‖ u16 len ‖ KEK reference ‖ data key identifier(16)
/// </code>
/// The purpose is the kind's purpose; the reference is the canonical scoped reference of the wrap algorithm's family and names
/// exactly this organization and purpose; the data key identifier is a UUIDv7. Any other combination is refused.
/// </summary>
internal sealed class EnvelopeV2HeaderPrefix
{
    private EnvelopeV2HeaderPrefix(EnvelopeV2PayloadKind kind, EnvelopeV2WrapAlgorithm wrap, Guid organizationId, EncryptionKeyPurpose purpose, string kekReference, int kekVersion, Guid dataKeyIdentifier)
    {
        Kind = kind;
        WrapAlgorithm = wrap;
        OrganizationId = organizationId;
        Purpose = purpose;
        KekReference = kekReference;
        KekVersion = kekVersion;
        DataKeyIdentifier = dataKeyIdentifier;
    }

    public EnvelopeV2PayloadKind Kind { get; }

    public EnvelopeV2WrapAlgorithm WrapAlgorithm { get; }

    public Guid OrganizationId { get; }

    public EncryptionKeyPurpose Purpose { get; }

    public string KekReference { get; }

    public int KekVersion { get; }

    public Guid DataKeyIdentifier { get; }

    public static EnvelopeV2HeaderPrefix Create(EnvelopeV2PayloadKind kind, EnvelopeV2WrapAlgorithm wrap, Guid organizationId, string kekReference, Guid dataKeyIdentifier)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(wrap))
        {
            throw EnvelopeV2Format.Unsupported();
        }

        var purpose = EnvelopeV2Format.PurposeOf(kind);
        if (organizationId == Guid.Empty || !EnvelopeV2Format.IsUuidVersion7(dataKeyIdentifier)
            || kekReference is null || kekReference.Length is < 1 or > EnvelopeV2Format.MaximumReferenceLength || !EnvelopeV2Format.IsAscii(kekReference, EnvelopeV2Format.IsReferenceChar)
            || !KeyScopeEncoding.TryParseReference(kekReference, EnvelopeV2Format.ReferencePrefixOf(wrap), out var referenceOrganization, out var referencePurpose, out var version)
            || referenceOrganization != organizationId || !ReferenceEquals(referencePurpose, purpose))
        {
            throw EnvelopeV2Format.Malformed();
        }

        return new EnvelopeV2HeaderPrefix(kind, wrap, organizationId, purpose, kekReference, version, dataKeyIdentifier);
    }

    /// <summary>H0, the exact bytes the commitment and every key derivation are bound to.</summary>
    public byte[] Encode() =>
        new EnvelopeV2Writer()
            .Bytes(EnvelopeV2Format.Magic).U16(EnvelopeV2Format.FormatVersion).U8((byte)Kind).U8(EnvelopeV2Format.AeadAes256Gcm).U8(EnvelopeV2Format.KdfHkdfSha256).U8((byte)WrapAlgorithm).U8(0)
            .Uuid(OrganizationId).Ascii8(Purpose.Token).Ascii16(KekReference).Uuid(DataKeyIdentifier)
            .ToArray();

    /// <summary>Reads H0 strictly. Not a v2 envelope (magic), another format version or an unknown algorithm is unsupported; anything else that fails is malformed.</summary>
    public static EnvelopeV2HeaderPrefix Read(ref EnvelopeV2Reader reader)
    {
        if (!reader.Take(4).SequenceEqual(EnvelopeV2Format.Magic) || reader.U16() != EnvelopeV2Format.FormatVersion)
        {
            throw EnvelopeV2Format.Unsupported();
        }

        var kind = (EnvelopeV2PayloadKind)reader.U8();
        var aead = reader.U8();
        var kdf = reader.U8();
        var wrap = (EnvelopeV2WrapAlgorithm)reader.U8();
        var flags = reader.U8();
        if (!Enum.IsDefined(kind) || aead != EnvelopeV2Format.AeadAes256Gcm || kdf != EnvelopeV2Format.KdfHkdfSha256 || !Enum.IsDefined(wrap))
        {
            throw EnvelopeV2Format.Unsupported();
        }

        if (flags != 0)
        {
            throw EnvelopeV2Format.Malformed();
        }

        var organization = reader.Uuid();
        var purposeToken = reader.Ascii(reader.U8(), EnvelopeV2Format.MaximumPurposeLength, EnvelopeV2Format.IsPurposeChar);
        var reference = reader.Ascii(reader.U16(), EnvelopeV2Format.MaximumReferenceLength, EnvelopeV2Format.IsReferenceChar);
        var dataKey = reader.Uuid();
        var prefix = Create(kind, wrap, organization, reference, dataKey);
        if (!string.Equals(purposeToken, prefix.Purpose.Token, StringComparison.Ordinal))
        {
            throw EnvelopeV2Format.Malformed();
        }

        return prefix;
    }
}

/// <summary>The full header H = H0 ‖ key commitment (32 bytes).</summary>
internal sealed class EnvelopeV2Header
{
    private readonly byte[] commitment;

    public EnvelopeV2Header(EnvelopeV2HeaderPrefix prefix, ReadOnlySpan<byte> keyCommitment)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (keyCommitment.Length != EnvelopeV2Format.CommitmentSize)
        {
            throw EnvelopeV2Format.Malformed();
        }

        Prefix = prefix;
        commitment = keyCommitment.ToArray();
    }

    public EnvelopeV2HeaderPrefix Prefix { get; }

    public ReadOnlySpan<byte> KeyCommitment => commitment;

    public byte[] Encode() => new EnvelopeV2Writer().Bytes(Prefix.Encode()).Bytes(commitment).ToArray();

    public static EnvelopeV2Header Read(ref EnvelopeV2Reader reader) => new(EnvelopeV2HeaderPrefix.Read(ref reader), reader.Take(EnvelopeV2Format.CommitmentSize));

    public bool SameBytesAs(EnvelopeV2Header other) => CryptographicOperations.FixedTimeEquals(Encode(), other.Encode());
}

/// <summary>
/// The field-item context C_field (§3.6): record(16) ‖ field(16) ‖ item(16) ‖ u8 len ‖ logical type. A FieldValueItem carries one of
/// the existing logical types; a SealedKey carries exactly the creation-fingerprint seal type.
/// </summary>
internal sealed record FieldItemIdentity(Guid FormRecordId, Guid FieldDefinitionId, Guid FieldValueItemId, string LogicalType)
{
    public void Validate(EnvelopeV2PayloadKind kind)
    {
        var typeAllowed = kind switch
        {
            EnvelopeV2PayloadKind.FieldValueItem => LogicalType is not null && EnvelopeV2Format.FieldLogicalTypes.Contains(LogicalType),
            EnvelopeV2PayloadKind.SealedKey => string.Equals(LogicalType, EnvelopeV2Format.SealedKeyLogicalType, StringComparison.Ordinal),
            _ => false,
        };
        if (!typeAllowed || FormRecordId == Guid.Empty || FieldDefinitionId == Guid.Empty || FieldValueItemId == Guid.Empty)
        {
            throw EnvelopeV2Format.Malformed();
        }
    }

    public byte[] Encode() => new EnvelopeV2Writer().Uuid(FormRecordId).Uuid(FieldDefinitionId).Uuid(FieldValueItemId).Ascii8(LogicalType).ToArray();

    public static FieldItemIdentity Read(ref EnvelopeV2Reader reader, EnvelopeV2PayloadKind kind)
    {
        var identity = new FieldItemIdentity(reader.Uuid(), reader.Uuid(), reader.Uuid(), reader.Ascii(reader.U8(), EnvelopeV2Format.MaximumLogicalTypeLength, EnvelopeV2Format.IsLogicalTypeChar));
        identity.Validate(kind);
        return identity;
    }
}

/// <summary>The identity a document version is bound to (record, attachment, positive version number).</summary>
internal sealed record DocumentVersionIdentity(Guid FormRecordId, Guid FileAttachmentId, int VersionNumber)
{
    public void Validate()
    {
        if (FormRecordId == Guid.Empty || FileAttachmentId == Guid.Empty || VersionNumber < 1)
        {
            throw EnvelopeV2Format.Malformed();
        }
    }
}

/// <summary>The document context C_doc (§3.6): record(16) ‖ attachment(16) ‖ u32 version ‖ u32 segment size ‖ nonce prefix(8).</summary>
internal sealed class DocumentContext
{
    private readonly byte[] noncePrefix;

    public DocumentContext(DocumentVersionIdentity identity, int segmentSizeBytes, ReadOnlySpan<byte> noncePrefix)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        if (segmentSizeBytes != EnvelopeV2Format.SegmentSize)
        {
            throw EnvelopeV2Format.Unsupported();
        }

        if (noncePrefix.Length != EnvelopeV2Format.NoncePrefixSize)
        {
            throw EnvelopeV2Format.Malformed();
        }

        Identity = identity;
        SegmentSizeBytes = segmentSizeBytes;
        this.noncePrefix = noncePrefix.ToArray();
    }

    public DocumentVersionIdentity Identity { get; }

    public int SegmentSizeBytes { get; }

    public ReadOnlySpan<byte> NoncePrefix => noncePrefix;

    public byte[] Encode() =>
        new EnvelopeV2Writer().Uuid(Identity.FormRecordId).Uuid(Identity.FileAttachmentId).U32((uint)Identity.VersionNumber).U32((uint)SegmentSizeBytes).Bytes(noncePrefix).ToArray();

    public static DocumentContext Read(ref EnvelopeV2Reader reader)
    {
        var record = reader.Uuid();
        var attachment = reader.Uuid();
        var version = reader.U32();
        var segment = reader.U32();
        if (version is 0 or > int.MaxValue)
        {
            throw EnvelopeV2Format.Malformed();
        }

        if (segment != EnvelopeV2Format.SegmentSize)
        {
            throw EnvelopeV2Format.Unsupported();
        }

        return new DocumentContext(new DocumentVersionIdentity(record, attachment, (int)version), (int)segment, reader.Take(EnvelopeV2Format.NoncePrefixSize));
    }
}

/// <summary>
/// The Envelope v2 key schedule (§3.7), HKDF-SHA256 (RFC 5869, .NET <see cref="HKDF"/>):
/// <code>
/// PRK          = HKDF-Extract(salt = "DynamicForms/EnvelopeV2/Extract", IKM = seed)
/// commitment   = HKDF-Expand(PRK, "DynamicForms/EnvelopeV2/Commitment" ‖ H0, 32)
/// item key     = HKDF-Expand(PRK, "DynamicForms/EnvelopeV2/FieldItem"  ‖ H0 ‖ C_field, 32)
/// document key = HKDF-Expand(PRK, "DynamicForms/EnvelopeV2/Document"   ‖ H0 ‖ C_doc, 32)
/// </code>
/// The PRK lives only in this object and is zeroed on <see cref="Dispose"/>; callers zero every derived key after use.
/// </summary>
internal sealed class EnvelopeV2KeySchedule : IDisposable
{
    private readonly byte[] prk = new byte[32];
    private bool disposed;

    public EnvelopeV2KeySchedule(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != EnvelopeV2Format.SeedSize)
        {
            throw new EnvelopeV2Exception(EnvelopeV2FailureReason.InvalidDataKey);
        }

        HKDF.Extract(HashAlgorithmName.SHA256, seed, EnvelopeV2Format.ExtractSalt, prk);
    }

    public byte[] Commitment(ReadOnlySpan<byte> h0) => Expand(EnvelopeV2Format.CommitmentLabel, h0, []);

    public byte[] FieldItemKey(ReadOnlySpan<byte> h0, ReadOnlySpan<byte> fieldContext) => Expand(EnvelopeV2Format.FieldItemLabel, h0, fieldContext);

    public byte[] DocumentKey(ReadOnlySpan<byte> h0, ReadOnlySpan<byte> documentContext) => Expand(EnvelopeV2Format.DocumentLabel, h0, documentContext);

    /// <summary>Constant-time comparison of a stored commitment with the one this seed produces for <paramref name="h0"/>.</summary>
    public bool Commits(ReadOnlySpan<byte> h0, ReadOnlySpan<byte> storedCommitment)
    {
        var expected = Commitment(h0);
        return storedCommitment.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, storedCommitment);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(prk);
        disposed = true;
    }

    private byte[] Expand(ReadOnlySpan<byte> label, ReadOnlySpan<byte> h0, ReadOnlySpan<byte> context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var info = new byte[label.Length + h0.Length + context.Length];
        label.CopyTo(info);
        h0.CopyTo(info.AsSpan(label.Length));
        context.CopyTo(info.AsSpan(label.Length + h0.Length));
        var output = new byte[EnvelopeV2Format.DerivedKeySize];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, output, info);
        return output;
    }
}
