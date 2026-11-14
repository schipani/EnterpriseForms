using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;

namespace EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2;

// E3-M1 (docs/security/E3_ENVELOPE_V2_KEY_HIERARCHY_DESIGN.md §3): the isolated Envelope v2 cryptographic core. Every type in this
// namespace is internal and is reached only by the contract tests (InternalsVisibleTo). Nothing in the product composes it: the
// architecture test EnvelopeV2IsolationTests keeps it disconnected until E3-M2 is separately authorized.

/// <summary>Envelope v2 payload kinds (§3.3 field 3). Each kind has exactly one key purpose.</summary>
internal enum EnvelopeV2PayloadKind : byte
{
    FieldValueItem = 1,
    DocumentVersion = 2,
    SealedKey = 3,
}

/// <summary>Wrap-algorithm registry (§3.5). Only the two Development/Testing wraps exist; codes 3 and 4 are reserved and refused.</summary>
internal enum EnvelopeV2WrapAlgorithm : byte
{
    LocalKeyringAes256Gcm = 1,
    VaultTransitAes256Gcm96 = 2,
}

/// <summary>Why an envelope was refused. Maps to the E0-4 reasons when E3-M2 wires the core: the first four to UNSUPPORTED_ENVELOPE, KeyCommitmentMismatch and InvalidDataKey to KEY_MISMATCH, AuthenticationFailed to AUTHENTICATION_FAILED.</summary>
internal enum EnvelopeV2FailureReason
{
    MalformedEnvelope,
    UnsupportedEnvelope,
    IdentityMismatch,
    InconsistentDataKeyGroup,
    KeyCommitmentMismatch,
    InvalidDataKey,
    AuthenticationFailed,
}

/// <summary>A refused envelope. The message is fixed per reason and never contains envelope bytes, identifiers, keys or plaintext.</summary>
internal sealed class EnvelopeV2Exception(EnvelopeV2FailureReason reason) : CryptographicException(MessageFor(reason))
{
    public EnvelopeV2FailureReason Reason { get; } = reason;

    private static string MessageFor(EnvelopeV2FailureReason reason) => reason switch
    {
        EnvelopeV2FailureReason.MalformedEnvelope => "the encryption envelope is malformed",
        EnvelopeV2FailureReason.UnsupportedEnvelope => "the encryption envelope format, version or algorithm is not supported",
        EnvelopeV2FailureReason.IdentityMismatch => "the encryption envelope does not belong to the expected organization, purpose or item",
        EnvelopeV2FailureReason.InconsistentDataKeyGroup => "envelopes sharing a data key disagree or repeat an item or nonce",
        EnvelopeV2FailureReason.KeyCommitmentMismatch => "the unwrapped data key does not match the envelope's key commitment",
        EnvelopeV2FailureReason.InvalidDataKey => "the unwrapped data key has the wrong size",
        EnvelopeV2FailureReason.AuthenticationFailed => "the encrypted content failed authentication",
        _ => "the encryption envelope was refused",
    };
}

/// <summary>Envelope v2 constants and the closed vocabularies of §3.2–§3.6.</summary>
internal static class EnvelopeV2Format
{
    public const ushort FormatVersion = 2;
    public const byte AeadAes256Gcm = 1;
    public const byte KdfHkdfSha256 = 1;
    public const int SeedSize = 32;
    public const int DerivedKeySize = 32;
    public const int CommitmentSize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int NoncePrefixSize = 8;
    public const int SegmentSize = 1024 * 1024;
    public const int WrappedSeedSize = 60;
    public const int MaximumPurposeLength = 32;
    public const int MaximumReferenceLength = 128;
    public const int MaximumLogicalTypeLength = 32;

    /// <summary>Parser bound on a field item's ciphertext (field values are short; v1 has no format-level bound).</summary>
    public const int MaximumFieldCiphertextBytes = 1024 * 1024;

    /// <summary>The logical types a FieldValueItem may carry (the existing <c>ck_field_encrypted_values_logical_type</c> set).</summary>
    public static readonly IReadOnlySet<string> FieldLogicalTypes = new HashSet<string>(StringComparer.Ordinal) { "String", "Integer", "Decimal", "Date", "DateTime", "Boolean", "Guid", "Option" };

    /// <summary>The single logical type of a SealedKey: the creation-idempotency fingerprint key.</summary>
    public const string SealedKeyLogicalType = CreationRequestFingerprint.SealLogicalType;

    public static ReadOnlySpan<byte> Magic => "EFE2"u8;

    public static ReadOnlySpan<byte> ExtractSalt => "DynamicForms/EnvelopeV2/Extract"u8;

    public static ReadOnlySpan<byte> CommitmentLabel => "DynamicForms/EnvelopeV2/Commitment"u8;

    public static ReadOnlySpan<byte> FieldItemLabel => "DynamicForms/EnvelopeV2/FieldItem"u8;

    public static ReadOnlySpan<byte> DocumentLabel => "DynamicForms/EnvelopeV2/Document"u8;

    /// <summary>Strict UTF-8: invalid input throws instead of being replaced.</summary>
    public static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static EncryptionKeyPurpose PurposeOf(EnvelopeV2PayloadKind kind) => kind switch
    {
        EnvelopeV2PayloadKind.FieldValueItem => EncryptionKeyPurpose.FieldValue,
        EnvelopeV2PayloadKind.DocumentVersion => EncryptionKeyPurpose.Document,
        EnvelopeV2PayloadKind.SealedKey => EncryptionKeyPurpose.IdempotencyProtection,
        _ => throw new EnvelopeV2Exception(EnvelopeV2FailureReason.UnsupportedEnvelope),
    };

    /// <summary>The field-envelope kind of a purpose. The auditor reason note keeps its v1 envelope (E3-D12) and has no v2 kind.</summary>
    public static EnvelopeV2PayloadKind FieldKindOf(EncryptionKeyPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        return ReferenceEquals(purpose, EncryptionKeyPurpose.FieldValue) ? EnvelopeV2PayloadKind.FieldValueItem
            : ReferenceEquals(purpose, EncryptionKeyPurpose.IdempotencyProtection) ? EnvelopeV2PayloadKind.SealedKey
            : throw new ArgumentException("field envelopes v2 seal only field values and idempotency keys", nameof(purpose));
    }

    public static string ReferencePrefixOf(EnvelopeV2WrapAlgorithm wrap) => wrap switch
    {
        EnvelopeV2WrapAlgorithm.LocalKeyringAes256Gcm => LocalDevelopmentKeyProvider.ScopedReferencePrefix,
        EnvelopeV2WrapAlgorithm.VaultTransitAes256Gcm96 => VaultTransitKeyProvider.ReferencePrefix,
        _ => throw new EnvelopeV2Exception(EnvelopeV2FailureReason.UnsupportedEnvelope),
    };

    /// <summary>The wrap algorithm named by a reference's family prefix; a legacy, foreign or non-canonical reference has none.</summary>
    public static EnvelopeV2WrapAlgorithm WrapAlgorithmOf(string keyReference)
    {
        var family = (keyReference ?? string.Empty).Split(':')[0];
        return family switch
        {
            LocalDevelopmentKeyProvider.ScopedReferencePrefix => EnvelopeV2WrapAlgorithm.LocalKeyringAes256Gcm,
            VaultTransitKeyProvider.ReferencePrefix => EnvelopeV2WrapAlgorithm.VaultTransitAes256Gcm96,
            _ => throw new EnvelopeV2Exception(EnvelopeV2FailureReason.UnsupportedEnvelope),
        };
    }

    /// <summary>The wrapped-seed length of a wrap algorithm (both Development/Testing wraps: nonce 12 ‖ ciphertext 32 ‖ tag 16).</summary>
    public static int WrappedSeedLengthOf(EnvelopeV2WrapAlgorithm wrap) => wrap switch
    {
        EnvelopeV2WrapAlgorithm.LocalKeyringAes256Gcm or EnvelopeV2WrapAlgorithm.VaultTransitAes256Gcm96 => WrappedSeedSize,
        _ => throw new EnvelopeV2Exception(EnvelopeV2FailureReason.UnsupportedEnvelope),
    };

    /// <summary>RFC 9562 UUIDv7 (version nibble 7, variant 10xx) in network byte order.</summary>
    public static bool IsUuidVersion7(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return (bytes[6] >> 4) == 7 && (bytes[8] & 0xC0) == 0x80;
    }

    public static bool IsAscii(string value, Func<char, bool> allowed) => value.All(c => c < 0x80 && allowed(c));

    public static bool IsReferenceChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '/' or '-';

    public static bool IsPurposeChar(char c) => c is (>= 'a' and <= 'z') or '-';

    public static bool IsLogicalTypeChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-';

    public static Exception Malformed() => new EnvelopeV2Exception(EnvelopeV2FailureReason.MalformedEnvelope);

    public static Exception Unsupported() => new EnvelopeV2Exception(EnvelopeV2FailureReason.UnsupportedEnvelope);
}

/// <summary>Big-endian canonical writer (§3.2).</summary>
internal sealed class EnvelopeV2Writer
{
    private readonly System.Buffers.ArrayBufferWriter<byte> buffer = new();

    public EnvelopeV2Writer Bytes(ReadOnlySpan<byte> value)
    {
        System.Buffers.BuffersExtensions.Write(buffer, value);
        return this;
    }

    public EnvelopeV2Writer U8(byte value) => Bytes([value]);

    public EnvelopeV2Writer U16(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        return Bytes(b);
    }

    public EnvelopeV2Writer U32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return Bytes(b);
    }

    public EnvelopeV2Writer Uuid(Guid value)
    {
        Span<byte> b = stackalloc byte[16];
        value.TryWriteBytes(b, bigEndian: true, out _);
        return Bytes(b);
    }

    public EnvelopeV2Writer Ascii8(string value) => U8(checked((byte)value.Length)).Bytes(Encoding.ASCII.GetBytes(value));

    public EnvelopeV2Writer Ascii16(string value) => U16(checked((ushort)value.Length)).Bytes(Encoding.ASCII.GetBytes(value));

    public byte[] ToArray() => buffer.WrittenSpan.ToArray();
}

/// <summary>Big-endian canonical reader (§3.2): every short read is a malformed envelope; <see cref="End"/> refuses trailing bytes.</summary>
internal ref struct EnvelopeV2Reader(ReadOnlySpan<byte> source)
{
    private readonly ReadOnlySpan<byte> source = source;
    private int offset;

    public ReadOnlySpan<byte> Take(int length)
    {
        if (length < 0 || source.Length - offset < length)
        {
            throw EnvelopeV2Format.Malformed();
        }

        var slice = source.Slice(offset, length);
        offset += length;
        return slice;
    }

    public byte U8() => Take(1)[0];

    public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

    public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public Guid Uuid() => new(Take(16), bigEndian: true);

    public string Ascii(int length, int maximum, Func<char, bool> allowed)
    {
        if (length < 1 || length > maximum)
        {
            throw EnvelopeV2Format.Malformed();
        }

        var bytes = Take(length);
        foreach (var b in bytes)
        {
            if (b >= 0x80 || !allowed((char)b))
            {
                throw EnvelopeV2Format.Malformed();
            }
        }

        return Encoding.ASCII.GetString(bytes);
    }

    public readonly void End()
    {
        if (offset != source.Length)
        {
            throw EnvelopeV2Format.Malformed();
        }
    }
}
