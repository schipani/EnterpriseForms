using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.FormRecords;

public sealed class FormRecordConfiguration : IEntityTypeConfiguration<FormRecord>
{
    public void Configure(EntityTypeBuilder<FormRecord> builder)
    {
        builder.MapSnakeCase("form_records");
        builder.HasKey(r => r.FormRecordId);
        builder.Property(r => r.RecordStatus).HasMaxLength(20);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).HasConstraintName("fk_form_records_tenant");
        builder.HasOne<FormDefinition>().WithMany().HasForeignKey(r => r.FormDefinitionId).HasConstraintName("fk_form_records_definition");
        builder.HasOne<FormVersion>().WithMany().HasForeignKey(r => r.CreatedFormVersionId).HasConstraintName("fk_form_records_created_version");
        builder.HasOne<FormVersion>().WithMany().HasForeignKey(r => r.LastEditedFormVersionId).HasConstraintName("fk_form_records_last_edited_version");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(r => r.ParentFormRecordId).HasConstraintName("fk_form_records_parent_record");
        builder.HasIndex(r => new { r.TenantId, r.FormDefinitionId }).HasDatabaseName("ix_form_records_tenant_definition");
        builder.HasIndex(r => new { r.ParentFormRecordId, r.FormRelationshipDefinitionId }).HasDatabaseName("ix_form_records_parent_relationship");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_form_records_status", "record_status IN ('Draft','Submitted','Archived','Deleted')");
            // Exactly-one-owner projection invariant (CLAUDE.md §4B) for top-level records; child records derive ownership from the parent (B-04).
            t.HasCheckConstraint("ck_form_records_single_owner", "(parent_form_record_id IS NOT NULL) OR (owner_user_id IS NOT NULL AND owner_team_id IS NULL) OR (owner_user_id IS NULL AND owner_team_id IS NOT NULL)");
            t.HasCheckConstraint("ck_form_records_revision_positive", "record_revision >= 1");
            t.HasCheckConstraint("ck_form_records_parent_pair", "(parent_form_record_id IS NULL) = (form_relationship_definition_id IS NULL)");
        });
    }
}

public sealed class FormRecordRevisionConfiguration : IEntityTypeConfiguration<FormRecordRevision>
{
    public void Configure(EntityTypeBuilder<FormRecordRevision> builder)
    {
        builder.MapSnakeCase("form_record_revisions");
        builder.HasKey(r => r.FormRecordRevisionId);
        builder.Ignore(r => r.EntityKind).Ignore(r => r.ItemKindCode).Ignore(r => r.ItemId).Ignore(r => r.RowPrimaryKey);
        builder.Property(r => r.RecordStatus).HasMaxLength(20);
        builder.Property(r => r.CandidateState).HasMaxLength(20);
        builder.HasIndex(r => new { r.FormRecordId, r.ContentChangeOperationId }).IsUnique().HasDatabaseName("uq_form_record_revisions_operation");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(r => r.FormRecordId).HasConstraintName("fk_form_record_revisions_record");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).HasConstraintName("fk_form_record_revisions_tenant");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_form_record_revisions_signature_length", "octet_length(row_signature) = 32");
            t.HasCheckConstraint("ck_form_record_revisions_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
            t.HasCheckConstraint("ck_form_record_revisions_candidate_state", "candidate_state IN ('Staged','Committed','Aborted')");
            t.HasCheckConstraint("ck_form_record_revisions_parent_pair", "(parent_form_record_id IS NULL) = (form_relationship_definition_id IS NULL)");
        });
    }
}

/// <summary>Shared mapping of the typed value tables: signed envelope, occurrence uniqueness per operation, tenant/record/field keys.</summary>
internal static class FieldValueConfiguration
{
    public static void ConfigureValueRow<TRow>(EntityTypeBuilder<TRow> builder, string table, string prefix)
        where TRow : FieldValueRowBase
    {
        builder.MapSnakeCase(table);
        builder.Ignore(v => v.EntityKind).Ignore(v => v.ItemKindCode).Ignore(v => v.ItemId).Ignore(v => v.RowPrimaryKey);
        // One occurrence per (operation, record, field, group) — NULLS NOT DISTINCT so top-level duplicates are rejected (CLAUDE.md §2.4).
        builder.HasIndex(v => new { v.ContentChangeOperationId, v.FormRecordId, v.FieldDefinitionId, v.FormRecordGroupId }).IsUnique().AreNullsDistinct(false).HasDatabaseName($"uq_{prefix}_occurrence");
        builder.HasIndex(v => new { v.FormRecordId, v.ContentChangeOperationId }).HasDatabaseName($"ix_{prefix}_record_operation");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(v => v.FormRecordId).HasConstraintName($"fk_{prefix}_record");
        builder.HasOne<FieldDefinition>().WithMany().HasForeignKey(v => v.FieldDefinitionId).HasConstraintName($"fk_{prefix}_field");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).HasConstraintName($"fk_{prefix}_tenant");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint($"ck_{prefix}_signature_length", "octet_length(row_signature) = 32");
            t.HasCheckConstraint($"ck_{prefix}_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
        });
    }
}

public sealed class FieldStringValueConfiguration : IEntityTypeConfiguration<FieldStringValue>
{
    public void Configure(EntityTypeBuilder<FieldStringValue> builder)
    {
        FieldValueConfiguration.ConfigureValueRow(builder, "field_string_values", "field_string_values");
        builder.HasKey(v => v.FieldStringValueId);
        builder.HasIndex(v => new { v.FieldDefinitionId, v.StringValue, v.FormRecordId }).HasDatabaseName("ix_field_string_values_lookup");
    }
}

public sealed class FieldIntegerValueConfiguration : IEntityTypeConfiguration<FieldIntegerValue>
{
    public void Configure(EntityTypeBuilder<FieldIntegerValue> builder)
    {
        FieldValueConfiguration.ConfigureValueRow(builder, "field_integer_values", "field_integer_values");
        builder.HasKey(v => v.FieldIntegerValueId);
        builder.HasIndex(v => new { v.FieldDefinitionId, v.IntegerValue, v.FormRecordId }).HasDatabaseName("ix_field_integer_values_lookup");
    }
}

public sealed class FieldDecimalValueConfiguration : IEntityTypeConfiguration<FieldDecimalValue>
{
    public void Configure(EntityTypeBuilder<FieldDecimalValue> builder)
    {
        FieldValueConfiguration.ConfigureValueRow(builder, "field_decimal_values", "field_decimal_values");
        builder.HasKey(v => v.FieldDecimalValueId);
        builder.Property(v => v.DecimalValue).HasColumnType("numeric(19,4)");
        builder.HasIndex(v => new { v.FieldDefinitionId, v.DecimalValue, v.FormRecordId }).HasDatabaseName("ix_field_decimal_values_lookup");
    }
}

public sealed class FieldDateValueConfiguration : IEntityTypeConfiguration<FieldDateValue>
{
    public void Configure(EntityTypeBuilder<FieldDateValue> builder)
    {
        FieldValueConfiguration.ConfigureValueRow(builder, "field_date_values", "field_date_values");
        builder.HasKey(v => v.FieldDateValueId);
        builder.Property(v => v.DateValue).HasColumnType("date");
        builder.HasIndex(v => new { v.FieldDefinitionId, v.DateValue, v.FormRecordId }).HasDatabaseName("ix_field_date_values_lookup");
    }
}

public sealed class FieldBooleanValueConfiguration : IEntityTypeConfiguration<FieldBooleanValue>
{
    public void Configure(EntityTypeBuilder<FieldBooleanValue> builder)
    {
        FieldValueConfiguration.ConfigureValueRow(builder, "field_boolean_values", "field_boolean_values");
        builder.HasKey(v => v.FieldBooleanValueId);
        builder.HasIndex(v => new { v.FieldDefinitionId, v.BooleanValue, v.FormRecordId }).HasDatabaseName("ix_field_boolean_values_lookup");
    }
}

public sealed class FieldOptionValueConfiguration : IEntityTypeConfiguration<FieldOptionValue>
{
    public void Configure(EntityTypeBuilder<FieldOptionValue> builder)
    {
        FieldValueConfiguration.ConfigureValueRow(builder, "field_option_values", "field_option_values");
        builder.HasKey(v => v.FieldOptionValueId);
        builder.HasOne<FieldOption>().WithMany().HasForeignKey(v => v.FieldOptionId).HasConstraintName("fk_field_option_values_option");
        builder.HasIndex(v => new { v.FieldDefinitionId, v.FieldOptionId, v.FormRecordId }).HasDatabaseName("ix_field_option_values_lookup");
    }
}
