using System.Buffers.Binary;
using System.Globalization;
using EnterpriseForms.Integrity.Canonical;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// E3-M2d (docs/security/E3_M2D_IMPLEMENTATION_RECORD.md; contracts E3-M2 §2.3/§2.4/§3.5, E3-M2b §5.1/§8, owner decisions D-1..D-12):
/// the Authority's Envelope v2 rules, used only for rows of signed schema 2 / envelope 2 (the S-4 tie admits no other schema-2
/// pair). They are dark: they evaluate v2 rows, but no v2 row is accepted anywhere (<see cref="Reasons.VersionNotEnabled"/>). At
/// Prepare only a byte-identical carry of a v2 row of the current accepted head passes (and no product path produces such a head);
/// Commit refuses every v2 row (<see cref="CommitViolation"/>). Decided from canonical bytes alone; the Authority holds no key and never checks AEAD authentication or the commitment's
/// correctness (the business opener does, E3-M1). The v1 rules (<see cref="EnvelopeRules"/>) are unchanged and never see a v2 row.
/// <para>
/// The Authority keeps its own copy of the M1 registry (wrap codes, families, lengths); an architecture test cross-checks it
/// against the business definitions (ADR-ARCH-001: no reference to business Infrastructure). Error texts carry reason codes only.
/// </para>
/// </summary>
public static class EnvelopeV2Rules
{
    public const int EnvelopeVersionV2 = 2;
    public const int SchemaVersionV2 = 2;
    public const int CommitmentLength = 32;

    /// <summary>
    /// D-8: the largest legitimate v2 field ciphertext. The ciphertext is exactly the strict UTF-8 bytes of the canonical wire
    /// string (FieldEnvelopeV2: no framing; the tag is a separate column). The largest wire string is a String value of 4,000
    /// UTF-16 code units (FieldValueCodec.ValidateString; FormLayout caps maxLength at 1..4000), at most 3 UTF-8 bytes per code
    /// unit (a surrogate pair is 4 bytes for 2 units; a lone surrogate cannot be encoded strictly), so 12,000 bytes. Every other
    /// type is shorter (option key 64, UUID 36, decimal 24, integer and instant 20, date 10, boolean 5).
    /// </summary>
    public const int MaximumFieldCiphertextBytes = 12_000;

    /// <summary>Per-segment document overhead: 4-byte big-endian length prefix plus the 16-byte tag (DocumentEnvelopeV2Cipher).</summary>
    public const int DocumentSegmentOverheadBytes = 20;

    /// <summary>The M1 wrap-algorithm registry: code → (key-reference family, wrapped seed length). Codes 3 and 4 are reserved and refused.</summary>
    public static readonly IReadOnlyDictionary<int, (string Family, int WrappedLength)> WrapAlgorithms = new Dictionary<int, (string, int)>
    {
        [1] = ("localdev", 60),
        [2] = ("vault-transit", 60),
    };

    /// <summary>Reason codes (stable, identifier-free). Shape reasons shared with v1 reuse <see cref="EnvelopeRules.Reasons"/>.</summary>
    public static class Reasons
    {
        public const string WrapAlgorithmUnsupported = "ENVELOPE_WRAP_ALGORITHM_UNSUPPORTED";
        public const string KeyReferenceFamily = "ENVELOPE_KEY_REFERENCE_FAMILY";
        public const string DataKeyIdentifierInvalid = "ENVELOPE_DATA_KEY_IDENTIFIER_INVALID";
        public const string CommitmentLength = "ENVELOPE_COMMITMENT_LENGTH";
        public const string CiphertextLength = "ENVELOPE_CIPHERTEXT_LENGTH";
        public const string CiphertextSize = "ENVELOPE_CIPHERTEXT_SIZE";
        public const string BatchInconsistent = "ENVELOPE_BATCH_INCONSISTENT";
        public const string DataKeyIdentifierReused = "DATA_KEY_IDENTIFIER_REUSED";
        public const string VersionNotEnabled = "ENVELOPE_VERSION_NOT_ENABLED";
        public const string Downgrade = "ENVELOPE_DOWNGRADE";
    }

    /// <summary>Abort reason of a Commit whose verified staged bytes hold any v2 row (E3-M2d commits no v2 row, <see cref="CommitViolation"/>).</summary>
    public const string NotEnabledAtCommitReason = "ENVELOPE_VERSION_NOT_ENABLED_AT_COMMIT";

    /// <summary>True for an envelope row of signed schema 2 (the S-4 tie guarantees envelope 2 wherever this is consulted).</summary>
    public static bool IsV2(RowCanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return EnvelopeRules.IsEnvelopeRow(document) && document.Header.SchemaVersion == SchemaVersionV2;
    }

    /// <summary>The v2 shape of one row (package §2.3). Null when valid; otherwise the reason code.</summary>
    public static string? Validate(RowCanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var field = document.Header.EntityKind == EnvelopeRules.FieldEntityKind;
        var wrappedColumn = field ? "wrapped_data_key_bytes" : "wrapped_data_key";
        var purpose = field ? EnvelopeRules.FieldValuePurposeToken : EnvelopeRules.DocumentPurposeToken;
        var version = ReadInt32(document.Column("encryption_envelope_version"));
        var algorithm = document.Column("encryption_algorithm_name")?.AsText();
        var wrapCode = ReadInt32(document.Column("key_wrap_algorithm_code"));
        var reference = document.Column("encryption_key_reference")?.AsText();
        var identifier = document.Column("data_key_identifier");
        var commitmentColumn = document.Column("data_key_commitment_bytes");
        var wrappedColumnValue = document.Column(wrappedColumn);

        // RowCanonicalColumn.AsBytes() reads a NULL column as empty bytes, so NULL is tested explicitly (S-9 refuses it first anyway).
        if (version is null || algorithm is null || wrapCode is null || reference is null || identifier is null || identifier.IsNull || identifier.Type != RowColumnType.Uuid
            || commitmentColumn is null || commitmentColumn.Type != RowColumnType.Bytes || wrappedColumnValue is null || wrappedColumnValue.Type != RowColumnType.Bytes)
        {
            return EnvelopeRules.Reasons.ColumnMissing;
        }

        var commitment = commitmentColumn.AsBytes()!.Value;
        var wrapped = wrappedColumnValue.AsBytes()!.Value;

        if (version != EnvelopeVersionV2)
        {
            return EnvelopeRules.Reasons.VersionUnsupported;
        }

        if (!EnvelopeRules.AllowedAlgorithms.Contains(algorithm))
        {
            return EnvelopeRules.Reasons.AlgorithmUnsupported;
        }

        if (!WrapAlgorithms.TryGetValue(wrapCode.Value, out var wrap))
        {
            return Reasons.WrapAlgorithmUnsupported;
        }

        if (ValidateReference(reference, document.Header.OrganizationId, purpose, wrap.Family) is { } referenceFailure)
        {
            return referenceFailure;
        }

        if (identifier.AsUuid() is not { } identifierValue || !IsDataKeyIdentifier(identifierValue))
        {
            return Reasons.DataKeyIdentifierInvalid;
        }

        if (commitment.Length != CommitmentLength)
        {
            return Reasons.CommitmentLength;
        }

        if (wrapped.Length != wrap.WrappedLength)
        {
            return EnvelopeRules.Reasons.WrappedKeyLength;
        }

        return field ? ValidateField(document) : ValidateDocument(document);
    }

    /// <summary>
    /// Every v2 row of one candidate (Prepare) or of the verified staged rows (Commit), re-derivable from the bytes alone: each
    /// row's shape; batch consistency over all v2 rows grouped by data-key id (package §2.4, applied to every v2 row so Commit can
    /// re-check it: carried rows of one batch are byte-consistent); no wrapped key shared between v1 and v2 rows or between two
    /// ids. Throws a 422 invalid-command error; nothing has been signed when it is thrown at Prepare. v1 rows are judged by
    /// <see cref="EnvelopeRules"/> only and are passed here only for the cross-version separation.
    /// </summary>
    public static void Enforce(IReadOnlyList<RowCanonicalDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var v1Wrapped = new HashSet<string>(StringComparer.Ordinal);
        var v2 = new List<RowCanonicalDocument>();
        foreach (var document in documents)
        {
            if (!EnvelopeRules.IsEnvelopeRow(document))
            {
                continue;
            }

            if (IsV2(document))
            {
                if (Validate(document) is { } reason)
                {
                    throw EnvelopeRules.Refused(reason);
                }

                v2.Add(document);
            }
            else
            {
                v1Wrapped.Add(WrappedOf(document));
            }
        }

        if (BatchViolation(v2, v1Wrapped) is { } batchReason)
        {
            throw EnvelopeRules.Refused(batchReason);
        }
    }

    /// <summary>
    /// Prepare only (needs the verified current revision; covered by CARV 2 at Commit). For every v2 candidate row: Carried when the
    /// current accepted head (D-7: never an older revision) holds the same item as a schema-2 row and every signed column is
    /// byte-identical except the operation binding; otherwise New. DK-3 (D-10): a New row's data-key id must not equal the id of a
    /// Carried row or of any row of the current snapshot. Every New v2 row is then refused (<see cref="Reasons.VersionNotEnabled"/>,
    /// D-12: no new v2 is accepted in E3-M2d). The downgrade guard (D-6): an item that is a v2 envelope row in the current head may
    /// not become a v1 envelope row. No v1 candidate row may reuse a wrapped key of a v2 row of the current head.
    /// </summary>
    public static void EnforceAgainstCurrent(IReadOnlyList<(Guid ItemId, RowCanonicalDocument Document)> candidate, IReadOnlyList<(Guid ItemId, RowCanonicalDocument Document)> current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(current);
        var currentByItem = new Dictionary<(string Kind, Guid ItemId), RowCanonicalDocument>();
        var currentV2Identifiers = new HashSet<Guid>();
        var currentV2Wrapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (itemId, document) in current)
        {
            if (!EnvelopeRules.IsEnvelopeRow(document))
            {
                continue;
            }

            currentByItem[(document.Header.EntityKind, itemId)] = document;
            if (IsV2(document))
            {
                if (IdentifierOf(document) is { } id)
                {
                    currentV2Identifiers.Add(id);
                }

                currentV2Wrapped.Add(WrappedOf(document));
            }
        }

        var carriedIdentifiers = new HashSet<Guid>();
        var newRows = new List<RowCanonicalDocument>();
        foreach (var (itemId, document) in candidate)
        {
            if (!EnvelopeRules.IsEnvelopeRow(document))
            {
                continue;
            }

            currentByItem.TryGetValue((document.Header.EntityKind, itemId), out var existing);
            if (!IsV2(document))
            {
                if (existing is not null && IsV2(existing))
                {
                    throw EnvelopeRules.Refused(Reasons.Downgrade);
                }

                if (currentV2Wrapped.Contains(WrappedOf(document)))
                {
                    throw EnvelopeRules.Refused(EnvelopeRules.Reasons.WrappedKeyReused);
                }

                continue;
            }

            if (existing is not null && IsV2(existing) && EnvelopeRules.IsByteIdenticalCarry(existing, document))
            {
                if (IdentifierOf(document) is { } carried)
                {
                    carriedIdentifiers.Add(carried);
                }

                continue;
            }

            newRows.Add(document);
        }

        foreach (var document in newRows)
        {
            if (IdentifierOf(document) is { } id && (carriedIdentifiers.Contains(id) || currentV2Identifiers.Contains(id)))
            {
                throw EnvelopeRules.Refused(Reasons.DataKeyIdentifierReused);
            }
        }

        if (newRows.Count > 0)
        {
            throw EnvelopeRules.Refused(Reasons.VersionNotEnabled);
        }
    }

    /// <summary>
    /// Commit, E3-M2d (dark): no v2 row is committed by this build, carried or new. Commit holds only digests of the current revision,
    /// so it cannot re-derive the carried/new decision; refusing every v2 row keeps the accepted E3-M2b-2 property that schema 2 /
    /// envelope 2 is never accepted at Commit (S-4 gate S4-T1/T2), even for an operation recorded outside this build's Prepare with a
    /// version-2 stamp. Carried rows are still classified and validated at Prepare, and the v2 shape and batch rules still run on the
    /// staged bytes first. Committing carried v2 rows is left to the activation increment (E3-M2e, with the data-key ledger).
    /// Null when no staged row is v2; otherwise <see cref="Reasons.VersionNotEnabled"/>.
    /// </summary>
    public static string? CommitViolation(IEnumerable<RowCanonicalDocument> staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        return staged.Any(IsV2) ? Reasons.VersionNotEnabled : null;
    }

    /// <summary>
    /// The protection census for one record revision (accepted bytes): v1 rows exactly as before (the E3-M2a rules and one value per
    /// v1 wrapped key) plus, when v2 rows are present, the v2 shape, batch consistency and the v1/v2 wrapped-key separation.
    /// </summary>
    public sealed class CensusState
    {
        private readonly HashSet<string> v1Wrapped = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid?> v2WrappedOwner = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, BatchSignature> batches = [];

        /// <summary>True when the accepted row is as every write requires; false is an envelope issue (blocks completion).</summary>
        public bool Accepts(RowCanonicalDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            if (!IsV2(document))
            {
                // Unchanged E3-M2a census rule for v1 rows, plus the cross-version separation (only reachable when v2 rows exist).
                var wrapped = EnvelopeRules.WrappedKeyOf(document);
                return EnvelopeRules.Validate(document) is null && v1Wrapped.Add(wrapped) && !v2WrappedOwner.ContainsKey(wrapped);
            }

            if (Validate(document) is not null || IdentifierOf(document) is not { } id)
            {
                return false;
            }

            var key = WrappedOf(document);
            if (v1Wrapped.Contains(key) || (v2WrappedOwner.TryGetValue(key, out var owner) && owner != id))
            {
                return false;
            }

            v2WrappedOwner[key] = id;
            var signature = BatchSignature.Of(document);
            if (batches.TryGetValue(id, out var batch))
            {
                return batch.Accepts(signature);
            }

            batches[id] = signature;
            return true;
        }
    }

    private static string? BatchViolation(List<RowCanonicalDocument> v2, HashSet<string> v1Wrapped)
    {
        var batches = new Dictionary<Guid, BatchSignature>();
        var wrappedOwner = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var document in v2)
        {
            var id = IdentifierOf(document)!.Value; // shape validated: non-NULL Uuid
            var wrapped = WrappedOf(document);
            if (v1Wrapped.Contains(wrapped))
            {
                return EnvelopeRules.Reasons.WrappedKeyReused;
            }

            if (wrappedOwner.TryGetValue(wrapped, out var owner) && owner != id)
            {
                return EnvelopeRules.Reasons.WrappedKeyReused;
            }

            wrappedOwner[wrapped] = id;
            var signature = BatchSignature.Of(document);
            if (batches.TryGetValue(id, out var batch))
            {
                if (!batch.Accepts(signature))
                {
                    return signature.NonceKey is not null && batch.HasNonce(signature.NonceKey) ? EnvelopeRules.Reasons.NonceReused : Reasons.BatchInconsistent;
                }
            }
            else
            {
                batches[id] = signature;
            }
        }

        return null;
    }

    /// <summary>The batch-defining columns of one v2 row, plus its item and nonce for the distinctness rules.</summary>
    private sealed class BatchSignature
    {
        private readonly HashSet<string> items = new(StringComparer.Ordinal);
        private readonly HashSet<string> nonces = new(StringComparer.Ordinal);

        private BatchSignature(string kind, string commitment, int wrapCode, string wrapped, string reference, string item, string? nonce)
        {
            Kind = kind;
            Commitment = commitment;
            WrapCode = wrapCode;
            Wrapped = wrapped;
            Reference = reference;
            Item = item;
            NonceKey = nonce;
            items.Add(item);
            if (nonce is not null)
            {
                nonces.Add(nonce);
            }
        }

        public string Kind { get; }

        public string Commitment { get; }

        public int WrapCode { get; }

        public string Wrapped { get; }

        public string Reference { get; }

        public string Item { get; }

        public string? NonceKey { get; }

        public bool HasNonce(string nonce) => nonces.Contains(nonce);

        public static BatchSignature Of(RowCanonicalDocument document)
        {
            var field = document.Header.EntityKind == EnvelopeRules.FieldEntityKind;
            return new BatchSignature(
                document.Header.EntityKind,
                Hex(document.Column("data_key_commitment_bytes")),
                ReadInt32(document.Column("key_wrap_algorithm_code")) ?? -1,
                WrappedOf(document),
                document.Column("encryption_key_reference")?.AsText() ?? string.Empty,
                field ? Hex(document.Column("field_value_item_id")) : Hex(document.Column("file_attachment_id")),
                field ? Hex(document.Column("encryption_nonce_bytes")) : null);
        }

        /// <summary>A further row of this batch: same kind (a document batch is exactly one row), identical batch columns, a distinct item and nonce.</summary>
        public bool Accepts(BatchSignature other)
        {
            if (!string.Equals(Kind, other.Kind, StringComparison.Ordinal) || !string.Equals(Kind, EnvelopeRules.FieldEntityKind, StringComparison.Ordinal)
                || !string.Equals(Commitment, other.Commitment, StringComparison.Ordinal) || WrapCode != other.WrapCode
                || !string.Equals(Wrapped, other.Wrapped, StringComparison.Ordinal) || !string.Equals(Reference, other.Reference, StringComparison.Ordinal))
            {
                return false;
            }

            if (!items.Add(other.Item) || other.NonceKey is null || !nonces.Add(other.NonceKey))
            {
                return false;
            }

            return true;
        }
    }

    private static string? ValidateField(RowCanonicalDocument document)
    {
        var nonce = document.Column("encryption_nonce_bytes")?.AsBytes();
        var tag = document.Column("encryption_auth_tag_bytes")?.AsBytes();
        var ciphertext = document.Column("encrypted_value_bytes")?.AsBytes();
        var logicalType = document.Column("encrypted_logical_type")?.AsText();
        if (nonce is null || tag is null || ciphertext is null || logicalType is null)
        {
            return EnvelopeRules.Reasons.ColumnMissing;
        }

        if (nonce.Value.Length != EnvelopeRules.FieldNonceLength)
        {
            return EnvelopeRules.Reasons.NonceLength;
        }

        if (tag.Value.Length != EnvelopeRules.FieldTagLength)
        {
            return EnvelopeRules.Reasons.TagLength;
        }

        // 0 bytes is legitimate in v2 (AES-GCM of the empty string; the contract allows it); above the codec bound is not (D-8).
        if (ciphertext.Value.Length > MaximumFieldCiphertextBytes)
        {
            return Reasons.CiphertextLength;
        }

        return EnvelopeRules.LogicalTypes.Contains(logicalType) ? null : EnvelopeRules.Reasons.LogicalType;
    }

    private static string? ValidateDocument(RowCanonicalDocument document)
    {
        var prefix = document.Column("encryption_nonce_prefix")?.AsBytes();
        var segment = ReadInt32(document.Column("encryption_segment_size_bytes"));
        var plaintextSize = ReadInt64(document.Column("plaintext_size_bytes"));
        var ciphertextSize = ReadInt64(document.Column("ciphertext_size_bytes"));
        if (prefix is null || segment is null || plaintextSize is null || ciphertextSize is null)
        {
            return EnvelopeRules.Reasons.ColumnMissing;
        }

        if (prefix.Value.Length != EnvelopeRules.DocumentNoncePrefixLength)
        {
            return EnvelopeRules.Reasons.NoncePrefixLength;
        }

        if (segment != EnvelopeRules.DocumentSegmentSize)
        {
            return EnvelopeRules.Reasons.SegmentSize;
        }

        return ExpectedDocumentCiphertextSize(plaintextSize.Value) == ciphertextSize.Value ? null : Reasons.CiphertextSize;
    }

    /// <summary>
    /// The v2 document ciphertext size for <paramref name="plaintextSize"/> bytes: plaintext + 20 × max(1, ⌈plaintext / 1 MiB⌉)
    /// (one segment for an empty document; pinned against the E3-M1 vectors D0–D4). Null for a negative or overflowing size.
    /// </summary>
    public static long? ExpectedDocumentCiphertextSize(long plaintextSize)
    {
        if (plaintextSize < 0)
        {
            return null;
        }

        var segments = Math.Max(1L, (plaintextSize / EnvelopeRules.DocumentSegmentSize) + (plaintextSize % EnvelopeRules.DocumentSegmentSize == 0 ? 0 : 1));
        try
        {
            return checked(plaintextSize + (segments * DocumentSegmentOverheadBytes));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The canonical v2 reference <c>{family}:{organization}:{purpose}:{version}</c>: 1–128 characters of [A-Za-z0-9._:/-], a known
    /// family equal to the wrap code's family, the lowercase "D" organization equal to the row's, the purpose of the row's kind, a
    /// decimal version 1..2147483647 without sign, padding or leading zeros. The legacy v1 literal is not a v2 reference.
    /// </summary>
    private static string? ValidateReference(string reference, Guid organizationId, string purposeToken, string wrapFamily)
    {
        if (reference.Length is 0 or > 128 || reference.AsSpan().ContainsAnyExcept(ReferenceAlphabet))
        {
            return EnvelopeRules.Reasons.KeyReferenceInvalid;
        }

        var parts = reference.Split(':');
        if (parts.Length != 4 || !EnvelopeRules.ScopedReferenceFamilies.Contains(parts[0])
            || !Guid.TryParseExact(parts[1], "D", out var referenceOrganization) || parts[1] != referenceOrganization.ToString("D")
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1 || parts[3] != version.ToString(CultureInfo.InvariantCulture))
        {
            return EnvelopeRules.Reasons.KeyReferenceInvalid;
        }

        if (!string.Equals(parts[0], wrapFamily, StringComparison.Ordinal))
        {
            return Reasons.KeyReferenceFamily;
        }

        if (referenceOrganization != organizationId)
        {
            return EnvelopeRules.Reasons.KeyReferenceOrganization;
        }

        return string.Equals(parts[2], purposeToken, StringComparison.Ordinal) ? null : EnvelopeRules.Reasons.KeyReferencePurpose;
    }

    private static readonly System.Buffers.SearchValues<char> ReferenceAlphabet =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._:/-");

    /// <summary>A UUID with version nibble 7 and the RFC 9562 variant (10xx), not all-zero.</summary>
    public static bool IsDataKeyIdentifier(Guid identifier)
    {
        if (identifier == Guid.Empty)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        identifier.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes[6] >> 4 == 7 && (bytes[8] & 0xC0) == 0x80;
    }

    private static Guid? IdentifierOf(RowCanonicalDocument document) =>
        document.Column("data_key_identifier") is { IsNull: false, Type: RowColumnType.Uuid } column ? column.AsUuid() : null;

    /// <summary>The wrapped data key as raw-byte hex (the same identity as <see cref="EnvelopeRules.WrappedKeyOf"/> for field rows), for both kinds.</summary>
    private static string WrappedOf(RowCanonicalDocument document) =>
        Convert.ToHexStringLower((document.Column(document.Header.EntityKind == EnvelopeRules.FieldEntityKind ? "wrapped_data_key_bytes" : "wrapped_data_key")?.AsBytes() ?? ReadOnlyMemory<byte>.Empty).Span);

    private static string Hex(RowCanonicalColumn? column) => column is null || column.IsNull ? string.Empty : Convert.ToHexStringLower(column.FramedValue.Span);

    private static int? ReadInt32(RowCanonicalColumn? column)
    {
        if (column is null || column.Type != RowColumnType.Int32 || column.FramedValue.Length != 5)
        {
            return null;
        }

        return BinaryPrimitives.ReadInt32BigEndian(column.FramedValue.Span[1..]);
    }

    private static long? ReadInt64(RowCanonicalColumn? column)
    {
        if (column is null || column.Type != RowColumnType.Int64 || column.FramedValue.Length != 9)
        {
            return null;
        }

        return BinaryPrimitives.ReadInt64BigEndian(column.FramedValue.Span[1..]);
    }
}
