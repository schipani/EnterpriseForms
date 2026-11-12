using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthorizationAuthority.Infrastructure.Persistence.Configurations;

/// <summary>
/// IX-1: schema <c>authority_search</c> (design R1 §B.2, revision 2 §4). Organization leads every key and index. Postings and
/// anomalies reference their entry by (organization, record, operation), so an orphan or a stale-operation row is impossible
/// by constraint, not only detected by the sweep. RLS, the INV-R trigger and grants are applied by the migration and
/// <see cref="AuthorityRuntimeGrants"/>.
/// </summary>
public static class SearchIndexSchema
{
    public const string Name = "authority_search";

    public static readonly string[] DerivedTables =
    [
        "search_record_entries", "search_string_postings", "search_integer_postings", "search_decimal_postings", "search_date_postings",
        "search_date_time_postings", "search_boolean_postings", "search_option_postings", "search_record_field_anomalies",
    ];

    public static readonly string[] StateTables = ["search_projection_states", "search_field_coverages", "search_form_states"];

    /// <summary>IX-3: result snapshots. The runtime inserts and deletes them (never updates); no values are stored.</summary>
    public static readonly string[] ResultTables = ["search_result_snapshots", "search_result_snapshot_entries"];

    public static readonly string[] PostingTables = DerivedTables.Where(t => t.EndsWith("_postings", StringComparison.Ordinal)).ToArray();
}

public sealed class SearchProjectionStateConfiguration : IEntityTypeConfiguration<SearchProjectionState>
{
    public void Configure(EntityTypeBuilder<SearchProjectionState> builder)
    {
        builder.MapSnakeCase("search_projection_states");
        builder.ToTable("search_projection_states", SearchIndexSchema.Name);
        builder.HasKey(e => e.SearchProjectionStateId);
        builder.Property(e => e.ProjectionStatusName).HasMaxLength(30);
        builder.Property(e => e.ProjectionStatusReasonCode).HasMaxLength(64);
        builder.Property(e => e.SuspendedScopeNames).HasMaxLength(40);
        builder.HasIndex(e => e.OrganizationId).IsUnique().HasDatabaseName("uq_search_projection_state_org");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_search_projection_state_org").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_search_projection_status", "projection_status_name IN ('Active','ReconciliationRequired','Quarantined','Rebuilding')");
            t.HasCheckConstraint("ck_search_projection_reconciliation", "(projection_status_name = 'ReconciliationRequired') = (reconciliation_from_revision IS NOT NULL)");
            t.HasCheckConstraint("ck_search_projection_suspension", "verification_suspended = (suspended_scope_names IS NOT NULL)");
            t.HasCheckConstraint("ck_search_projection_deep_exclusions", "deep_deadline_excluded_entry_count >= 0");
            t.HasCheckConstraint("ck_search_projection_revision", "projection_content_integrity_revision >= 0 AND projection_schema_version >= 1");
        });
    }
}

public sealed class SearchFieldCoverageConfiguration : IEntityTypeConfiguration<SearchFieldCoverage>
{
    public void Configure(EntityTypeBuilder<SearchFieldCoverage> builder)
    {
        builder.MapSnakeCase("search_field_coverages");
        builder.ToTable("search_field_coverages", SearchIndexSchema.Name);
        builder.HasKey(e => e.SearchFieldCoverageId);
        builder.Property(e => e.IndexedValueTypeName).HasMaxLength(20);
        builder.Property(e => e.CoverageStateName).HasMaxLength(20);
        builder.Property(e => e.CoveredFieldClassification).HasMaxLength(20);
        builder.Property(e => e.CoverageWithdrawalReasonCode).HasMaxLength(64);
        builder.HasIndex(e => e.FieldSearchApprovalId).IsUnique().HasDatabaseName("uq_search_field_coverage_approval");
        builder.HasIndex(e => new { e.OrganizationId, e.FormDefinitionId }).HasDatabaseName("ix_search_field_coverages_form");
        builder.HasIndex(e => e.CoverageAttestedJournalEntryId).HasDatabaseName("ix_search_field_coverages_attestation");
        builder.HasOne<FieldSearchApproval>().WithMany().HasForeignKey(e => e.FieldSearchApprovalId).HasConstraintName("fk_search_field_coverage_approval").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_search_field_coverage_org").OnDelete(DeleteBehavior.Restrict);
        // IX-2: Complete names the audit-stream attestation that set it (defence in depth; the Authority alone writes either).
        builder.HasOne<AuthorizationAuthority.Domain.Features.Journal.SecurityJournalEntry>().WithMany().HasForeignKey(e => e.CoverageAttestedJournalEntryId).HasConstraintName("fk_search_field_coverage_attestation").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_search_coverage_attestation", "coverage_state_name <> 'Complete' OR (coverage_attested_journal_entry_id IS NOT NULL AND coverage_completed_on IS NOT NULL)");
            t.HasCheckConstraint("ck_search_coverage_state", "coverage_state_name IN ('Building','Complete','Withdrawn')");
            t.HasCheckConstraint("ck_search_coverage_classification", "covered_field_classification IN ('Public','Internal')");
            t.HasCheckConstraint("ck_search_coverage_value_type", "indexed_value_type_name IN ('String','Integer','Decimal','Date','DateTime','Boolean','Option')");
            t.HasCheckConstraint("ck_search_coverage_withdrawal", "(coverage_state_name = 'Withdrawn') = (coverage_withdrawn_on IS NOT NULL)");
        });
    }
}

public sealed class SearchRecordEntryConfiguration : IEntityTypeConfiguration<SearchRecordEntry>
{
    public void Configure(EntityTypeBuilder<SearchRecordEntry> builder)
    {
        builder.MapSnakeCase("search_record_entries");
        builder.ToTable("search_record_entries", SearchIndexSchema.Name);
        builder.HasKey(e => e.SearchRecordEntryId);
        builder.Property(e => e.DerivedFieldSearchApprovalIds).HasColumnName("derived_field_search_approval_ids").HasColumnType("uuid[]");
        builder.Property(e => e.DerivedFieldItemCounts).HasColumnName("derived_field_item_counts").HasColumnType("integer[]");
        builder.Property(e => e.RecordStatusName).HasMaxLength(40);
        builder.Property(e => e.SealKeyName).HasMaxLength(100);
        builder.Property(e => e.LastSealAttemptOutcomeCode).HasMaxLength(64);
        builder.Property(e => e.LastDeepAttemptOutcomeCode).HasMaxLength(64);
        builder.Property(e => e.EntryDerivationVersion).HasDefaultValue(1);
        builder.HasAlternateKey(e => new { e.OrganizationId, e.FormRecordId, e.IndexedOperationId }).HasName("uq_search_record_entry_operation");
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId }).IsUnique().HasDatabaseName("uq_search_record_entry_record");
        builder.HasIndex(e => new { e.OrganizationId, e.FormDefinitionId, e.FormRecordId }).HasDatabaseName("ix_search_record_entries_form");
        builder.HasIndex(e => new { e.OrganizationId, e.LastSealVerifiedOn }).HasDatabaseName("ix_search_record_entries_seal_check");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_search_record_entry_org").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_search_entry_seal", "octet_length(entry_seal_signature) = 64 AND seal_key_version >= 1 AND seal_schema_version = 1");
            t.HasCheckConstraint("ck_search_entry_head", "octet_length(indexed_manifest_root) = 32 AND indexed_content_revision >= 1");
            t.HasCheckConstraint("ck_search_entry_counts", "indexed_posting_count >= 0 AND indexed_anomaly_count >= 0 AND cardinality(derived_field_search_approval_ids) >= 1");
            // IX-3 (D-IX3-1): version 2 entries carry the verified header bytes and the creator decoded from them.
            t.HasCheckConstraint("ck_search_entry_derivation", "entry_derivation_version IN (1, 2, 3) AND (entry_derivation_version = 1 OR (octet_length(header_source_canonical_bytes) > 0 AND created_by_business_user_id IS NOT NULL))");
            // C-141 (UF-25): version 3 entries carry one non-negative item count per derived approval; older entries carry none.
            t.HasCheckConstraint("ck_search_entry_field_item_counts",
                "(entry_derivation_version < 3 AND derived_field_item_counts IS NULL) OR (entry_derivation_version >= 3 AND derived_field_item_counts IS NOT NULL"
                + " AND cardinality(derived_field_item_counts) = cardinality(derived_field_search_approval_ids) AND array_position(derived_field_item_counts, NULL) IS NULL"
                + " AND 0 <= ALL (derived_field_item_counts))");
        });
    }
}

/// <summary>IX-3 (§17.3, §18.4): Authority-held result snapshots. Ids and positions only; no RLS (no values, D-IX1-5 precedent).</summary>
public sealed class SearchResultSnapshotConfiguration : IEntityTypeConfiguration<SearchResultSnapshot>
{
    public void Configure(EntityTypeBuilder<SearchResultSnapshot> builder)
    {
        builder.MapSnakeCase("search_result_snapshots");
        builder.ToTable("search_result_snapshots", SearchIndexSchema.Name);
        builder.HasKey(e => e.SearchResultSnapshotId);
        builder.Property(e => e.SearchEngineName).HasMaxLength(20);
        builder.Property(e => e.SearchTierName).HasMaxLength(10);
        builder.Property(e => e.CreatingInstanceName).HasMaxLength(100);
        builder.Property(e => e.PredicateFieldDefinitionIds).HasColumnName("predicate_field_definition_ids").HasColumnType("uuid[]");
        builder.Property(e => e.PredicateFieldSearchApprovalIds).HasColumnName("predicate_field_search_approval_ids").HasColumnType("uuid[]");
        builder.Property(e => e.PredicateOperatorNames).HasColumnName("predicate_operator_names").HasColumnType("text[]");
        builder.HasIndex(e => new { e.OrganizationId, e.SnapshotAuthorizationRevision }).HasDatabaseName("ix_search_result_snapshots_revision");
        builder.HasIndex(e => new { e.OrganizationId, e.FormDefinitionId }).HasDatabaseName("ix_search_result_snapshots_form");
        builder.HasIndex(e => new { e.AuthenticatedSessionId, e.SnapshotCreatedOn }).HasDatabaseName("ix_search_result_snapshots_session");
        builder.HasIndex(e => e.SnapshotExpiresOn).HasDatabaseName("ix_search_result_snapshots_expiry");
        builder.HasIndex(e => e.CreatingInstanceName).HasDatabaseName("ix_search_result_snapshots_instance");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_search_result_snapshot_org").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_search_result_snapshot_engine", "search_engine_name = 'Indexed' AND search_tier_name = 'Tier1'");
            t.HasCheckConstraint("ck_search_result_snapshot_digest", "octet_length(query_digest) = 32");
            t.HasCheckConstraint("ck_search_result_snapshot_counts", "result_entry_count BETWEEN 0 AND 10000 AND snapshot_expires_on > snapshot_created_on");
            t.HasCheckConstraint("ck_search_result_snapshot_predicates", "cardinality(predicate_field_definition_ids) BETWEEN 1 AND 5 AND cardinality(predicate_field_search_approval_ids) = cardinality(predicate_field_definition_ids) AND cardinality(predicate_operator_names) = cardinality(predicate_field_definition_ids)");
        });
    }
}

public sealed class SearchResultSnapshotEntryConfiguration : IEntityTypeConfiguration<SearchResultSnapshotEntry>
{
    public void Configure(EntityTypeBuilder<SearchResultSnapshotEntry> builder)
    {
        builder.MapSnakeCase("search_result_snapshot_entries");
        builder.ToTable("search_result_snapshot_entries", SearchIndexSchema.Name);
        builder.HasKey(e => e.SearchResultSnapshotEntryId);
        builder.HasIndex(e => new { e.SearchResultSnapshotId, e.ResultEntryPosition }).IsUnique().HasDatabaseName("uq_search_result_snapshot_entry_position");
        builder.HasOne<SearchResultSnapshot>().WithMany().HasForeignKey(e => e.SearchResultSnapshotId).HasConstraintName("fk_search_result_snapshot_entry_snapshot").OnDelete(DeleteBehavior.Cascade);
        builder.ToTable(t => t.HasCheckConstraint("ck_search_result_snapshot_entry_position", "result_entry_position >= 0"));
    }
}

public sealed class SearchRecordFieldAnomalyConfiguration : IEntityTypeConfiguration<SearchRecordFieldAnomaly>
{
    public void Configure(EntityTypeBuilder<SearchRecordFieldAnomaly> builder)
    {
        builder.MapSnakeCase("search_record_field_anomalies");
        builder.ToTable("search_record_field_anomalies", SearchIndexSchema.Name);
        builder.HasKey(e => e.SearchRecordFieldAnomalyId);
        builder.Property(e => e.AnomalyKindName).HasMaxLength(40);
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId, e.FieldDefinitionId, e.AnomalyKindName }).IsUnique().HasDatabaseName("uq_search_record_field_anomaly");
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId, e.IndexedOperationId }).HasDatabaseName("ix_search_record_field_anomalies_entry");
        builder.HasOne<SearchRecordEntry>().WithMany().HasForeignKey(e => new { e.OrganizationId, e.FormRecordId, e.IndexedOperationId })
            .HasPrincipalKey(e => new { e.OrganizationId, e.FormRecordId, e.IndexedOperationId }).HasConstraintName("fk_search_record_field_anomaly_entry").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t => t.HasCheckConstraint("ck_search_anomaly_kind", "anomaly_kind_name IN ('EncryptedValuePresent')"));
    }
}

public sealed class SearchFormStateConfiguration : IEntityTypeConfiguration<SearchFormState>
{
    public void Configure(EntityTypeBuilder<SearchFormState> builder)
    {
        builder.MapSnakeCase("search_form_states");
        builder.ToTable("search_form_states", SearchIndexSchema.Name);
        builder.HasKey(e => e.SearchFormStateId);
        builder.HasIndex(e => new { e.OrganizationId, e.FormDefinitionId }).IsUnique().HasDatabaseName("uq_search_form_state_form");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_search_form_state_org").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t => t.HasCheckConstraint("ck_search_form_state_revision", "form_content_revision >= 0"));
    }
}

/// <summary>Common mapping of the typed posting tables.</summary>
public abstract class SearchPostingConfiguration<TPosting> : IEntityTypeConfiguration<TPosting>
    where TPosting : SearchPostingBase
{
    protected abstract string TableName { get; }

    protected abstract string ShortName { get; }

    public void Configure(EntityTypeBuilder<TPosting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.MapSnakeCase(TableName);
        builder.ToTable(TableName, SearchIndexSchema.Name);
        builder.Property(e => e.SourceEntityKind).HasMaxLength(40);
        // C-141 (UF-25): the record probe index ends with the item id, so the share-only readability step probes one posting per
        // candidate item (organization, record, field, item) and never visits a posting the caller cannot read. Still record-keyed
        // (PD-9); the candidate statement's (organization, record, field) probe is its prefix. No value column.
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId, e.FieldDefinitionId, e.ManifestItemId }).HasDatabaseName($"ix_search_{ShortName}_postings_record");
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId, e.IndexedOperationId }).HasDatabaseName($"ix_search_{ShortName}_postings_entry");
        builder.HasOne<SearchRecordEntry>().WithMany().HasForeignKey(e => new { e.OrganizationId, e.FormRecordId, e.IndexedOperationId })
            .HasPrincipalKey(e => new { e.OrganizationId, e.FormRecordId, e.IndexedOperationId }).HasConstraintName($"fk_search_{ShortName}_posting_entry").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t => t.HasCheckConstraint($"ck_search_{ShortName}_posting_source", "octet_length(manifest_item_key) = 17 AND octet_length(source_canonical_bytes) > 0"));
        ConfigureValue(builder);
    }

    /// <summary>
    /// The value column and its constraints only. C-135 (PD-9): posting tables carry **no index on a value column**. The only
    /// indexes are the record probe index and the entry index, both keyed (organization_id, form_record_id, …), so every posting
    /// read is per record. A value-keyed index (the IX-1 B-tree value indexes and the trigram GIN index, dropped by migration
    /// AuthoritySearchIndexIx4DropValueIndexes) would let the planner serve the per-record probe by a PostgreSQL 18 skip scan whose
    /// cost depends on the organization-wide value distribution (design §8.2, §22.4). Adding one needs a separate approval.
    /// </summary>
    protected abstract void ConfigureValue(EntityTypeBuilder<TPosting> builder);
}

public sealed class SearchStringPostingConfiguration : SearchPostingConfiguration<SearchStringPosting>
{
    protected override string TableName => "search_string_postings";

    protected override string ShortName => "string";

    protected override void ConfigureValue(EntityTypeBuilder<SearchStringPosting> builder)
    {
        builder.HasKey(e => e.SearchStringPostingId);
        builder.Property(e => e.NormalizedStringValue).UseCollation("C");
    }
}

public sealed class SearchIntegerPostingConfiguration : SearchPostingConfiguration<SearchIntegerPosting>
{
    protected override string TableName => "search_integer_postings";

    protected override string ShortName => "integer";

    protected override void ConfigureValue(EntityTypeBuilder<SearchIntegerPosting> builder)
    {
        builder.HasKey(e => e.SearchIntegerPostingId);
    }
}

public sealed class SearchDecimalPostingConfiguration : SearchPostingConfiguration<SearchDecimalPosting>
{
    protected override string TableName => "search_decimal_postings";

    protected override string ShortName => "decimal";

    protected override void ConfigureValue(EntityTypeBuilder<SearchDecimalPosting> builder)
    {
        builder.HasKey(e => e.SearchDecimalPostingId);
        builder.Property(e => e.DecimalValue).HasColumnType("numeric");
    }
}

public sealed class SearchDatePostingConfiguration : SearchPostingConfiguration<SearchDatePosting>
{
    protected override string TableName => "search_date_postings";

    protected override string ShortName => "date";

    protected override void ConfigureValue(EntityTypeBuilder<SearchDatePosting> builder)
    {
        builder.HasKey(e => e.SearchDatePostingId);
        builder.Property(e => e.DateValue).HasColumnName("date_value").HasColumnType("date");
    }
}

public sealed class SearchDateTimePostingConfiguration : SearchPostingConfiguration<SearchDateTimePosting>
{
    protected override string TableName => "search_date_time_postings";

    protected override string ShortName => "date_time";

    protected override void ConfigureValue(EntityTypeBuilder<SearchDateTimePosting> builder)
    {
        builder.HasKey(e => e.SearchDateTimePostingId);
    }
}

public sealed class SearchBooleanPostingConfiguration : SearchPostingConfiguration<SearchBooleanPosting>
{
    protected override string TableName => "search_boolean_postings";

    protected override string ShortName => "boolean";

    protected override void ConfigureValue(EntityTypeBuilder<SearchBooleanPosting> builder)
    {
        builder.HasKey(e => e.SearchBooleanPostingId);
    }
}

public sealed class SearchOptionPostingConfiguration : SearchPostingConfiguration<SearchOptionPosting>
{
    protected override string TableName => "search_option_postings";

    protected override string ShortName => "option";

    protected override void ConfigureValue(EntityTypeBuilder<SearchOptionPosting> builder)
    {
        builder.HasKey(e => e.SearchOptionPostingId);
    }
}
