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
    public static readonly string[] DerivedTables = ["search_access_edges", "search_access_record_grants", "search_access_field_item_grants"];

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
