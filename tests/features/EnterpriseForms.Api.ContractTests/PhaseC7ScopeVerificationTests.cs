using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Ports;
using EnterpriseForms.Application.Features.Search.FieldSearch;
using EnterpriseForms.Application.Features.Search.Verification;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C7 M1 business half of AC-5 (TrustedScopeVerifier): the expected population is the Authority catalogue only;
/// contentful records are verified in batches of at most 50 against their catalogue heads; anything short of complete
/// verification at one snapshot is a typed failure (D3), never a smaller result. Authority stubbed here; the real path
/// runs in the Stage B integration suite (SearchCatalogueTests).
/// </summary>
public sealed class PhaseC7ScopeVerificationTests
{
    private static readonly RequestContext Context = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());
    private static readonly Guid Form = Guid.CreateVersion7();

    public class Stub<T> : DispatchProxy
        where T : class
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (m, _) => throw new NotSupportedException(m.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private sealed class FakeAuthority
    {
        public List<CatalogueEntry> Entries { get; } = [];
        public long Revision { get; set; } = 9;
        public long BatchRevision { get; set; } = 9;
        public Func<BatchVerificationItem, BatchVerificationResult>? Result { get; set; }
        public CatalogueSnapshotValidation? Validation { get; set; }
        public Exception? CatalogueFailure { get; set; }
        public TimeSpan BatchDelay { get; set; }
        public List<int> BatchSizes { get; } = [];
        public int CatalogueRequestedMaximum { get; private set; }
        public Guid Snapshot { get; } = Guid.CreateVersion7();

        public IAuthorizationAuthorityClient Create()
        {
            var proxy = DispatchProxy.Create<IAuthorizationAuthorityClient, Stub<IAuthorizationAuthorityClient>>();
            ((Stub<IAuthorizationAuthorityClient>)(object)proxy).Handler = (method, args) => method.Name switch
            {
                nameof(IAuthorizationAuthorityClient.IssueSearchCatalogueAsync) => Catalogue((SearchCatalogueRequest)args[1]!),
                nameof(IAuthorizationAuthorityClient.VerifyRecordProjectionsAsync) => Batch((VerifyRecordProjectionsRequest)args[1]!, (CancellationToken)args[2]!),
                nameof(IAuthorizationAuthorityClient.ValidateSearchCatalogueAsync) => Task.FromResult(Validation ?? new CatalogueSnapshotValidation(Snapshot, true, null, Revision, 3, Guid.Empty)),
                _ => throw new NotSupportedException(method.Name),
            };
            return proxy;
        }

        private Task<SearchCatalogue> Catalogue(SearchCatalogueRequest request)
        {
            CatalogueRequestedMaximum = request.MaximumEntries;
            return CatalogueFailure is not null ? Task.FromException<SearchCatalogue>(CatalogueFailure) : Task.FromResult(new SearchCatalogue(Snapshot, Context.OrganizationId, Form, Revision, Revision, 3, Entries, DateTimeOffset.UtcNow.AddMinutes(5), Guid.Empty));
        }

        private async Task<VerifyRecordProjectionsResponse> Batch(VerifyRecordProjectionsRequest request, CancellationToken cancellationToken)
        {
            BatchSizes.Add(request.Records.Count);
            if (BatchDelay > TimeSpan.Zero)
            {
                await Task.Delay(BatchDelay, cancellationToken);
            }

            var results = request.Records.Select(r => Result?.Invoke(r) ?? new BatchVerificationResult(r.FormRecordId, true, null, 1, r.ExpectedCommittedOperationId, true, [new VerifiedProjectionItem(ManifestItemKind.RecordHeader, r.FormRecordId)])).ToList();
            return new VerifyRecordProjectionsResponse(results, BatchRevision, Guid.Empty);
        }
    }

    private sealed class FakeRecords(int bytesPerRecord = 100) : IAcceptedRecordItemsReader
    {
        public List<(Guid Record, Guid Operation)> Reads { get; } = [];

        public Task<IReadOnlyList<StagedContentItem>> ReadObservedItemsAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken)
        {
            Reads.Add((formRecordId, committedOperationId));
            return Task.FromResult<IReadOnlyList<StagedContentItem>>([new StagedContentItem(ManifestItemKind.RecordHeader, formRecordId, Convert.ToBase64String(new byte[bytesPerRecord]), Convert.ToBase64String(new byte[32]))]);
        }
    }

    private static CatalogueEntry Entry(long revision = 1, bool quarantined = false) => new(Guid.CreateVersion7(), revision, revision == 0 ? null : Guid.CreateVersion7(), quarantined, false);

    private static async Task<VerifiedScope> VerifyAsync(FakeAuthority authority, FakeRecords? records = null, FieldSearchLimits? limits = null) =>
        await new TrustedScopeVerifier(authority.Create(), records ?? new FakeRecords(), limits ?? new FieldSearchLimits(), TimeProvider.System).VerifyAsync(Context, Form, CancellationToken.None);

    private static async Task<SearchException> FailAsync(FakeAuthority authority, FakeRecords? records = null, FieldSearchLimits? limits = null) =>
        await Assert.ThrowsAsync<SearchException>(() => VerifyAsync(authority, records, limits));

    [Fact]
    public async Task EveryContentfulCatalogueRecordIsVerifiedInBatchesOfAtMostFiftyAgainstItsCatalogueHead()
    {
        var authority = new FakeAuthority();
        authority.Entries.AddRange(Enumerable.Range(0, 120).Select(_ => Entry()));
        authority.Entries.AddRange(Enumerable.Range(0, 3).Select(_ => Entry(revision: 0)));
        var records = new FakeRecords();
        var scope = await VerifyAsync(authority, records);

        Assert.Equal([50, 50, 20], authority.BatchSizes);
        Assert.Equal(123, scope.CatalogueEntryCount);
        Assert.Equal(3, scope.RecordsWithoutContent);
        Assert.Equal(120, scope.Records.Count);
        // Business rows are read only for catalogue records and only for the operation the catalogue fixed.
        Assert.Equal(authority.Entries.Where(e => e.CommittedContentRevision > 0).Select(e => (e.FormRecordId, e.CommittedOperationId!.Value)).Order(), records.Reads.Order());
        Assert.Equal(1 + 3 + 1, scope.Metrics.AuthorityCalls);
        Assert.Equal(3, scope.Metrics.VerificationBatches);
        Assert.Equal(500, authority.CatalogueRequestedMaximum); // the D4 limit, never more
    }

    [Theory]
    [InlineData("ITEM_DIGEST_MISMATCH", SearchErrorCodes.VerificationUnavailable, "RECORD_VERIFICATION_FAILED")]
    [InlineData("MISSING_ITEM", SearchErrorCodes.VerificationUnavailable, "RECORD_VERIFICATION_FAILED")]
    [InlineData("ROOT_MISMATCH", SearchErrorCodes.VerificationUnavailable, "RECORD_VERIFICATION_FAILED")]
    [InlineData("QUARANTINED", SearchErrorCodes.VerificationUnavailable, "RECORD_QUARANTINED")]
    [InlineData("AWAITING_ARCHIVE", SearchErrorCodes.VerificationUnavailable, "AWAITING_ARCHIVE")]
    [InlineData("HEAD_CHANGED", SearchErrorCodes.SnapshotChanged, null)]
    [InlineData("RECORD_UNAVAILABLE", SearchErrorCodes.SnapshotChanged, null)]
    public async Task OneUnverifiedRecordFailsTheWholeScope(string failure, string expectedCode, string? expectedReason)
    {
        var authority = new FakeAuthority();
        authority.Entries.AddRange(Enumerable.Range(0, 60).Select(_ => Entry()));
        var bad = authority.Entries[55].FormRecordId;
        authority.Result = r => r.FormRecordId == bad ? new BatchVerificationResult(r.FormRecordId, false, failure, 1, null, false, []) : new BatchVerificationResult(r.FormRecordId, true, null, 1, r.ExpectedCommittedOperationId, true, []);
        var refused = await FailAsync(authority);
        Assert.Equal(expectedCode, refused.ErrorCode);
        Assert.Equal(expectedReason, refused.UnavailableReason);
    }

    [Fact]
    public async Task AQuarantinedCatalogueEntryFailsBeforeAnyRowIsRead()
    {
        var authority = new FakeAuthority();
        authority.Entries.Add(Entry());
        authority.Entries.Add(Entry(quarantined: true));
        var records = new FakeRecords();
        var refused = await FailAsync(authority, records);
        Assert.Equal("RECORD_QUARANTINED", refused.UnavailableReason);
        Assert.Empty(records.Reads);
    }

    [Fact]
    public async Task AChangedSnapshotOrAuthorizationRevisionFailsAsSnapshotChanged()
    {
        var invalid = new FakeAuthority();
        invalid.Entries.Add(Entry());
        invalid.Validation = new CatalogueSnapshotValidation(invalid.Snapshot, false, "AUTHORIZATION_REVISION_CHANGED", 10, 3, Guid.Empty);
        Assert.Equal(SearchErrorCodes.SnapshotChanged, (await FailAsync(invalid)).ErrorCode);

        var moved = new FakeAuthority { BatchRevision = 10 };
        moved.Entries.Add(Entry());
        Assert.Equal(SearchErrorCodes.SnapshotChanged, (await FailAsync(moved)).ErrorCode);

        var gate = new FakeAuthority();
        gate.Entries.Add(Entry());
        gate.Validation = new CatalogueSnapshotValidation(gate.Snapshot, false, "ORGANIZATION_GATE_CLOSED", 9, 3, Guid.Empty);
        var closed = await FailAsync(gate);
        Assert.Equal(SearchErrorCodes.VerificationUnavailable, closed.ErrorCode);
        Assert.True(closed.Retryable);
    }

    [Fact]
    public async Task TheRecordByteAndTimeLimitsFailWithoutAPartialScope()
    {
        var tooMany = new FakeAuthority { CatalogueFailure = new AuthorityClientException("LIMIT_EXCEEDED", 422, false) };
        var records = await FailAsync(tooMany);
        Assert.Equal((SearchErrorCodes.VerificationLimit, "records"), (records.ErrorCode, records.LimitKind));

        var big = new FakeAuthority();
        big.Entries.AddRange(Enumerable.Range(0, 10).Select(_ => Entry()));
        var bytes = await FailAsync(big, new FakeRecords(bytesPerRecord: 1000), new FieldSearchLimits { MaximumVerifiedBytes = 5000 });
        Assert.Equal((SearchErrorCodes.VerificationLimit, "bytes"), (bytes.ErrorCode, bytes.LimitKind));
        Assert.Empty(big.BatchSizes); // refused before anything was sent for verification

        var slow = new FakeAuthority { BatchDelay = TimeSpan.FromSeconds(5) };
        slow.Entries.Add(Entry());
        var time = await FailAsync(slow, limits: new FieldSearchLimits { MaximumExecutionSeconds = 1 });
        Assert.Equal((SearchErrorCodes.VerificationLimit, "time"), (time.ErrorCode, time.LimitKind));
    }

    [Theory]
    [InlineData("AUTHORITY_NOT_READY", 503, true, SearchErrorCodes.SnapshotChanged)]
    [InlineData("SIGNING_UNAVAILABLE", 503, true, SearchErrorCodes.VerificationUnavailable)]
    [InlineData("AUTHORITY_UNAVAILABLE", 503, true, SearchErrorCodes.VerificationUnavailable)]
    [InlineData("INTEGRITY_QUARANTINE", 423, false, SearchErrorCodes.VerificationUnavailable)]
    public async Task AuthorityFailuresAreTypedSearchFailures(string code, int status, bool retryable, string expected)
    {
        var authority = new FakeAuthority { CatalogueFailure = new AuthorityClientException(code, status, retryable) };
        Assert.Equal(expected, (await FailAsync(authority)).ErrorCode);
    }

    [Fact]
    public async Task AnEmptyScopeIsAValidCompleteScopeWithoutVerificationCalls()
    {
        var authority = new FakeAuthority();
        var scope = await VerifyAsync(authority);
        Assert.Empty(scope.Records);
        Assert.Empty(authority.BatchSizes);
        Assert.Equal(2, scope.Metrics.AuthorityCalls); // catalogue + final snapshot validation
    }
}
