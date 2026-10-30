using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.SecurityOperations;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.SecurityOperations;

/// <summary>
/// Security monitoring (PG-056): incident triage store, owned by SecurityOperations. Untrusted projection class: the
/// independent evidence of a confirmed mismatch is the Authority's journal and quarantine. No foreign key to
/// form_records on purpose — an incident must survive the deletion of the row it is about.
/// </summary>
public sealed class SecurityIncidentConfiguration : IEntityTypeConfiguration<SecurityIncident>
{
    public void Configure(EntityTypeBuilder<SecurityIncident> builder)
    {
        builder.MapSnakeCase("security_incidents");
        builder.HasKey(i => i.SecurityIncidentId);
        builder.Property(i => i.IncidentTypeName).HasMaxLength(40);
        builder.Property(i => i.IncidentSeverityName).HasMaxLength(20);
        builder.Property(i => i.IncidentStatusName).HasMaxLength(20);
        builder.Property(i => i.IncidentDeduplicationKey).HasMaxLength(120);
        builder.Property(i => i.DetectionSourceName).HasMaxLength(40);
        builder.Property(i => i.VerificationErrorCode).HasMaxLength(80);
        builder.Property(i => i.ResolutionExplanationText).HasMaxLength(SecurityIncidentNote.MaximumNoteLength);
        builder.HasIndex(i => new { i.TenantId, i.IncidentDeduplicationKey, i.FirstDetectedOn }).HasDatabaseName("ix_security_incidents_deduplication");
        builder.HasIndex(i => new { i.TenantId, i.LastDetectedOn, i.SecurityIncidentId }).HasDatabaseName("ix_security_incidents_recent");
        builder.HasIndex(i => new { i.TenantId, i.FormRecordId }).HasDatabaseName("ix_security_incidents_record");
        // At most one active (Open/Investigating) incident per problem: backstop for the advisory-lock serialization.
        builder.HasIndex(i => new { i.TenantId, i.IncidentDeduplicationKey }).IsUnique().HasFilter("incident_status_name IN ('Open','Investigating')").HasDatabaseName("uq_security_incidents_active_key");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).HasConstraintName("fk_security_incidents_tenant");
        builder.HasOne<User>().WithMany().HasForeignKey(i => i.AssignedToUserId).HasConstraintName("fk_security_incidents_assignee");
        builder.HasOne<User>().WithMany().HasForeignKey(i => i.ResolvedByUserId).HasConstraintName("fk_security_incidents_resolver");
        builder.HasOne<User>().WithMany().HasForeignKey(i => i.IncidentModifiedByUserId).HasConstraintName("fk_security_incidents_modifier");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_security_incidents_type", "incident_type_name IN ('ConfirmedIntegrityMismatch','IntegrityQuarantine','VerificationServiceUnavailable','OperationalVerificationFailure')");
            t.HasCheckConstraint("ck_security_incidents_severity", "incident_severity_name IN ('Critical','High','Medium','Low')");
            t.HasCheckConstraint("ck_security_incidents_status", "incident_status_name IN ('Open','Investigating','Resolved','FalsePositive')");
            t.HasCheckConstraint("ck_security_incidents_counts", "occurrence_count >= 1 AND incident_revision >= 1 AND last_detected_on >= first_detected_on");
            t.HasCheckConstraint("ck_security_incidents_resolution", "(incident_status_name IN ('Resolved','FalsePositive')) = (resolved_on IS NOT NULL AND resolved_by_user_id IS NOT NULL AND resolution_explanation_text IS NOT NULL)");
            t.HasCheckConstraint("ck_security_incidents_error_code", "verification_error_code ~ '^[A-Za-z0-9_.:-]{0,80}$'");
        });
    }
}

public sealed class SecurityIncidentOccurrenceConfiguration : IEntityTypeConfiguration<SecurityIncidentOccurrence>
{
    public void Configure(EntityTypeBuilder<SecurityIncidentOccurrence> builder)
    {
        builder.MapSnakeCase("security_incident_occurrences");
        builder.HasKey(o => o.SecurityIncidentOccurrenceId);
        builder.Property(o => o.ObservedIncidentTypeName).HasMaxLength(40);
        builder.Property(o => o.DetectionSourceName).HasMaxLength(40);
        builder.Property(o => o.VerificationErrorCode).HasMaxLength(80);
        builder.HasIndex(o => new { o.SecurityIncidentId, o.OccurrenceDetectedOn }).HasDatabaseName("ix_security_incident_occurrences_incident");
        builder.HasOne<SecurityIncident>().WithMany().HasForeignKey(o => o.SecurityIncidentId).HasConstraintName("fk_security_incident_occurrences_incident");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(o => o.TenantId).HasConstraintName("fk_security_incident_occurrences_tenant");
        builder.HasOne<User>().WithMany().HasForeignKey(o => o.ObservedByUserId).HasConstraintName("fk_security_incident_occurrences_observer");
        builder.ToTable(t => t.HasCheckConstraint("ck_security_incident_occurrences_type", "observed_incident_type_name IN ('ConfirmedIntegrityMismatch','IntegrityQuarantine','VerificationServiceUnavailable','OperationalVerificationFailure')"));
    }
}

public sealed class SecurityIncidentNoteConfiguration : IEntityTypeConfiguration<SecurityIncidentNote>
{
    public void Configure(EntityTypeBuilder<SecurityIncidentNote> builder)
    {
        builder.MapSnakeCase("security_incident_notes");
        builder.HasKey(n => n.SecurityIncidentNoteId);
        builder.Property(n => n.InvestigationNoteText).HasMaxLength(SecurityIncidentNote.MaximumNoteLength);
        builder.HasIndex(n => new { n.SecurityIncidentId, n.NoteCreatedOn }).HasDatabaseName("ix_security_incident_notes_incident");
        builder.HasOne<SecurityIncident>().WithMany().HasForeignKey(n => n.SecurityIncidentId).HasConstraintName("fk_security_incident_notes_incident");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(n => n.TenantId).HasConstraintName("fk_security_incident_notes_tenant");
        builder.HasOne<User>().WithMany().HasForeignKey(n => n.NoteAuthorUserId).HasConstraintName("fk_security_incident_notes_author");
        builder.ToTable(t => t.HasCheckConstraint("ck_security_incident_notes_text", "char_length(investigation_note_text) BETWEEN 1 AND 2000"));
    }
}
