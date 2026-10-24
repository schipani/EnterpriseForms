using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.ListRecords;
using EnterpriseForms.Domain.Features.FormRecords;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C4: permission-check batches above the authority's 250-per-call limit (C-27), complete authorized discovery at
/// one authorization revision (C-29), deterministic filtering/sorting and bound, expiring list cursors.
/// </summary>
public sealed class PhaseC4RecordWorkspaceTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DelegatedUserContext User = new("https://issuer.invalid", "subject", Org, "assertion");

    private static PermissionCheckRequest Check(int index) =>
        new(Org, new ResourceReference(ProtectedResourceKind.FormDefinition, new Guid(index, 0, 0, new byte[8])), index % 2 == 0 ? ProtectedAction.DesignForm : ProtectedAction.CreateRecord);

    private static int IndexOf(PermissionCheckRequest request) => BitConverter.ToInt32(request.Resource.ResourceId.ToByteArray(), 0);

    /// <summary>Authority stand-in: allows every third check; reports the given revision for each call in turn.</summary>
    private sealed class ChunkAuthority(params long[] revisionsPerCall)
    {
        public List<int> CallSizes { get; } = [];

        public Task<IReadOnlyList<PermissionDecision>> CheckAsync(IReadOnlyList<PermissionCheckRequest> chunk, CancellationToken cancellationToken)
        {
            Assert.InRange(chunk.Count, 1, PermissionCheckBatching.MaximumChecksPerCall);
            var revision = revisionsPerCall[Math.Min(CallSizes.Count, revisionsPerCall.Length - 1)];
            CallSizes.Add(chunk.Count);
            return Task.FromResult<IReadOnlyList<PermissionDecision>>(chunk.Select(c => new PermissionDecision(IndexOf(c) % 3 == 0, null, revision, revision, null, Guid.Empty)).ToList());
        }
    }

    [Fact]
    public async Task BatchesAboveTheAuthorityLimitAreSplitAndReassembledInRequestOrder()
    {
        var requests = Enumerable.Range(0, 601).Select(Check).ToList();
        var authority = new ChunkAuthority(7);
        var decisions = await PermissionCheckBatching.CheckAllAsync(requests, authority.CheckAsync, CancellationToken.None);

        Assert.Equal([250, 250, 101], authority.CallSizes);
        Assert.Equal(601, decisions.Count);
        for (var i = 0; i < requests.Count; i++)
        {
            Assert.Equal(i % 3 == 0, decisions[i].Allowed);
        }
    }

    [Fact]
    public async Task SmallBatchesUseOneCallAndEmptyBatchesNone()
    {
        var authority = new ChunkAuthority(1);
        Assert.Empty(await PermissionCheckBatching.CheckAllAsync([], authority.CheckAsync, CancellationToken.None));
        Assert.Empty(authority.CallSizes);
        Assert.Equal(250, (await PermissionCheckBatching.CheckAllAsync(Enumerable.Range(0, 250).Select(Check).ToList(), authority.CheckAsync, CancellationToken.None)).Count);
        Assert.Equal([250], authority.CallSizes);
    }

    [Fact]
    public async Task ChunksEvaluatedAtDifferentRevisionsAreReEvaluatedNotCombined()
    {
        // Attempt 1: revision 7, then 8 (a grant landed between chunks) → discarded; attempt 2: 8, 8 → returned.
        var authority = new ChunkAuthority(7, 8, 8, 8);
        var decisions = await PermissionCheckBatching.CheckAllAsync(Enumerable.Range(0, 300).Select(Check).ToList(), authority.CheckAsync, CancellationToken.None);
        Assert.Equal([250, 50, 250, 50], authority.CallSizes);
        Assert.All(decisions, d => Assert.Equal(8, d.AuthorizationRevision));
    }

    [Fact]
    public async Task PersistentRevisionChurnFailsClosedWithARetryableError()
    {
        var authority = new ChunkAuthority(1, 2, 3, 4, 5, 6, 7, 8);
        var failure = await Assert.ThrowsAsync<AuthorityClientException>(() => PermissionCheckBatching.CheckAllAsync(Enumerable.Range(0, 260).Select(Check).ToList(), authority.CheckAsync, CancellationToken.None));
        Assert.Equal("AUTHORIZATION_STATE_CHANGED", failure.ErrorCode);
        Assert.True(failure.Retryable);
        Assert.Equal(PermissionCheckBatching.MaximumAttempts * 2, authority.CallSizes.Count);
    }

    [Fact]
    public async Task AFailingChunkFailsTheWholeBatchAndNothingIsAllowed()
    {
        var calls = 0;
        Task<IReadOnlyList<PermissionDecision>> Failing(IReadOnlyList<PermissionCheckRequest> chunk, CancellationToken token)
        {
            if (++calls == 2)
            {
                throw new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true);
            }

            return Task.FromResult<IReadOnlyList<PermissionDecision>>(chunk.Select(_ => new PermissionDecision(true, null, 1, 1, null, Guid.Empty)).ToList());
        }

        var failure = await Assert.ThrowsAsync<AuthorityClientException>(() => PermissionCheckBatching.CheckAllAsync(Enumerable.Range(0, 400).Select(Check).ToList(), Failing, CancellationToken.None));
        Assert.Equal("AUTHORITY_UNAVAILABLE", failure.ErrorCode);
    }

    [Fact]
    public async Task AChunkWithTheWrongNumberOfDecisionsIsRejected()
    {
        static Task<IReadOnlyList<PermissionDecision>> Short(IReadOnlyList<PermissionCheckRequest> chunk, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<PermissionDecision>>(chunk.Skip(1).Select(_ => new PermissionDecision(true, null, 1, 1, null, Guid.Empty)).ToList());
        await Assert.ThrowsAsync<AuthorityClientException>(() => PermissionCheckBatching.CheckAllAsync(Enumerable.Range(0, 251).Select(Check).ToList(), Short, CancellationToken.None));
        await Assert.ThrowsAsync<AuthorityClientException>(() => PermissionCheckBatching.CheckAllAsync([Check(0), Check(1)], Short, CancellationToken.None));
    }

    // ---- complete discovery ----

    /// <summary>Discover stand-in: pages a list of revision snapshots; each call reads the snapshot for its call number.</summary>
    private sealed class DiscoverAuthority(Func<int, (long Revision, List<Guid> Ids)> snapshotForCall, Func<int, string?, Exception?>? fault = null) : IAuthorizationAuthorityClient
    {
        public int Calls { get; private set; }

        public Task<RecordDiscoveryPage> DiscoverAuthorizedRecordsAsync(DelegatedUserContext user, Guid organizationId, int pageSize, string? continuationToken, CancellationToken cancellationToken)
        {
            var call = Calls++;
            if (fault?.Invoke(call, continuationToken) is { } error)
            {
                throw error;
            }

            var (revision, ids) = snapshotForCall(call);
            var skip = continuationToken is null ? 0 : int.Parse(continuationToken.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
            var page = ids.Skip(skip).Take(pageSize).ToList();
            var next = skip + page.Count < ids.Count ? $"{revision}:{skip + page.Count}" : null;
            return Task.FromResult(new RecordDiscoveryPage(page, revision, next));
        }

        public Task<PermissionDecision> CheckPermissionAsync(DelegatedUserContext user, PermissionCheckRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FormAccessState> GetFormAccessAsync(DelegatedUserContext user, Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PermissionDecision>> CheckPermissionsBatchAsync(DelegatedUserContext user, IReadOnlyList<PermissionCheckRequest> requests, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SecurityCommandResult> SubmitSecurityCommandAsync(DelegatedUserContext user, SecurityCommandRequest request, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SecurityCommandResult> GetSecurityCommandStatusAsync(DelegatedUserContext user, Guid commandId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PreparedContentOperation> PrepareContentChangeAsync(DelegatedUserContext user, PrepareContentChangeRequest request, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AcceptedContentRevision> CommitContentChangeAsync(DelegatedUserContext user, Guid operationId, CommitContentChangeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AcceptedContentRevision> GetCommittedContentRevisionAsync(DelegatedUserContext user, Guid organizationId, Guid formRecordId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VerifyRecordProjectionResult> VerifyRecordProjectionAsync(DelegatedUserContext user, VerifyRecordProjectionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SearchCatalogue> IssueSearchCatalogueAsync(DelegatedUserContext user, SearchCatalogueRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CatalogueSnapshotValidation> ValidateSearchCatalogueAsync(DelegatedUserContext user, Guid searchCatalogueSnapshotId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VerifyRecordProjectionsResponse> VerifyRecordProjectionsAsync(DelegatedUserContext user, VerifyRecordProjectionsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ContentHistoryPage> GetContentHistoryAsync(DelegatedUserContext user, Guid organizationId, Guid formRecordId, long? afterRevision, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AcceptedContentRevision> GetCommittedContentRevisionForReprotectionAsync(Guid organizationId, Guid formRecordId, Guid reprotectionCommandId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PreparedContentOperation> PrepareReprotectionAsync(PrepareContentChangeRequest request, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AcceptedContentRevision> CommitReprotectionAsync(Guid operationId, CommitContentChangeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static List<Guid> Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).Order().ToList();

    [Fact]
    public async Task DiscoveryReadsEveryAuthorityPageBeyondTheOldLimits()
    {
        var ids = Ids(1_234);
        var authority = new DiscoverAuthority(_ => (12, ids));
        var set = await AuthorizedRecordEnumerator.EnumerateAsync(authority, User, Org, CancellationToken.None);
        Assert.Equal(ids, set.FormRecordIds);
        Assert.Equal(12, set.AuthorizationRevision);
        Assert.Equal(5, authority.Calls); // 250 × 4 + 234
    }

    [Fact]
    public async Task DiscoveryRestartsWhenAccessChangesBetweenPages()
    {
        var before = Ids(400);
        var after = before.Take(399).ToList(); // a share was revoked while the list was being read
        var authority = new DiscoverAuthority(call => call == 0 ? (5, before) : (6, after));
        var set = await AuthorizedRecordEnumerator.EnumerateAsync(authority, User, Org, CancellationToken.None);
        Assert.Equal(after, set.FormRecordIds);
        Assert.Equal(6, set.AuthorizationRevision);
    }

    [Fact]
    public async Task AnInvalidatedContinuationRestartsFromTheFirstPage()
    {
        var ids = Ids(300);
        var authority = new DiscoverAuthority(_ => (9, ids), (call, token) => call == 1 ? new AuthorityClientException("CONTINUATION_INVALIDATED", 409, true) : null);
        var set = await AuthorizedRecordEnumerator.EnumerateAsync(authority, User, Org, CancellationToken.None);
        Assert.Equal(ids, set.FormRecordIds);
        Assert.Equal(4, authority.Calls);
    }

    [Fact]
    public async Task PersistentChurnDuringDiscoveryFailsClosed()
    {
        var authority = new DiscoverAuthority(call => (call, Ids(300)));
        var failure = await Assert.ThrowsAsync<AuthorityClientException>(() => AuthorizedRecordEnumerator.EnumerateAsync(authority, User, Org, CancellationToken.None));
        Assert.Equal("AUTHORIZATION_STATE_CHANGED", failure.ErrorCode);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public async Task DiscoveryAboveTheListCeilingIsRefusedNotTruncated()
    {
        var authority = new DiscoverAuthority(_ => (3, Ids(600)));
        var failure = await Assert.ThrowsAsync<BusinessException>(() => AuthorizedRecordEnumerator.EnumerateAsync(authority, User, Org, CancellationToken.None, maximumRecords: 500));
        Assert.Equal("RECORD_LIST_TOO_LARGE", failure.ErrorCode);
    }

    [Fact]
    public async Task AuthorityFailuresDuringDiscoveryPropagate()
    {
        var authority = new DiscoverAuthority(_ => (3, Ids(600)), (call, _) => call == 2 ? new AuthorityClientException("AUTHORITY_NOT_READY", 503, true) : null);
        var failure = await Assert.ThrowsAsync<AuthorityClientException>(() => AuthorizedRecordEnumerator.EnumerateAsync(authority, User, Org, CancellationToken.None));
        Assert.Equal("AUTHORITY_NOT_READY", failure.ErrorCode);
    }

    // ---- query, sort and cursor ----

    private static FormRecord Record(Guid form, string status, DateTimeOffset modified, Guid? parent = null, Guid? id = null) => new()
    {
        FormRecordId = id ?? Guid.CreateVersion7(), TenantId = Org, FormDefinitionId = form, RecordStatus = status, CreatedOn = modified.AddDays(-1), ModifiedOn = modified, ParentFormRecordId = parent, CreatedByUserId = UserId,
    };

    [Fact]
    public void QueryValidatesSortDirectionAndStatus()
    {
        Assert.Equal("INVALID_REQUEST", Assert.Throws<BusinessException>(() => RecordListQuery.Create(null, null, false, "given_name", null, 25)).ErrorCode);
        Assert.Equal("INVALID_REQUEST", Assert.Throws<BusinessException>(() => RecordListQuery.Create(null, null, false, "createdOn", "sideways", 25)).ErrorCode);
        Assert.Equal("INVALID_REQUEST", Assert.Throws<BusinessException>(() => RecordListQuery.Create(null, "Secret", false, null, null, 25)).ErrorCode);
        var defaults = RecordListQuery.Create(null, null, false, null, null, 5_000);
        Assert.Equal(("modifiedOn", "desc", RecordListQuery.MaximumPageSize), (defaults.SortBy, defaults.SortDirection, defaults.PageSize));
        Assert.Equal("asc", RecordListQuery.Create(null, null, false, "formName", null, 25).SortDirection);
    }

    [Fact]
    public void FiltersAndSortAreDeterministicWithARecordIdTieBreak()
    {
        var formA = Guid.CreateVersion7();
        var formB = Guid.CreateVersion7();
        var names = new Dictionary<Guid, string> { [formA] = "Accused", [formB] = "Witness" };
        var t = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
        var tieLow = new Guid("00000000-0000-7000-8000-000000000001");
        var tieHigh = new Guid("00000000-0000-7000-8000-000000000002");
        var parent = Record(formA, "Draft", t.AddHours(-5));
        var records = new List<FormRecord>
        {
            Record(formA, "Draft", t, id: tieHigh),
            Record(formA, "Draft", t, id: tieLow),
            Record(formB, "Submitted", t.AddHours(-1)),
            parent,
            Record(formB, "Draft", t.AddHours(2), parent: parent.FormRecordId),
        };

        var byModified = RecordListQuery.Create(null, null, false, null, null, 25).Apply(records, names);
        Assert.Equal(4, byModified.Count); // child records excluded by default
        Assert.Equal([tieHigh, tieLow], byModified.Take(2).Select(r => r.FormRecordId));
        Assert.Equal(byModified.Select(r => r.FormRecordId), RecordListQuery.Create(null, null, false, null, null, 25).Apply(records.AsEnumerable().Reverse(), names).Select(r => r.FormRecordId));

        Assert.Equal(5, RecordListQuery.Create(null, null, true, null, null, 25).Apply(records, names).Count);
        Assert.All(RecordListQuery.Create(formB, null, true, null, null, 25).Apply(records, names), r => Assert.Equal(formB, r.FormDefinitionId));
        Assert.Single(RecordListQuery.Create(null, "Submitted", false, null, null, 25).Apply(records, names));
        var byForm = RecordListQuery.Create(null, null, false, "formName", "asc", 25).Apply(records, names);
        Assert.Equal(["Accused", "Accused", "Accused", "Witness"], byForm.Select(r => names[r.FormDefinitionId]));
        Assert.Equal([parent.FormRecordId, tieLow, tieHigh], byForm.Take(3).Select(r => r.FormRecordId)); // ascending tie-break by last change, then id
    }

    [Fact]
    public void RecordReferenceIsAShortStableDisplayCode()
    {
        var id = new Guid("0199a2b3-c4d5-7e6f-8123-456789abcdef");
        Assert.Equal("REC-89ABCDEF", RecordListQuery.ReferenceFor(id));
        Assert.Equal(RecordListQuery.ReferenceFor(id), RecordListQuery.ReferenceFor(id));
    }

    /// <summary>Reversible stand-in for Data Protection that still detects tampering (prefix check) and expiry.</summary>
    private sealed class TestProtector : IOpaqueCursorProtector
    {
        public bool Expired { get; set; }

        public string Protect(string payload, TimeSpan lifetime) => "ok." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

        public string? Unprotect(string token) => !Expired && token.StartsWith("ok.", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token[3..])) : null;
    }

    private static RequestContext Context(Guid user) => new(Org, user, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());

    [Fact]
    public void CursorsAreBoundToCallerScopeAndRevision()
    {
        var protector = new TestProtector();
        var query = RecordListQuery.Create(null, "Draft", false, "createdOn", "asc", 10);
        var token = new RecordListCursor(Org, UserId, 40, query.ScopeDigest(), 20).Encode(protector);

        Assert.Equal(20, RecordListCursor.Decode(protector, token, Context(UserId), query, 40).Offset);
        Assert.Equal("CURSOR_INVALID", Assert.Throws<BusinessException>(() => RecordListCursor.Decode(protector, token, Context(Guid.CreateVersion7()), query, 40)).ErrorCode);
        Assert.Equal("CURSOR_INVALID", Assert.Throws<BusinessException>(() => RecordListCursor.Decode(protector, "forged" + token, Context(UserId), query, 40)).ErrorCode);
        Assert.Equal("CURSOR_SCOPE_MISMATCH", Assert.Throws<BusinessException>(() => RecordListCursor.Decode(protector, token, Context(UserId), RecordListQuery.Create(null, null, false, "createdOn", "asc", 10), 40)).ErrorCode);
        var changed = Assert.Throws<BusinessException>(() => RecordListCursor.Decode(protector, token, Context(UserId), query, 41));
        Assert.Equal(("LIST_CHANGED", 409), (changed.ErrorCode, changed.HttpStatusCode));
        protector.Expired = true;
        Assert.Equal("CURSOR_INVALID", Assert.Throws<BusinessException>(() => RecordListCursor.Decode(protector, token, Context(UserId), query, 40)).ErrorCode);
    }
}
