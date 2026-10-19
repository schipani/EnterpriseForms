using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.RelatedForms;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.RelatedForms;

public sealed class FormRelationshipDefinitionConfiguration : IEntityTypeConfiguration<FormRelationshipDefinition>
{
    public void Configure(EntityTypeBuilder<FormRelationshipDefinition> builder)
    {
        builder.MapSnakeCase("form_relationship_definitions");
        builder.HasKey(r => r.FormRelationshipDefinitionId);
        builder.Property(r => r.RelationshipDefinitionKey).HasMaxLength(64);
        builder.Property(r => r.RelationshipDisplayName).HasMaxLength(200);
        builder.Property(r => r.LifecycleStatus).HasMaxLength(20);
        builder.HasIndex(r => new { r.ParentFormDefinitionId, r.RelationshipDefinitionKey }).IsUnique().HasDatabaseName("uq_form_relationship_definitions_parent_key");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).HasConstraintName("fk_form_relationship_definitions_tenant");
        builder.HasOne<FormDefinition>().WithMany().HasForeignKey(r => r.ParentFormDefinitionId).HasConstraintName("fk_form_relationship_definitions_parent_form");
        builder.HasOne<FormDefinition>().WithMany().HasForeignKey(r => r.ChildFormDefinitionId).HasConstraintName("fk_form_relationship_definitions_child_form");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_form_relationship_definitions_bounds", "minimum_child_count >= 0 AND maximum_child_count >= 1 AND maximum_child_count <= 250 AND minimum_child_count <= maximum_child_count");
            t.HasCheckConstraint("ck_form_relationship_definitions_lifecycle", "lifecycle_status IN ('Active','Retired')");
            t.HasCheckConstraint("ck_form_relationship_definitions_no_self", "parent_form_definition_id <> child_form_definition_id");
        });
    }
}

public sealed class FormRecordRelationshipConfiguration : IEntityTypeConfiguration<FormRecordRelationship>
{
    public void Configure(EntityTypeBuilder<FormRecordRelationship> builder)
    {
        builder.MapSnakeCase("form_record_relationships");
        builder.HasKey(r => r.FormRecordRelationshipId);
        builder.Property(r => r.LinkLifecycleState).HasMaxLength(20);
        // V1 composition: one active composing parent per child (RELATED_FORM_COLLECTIONS.md §2).
        builder.HasIndex(r => r.ChildFormRecordId).IsUnique().HasFilter("link_lifecycle_state = 'Active'").HasDatabaseName("uq_form_record_relationships_active_child");
        builder.HasIndex(r => new { r.ParentFormRecordId, r.FormRelationshipDefinitionId, r.ListSortOrder }).HasDatabaseName("ix_form_record_relationships_parent_list");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).HasConstraintName("fk_form_record_relationships_tenant");
        builder.HasOne<FormRelationshipDefinition>().WithMany().HasForeignKey(r => r.FormRelationshipDefinitionId).HasConstraintName("fk_form_record_relationships_definition");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(r => r.ParentFormRecordId).HasConstraintName("fk_form_record_relationships_parent");
        builder.HasOne<FormRecord>().WithMany().HasForeignKey(r => r.ChildFormRecordId).HasConstraintName("fk_form_record_relationships_child");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_form_record_relationships_lifecycle", "link_lifecycle_state IN ('Active','Removed')");
            t.HasCheckConstraint("ck_form_record_relationships_no_self", "parent_form_record_id <> child_form_record_id");
        });
    }
}
