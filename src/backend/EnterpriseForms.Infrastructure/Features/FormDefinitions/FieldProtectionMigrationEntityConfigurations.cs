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
            t.HasCheckConstraint("ck_field_protection_migrations_state", "migration_state IN ('Queued','Running','Completed','Failed','Blocked','Cancelled')");
            t.HasCheckConstraint("ck_field_protection_migrations_direction", "migration_direction IN ('Encrypt','Decrypt')");
            t.HasCheckConstraint("ck_field_protection_migrations_counts", "total_record_count >= 0 AND total_value_count >= 0 AND completed_record_count >= 0 AND failed_record_count >= 0 AND completed_record_count + failed_record_count <= total_record_count");
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
        builder.HasIndex(i => new { i.FieldProtectionMigrationId, i.FormRecordId }).IsUnique().HasDatabaseName("uq_field_protection_migration_items_record");
        builder.HasIndex(i => new { i.FieldProtectionMigrationId, i.ItemState }).HasDatabaseName("ix_field_protection_migration_items_state");
        builder.HasOne<FieldProtectionMigration>().WithMany().HasForeignKey(i => i.FieldProtectionMigrationId).HasConstraintName("fk_field_protection_migration_items_migration");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(i => i.FormRecordId).HasConstraintName("fk_field_protection_migration_items_record");
        builder.ToTable(t => t.HasCheckConstraint("ck_field_protection_migration_items_state", "item_state IN ('Pending','Completed','Failed')"));
    }
}
