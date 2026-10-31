using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Organizations;
using EnterpriseForms.Domain.Features.RelatedForms;
using EnterpriseForms.Domain.Features.SecurityOperations;
using EnterpriseForms.Domain.Features.Sharing;
using EnterpriseForms.Domain.Features.Teams;
using EnterpriseForms.Domain.Features.UserPreferences;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseForms.Infrastructure.Persistence;

/// <summary>
/// Single business DbContext and migration stream (ADR-ARCH-001 §4U.1). Feature-owned
/// <see cref="IEntityTypeConfiguration{TEntity}"/> classes under Features/&lt;Feature&gt;/ are applied here and checked
/// by <see cref="Conventions.ModelNamingValidator"/> in the architecture tests. The business database is UNTRUSTED for
/// authorization: owner/share/team/link rows are projections of authority decisions (TRUST_AUTHORITY_ARCHITECTURE.md §2).
/// </summary>
public sealed class BusinessDbContext(DbContextOptions<BusinessDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserPresence> UserPresences => Set<UserPresence>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();

    public DbSet<AuthorityCommandOperation> AuthorityCommandOperations => Set<AuthorityCommandOperation>();

    public DbSet<TeamMembershipProjectionState> TeamMembershipProjectionStates => Set<TeamMembershipProjectionState>();

    public DbSet<SecurityIncident> SecurityIncidents => Set<SecurityIncident>();

    public DbSet<SecurityIncidentOccurrence> SecurityIncidentOccurrences => Set<SecurityIncidentOccurrence>();

    public DbSet<SecurityIncidentNote> SecurityIncidentNotes => Set<SecurityIncidentNote>();

    public DbSet<FormDefinition> FormDefinitions => Set<FormDefinition>();

    public DbSet<FormVersion> FormVersions => Set<FormVersion>();

    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();

    public DbSet<FieldOption> FieldOptions => Set<FieldOption>();

    public DbSet<OptionList> OptionLists => Set<OptionList>();

    public DbSet<GroupDefinition> GroupDefinitions => Set<GroupDefinition>();

    public DbSet<FormRecordGroupRevision> FormRecordGroupRevisions => Set<FormRecordGroupRevision>();

    public DbSet<OptionListItem> OptionListItems => Set<OptionListItem>();

    public DbSet<FieldProtectionMigration> FieldProtectionMigrations => Set<FieldProtectionMigration>();

    public DbSet<FieldProtectionMigrationItem> FieldProtectionMigrationItems => Set<FieldProtectionMigrationItem>();

    public DbSet<FieldProtectionReconciliation> FieldProtectionReconciliations => Set<FieldProtectionReconciliation>();

    public DbSet<FormDraft> FormDrafts => Set<FormDraft>();

    public DbSet<FormRecord> FormRecords => Set<FormRecord>();

    public DbSet<FormRecordCreationAttempt> FormRecordCreationAttempts => Set<FormRecordCreationAttempt>();

    public DbSet<FormRecordRevision> FormRecordRevisions => Set<FormRecordRevision>();

    public DbSet<FieldStringValue> FieldStringValues => Set<FieldStringValue>();

    public DbSet<FieldIntegerValue> FieldIntegerValues => Set<FieldIntegerValue>();

    public DbSet<FieldDecimalValue> FieldDecimalValues => Set<FieldDecimalValue>();

    public DbSet<FieldDateValue> FieldDateValues => Set<FieldDateValue>();

    public DbSet<FieldDateTimeValue> FieldDateTimeValues => Set<FieldDateTimeValue>();

    public DbSet<FieldGuidValue> FieldGuidValues => Set<FieldGuidValue>();

    public DbSet<FieldEncryptedValue> FieldEncryptedValues => Set<FieldEncryptedValue>();

    public DbSet<FieldBooleanValue> FieldBooleanValues => Set<FieldBooleanValue>();

    public DbSet<FieldOptionValue> FieldOptionValues => Set<FieldOptionValue>();

    public DbSet<FormRelationshipDefinition> FormRelationshipDefinitions => Set<FormRelationshipDefinition>();

    public DbSet<FormRecordRelationship> FormRecordRelationships => Set<FormRecordRelationship>();

    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();

    public DbSet<FileAttachment> FileAttachments => Set<FileAttachment>();

    public DbSet<FileAttachmentVersion> FileAttachmentVersions => Set<FileAttachmentVersion>();

    public DbSet<DocumentProcessingJob> DocumentProcessingJobs => Set<DocumentProcessingJob>();

    public DbSet<RecordShare> RecordShares => Set<RecordShare>();

    public DbSet<RecordShareField> RecordShareFields => Set<RecordShareField>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<OrganizationBranding> OrganizationBrandings => Set<OrganizationBranding>();

    public DbSet<OrganizationSecurityPolicy> OrganizationSecurityPolicies => Set<OrganizationSecurityPolicy>();

    public DbSet<UserUiPreference> UserUiPreferences => Set<UserUiPreference>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BusinessDbContext).Assembly);
    }
}
