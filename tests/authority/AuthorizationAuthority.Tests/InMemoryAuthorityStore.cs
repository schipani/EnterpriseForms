using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sessions;
using AuthorizationAuthority.Domain.Features.Sharing;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Single-threaded in-memory Authority store for content-protocol unit tests (Phase C5). It reproduces the store's data
/// contract, not PostgreSQL concurrency: lock ordering between security commands and content commits is covered by the
/// Docker integration tests (revocation race) and the code inspection recorded for U5.
/// </summary>
internal sealed class InMemoryAuthorityStore : IAuthorityStore
{
    /// <summary>SEC-BL-01 WP-5b: when true, pending journal queries and the gate's archive count reflect the journal (default: nothing pending).</summary>
    public bool RealJournalDelivery { get; set; }

    /// <summary>
    /// SEC-BL-01 E-6/E-7: the receipt verifier of this store. Unset: with <see cref="RealJournalDelivery"/> the fail-closed verifier (a test must
    /// compose a real one); without it, the test double that treats Anchored rows as delivered (this store models no archive).
    /// </summary>
    public IJournalReceiptVerifier? ReceiptVerifier { get; set; }

    public List<OrganizationAuthorizationState> Organizations { get; } = [];
    public List<IdentityBinding> Bindings { get; } = [];
    public List<AuthorityResource> Resources { get; } = [];
    public List<ShareResourceMetadata> ShareMetadata { get; } = [];
    public List<AuthorityRelationshipEdge> Edges { get; } = [];
    public List<ContentChangeOperation> Operations { get; } = [];
    public List<ContentCandidateLeaf> CandidateLeaves { get; } = [];
    public List<RecordContentHead> Heads { get; } = [];
    public List<RecordManifestCommit> Commits { get; } = [];
    public List<RecordManifestLeaf> ManifestLeaves { get; } = [];

    public List<ContentItemIdentity> ItemIdentities { get; } = [];

    /// <summary>E3-M2e: the data-key ledger. Emulates the global unique identifier and the P-1 binding guard (the real behaviour is PostgreSQL-only, gate E3-M2e-1).</summary>
    public List<ContentDataKeyBinding> DataKeyBindings { get; } = [];

    /// <summary>E3-M2e L-13: when set, every <see cref="IAuthorityScope.RequireDataKeyLedgerAsync"/> refuses as a store whose ledger is not enforced.</summary>
    public bool DataKeyLedgerRefused { get; set; }

    /// <summary>E3-M2e L-13: number of ledger verifications requested by the services under test.</summary>
    public int DataKeyLedgerChecks { get; private set; }

    /// <summary>E3-M2f-2 AM-F1: number of organization binding-existence queries (proves the CAP-F3 short-circuit).</summary>
    public int DataKeyBindingExistenceQueries { get; private set; }

    /// <summary>E3-M2f-2 CAP-F5: when set, every binding-existence query throws this (a store failure).</summary>
    public Exception? DataKeyBindingExistenceFailure { get; set; }
    public List<SecurityJournalEntry> Journal { get; } = [];

    /// <summary>E3-M2f-3b: whether the evidence index is reported usable (T2-8).</summary>
    public bool EvidenceIndexUsable { get; set; } = true;

    /// <summary>E3-M2f-3b: the emulated AuthorityDb clock_timestamp().</summary>
    public Func<DateTimeOffset> EvidenceDatabaseClock { get; set; } = () => DateTimeOffset.UtcNow;
    public List<ContentIntegrityCheckpoint> Checkpoints { get; } = [];
    public List<IntegrityKeyRegistration> Keys { get; } = [];
    public List<SearchCatalogueSnapshot> Snapshots { get; } = [];
    public List<SecurityCommand> Commands { get; } = [];

    public List<ProjectionOutboxEvent> Outbox { get; } = [];
    public List<SearchCatalogueSnapshotEntry> SnapshotEntries { get; } = [];

    public List<AuthenticatedSession> Sessions { get; } = [];

    /// <summary>C-133 PD-3 fault injection: the next commit throws this exception (once).</summary>
    public Exception? FailNextCommit { get; set; }

    /// <summary>C8 M4-2: the next N no-wait organization locks report busy (a lock held by a security command).</summary>
    public int NoWaitLockBusyCount { get; set; }

    /// <summary>C8 M4-2: runs before every no-wait organization lock (e.g. a revocation that commits between check and lock).</summary>
    public Action? BeforeNoWaitLock { get; set; }

    /// <summary>C8 M4-2: no-wait lock attempts made.</summary>
    public int NoWaitLockAttempts { get; private set; }

    /// <summary>C8 M4-2: statement and transaction timeouts applied to audit-access scopes.</summary>
    public List<(int Statement, int Transaction)> AppliedAuditAccessTimeouts { get; } = [];

    /// <summary>C8 M4-2: when false, scopes do not implement IAuditAccessScope (a store that cannot bound the transaction).</summary>
    public bool SupportsAuditAccess { get; set; } = true;

    /// <summary>C8 M4-3: timeline source reads (commands and audit entries) issued by the timeline service.</summary>
    public int TimelineReads { get; set; }
    public List<PublishedFormVersion> PublishedVersions { get; } = [];
    public List<FieldSearchApproval> Approvals { get; } = [];

    public List<FieldFormBinding> FieldFormBindings { get; } = [];

    /// <summary>C-188 BF-1: staged publications (the real table allows at most one Pending per form; enforced on add).</summary>
    public List<FormVersionPublication> Publications { get; } = [];

    /// <summary>C-188: adds a binding with the real table's primary key (organization, field) enforced.</summary>
    public void AddFieldFormBindingForTest(FieldFormBinding binding)
    {
        if (FieldFormBindings.Any(b => b.OrganizationId == binding.OrganizationId && b.FieldDefinitionId == binding.FieldDefinitionId))
        {
            throw new InvalidOperationException("duplicate key value violates unique constraint pk_field_form_bindings");
        }

        FieldFormBindings.Add(binding);
    }
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
    public List<OrganizationOnboardingRequest> OnboardingRequests { get; } = [];
    public List<OrganizationOnboardingOwner> OnboardingOwners { get; } = [];
    public List<OrganizationOnboardingApproval> OnboardingApprovals { get; } = [];
    public List<OrganizationOwnerRecoveryCase> RecoveryCases { get; } = [];
    public List<OrganizationOwnerRecoverySubject> RecoverySubjects { get; } = [];
    public List<OrganizationOwnerRecoveryApproval> RecoveryApprovals { get; } = [];
    public List<OrganizationRecoveredOwnerProbation> RecoveryProbations { get; } = [];
    public int ScopeCommitCount { get; private set; }

    /// <summary>IX-1: the in-memory search index shared by every scope of this store.</summary>
    public InMemorySearchIndexStore SearchIndex => searchIndex ??= new InMemorySearchIndexStore(this);

    private InMemorySearchIndexStore? searchIndex;

    public InMemorySearchAccessStore SearchAccess => searchAccess ??= new InMemorySearchAccessStore();

    private InMemorySearchAccessStore? searchAccess;

    /// <summary>C-149: number of read-write scopes opened.</summary>
    public int ScopeCount { get; private set; }

    /// <summary>
    /// RB-CARV-1: when set, every <see cref="IAuthorityScope.RequireContentAcceptanceFloorAsync"/> refuses as a Release B instance
    /// whose floor is not enforced (the real verification is PostgreSQL-only and is covered by harness R). Null = Release A.
    /// </summary>
    public string? ContentAcceptanceFenceRefusal { get; set; }

    /// <summary>RB-CARV-1: number of content write fence checks requested by the services under test.</summary>
    public int ContentAcceptanceFenceChecks { get; private set; }

    public Task<IAuthorityScope> BeginScopeAsync(CancellationToken cancellationToken)
    {
        ScopeCount++;
        return Task.FromResult<IAuthorityScope>(SupportsAuditAccess ? new AuditAccessScope(this) : new Scope(this));
    }

    /// <summary>Phase C8 AC-9: number of read-only snapshot scopes opened; such a scope refuses to commit.</summary>
    public int ReadOnlyScopeCount { get; private set; }

    public Task<IAuthorityScope> BeginReadOnlySnapshotScopeAsync(CancellationToken cancellationToken)
    {
        ReadOnlyScopeCount++;
        return Task.FromResult<IAuthorityScope>(new Scope(this, readOnly: true));
    }

    public Task<IReadOnlyList<Guid>> GetOrganizationsWithPendingWorkAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyList<Guid>> GetAllOrganizationIdsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>(Organizations.Select(o => o.OrganizationId).ToList());

    public int ChangeMarkerReadCount { get; private set; }

    public Task<IReadOnlyList<OrganizationChangeMarker>> GetOrganizationChangeMarkersAsync(CancellationToken cancellationToken)
    {
        ChangeMarkerReadCount++;
        return Task.FromResult<IReadOnlyList<OrganizationChangeMarker>>(Organizations.OrderBy(o => o.OrganizationId).Select(o => new OrganizationChangeMarker(o.OrganizationId, o.AuthorizationRevision, o.ContentIntegrityRevision, o.AuditJournalRevision)).ToList());
    }

    private sealed class AuditAccessScope(InMemoryAuthorityStore owner) : Scope(owner), IAuditAccessScope, ISecurityCommandTimelineScope
    {
        // C8 M4-3: the same filters, order and limits as the PostgreSQL store (AuthorityStore), over the in-memory lists.
        public Task<List<SecurityCommand>> GetTimelineCommandsAsync(Guid organizationId, TimelineRange range, IReadOnlyCollection<string> kinds, IReadOnlyCollection<string>? states, int limit, CancellationToken cancellationToken)
        {
            Store.TimelineReads++;
            var rows = Store.Commands.Where(c => c.OrganizationId == organizationId && kinds.Contains(c.CommandKindName)
                    && (states is null || states.Contains(c.CommandStateName))
                    && (range.From is null || c.CommandCreatedOn >= range.From) && (range.To is null || c.CommandCreatedOn < range.To)
                    && (range.After is not { } after || (after.IsCommand
                        ? c.CommandCreatedOn < after.OccurredOn || (c.CommandCreatedOn == after.OccurredOn && string.CompareOrdinal(c.SecurityCommandId.ToString("D"), after.CommandId.ToString("D")) < 0)
                        : c.CommandCreatedOn < after.OccurredOn)))
                .OrderByDescending(c => c.CommandCreatedOn).ThenByDescending(c => c.SecurityCommandId.ToString("D"), StringComparer.Ordinal).Take(limit).ToList();
            return Task.FromResult(rows);
        }

        public Task<List<SecurityJournalEntry>> GetTimelineAuditEntriesAsync(Guid organizationId, TimelineRange range, IReadOnlyCollection<string> kinds, int limit, CancellationToken cancellationToken)
        {
            Store.TimelineReads++;
            var rows = Store.Journal.Where(e => e.OrganizationId == organizationId && e.JournalStreamName == JournalStreamNames.Audit && kinds.Contains(e.JournalEventKind)
                    && (range.From is null || e.JournalCreatedOn >= range.From) && (range.To is null || e.JournalCreatedOn < range.To)
                    && (range.After is not { } after || (after.IsCommand
                        ? e.JournalCreatedOn <= after.OccurredOn
                        : e.JournalCreatedOn < after.OccurredOn || (e.JournalCreatedOn == after.OccurredOn && e.JournalStreamRevision < after.StreamRevision))))
                .OrderByDescending(e => e.JournalCreatedOn).ThenByDescending(e => e.JournalStreamRevision).Take(limit).ToList();
            return Task.FromResult(rows);
        }

        public Task<List<SecurityJournalEntry>> GetAuthorizationEntriesAsync(Guid organizationId, IReadOnlyCollection<long> streamRevisions, CancellationToken cancellationToken) =>
            Task.FromResult(Store.Journal.Where(e => e.OrganizationId == organizationId && e.JournalStreamName == JournalStreamNames.Authorization && streamRevisions.Contains(e.JournalStreamRevision)).ToList());

        public Task<List<SecurityJournalEntry>> GetRejectionEntriesAsync(Guid organizationId, IReadOnlyCollection<string> kinds, IReadOnlyCollection<Guid> commandIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
            Task.FromResult(Store.Journal.Where(e => e.OrganizationId == organizationId && e.JournalStreamName == JournalStreamNames.Audit && kinds.Contains(e.JournalEventKind)
                && e.JournalCreatedOn >= windowStart && e.JournalCreatedOn <= windowEnd
                && Application.Features.Audit.SecurityCommandTimelineCatalog.NamedCommandId(e.JournalEventBodyJson) is { } id && commandIds.Contains(id)).ToList());

        public Task ApplyAuditAccessTimeoutsAsync(int statementTimeoutMilliseconds, int transactionTimeoutMilliseconds, CancellationToken cancellationToken)
        {
            Store.AppliedAuditAccessTimeouts.Add((statementTimeoutMilliseconds, transactionTimeoutMilliseconds));
            return Task.CompletedTask;
        }

        public Task<OrganizationLockAttempt> TryLockOrganizationNoWaitAsync(Guid organizationId, CancellationToken cancellationToken)
        {
            Store.NoWaitLockAttempts++;
            Store.BeforeNoWaitLock?.Invoke();
            if (Store.NoWaitLockBusyCount > 0)
            {
                Store.NoWaitLockBusyCount--;
                return Task.FromResult(OrganizationLockAttempt.LockBusy);
            }

            var state = Store.Organizations.FirstOrDefault(o => o.OrganizationId == organizationId);
            return Task.FromResult(state is null ? OrganizationLockAttempt.NotFound : OrganizationLockAttempt.Locked(state));
        }
    }

    private class Scope(InMemoryAuthorityStore s, bool readOnly = false) : IAuthorityScope, ISecurityOverviewScope
    {
        protected InMemoryAuthorityStore Store => s;

        // C8 M4-4 (AC-8.4): the same aggregates as the PostgreSQL store, over the in-memory lists.
        public Task<List<CommandStateTally>> CountCommandsByStateAsync(Guid organizationId, IReadOnlyCollection<string> states, CancellationToken cancellationToken) =>
            Task.FromResult(s.Commands.Where(c => c.OrganizationId == organizationId && states.Contains(c.CommandStateName)).GroupBy(c => c.CommandStateName)
                .Select(g => new CommandStateTally(g.Key, g.Count(), g.Min(c => c.CommandCreatedOn))).ToList());

        public Task<List<SecurityCommand>> GetOldestCommandsInStatesAsync(Guid organizationId, IReadOnlyCollection<string> states, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(s.Commands.Where(c => c.OrganizationId == organizationId && states.Contains(c.CommandStateName))
                .OrderBy(c => c.CommandCreatedOn).ThenBy(c => c.SecurityCommandId).Take(limit).ToList());

        public Task<List<ArchiveDeliveryTally>> CountJournalArchiveStatesAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Journal.Where(j => j.OrganizationId == organizationId).GroupBy(j => (j.JournalStreamName, j.ArchiveDeliveryState))
                .Select(g => new ArchiveDeliveryTally(g.Key.JournalStreamName, g.Key.ArchiveDeliveryState, g.Count(), g.Min(j => j.JournalCreatedOn), g.Max(j => j.JournalArchivedOn))).ToList());

        public ISearchIndexStore SearchIndex => s.SearchIndex;

        public ISearchAccessStore SearchAccess => s.SearchAccess;

        public Task<OrganizationAuthorizationState?> LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult(s.Organizations.FirstOrDefault(o => o.OrganizationId == organizationId));

        public Task RequireContentAcceptanceFloorAsync(CancellationToken cancellationToken)
        {
            s.ContentAcceptanceFenceChecks++;
            return s.ContentAcceptanceFenceRefusal is null ? Task.CompletedTask : throw AuthorizationAuthority.Application.Features.Content.ContentAcceptanceFloorFailures.Refusal();
        }

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

        public Task<ShareResourceMetadata?> FindShareMetadataAsync(Guid organizationId, Guid recordShareId, CancellationToken cancellationToken) =>
            Task.FromResult(s.ShareMetadata.SingleOrDefault(m => m.OrganizationId == organizationId && m.RecordShareId == recordShareId));

        public void AddShareMetadata(ShareResourceMetadata metadata) => s.ShareMetadata.Add(metadata);

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

        public Task<List<string>> GetCommandPayloadsAsync(Guid organizationId, IReadOnlyCollection<string> commandKinds, CancellationToken cancellationToken) =>
            Task.FromResult(s.Commands.Where(c => c.OrganizationId == organizationId && commandKinds.Contains(c.CommandKindName) && c.CommandStateName != SecurityCommandStates.Rejected).Select(c => c.CommandPayloadJson).ToList());

        public Task<SecurityCommand?> FindCommandAsync(Guid securityCommandId, CancellationToken cancellationToken) => Task.FromResult(s.Commands.FirstOrDefault(c => c.SecurityCommandId == securityCommandId));

        public void AddCommand(SecurityCommand command) => s.Commands.Add(command);

        public void AddOutboxEvent(ProjectionOutboxEvent outboxEvent) => s.Outbox.Add(outboxEvent);

        public Task<ProjectionOutboxEvent?> FindOutboxEventAsync(Guid organizationId, long targetAuthorizationRevision, CancellationToken cancellationToken) => Task.FromResult(s.Outbox.FirstOrDefault(o => o.OrganizationId == organizationId && o.TargetAuthorizationRevision == targetAuthorizationRevision));

        public void AddJournalEntry(SecurityJournalEntry entry) => s.Journal.Add(entry);

        public Task<SecurityJournalEntry?> FindContentCommitJournalEntryAsync(Guid organizationId, Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Journal.Where(j => j.OrganizationId == organizationId && j.JournalStreamName == JournalStreamNames.Content && j.ContentChangeOperationId == contentChangeOperationId && j.JournalEventKind == "ContentHeadCommitted")
                .OrderByDescending(j => j.JournalStreamRevision).FirstOrDefault());

        public Task<SecurityJournalEntry?> FindJournalEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            Task.FromResult(s.Journal.FirstOrDefault(j => j.OrganizationId == organizationId && j.JournalStreamName == streamName && j.JournalStreamRevision == streamRevision));

        public Task<List<SecurityJournalEntry>> GetPendingJournalEntriesAsync(Guid organizationId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(s.RealJournalDelivery
                ? s.Journal.Where(j => j.OrganizationId == organizationId && j.ArchiveDeliveryState == ArchiveDeliveryStates.Pending)
                    .OrderBy(j => j.JournalStreamName, StringComparer.Ordinal).ThenBy(j => j.JournalStreamRevision).Take(limit).ToList()
                : new List<SecurityJournalEntry>());

        public Task<SecurityJournalEntry?> FindLatestJournalEntryAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) =>
            Task.FromResult(s.Journal.Where(j => j.OrganizationId == organizationId && j.JournalStreamName == streamName).OrderByDescending(j => j.JournalStreamRevision).FirstOrDefault());

        public IJournalReceiptVerifier JournalReceipts => s.ReceiptVerifier ?? (s.RealJournalDelivery ? NotComposedJournalReceiptVerifier.Instance : AssumeDeliveredTestReceipts.Instance);

        public Task<int> CountPendingArchiveAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) =>
            Task.FromResult(s.RealJournalDelivery ? s.Journal.Count(j => j.OrganizationId == organizationId && j.JournalStreamName == streamName && j.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored) : 0);

        public Task<RecordContentHead?> FindContentHeadAsync(Guid organizationId, Guid formRecordId, bool lockForUpdate, CancellationToken cancellationToken) =>
            Task.FromResult(s.Heads.FirstOrDefault(h => h.OrganizationId == organizationId && h.FormRecordId == formRecordId));

        public void AddContentHead(RecordContentHead head) => s.Heads.Add(head);

        public Task<ContentChangeOperation?> FindOperationByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(s.Operations.FirstOrDefault(o => o.OrganizationId == organizationId && o.OperationIdempotencyKey == idempotencyKey));

        public Task<ContentChangeOperation?> FindOperationAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Operations.FirstOrDefault(o => o.ContentChangeOperationId == contentChangeOperationId));

        public void AddOperation(ContentChangeOperation operation)
        {
            s.Operations.Add(operation);
            addedOperations.Add(operation);
        }

        public void AddCandidateLeaves(IEnumerable<ContentCandidateLeaf> leaves)
        {
            var list = leaves.ToList();
            s.CandidateLeaves.AddRange(list);
            addedLeaves.AddRange(list);
        }

        public Task<List<ContentCandidateLeaf>> GetCandidateLeavesAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.CandidateLeaves.Where(l => l.ContentChangeOperationId == contentChangeOperationId).ToList());

        public void AddManifestCommit(RecordManifestCommit commit) => s.Commits.Add(commit);

        public void AddManifestLeaves(IEnumerable<RecordManifestLeaf> leaves) => s.ManifestLeaves.AddRange(leaves);

        public Task<List<ContentItemIdentity>> FindItemIdentitiesAsync(IReadOnlyCollection<Guid> manifestItemIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.ItemIdentities.Where(i => manifestItemIds.Contains(i.ManifestItemId)).ToList());

        public void AddItemIdentities(IEnumerable<ContentItemIdentity> identities)
        {
            foreach (var identity in identities)
            {
                if (s.ItemIdentities.Any(i => i.ManifestItemId == identity.ManifestItemId))
                {
                    throw new InvalidOperationException("uq_content_item_identity_item");
                }

                s.ItemIdentities.Add(identity);
            }
        }

        public Task<List<ContentDataKeyBinding>> FindDataKeyBindingsAsync(IReadOnlyCollection<Guid> dataKeyIdentifiers, CancellationToken cancellationToken) =>
            Task.FromResult(s.DataKeyBindings.Where(b => dataKeyIdentifiers.Contains(b.DataKeyIdentifier)).ToList());

        public Task<List<ContentDataKeyBinding>> GetDataKeyBindingsForOperationAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.DataKeyBindings.Where(b => b.BindingContentChangeOperationId == contentChangeOperationId).ToList());

        /// <summary>Unique identifier and P-1 emulation; a refusal also removes the operation and leaves this scope added (the transaction rollback).</summary>
        public void AddDataKeyBindings(IEnumerable<ContentDataKeyBinding> bindings)
        {
            var list = bindings.ToList();
            foreach (var binding in list)
            {
                var operation = s.Operations.FirstOrDefault(o => o.ContentChangeOperationId == binding.BindingContentChangeOperationId);
                if (operation is null || operation.OperationStateName != ContentOperationStates.Prepared || operation.OrganizationId != binding.OrganizationId
                    || operation.FormRecordId != binding.FormRecordId || operation.PrepareAcceptanceRuleVersion is not >= 3)
                {
                    RollBackScopeAdds();
                    throw AuthorizationAuthority.Application.Features.Content.DataKeyLedgerFailures.InvariantViolated();
                }

                if (s.DataKeyBindings.Any(b => b.DataKeyIdentifier == binding.DataKeyIdentifier) || list.Count(b => b.DataKeyIdentifier == binding.DataKeyIdentifier) > 1)
                {
                    RollBackScopeAdds();
                    throw AuthorizationAuthority.Application.Features.Content.EnvelopeRules.Refused(AuthorizationAuthority.Application.Features.Content.EnvelopeV2Rules.Reasons.DataKeyIdentifierReused);
                }
            }

            s.DataKeyBindings.AddRange(list);
        }

        public Task<bool> OrganizationHasDataKeyBindingsAsync(Guid organizationId, CancellationToken cancellationToken)
        {
            s.DataKeyBindingExistenceQueries++;
            return s.DataKeyBindingExistenceFailure is { } failure ? throw failure : Task.FromResult(s.DataKeyBindings.Any(b => b.OrganizationId == organizationId));
        }

        // E3-M2f-3b: the evidence port over the in-memory journal (no rollback: use CapabilityRefusalEvidenceWorld for T2 semantics).
        public Task SetCapabilityEvidenceLockTimeoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DateTimeOffset> StartCapabilityEvidenceLookupAsync(CancellationToken cancellationToken) => Task.FromResult(s.EvidenceDatabaseClock());

        public Task<bool> IsCapabilityRefusalEvidenceIndexUsableAsync(CancellationToken cancellationToken) => Task.FromResult(s.EvidenceIndexUsable);

        public Task<IReadOnlyList<CapabilityRefusalEvidenceRow>> GetNewestCapabilityRefusalEvidenceAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityRefusalEvidenceRow>>(s.Journal
                .Where(e => e.OrganizationId == organizationId && e.JournalStreamName == CapabilityRefusalEvidence.StreamName && e.JournalEventKind == CapabilityRefusalEvidence.EventKind)
                .OrderByDescending(e => e.JournalStreamRevision).Take(CapabilityRefusalEvidence.LookupLimit)
                .Select(e => new CapabilityRefusalEvidenceRow(e.SecurityJournalEntryId, e.JournalStreamRevision, e.JournalEventBodyJson, e.JournalEventDigest)).ToList());

        public Task RequireDataKeyLedgerAsync(CancellationToken cancellationToken)
        {
            s.DataKeyLedgerChecks++;
            return s.DataKeyLedgerRefused ? throw AuthorizationAuthority.Application.Features.Content.DataKeyLedgerFailures.NotEnforced() : Task.CompletedTask;
        }

        private readonly List<ContentChangeOperation> addedOperations = [];
        private readonly List<ContentCandidateLeaf> addedLeaves = [];

        private void RollBackScopeAdds()
        {
            foreach (var operation in addedOperations)
            {
                s.Operations.Remove(operation);
            }

            foreach (var leaf in addedLeaves)
            {
                s.CandidateLeaves.Remove(leaf);
            }
        }

        public Task<List<Guid>> FindAcceptedItemIdsAsync(IReadOnlyCollection<Guid> manifestItemIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.ManifestLeaves.Where(l => manifestItemIds.Contains(l.ManifestItemId)).Select(l => l.ManifestItemId).Distinct().ToList());

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

        public Task<IntegrityKeyRegistration?> FindKeyRegistrationForUpdateAsync(Guid organizationId, string keyPurposeName, CancellationToken cancellationToken) =>
            FindKeyRegistrationAsync(organizationId, keyPurposeName, cancellationToken);

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

        public Task<List<PublishedFormVersion>> GetPublishedFormVersionsAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
            Task.FromResult(s.PublishedVersions.Where(v => v.OrganizationId == organizationId && v.FormDefinitionId == formDefinitionId).OrderByDescending(v => v.FormVersionNumber).ToList());

        public Task<FieldFormBinding?> FindFieldFormBindingAsync(Guid organizationId, Guid fieldDefinitionId, CancellationToken cancellationToken) =>
            Task.FromResult(s.FieldFormBindings.FirstOrDefault(b => b.OrganizationId == organizationId && b.FieldDefinitionId == fieldDefinitionId));

        public void AddFieldFormBinding(FieldFormBinding binding) => s.AddFieldFormBindingForTest(binding);

        public Task<FormVersionPublication?> FindPendingFormVersionPublicationAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Publications.FirstOrDefault(p => p.OrganizationId == organizationId && p.FormDefinitionId == formDefinitionId && p.PublicationStateName == FormVersionPublicationStates.Pending));

        public void AddFormVersionPublication(FormVersionPublication publication)
        {
            if (publication.PublicationStateName == FormVersionPublicationStates.Pending
                && s.Publications.Any(p => p.OrganizationId == publication.OrganizationId && p.FormDefinitionId == publication.FormDefinitionId && p.PublicationStateName == FormVersionPublicationStates.Pending))
            {
                throw new InvalidOperationException("duplicate key value violates unique constraint uq_form_version_publication_pending");
            }

            s.Publications.Add(publication);
        }

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

        public Task<List<PlatformJournalEntry>> GetLatestPlatformJournalEntriesAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult(s.PlatformJournal.OrderByDescending(e => e.PlatformJournalRevision).Take(limit).ToList());

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

        // ---- C8 onboarding ----

        public Task<OrganizationOnboardingRequest?> FindOnboardingRequestAsync(Guid organizationOnboardingRequestId, bool lockForUpdate, CancellationToken cancellationToken) =>
            Task.FromResult(s.OnboardingRequests.FirstOrDefault(r => r.OrganizationOnboardingRequestId == organizationOnboardingRequestId));

        public Task<OrganizationOnboardingRequest?> FindOnboardingRequestByIdempotencyKeyAsync(Guid platformOperatorId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(s.OnboardingRequests.FirstOrDefault(r => r.CreatedByPlatformOperatorId == platformOperatorId && r.CreateIdempotencyKey == idempotencyKey));

        public Task<OrganizationOnboardingRequest?> FindOnboardingRequestByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.OnboardingRequests.FirstOrDefault(r => r.ReservedOrganizationId == organizationId));

        public Task<List<OrganizationOnboardingRequest>> GetOpenOnboardingRequestsByNameAsync(string normalizedName, CancellationToken cancellationToken) =>
            Task.FromResult(s.OnboardingRequests.Where(r => r.OrganizationNormalizedName == normalizedName && OrganizationOnboardingStates.Open.Contains(r.OnboardingStateName)).ToList());

        public void AddOnboardingRequest(OrganizationOnboardingRequest request)
        {
            // The partial unique indexes of the real store: open name, reserved organization, idempotency key.
            if (s.OnboardingRequests.Any(r => r.ReservedOrganizationId == request.ReservedOrganizationId
                                               || (r.CreatedByPlatformOperatorId == request.CreatedByPlatformOperatorId && r.CreateIdempotencyKey == request.CreateIdempotencyKey)
                                               || (r.OrganizationNormalizedName == request.OrganizationNormalizedName && OrganizationOnboardingStates.Open.Contains(r.OnboardingStateName) && OrganizationOnboardingStates.Open.Contains(request.OnboardingStateName))))
            {
                throw new InvalidOperationException("unique index violation (onboarding request)");
            }

            s.OnboardingRequests.Add(request);
        }

        public Task<List<OrganizationOnboardingOwner>> GetOnboardingOwnersAsync(Guid organizationOnboardingRequestId, CancellationToken cancellationToken) =>
            Task.FromResult(s.OnboardingOwners.Where(o => o.OrganizationOnboardingRequestId == organizationOnboardingRequestId).OrderBy(o => o.OwnerOrdinalNumber).ToList());

        public Task<OrganizationOnboardingOwner?> FindOnboardingOwnerAsync(Guid organizationOnboardingOwnerId, CancellationToken cancellationToken) =>
            Task.FromResult(s.OnboardingOwners.FirstOrDefault(o => o.OrganizationOnboardingOwnerId == organizationOnboardingOwnerId));

        public void AddOnboardingOwner(OrganizationOnboardingOwner owner) => s.OnboardingOwners.Add(owner);

        public Task<List<OrganizationOnboardingApproval>> GetOnboardingApprovalsAsync(Guid organizationOnboardingRequestId, CancellationToken cancellationToken) =>
            Task.FromResult(s.OnboardingApprovals.Where(a => a.OrganizationOnboardingRequestId == organizationOnboardingRequestId).OrderBy(a => a.ApprovalRecordedOn).ThenBy(a => a.OrganizationOnboardingApprovalId).ToList());

        public Task<List<OrganizationOnboardingApproval>> GetOnboardingApprovalsForRequestsAsync(IReadOnlyCollection<Guid> organizationOnboardingRequestIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.OnboardingApprovals.Where(a => organizationOnboardingRequestIds.Contains(a.OrganizationOnboardingRequestId)).ToList());

        public Task<OrganizationOnboardingApproval?> FindOnboardingApprovalAsync(Guid organizationOnboardingApprovalId, CancellationToken cancellationToken) =>
            Task.FromResult(s.OnboardingApprovals.FirstOrDefault(a => a.OrganizationOnboardingApprovalId == organizationOnboardingApprovalId));

        public void AddOnboardingApproval(OrganizationOnboardingApproval approval)
        {
            if (s.OnboardingApprovals.Any(a => a.ApprovalEvidenceSha256.AsSpan().SequenceEqual(approval.ApprovalEvidenceSha256) || a.ApprovalEvidenceIdentifier == approval.ApprovalEvidenceIdentifier
                                               || (a.OrganizationOnboardingRequestId == approval.OrganizationOnboardingRequestId && a.PlatformOperatorId == approval.PlatformOperatorId
                                                   && a.ApprovalStateName is OnboardingApprovalStates.Valid or OnboardingApprovalStates.Consumed)))
            {
                throw new InvalidOperationException("unique index violation (onboarding approval)");
            }

            s.OnboardingApprovals.Add(approval);
        }

        public Task<OnboardingRequestStorePage> SearchOnboardingRequestsAsync(OnboardingRequestQuery query, CancellationToken cancellationToken)
        {
            var now = query.Now;
            var all = s.OnboardingRequests.Where(r => query.Status switch
            {
                OrganizationOnboardingFilters.NeedsApproval => r.OnboardingStateName == OrganizationOnboardingStates.PendingApproval && r.RequestExpiresOn > now,
                OrganizationOnboardingFilters.AwaitingKeyCustody => r.OnboardingStateName == OrganizationOnboardingStates.AwaitingKeyCustody && r.KeyCustodyExpiresOn > now,
                OrganizationOnboardingFilters.Initialized => r.OnboardingStateName == OrganizationOnboardingStates.Initialized,
                OrganizationOnboardingFilters.Closed => OrganizationOnboardingStates.IsClosed(r.OnboardingStateName)
                                                        || (r.OnboardingStateName == OrganizationOnboardingStates.PendingApproval && r.RequestExpiresOn <= now)
                                                        || (r.OnboardingStateName == OrganizationOnboardingStates.AwaitingKeyCustody && r.KeyCustodyExpiresOn <= now),
                _ => true,
            }).Where(r => string.IsNullOrWhiteSpace(query.Text) || r.OrganizationDisplayName.Contains(query.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            var ordered = all.OrderByDescending(r => r.RequestCreatedOn).ThenByDescending(r => r.OrganizationOnboardingRequestId).AsEnumerable();
            if (query.After is { } after)
            {
                ordered = ordered.Where(r => r.RequestCreatedOn < after.CreatedOn || (r.RequestCreatedOn == after.CreatedOn && r.OrganizationOnboardingRequestId.CompareTo(after.RequestId) < 0));
            }

            return Task.FromResult(new OnboardingRequestStorePage(ordered.Take(query.Limit).ToList(), all.Count));
        }

        public Task<List<OrganizationDirectoryRow>> GetOrganizationDirectoryRowsAsync(CancellationToken cancellationToken)
        {
            int Count(Guid org, string role) => s.Edges.Count(e => e.OrganizationId == org && e.ResourceTypeName == ResourceTypeNames.Organization && e.ResourceEntityId == org && e.RelationshipName == role
                                                                   && e.SubjectTypeName == ResourceTypeNames.User && e.RelationshipRemovedOn is null
                                                                   && s.Bindings.Any(b => b.OrganizationId == org && b.AuthorityUserId == e.SubjectEntityId && b.IdentityBindingEnabled));
            return Task.FromResult(s.Organizations.OrderBy(o => o.OrganizationDisplayName, StringComparer.Ordinal).Select(o => new OrganizationDirectoryRow(o, Count(o.OrganizationId, OrganizationRoleNames.OrganizationOwner),
                Count(o.OrganizationId, OrganizationRoleNames.SystemAdministrator), s.Bindings.Count(b => b.OrganizationId == o.OrganizationId && b.IdentityBindingEnabled))).ToList());
        }

        public Task<int> CountOrganizationsBindingIdentityAsync(string issuerUri, string subjectIdentifier, CancellationToken cancellationToken) =>
            Task.FromResult(s.Bindings.Where(b => b.IdentityIssuerUri == issuerUri && b.IdentitySubjectIdentifier == subjectIdentifier).Select(b => b.OrganizationId).Distinct().Count());

        // ---- C8 owner recovery ----

        public Task<OrganizationOwnerRecoveryCase?> FindOwnerRecoveryCaseAsync(Guid organizationOwnerRecoveryCaseId, bool lockForUpdate, CancellationToken cancellationToken) =>
            Task.FromResult(s.RecoveryCases.FirstOrDefault(c => c.OrganizationOwnerRecoveryCaseId == organizationOwnerRecoveryCaseId));

        public Task<OrganizationOwnerRecoveryCase?> FindOwnerRecoveryCaseByIdempotencyKeyAsync(Guid platformOperatorId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(s.RecoveryCases.FirstOrDefault(c => c.OpenedByPlatformOperatorId == platformOperatorId && c.CreateIdempotencyKey == idempotencyKey));

        public Task<OrganizationOwnerRecoveryCase?> FindOpenOwnerRecoveryCaseAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.RecoveryCases.FirstOrDefault(c => c.OrganizationId == organizationId && c.RecoveryStateName == OwnerRecoveryStates.Open));

        public void AddOwnerRecoveryCase(OrganizationOwnerRecoveryCase recoveryCase)
        {
            if (s.RecoveryCases.Any(c => (c.OrganizationId == recoveryCase.OrganizationId && c.RecoveryStateName == OwnerRecoveryStates.Open && recoveryCase.RecoveryStateName == OwnerRecoveryStates.Open)
                                         || (c.OpenedByPlatformOperatorId == recoveryCase.OpenedByPlatformOperatorId && c.CreateIdempotencyKey == recoveryCase.CreateIdempotencyKey)))
            {
                throw new InvalidOperationException("unique index violation (owner recovery case)");
            }

            s.RecoveryCases.Add(recoveryCase);
        }

        public Task<List<OrganizationOwnerRecoverySubject>> GetOwnerRecoverySubjectsAsync(Guid organizationOwnerRecoveryCaseId, CancellationToken cancellationToken) =>
            Task.FromResult(s.RecoverySubjects.Where(x => x.OrganizationOwnerRecoveryCaseId == organizationOwnerRecoveryCaseId).OrderBy(x => x.RecoverySubjectRoleName, StringComparer.Ordinal).ThenBy(x => x.SubjectOrdinalNumber).ToList());

        public Task<OrganizationOwnerRecoverySubject?> FindOwnerRecoverySubjectAsync(Guid organizationOwnerRecoverySubjectId, CancellationToken cancellationToken) =>
            Task.FromResult(s.RecoverySubjects.FirstOrDefault(x => x.OrganizationOwnerRecoverySubjectId == organizationOwnerRecoverySubjectId));

        public void AddOwnerRecoverySubject(OrganizationOwnerRecoverySubject subject) => s.RecoverySubjects.Add(subject);

        public Task<List<OrganizationOwnerRecoveryApproval>> GetOwnerRecoveryApprovalsAsync(Guid organizationOwnerRecoveryCaseId, CancellationToken cancellationToken) =>
            Task.FromResult(s.RecoveryApprovals.Where(a => a.OrganizationOwnerRecoveryCaseId == organizationOwnerRecoveryCaseId).OrderBy(a => a.ApprovalRecordedOn).ThenBy(a => a.OrganizationOwnerRecoveryApprovalId).ToList());

        public Task<List<OrganizationOwnerRecoveryApproval>> GetOwnerRecoveryApprovalsForCasesAsync(IReadOnlyCollection<Guid> organizationOwnerRecoveryCaseIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.RecoveryApprovals.Where(a => organizationOwnerRecoveryCaseIds.Contains(a.OrganizationOwnerRecoveryCaseId)).ToList());

        public Task<OrganizationOwnerRecoveryApproval?> FindOwnerRecoveryApprovalAsync(Guid organizationOwnerRecoveryApprovalId, CancellationToken cancellationToken) =>
            Task.FromResult(s.RecoveryApprovals.FirstOrDefault(a => a.OrganizationOwnerRecoveryApprovalId == organizationOwnerRecoveryApprovalId));

        public void AddOwnerRecoveryApproval(OrganizationOwnerRecoveryApproval approval)
        {
            if (s.RecoveryApprovals.Any(a => a.ApprovalEvidenceSha256.AsSpan().SequenceEqual(approval.ApprovalEvidenceSha256) || a.ApprovalEvidenceIdentifier == approval.ApprovalEvidenceIdentifier
                                             || (a.OrganizationOwnerRecoveryCaseId == approval.OrganizationOwnerRecoveryCaseId && a.PlatformOperatorId == approval.PlatformOperatorId
                                                 && a.ApprovalStateName is OnboardingApprovalStates.Valid or OnboardingApprovalStates.Consumed)))
            {
                throw new InvalidOperationException("unique index violation (owner recovery approval)");
            }

            s.RecoveryApprovals.Add(approval);
        }

        public Task<OwnerRecoveryStorePage> SearchOwnerRecoveryCasesAsync(OwnerRecoveryQuery query, CancellationToken cancellationToken)
        {
            var now = query.Now;
            int Valid(OrganizationOwnerRecoveryCase c) => s.RecoveryApprovals.Count(a => a.OrganizationOwnerRecoveryCaseId == c.OrganizationOwnerRecoveryCaseId && a.ApprovalStateName == OnboardingApprovalStates.Valid);
            bool Open(OrganizationOwnerRecoveryCase c) => c.RecoveryStateName == OwnerRecoveryStates.Open && c.CaseExpiresOn > now;
            var all = s.RecoveryCases.Where(c => query.Status switch
            {
                OwnerRecoveryFilters.Open => Open(c),
                OwnerRecoveryFilters.AwaitingApprovals => Open(c) && Valid(c) < 3,
                OwnerRecoveryFilters.WaitingPeriod => Open(c) && Valid(c) >= 3 && c.WaitingPeriodEndsOn > now,
                OwnerRecoveryFilters.Ready => Open(c) && Valid(c) >= 3 && c.WaitingPeriodEndsOn <= now,
                OwnerRecoveryFilters.Objected => c.RecoveryStateName == OwnerRecoveryStates.Objected,
                OwnerRecoveryFilters.Recovered => c.RecoveryStateName == OwnerRecoveryStates.Recovered,
                OwnerRecoveryFilters.Closed => c.RecoveryStateName is OwnerRecoveryStates.Cancelled or OwnerRecoveryStates.Expired or OwnerRecoveryStates.Invalidated || (c.RecoveryStateName == OwnerRecoveryStates.Open && c.CaseExpiresOn <= now),
                _ => true,
            }).Select(c => new OwnerRecoveryStoreRow(c, s.Organizations.Single(o => o.OrganizationId == c.OrganizationId).OrganizationDisplayName, Valid(c)))
              .Where(r => string.IsNullOrWhiteSpace(query.Text) || r.OrganizationDisplayName.Contains(query.Text, StringComparison.OrdinalIgnoreCase) || r.Case.IncidentReferenceText.Contains(query.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            var ordered = all.OrderByDescending(r => r.Case.CaseOpenedOn).ThenByDescending(r => r.Case.OrganizationOwnerRecoveryCaseId).AsEnumerable();
            if (query.After is { } after)
            {
                ordered = ordered.Where(r => r.Case.CaseOpenedOn < after.OpenedOn || (r.Case.CaseOpenedOn == after.OpenedOn && r.Case.OrganizationOwnerRecoveryCaseId.CompareTo(after.CaseId) < 0));
            }

            return Task.FromResult(new OwnerRecoveryStorePage(ordered.Take(query.Limit).ToList(), all.Count));
        }

        public Task<List<OrganizationRecoveredOwnerProbation>> GetActiveRecoveredOwnerProbationsAsync(Guid organizationId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(s.RecoveryProbations.Where(p => p.OrganizationId == organizationId && p.ProbationEndsOn > now).ToList());

        public Task<OrganizationRecoveredOwnerProbation?> FindRecoveredOwnerProbationAsync(Guid organizationRecoveredOwnerProbationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.RecoveryProbations.FirstOrDefault(p => p.OrganizationRecoveredOwnerProbationId == organizationRecoveredOwnerProbationId));

        public void AddRecoveredOwnerProbation(OrganizationRecoveredOwnerProbation probation) => s.RecoveryProbations.Add(probation);

        public Task<int> CountSessionsCreatedSinceAsync(Guid identityBindingId, DateTimeOffset since, CancellationToken cancellationToken) =>
            Task.FromResult(s.Sessions.Count(x => x.IdentityBindingId == identityBindingId && x.SessionCreatedOn >= since));

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (readOnly)
            {
                throw new InvalidOperationException("read-only snapshot scope cannot commit");
            }

            if (s.FailNextCommit is { } failure)
            {
                s.FailNextCommit = null;
                throw failure;
            }

            s.ScopeCommitCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
