using System.Buffers.Binary;
using System.Globalization;
using AuthorizationAuthority.Application.Common;
using EnterpriseForms.Integrity.Canonical;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// E3-M2a: the single definition of the Envelope v1 shape the Authority accepts, applied to every encrypted row of every
/// content write (owner, share editor, protection migration) before anything is signed, again on the verified staged bytes at
/// commit, and by the protection census. Decided from canonical bytes alone: the Authority holds no encryption key, so it
/// checks metadata (codes, lengths, key reference, organization and purpose agreement) and never AES-GCM authentication, which
/// only the key holder can check. Envelope v2 is not accepted (E3-M2 design §2; not authorized). Error texts carry reason
/// codes only, never identifiers or bytes.
/// </summary>
public static class EnvelopeRules
{
    public const string FieldEntityKind = "field_encrypted_values";
    public const string DocumentEntityKind = "file_attachment_versions";

    /// <summary>The only AEAD the business encrypts with (field values and documents).</summary>
    public const string ApprovedAlgorithm = "AES-256-GCM";

    public const int EnvelopeVersionV1 = 1;
    public const int FieldNonceLength = 12;
    public const int FieldTagLength = 16;
    public const int DocumentNoncePrefixLength = 8;
    public const int DocumentSegmentSize = 1_048_576;

    /// <summary>Every v1 wrap the business has produced: a 32-byte data key sealed with AES-256-GCM (12-byte nonce ‖ 32 ‖ 16-byte tag), by the Stage B legacy provider and by both E-1/E-2 providers.</summary>
    public const int WrappedDataKeyLength = 60;

    /// <summary>The pre-E-1 development key reference (decrypt-only on the business side; every row stored before E-1 names it).</summary>
    public const string LegacyKeyReference = "local-dev-kek-v1";

    public const string FieldValuePurposeToken = "field-value";
    public const string DocumentPurposeToken = "document";

    /// <summary>Envelope versions accepted for new and carried rows. v2 is deliberately absent (E3-M2a).</summary>
    public static readonly IReadOnlySet<int> AllowedEnvelopeVersions = new HashSet<int> { EnvelopeVersionV1 };

    public static readonly IReadOnlySet<string> AllowedAlgorithms = new HashSet<string>(StringComparer.Ordinal) { ApprovedAlgorithm };

    /// <summary>Scoped key-reference families of the E-1/E-2 providers (<c>{family}:{organization}:{purpose}:{version}</c>).</summary>
    public static readonly IReadOnlySet<string> ScopedReferenceFamilies = new HashSet<string>(StringComparer.Ordinal) { "localdev", "vault-transit" };

    public static readonly IReadOnlySet<string> LogicalTypes = new HashSet<string>(StringComparer.Ordinal) { "String", "Integer", "Decimal", "Date", "DateTime", "Boolean", "Guid", "Option" };

    /// <summary>Reason codes (stable, identifier-free).</summary>
    public static class Reasons
    {
        public const string SchemaUnsupported = "ENVELOPE_SCHEMA_UNSUPPORTED";
        public const string ColumnMissing = "ENVELOPE_COLUMN_MISSING";
        public const string VersionUnsupported = "ENVELOPE_VERSION_UNSUPPORTED";
        public const string AlgorithmUnsupported = "ENVELOPE_ALGORITHM_UNSUPPORTED";
        public const string NonceLength = "ENVELOPE_NONCE_LENGTH";
        public const string TagLength = "ENVELOPE_TAG_LENGTH";
        public const string CiphertextEmpty = "ENVELOPE_CIPHERTEXT_EMPTY";
        public const string WrappedKeyLength = "ENVELOPE_WRAPPED_KEY_LENGTH";
        public const string KeyReferenceInvalid = "ENVELOPE_KEY_REFERENCE_INVALID";
        public const string KeyReferenceOrganization = "ENVELOPE_KEY_REFERENCE_ORGANIZATION";
        public const string KeyReferencePurpose = "ENVELOPE_KEY_REFERENCE_PURPOSE";
        public const string LogicalType = "ENVELOPE_LOGICAL_TYPE";
        public const string NoncePrefixLength = "ENVELOPE_NONCE_PREFIX_LENGTH";
        public const string SegmentSize = "ENVELOPE_SEGMENT_SIZE";
        /// <summary>A field row reuses the wrapped data key and the nonce of another row (the candidate's or the current revision's) without being its byte-identical carry.</summary>
        public const string NonceReused = "ENVELOPE_NONCE_REUSED";

        /// <summary>A field row reuses the wrapped data key of another row under a different nonce: a v1 data key seals exactly one value.</summary>
        public const string WrappedKeyReused = "ENVELOPE_WRAPPED_KEY_REUSED";
    }

    /// <summary>Whether the entity kind carries an encryption envelope (and so is subject to these rules).</summary>
    public static bool IsEnvelopeRow(RowCanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Header.EntityKind is FieldEntityKind or DocumentEntityKind;
    }

    /// <summary>
    /// The shape of one row. Null when the row is valid or carries no envelope; otherwise the reason code. The organization of
    /// a scoped key reference must equal the row's header organization (the tenant column is bound to it by identity binding).
    /// </summary>
    public static string? Validate(RowCanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Header.EntityKind switch
        {
            FieldEntityKind => ValidateField(document),
            DocumentEntityKind => ValidateDocument(document),
            _ => null,
        };
    }

    /// <summary>
    /// Every envelope row of one candidate (or of the verified staged rows at commit): each row's shape, and no wrapped data key
    /// shared by two field rows — a v1 data key seals exactly one value, so a shared wrapped key is data-key reuse
    /// (<see cref="Reasons.NonceReused"/> when the nonce is shared too, <see cref="Reasons.WrappedKeyReused"/> otherwise).
    /// Throws a 422 invalid-command error naming the reason; nothing has been signed when it is thrown at prepare.
    /// </summary>
    public static void Enforce(IEnumerable<RowCanonicalDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var nonceByWrappedKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (Validate(document) is { } reason)
            {
                throw Refused(reason);
            }

            if (document.Header.EntityKind != FieldEntityKind)
            {
                continue;
            }

            var wrapped = WrappedKeyOf(document);
            var nonce = NonceOf(document);
            if (nonceByWrappedKey.TryGetValue(wrapped, out var seenNonce))
            {
                throw Refused(string.Equals(seenNonce, nonce, StringComparison.Ordinal) ? Reasons.NonceReused : Reasons.WrappedKeyReused);
            }

            nonceByWrappedKey[wrapped] = nonce;
        }
    }

    /// <summary>
    /// E3-M2a remediation: every field row of a candidate against the verified current accepted revision of the record (the
    /// snapshot proven equal to the Authority's accepted head, never business rows). A candidate row whose wrapped data key
    /// already appears in the current revision is accepted only as the byte-identical carry of that same item: every signed
    /// column equal except the operation binding (the staged row's storage key and row revision live in the header and differ
    /// by design). Anything else — the key and nonce for changed ciphertext, the key for another item, or the key under a
    /// different nonce — is data-key reuse and is refused before anything is signed. <paramref name="current"/> is empty for a
    /// record without accepted content. Reuse of a key from an older, no longer current revision is not visible here (the
    /// Authority keeps only digests of history): a documented v1 limitation.
    /// </summary>
    public static void EnforceAgainstCurrent(IEnumerable<(Guid ItemId, RowCanonicalDocument Document)> candidate, IEnumerable<(Guid ItemId, RowCanonicalDocument Document)> current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(current);
        var currentByWrappedKey = new Dictionary<string, (Guid ItemId, RowCanonicalDocument Document)>(StringComparer.Ordinal);
        foreach (var row in current)
        {
            if (row.Document.Header.EntityKind == FieldEntityKind)
            {
                currentByWrappedKey.TryAdd(WrappedKeyOf(row.Document), row);
            }
        }

        foreach (var row in candidate)
        {
            if (row.Document.Header.EntityKind != FieldEntityKind || !currentByWrappedKey.TryGetValue(WrappedKeyOf(row.Document), out var existing))
            {
                continue; // a fresh data key (or no envelope)
            }

            if (existing.ItemId == row.ItemId && IsByteIdenticalCarry(existing.Document, row.Document))
            {
                continue;
            }

            throw Refused(string.Equals(NonceOf(existing.Document), NonceOf(row.Document), StringComparison.Ordinal) ? Reasons.NonceReused : Reasons.WrappedKeyReused);
        }
    }

    /// <summary>The wrapped data key of a field row (per-revision reuse identity for writes and the census).</summary>
    public static string WrappedKeyOf(RowCanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Convert.ToHexStringLower((document.Column("wrapped_data_key_bytes")?.AsBytes() ?? ReadOnlyMemory<byte>.Empty).Span);
    }

    private static string NonceOf(RowCanonicalDocument document) =>
        Convert.ToHexStringLower((document.Column("encryption_nonce_bytes")?.AsBytes() ?? ReadOnlyMemory<byte>.Empty).Span);

    /// <summary>Same entity kind, schema and column list, and every column byte-identical except the operation binding.</summary>
    private static bool IsByteIdenticalCarry(RowCanonicalDocument current, RowCanonicalDocument candidate)
    {
        if (current.Header.EntityKind != candidate.Header.EntityKind || current.Header.SchemaVersion != candidate.Header.SchemaVersion || current.Columns.Count != candidate.Columns.Count)
        {
            return false;
        }

        foreach (var column in current.Columns)
        {
            if (string.Equals(column.Name, ContentDeltaRules.OperationColumn, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.Column(column.Name) is not { } other || !column.FramedValueEquals(other))
            {
                return false;
            }
        }

        return true;
    }

    public static AuthorityException Refused(string reason) => AuthorityException.Invalid("ENVELOPE_INVALID: " + reason);

    private static string? ValidateField(RowCanonicalDocument document)
    {
        if (document.Header.SchemaVersion != 1)
        {
            return Reasons.SchemaUnsupported;
        }

        if (Common(document, "wrapped_data_key_bytes", FieldValuePurposeToken) is { } common)
        {
            return common;
        }

        var nonce = document.Column("encryption_nonce_bytes")?.AsBytes();
        var tag = document.Column("encryption_auth_tag_bytes")?.AsBytes();
        var ciphertext = document.Column("encrypted_value_bytes")?.AsBytes();
        var logicalType = document.Column("encrypted_logical_type")?.AsText();
        if (nonce is null || tag is null || ciphertext is null || logicalType is null)
        {
            return Reasons.ColumnMissing;
        }

        if (nonce.Value.Length != FieldNonceLength)
        {
            return Reasons.NonceLength;
        }

        if (tag.Value.Length != FieldTagLength)
        {
            return Reasons.TagLength;
        }

        // Unchanged existing rule (census): no write path stores an empty encrypted value — an empty value is no row (owner
        // writes) or refused (share edits) — and the assessed stored data contains none (E3-M2a compatibility assessment).
        if (ciphertext.Value.Length == 0)
        {
            return Reasons.CiphertextEmpty;
        }

        return LogicalTypes.Contains(logicalType) ? null : Reasons.LogicalType;
    }

    private static string? ValidateDocument(RowCanonicalDocument document)
    {
        if (document.Header.SchemaVersion != 1)
        {
            return Reasons.SchemaUnsupported;
        }

        if (Common(document, "wrapped_data_key", DocumentPurposeToken) is { } common)
        {
            return common;
        }

        var prefix = document.Column("encryption_nonce_prefix")?.AsBytes();
        var segment = ReadInt32(document.Column("encryption_segment_size_bytes"));
        if (prefix is null || segment is null)
        {
            return Reasons.ColumnMissing;
        }

        if (prefix.Value.Length != DocumentNoncePrefixLength)
        {
            return Reasons.NoncePrefixLength;
        }

        return segment == DocumentSegmentSize ? null : Reasons.SegmentSize;
    }

    /// <summary>Version, algorithm, wrapped key and key reference: identical for both envelope kinds.</summary>
    private static string? Common(RowCanonicalDocument document, string wrappedColumn, string purposeToken)
    {
        var version = ReadInt32(document.Column("encryption_envelope_version"));
        var algorithm = document.Column("encryption_algorithm_name")?.AsText();
        var wrapped = document.Column(wrappedColumn)?.AsBytes();
        var reference = document.Column("encryption_key_reference")?.AsText();
        if (version is null || algorithm is null || wrapped is null || reference is null)
        {
            return Reasons.ColumnMissing;
        }

        if (!AllowedEnvelopeVersions.Contains(version.Value))
        {
            return Reasons.VersionUnsupported;
        }

        if (!AllowedAlgorithms.Contains(algorithm))
        {
            return Reasons.AlgorithmUnsupported;
        }

        if (wrapped.Value.Length != WrappedDataKeyLength)
        {
            return Reasons.WrappedKeyLength;
        }

        return ValidateReference(reference, document.Header.OrganizationId, purposeToken);
    }

    /// <summary>
    /// The legacy literal, or the canonical scoped form <c>{family}:{organization}:{purpose}:{version}</c> exactly as the
    /// business writes it (KeyScopeEncoding.Reference): a known family, the lowercase "D" organization equal to the row's, the
    /// purpose of the row's kind, and a decimal version 1..2147483647 without sign, padding or leading zeros.
    /// </summary>
    private static string? ValidateReference(string reference, Guid organizationId, string purposeToken)
    {
        if (string.Equals(reference, LegacyKeyReference, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = reference.Split(':');
        if (parts.Length != 4 || !ScopedReferenceFamilies.Contains(parts[0])
            || !Guid.TryParseExact(parts[1], "D", out var referenceOrganization) || parts[1] != referenceOrganization.ToString("D")
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1 || parts[3] != version.ToString(CultureInfo.InvariantCulture))
        {
            return Reasons.KeyReferenceInvalid;
        }

        if (referenceOrganization != organizationId)
        {
            return Reasons.KeyReferenceOrganization;
        }

        return string.Equals(parts[2], purposeToken, StringComparison.Ordinal) ? null : Reasons.KeyReferencePurpose;
    }

    private static int? ReadInt32(RowCanonicalColumn? column)
    {
        if (column is null || column.Type != RowColumnType.Int32 || column.FramedValue.Length != 5)
        {
            return null;
        }

        return BinaryPrimitives.ReadInt32BigEndian(column.FramedValue.Span[1..]);
    }
}
