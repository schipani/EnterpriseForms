using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.FormDesigner;

public sealed class FormDraftConfiguration : IEntityTypeConfiguration<FormDraft>
{
    public void Configure(EntityTypeBuilder<FormDraft> builder)
    {
        builder.MapSnakeCase("form_drafts");
        builder.HasKey(d => d.FormDraftId);
        builder.Property(d => d.LayoutJson).HasColumnType("jsonb");
        builder.HasIndex(d => d.FormDefinitionId).IsUnique().HasDatabaseName("uq_form_drafts_definition");
        builder.HasOne<FormDefinition>().WithMany().HasForeignKey(d => d.FormDefinitionId).HasConstraintName("fk_form_drafts_definition");
        builder.HasOne<FormVersion>().WithMany().HasForeignKey(d => d.BasedOnFormVersionId).HasConstraintName("fk_form_drafts_based_on_version");
        builder.ToTable(t => t.HasCheckConstraint("ck_form_drafts_revision_positive", "draft_revision >= 1 AND layout_schema_version > 0"));
    }
}

public sealed class FieldOptionConfiguration : IEntityTypeConfiguration<FieldOption>
{
    public void Configure(EntityTypeBuilder<FieldOption> builder)
    {
        builder.MapSnakeCase("field_options");
        builder.HasKey(o => o.FieldOptionId);
        builder.Property(o => o.OptionKey).HasMaxLength(64);
        builder.Property(o => o.OptionDisplayText).HasMaxLength(200);
        builder.HasIndex(o => new { o.FieldDefinitionId, o.OptionKey }).IsUnique().HasDatabaseName("uq_field_options_field_key");
        builder.HasOne<FieldDefinition>().WithMany().HasForeignKey(o => o.FieldDefinitionId).HasConstraintName("fk_field_options_field");
        builder.HasOne<OptionListItem>().WithMany().HasForeignKey(o => o.OptionListItemId).HasConstraintName("fk_field_options_list_item");
    }
}

public sealed class OptionListConfiguration : IEntityTypeConfiguration<OptionList>
{
    public void Configure(EntityTypeBuilder<OptionList> builder)
    {
        builder.MapSnakeCase("option_lists");
        builder.HasKey(l => l.OptionListId);
        builder.Property(l => l.OptionListName).HasMaxLength(200);
        builder.Property(l => l.OptionListDescription).HasMaxLength(1000);
        builder.Property(l => l.LifecycleStatus).HasMaxLength(20);
        builder.Property(l => l.OptionListRevision).HasDefaultValue(1L);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).HasConstraintName("fk_option_lists_tenant");
        builder.HasIndex(l => new { l.TenantId, l.OptionListName }).IsUnique().HasDatabaseName("uq_option_lists_tenant_name");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_option_lists_lifecycle", "lifecycle_status IN ('Active','Archived')");
            t.HasCheckConstraint("ck_option_lists_revision_positive", "option_list_revision >= 1");
        });
    }
}

public sealed class OptionListItemConfiguration : IEntityTypeConfiguration<OptionListItem>
{
    public void Configure(EntityTypeBuilder<OptionListItem> builder)
    {
        builder.MapSnakeCase("option_list_items");
        builder.HasKey(i => i.OptionListItemId);
        builder.Property(i => i.OptionKey).HasMaxLength(64);
        builder.Property(i => i.OptionDisplayText).HasMaxLength(200);
        builder.HasIndex(i => new { i.OptionListId, i.OptionKey }).IsUnique().HasDatabaseName("uq_option_list_items_list_key");
        builder.HasOne<OptionList>().WithMany().HasForeignKey(i => i.OptionListId).HasConstraintName("fk_option_list_items_list");
    }
}
