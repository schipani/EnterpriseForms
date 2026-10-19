using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Sharing;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.Sharing;

public sealed class RecordShareConfiguration : IEntityTypeConfiguration<RecordShare>
{
    public void Configure(EntityTypeBuilder<RecordShare> builder)
    {
        builder.MapSnakeCase("record_shares");
        builder.HasKey(s => s.RecordShareId);
        builder.Property(s => s.RecipientRole).HasMaxLength(20);
        builder.Property(s => s.ShareScope).HasMaxLength(30);
        builder.Property(s => s.ShareStatus).HasMaxLength(20);
        builder.HasIndex(s => new { s.TenantId, s.FormRecordId, s.ShareStatus }).HasDatabaseName("ix_record_shares_tenant_record_status");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(s => s.FormRecordId).HasConstraintName("fk_record_shares_record");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).HasConstraintName("fk_record_shares_tenant");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_record_shares_single_recipient", "(recipient_user_id IS NOT NULL AND recipient_team_id IS NULL) OR (recipient_user_id IS NULL AND recipient_team_id IS NOT NULL)");
            t.HasCheckConstraint("ck_record_shares_role", "recipient_role IN ('Reader','Contributor','Manager')");
            t.HasCheckConstraint("ck_record_shares_status", "share_status IN ('Active','Revoked','Expired')");
        });
    }
}

public sealed class RecordShareFieldConfiguration : IEntityTypeConfiguration<RecordShareField>
{
    public void Configure(EntityTypeBuilder<RecordShareField> builder)
    {
        builder.MapSnakeCase("record_share_fields");
        builder.HasKey(f => f.RecordShareFieldId);
        builder.HasIndex(f => new { f.RecordShareId, f.FieldValueItemId }).IsUnique().HasDatabaseName("uq_record_share_fields_item");
        builder.HasOne<RecordShare>().WithMany().HasForeignKey(f => f.RecordShareId).HasConstraintName("fk_record_share_fields_share");
        builder.ToTable(t => t.HasCheckConstraint("ck_record_share_fields_edit_implies_view", "NOT can_edit_field OR can_view_field"));
    }
}
