using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.Audit;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.MapSnakeCase("audit_events");
        builder.HasKey(e => e.AuditEventId);
        builder.Property(e => e.AuditSequence).UseIdentityAlwaysColumn();
        builder.HasAlternateKey(e => e.AuditSequence).HasName("uq_audit_events_sequence");
        builder.Property(e => e.EventCategory).HasMaxLength(40);
        builder.Property(e => e.EventAction).HasMaxLength(60);
        builder.Property(e => e.EventOutcome).HasMaxLength(20);
        builder.Property(e => e.ActorType).HasMaxLength(20);
        builder.Property(e => e.TargetEntityType).HasMaxLength(60);
        builder.Property(e => e.EventDescription).HasMaxLength(500);
        builder.Property(e => e.EventMetadataJson).HasColumnType("jsonb");
        builder.HasIndex(e => new { e.TenantId, e.EventOccurredOn, e.AuditSequence }).HasDatabaseName("ix_audit_events_tenant_time");
        builder.HasIndex(e => new { e.FormRecordId, e.EventOccurredOn }).HasDatabaseName("ix_audit_events_record_time");
        // C8 M4-2 (AC-8 §7): the audit explorer's actor and correlation filters. A tenant-scoped record-time index is added only
        // if EXPLAIN (ANALYZE, BUFFERS) on a seeded 1M-row organization requires it (measurement deferred by the owner, not waived).
        builder.HasIndex(e => new { e.TenantId, e.ActorUserId, e.EventOccurredOn, e.AuditSequence }).HasDatabaseName("ix_audit_events_tenant_actor_time");
        builder.HasIndex(e => new { e.TenantId, e.CorrelationIdentifier }).HasDatabaseName("ix_audit_events_tenant_correlation");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).HasConstraintName("fk_audit_events_tenant");
        builder.ToTable(t => t.HasCheckConstraint("ck_audit_events_outcome", "event_outcome IN ('Success','Denied','Failed')"));
    }
}
