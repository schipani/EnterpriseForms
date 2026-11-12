using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SearchAccess;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// Isolated authority store (schema <c>authorization</c>). Reference: trust_authority/authority-store.reference.sql,
/// consistency-schema-additions.reference.sql and record-manifest.reference.sql. All identifiers are multiword snake_case
/// with entity-based UUIDv7 primary keys; the runtime role has no DDL and no access from any business identity.
/// </summary>
public sealed class AuthorityDbContext(DbContextOptions<AuthorityDbContext> options) : DbContext(options)
{
    public const string SchemaName = "authority"; // "authorization" is a reserved word in PostgreSQL; reference SQL schema name adapted (see STAGE_A_STATUS.md)

    public DbSet<OrganizationAuthorizationState> OrganizationStates => Set<OrganizationAuthorizationState>();

    public DbSet<IdentityBinding> IdentityBindings => Set<IdentityBinding>();

    public DbSet<OrganizationRoleChangeProposal> RoleChangeProposals => Set<OrganizationRoleChangeProposal>();

    public DbSet<OrganizationMembershipRequest> MembershipRequests => Set<OrganizationMembershipRequest>();

    public DbSet<AuthorityResource> Resources => Set<AuthorityResource>();

    public DbSet<SecurityCommand> SecurityCommands => Set<SecurityCommand>();

    public DbSet<AuthorityRelationshipEdge> RelationshipEdges => Set<AuthorityRelationshipEdge>();

    public DbSet<ProjectionOutboxEvent> ProjectionOutbox => Set<ProjectionOutboxEvent>();

    public DbSet<SecurityJournalEntry> JournalEntries => Set<SecurityJournalEntry>();

    public DbSet<RecordContentHead> ContentHeads => Set<RecordContentHead>();

    public DbSet<ContentChangeOperation> ContentOperations => Set<ContentChangeOperation>();

    public DbSet<ContentCandidateLeaf> CandidateLeaves => Set<ContentCandidateLeaf>();

    public DbSet<RecordManifestCommit> ManifestCommits => Set<RecordManifestCommit>();

    public DbSet<RecordManifestLeaf> ManifestLeaves => Set<RecordManifestLeaf>();

    public DbSet<ContentItemIdentity> ContentItemIdentities => Set<ContentItemIdentity>();

    public DbSet<ContentIntegrityCheckpoint> ContentCheckpoints => Set<ContentIntegrityCheckpoint>();

    public DbSet<IntegrityKeyRegistration> KeyRegistrations => Set<IntegrityKeyRegistration>();

    public DbSet<OrganizationProtectionPolicy> ProtectionPolicies => Set<OrganizationProtectionPolicy>();

    public DbSet<SearchCatalogueSnapshot> CatalogueSnapshots => Set<SearchCatalogueSnapshot>();

    public DbSet<SearchCatalogueSnapshotEntry> CatalogueSnapshotEntries => Set<SearchCatalogueSnapshotEntry>();

    public DbSet<AuthenticatedSession> Sessions => Set<AuthenticatedSession>();

    public DbSet<PublishedFormVersion> PublishedFormVersions => Set<PublishedFormVersion>();

    public DbSet<FieldSearchApproval> FieldSearchApprovals => Set<FieldSearchApproval>();

    public DbSet<ProtectionCensusResult> ProtectionCensusResults => Set<ProtectionCensusResult>();

    public DbSet<ProtectionReconciliationAttestation> ProtectionAttestations => Set<ProtectionReconciliationAttestation>();

    public DbSet<PlatformOperator> PlatformOperators => Set<PlatformOperator>();

    public DbSet<PlatformOperatorChangeProposal> PlatformOperatorProposals => Set<PlatformOperatorChangeProposal>();

    public DbSet<PlatformOperatorApproval> PlatformOperatorApprovals => Set<PlatformOperatorApproval>();

    public DbSet<PlatformOperatorEvidenceUse> PlatformEvidenceUses => Set<PlatformOperatorEvidenceUse>();

    public DbSet<PlatformJournalHead> PlatformJournalHeads => Set<PlatformJournalHead>();

    public DbSet<PlatformJournalEntry> PlatformJournalEntries => Set<PlatformJournalEntry>();

    public DbSet<OrganizationOnboardingRequest> OnboardingRequests => Set<OrganizationOnboardingRequest>();

    public DbSet<OrganizationOnboardingOwner> OnboardingOwners => Set<OrganizationOnboardingOwner>();

    public DbSet<OrganizationOnboardingApproval> OnboardingApprovals => Set<OrganizationOnboardingApproval>();

    public DbSet<OrganizationOwnerRecoveryCase> OwnerRecoveryCases => Set<OrganizationOwnerRecoveryCase>();

    public DbSet<OrganizationOwnerRecoverySubject> OwnerRecoverySubjects => Set<OrganizationOwnerRecoverySubject>();

    public DbSet<OrganizationOwnerRecoveryApproval> OwnerRecoveryApprovals => Set<OrganizationOwnerRecoveryApproval>();

    public DbSet<OrganizationRecoveredOwnerProbation> RecoveredOwnerProbations => Set<OrganizationRecoveredOwnerProbation>();

    // IX-1: Authority-owned search index (schema authority_search).
    public DbSet<SearchProjectionState> SearchProjectionStates => Set<SearchProjectionState>();

    public DbSet<SearchFieldCoverage> SearchFieldCoverages => Set<SearchFieldCoverage>();

    public DbSet<SearchRecordEntry> SearchRecordEntries => Set<SearchRecordEntry>();

    public DbSet<SearchStringPosting> SearchStringPostings => Set<SearchStringPosting>();

    public DbSet<SearchIntegerPosting> SearchIntegerPostings => Set<SearchIntegerPosting>();

    public DbSet<SearchDecimalPosting> SearchDecimalPostings => Set<SearchDecimalPosting>();

    public DbSet<SearchDatePosting> SearchDatePostings => Set<SearchDatePosting>();

    public DbSet<SearchDateTimePosting> SearchDateTimePostings => Set<SearchDateTimePosting>();

    public DbSet<SearchBooleanPosting> SearchBooleanPostings => Set<SearchBooleanPosting>();

    public DbSet<SearchOptionPosting> SearchOptionPostings => Set<SearchOptionPosting>();

    public DbSet<SearchRecordFieldAnomaly> SearchRecordFieldAnomalies => Set<SearchRecordFieldAnomaly>();

    public DbSet<SearchFormState> SearchFormStates => Set<SearchFormState>();

    public DbSet<SearchResultSnapshot> SearchResultSnapshots => Set<SearchResultSnapshot>();

    public DbSet<SearchResultSnapshotEntry> SearchResultSnapshotEntries => Set<SearchResultSnapshotEntry>();

    public DbSet<SearchAccessCheckpoint> SearchAccessCheckpoints => Set<SearchAccessCheckpoint>();

    public DbSet<SearchAccessEdge> SearchAccessEdges => Set<SearchAccessEdge>();

    public DbSet<SearchAccessRecordGrant> SearchAccessRecordGrants => Set<SearchAccessRecordGrant>();

    public DbSet<SearchAccessFieldItemGrant> SearchAccessFieldItemGrants => Set<SearchAccessFieldItemGrant>();

    public DbSet<SearchAccessValuePosting> SearchAccessValuePostings => Set<SearchAccessValuePosting>();

    public DbSet<SearchAccessVerificationState> SearchAccessVerificationStates => Set<SearchAccessVerificationState>();

    public DbSet<SearchAccessShadowSample> SearchAccessShadowSamples => Set<SearchAccessShadowSample>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthorityDbContext).Assembly);
    }
}
