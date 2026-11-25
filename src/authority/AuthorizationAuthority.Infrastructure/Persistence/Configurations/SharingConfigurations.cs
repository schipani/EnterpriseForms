using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthorizationAuthority.Infrastructure.Persistence.Configurations;

/// <summary>C10 M1-WP1: share-specific metadata stored alongside the generic AuthorityResource.</summary>
public sealed class ShareResourceMetadataConfiguration : IEntityTypeConfiguration<ShareResourceMetadata>
{
    public void Configure(EntityTypeBuilder<ShareResourceMetadata> builder)
    {
        builder.MapSnakeCase("share_resource_metadata");
        builder.HasKey(e => e.ShareResourceMetadataId);
        builder.Property(e => e.ResourceTypeName).HasMaxLength(40).HasDefaultValue(ResourceTypeNames.RecordShare);
        builder.Property(e => e.ShareInvalidationReasonCode).HasMaxLength(40);

        // Unique per organization (matches authority_resources.resource_entity_id for record_share resources).
        builder.HasAlternateKey(e => new { e.OrganizationId, e.RecordShareId })
            .HasName("ak_share_resource_metadata_organization_share");

        // Lineage index for descendant cascade.
        builder.HasIndex(e => new { e.OrganizationId, e.ParentRecordShareId })
            .HasDatabaseName("ix_share_resource_metadata_parent")
            .HasFilter("parent_record_share_id IS NOT NULL");

        // Expiry index for convergence loop.
        builder.HasIndex(e => new { e.OrganizationId, e.ShareExpiresOn })
            .HasDatabaseName("ix_share_resource_metadata_expiry")
            .HasFilter("share_expires_on IS NOT NULL");

        // Legacy lineage unresolved index for dry-run tool.
        builder.HasIndex(e => new { e.OrganizationId, e.LegacyLineageUnresolved })
            .HasDatabaseName("ix_share_resource_metadata_legacy_unresolved")
            .HasFilter("legacy_lineage_unresolved");

        builder.HasOne<OrganizationAuthorizationState>().WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .HasPrincipalKey(o => o.OrganizationId)
            .HasConstraintName("fk_share_resource_metadata_org");

        builder.HasOne<AuthorityResource>().WithMany()
            .HasForeignKey(e => new { e.OrganizationId, e.ResourceTypeName, ResourceEntityId = e.RecordShareId })
            .HasPrincipalKey(r => new { r.OrganizationId, r.ResourceTypeName, r.ResourceEntityId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_share_resource_metadata_registry");
        builder.HasOne<ShareResourceMetadata>().WithMany()
            .HasForeignKey(e => new { e.OrganizationId, RecordShareId = e.ParentRecordShareId })
            .HasPrincipalKey(p => new { p.OrganizationId, p.RecordShareId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_share_resource_metadata_parent");
        builder.HasOne<SecurityCommand>().WithMany()
            .HasForeignKey(e => new { e.OrganizationId, SecurityCommandId = e.ExpirySourceSecurityCommandId })
            .HasPrincipalKey(c => new { c.OrganizationId, c.SecurityCommandId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_share_resource_metadata_expiry_source");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_share_metadata_registry_type", "resource_type_name = 'record_share'");

            // Delegation depth must be 1-3 (platform maximum).
            t.HasCheckConstraint("ck_share_delegation_depth", "delegation_depth BETWEEN 1 AND 3");

            // Revision must be positive.
            t.HasCheckConstraint("ck_share_revision_positive", "share_revision >= 1");

            // Depth 1 means no parent; depth > 1 requires a parent.
            t.HasCheckConstraint("ck_share_depth_parent_coherence",
                "(delegation_depth = 1 AND parent_record_share_id IS NULL) OR (delegation_depth > 1 AND parent_record_share_id IS NOT NULL)");

            // Expiry source required when expiry is set.
            t.HasCheckConstraint("ck_share_expiry_source_coherence",
                "(share_expires_on IS NULL) = (expiry_source_security_command_id IS NULL)");

            // The command planner determines when the allowlisted reason is required.
            t.HasCheckConstraint("ck_share_invalidation_reason",
                "share_invalidation_reason_code IS NULL OR share_invalidation_reason_code IN ('UpstreamExpired','UpstreamRevoked','UpstreamNarrowed','GrantorPathLost','OwnershipTransferred','LineageUnresolvable','DelegationDepthExceeded')");
        });
    }
}

/// <summary>C10 M1-WP8: document item version binding within a share.</summary>
public sealed class ShareDocumentBindingConfiguration : IEntityTypeConfiguration<ShareDocumentBinding>
{
    public void Configure(EntityTypeBuilder<ShareDocumentBinding> builder)
    {
        builder.MapSnakeCase("share_document_bindings");
        builder.HasKey(e => e.ShareDocumentBindingId);

        // Unique per share and attachment.
        builder.HasIndex(e => new { e.OrganizationId, e.RecordShareId, e.FileAttachmentId })
            .IsUnique()
            .HasDatabaseName("uq_share_document_binding");

        // Index for looking up all document bindings by organization.
        builder.HasIndex(e => e.OrganizationId)
            .HasDatabaseName("ix_share_document_bindings_org");

        builder.HasOne<OrganizationAuthorizationState>().WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .HasPrincipalKey(o => o.OrganizationId)
            .HasConstraintName("fk_share_document_binding_org");

        builder.HasOne<ShareResourceMetadata>().WithMany()
            .HasForeignKey(e => new { e.OrganizationId, e.RecordShareId })
            .HasPrincipalKey(s => new { s.OrganizationId, s.RecordShareId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_share_document_binding_share");

        builder.HasOne<SecurityCommand>().WithMany()
            .HasForeignKey(e => new { e.OrganizationId, SecurityCommandId = e.BoundBySecurityCommandId })
            .HasPrincipalKey(c => new { c.OrganizationId, c.SecurityCommandId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_share_document_binding_command");

        builder.ToTable(t =>
        {
            // Version number must be positive.
            t.HasCheckConstraint("ck_share_document_version_positive", "shared_through_version_number >= 1");

            // CanDownload and CanReplace both imply CanView.
            t.HasCheckConstraint("ck_share_document_view_implied",
                "(NOT can_download_document OR can_view_document) AND (NOT can_replace_document OR can_view_document)");
        });
    }
}
