using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.FormDefinitions;

public sealed class FieldProtectionMigrationConfiguration : IEntityTypeConfiguration<FieldProtectionMigration>
{
    public void Configure(EntityTypeBuilder<FieldProtectionMigration> builder)
    {
        builder.MapSnakeCase("field_protection_migrations");
        builder.HasKey(m => m.FieldProtectionMigrationId);
        builder.Property(m => m.MigrationState).HasMaxLength(20);
        builder.Property(m => m.MigrationDirection).HasMaxLength(10).HasDefaultValue("Encrypt");
        builder.Property(m => m.LastErrorCode).HasMaxLength(200);
        builder.Property(m => m.LeaseOwnerName).HasMaxLength(120);
        builder.HasIndex(m => new { m.FieldDefinitionId, m.ProtectionPolicyRevision }).IsUnique().HasDatabaseName("uq_field_protection_migrations_field_revision");
        builder.HasIndex(m => new { m.MigrationState, m.NextAttemptOn }).HasDatabaseName("ix_field_protection_migrations_due");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).HasConstraintName("fk_field_protection_migrations_tenant");
        builder.HasOne<FormDefinition>().WithMany().HasForeignKey(m => m.FormDefinitionId).HasConstraintName("fk_field_protection_migrations_form");
        builder.HasOne<FieldDefinition>().WithMany().HasForeignKey(m => m.FieldDefinitionId).HasConstraintName("fk_field_protection_migrations_field");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_field_protection_migrations_state", "migration_state IN ('Queued','Running','Verifying','Completed','Failed','Blocked','Cancelled')");
            t.HasCheckConstraint("ck_field_protection_migrations_direction", "migration_direction IN ('Encrypt','Decrypt')");
            t.HasCheckConstraint("ck_field_protection_migrations_counts", "total_record_count >= 0 AND total_value_count >= 0 AND completed_record_count >= 0 AND failed_record_count >= 0 AND completed_record_count + failed_record_count <= total_record_count");
            t.HasCheckConstraint("ck_field_protection_migrations_verified_counts", "(verified_record_count IS NULL OR verified_record_count >= 0) AND (verified_remaining_record_count IS NULL OR verified_remaining_record_count >= 0) AND (verified_remaining_value_count IS NULL OR verified_remaining_value_count >= 0) AND (unverified_record_count IS NULL OR unverified_record_count >= 0)");
        });
    }
}

public sealed class FieldProtectionMigrationItemConfiguration : IEntityTypeConfiguration<FieldProtectionMigrationItem>
{
    public void Configure(EntityTypeBuilder<FieldProtectionMigrationItem> builder)
    {
        builder.MapSnakeCase("field_protection_migration_items");
        builder.HasKey(i => i.FieldProtectionMigrationItemId);
        builder.Property(i => i.ItemState).HasMaxLength(20);
        builder.Property(i => i.LastErrorCode).HasMaxLength(200);
        builder.Property(i => i.CompletedByOperationKind).HasMaxLength(30);
        builder.HasIndex(i => new { i.FieldProtectionMigrationId, i.FormRecordId }).IsUnique().HasDatabaseName("uq_field_protection_migration_items_record");
        builder.HasIndex(i => new { i.FieldProtectionMigrationId, i.ItemState }).HasDatabaseName("ix_field_protection_migration_items_state");
        builder.HasIndex(i => i.FormRecordId).HasDatabaseName("ix_field_protection_migration_items_form_record");
        builder.HasOne<FieldProtectionMigration>().WithMany().HasForeignKey(i => i.FieldProtectionMigrationId).HasConstraintName("fk_field_protection_migration_items_migration");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(i => i.FormRecordId).HasConstraintName("fk_field_protection_migration_items_record");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_field_protection_migration_items_state", "item_state IN ('Pending','Completed','Failed')");
            t.HasCheckConstraint("ck_field_protection_migration_items_completion_kind", "completed_by_operation_kind IS NULL OR completed_by_operation_kind IN ('MigrationWorker','OtherMigration','UserOperation','NoAcceptedContent')");
            t.HasCheckConstraint("ck_field_protection_migration_items_verified_counts", "(verified_old_form_value_count IS NULL OR verified_old_form_value_count >= 0) AND (verified_head_content_revision IS NULL OR verified_head_content_revision >= 0)");
        });
    }
}

/// <summary>Phase C7 M4 (G19): durable reconciliation jobs of the Worker's field-protection verifier; owned by FormDesigner like the migrations.</summary>
public sealed class FieldProtectionReconciliationConfiguration : IEntityTypeConfiguration<FieldProtectionReconciliation>
{
    public void Configure(EntityTypeBuilder<FieldProtectionReconciliation> builder)
    {
        builder.MapSnakeCase("field_protection_reconciliations");
        builder.HasKey(r => r.FieldProtectionReconciliationId);
        builder.Property(r => r.MigrationDirection).HasMaxLength(10);
        builder.Property(r => r.ReconciliationMode).HasMaxLength(30);
        builder.Property(r => r.ReconciliationState).HasMaxLength(20);
        builder.Property(r => r.RequestReasonText).HasMaxLength(500);
        builder.Property(r => r.LeaseOwnerName).HasMaxLength(120);
        builder.Property(r => r.LastErrorCode).HasMaxLength(200);
        builder.Property(r => r.AttestationDigestHex).HasMaxLength(64);
        builder.Property(r => r.AttestationIncompleteReason).HasMaxLength(60);
        builder.Property(r => r.AttestationReportJson).HasColumnType("jsonb");
        builder.HasIndex(r => new { r.ReconciliationState, r.NextAttemptOn }).HasDatabaseName("ix_field_protection_reconciliations_due");
        builder.HasIndex(r => new { r.FieldDefinitionId, r.ReconciliationRequestedOn }).HasDatabaseName("ix_field_protection_reconciliations_field");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).HasConstraintName("fk_field_protection_reconciliations_tenant");
        builder.HasOne<FormDefinition>().WithMany().HasForeignKey(r => r.FormDefinitionId).HasConstraintName("fk_field_protection_reconciliations_form");
        builder.HasOne<FieldDefinition>().WithMany().HasForeignKey(r => r.FieldDefinitionId).HasConstraintName("fk_field_protection_reconciliations_field");
        builder.HasOne<FieldProtectionMigration>().WithMany().HasForeignKey(r => r.FieldProtectionMigrationId).HasConstraintName("fk_field_protection_reconciliations_migration");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_field_protection_reconciliations_state", "reconciliation_state IN ('Queued','Running','Blocked','Complete','Incomplete','Unavailable','Superseded')");
            t.HasCheckConstraint("ck_field_protection_reconciliations_mode", "reconciliation_mode IN ('MigrationCompletion','VerifyOnly')");
            t.HasCheckConstraint("ck_field_protection_reconciliations_direction", "migration_direction IN ('Encrypt','Decrypt')");
            t.HasCheckConstraint("ck_field_protection_reconciliations_counts", "census_next_offset >= 0 AND census_verified_record_count >= 0 AND census_reused_record_count >= 0 AND census_failed_record_count >= 0 AND decrypt_checked_value_count >= 0 AND decrypt_failed_value_count >= 0 AND logical_type_mismatch_count >= 0 AND reconciliation_round_count >= 0 AND transient_failure_count >= 0");
            // "Complete" is only ever the Authority's complete attestation with every decrypt check passing.
            t.HasCheckConstraint("ck_field_protection_reconciliations_complete", "reconciliation_state <> 'Complete' OR (attestation_complete = true AND authority_attestation_id IS NOT NULL AND decrypt_failed_value_count = 0 AND logical_type_mismatch_count = 0)");
        });
    }
}
