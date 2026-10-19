using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.Documents;

public sealed class UploadSessionConfiguration : IEntityTypeConfiguration<UploadSession>
{
    public void Configure(EntityTypeBuilder<UploadSession> builder)
    {
        builder.MapSnakeCase("upload_sessions");
        builder.HasKey(s => s.UploadSessionId);
        builder.Property(s => s.SessionIdempotencyKey).HasMaxLength(100);
        builder.Property(s => s.OriginalFileName).HasMaxLength(255);
        builder.Property(s => s.DeclaredContentType).HasMaxLength(127);
        builder.Property(s => s.StagingObjectKey).HasMaxLength(512);
        builder.Property(s => s.SessionState).HasMaxLength(20);
        builder.Property(s => s.FailureReasonCode).HasMaxLength(60);
        builder.HasIndex(s => new { s.TenantId, s.OwnerUserId, s.SessionIdempotencyKey }).IsUnique().HasDatabaseName("uq_upload_sessions_owner_idempotency");
        builder.HasIndex(s => new { s.FormRecordId, s.SessionState }).HasDatabaseName("ix_upload_sessions_record_state");
        builder.HasIndex(s => new { s.SessionState, s.SessionExpiresOn }).HasDatabaseName("ix_upload_sessions_staging_purge").HasFilter("staging_object_purged_on IS NULL");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).HasConstraintName("fk_upload_sessions_tenant");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(s => s.FormRecordId).HasConstraintName("fk_upload_sessions_record");
        builder.HasOne<FieldDefinition>().WithMany().HasForeignKey(s => s.FieldDefinitionId).HasConstraintName("fk_upload_sessions_field");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_upload_sessions_state", "session_state IN ('Open','Completed','Processing','Processed','Committed','Rejected','Failed','Cancelled','Expired')");
            t.HasCheckConstraint("ck_upload_sessions_sizes", "declared_size_bytes >= 1 AND chunk_size_bytes >= 1 AND expected_chunk_count >= 1 AND received_bytes >= 0");
        });
    }
}

public sealed class FileAttachmentConfiguration : IEntityTypeConfiguration<FileAttachment>
{
    public void Configure(EntityTypeBuilder<FileAttachment> builder)
    {
        builder.MapSnakeCase("file_attachments");
        builder.HasKey(a => a.FileAttachmentId);
        builder.Property(a => a.OriginalFileName).HasMaxLength(255);
        builder.Property(a => a.AttachmentLifecycleState).HasMaxLength(20);
        builder.HasIndex(a => new { a.FormRecordId, a.FieldDefinitionId }).HasDatabaseName("ix_file_attachments_record_field");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).HasConstraintName("fk_file_attachments_tenant");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(a => a.FormRecordId).HasConstraintName("fk_file_attachments_record");
        builder.HasOne<FieldDefinition>().WithMany().HasForeignKey(a => a.FieldDefinitionId).HasConstraintName("fk_file_attachments_field");
        builder.ToTable(t => t.HasCheckConstraint("ck_file_attachments_lifecycle", "attachment_lifecycle_state IN ('Ready','Removed')"));
    }
}

public sealed class FileAttachmentVersionConfiguration : IEntityTypeConfiguration<FileAttachmentVersion>
{
    public void Configure(EntityTypeBuilder<FileAttachmentVersion> builder)
    {
        builder.MapSnakeCase("file_attachment_versions");
        builder.HasKey(v => v.FileAttachmentVersionId);
        builder.Ignore(v => v.EntityKind).Ignore(v => v.ItemKindCode).Ignore(v => v.ItemId).Ignore(v => v.RowPrimaryKey);
        builder.Property(v => v.OriginalFileName).HasMaxLength(255);
        builder.Property(v => v.DetectedContentType).HasMaxLength(127);
        builder.Property(v => v.StorageProviderName).HasMaxLength(40);
        builder.Property(v => v.StorageObjectKey).HasMaxLength(512);
        builder.Property(v => v.EncryptionAlgorithmName).HasMaxLength(40);
        builder.Property(v => v.EncryptionKeyReference).HasMaxLength(200);
        builder.Property(v => v.ScanVerdict).HasMaxLength(20);
        builder.Property(v => v.ScannerName).HasMaxLength(100);
        builder.Property(v => v.ScanSignatureName).HasMaxLength(200);
        builder.Property(v => v.VersionState).HasMaxLength(20);
        builder.HasIndex(v => new { v.FileAttachmentId, v.VersionNumber, v.ContentChangeOperationId }).IsUnique().HasDatabaseName("uq_file_attachment_versions_number_operation");
        builder.HasIndex(v => new { v.FormRecordId, v.ContentChangeOperationId }).HasDatabaseName("ix_file_attachment_versions_record_operation");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).HasConstraintName("fk_file_attachment_versions_tenant");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(v => v.FormRecordId).HasConstraintName("fk_file_attachment_versions_record");
        builder.HasOne<FileAttachment>().WithMany().HasForeignKey(v => v.FileAttachmentId).HasConstraintName("fk_file_attachment_versions_attachment");
        builder.HasOne<FieldDefinition>().WithMany().HasForeignKey(v => v.FieldDefinitionId).HasConstraintName("fk_file_attachment_versions_field");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_file_attachment_versions_digests", "octet_length(plaintext_sha256) = 32 AND octet_length(ciphertext_sha256) = 32");
            t.HasCheckConstraint("ck_file_attachment_versions_scan", "scan_verdict IN ('Clean','Infected','ScanError')");
            t.HasCheckConstraint("ck_file_attachment_versions_state", "version_state IN ('Processed','Committed','Superseded','Removed')");
            // The processed (pre-commit) copy has operation 00000000-… and no signature; every operation-bound copy carries 32 bytes (B-05).
            t.HasCheckConstraint("ck_file_attachment_versions_signature_length", "(content_change_operation_id = '00000000-0000-0000-0000-000000000000' AND octet_length(row_signature) = 0) OR octet_length(row_signature) = 32");
        });
    }
}

public sealed class DocumentProcessingJobConfiguration : IEntityTypeConfiguration<DocumentProcessingJob>
{
    public void Configure(EntityTypeBuilder<DocumentProcessingJob> builder)
    {
        builder.MapSnakeCase("document_processing_jobs");
        builder.HasKey(j => j.DocumentProcessingJobId);
        builder.Property(j => j.JobState).HasMaxLength(20);
        builder.Property(j => j.LeaseOwnerName).HasMaxLength(120);
        builder.Property(j => j.LastErrorCode).HasMaxLength(200);
        builder.HasIndex(j => j.UploadSessionId).IsUnique().HasDatabaseName("uq_document_processing_jobs_session");
        builder.HasIndex(j => new { j.JobState, j.NextAttemptOn }).HasDatabaseName("ix_document_processing_jobs_due");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(j => j.TenantId).HasConstraintName("fk_document_processing_jobs_tenant");
        builder.HasOne<UploadSession>().WithMany().HasForeignKey(j => j.UploadSessionId).HasConstraintName("fk_document_processing_jobs_session");
        builder.ToTable(t => t.HasCheckConstraint("ck_document_processing_jobs_state", "job_state IN ('Queued','Leased','ScanPending','Processed','Rejected','Failed')"));
    }
}
