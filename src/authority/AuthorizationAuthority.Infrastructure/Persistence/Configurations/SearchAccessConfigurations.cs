using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SearchAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthorizationAuthority.Infrastructure.Persistence.Configurations;

/// <summary>
/// IX-5a (C-147): the Tier 2 access projection lives in its own schema so that the Tier 1 search reader (granted SELECT on
/// authority_search) can never read it. Granted to the Authority runtime only; forced row-level security on the bound organization.
/// </summary>
public static class SearchAccessSchema
{
    public const string Name = "authority_search_access";

    /// <summary>Derived rows: inserted and deleted, never updated.</summary>
    public static readonly string[] DerivedTables = ["search_access_edges", "search_access_record_grants", "search_access_field_item_grants", "search_access_value_postings"];

    /// <summary>Checkpoints: inserted and compare-and-set updated, never deleted.</summary>
    public static readonly string[] StateTables = ["search_access_checkpoints"];

    public static IEnumerable<string> AllTables => DerivedTables.Concat(StateTables);
}

public sealed class SearchAccessCheckpointConfiguration : IEntityTypeConfiguration<SearchAccessCheckpoint>
{
    public void Configure(EntityTypeBuilder<SearchAccessCheckpoint> builder)
    {
        builder.MapSnakeCase("search_access_checkpoints");
        builder.ToTable("search_access_checkpoints", SearchAccessSchema.Name);
        builder.HasKey(e => e.SearchAccessCheckpointId);
        builder.Property(e => e.AccessProjectionStateName).HasMaxLength(20);
        builder.Property(e => e.QuarantineReasonCode).HasMaxLength(64);
        builder.HasIndex(e => e.OrganizationId).IsUnique().HasDatabaseName("uq_search_access_checkpoint_org");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_search_access_checkpoint_org").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_search_access_checkpoint_state", "access_projection_state_name IN ('Maintaining','Quarantined')");
            t.HasCheckConstraint("ck_search_access_checkpoint_quarantine", "(access_projection_state_name = 'Quarantined') = (quarantine_reason_code IS NOT NULL)");
            t.HasCheckConstraint("ck_search_access_checkpoint_revision", "consumed_authorization_revision >= 0 AND checkpoint_revision >= 1 AND access_projection_protocol_version >= 1");
            t.HasCheckConstraint("ck_search_access_checkpoint_digest", "(consumed_authorization_revision = 0) = (consumed_journal_digest IS NULL) AND (consumed_journal_digest IS NULL OR octet_length(consumed_journal_digest) = 32)");
            t.HasCheckConstraint("ck_search_access_checkpoint_content_digest", "consumed_content_integrity_revision >= 0 AND (consumed_content_integrity_revision = 0) = (consumed_content_journal_digest IS NULL) AND (consumed_content_journal_digest IS NULL OR octet_length(consumed_content_journal_digest) = 32)");
            t.HasCheckConstraint("ck_search_access_checkpoint_coverage_digest", "consumed_coverage_digest IS NULL OR octet_length(consumed_coverage_digest) = 32");
        });
    }
}

public sealed class SearchAccessEdgeConfiguration : IEntityTypeConfiguration<SearchAccessEdge>
{
    public void Configure(EntityTypeBuilder<SearchAccessEdge> builder)
    {
        builder.MapSnakeCase("search_access_edges");
        builder.ToTable("search_access_edges", SearchAccessSchema.Name);
        builder.HasKey(e => e.SearchAccessEdgeId);
        builder.Property(e => e.ResourceTypeName).HasMaxLength(40);
        builder.Property(e => e.RelationshipName).HasMaxLength(40);
        builder.Property(e => e.SubjectTypeName).HasMaxLength(40);
        builder.Property(e => e.SubjectRelationName).HasMaxLength(40);
        builder.HasIndex(e => new { e.OrganizationId, e.ResourceTypeName, e.ResourceEntityId, e.RelationshipName, e.SubjectTypeName, e.SubjectEntityId, e.SubjectRelationName }).IsUnique().HasDatabaseName("uq_search_access_edge_tuple");
        builder.HasIndex(e => new { e.OrganizationId, e.SubjectTypeName, e.SubjectEntityId, e.ResourceTypeName, e.RelationshipName }).HasDatabaseName("ix_search_access_edges_subject");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_search_access_edge_org").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t => t.HasCheckConstraint("ck_search_access_edge_kind",
            "(resource_type_name, relationship_name) IN (('form_record','owner'),('form_record','parent'),('form_record','shared_via'),('record_share','reader'),('record_share','contributor'),('record_share','manager'),('field_item','shared_via'),('team','member')) AND mirrored_authorization_revision >= 0"));
    }
}

public sealed class SearchAccessRecordGrantConfiguration : IEntityTypeConfiguration<SearchAccessRecordGrant>
{
    public void Configure(EntityTypeBuilder<SearchAccessRecordGrant> builder)
    {
        builder.MapSnakeCase("search_access_record_grants");
        builder.ToTable("search_access_record_grants", SearchAccessSchema.Name);
        builder.HasKey(e => e.SearchAccessRecordGrantId);
        builder.Property(e => e.AccessPrincipalTypeName).HasMaxLength(10);
        builder.Property(e => e.AccessKindName).HasMaxLength(20);
        builder.Property(e => e.ShareRoleName).HasMaxLength(20);
        builder.HasIndex(e => new { e.OrganizationId, e.AccessPrincipalTypeName, e.AccessPrincipalEntityId, e.FormRecordId }).HasDatabaseName("ix_search_access_record_grants_principal");
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId }).HasDatabaseName("ix_search_access_record_grants_record");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_search_access_record_grant_org").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_search_access_record_grant_principal", "access_principal_type_name IN ('user','team')");
            t.HasCheckConstraint("ck_search_access_record_grant_kind",
                "(access_kind_name = 'EffectiveOwner' AND record_share_id IS NULL AND share_role_name IS NULL) OR (access_kind_name = 'ShareRecipient' AND record_share_id IS NOT NULL AND share_role_name IN ('reader','contributor','manager'))");
        });
    }
}

/// <summary>
/// IX-5b: access-scoped value postings. Every index leads with (organization, principal type, principal, field): a statement over
/// these rows is structurally bounded by the caller's principals (SAFE conditions); one partial index per value type.
/// </summary>
public sealed class SearchAccessValuePostingConfiguration : IEntityTypeConfiguration<SearchAccessValuePosting>
{
    public void Configure(EntityTypeBuilder<SearchAccessValuePosting> builder)
    {
        builder.MapSnakeCase("search_access_value_postings");
        builder.ToTable("search_access_value_postings", SearchAccessSchema.Name);
        builder.HasKey(e => e.SearchAccessValuePostingId);
        builder.Property(e => e.AccessPrincipalTypeName).HasMaxLength(10);
        builder.Property(e => e.ValueTypeName).HasMaxLength(10);
        builder.Property(e => e.DateValue).HasColumnName("date_value").HasColumnType("date"); // as Tier 1 (DateOnly is not snake-cased by convention)
        builder.Property(e => e.DecimalValue).HasColumnType("numeric"); // exactly Tier 1's type: copied values are never rounded
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId }).HasDatabaseName("ix_search_access_value_postings_record");
        foreach (var (name, type, column) in new[]
                 {
                     ("string", "String", nameof(SearchAccessValuePosting.NormalizedStringValue)), ("integer", "Integer", nameof(SearchAccessValuePosting.IntegerValue)),
                     ("decimal", "Decimal", nameof(SearchAccessValuePosting.DecimalValue)), ("date", "Date", nameof(SearchAccessValuePosting.DateValue)),
                     ("date_time", "DateTime", nameof(SearchAccessValuePosting.DateTimeValue)), ("boolean", "Boolean", nameof(SearchAccessValuePosting.BooleanValue)),
                     ("option", "Option", nameof(SearchAccessValuePosting.FieldOptionId)),
                 })
        {
            builder.HasIndex(nameof(SearchAccessValuePosting.OrganizationId), nameof(SearchAccessValuePosting.AccessPrincipalTypeName), nameof(SearchAccessValuePosting.AccessPrincipalEntityId), nameof(SearchAccessValuePosting.FieldDefinitionId), column)
                .HasDatabaseName($"ix_search_access_value_postings_{name}").HasFilter($"value_type_name = '{type}'");
        }

        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_search_access_value_posting_org").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_search_access_value_posting_principal", "access_principal_type_name IN ('user','team')");
            t.HasCheckConstraint("ck_search_access_value_posting_one_value",
                "(value_type_name = 'String') = (normalized_string_value IS NOT NULL) AND (value_type_name = 'Integer') = (integer_value IS NOT NULL) AND (value_type_name = 'Decimal') = (decimal_value IS NOT NULL) "
                + "AND (value_type_name = 'Date') = (date_value IS NOT NULL) AND (value_type_name = 'DateTime') = (date_time_value IS NOT NULL) AND (value_type_name = 'Boolean') = (boolean_value IS NOT NULL) "
                + "AND (value_type_name = 'Option') = (field_option_id IS NOT NULL)");
        });
    }
}

public sealed class SearchAccessFieldItemGrantConfiguration : IEntityTypeConfiguration<SearchAccessFieldItemGrant>
{
    public void Configure(EntityTypeBuilder<SearchAccessFieldItemGrant> builder)
    {
        builder.MapSnakeCase("search_access_field_item_grants");
        builder.ToTable("search_access_field_item_grants", SearchAccessSchema.Name);
        builder.HasKey(e => e.SearchAccessFieldItemGrantId);
        builder.Property(e => e.AccessPrincipalTypeName).HasMaxLength(10);
        builder.Property(e => e.ShareRoleName).HasMaxLength(20);
        builder.HasIndex(e => new { e.OrganizationId, e.AccessPrincipalTypeName, e.AccessPrincipalEntityId, e.FormRecordId, e.ManifestItemId }).HasDatabaseName("ix_search_access_item_grants_principal");
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId }).HasDatabaseName("ix_search_access_item_grants_record");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_search_access_item_grant_org").OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_search_access_item_grant_principal", "access_principal_type_name IN ('user','team')");
            t.HasCheckConstraint("ck_search_access_item_grant_role", "share_role_name IN ('reader','contributor','manager')");
        });
    }
}
