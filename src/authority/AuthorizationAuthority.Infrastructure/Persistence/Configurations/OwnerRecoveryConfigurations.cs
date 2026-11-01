using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthorizationAuthority.Infrastructure.Persistence.Configurations;

// C8 owner recovery (C-105). A case belongs to one existing organization; at most one Open case per organization; closed cases and
// closed approvals never change again (trigger in the migration). Probations are organization-scoped and append-only.

public sealed class OrganizationOwnerRecoveryCaseConfiguration : IEntityTypeConfiguration<OrganizationOwnerRecoveryCase>
{
    public void Configure(EntityTypeBuilder<OrganizationOwnerRecoveryCase> builder)
    {
        builder.MapSnakeCase("organization_owner_recovery_cases");
        builder.HasKey(e => e.OrganizationOwnerRecoveryCaseId);
        builder.Property(e => e.RecoveryStateName).HasMaxLength(20);
        builder.Property(e => e.IncidentReferenceText).HasMaxLength(64);
        builder.Property(e => e.RecoveryReasonText).HasMaxLength(500);
        builder.Property(e => e.CreateIdempotencyKey).HasMaxLength(128);
        builder.Property(e => e.ObjectionReasonText).HasMaxLength(500);
        builder.Property(e => e.CaseClosureReasonName).HasMaxLength(40);
        builder.Property(e => e.ClosureJustificationText).HasMaxLength(500);
        builder.Property(e => e.LastExecutionFailureCode).HasMaxLength(80);
        builder.HasIndex(e => e.OrganizationId).IsUnique().HasFilter("recovery_state_name = 'Open'").HasDatabaseName("uq_owner_recovery_open_case");
        builder.HasIndex(e => new { e.OpenedByPlatformOperatorId, e.CreateIdempotencyKey }).IsUnique().HasDatabaseName("uq_owner_recovery_create_idempotency");
        builder.HasIndex(e => new { e.CaseOpenedOn, e.OrganizationOwnerRecoveryCaseId }).HasDatabaseName("ix_owner_recovery_cases_recent");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_owner_recovery_organization");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.OpenedByPlatformOperatorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_owner_recovery_opener");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.ExecutedByPlatformOperatorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_owner_recovery_executor");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.ClosedByPlatformOperatorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_owner_recovery_closer");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_owner_recovery_state", "recovery_state_name IN ('Open','Objected','Recovered','Cancelled','Expired','Invalidated')");
            t.HasCheckConstraint("ck_owner_recovery_digests", "octet_length(recovery_statement_sha256) = 32 AND octet_length(create_request_digest) = 32 AND octet_length(owner_set_sha256) = 32");
            t.HasCheckConstraint("ck_owner_recovery_times", "waiting_period_ends_on > case_opened_on AND case_expires_on > waiting_period_ends_on");
            t.HasCheckConstraint("ck_owner_recovery_recovered", "(recovery_state_name = 'Recovered') = (recovered_on IS NOT NULL AND executed_by_platform_operator_id IS NOT NULL AND executed_by_security_command_id IS NOT NULL)");
            t.HasCheckConstraint("ck_owner_recovery_objected", "(recovery_state_name = 'Objected') = (objected_on IS NOT NULL AND objected_by_authority_user_id IS NOT NULL AND objection_reason_text IS NOT NULL)");
            t.HasCheckConstraint("ck_owner_recovery_closed", "(recovery_state_name = 'Open') = (case_closed_on IS NULL)");
            t.HasCheckConstraint("ck_owner_recovery_revision", "case_row_revision > 0");
        });
    }
}

public sealed class OrganizationOwnerRecoverySubjectConfiguration : IEntityTypeConfiguration<OrganizationOwnerRecoverySubject>
{
    public void Configure(EntityTypeBuilder<OrganizationOwnerRecoverySubject> builder)
    {
        builder.MapSnakeCase("organization_owner_recovery_subjects");
        builder.HasKey(e => e.OrganizationOwnerRecoverySubjectId);
        builder.Property(e => e.RecoverySubjectRoleName).HasMaxLength(30);
        builder.Property(e => e.SubjectIssuerUri).HasMaxLength(512);
        builder.Property(e => e.SubjectIdentifier).HasMaxLength(512);
        builder.Property(e => e.SubjectDisplayNameHint).HasMaxLength(200);
        builder.Property(e => e.VerificationMethodName).HasMaxLength(40);
        builder.Property(e => e.VerificationReferenceText).HasMaxLength(100);
        builder.Property(e => e.UnavailabilityReasonName).HasMaxLength(40);
        builder.Property(e => e.EvidenceReferenceText).HasMaxLength(100);
        builder.Property(e => e.EvidenceSummaryText).HasMaxLength(500);
        builder.HasIndex(e => new { e.OrganizationOwnerRecoveryCaseId, e.RecoverySubjectRoleName, e.SubjectAuthorityUserId }).IsUnique().HasDatabaseName("uq_owner_recovery_subject_user");
        builder.HasIndex(e => new { e.OrganizationOwnerRecoveryCaseId, e.SubjectIssuerUri, e.SubjectIdentifier }).IsUnique().HasDatabaseName("uq_owner_recovery_subject_identity");
        builder.HasOne<OrganizationOwnerRecoveryCase>().WithMany().HasForeignKey(e => e.OrganizationOwnerRecoveryCaseId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_owner_recovery_subject_case");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_owner_recovery_subject_role", "recovery_subject_role_name IN ('Replacement','DeclaredUnavailable')");
            t.HasCheckConstraint("ck_owner_recovery_subject_replacement", "recovery_subject_role_name <> 'Replacement' OR (verification_method_name IS NOT NULL AND verification_reference_text IS NOT NULL)");
            t.HasCheckConstraint("ck_owner_recovery_subject_declared", "recovery_subject_role_name <> 'DeclaredUnavailable' OR (NOT is_new_identity AND unavailability_reason_name IS NOT NULL AND evidence_reference_text IS NOT NULL AND evidence_summary_text IS NOT NULL)");
        });
    }
}

public sealed class OrganizationOwnerRecoveryApprovalConfiguration : IEntityTypeConfiguration<OrganizationOwnerRecoveryApproval>
{
    public void Configure(EntityTypeBuilder<OrganizationOwnerRecoveryApproval> builder)
    {
        builder.MapSnakeCase("organization_owner_recovery_approvals");
        builder.HasKey(e => e.OrganizationOwnerRecoveryApprovalId);
        builder.Property(e => e.ApprovalJustificationText).HasMaxLength(500);
        builder.Property(e => e.ApprovalEvidenceKindName).HasMaxLength(40);
        builder.Property(e => e.ApprovalEvidenceIdentifier).HasMaxLength(128);
        builder.Property(e => e.ApprovalStateName).HasMaxLength(20);
        builder.Property(e => e.ApprovalClosureReasonName).HasMaxLength(40);
        // One live approval per operator per case: no operator can supply two of the three approvals.
        builder.HasIndex(e => new { e.OrganizationOwnerRecoveryCaseId, e.PlatformOperatorId }).IsUnique().HasFilter("approval_state_name IN ('Valid','Consumed')").HasDatabaseName("uq_owner_recovery_approval_operator");
        builder.HasIndex(e => e.ApprovalEvidenceSha256).IsUnique().HasDatabaseName("uq_owner_recovery_approval_evidence");
        builder.HasIndex(e => e.ApprovalEvidenceIdentifier).IsUnique().HasDatabaseName("uq_owner_recovery_approval_evidence_id");
        builder.HasOne<OrganizationOwnerRecoveryCase>().WithMany().HasForeignKey(e => e.OrganizationOwnerRecoveryCaseId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_owner_recovery_approval_case");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.PlatformOperatorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_owner_recovery_approval_operator");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_owner_recovery_approval_state", "approval_state_name IN ('Valid','Consumed','Withdrawn','Invalidated')");
            t.HasCheckConstraint("ck_owner_recovery_approval_digests", "octet_length(approval_statement_sha256) = 32 AND octet_length(approval_evidence_sha256) = 32");
            t.HasCheckConstraint("ck_owner_recovery_approval_closed", "(approval_state_name IN ('Withdrawn','Invalidated')) = (approval_closed_on IS NOT NULL AND approval_closure_reason_name IS NOT NULL)");
        });
    }
}

public sealed class OrganizationRecoveredOwnerProbationConfiguration : IEntityTypeConfiguration<OrganizationRecoveredOwnerProbation>
{
    public void Configure(EntityTypeBuilder<OrganizationRecoveredOwnerProbation> builder)
    {
        builder.MapSnakeCase("organization_recovered_owner_probations");
        builder.HasKey(e => e.OrganizationRecoveredOwnerProbationId);
        builder.HasIndex(e => new { e.OrganizationId, e.AuthorityUserId, e.OrganizationOwnerRecoveryCaseId }).IsUnique().HasDatabaseName("uq_recovered_owner_probation");
        builder.HasIndex(e => new { e.OrganizationId, e.ProbationEndsOn }).HasDatabaseName("ix_recovered_owner_probations_active");
        builder.HasOne<OrganizationOwnerRecoveryCase>().WithMany().HasForeignKey(e => e.OrganizationOwnerRecoveryCaseId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_recovered_owner_probation_case");
        builder.ToTable(t => t.HasCheckConstraint("ck_recovered_owner_probation_period", "probation_ends_on > probation_created_on"));
    }
}
