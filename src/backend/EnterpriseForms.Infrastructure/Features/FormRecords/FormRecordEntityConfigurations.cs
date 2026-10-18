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
        builder.HasIndex(r => new { r.TenantId, r.FormDefinitionId }).HasDatabaseName("ix_form_records_tenant_definition");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_form_records_status", "record_status IN ('Draft','Submitted','Archived','Deleted')");
            // Exactly-one-owner projection invariant (CLAUDE.md §4B); the authority holds the authoritative owner edge.
            t.HasCheckConstraint("ck_form_records_single_owner", "(owner_user_id IS NOT NULL AND owner_team_id IS NULL) OR (owner_user_id IS NULL AND owner_team_id IS NOT NULL)");
            t.HasCheckConstraint("ck_form_records_revision_positive", "record_revision >= 1");
        });
    }
}

public sealed class FormRecordRevisionConfiguration : IEntityTypeConfiguration<FormRecordRevision>
{
    public void Configure(EntityTypeBuilder<FormRecordRevision> builder)
    {
        builder.MapSnakeCase("form_record_revisions");
        builder.HasKey(r => r.FormRecordRevisionId);
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
        });
    }
}

public sealed class FieldStringValueConfiguration : IEntityTypeConfiguration<FieldStringValue>
{
    public void Configure(EntityTypeBuilder<FieldStringValue> builder)
    {
        builder.MapSnakeCase("field_string_values");
        builder.HasKey(v => v.FieldStringValueId);
        // One occurrence per (operation, record, field, group) — NULLS NOT DISTINCT so top-level duplicates are rejected (CLAUDE.md §2.4).
        builder.HasIndex(v => new { v.ContentChangeOperationId, v.FormRecordId, v.FieldDefinitionId, v.FormRecordGroupId }).IsUnique().AreNullsDistinct(false).HasDatabaseName("uq_field_string_values_occurrence");
        builder.HasIndex(v => new { v.FormRecordId, v.ContentChangeOperationId }).HasDatabaseName("ix_field_string_values_record_operation");
        builder.HasIndex(v => new { v.FieldDefinitionId, v.StringValue, v.FormRecordId }).HasDatabaseName("ix_field_string_values_lookup");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(v => v.FormRecordId).HasConstraintName("fk_field_string_values_record");
        builder.HasOne<FieldDefinition>().WithMany().HasForeignKey(v => v.FieldDefinitionId).HasConstraintName("fk_field_string_values_field");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).HasConstraintName("fk_field_string_values_tenant");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_field_string_values_signature_length", "octet_length(row_signature) = 32");
            t.HasCheckConstraint("ck_field_string_values_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
        });
    }
}
