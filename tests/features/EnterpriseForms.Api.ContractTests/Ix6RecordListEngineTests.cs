using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormRecords.ListRecords;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// IX-6 (IX-D12, R1 §E.6): engine selection of My Records. The indexed engine answers only supported listings and only when the
/// Authority can list from verified entries; otherwise the C-29 listing answers unchanged. A listing's cursor keeps its engine: a page of
/// one engine's listing is never served by the other (409 LIST_CHANGED). Authority failures other than "the index cannot list now"
/// propagate (never a silent fallback that would hide a quarantine).
/// </summary>
public sealed class Ix6RecordListEngineTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Me = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Me, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());

    private static RecordListQuery Query(string? search = null, string? status = null, string? sort = null, int pageSize = 2) => RecordListQuery.Create(null, status, false, sort, null, pageSize, false, search);

    private static (ListRecordsHandler Handler, FakeIndexed Indexed, FakeAuthority Authority) Build(string engine, FakeIndexed? indexed = null, List<Guid>? authorized = null)
    {
        var fakeIndexed = indexed ?? new FakeIndexed();
        var authority = new FakeAuthority { Authorized = authorized ?? [] };
        var store = new FakeStore((authorized ?? []).Select(id => new FormRecord
        {
            FormRecordId = id, TenantId = Org, FormDefinitionId = Form, RecordStatus = "Draft", CreatedByUserId = Me, CreatedOn = DateTimeOffset.UtcNow.AddDays(-1), ModifiedOn = DateTimeOffset.UtcNow,
        }).ToList());
        var protector = new TestProtector();
        var options = new RecordListOptions { Engine = engine };
        var engineInstance = new RecordListIndexedEngine(options, fakeIndexed, store.Proxy, protector);
        return (new ListRecordsHandler(store.Proxy, authority.Proxy, protector, engineInstance), fakeIndexed, authority);
    }

    [Fact]
    public async Task TheIndexedEngineAnswersASupportedListingWithTheAuthorityFactsAndNoDiscoverEnumeration()
    {
        var (handler, indexed, authority) = Build(RecordListOptions.EngineIndexed);
        var result = await handler.HandleAsync(Context, Query(), null, CancellationToken.None);

        Assert.Equal(1, indexed.Calls);
        Assert.Equal(0, authority.DiscoverPages);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(indexed.Items.Take(2).Select(i => i.FormRecordId), result.Items.Select(i => i.FormRecordId));
        Assert.All(result.Items, i => Assert.Equal("Case intake", i.FormDefinitionName));
        Assert.All(result.Items, i => Assert.False(i.ProjectionVerified));
        Assert.Equal(indexed.Items[0].ContentHeadModifiedOn, result.Items[0].ModifiedOn);
        Assert.Equal(RecordListQuery.ReferenceFor(indexed.Items[0].FormRecordId), result.Items[0].RecordReference);
        Assert.Equal(1, result.UnavailableRecordCount); // "registered without accepted content"

        // The next page goes to the indexed engine again, at the first page's authorization revision and the next offset.
        var next = await handler.HandleAsync(Context, Query(), result.ContinuationToken, CancellationToken.None);
        Assert.Equal(2, indexed.Calls);
        Assert.Equal(2, indexed.LastQuery!.Offset);
        Assert.Equal(41, indexed.LastQuery.ExpectedAuthorizationRevision);
        Assert.Single(next.Items);
        Assert.Equal(0, authority.DiscoverPages);
    }

    [Theory]
    [InlineData("REC-1A2B3C4D", null, null)]
    [InlineData(null, null, "formName")]
    [InlineData(null, "Deleted", null)]
    public async Task UnsupportedListingsAreAnsweredByTheUnchangedListing(string? search, string? status, string? sort)
    {
        var (handler, indexed, authority) = Build(RecordListOptions.EngineIndexed, authorized: [Guid.CreateVersion7()]);
        await handler.HandleAsync(Context, Query(search, status, sort), null, CancellationToken.None);
        Assert.Equal(0, indexed.Calls);
        Assert.True(authority.DiscoverPages > 0);
    }

    [Theory]
    [InlineData("RECORD_LIST_INDEX_INCOMPLETE")]
    [InlineData("SEARCH_INDEX_NOT_READY")]
    [InlineData("SEARCH_INDEX_INCONSISTENT")]
    [InlineData("SEARCH_INDEX_VERIFICATION_OVERDUE")]
    [InlineData("SEARCH_VERIFICATION_LIMIT")]
    public async Task WhenTheIndexCannotListTheFirstPageTheUnchangedListingAnswers(string code)
    {
        var id = Guid.CreateVersion7();
        var (handler, indexed, authority) = Build(RecordListOptions.EngineIndexed, new FakeIndexed { Failure = new AuthorityClientException(code, 503, true) }, [id]);
        var result = await handler.HandleAsync(Context, Query(), null, CancellationToken.None);
        Assert.Equal(1, indexed.Calls);
        Assert.True(authority.DiscoverPages > 0);
        Assert.Equal([id], result.Items.Select(i => i.FormRecordId));
    }

    [Fact]
    public async Task ALaterIndexedPageNeverContinuesOnTheOtherEngine()
    {
        var indexed = new FakeIndexed();
        var (handler, _, authority) = Build(RecordListOptions.EngineIndexed, indexed);
        var first = await handler.HandleAsync(Context, Query(), null, CancellationToken.None);

        indexed.Failure = new AuthorityClientException("RECORD_LIST_INDEX_INCOMPLETE", 503, true);
        var failure = await Assert.ThrowsAsync<BusinessException>(() => handler.HandleAsync(Context, Query(), first.ContinuationToken, CancellationToken.None));
        Assert.Equal("LIST_CHANGED", failure.ErrorCode);
        Assert.Equal(409, failure.HttpStatusCode);
        Assert.Equal(0, authority.DiscoverPages);

        indexed.Failure = new AuthorityClientException("SEARCH_SNAPSHOT_CHANGED", 409, true);
        failure = await Assert.ThrowsAsync<BusinessException>(() => handler.HandleAsync(Context, Query(), first.ContinuationToken, CancellationToken.None));
        Assert.Equal("LIST_CHANGED", failure.ErrorCode);
    }

    [Fact]
    public async Task AnIndexedCursorIsRefusedWhenTheIndexedEngineIsNoLongerSelectedAndAnUnchangedListingCursorStaysOnItsEngine()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToList();
        var (indexedHandler, _, _) = Build(RecordListOptions.EngineIndexed, authorized: ids);
        var indexedFirst = await indexedHandler.HandleAsync(Context, Query(), null, CancellationToken.None);

        var (scanHandler, scanIndexed, _) = Build(RecordListOptions.EngineVerifiedScan, authorized: ids);
        var failure = await Assert.ThrowsAsync<BusinessException>(() => scanHandler.HandleAsync(Context, Query(), indexedFirst.ContinuationToken, CancellationToken.None));
        Assert.Equal("LIST_CHANGED", failure.ErrorCode);
        Assert.Equal(0, scanIndexed.Calls);

        var scanFirst = await scanHandler.HandleAsync(Context, Query(), null, CancellationToken.None);
        Assert.Equal(0, scanIndexed.Calls);
        var (laterIndexedHandler, laterIndexed, laterAuthority) = Build(RecordListOptions.EngineIndexed, authorized: ids);
        var continued = await laterIndexedHandler.HandleAsync(Context, Query(), scanFirst.ContinuationToken, CancellationToken.None);
        Assert.Equal(0, laterIndexed.Calls);
        Assert.True(laterAuthority.DiscoverPages > 0);
        Assert.Single(continued.Items);
    }

    [Theory]
    [InlineData("INTEGRITY_QUARANTINE", 423)]
    [InlineData("AUTHORITY_UNAVAILABLE", 503)]
    [InlineData("RECORD_QUARANTINED", 503)]
    public async Task OtherAuthorityFailuresPropagateAndAreNeverHiddenByAFallback(string code, int status)
    {
        var (handler, _, authority) = Build(RecordListOptions.EngineIndexed, new FakeIndexed { Failure = new AuthorityClientException(code, status, false) }, [Guid.CreateVersion7()]);
        var failure = await Assert.ThrowsAsync<AuthorityClientException>(() => handler.HandleAsync(Context, Query(), null, CancellationToken.None));
        Assert.Equal(code, failure.ErrorCode);
        Assert.Equal(0, authority.DiscoverPages);
    }

    [Fact]
    public async Task TheTestingOverrideSelectsAnEngineForOneRequest()
    {
        var (handler, indexed, _) = Build(RecordListOptions.EngineVerifiedScan, authorized: [Guid.CreateVersion7()]);
        await handler.HandleAsync(Context, Query(), null, CancellationToken.None, RecordListOptions.EngineIndexed);
        Assert.Equal(1, indexed.Calls);
        await handler.HandleAsync(Context, Query(), null, CancellationToken.None);
        Assert.Equal(1, indexed.Calls);

        var (indexedHandler, indexedByDefault, _) = Build(RecordListOptions.EngineIndexed, authorized: [Guid.CreateVersion7()]);
        await indexedHandler.HandleAsync(Context, Query(), null, CancellationToken.None, RecordListOptions.EngineVerifiedScan);
        Assert.Equal(0, indexedByDefault.Calls);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void TheIndexedEngineIsRefusedOutsideDevelopmentAndTesting(string environment)
    {
        Assert.Throws<InvalidOperationException>(() => new RecordListOptions { Engine = RecordListOptions.EngineIndexed }.Validate(environment));
        new RecordListOptions().Validate(environment);
        new RecordListOptions { Engine = RecordListOptions.EngineIndexed }.Validate("Testing");
        Assert.Throws<InvalidOperationException>(() => new RecordListOptions { Engine = "Shadow" }.Validate("Testing"));
    }

    [Fact]
    public void AnOldCursorWithoutAnEngineDecodesAsTheUnchangedListing()
    {
        var protector = new TestProtector();
        var query = Query();
        var token = protector.Protect(System.Text.Json.JsonSerializer.Serialize(new { V = 1, O = Org, U = Me, R = 7L, Q = query.ScopeDigest(), S = 2 }), TimeSpan.FromMinutes(1));
        var cursor = RecordListCursor.Decode(protector, token, Context, query, 7);
        Assert.Null(cursor.Engine);
        Assert.Equal(2, cursor.Offset);
    }

    // ---- stand-ins ----

    private sealed class TestProtector : IOpaqueCursorProtector
    {
        public string Protect(string payload, TimeSpan lifetime) => "ok." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

        public string? Unprotect(string token) => token.StartsWith("ok.", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token[3..])) : null;
    }

    private sealed class FakeIndexed : IIndexedRecordListAuthority
    {
        public List<AuthorityIndexedRecordListItem> Items { get; } = Enumerable.Range(0, 3)
            .Select(i => new AuthorityIndexedRecordListItem(Guid.CreateVersion7(), Form, "Draft", null, Me, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddMinutes(-i), i == 0)).ToList();

        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public AuthorityIndexedRecordListQuery? LastQuery { get; private set; }

        public Task<AuthorityIndexedRecordListResult> ListRecordsAsync(DelegatedUserContext user, AuthorityIndexedRecordListQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            LastQuery = query;
            if (Failure is not null)
            {
                return Task.FromException<AuthorityIndexedRecordListResult>(Failure);
            }

            var page = Items.Skip(query.Offset).Take(query.PageSize).ToList();
            return Task.FromResult(new AuthorityIndexedRecordListResult(41, Items.Count, query.Offset, query.PageSize, page, [Form], 1, [Guid.CreateVersion7()], "AuthorityVerifiedEntries", Guid.CreateVersion7()));
        }
    }

    public class Stub<T> : DispatchProxy
        where T : class
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private static T Create<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub<T>>();
        ((Stub<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private sealed class FakeAuthority
    {
        public FakeAuthority() => Proxy = Create<IAuthorizationAuthorityClient>(Handle);

        public IAuthorizationAuthorityClient Proxy { get; }

        public List<Guid> Authorized { get; init; } = [];

        public int DiscoverPages { get; private set; }

        private object? Handle(MethodInfo method, object?[] args)
        {
            switch (method.Name)
            {
                case nameof(IAuthorizationAuthorityClient.CheckPermissionsBatchAsync):
                    var requests = (IReadOnlyList<PermissionCheckRequest>)args[1]!;
                    return Task.FromResult<IReadOnlyList<PermissionDecision>>(requests.Select(_ => new PermissionDecision(true, null, 9, 9, null, Guid.Empty)).ToList());
                case nameof(IAuthorizationAuthorityClient.DiscoverAuthorizedRecordsAsync):
                    DiscoverPages++;
                    return Task.FromResult(new RecordDiscoveryPage(Authorized, 9, null));
                default:
                    throw new NotSupportedException(method.Name);
            }
        }
    }

    private sealed class FakeStore
    {
        private readonly List<FormRecord> records;

        public FakeStore(List<FormRecord> records)
        {
            this.records = records;
            Proxy = Create<IBusinessStore>((method, _) => method.Name == nameof(IBusinessStore.BeginScopeAsync) ? Task.FromResult(NewScope()) : throw new NotSupportedException(method.Name));
        }

        public IBusinessStore Proxy { get; }

        private IBusinessScope NewScope() => Create<IBusinessScope>((method, args) => method.Name switch
        {
            nameof(IBusinessScope.GetFormRecordsAsync) => Task.FromResult(records.Where(r => ((IReadOnlyCollection<Guid>)args[1]!).Contains(r.FormRecordId)).ToList()),
            nameof(IBusinessScope.GetFormDefinitionsAsync) => Task.FromResult(new List<FormDefinition> { new() { FormDefinitionId = Form, TenantId = Org, FormDefinitionName = "Case intake" } }),
            nameof(IBusinessScope.GetUsersAsync) => Task.FromResult(new List<User> { new() { UserId = Me, TenantId = Org, DisplayName = "Tester" } }),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });
    }
}
