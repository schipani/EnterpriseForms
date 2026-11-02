using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.Identity;

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.MapSnakeCase("tenants");
        builder.HasKey(t => t.TenantId);
        builder.Property(t => t.TenantName).HasMaxLength(200);
        builder.Property(t => t.TenantLifecycleStatus).HasMaxLength(20);
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.MapSnakeCase("users");
        builder.HasKey(u => u.UserId);
        builder.Property(u => u.ExternalIssuerUri).HasMaxLength(512);
        builder.Property(u => u.ExternalSubjectIdentifier).HasMaxLength(512);
        builder.Property(u => u.DisplayName).HasMaxLength(200);
        builder.Property(u => u.EmailAddress).HasMaxLength(320);
        builder.Property(u => u.PreferredName).HasMaxLength(100);
        builder.Property(u => u.JobTitle).HasMaxLength(100);
        builder.Property(u => u.IdentityDisplayName).HasMaxLength(200).HasDefaultValue(string.Empty);
        builder.Property(u => u.IsDisplayNameOverridden).HasDefaultValue(false);
        builder.Property(u => u.ProfileRevision).HasDefaultValue(1L);
        builder.HasIndex(u => new { u.TenantId, u.ExternalIssuerUri, u.ExternalSubjectIdentifier }).IsUnique().HasDatabaseName("uq_users_tenant_subject");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).HasConstraintName("fk_users_tenant");
    }
}

public sealed class UserPresenceConfiguration : IEntityTypeConfiguration<UserPresence>
{
    public void Configure(EntityTypeBuilder<UserPresence> builder)
    {
        builder.MapSnakeCase("user_presences");
        builder.HasKey(p => p.UserPresenceId);
        builder.HasIndex(p => p.UserId).IsUnique().HasDatabaseName("uq_user_presences_user");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).HasConstraintName("fk_user_presences_tenant");
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).HasConstraintName("fk_user_presences_user");
    }
}
