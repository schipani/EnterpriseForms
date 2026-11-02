using AuthorizationAuthority.Domain.Features.PlatformOperators;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthorizationAuthority.Infrastructure.Persistence.Configurations;

// C8 onboarding (C-104). Platform-scoped tables: a request reserves an organization id and owner user ids before the organization
// exists, so there is no organization foreign key. Reserved ids, open names, idempotency keys and evidence are unique; closed
// requests and closed approvals never change again (trigger in the migration).

public sealed class OrganizationOnboardingRequestConfiguration : IEntityTypeConfiguration<OrganizationOnboardingRequest>
{
    public void Configure(EntityTypeBuilder<OrganizationOnboardingRequest> builder)
    {
        builder.MapSnakeCase("organization_onboarding_requests");
        builder.HasKey(e => e.OrganizationOnboardingRequestId);
        builder.Property(e => e.OrganizationDisplayName).HasMaxLength(200);
        builder.Property(e => e.OrganizationNormalizedName).HasMaxLength(200);
        builder.Property(e => e.IdentityIssuerUri).HasMaxLength(512);
        builder.Property(e => e.OnboardingStateName).HasMaxLength(30);
        builder.Property(e => e.RequestReasonText).HasMaxLength(500);
        builder.Property(e => e.CreateIdempotencyKey).HasMaxLength(128);
        builder.Property(e => e.IntegrityKeyName).HasMaxLength(128);
        builder.Property(e => e.OnboardingClosureReasonName).HasMaxLength(40);
        builder.Property(e => e.ClosureJustificationText).HasMaxLength(500);
        builder.Property(e => e.LastInitializationFailureCode).HasMaxLength(80);
        builder.HasIndex(e => e.ReservedOrganizationId).IsUnique().HasDatabaseName("uq_onboarding_reserved_organization");
        builder.HasIndex(e => e.IntegrityKeyName).IsUnique().HasDatabaseName("uq_onboarding_integrity_key");
        builder.HasIndex(e => new { e.CreatedByPlatformOperatorId, e.CreateIdempotencyKey }).IsUnique().HasDatabaseName("uq_onboarding_create_idempotency");
        builder.HasIndex(e => e.OrganizationNormalizedName).IsUnique().HasFilter("onboarding_state_name IN ('PendingApproval','AwaitingKeyCustody')").HasDatabaseName("uq_onboarding_open_name");
        builder.HasIndex(e => new { e.RequestCreatedOn, e.OrganizationOnboardingRequestId }).HasDatabaseName("ix_onboarding_requests_recent");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.CreatedByPlatformOperatorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_onboarding_creator");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.InitializedByPlatformOperatorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_onboarding_initializer");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.ClosedByPlatformOperatorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_onboarding_closer");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_onboarding_state", "onboarding_state_name IN ('PendingApproval','AwaitingKeyCustody','Initialized','Rejected','Cancelled','Expired')");
            t.HasCheckConstraint("ck_onboarding_statement", "octet_length(onboarding_statement_sha256) = 32 AND octet_length(create_request_digest) = 32");
            t.HasCheckConstraint("ck_onboarding_expiry", "request_expires_on > request_created_on");
            t.HasCheckConstraint("ck_onboarding_approved", "(onboarding_state_name IN ('AwaitingKeyCustody','Initialized')) <= (onboarding_approved_on IS NOT NULL AND key_custody_expires_on IS NOT NULL)");
            t.HasCheckConstraint("ck_onboarding_initialized", "(onboarding_state_name = 'Initialized') = (onboarding_initialized_on IS NOT NULL AND initialized_by_platform_operator_id IS NOT NULL AND initialized_by_security_command_id IS NOT NULL)");
            t.HasCheckConstraint("ck_onboarding_closed", "(onboarding_state_name IN ('Rejected','Cancelled','Expired')) = (onboarding_closed_on IS NOT NULL AND onboarding_closure_reason_name IS NOT NULL)");
            t.HasCheckConstraint("ck_onboarding_revision", "onboarding_row_revision > 0");
        });
    }
}

public sealed class OrganizationOnboardingOwnerConfiguration : IEntityTypeConfiguration<OrganizationOnboardingOwner>
{
    public void Configure(EntityTypeBuilder<OrganizationOnboardingOwner> builder)
    {
        builder.MapSnakeCase("organization_onboarding_owners");
        builder.HasKey(e => e.OrganizationOnboardingOwnerId);
        builder.Property(e => e.OwnerIssuerUri).HasMaxLength(512);
        builder.Property(e => e.OwnerSubjectIdentifier).HasMaxLength(512);
        builder.Property(e => e.OwnerDisplayNameHint).HasMaxLength(200);
        builder.Property(e => e.OwnerVerificationMethodName).HasMaxLength(40);
        builder.Property(e => e.OwnerVerificationReferenceText).HasMaxLength(100);
        builder.HasIndex(e => new { e.OrganizationOnboardingRequestId, e.OwnerIssuerUri, e.OwnerSubjectIdentifier }).IsUnique().HasDatabaseName("uq_onboarding_owner_identity");
        builder.HasIndex(e => new { e.OrganizationOnboardingRequestId, e.OwnerOrdinalNumber }).IsUnique().HasDatabaseName("uq_onboarding_owner_ordinal");
        builder.HasIndex(e => e.ReservedAuthorityUserId).IsUnique().HasDatabaseName("uq_onboarding_owner_user");
        builder.HasOne<OrganizationOnboardingRequest>().WithMany().HasForeignKey(e => e.OrganizationOnboardingRequestId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_onboarding_owner_request");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_onboarding_owner_ordinal", "owner_ordinal_number BETWEEN 1 AND 10");
            t.HasCheckConstraint("ck_onboarding_owner_method", "owner_verification_method_name IN ('SupportCallback','ContractSignatory','CustomerAdministratorAttestation')");
        });
    }
}

public sealed class OrganizationOnboardingApprovalConfiguration : IEntityTypeConfiguration<OrganizationOnboardingApproval>
{
    public void Configure(EntityTypeBuilder<OrganizationOnboardingApproval> builder)
    {
        builder.MapSnakeCase("organization_onboarding_approvals");
        builder.HasKey(e => e.OrganizationOnboardingApprovalId);
        builder.Property(e => e.ApprovalJustificationText).HasMaxLength(500);
        builder.Property(e => e.ApprovalEvidenceKindName).HasMaxLength(40);
        builder.Property(e => e.ApprovalEvidenceIdentifier).HasMaxLength(128);
        builder.Property(e => e.ApprovalStateName).HasMaxLength(20);
        builder.Property(e => e.ApprovalClosureReasonName).HasMaxLength(40);
        // One live approval per operator per request: the same operator can never supply both approvals.
        builder.HasIndex(e => new { e.OrganizationOnboardingRequestId, e.PlatformOperatorId }).IsUnique().HasFilter("approval_state_name IN ('Valid','Consumed')").HasDatabaseName("uq_onboarding_approval_operator");
        builder.HasIndex(e => e.ApprovalEvidenceSha256).IsUnique().HasDatabaseName("uq_onboarding_approval_evidence");
        builder.HasIndex(e => e.ApprovalEvidenceIdentifier).IsUnique().HasDatabaseName("uq_onboarding_approval_evidence_id");
        builder.HasIndex(e => new { e.OrganizationOnboardingRequestId, e.ApprovalRecordedOn }).HasDatabaseName("ix_onboarding_approvals_request");
        builder.HasOne<OrganizationOnboardingRequest>().WithMany().HasForeignKey(e => e.OrganizationOnboardingRequestId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_onboarding_approval_request");
        builder.HasOne<PlatformOperator>().WithMany().HasForeignKey(e => e.PlatformOperatorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_onboarding_approval_operator");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_onboarding_approval_state", "approval_state_name IN ('Valid','Consumed','Withdrawn','Invalidated')");
            t.HasCheckConstraint("ck_onboarding_approval_digests", "octet_length(approval_statement_sha256) = 32 AND octet_length(approval_evidence_sha256) = 32");
            t.HasCheckConstraint("ck_onboarding_approval_closed", "(approval_state_name IN ('Withdrawn','Invalidated')) = (approval_closed_on IS NOT NULL AND approval_closure_reason_name IS NOT NULL)");
        });
    }
}
