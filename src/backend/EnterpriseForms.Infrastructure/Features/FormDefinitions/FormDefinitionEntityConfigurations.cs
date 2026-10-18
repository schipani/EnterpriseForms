using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.FormDefinitions;

public sealed class FormDefinitionConfiguration : IEntityTypeConfiguration<FormDefinition>
{
    public void Configure(EntityTypeBuilder<FormDefinition> builder)
    {
        builder.MapSnakeCase("form_definitions");
        builder.HasKey(f => f.FormDefinitionId);
        builder.Property(f => f.FormDefinitionName).HasMaxLength(200);
        builder.Property(f => f.FormDefinitionDescription).HasMaxLength(2000);
        builder.Property(f => f.VersionBehavior).HasMaxLength(20);
        builder.Property(f => f.LifecycleStatus).HasMaxLength(20);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(f => f.TenantId).HasConstraintName("fk_form_definitions_tenant");
        builder.HasIndex(f => new { f.TenantId, f.FormDefinitionName }).HasDatabaseName("ix_form_definitions_tenant_name");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_form_definitions_version_behavior", "version_behavior IN ('Historical','Latest','Hybrid')");
            t.HasCheckConstraint("ck_form_definitions_lifecycle", "lifecycle_status IN ('Active','Archived','Deleted')");
        });
    }
}

public sealed class FormVersionConfiguration : IEntityTypeConfiguration<FormVersion>
{
    public void Configure(EntityTypeBuilder<FormVersion> builder)
    {
        builder.MapSnakeCase("form_versions");
        builder.HasKey(v => v.FormVersionId);
        builder.Property(v => v.LayoutJson).HasColumnType("jsonb");
        builder.Property(v => v.ChangeReason).HasMaxLength(1000);
        builder.HasIndex(v => new { v.FormDefinitionId, v.VersionNumber }).IsUnique().HasDatabaseName("uq_form_versions_definition_number");
        builder.HasOne<FormDefinition>().WithMany().HasForeignKey(v => v.FormDefinitionId).HasConstraintName("fk_form_versions_definition");
        builder.HasOne<FormVersion>().WithMany().HasForeignKey(v => v.RestoredFromFormVersionId).HasConstraintName("fk_form_versions_restored_from");
        builder.ToTable(t => t.HasCheckConstraint("ck_form_versions_number_positive", "version_number > 0 AND layout_schema_version > 0"));
    }
}

public sealed class FieldDefinitionConfiguration : IEntityTypeConfiguration<FieldDefinition>
{
    public void Configure(EntityTypeBuilder<FieldDefinition> builder)
    {
        builder.MapSnakeCase("field_definitions");
        builder.HasKey(f => f.FieldDefinitionId);
        builder.Property(f => f.FieldKey).HasMaxLength(64);
        builder.Property(f => f.FieldDisplayName).HasMaxLength(200);
        builder.Property(f => f.FieldDescription).HasMaxLength(2000);
        builder.Property(f => f.FieldDataType).HasMaxLength(20);
        builder.Property(f => f.FieldClassification).HasMaxLength(20);
        builder.Property(f => f.LifecycleStatus).HasMaxLength(20);
        builder.HasIndex(f => new { f.FormDefinitionId, f.FieldKey }).IsUnique().HasDatabaseName("uq_field_definitions_form_key");
        builder.HasOne<FormDefinition>().WithMany().HasForeignKey(f => f.FormDefinitionId).HasConstraintName("fk_field_definitions_definition");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_field_definitions_data_type", "field_data_type IN ('String','Integer','Decimal','Date','DateTime','Boolean','Guid','Option','File')");
            t.HasCheckConstraint("ck_field_definitions_classification", "field_classification IN ('Public','Internal','Confidential','Restricted')");
        });
    }
}
