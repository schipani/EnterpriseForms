namespace EnterpriseForms.Infrastructure.Persistence;

/// <summary>
/// E3-M2b-2 (docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §9.3 as corrected by owner decision D-1, §23): the BusinessDb
/// backstops for the two envelope tables. They are defence in depth, not authority: the Authority rules and RowHmac are the
/// trust boundary. Every comparison in the envelope-2 disjunct is paired with an explicit <c>IS NOT NULL</c>, because
/// PostgreSQL accepts a CHECK whose expression evaluates to NULL (the CARV D-1 defect class): without it a v2 row with a NULL
/// wrap code or commitment would be accepted.
/// </summary>
public static class EnvelopeMetadataConstraints
{
    /// <summary>Closed set of envelope versions (replaces <c>ck_field_encrypted_values_envelope_positive</c>, which accepted any positive version).</summary>
    public const string EnvelopeVersionSql = "encryption_envelope_version IN (1, 2)";

    /// <summary>S-11: the signed schema version equals the envelope version (schema 1 = envelope 1, schema 2 = envelope 2). Both columns are NOT NULL.</summary>
    public const string SchemaEnvelopeTieSql = "row_signature_schema_version = encryption_envelope_version";

    /// <summary>Envelope 1 carries no v2 metadata; envelope 2 carries all of it (non-NULL identifier, 32-byte commitment, wrap code 1 or 2).</summary>
    public const string MetadataSql =
        "(encryption_envelope_version = 1 AND data_key_identifier IS NULL AND data_key_commitment_bytes IS NULL AND key_wrap_algorithm_code IS NULL)"
        + " OR (encryption_envelope_version = 2 AND data_key_identifier IS NOT NULL"
        + " AND data_key_commitment_bytes IS NOT NULL AND octet_length(data_key_commitment_bytes) = 32"
        + " AND key_wrap_algorithm_code IS NOT NULL AND key_wrap_algorithm_code IN (1, 2))";

    public const string FieldEnvelopeVersionName = "ck_field_encrypted_values_envelope_version";
    public const string FieldSchemaEnvelopeTieName = "ck_field_encrypted_values_schema_envelope_tie";
    public const string FieldMetadataName = "ck_field_encrypted_values_envelope_metadata";
    public const string AttachmentEnvelopeVersionName = "ck_file_attachment_versions_envelope_version";
    public const string AttachmentSchemaEnvelopeTieName = "ck_file_attachment_versions_schema_envelope_tie";
    public const string AttachmentMetadataName = "ck_file_attachment_versions_envelope_metadata";

    /// <summary>The pre-E3-M2b-2 constraint the migration drops (and the guarded Down restores).</summary>
    public const string LegacyFieldEnvelopePositiveName = "ck_field_encrypted_values_envelope_positive";
    public const string LegacyFieldEnvelopePositiveSql = "encryption_envelope_version >= 1";
}
