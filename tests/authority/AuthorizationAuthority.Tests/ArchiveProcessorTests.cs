using AuthorizationAuthority.Domain.Features.Catalogue;
using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Archive delivery invariants (G14, SEC-P12): the body that leaves the authority is byte-identical to the digested,
/// hash-chained body; redelivery is idempotent; a tampered or non-canonical body quarantines the organization instead
/// of being archived; an outage leaves the entry Pending without quarantine. A-F12 regression: the body column was
/// jsonb, so every archived body differed from its digest and the Azurite 409 read-back path quarantined SEC-P12.
/// </summary>
public sealed class ArchiveProcessorTests
{
    private static readonly Guid Org = Guid.CreateVersion7();

    [Fact]
    public void JournalChainDigestIsTheSha256OfTheExactStoredBodyBytes()
    {
        var state = new OrganizationAuthorizationState { OrganizationId = Org };
        var entry = JournalChain.Append(state, JournalStreamNames.Authorization, 1, "Test", new { value = "Café", n = 1 }, null, null, TimeProvider.System);
        Assert.Equal(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.JournalEventBodyJson)), entry.JournalEventDigest);
        Assert.Equal(entry.JournalEventDigest, state.LatestSecurityJournalDigest);
    }

    [Fact]
    public async Task CanonicalEntryIsAnchoredAndRedeliveryOfTheSameBytesIsIdempotent()
    {
        var store = new FakeStore(Org);
        var entry = store.AddPending(1);
        var archive = new InMemorySecurityEventArchive();
        var processor = new ArchiveProcessor(store, archive, TimeProvider.System);

        Assert.Equal(1, await processor.ProcessOrganizationAsync(Org, CancellationToken.None));
        Assert.Equal(ArchiveDeliveryStates.Anchored, entry.ArchiveDeliveryState);
        Assert.NotNull(entry.ArchiveReceiptReference);

        entry.ArchiveDeliveryState = ArchiveDeliveryStates.Pending; // simulate a lost acknowledgement
        Assert.Equal(1, await processor.ProcessOrganizationAsync(Org, CancellationToken.None));
        Assert.Equal(ArchiveDeliveryStates.Anchored, entry.ArchiveDeliveryState);
        Assert.False(store.State.IntegrityQuarantineEnabled);
    }

    [Fact]
    public async Task BodyThatNoLongerMatchesItsDigestIsNeverArchivedAndQuarantinesTheOrganization()
    {
        var store = new FakeStore(Org);
        var entry = store.AddPending(1);
        entry.JournalEventBodyJson = entry.JournalEventBodyJson.Replace("Test", "Tampered", StringComparison.Ordinal);
        var archive = new InMemorySecurityEventArchive();
        var processor = new ArchiveProcessor(store, archive, TimeProvider.System);

        Assert.Equal(0, await processor.ProcessOrganizationAsync(Org, CancellationToken.None));
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, entry.ArchiveDeliveryState);
        Assert.Equal(ArchiveProcessor.JournalBodyDigestMismatch, entry.LastArchiveErrorCode);
        Assert.True(store.State.IntegrityQuarantineEnabled);
        Assert.Equal(ArchiveProcessor.JournalBodyDigestMismatch, store.State.IntegrityQuarantineReasonCode);
        Assert.Null(entry.ArchiveReceiptReference);
    }

    [Fact]
    public async Task ArchivedBytesThatDifferFromTheJournalAreAConflictNotAnOverwrite()
    {
        var store = new FakeStore(Org);
        var entry = store.AddPending(1);
        var archive = new InMemorySecurityEventArchive();
        var other = System.Text.Encoding.UTF8.GetBytes("{\"forged\":true}");
        await archive.AppendAsync(Org, JournalStreamNames.Authorization, 1, other, SHA256.HashData(other), CancellationToken.None);
        var processor = new ArchiveProcessor(store, archive, TimeProvider.System);

        Assert.Equal(0, await processor.ProcessOrganizationAsync(Org, CancellationToken.None));
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, entry.ArchiveDeliveryState);
        Assert.Equal("ARCHIVE_BODY_MISMATCH", store.State.IntegrityQuarantineReasonCode);
    }

    [Fact]
    public async Task ArchiveOutageKeepsTheEntryPendingWithoutQuarantine()
    {
        var store = new FakeStore(Org);
        var entry = store.AddPending(1);
        var processor = new ArchiveProcessor(store, new UnavailableSecurityEventArchive("paused"), TimeProvider.System);

        Assert.Equal(0, await processor.ProcessOrganizationAsync(Org, CancellationToken.None));
        Assert.Equal(ArchiveDeliveryStates.Pending, entry.ArchiveDeliveryState);
        Assert.Equal("paused", entry.LastArchiveErrorCode);
        Assert.Equal(1, entry.ArchiveAttemptCount);
        Assert.False(store.State.IntegrityQuarantineEnabled);
    }

    private sealed class FakeStore(Guid organizationId) : IAuthorityStore, IAuthorityScope
    {
        public OrganizationAuthorizationState State { get; } = new() { OrganizationId = organizationId };

        public List<SecurityJournalEntry> Entries { get; } = [];

        public SecurityJournalEntry AddPending(long revision)
        {
            var entry = JournalChain.Append(State, JournalStreamNames.Authorization, revision, "Test", new { kind = "Test", revision }, null, null, TimeProvider.System);
            Entries.Add(entry);
            return entry;
        }

        public Task<IAuthorityScope> BeginScopeAsync(CancellationToken ct) => Task.FromResult<IAuthorityScope>(this);

        public Task<IReadOnlyList<Guid>> GetOrganizationsWithPendingWorkAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([organizationId]);

        public Task<OrganizationAuthorizationState?> LockOrganizationAsync(Guid org, CancellationToken ct) => Task.FromResult<OrganizationAuthorizationState?>(org == organizationId ? State : null);

        public Task<List<SecurityJournalEntry>> GetPendingJournalEntriesAsync(Guid org, int limit, CancellationToken ct) => Task.FromResult(Entries.Where(e => e.OrganizationId == org && e.ArchiveDeliveryState == ArchiveDeliveryStates.Pending).Take(limit).ToList());

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<OrganizationAuthorizationState?> ReadOrganizationAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public void AddOrganization(OrganizationAuthorizationState state) => throw new NotSupportedException();

        public Task<IdentityBinding?> FindIdentityBindingAsync(Guid org, string issuer, string subject, CancellationToken ct) => throw new NotSupportedException();

        public Task<IdentityBinding?> FindIdentityBindingByUserAsync(Guid org, Guid userId, CancellationToken ct) => throw new NotSupportedException();

        public void AddIdentityBinding(IdentityBinding binding) => throw new NotSupportedException();

        public Task<AuthorityResource?> FindResourceAsync(Guid org, string type, Guid id, CancellationToken ct) => throw new NotSupportedException();

        public void AddResource(AuthorityResource resource) => throw new NotSupportedException();

        public Task<List<AuthorityResource>> GetActiveResourcesByParentAsync(Guid org, Guid parent, IReadOnlyCollection<string> types, CancellationToken ct) => Task.FromResult(new List<AuthorityResource>());

        public Task<OrganizationProtectionPolicy?> FindProtectionPolicyAsync(Guid org, CancellationToken ct) => Task.FromResult<OrganizationProtectionPolicy?>(null);

        public void AddProtectionPolicy(OrganizationProtectionPolicy policy) => throw new NotSupportedException();

        public Task<List<AuthorityRelationshipEdge>> GetActiveEdgesAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public void AddEdge(AuthorityRelationshipEdge edge) => throw new NotSupportedException();

        public Task<SecurityCommand?> FindCommandByIdempotencyKeyAsync(Guid org, string key, CancellationToken ct) => throw new NotSupportedException();

        public Task<SecurityCommand?> FindCommandAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public void AddCommand(SecurityCommand command) => throw new NotSupportedException();

        public void AddOutboxEvent(ProjectionOutboxEvent outboxEvent) => throw new NotSupportedException();

        public Task<ProjectionOutboxEvent?> FindOutboxEventAsync(Guid org, long revision, CancellationToken ct) => throw new NotSupportedException();

        public void AddJournalEntry(SecurityJournalEntry entry) => throw new NotSupportedException();

        public Task<SecurityJournalEntry?> FindContentCommitJournalEntryAsync(Guid org, Guid operation, CancellationToken ct) => throw new NotSupportedException();

        public Task<SecurityJournalEntry?> FindJournalEntryAsync(Guid org, string stream, long revision, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> CountPendingArchiveAsync(Guid org, string stream, CancellationToken ct) => throw new NotSupportedException();

        public Task<RecordContentHead?> FindContentHeadAsync(Guid org, Guid recordId, bool lockForUpdate, CancellationToken ct) => throw new NotSupportedException();

        public void AddContentHead(RecordContentHead head) => throw new NotSupportedException();

        public Task<ContentChangeOperation?> FindOperationByIdempotencyKeyAsync(Guid org, string key, CancellationToken ct) => throw new NotSupportedException();

        public Task<ContentChangeOperation?> FindOperationAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public void AddOperation(ContentChangeOperation operation) => throw new NotSupportedException();

        public void AddCandidateLeaves(IEnumerable<ContentCandidateLeaf> leaves) => throw new NotSupportedException();

        public Task<List<ContentCandidateLeaf>> GetCandidateLeavesAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public void AddManifestCommit(RecordManifestCommit commit) => throw new NotSupportedException();

        public void AddManifestLeaves(IEnumerable<RecordManifestLeaf> leaves) => throw new NotSupportedException();

        public Task<List<RecordManifestLeaf>> GetManifestLeavesAsync(Guid commitId, CancellationToken ct) => throw new NotSupportedException();

        public Task<RecordManifestCommit?> FindManifestCommitByOperationAsync(Guid organizationId, Guid formRecordId, Guid committedOperationId, CancellationToken ct) => Task.FromResult<RecordManifestCommit?>(null);

        public Task<List<RecordManifestCommit>> GetManifestCommitsAsync(Guid organizationId, Guid formRecordId, long afterRevision, int limit, CancellationToken ct) => Task.FromResult(new List<RecordManifestCommit>());

        public Task<List<ContentChangeOperation>> GetOperationsAsync(IReadOnlyCollection<Guid> contentChangeOperationIds, CancellationToken ct) => Task.FromResult(new List<ContentChangeOperation>());

        public Task<IdentityBinding?> FindIdentityBindingByIdAsync(Guid identityBindingId, CancellationToken ct) => Task.FromResult<IdentityBinding?>(null);

        public void AddContentCheckpoint(ContentIntegrityCheckpoint checkpoint) => throw new NotSupportedException();

        public Task<IntegrityKeyRegistration?> FindKeyRegistrationAsync(Guid org, string purpose, CancellationToken ct) => throw new NotSupportedException();

        public void AddKeyRegistration(IntegrityKeyRegistration registration) => throw new NotSupportedException();

        public Task<List<Guid>> GetActiveRecordIdsAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<AuthorityResource>> GetFormRecordResourcesAsync(Guid org, Guid form, IReadOnlyCollection<string> states, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<AuthorityResource>> GetResourcesAsync(Guid org, string type, IReadOnlyCollection<Guid> ids, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<RecordContentHead>> GetContentHeadsAsync(Guid org, IReadOnlyCollection<Guid> ids, CancellationToken ct) => throw new NotSupportedException();

        public void AddCatalogueSnapshot(SearchCatalogueSnapshot snapshot, IEnumerable<SearchCatalogueSnapshotEntry> entries) => throw new NotSupportedException();

        public Task<SearchCatalogueSnapshot?> FindCatalogueSnapshotAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesAsync(Guid id, int offset, int? limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.Sessions.AuthenticatedSession?> FindSessionByDigestAsync(byte[] digest, CancellationToken ct) => throw new NotSupportedException();

        public void AddSession(AuthorizationAuthority.Domain.Features.Sessions.AuthenticatedSession session) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion?> FindLatestPublishedFormVersionAsync(Guid org, Guid form, CancellationToken ct) => Task.FromResult<AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion?>(null);

        public void AddPublishedFormVersion(AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion version) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion>> GetLatestPublishedFormVersionsAsync(Guid org, CancellationToken ct) => Task.FromResult(new List<AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion>());

        public Task<List<AuthorizationAuthority.Domain.Features.FieldSearch.FieldSearchApproval>> GetFieldSearchApprovalsAsync(Guid org, Guid? form, CancellationToken ct) => Task.FromResult(new List<AuthorizationAuthority.Domain.Features.FieldSearch.FieldSearchApproval>());

        public void AddFieldSearchApproval(AuthorizationAuthority.Domain.Features.FieldSearch.FieldSearchApproval approval) => throw new NotSupportedException();
    }
}
