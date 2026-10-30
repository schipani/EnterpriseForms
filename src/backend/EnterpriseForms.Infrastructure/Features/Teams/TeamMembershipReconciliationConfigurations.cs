using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Teams;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.Teams;

/// <summary>Phase C8 (C8-M1-01): durable team-membership operations, owned by Teams. Untrusted projection class (D8-18).</summary>
public sealed class AuthorityCommandOperationConfiguration : IEntityTypeConfiguration<AuthorityCommandOperation>
{
    public void Configure(EntityTypeBuilder<AuthorityCommandOperation> builder)
    {
        builder.MapSnakeCase("authority_command_operations");
        builder.HasKey(o => o.AuthorityCommandOperationId);
        builder.Property(o => o.CommandKindName).HasMaxLength(30);
        builder.Property(o => o.AuthorityIdempotencyKey).HasMaxLength(128);
        builder.Property(o => o.OperationStateName).HasMaxLength(40);
        builder.Property(o => o.OperationOutcomeCode).HasMaxLength(40);
        builder.Property(o => o.RejectionReasonCode).HasMaxLength(80);
        builder.Property(o => o.LeaseOwnerName).HasMaxLength(120);
        builder.Property(o => o.LastReconciliationErrorCode).HasMaxLength(80);
        builder.HasIndex(o => new { o.TenantId, o.AuthorityIdempotencyKey }).IsUnique().HasDatabaseName("uq_authority_command_operations_key");
        builder.HasIndex(o => new { o.OperationStateName, o.NextAttemptOn }).HasDatabaseName("ix_authority_command_operations_due");
        builder.HasIndex(o => new { o.TenantId, o.TeamId, o.OperationStateName }).HasDatabaseName("ix_authority_command_operations_team");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(o => o.TenantId).HasConstraintName("fk_authority_command_operations_tenant");
        builder.HasOne<User>().WithMany().HasForeignKey(o => o.InitiatedByUserId).HasConstraintName("fk_authority_command_operations_initiator");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_authority_command_operations_kind", "command_kind_name IN ('AddTeamMember','RemoveTeamMember')");
            t.HasCheckConstraint("ck_authority_command_operations_key_prefix", "(command_kind_name = 'AddTeamMember' AND authority_idempotency_key LIKE 'team-add:%') OR (command_kind_name = 'RemoveTeamMember' AND authority_idempotency_key LIKE 'team-remove:%')");
            t.HasCheckConstraint("ck_authority_command_operations_state", "operation_state_name IN ('AwaitingCommit','AwaitingEffective','AwaitingQuarantineClearance','ReconciliationError','Projected','Rejected','AuthorityFailed','NotCommitted')");
            t.HasCheckConstraint("ck_authority_command_operations_outcome", "operation_outcome_code IS NULL OR operation_outcome_code IN ('MembershipPresentProjected','MembershipAbsentProjected','ProjectionUnchanged','DriftCorrected')");
            t.HasCheckConstraint("ck_authority_command_operations_revisions", "(committed_authorization_revision IS NULL OR committed_authorization_revision >= 1) AND (evaluated_projection_revision IS NULL OR evaluated_projection_revision >= 0) AND (projected_membership_revision IS NULL OR projected_membership_revision >= 0)");
            t.HasCheckConstraint("ck_authority_command_operations_counts", "attempt_count >= 0 AND consecutive_error_count >= 0");
            t.HasCheckConstraint("ck_authority_command_operations_projected_complete", "operation_state_name <> 'Projected' OR (authority_command_id IS NOT NULL AND committed_authorization_revision IS NOT NULL AND evaluated_projection_revision IS NOT NULL AND projected_membership_revision IS NOT NULL AND projected_membership_present IS NOT NULL AND operation_outcome_code IS NOT NULL AND operation_completed_on IS NOT NULL)");
        });
    }
}

/// <summary>Phase C8 (C8-M1-02): per-pair projection high-water mark, owned by Teams. Untrusted projection class (D8-18).</summary>
public sealed class TeamMembershipProjectionStateConfiguration : IEntityTypeConfiguration<TeamMembershipProjectionState>
{
    public void Configure(EntityTypeBuilder<TeamMembershipProjectionState> builder)
    {
        builder.MapSnakeCase("team_membership_projection_states");
        builder.HasKey(s => s.TeamMembershipProjectionStateId);
        builder.HasIndex(s => new { s.TenantId, s.TeamId, s.MemberUserId }).IsUnique().HasDatabaseName("uq_team_membership_projection_states_pair");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).HasConstraintName("fk_team_membership_projection_states_tenant");
        builder.HasOne<Team>().WithMany().HasForeignKey(s => s.TeamId).HasConstraintName("fk_team_membership_projection_states_team");
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.MemberUserId).HasConstraintName("fk_team_membership_projection_states_member");
        builder.HasOne<AuthorityCommandOperation>().WithMany().HasForeignKey(s => s.LastAuthorityCommandOperationId).HasConstraintName("fk_team_membership_projection_states_operation");
        builder.ToTable(t => t.HasCheckConstraint("ck_team_membership_projection_states_revision", "projected_membership_revision >= 0"));
    }
}
