using EnterpriseForms.Domain.Features.FormDefinitions;
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
    }
}
