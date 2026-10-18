using Microsoft.EntityFrameworkCore;

namespace EnterpriseForms.Infrastructure.Persistence;

/// <summary>
/// Single business DbContext and migration stream (ADR-ARCH-001 §4U.1). Feature-owned
/// <see cref="IEntityTypeConfiguration{TEntity}"/> classes under Features/&lt;Feature&gt;/ are applied here.
/// Phase 0 registers no entities; Phase 1 adds them with the naming rules enforced by
/// <see cref="Conventions.ModelNamingValidator"/> in the architecture tests.
/// </summary>
public sealed class BusinessDbContext(DbContextOptions<BusinessDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BusinessDbContext).Assembly);
    }
}
