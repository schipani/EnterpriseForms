using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthorityDbContext).Assembly);
    }
}
