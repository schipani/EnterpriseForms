using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthorizationAuthority.Infrastructure.Persistence.Configurations;

public sealed class OrganizationAuthorizationStateConfiguration : IEntityTypeConfiguration<OrganizationAuthorizationState>
{
    public void Configure(EntityTypeBuilder<OrganizationAuthorizationState> builder)
    {
        builder.MapSnakeCase("organization_authorization_states");
        builder.HasKey(e => e.OrganizationAuthorizationStateId);
        builder.HasIndex(e => e.OrganizationId).IsUnique();
        builder.Property(e => e.OrganizationDisplayName).HasMaxLength(200);
        builder.Property(e => e.CheckpointEnforcementModeName).HasMaxLength(30);
        builder.Property(e => e.IntegrityQuarantineReasonCode).HasMaxLength(100);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_projection_never_ahead", "projection_applied_revision <= authorization_revision");
            t.HasCheckConstraint("ck_authorization_revision_non_negative", "authorization_revision >= 0 AND projection_applied_revision >= 0 AND content_integrity_revision >= 0");
            t.HasCheckConstraint("ck_checkpoint_enforcement_mode", "checkpoint_enforcement_mode_name IN ('Asynchronous','RequiredBeforeEffective')");
        });
    }
}

public sealed class IdentityBindingConfiguration : IEntityTypeConfiguration<IdentityBinding>
{
    public void Configure(EntityTypeBuilder<IdentityBinding> builder)
    {
        builder.MapSnakeCase("identity_bindings");
        builder.HasKey(e => e.IdentityBindingId);
        builder.Property(e => e.IdentityIssuerUri).HasMaxLength(512);
        builder.Property(e => e.IdentitySubjectIdentifier).HasMaxLength(512);
        builder.HasIndex(e => new { e.OrganizationId, e.IdentityIssuerUri, e.IdentitySubjectIdentifier }).IsUnique().HasDatabaseName("uq_identity_subject");
        builder.HasIndex(e => new { e.OrganizationId, e.AuthorityUserId }).IsUnique().HasDatabaseName("uq_authority_user_per_org");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_identity_org");
    }
}

public sealed class OrganizationProtectionPolicyConfiguration : IEntityTypeConfiguration<OrganizationProtectionPolicy>
{
    public void Configure(EntityTypeBuilder<OrganizationProtectionPolicy> builder)
    {
        builder.MapSnakeCase("organization_protection_policies");
        builder.HasKey(e => e.OrganizationProtectionPolicyId);
        builder.Property(e => e.MinimumFieldClassification).HasMaxLength(20);
        builder.HasIndex(e => e.OrganizationId).IsUnique().HasDatabaseName("uq_protection_policy_org");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_protection_policy_org");
        builder.ToTable(t => t.HasCheckConstraint("ck_protection_policy_classification", "minimum_field_classification IN ('Public','Internal','Confidential','Restricted')"));
    }
}

public sealed class AuthorityResourceConfiguration : IEntityTypeConfiguration<AuthorityResource>
{
    public void Configure(EntityTypeBuilder<AuthorityResource> builder)
    {
        builder.MapSnakeCase("authority_resources");
        builder.HasKey(e => e.AuthorityResourceId);
        builder.Property(e => e.ResourceTypeName).HasMaxLength(40);
        builder.Property(e => e.ResourceLifecycleState).HasMaxLength(20);
        builder.HasIndex(e => new { e.OrganizationId, e.ResourceTypeName, e.ResourceEntityId }).IsUnique().HasDatabaseName("uq_authority_resource");
        // Phase C7 AC-5: form-scoped catalogue enumeration without scanning the organization's registry.
        builder.HasIndex(e => new { e.OrganizationId, e.ResourceTypeName, e.ParentResourceEntityId, e.ResourceLifecycleState }).HasDatabaseName("ix_authority_resources_form_scope");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_resource_org");
        builder.ToTable(t => t.HasCheckConstraint("ck_resource_lifecycle_state", "resource_lifecycle_state IN ('Registered','Active','Revoked','Quarantined')"));
    }
}

public sealed class SecurityCommandConfiguration : IEntityTypeConfiguration<SecurityCommand>
{
    public void Configure(EntityTypeBuilder<SecurityCommand> builder)
    {
        builder.MapSnakeCase("security_commands");
        builder.HasKey(e => e.SecurityCommandId);
        builder.Property(e => e.CommandKindName).HasMaxLength(60);
        builder.Property(e => e.CommandIdempotencyKey).HasMaxLength(128);
        builder.Property(e => e.CommandStateName).HasMaxLength(40);
        builder.Property(e => e.RejectionReasonCode).HasMaxLength(100);
        builder.Property(e => e.CommandReasonText).HasMaxLength(1000);
        builder.Property(e => e.CommandPayloadJson).HasColumnType("jsonb");
        builder.HasIndex(e => new { e.OrganizationId, e.CommandIdempotencyKey }).IsUnique().HasDatabaseName("uq_security_command_idempotency");
        builder.HasIndex(e => new { e.OrganizationId, e.CommittedAuthorizationRevision }).HasDatabaseName("ix_security_commands_org_revision");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_security_command_org");
        builder.HasOne<IdentityBinding>().WithMany().HasForeignKey(e => e.ActorIdentityBindingId).HasConstraintName("fk_security_command_actor");
        builder.ToTable(t => t.HasCheckConstraint("ck_security_command_state", "command_state_name IN ('Pending','CommittedAwaitingProjection','Effective','Rejected','FailedReconciliation')"));
    }
}

public sealed class AuthorityRelationshipEdgeConfiguration : IEntityTypeConfiguration<AuthorityRelationshipEdge>
{
    public void Configure(EntityTypeBuilder<AuthorityRelationshipEdge> builder)
    {
        builder.MapSnakeCase("authority_relationship_edges");
        builder.HasKey(e => e.AuthorityRelationshipEdgeId);
        builder.Property(e => e.ResourceTypeName).HasMaxLength(40);
        builder.Property(e => e.RelationshipName).HasMaxLength(40);
        builder.Property(e => e.SubjectTypeName).HasMaxLength(40);
        builder.Property(e => e.SubjectRelationName).HasMaxLength(40);
        builder.HasIndex(e => new { e.OrganizationId, e.ResourceTypeName, e.ResourceEntityId, e.RelationshipName, e.SubjectTypeName, e.SubjectEntityId, e.SubjectRelationName })
            .IsUnique().HasFilter("removed_by_security_command_id IS NULL").HasDatabaseName("uq_authority_relationship_active");
        builder.HasIndex(e => new { e.OrganizationId, e.SubjectTypeName, e.SubjectEntityId, e.RelationshipName }).HasDatabaseName("ix_authority_edges_subject");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_authority_edge_org");
        builder.HasOne<SecurityCommand>().WithMany().HasForeignKey(e => e.CreatedBySecurityCommandId).HasConstraintName("fk_edge_command");
        builder.HasOne<SecurityCommand>().WithMany().HasForeignKey(e => e.RemovedBySecurityCommandId).HasConstraintName("fk_edge_removing_command");
    }
}

public sealed class ProjectionOutboxEventConfiguration : IEntityTypeConfiguration<ProjectionOutboxEvent>
{
    public void Configure(EntityTypeBuilder<ProjectionOutboxEvent> builder)
    {
        builder.MapSnakeCase("projection_outbox_events");
        builder.HasKey(e => e.ProjectionOutboxEventId);
        builder.Property(e => e.RelationshipDeltaJson).HasColumnType("jsonb");
        builder.Property(e => e.ProjectionDeliveryState).HasMaxLength(20);
        builder.Property(e => e.LastProjectionErrorCode).HasMaxLength(100);
        builder.HasIndex(e => e.SecurityCommandId).IsUnique().HasDatabaseName("uq_projection_command");
        builder.HasIndex(e => new { e.OrganizationId, e.TargetAuthorizationRevision }).IsUnique().HasDatabaseName("uq_projection_org_revision");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_projection_org");
        builder.HasOne<SecurityCommand>().WithMany().HasForeignKey(e => e.SecurityCommandId).HasConstraintName("fk_projection_command");
        builder.ToTable(t => t.HasCheckConstraint("ck_projection_delivery_state", "projection_delivery_state IN ('Pending','Delivered','Failed')"));
    }
}

public sealed class SecurityJournalEntryConfiguration : IEntityTypeConfiguration<SecurityJournalEntry>
{
    public void Configure(EntityTypeBuilder<SecurityJournalEntry> builder)
    {
        builder.MapSnakeCase("security_journal_entries");
        builder.HasKey(e => e.SecurityJournalEntryId);
        builder.Property(e => e.JournalStreamName).HasMaxLength(30);
        builder.Property(e => e.JournalEventKind).HasMaxLength(60);
        // text, NOT jsonb: PostgreSQL normalizes jsonb (key order, whitespace, numbers), which silently broke the
        // byte-exact body <-> JournalEventDigest binding that the archive and replay verification depend on (A-F12).
        builder.Property(e => e.JournalEventBodyJson).HasColumnType("text");
        builder.Property(e => e.ArchiveDeliveryState).HasMaxLength(30);
        builder.Property(e => e.ArchiveReceiptReference).HasMaxLength(512);
        builder.Property(e => e.LastArchiveErrorCode).HasMaxLength(100);
        builder.HasIndex(e => new { e.OrganizationId, e.JournalStreamName, e.JournalStreamRevision }).IsUnique().HasDatabaseName("uq_journal_stream_revision");
        builder.HasIndex(e => new { e.OrganizationId, e.ArchiveDeliveryState }).HasDatabaseName("ix_journal_pending_archive");
        // Phase C7 M1: a record's checkpoint entry is found by its operation in one indexed lookup (verification was O(content revisions) per record).
        builder.HasIndex(e => new { e.OrganizationId, e.JournalStreamName, e.ContentChangeOperationId }).HasDatabaseName("ix_journal_content_operation");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_journal_org");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_journal_digest_length", "octet_length(journal_event_digest) = 32");
            t.HasCheckConstraint("ck_journal_archive_state", "archive_delivery_state IN ('Pending','Anchored','VerificationFailed')");
        });
    }
}

public sealed class RecordContentHeadConfiguration : IEntityTypeConfiguration<RecordContentHead>
{
    public void Configure(EntityTypeBuilder<RecordContentHead> builder)
    {
        builder.MapSnakeCase("record_content_heads");
        builder.HasKey(e => e.RecordContentHeadId);
        builder.Property(e => e.QuarantineReasonCode).HasMaxLength(100);
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId }).IsUnique().HasDatabaseName("uq_record_content_head");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_content_head_org");
        builder.HasOne<ContentChangeOperation>().WithMany().HasForeignKey(e => e.CommittedOperationId).HasConstraintName("fk_committed_content_operation");
        builder.HasOne<RecordManifestCommit>().WithMany().HasForeignKey(e => e.CurrentRecordManifestCommitId).HasConstraintName("fk_current_record_manifest_commit");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_committed_operation_identity", "(committed_content_revision = 0 AND committed_operation_id IS NULL AND current_record_manifest_commit_id IS NULL) OR (committed_content_revision > 0 AND committed_operation_id IS NOT NULL AND current_record_manifest_commit_id IS NOT NULL)");
            t.HasCheckConstraint("ck_content_head_root_length", "octet_length(committed_manifest_root) = 32");
        });
    }
}

public sealed class ContentChangeOperationConfiguration : IEntityTypeConfiguration<ContentChangeOperation>
{
    public void Configure(EntityTypeBuilder<ContentChangeOperation> builder)
    {
        builder.MapSnakeCase("content_change_operations");
        builder.HasKey(e => e.ContentChangeOperationId);
        builder.Property(e => e.OperationIdempotencyKey).HasMaxLength(128);
        builder.Property(e => e.OperationStateName).HasMaxLength(20);
        builder.Property(e => e.OperationQuarantineReasonCode).HasMaxLength(100);
        builder.HasIndex(e => new { e.OrganizationId, e.OperationIdempotencyKey }).IsUnique().HasDatabaseName("uq_content_op_idempotency");
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId, e.OperationStateName, e.OperationCreatedOn }).HasDatabaseName("ix_content_ops_org_record_state");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_content_op_org");
        builder.HasOne<IdentityBinding>().WithMany().HasForeignKey(e => e.ActorIdentityBindingId).HasConstraintName("fk_content_op_actor");
        builder.ToTable(t => t.HasCheckConstraint("ck_content_operation_state", "operation_state_name IN ('Prepared','Committed','Aborted','Quarantined')"));
    }
}

public sealed class ContentCandidateLeafConfiguration : IEntityTypeConfiguration<ContentCandidateLeaf>
{
    public void Configure(EntityTypeBuilder<ContentCandidateLeaf> builder)
    {
        builder.MapSnakeCase("content_candidate_leaves");
        builder.HasKey(e => e.ContentCandidateLeafId);
        builder.Property(e => e.MutationKindName).HasMaxLength(20);
        builder.HasIndex(e => new { e.ContentChangeOperationId, e.ManifestItemKey }).IsUnique().HasDatabaseName("uq_candidate_leaf_key");
        builder.HasOne<ContentChangeOperation>().WithMany().HasForeignKey(e => e.ContentChangeOperationId).HasConstraintName("fk_candidate_leaf_operation");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_candidate_leaf_key_length", "octet_length(manifest_item_key) = 17");
            t.HasCheckConstraint("ck_candidate_leaf_digest_lengths", "octet_length(canonical_payload_digest) = 32 AND octet_length(row_signature) = 32 AND octet_length(leaf_digest) = 32");
        });
    }
}

public sealed class RecordManifestCommitConfiguration : IEntityTypeConfiguration<RecordManifestCommit>
{
    public void Configure(EntityTypeBuilder<RecordManifestCommit> builder)
    {
        builder.MapSnakeCase("record_manifest_commits");
        builder.HasKey(e => e.RecordManifestCommitId);
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId, e.CommittedContentRevision }).IsUnique().HasDatabaseName("uq_manifest_record_revision");
        builder.HasIndex(e => new { e.OrganizationId, e.FormRecordId, e.CommittedOperationId }).IsUnique().HasDatabaseName("uq_manifest_committed_operation");
        builder.HasOne<ContentChangeOperation>().WithMany().HasForeignKey(e => e.CommittedOperationId).HasConstraintName("fk_manifest_content_op");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_manifest_root_lengths", "octet_length(manifest_root_digest) = 32 AND octet_length(manifest_leaf_index_digest) = 32");
            t.HasCheckConstraint("ck_manifest_leaf_count", "manifest_leaf_count >= 1");
        });
    }
}

public sealed class RecordManifestLeafConfiguration : IEntityTypeConfiguration<RecordManifestLeaf>
{
    public void Configure(EntityTypeBuilder<RecordManifestLeaf> builder)
    {
        builder.MapSnakeCase("record_manifest_leaves");
        builder.HasKey(e => e.RecordManifestLeafId);
        builder.HasIndex(e => new { e.RecordManifestCommitId, e.ManifestItemKey }).IsUnique().HasDatabaseName("uq_manifest_leaf_key");
        builder.HasIndex(e => new { e.RecordManifestCommitId, e.ManifestLeafPosition }).IsUnique().HasDatabaseName("uq_manifest_leaf_position");
        builder.HasOne<RecordManifestCommit>().WithMany().HasForeignKey(e => e.RecordManifestCommitId).HasConstraintName("fk_manifest_leaf_commit");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_manifest_leaf_key_length", "octet_length(manifest_item_key) = 17");
            t.HasCheckConstraint("ck_manifest_leaf_digest_lengths", "octet_length(canonical_payload_digest) = 32 AND octet_length(row_signature) = 32 AND octet_length(manifest_leaf_digest) = 32");
        });
    }
}

public sealed class ContentIntegrityCheckpointConfiguration : IEntityTypeConfiguration<ContentIntegrityCheckpoint>
{
    public void Configure(EntityTypeBuilder<ContentIntegrityCheckpoint> builder)
    {
        builder.MapSnakeCase("content_integrity_checkpoints");
        builder.HasKey(e => e.ContentIntegrityCheckpointId);
        builder.HasIndex(e => new { e.OrganizationId, e.ContentIntegrityRevision }).IsUnique().HasDatabaseName("uq_content_integrity_org_revision");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_content_integrity_org");
        builder.ToTable(t => t.HasCheckConstraint("ck_checkpoint_digest_lengths", "octet_length(manifest_root_digest) = 32 AND octet_length(checkpoint_event_digest) = 32"));
    }
}

public sealed class IntegrityKeyRegistrationConfiguration : IEntityTypeConfiguration<IntegrityKeyRegistration>
{
    public void Configure(EntityTypeBuilder<IntegrityKeyRegistration> builder)
    {
        builder.MapSnakeCase("integrity_key_registrations");
        builder.HasKey(e => e.IntegrityKeyRegistrationId);
        builder.Property(e => e.KeyPurposeName).HasMaxLength(60);
        builder.Property(e => e.ProviderName).HasMaxLength(40);
        builder.Property(e => e.ProviderKeyName).HasMaxLength(200);
        builder.HasIndex(e => new { e.OrganizationId, e.KeyPurposeName }).IsUnique().HasDatabaseName("uq_integrity_key_org_purpose");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_integrity_key_org");
    }
}

public sealed class SearchCatalogueSnapshotConfiguration : IEntityTypeConfiguration<SearchCatalogueSnapshot>
{
    public void Configure(EntityTypeBuilder<SearchCatalogueSnapshot> builder)
    {
        builder.MapSnakeCase("search_catalogue_snapshots");
        builder.HasKey(e => e.SearchCatalogueSnapshotId);
        builder.Property(e => e.SnapshotScopeKind).HasMaxLength(30);
        builder.Property(e => e.RequestingWorkloadName).HasMaxLength(100);
        builder.Property(e => e.SnapshotInvalidationReason).HasMaxLength(60);
        builder.HasIndex(e => new { e.OrganizationId, e.SnapshotExpiresOn }).HasDatabaseName("ix_search_catalogue_snapshots_expiry");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_search_catalogue_snapshot_org");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_search_catalogue_scope_kind", "snapshot_scope_kind IN ('UserSearch','MigrationVerification')");
            t.HasCheckConstraint("ck_search_catalogue_scope_actor",
                "(snapshot_scope_kind = 'UserSearch' AND requesting_authority_user_id IS NOT NULL AND scoping_security_command_id IS NULL) OR (snapshot_scope_kind = 'MigrationVerification' AND requesting_workload_name IS NOT NULL AND scoping_security_command_id IS NOT NULL AND requesting_authority_user_id IS NULL)");
            t.HasCheckConstraint("ck_search_catalogue_entry_count", "catalogue_entry_count >= 0 AND octet_length(catalogue_digest) = 32");
        });
    }
}

public sealed class SearchCatalogueSnapshotEntryConfiguration : IEntityTypeConfiguration<SearchCatalogueSnapshotEntry>
{
    public void Configure(EntityTypeBuilder<SearchCatalogueSnapshotEntry> builder)
    {
        builder.MapSnakeCase("search_catalogue_snapshot_entries");
        builder.HasKey(e => e.SearchCatalogueSnapshotEntryId);
        builder.Property(e => e.ResourceLifecycleState).HasMaxLength(20);
        builder.HasIndex(e => new { e.SearchCatalogueSnapshotId, e.CatalogueEntryPosition }).IsUnique().HasDatabaseName("uq_search_catalogue_entry_position");
        builder.HasIndex(e => new { e.SearchCatalogueSnapshotId, e.FormRecordId }).IsUnique().HasDatabaseName("uq_search_catalogue_entry_record");
        builder.HasOne<SearchCatalogueSnapshot>().WithMany().HasForeignKey(e => e.SearchCatalogueSnapshotId).HasConstraintName("fk_search_catalogue_entry_snapshot");
        builder.ToTable(t => t.HasCheckConstraint("ck_search_catalogue_entry_revision", "committed_content_revision >= 0 AND catalogue_entry_position >= 0"));
    }
}
