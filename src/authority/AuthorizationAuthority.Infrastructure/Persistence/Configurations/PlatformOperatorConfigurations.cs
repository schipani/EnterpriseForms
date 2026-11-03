using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthorizationAuthority.Infrastructure.Persistence.Configurations;

// C8 M2 Stages 1-2 (D8-34…D8-44). Platform-scoped tables: no organization foreign key except on approvals (which belong to one
// organization's request). Identities, approval keys and evidence are unique forever; closed approvals never change again.

public sealed class PlatformOperatorConfiguration : IEntityTypeConfiguration<PlatformOperator>
{
    public void Configure(EntityTypeBuilder<PlatformOperator> builder)
    {
        builder.MapSnakeCase("platform_operators");
        builder.HasKey(e => e.PlatformOperatorId);
        builder.Property(e => e.OperatorIssuerUri).HasMaxLength(512);
        builder.Property(e => e.OperatorSubjectIdentifier).HasMaxLength(512);
        builder.Property(e => e.OperatorDisplayName).HasMaxLength(200);
        builder.Property(e => e.OperatorStatusName).HasMaxLength(20);
        builder.Property(e => e.OperatorEvidenceKindName).HasMaxLength(40);
        builder.Property(e => e.SyntheticApprovalKeyIdentifier).HasMaxLength(64);
        builder.Property(e => e.OperatorStatusReasonText).HasMaxLength(500);
        builder.HasIndex(e => new { e.OperatorIssuerUri, e.OperatorSubjectIdentifier }).IsUnique().HasDatabaseName("uq_platform_operator_identity");
        builder.HasIndex(e => e.SyntheticApprovalKeyIdentifier).IsUnique().HasFilter("synthetic_approval_key_identifier IS NOT NULL").HasDatabaseName("uq_platform_operator_key");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.ProvisionedByPlatformOperatorId).HasConstraintName("fk_platform_operator_provisioner");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_platform_operator_status", "operator_status_name IN ('Active','Suspended','Removed')");
            t.HasCheckConstraint("ck_platform_operator_evidence_kind", "operator_evidence_kind_name IN ('SyntheticDevelopmentJws','EntraTransactionStepUp')");
            t.HasCheckConstraint("ck_platform_operator_synthetic_key", "operator_evidence_kind_name <> 'SyntheticDevelopmentJws' OR (synthetic_approval_key_identifier IS NOT NULL AND synthetic_approval_public_key IS NOT NULL)");
            t.HasCheckConstraint("ck_platform_operator_revision", "operator_row_revision > 0 AND last_platform_journal_revision >= 0");
        });
    }
}

public sealed class PlatformOperatorChangeProposalConfiguration : IEntityTypeConfiguration<PlatformOperatorChangeProposal>
{
    public void Configure(EntityTypeBuilder<PlatformOperatorChangeProposal> builder)
    {
        builder.MapSnakeCase("platform_operator_change_proposals");
        builder.HasKey(e => e.PlatformOperatorChangeProposalId);
        builder.Property(e => e.ProposedChangeName).HasMaxLength(20);
        builder.Property(e => e.TargetIssuerUri).HasMaxLength(512);
        builder.Property(e => e.TargetSubjectIdentifier).HasMaxLength(512);
        builder.Property(e => e.TargetDisplayName).HasMaxLength(200);
        builder.Property(e => e.TargetEvidenceKindName).HasMaxLength(40);
        builder.Property(e => e.TargetSyntheticKeyIdentifier).HasMaxLength(64);
        builder.Property(e => e.ProposalJustificationText).HasMaxLength(500);
        builder.Property(e => e.ProposalStateName).HasMaxLength(20);
        builder.HasIndex(e => e.ProposalCreatedOn).HasDatabaseName("ix_platform_operator_proposals_recent");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.ProposedByPlatformOperatorId).HasConstraintName("fk_platform_proposal_proposer");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.ConfirmedByPlatformOperatorId).HasConstraintName("fk_platform_proposal_confirmer");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_platform_proposal_change", "proposed_change_name IN ('Provision','Reinstate','Remove')");
            t.HasCheckConstraint("ck_platform_proposal_state", "proposal_state_name IN ('Pending','Consumed')");
            t.HasCheckConstraint("ck_platform_proposal_consumed", "(proposal_state_name = 'Consumed') = (confirmed_by_platform_operator_id IS NOT NULL AND proposal_consumed_on IS NOT NULL)");
            t.HasCheckConstraint("ck_platform_proposal_two_operators", "confirmed_by_platform_operator_id IS NULL OR confirmed_by_platform_operator_id <> proposed_by_platform_operator_id");
            t.HasCheckConstraint("ck_platform_proposal_statement", "octet_length(proposal_statement_sha256) = 32");
            t.HasCheckConstraint("ck_platform_proposal_expiry", "proposal_expires_on > proposal_created_on");
        });
    }
}

public sealed class PlatformOperatorApprovalConfiguration : IEntityTypeConfiguration<PlatformOperatorApproval>
{
    public void Configure(EntityTypeBuilder<PlatformOperatorApproval> builder)
    {
        builder.MapSnakeCase("platform_operator_approvals");
        builder.HasKey(e => e.PlatformOperatorApprovalId);
        builder.Property(e => e.ApprovalJustificationText).HasMaxLength(500);
        builder.Property(e => e.ApprovalEvidenceKindName).HasMaxLength(40);
        builder.Property(e => e.ApprovalStateName).HasMaxLength(20);
        builder.Property(e => e.ApprovalClosureReasonName).HasMaxLength(40);
        // D8-36: at most one Pending approval per request; the second valid approval completes the membership instead.
        builder.HasIndex(e => e.OrganizationMembershipRequestId).IsUnique().HasFilter("approval_state_name = 'Pending'").HasDatabaseName("uq_platform_approval_pending_request");
        builder.HasIndex(e => new { e.OrganizationMembershipRequestId, e.ApprovalRecordedOn }).HasDatabaseName("ix_platform_approvals_request");
        builder.HasIndex(e => new { e.PlatformOperatorId, e.ApprovalStateName }).HasDatabaseName("ix_platform_approvals_operator");
        builder.HasIndex(e => e.ApprovalEvidenceSha256).IsUnique().HasDatabaseName("uq_platform_approval_evidence");
        builder.HasOne<OrganizationAuthorizationState>().WithMany().HasForeignKey(e => e.OrganizationId).HasPrincipalKey(o => o.OrganizationId).HasConstraintName("fk_platform_approval_org");
        builder.HasOne<OrganizationMembershipRequest>().WithMany().HasForeignKey(e => e.OrganizationMembershipRequestId).HasConstraintName("fk_platform_approval_request");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.PlatformOperatorId).HasConstraintName("fk_platform_approval_operator");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_platform_approval_state", "approval_state_name IN ('Pending','Consumed','Withdrawn','Invalidated')");
            t.HasCheckConstraint("ck_platform_approval_closure", "(approval_state_name = 'Pending') = (approval_closed_on IS NULL AND approval_closure_reason_name IS NULL)");
            t.HasCheckConstraint("ck_platform_approval_digests", "octet_length(approval_statement_sha256) = 32 AND octet_length(approval_evidence_sha256) = 32");
            t.HasCheckConstraint("ck_platform_approval_window", "approval_valid_until > approval_recorded_on");
            t.HasCheckConstraint("ck_platform_approval_statement_version", "approval_statement_version = 2");
        });
    }
}

public sealed class PlatformOperatorEvidenceUseConfiguration : IEntityTypeConfiguration<PlatformOperatorEvidenceUse>
{
    public void Configure(EntityTypeBuilder<PlatformOperatorEvidenceUse> builder)
    {
        builder.MapSnakeCase("platform_operator_evidence_uses");
        builder.HasKey(e => e.PlatformOperatorEvidenceUseId);
        builder.Property(e => e.EvidenceUseIdentifier).HasMaxLength(200);
        builder.Property(e => e.EvidenceDecisionName).HasMaxLength(20);
        builder.HasIndex(e => e.EvidenceUseIdentifier).IsUnique().HasDatabaseName("uq_platform_evidence_identifier");
        builder.HasIndex(e => e.EvidenceUseSha256).IsUnique().HasDatabaseName("uq_platform_evidence_digest");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.PlatformOperatorId).HasConstraintName("fk_platform_evidence_operator");
        builder.ToTable(t => t.HasCheckConstraint("ck_platform_evidence_digest", "octet_length(evidence_use_sha256) = 32"));
    }
}

public sealed class PlatformJournalHeadConfiguration : IEntityTypeConfiguration<PlatformJournalHead>
{
    public void Configure(EntityTypeBuilder<PlatformJournalHead> builder)
    {
        builder.MapSnakeCase("platform_journal_heads");
        builder.HasKey(e => e.PlatformJournalHeadId);
        builder.Property(e => e.PlatformQuarantineReasonCode).HasMaxLength(100);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_platform_journal_single_head", $"platform_journal_head_id = '{PlatformJournalHead.SingletonId:D}'");
            t.HasCheckConstraint("ck_platform_journal_head_revision", "platform_journal_revision >= 0 AND (latest_platform_journal_digest IS NULL OR octet_length(latest_platform_journal_digest) = 32)");
        });
    }
}

public sealed class PlatformJournalEntryConfiguration : IEntityTypeConfiguration<PlatformJournalEntry>
{
    public void Configure(EntityTypeBuilder<PlatformJournalEntry> builder)
    {
        builder.MapSnakeCase("platform_journal_entries");
        builder.HasKey(e => e.PlatformJournalEntryId);
        builder.Property(e => e.JournalEventKind).HasMaxLength(60);
        // text, NOT jsonb: the body must stay byte-exact for its digest, the archive and replay (as for the organization journal, A-F12).
        builder.Property(e => e.JournalEventBodyJson).HasColumnType("text");
        builder.Property(e => e.ArchiveDeliveryState).HasMaxLength(30);
        builder.Property(e => e.ArchiveReceiptReference).HasMaxLength(512);
        builder.Property(e => e.LastArchiveErrorCode).HasMaxLength(100);
        builder.HasIndex(e => e.PlatformJournalRevision).IsUnique().HasDatabaseName("uq_platform_journal_revision");
        builder.HasIndex(e => e.ArchiveDeliveryState).HasDatabaseName("ix_platform_journal_pending_archive");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_platform_journal_digest", "octet_length(journal_event_digest) = 32 AND platform_journal_revision > 0");
            t.HasCheckConstraint("ck_platform_journal_archive_state", "archive_delivery_state IN ('Pending','Anchored','VerificationFailed')");
        });
    }
}
