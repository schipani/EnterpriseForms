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
/// Phase C6 unified search: exact input only; matches decided from the authority's complete discoverable set; the business
/// database only describes matches; unauthorized and missing records are indistinguishable; duplicate references are all
/// returned in the one table with filters, sorting and paging; archived records are found; authority failures propagate
/// as failures, never as "not found".
/// </summary>
public sealed class PhaseC6RecordLookupTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Me = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Me, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());

    /// <summary>A record id whose short reference is REC-&lt;suffix&gt; (the last eight hex digits), with a distinct prefix.</summary>
    private static Guid IdWithSuffix(string suffix, int prefix) => Guid.Parse($"{prefix:x8}-0000-7000-8000-0000{suffix.ToLowerInvariant()}");

    // ---- input ----

    [Theory]
    [InlineData("REC-017EF770", "REC-017EF770")]
    [InlineData("  rec-017ef770  ", "REC-017EF770")]
    public void AShortReferenceIsAcceptedInAnyCaseAndNormalized(string input, string expected)
    {
        var query = RecordLookupQuery.Parse(input);
        Assert.Equal(RecordLookupKind.Reference, query.Kind);
        Assert.Equal(expected, query.Normalized);
    }

    [Theory]
    [InlineData("975a62c5-c1db-8456-99ca-c53c017ef770")]
    [InlineData("975A62C5C1DB845699CAC53C017EF770")]
    [InlineData("{975a62c5-c1db-8456-99ca-c53c017ef770}")]
    public void AFullRecordIdIsAcceptedWithOrWithoutHyphensOrBraces(string input)
    {
        var query = RecordLookupQuery.Parse(input);
        Assert.Equal(RecordLookupKind.RecordId, query.Kind);
        Assert.Equal(Guid.Parse("975a62c5-c1db-8456-99ca-c53c017ef770"), query.FormRecordId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("REC-017EF77")]
    [InlineData("REC-017EF7701")]
    [InlineData("REC-XYZ12345")]
    [InlineData("017EF770")]
    [InlineData("Lifecycle case")]
    [InlineData("case_title = 'x'")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void AnythingElseIsRefusedSoNoFreeTextOrFieldValueSearchIsPossible(string? input)
    {
        var failure = Assert.Throws<BusinessException>(() => RecordLookupQuery.Parse(input));
        Assert.Equal("LOOKUP_INVALID", failure.ErrorCode);
        Assert.Equal(422, failure.HttpStatusCode);
    }

    [Fact]
    public void ReferencesMatchOnlyAmongTheGivenAuthorizedIdsAndEveryDuplicateIsReturned()
    {
        var first = IdWithSuffix("017EF770", 1);
        var second = IdWithSuffix("017EF770", 2);
        var other = IdWithSuffix("AAAAAAAA", 3);
        var query = RecordLookupQuery.Parse("REC-017EF770");
        Assert.Equal([first, second], query.Match([first, other, second, first]));
        Assert.Empty(query.Match([other]));
    }

    // ---- unified list handler (search + filters) ----

    private sealed class TestProtector : IOpaqueCursorProtector
    {
        public string Protect(string payload, TimeSpan lifetime) => "ok." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

        public string? Unprotect(string token) => token.StartsWith("ok.", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token[3..])) : null;
    }

    private static ListRecordsHandler Handler(FakeStore store, FakeAuthority authority) => new(store.Proxy, authority.Proxy, new TestProtector());

    private static RecordListQuery Query(string? search, string? status = null, Guid? form = null, bool archived = false, bool children = false, int pageSize = 25) =>
        RecordListQuery.Create(form, status, children, null, null, pageSize, archived, search);

    [Fact]
    public async Task AnExactIdTheAuthorityDoesNotListGivesTheSameEmptyAnswerAsAMissingRecordAndReadsNothingElse()
    {
        var hidden = Guid.CreateVersion7();
        var visible = Guid.CreateVersion7();
        var authority = new FakeAuthority { Authorized = [visible] };
        var store = new FakeStore([Record(hidden, "Draft"), Record(visible, "Draft")]);
        var handler = Handler(store, authority);

        var refused = await handler.HandleAsync(Context, Query(hidden.ToString("D")), null, CancellationToken.None);
        var missing = await handler.HandleAsync(Context, Query(Guid.CreateVersion7().ToString("D")), null, CancellationToken.None);

        foreach (var result in new[] { refused, missing })
        {
            Assert.Empty(result.Items);
            Assert.Equal(0, result.TotalCount);
            Assert.Equal(0, result.UnavailableRecordCount);
            Assert.Empty(result.AvailableForms ?? []);
        }

        Assert.DoesNotContain(hidden, store.RequestedIds); // an undiscoverable record is never even requested from the business database
    }

    [Fact]
    public async Task AnExactIdFindsAnAuthorizedArchivedRecordThatTheOrdinaryListHides()
    {
        var id = Guid.CreateVersion7();
        var authority = new FakeAuthority { Authorized = [id], EditAllowed = _ => false };
        var handler = Handler(new FakeStore([Record(id, "Archived")]), authority);

        Assert.Empty((await handler.HandleAsync(Context, Query(null), null, CancellationToken.None)).Items); // hidden by default
        var found = await handler.HandleAsync(Context, Query(id.ToString("N")), null, CancellationToken.None);

        var item = Assert.Single(found.Items);
        Assert.Equal(("Archived", RecordListQuery.ReferenceFor(id), false), (item.RecordStatus, item.RecordReference, item.CanEdit));
    }

    [Fact]
    public async Task AnExplicitStatusAndFormFilterNarrowTheSearchWithAndSemantics()
    {
        var id = Guid.CreateVersion7();
        var authority = new FakeAuthority { Authorized = [id] };
        var handler = Handler(new FakeStore([Record(id, "Archived")]), authority);

        Assert.Single((await handler.HandleAsync(Context, Query(RecordListQuery.ReferenceFor(id), status: "Archived"), null, CancellationToken.None)).Items);
        Assert.Empty((await handler.HandleAsync(Context, Query(RecordListQuery.ReferenceFor(id), status: "Draft"), null, CancellationToken.None)).Items);
        Assert.Single((await handler.HandleAsync(Context, Query(RecordListQuery.ReferenceFor(id), form: Form), null, CancellationToken.None)).Items);
        Assert.Empty((await handler.HandleAsync(Context, Query(RecordListQuery.ReferenceFor(id), form: Guid.CreateVersion7()), null, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task AReferenceIsMatchedAgainstTheCompleteAuthorizedSetAndAnUnauthorizedRecordWithTheSameReferenceIsNeverRevealed()
    {
        var mine = IdWithSuffix("1A2B3C4D", 10);
        var notMine = IdWithSuffix("1A2B3C4D", 11); // exists in the business database, not discoverable by the caller
        var authority = new FakeAuthority { Authorized = [mine, .. Enumerable.Range(0, 600).Select(i => IdWithSuffix($"{i:X8}", 100 + i))] };
        var store = new FakeStore([Record(mine, "Draft"), Record(notMine, "Draft")]);
        var result = await Handler(store, authority).HandleAsync(Context, Query("REC-1A2B3C4D"), null, CancellationToken.None);

        Assert.Equal(mine, Assert.Single(result.Items).FormRecordId);
        Assert.Equal(1, result.TotalCount);
        Assert.True(authority.DiscoverPages > 1); // every page of the authority's discover set was read
        Assert.DoesNotContain(notMine, store.RequestedIds);
    }

    [Fact]
    public async Task DuplicateReferencesReturnEveryAuthorizedMatchInTheOneTableWithSortingAndPaging()
    {
        var older = IdWithSuffix("C0FFEE00", 20);
        var newer = IdWithSuffix("C0FFEE00", 21);
        var oldest = IdWithSuffix("C0FFEE00", 22);
        var authority = new FakeAuthority { Authorized = [older, newer, oldest] };
        var store = new FakeStore([Record(older, "Submitted", minutesAgo: 60), Record(newer, "Draft", minutesAgo: 1), Record(oldest, "Draft", minutesAgo: 600)]);
        var handler = Handler(store, authority);

        var all = await handler.HandleAsync(Context, Query("REC-C0FFEE00"), null, CancellationToken.None);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal([newer, older, oldest], all.Items.Select(m => m.FormRecordId)); // most recently changed first; none chosen for the user

        var first = await handler.HandleAsync(Context, Query("REC-C0FFEE00", pageSize: 2), null, CancellationToken.None);
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.ContinuationToken);
        var second = await handler.HandleAsync(Context, Query("REC-C0FFEE00", pageSize: 2), first.ContinuationToken, CancellationToken.None);
        Assert.Equal([oldest], second.Items.Select(m => m.FormRecordId));

        // A page position belongs to the search it came from.
        var mismatch = await Assert.ThrowsAsync<BusinessException>(() => handler.HandleAsync(Context, Query("REC-DEADBEEF", pageSize: 2), first.ContinuationToken, CancellationToken.None));
        Assert.Equal("CURSOR_SCOPE_MISMATCH", mismatch.ErrorCode);
    }

    [Fact]
    public async Task AnAuthorizedMatchWithoutBusinessMetadataIsReportedNotDropped()
    {
        var orphan = IdWithSuffix("DEADBEEF", 30);
        var result = await Handler(new FakeStore([]), new FakeAuthority { Authorized = [orphan] }).HandleAsync(Context, Query("REC-DEADBEEF"), null, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(1, result.UnavailableRecordCount);
        Assert.Equal(orphan, Assert.Single(result.UnavailableRecordIds!));
    }

    [Fact]
    public async Task AnAuthorityFailureIsATechnicalFailureNeverAnEmptyResult()
    {
        var authority = new FakeAuthority { Failure = new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true) };
        var failure = await Assert.ThrowsAsync<AuthorityClientException>(() => Handler(new FakeStore([]), authority).HandleAsync(Context, Query("REC-1A2B3C4D"), null, CancellationToken.None));
        Assert.Equal("AUTHORITY_UNAVAILABLE", failure.ErrorCode);
    }

    [Fact]
    public async Task ADiscoverSetAboveTheEnumerationBoundFailsClosedInsteadOfAnsweringFromAPartialSet()
    {
        var authority = new FakeAuthority { Authorized = Enumerable.Range(0, AuthorizedRecordEnumerator.MaximumRecords + 1).Select(i => IdWithSuffix($"{i:X8}", i)).ToList() };
        var failure = await Assert.ThrowsAsync<BusinessException>(() => Handler(new FakeStore([]), authority).HandleAsync(Context, Query("REC-00000001"), null, CancellationToken.None));
        Assert.Equal("RECORD_LIST_TOO_LARGE", failure.ErrorCode);
    }

    [Fact]
    public void ASearchThatIsNotAnExactReferenceOrIdIsRefusedWhenTheQueryIsBuilt()
    {
        Assert.Equal("LOOKUP_INVALID", Assert.Throws<BusinessException>(() => Query("Smith")).ErrorCode);
        Assert.Null(Query("   ").Search); // an empty box is no search
        Assert.Equal("REC-017EF770", Query("rec-017ef770").Search);
    }

    private static FormRecord Record(Guid id, string status, int minutesAgo = 5) => new()
    {
        FormRecordId = id, TenantId = Org, FormDefinitionId = Form, RecordStatus = status, CreatedByUserId = Me,
        CreatedOn = DateTimeOffset.UtcNow.AddDays(-1), ModifiedOn = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
    };

    // ---- stand-ins (DispatchProxy: only the members the handler uses are implemented; anything else fails the test) ----

    public class Stub<T> : DispatchProxy
        where T : class
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private static class Stubs
    {
        public static T Create<T>(Func<MethodInfo, object?[], object?> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy).Handler = handler;
            return proxy;
        }
    }

    private sealed class FakeAuthority
    {
        public FakeAuthority() => Proxy = Stubs.Create<IAuthorizationAuthorityClient>(Handle);

        public IAuthorizationAuthorityClient Proxy { get; }

        public List<Guid> Authorized { get; init; } = [];

        public Func<Guid, bool> DiscoverAllowed { get; init; } = _ => true;

        public Func<Guid, bool> EditAllowed { get; init; } = _ => true;

        public Exception? Failure { get; init; }

        public int DiscoverPages { get; private set; }

        public List<(Guid Id, ProtectedAction Action)> SingleChecks { get; } = [];

        private object? Handle(MethodInfo method, object?[] args)
        {
            if (Failure is not null)
            {
                return method.Name switch
                {
                    nameof(IAuthorizationAuthorityClient.CheckPermissionAsync) => Task.FromException<PermissionDecision>(Failure),
                    nameof(IAuthorizationAuthorityClient.DiscoverAuthorizedRecordsAsync) => Task.FromException<RecordDiscoveryPage>(Failure),
                    _ => throw new NotSupportedException(method.Name),
                };
            }

            switch (method.Name)
            {
                case nameof(IAuthorizationAuthorityClient.CheckPermissionAsync):
                    var request = (PermissionCheckRequest)args[1]!;
                    SingleChecks.Add((request.Resource.ResourceId, request.RequestedAction));
                    return Task.FromResult(new PermissionDecision(request.RequestedAction == ProtectedAction.Discover && DiscoverAllowed(request.Resource.ResourceId), null, 9, 9, null, Guid.Empty));
                case nameof(IAuthorizationAuthorityClient.CheckPermissionsBatchAsync):
                    var requests = (IReadOnlyList<PermissionCheckRequest>)args[1]!;
                    return Task.FromResult<IReadOnlyList<PermissionDecision>>(requests.Select(r => new PermissionDecision(EditAllowed(r.Resource.ResourceId), null, 9, 9, null, Guid.Empty)).ToList());
                case nameof(IAuthorizationAuthorityClient.DiscoverAuthorizedRecordsAsync):
                    DiscoverPages++;
                    var size = (int)args[2]!;
                    var skip = args[3] is string token ? int.Parse(token, System.Globalization.CultureInfo.InvariantCulture) : 0;
                    var page = Authorized.Skip(skip).Take(size).ToList();
                    return Task.FromResult(new RecordDiscoveryPage(page, 9, skip + page.Count < Authorized.Count ? (skip + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
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
            Proxy = Stubs.Create<IBusinessStore>((method, _) => method.Name == nameof(IBusinessStore.BeginScopeAsync) ? Task.FromResult(NewScope()) : throw new NotSupportedException(method.Name));
        }

        public IBusinessStore Proxy { get; }

        public int Scopes { get; private set; }

        public List<Guid> RequestedIds { get; } = [];

        private IBusinessScope NewScope()
        {
            Scopes++;
            return Stubs.Create<IBusinessScope>((method, args) => method.Name switch
            {
                nameof(IBusinessScope.GetFormRecordsAsync) => Task.FromResult(Requested((IReadOnlyCollection<Guid>)args[1]!)),
                nameof(IBusinessScope.GetFormDefinitionsAsync) => Task.FromResult(new List<FormDefinition> { new() { FormDefinitionId = Form, TenantId = Org, FormDefinitionName = "Case intake" } }),
                nameof(IBusinessScope.GetUsersAsync) => Task.FromResult(new List<User> { new() { UserId = Me, TenantId = Org, DisplayName = "Tester" } }),
                nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
                _ => throw new NotSupportedException(method.Name),
            });
        }

        private List<FormRecord> Requested(IReadOnlyCollection<Guid> ids)
        {
            RequestedIds.AddRange(ids);
            return records.Where(r => ids.Contains(r.FormRecordId)).ToList();
        }
    }
}
