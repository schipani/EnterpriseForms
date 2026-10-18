using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Sharing;
using EnterpriseForms.Domain.Features.Teams;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseForms.Infrastructure.Persistence;

/// <summary>
/// Single business DbContext and migration stream (ADR-ARCH-001 §4U.1). Feature-owned
/// <see cref="IEntityTypeConfiguration{TEntity}"/> classes under Features/&lt;Feature&gt;/ are applied here and checked
/// by <see cref="Conventions.ModelNamingValidator"/> in the architecture tests. The business database is UNTRUSTED for
/// authorization: owner/share/team rows are projections of authority decisions (TRUST_AUTHORITY_ARCHITECTURE.md §2).
/// </summary>
public sealed class BusinessDbContext(DbContextOptions<BusinessDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();

    public DbSet<FormDefinition> FormDefinitions => Set<FormDefinition>();

    public DbSet<FormVersion> FormVersions => Set<FormVersion>();

    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();

    public DbSet<FormRecord> FormRecords => Set<FormRecord>();

    public DbSet<FormRecordRevision> FormRecordRevisions => Set<FormRecordRevision>();

    public DbSet<FieldStringValue> FieldStringValues => Set<FieldStringValue>();

    public DbSet<RecordShare> RecordShares => Set<RecordShare>();

    public DbSet<RecordShareField> RecordShareFields => Set<RecordShareField>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BusinessDbContext).Assembly);
    }
}
