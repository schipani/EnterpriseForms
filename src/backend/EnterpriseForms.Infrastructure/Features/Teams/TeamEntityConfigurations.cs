using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Teams;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.Teams;

public sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.MapSnakeCase("teams");
        builder.HasKey(t => t.TeamId);
        builder.Property(t => t.TeamName).HasMaxLength(200);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).HasConstraintName("fk_teams_tenant");
    }
}

public sealed class TeamMembershipConfiguration : IEntityTypeConfiguration<TeamMembership>
{
    public void Configure(EntityTypeBuilder<TeamMembership> builder)
    {
        builder.MapSnakeCase("team_memberships");
        builder.HasKey(m => m.TeamMembershipId);
        builder.Property(m => m.MembershipRole).HasMaxLength(30);
        builder.HasIndex(m => new { m.TeamId, m.UserId }).IsUnique().HasFilter("removed_on IS NULL").HasDatabaseName("uq_team_memberships_active");
        builder.HasOne<Team>().WithMany().HasForeignKey(m => m.TeamId).HasConstraintName("fk_team_memberships_team");
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).HasConstraintName("fk_team_memberships_user");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).HasConstraintName("fk_team_memberships_tenant");
    }
}
