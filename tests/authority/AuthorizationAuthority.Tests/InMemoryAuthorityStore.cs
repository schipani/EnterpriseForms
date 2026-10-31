using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sessions;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Single-threaded in-memory Authority store for content-protocol unit tests (Phase C5). It reproduces the store's data
/// contract, not PostgreSQL concurrency: lock ordering between security commands and content commits is covered by the
/// Docker integration tests (revocation race) and the code inspection recorded for U5.
/// </summary>
internal sealed class InMemoryAuthorityStore : IAuthorityStore
{
    public List<OrganizationAuthorizationState> Organizations { get; } = [];
    public List<IdentityBinding> Bindings { get; } = [];
    public List<AuthorityResource> Resources { get; } = [];
    public List<AuthorityRelationshipEdge> Edges { get; } = [];
    public List<ContentChangeOperation> Operations { get; } = [];
    public List<ContentCandidateLeaf> CandidateLeaves { get; } = [];
    public List<RecordContentHead> Heads { get; } = [];
    public List<RecordManifestCommit> Commits { get; } = [];
    public List<RecordManifestLeaf> ManifestLeaves { get; } = [];
    public List<SecurityJournalEntry> Journal { get; } = [];
    public List<ContentIntegrityCheckpoint> Checkpoints { get; } = [];
    public List<IntegrityKeyRegistration> Keys { get; } = [];
    public List<SearchCatalogueSnapshot> Snapshots { get; } = [];
    public List<SecurityCommand> Commands { get; } = [];

    public List<ProjectionOutboxEvent> Outbox { get; } = [];
    public List<SearchCatalogueSnapshotEntry> SnapshotEntries { get; } = [];

    public List<AuthenticatedSession> Sessions { get; } = [];
    public List<PublishedFormVersion> PublishedVersions { get; } = [];
    public List<FieldSearchApproval> Approvals { get; } = [];
    public List<OrganizationProtectionPolicy> Policies { get; } = [];
    public List<ProtectionCensusResult> CensusResults { get; } = [];
    public List<ProtectionReconciliationAttestation> Attestations { get; } = [];
    public List<OrganizationRoleChangeProposal> Proposals { get; } = [];
    public List<OrganizationMembershipRequest> MembershipRequests { get; } = [];
    public List<PlatformOperator> PlatformOperators { get; } = [];
    public List<PlatformOperatorChangeProposal> PlatformProposals { get; } = [];
    public List<PlatformOperatorApproval> PlatformApprovals { get; } = [];
    public List<PlatformOperatorEvidenceUse> PlatformEvidenceUses { get; } = [];
    public List<PlatformJournalEntry> PlatformJournal { get; } = [];
    public PlatformJournalHead? PlatformHead { get; set; }
    public int ScopeCommitCount { get; private set; }

    public Task<IAuthorityScope> BeginScopeAsync(CancellationToken cancellationToken) => Task.FromResult<IAuthorityScope>(new Scope(this));

    /// <summary>Phase C8 AC-9: number of read-only snapshot scopes opened; such a scope refuses to commit.</summary>
    public int ReadOnlyScopeCount { get; private set; }

    public Task<IAuthorityScope> BeginReadOnlySnapshotScopeAsync(CancellationToken cancellationToken)
    {
        ReadOnlyScopeCount++;
        return Task.FromResult<IAuthorityScope>(new Scope(this, readOnly: true));
    }

    public Task<IReadOnlyList<Guid>> GetOrganizationsWithPendingWorkAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);

    private sealed class Scope(InMemoryAuthorityStore s, bool readOnly = false) : IAuthorityScope
    {
        public Task<OrganizationAuthorizationState?> LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult(s.Organizations.FirstOrDefault(o => o.OrganizationId == organizationId));

        public Task<OrganizationAuthorizationState?> ReadOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult(s.Organizations.FirstOrDefault(o => o.OrganizationId == organizationId));

        public void AddOrganization(OrganizationAuthorizationState state) => s.Organizations.Add(state);

        public Task<IdentityBinding?> FindIdentityBindingAsync(Guid organizationId, string issuerUri, string subjectIdentifier, CancellationToken cancellationToken) =>
            Task.FromResult(s.Bindings.FirstOrDefault(b => b.OrganizationId == organizationId && b.IdentityIssuerUri == issuerUri && b.IdentitySubjectIdentifier == subjectIdentifier));

        public Task<IdentityBinding?> FindIdentityBindingByUserAsync(Guid organizationId, Guid authorityUserId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Bindings.FirstOrDefault(b => b.OrganizationId == organizationId && b.AuthorityUserId == authorityUserId));

        public Task<List<IdentityBinding>> GetIdentityBindingsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Bindings.Where(b => b.OrganizationId == organizationId).ToList());

        public Task<IdentityBinding?> FindIdentityBindingByIdAsync(Guid identityBindingId, CancellationToken cancellationToken) => Task.FromResult(s.Bindings.FirstOrDefault(b => b.IdentityBindingId == identityBindingId));

        public void AddIdentityBinding(IdentityBinding binding) => s.Bindings.Add(binding);

        public Task<IdentityBinding?> FindIdentityBindingForUpdateAsync(Guid organizationId, Guid authorityUserId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Bindings.FirstOrDefault(b => b.OrganizationId == organizationId && b.AuthorityUserId == authorityUserId));

        public Task<List<AuthenticatedSession>> GetUnrevokedSessionsAsync(Guid identityBindingId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Sessions.Where(x => x.IdentityBindingId == identityBindingId && x.SessionRevokedOn == null).ToList());

        public Task<OrganizationRoleChangeProposal?> FindRoleChangeProposalAsync(Guid organizationId, Guid proposalId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Proposals.FirstOrDefault(p => p.OrganizationId == organizationId && p.OrganizationRoleChangeProposalId == proposalId));

        public Task<List<OrganizationRoleChangeProposal>> GetRoleChangeProposalsAsync(Guid organizationId, DateTimeOffset createdSince, CancellationToken cancellationToken) =>
            Task.FromResult(s.Proposals.Where(p => p.OrganizationId == organizationId && p.ProposalCreatedOn >= createdSince).OrderByDescending(p => p.ProposalCreatedOn).ToList());

        public void AddRoleChangeProposal(OrganizationRoleChangeProposal proposal) => s.Proposals.Add(proposal);

        public Task<OrganizationMembershipRequest?> FindMembershipRequestAsync(Guid organizationId, Guid requestId, CancellationToken cancellationToken) =>
            Task.FromResult(s.MembershipRequests.FirstOrDefault(r => r.OrganizationId == organizationId && r.OrganizationMembershipRequestId == requestId));

        public Task<List<OrganizationMembershipRequest>> GetMembershipRequestsAsync(Guid organizationId, DateTimeOffset createdSince, CancellationToken cancellationToken) =>
            Task.FromResult(s.MembershipRequests.Where(r => r.OrganizationId == organizationId && r.RequestCreatedOn >= createdSince).OrderByDescending(r => r.RequestCreatedOn).ToList());

        public void AddMembershipRequest(OrganizationMembershipRequest request) => s.MembershipRequests.Add(request);

        public Task<AuthorityResource?> FindResourceAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Resources.FirstOrDefault(r => r.OrganizationId == organizationId && r.ResourceTypeName == resourceTypeName && r.ResourceEntityId == resourceEntityId));

        public void AddResource(AuthorityResource resource) => s.Resources.Add(resource);

        public Task<List<AuthorityResource>> GetActiveResourcesByParentAsync(Guid organizationId, Guid parentResourceEntityId, IReadOnlyCollection<string> resourceTypeNames, CancellationToken cancellationToken) =>
            Task.FromResult(s.Resources.Where(r => r.OrganizationId == organizationId && r.ParentResourceEntityId == parentResourceEntityId && resourceTypeNames.Contains(r.ResourceTypeName) && r.ResourceLifecycleState == ResourceLifecycleStates.Active).ToList());

        public Task<OrganizationProtectionPolicy?> FindProtectionPolicyAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult(s.Policies.FirstOrDefault(p => p.OrganizationId == organizationId));

        public void AddProtectionPolicy(OrganizationProtectionPolicy policy) => s.Policies.Add(policy);

        public Task<List<AuthorityRelationshipEdge>> GetActiveEdgesAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Edges.Where(e => e.OrganizationId == organizationId && e.RemovedBySecurityCommandId is null).ToList());

        public void AddEdge(AuthorityRelationshipEdge edge) => s.Edges.Add(edge);

        public Task<List<AuthorityRelationshipEdge>> GetRelationshipEdgeHistoryAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, string relationshipName, string subjectTypeName, Guid subjectEntityId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Edges.Where(e => e.OrganizationId == organizationId && e.ResourceTypeName == resourceTypeName && e.ResourceEntityId == resourceEntityId && e.RelationshipName == relationshipName && e.SubjectTypeName == subjectTypeName && e.SubjectEntityId == subjectEntityId).ToList());

        public Task<Dictionary<Guid, long?>> GetCommittedRevisionsAsync(Guid organizationId, IReadOnlyCollection<Guid> securityCommandIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.Commands.Where(c => c.OrganizationId == organizationId && securityCommandIds.Contains(c.SecurityCommandId)).ToDictionary(c => c.SecurityCommandId, c => c.CommittedAuthorizationRevision));

        public Task<SecurityCommand?> FindCommandByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(s.Commands.FirstOrDefault(c => c.OrganizationId == organizationId && c.CommandIdempotencyKey == idempotencyKey));

        public Task<SecurityCommand?> FindCommandAsync(Guid securityCommandId, CancellationToken cancellationToken) => Task.FromResult(s.Commands.FirstOrDefault(c => c.SecurityCommandId == securityCommandId));

        public void AddCommand(SecurityCommand command) => s.Commands.Add(command);

        public void AddOutboxEvent(ProjectionOutboxEvent outboxEvent) => s.Outbox.Add(outboxEvent);

        public Task<ProjectionOutboxEvent?> FindOutboxEventAsync(Guid organizationId, long targetAuthorizationRevision, CancellationToken cancellationToken) => Task.FromResult<ProjectionOutboxEvent?>(null);

        public void AddJournalEntry(SecurityJournalEntry entry) => s.Journal.Add(entry);

        public Task<SecurityJournalEntry?> FindContentCommitJournalEntryAsync(Guid organizationId, Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Journal.Where(j => j.OrganizationId == organizationId && j.JournalStreamName == JournalStreamNames.Content && j.ContentChangeOperationId == contentChangeOperationId && j.JournalEventKind == "ContentHeadCommitted")
                .OrderByDescending(j => j.JournalStreamRevision).FirstOrDefault());

        public Task<SecurityJournalEntry?> FindJournalEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            Task.FromResult(s.Journal.FirstOrDefault(j => j.OrganizationId == organizationId && j.JournalStreamName == streamName && j.JournalStreamRevision == streamRevision));

        public Task<List<SecurityJournalEntry>> GetPendingJournalEntriesAsync(Guid organizationId, int limit, CancellationToken cancellationToken) => Task.FromResult(new List<SecurityJournalEntry>());

        public Task<int> CountPendingArchiveAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<RecordContentHead?> FindContentHeadAsync(Guid organizationId, Guid formRecordId, bool lockForUpdate, CancellationToken cancellationToken) =>
            Task.FromResult(s.Heads.FirstOrDefault(h => h.OrganizationId == organizationId && h.FormRecordId == formRecordId));

        public void AddContentHead(RecordContentHead head) => s.Heads.Add(head);

        public Task<ContentChangeOperation?> FindOperationByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(s.Operations.FirstOrDefault(o => o.OrganizationId == organizationId && o.OperationIdempotencyKey == idempotencyKey));

        public Task<ContentChangeOperation?> FindOperationAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Operations.FirstOrDefault(o => o.ContentChangeOperationId == contentChangeOperationId));

        public void AddOperation(ContentChangeOperation operation) => s.Operations.Add(operation);

        public void AddCandidateLeaves(IEnumerable<ContentCandidateLeaf> leaves) => s.CandidateLeaves.AddRange(leaves);

        public Task<List<ContentCandidateLeaf>> GetCandidateLeavesAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.CandidateLeaves.Where(l => l.ContentChangeOperationId == contentChangeOperationId).ToList());

        public void AddManifestCommit(RecordManifestCommit commit) => s.Commits.Add(commit);

        public void AddManifestLeaves(IEnumerable<RecordManifestLeaf> leaves) => s.ManifestLeaves.AddRange(leaves);

        public Task<List<RecordManifestLeaf>> GetManifestLeavesAsync(Guid recordManifestCommitId, CancellationToken cancellationToken) =>
            Task.FromResult(s.ManifestLeaves.Where(l => l.RecordManifestCommitId == recordManifestCommitId).OrderBy(l => l.ManifestLeafPosition).ToList());

        public Task<RecordManifestCommit?> FindManifestCommitByOperationAsync(Guid organizationId, Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Commits.FirstOrDefault(c => c.OrganizationId == organizationId && c.FormRecordId == formRecordId && c.CommittedOperationId == committedOperationId));

        public Task<List<RecordManifestCommit>> GetManifestCommitsAsync(Guid organizationId, Guid formRecordId, long afterRevision, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(s.Commits.Where(c => c.OrganizationId == organizationId && c.FormRecordId == formRecordId && c.CommittedContentRevision > afterRevision).OrderBy(c => c.CommittedContentRevision).Take(limit).ToList());

        public Task<List<ContentChangeOperation>> GetOperationsAsync(IReadOnlyCollection<Guid> contentChangeOperationIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.Operations.Where(o => contentChangeOperationIds.Contains(o.ContentChangeOperationId)).ToList());

        public void AddContentCheckpoint(ContentIntegrityCheckpoint checkpoint) => s.Checkpoints.Add(checkpoint);

        public Task<IntegrityKeyRegistration?> FindKeyRegistrationAsync(Guid organizationId, string keyPurposeName, CancellationToken cancellationToken) =>
            Task.FromResult(s.Keys.FirstOrDefault(k => k.OrganizationId == organizationId && k.KeyPurposeName == keyPurposeName));

        public void AddKeyRegistration(IntegrityKeyRegistration registration) => s.Keys.Add(registration);

        public Task<List<Guid>> GetActiveRecordIdsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Resources.Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == ResourceTypeNames.FormRecord && r.ResourceLifecycleState == ResourceLifecycleStates.Active).Select(r => r.ResourceEntityId).ToList());

        public Task<List<AuthorityResource>> GetFormRecordResourcesAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyCollection<string> lifecycleStates, CancellationToken cancellationToken) =>
            Task.FromResult(s.Resources.Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == ResourceTypeNames.FormRecord && r.ParentResourceEntityId == formDefinitionId && lifecycleStates.Contains(r.ResourceLifecycleState)).OrderBy(r => r.ResourceEntityId).ToList());

        public Task<List<AuthorityResource>> GetResourcesAsync(Guid organizationId, string resourceTypeName, IReadOnlyCollection<Guid> resourceEntityIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.Resources.Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == resourceTypeName && resourceEntityIds.Contains(r.ResourceEntityId)).ToList());

        public Task<List<RecordContentHead>> GetContentHeadsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.Heads.Where(h => h.OrganizationId == organizationId && formRecordIds.Contains(h.FormRecordId)).ToList());

        public void AddCatalogueSnapshot(SearchCatalogueSnapshot snapshot, IEnumerable<SearchCatalogueSnapshotEntry> entries)
        {
            s.Snapshots.Add(snapshot);
            s.SnapshotEntries.AddRange(entries);
        }

        public Task<SearchCatalogueSnapshot?> FindCatalogueSnapshotAsync(Guid searchCatalogueSnapshotId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Snapshots.FirstOrDefault(x => x.SearchCatalogueSnapshotId == searchCatalogueSnapshotId));

        public Task<List<SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesAsync(Guid searchCatalogueSnapshotId, int offset, int? limit, CancellationToken cancellationToken)
        {
            var query = s.SnapshotEntries.Where(e => e.SearchCatalogueSnapshotId == searchCatalogueSnapshotId).OrderBy(e => e.CatalogueEntryPosition).Skip(offset);
            return Task.FromResult((limit is { } take ? query.Take(take) : query).ToList());
        }

        public Task<AuthenticatedSession?> FindSessionByDigestAsync(byte[] sessionSecretDigest, CancellationToken cancellationToken) =>
            Task.FromResult(s.Sessions.FirstOrDefault(x => x.SessionSecretDigest.AsSpan().SequenceEqual(sessionSecretDigest)));

        public void AddSession(AuthenticatedSession session) => s.Sessions.Add(session);

        public Task<PublishedFormVersion?> FindLatestPublishedFormVersionAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
            Task.FromResult(s.PublishedVersions.Where(v => v.OrganizationId == organizationId && v.FormDefinitionId == formDefinitionId).OrderByDescending(v => v.FormVersionNumber).FirstOrDefault());

        public void AddPublishedFormVersion(PublishedFormVersion version) => s.PublishedVersions.Add(version);

        public Task<List<PublishedFormVersion>> GetLatestPublishedFormVersionsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.PublishedVersions.Where(v => v.OrganizationId == organizationId).GroupBy(v => v.FormDefinitionId).Select(g => g.MaxBy(v => v.FormVersionNumber)!).ToList());

        public Task<List<FieldSearchApproval>> GetFieldSearchApprovalsAsync(Guid organizationId, Guid? formDefinitionId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Approvals.Where(a => a.OrganizationId == organizationId && (formDefinitionId == null || a.FormDefinitionId == formDefinitionId)).ToList());

        public void AddFieldSearchApproval(FieldSearchApproval approval) => s.Approvals.Add(approval);

        public Task<List<SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesForRecordsAsync(Guid searchCatalogueSnapshotId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.SnapshotEntries.Where(e => e.SearchCatalogueSnapshotId == searchCatalogueSnapshotId && formRecordIds.Contains(e.FormRecordId)).ToList());

        public Task<List<AuthorityResource>> GetResourcesByTypeAsync(Guid organizationId, IReadOnlyCollection<string> resourceTypeNames, IReadOnlyCollection<string> lifecycleStates, CancellationToken cancellationToken) =>
            Task.FromResult(s.Resources.Where(r => r.OrganizationId == organizationId && resourceTypeNames.Contains(r.ResourceTypeName) && lifecycleStates.Contains(r.ResourceLifecycleState)).OrderBy(r => r.ResourceEntityId).ToList());

        public Task<Dictionary<Guid, int>> CountManifestCommitsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.Commits.Where(c => c.OrganizationId == organizationId && formRecordIds.Contains(c.FormRecordId)).GroupBy(c => c.FormRecordId).ToDictionary(g => g.Key, g => g.Count()));

        public Task<List<ProtectionCensusResult>> GetProtectionCensusResultsAsync(Guid organizationId, Guid scopingSecurityCommandId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.CensusResults.Where(r => r.OrganizationId == organizationId && r.ScopingSecurityCommandId == scopingSecurityCommandId && formRecordIds.Contains(r.FormRecordId)).ToList());

        public void AddProtectionCensusResult(ProtectionCensusResult result) => s.CensusResults.Add(result);

        public Task<ProtectionCensusResult?> FindProtectionCensusResultAsync(Guid scopingSecurityCommandId, Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.CensusResults.FirstOrDefault(r => r.ScopingSecurityCommandId == scopingSecurityCommandId && r.FormRecordId == formRecordId && r.CommittedOperationId == committedOperationId));

        public void AddProtectionAttestation(ProtectionReconciliationAttestation attestation) => s.Attestations.Add(attestation);

        public Task<List<ProtectionReconciliationAttestation>> GetLatestProtectionAttestationsAsync(Guid organizationId, IReadOnlyCollection<Guid> scopingSecurityCommandIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.Attestations.Where(a => a.OrganizationId == organizationId && scopingSecurityCommandIds.Contains(a.ScopingSecurityCommandId)).GroupBy(a => a.ScopingSecurityCommandId).Select(g => g.MaxBy(a => a.AttestedAuditJournalRevision)!).ToList());

        // ---- C8 M2 Stages 1-2: Platform Operators ----

        public Task<PlatformJournalHead?> LockPlatformJournalAsync(CancellationToken cancellationToken) => Task.FromResult(s.PlatformHead);

        public Task<PlatformJournalHead?> ReadPlatformJournalAsync(CancellationToken cancellationToken) => Task.FromResult(s.PlatformHead);

        public void AddPlatformJournalHead(PlatformJournalHead head) => s.PlatformHead = head;

        public void AddPlatformJournalEntry(PlatformJournalEntry entry) => s.PlatformJournal.Add(entry);

        public Task<List<PlatformJournalEntry>> GetPendingPlatformJournalEntriesAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult(s.PlatformJournal.Where(e => e.ArchiveDeliveryState == ArchiveDeliveryStates.Pending).OrderBy(e => e.PlatformJournalRevision).Take(limit).ToList());

        public Task<int> CountPendingPlatformArchiveAsync(CancellationToken cancellationToken) => Task.FromResult(s.PlatformJournal.Count(e => e.ArchiveDeliveryState == ArchiveDeliveryStates.Pending));

        public Task<PlatformOperator?> FindPlatformOperatorAsync(Guid platformOperatorId, CancellationToken cancellationToken) => Task.FromResult(s.PlatformOperators.FirstOrDefault(o => o.PlatformOperatorId == platformOperatorId));

        public Task<PlatformOperator?> FindPlatformOperatorByIdentityAsync(string issuerUri, string subjectIdentifier, CancellationToken cancellationToken) =>
            Task.FromResult(s.PlatformOperators.FirstOrDefault(o => o.OperatorIssuerUri == issuerUri && o.OperatorSubjectIdentifier == subjectIdentifier));

        public Task<List<PlatformOperator>> GetPlatformOperatorsAsync(CancellationToken cancellationToken) => Task.FromResult(s.PlatformOperators.OrderBy(o => o.OperatorProvisionedOn).ThenBy(o => o.PlatformOperatorId).ToList());

        public void AddPlatformOperator(PlatformOperator platformOperator)
        {
            if (s.PlatformOperators.Any(o => o.PlatformOperatorId == platformOperator.PlatformOperatorId || (o.OperatorIssuerUri == platformOperator.OperatorIssuerUri && o.OperatorSubjectIdentifier == platformOperator.OperatorSubjectIdentifier)))
            {
                throw new InvalidOperationException("uq_platform_operator_identity");
            }

            s.PlatformOperators.Add(platformOperator);
        }

        public Task<PlatformOperatorChangeProposal?> FindPlatformOperatorProposalAsync(Guid proposalId, CancellationToken cancellationToken) => Task.FromResult(s.PlatformProposals.FirstOrDefault(p => p.PlatformOperatorChangeProposalId == proposalId));

        public Task<List<PlatformOperatorChangeProposal>> GetPlatformOperatorProposalsAsync(DateTimeOffset createdSince, CancellationToken cancellationToken) =>
            Task.FromResult(s.PlatformProposals.Where(p => p.ProposalCreatedOn >= createdSince).OrderBy(p => p.ProposalCreatedOn).ToList());

        public void AddPlatformOperatorProposal(PlatformOperatorChangeProposal proposal) => s.PlatformProposals.Add(proposal);

        public Task<PlatformOperatorApproval?> FindPlatformApprovalAsync(Guid platformOperatorApprovalId, CancellationToken cancellationToken) => Task.FromResult(s.PlatformApprovals.FirstOrDefault(a => a.PlatformOperatorApprovalId == platformOperatorApprovalId));

        public Task<List<PlatformOperatorApproval>> GetPlatformApprovalsForRequestAsync(Guid organizationMembershipRequestId, CancellationToken cancellationToken) =>
            Task.FromResult(s.PlatformApprovals.Where(a => a.OrganizationMembershipRequestId == organizationMembershipRequestId).OrderBy(a => a.ApprovalRecordedOn).ToList());

        public Task<List<PlatformOperatorApproval>> GetPendingPlatformApprovalsForOperatorAsync(Guid platformOperatorId, CancellationToken cancellationToken) =>
            Task.FromResult(s.PlatformApprovals.Where(a => a.PlatformOperatorId == platformOperatorId && a.ApprovalStateName == PlatformApprovalStates.Pending).ToList());

        public Task<List<PlatformOperatorApproval>> GetPlatformApprovalsForRequestsAsync(IReadOnlyCollection<Guid> organizationMembershipRequestIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.PlatformApprovals.Where(a => organizationMembershipRequestIds.Contains(a.OrganizationMembershipRequestId)).ToList());

        /// <summary>Mirrors <c>uq_platform_approval_pending_request</c> and <c>uq_platform_approval_evidence</c> at insert time.</summary>
        public void AddPlatformApproval(PlatformOperatorApproval approval)
        {
            if ((approval.ApprovalStateName == PlatformApprovalStates.Pending && s.PlatformApprovals.Any(a => a.OrganizationMembershipRequestId == approval.OrganizationMembershipRequestId && a.ApprovalStateName == PlatformApprovalStates.Pending))
                || s.PlatformApprovals.Any(a => a.ApprovalEvidenceSha256.AsSpan().SequenceEqual(approval.ApprovalEvidenceSha256)))
            {
                throw new InvalidOperationException("unique platform approval constraint violated");
            }

            s.PlatformApprovals.Add(approval);
        }

        public Task<bool> IsPlatformEvidenceUsedAsync(string evidenceUseIdentifier, byte[] evidenceUseSha256, CancellationToken cancellationToken) =>
            Task.FromResult(s.PlatformEvidenceUses.Any(e => e.EvidenceUseIdentifier == evidenceUseIdentifier || e.EvidenceUseSha256.AsSpan().SequenceEqual(evidenceUseSha256)));

        public Task<PlatformOperatorEvidenceUse?> FindPlatformEvidenceUseAsync(Guid platformOperatorEvidenceUseId, CancellationToken cancellationToken) =>
            Task.FromResult(s.PlatformEvidenceUses.FirstOrDefault(e => e.PlatformOperatorEvidenceUseId == platformOperatorEvidenceUseId));

        public void AddPlatformEvidenceUse(PlatformOperatorEvidenceUse evidenceUse) => s.PlatformEvidenceUses.Add(evidenceUse);

        public Task<PlatformRequestStorePage> SearchMembershipRequestsAsync(PlatformMembershipRequestQuery query, CancellationToken cancellationToken)
        {
            var now = query.Now;
            bool Pending(OrganizationMembershipRequest r) => r.RequestStateName == MembershipRequestStates.Pending && r.RequestExpiresOn > now;
            bool HasApproval(OrganizationMembershipRequest r) => s.PlatformApprovals.Any(a => a.OrganizationMembershipRequestId == r.OrganizationMembershipRequestId && a.ApprovalStateName == PlatformApprovalStates.Pending && a.ApprovalValidUntil > now);
            var rows = s.MembershipRequests.Select(r => new PlatformRequestStoreRow(r, s.Organizations.Single(o => o.OrganizationId == r.OrganizationId).OrganizationDisplayName)).Where(x => query.Status switch
            {
                PlatformRequestFilters.NeedsDecision => Pending(x.Request),
                PlatformRequestFilters.AwaitingFirstApproval => Pending(x.Request) && !HasApproval(x.Request),
                PlatformRequestFilters.AwaitingSecondApproval => Pending(x.Request) && HasApproval(x.Request),
                PlatformRequestFilters.Approved => x.Request.RequestStateName == MembershipRequestStates.Approved,
                PlatformRequestFilters.Rejected => x.Request.RequestStateName == MembershipRequestStates.Rejected,
                PlatformRequestFilters.Expired => x.Request.RequestStateName == MembershipRequestStates.Expired || (x.Request.RequestStateName == MembershipRequestStates.Pending && x.Request.RequestExpiresOn <= now),
                PlatformRequestFilters.CancelledOrReplaced => x.Request.RequestStateName is MembershipRequestStates.Cancelled or MembershipRequestStates.Replaced,
                _ => true,
            });
            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                rows = rows.Where(x => new[] { x.Request.RequestedDisplayNameHint, x.Request.RequestedSubjectIdentifier, x.Request.RequestedIssuerUri, x.OrganizationDisplayName, x.Request.OrganizationMembershipRequestId.ToString("D") }
                    .Any(v => v.Contains(query.Text, StringComparison.OrdinalIgnoreCase)));
            }

            var all = rows.ToList();
            var ordered = all.OrderByDescending(x => x.Request.RequestCreatedOn).ThenByDescending(x => x.Request.OrganizationMembershipRequestId).AsEnumerable();
            if (query.After is { } after)
            {
                ordered = ordered.Where(x => x.Request.RequestCreatedOn < after.CreatedOn || (x.Request.RequestCreatedOn == after.CreatedOn && x.Request.OrganizationMembershipRequestId.CompareTo(after.RequestId) < 0));
            }

            return Task.FromResult(new PlatformRequestStorePage(ordered.Take(query.Limit).ToList(), all.Count));
        }

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (readOnly)
            {
                throw new InvalidOperationException("read-only snapshot scope cannot commit");
            }

            s.ScopeCommitCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
