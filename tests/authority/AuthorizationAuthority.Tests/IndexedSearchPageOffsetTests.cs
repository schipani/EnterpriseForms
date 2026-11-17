using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Contracts.V1;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// PR #57, CodeQL alert 3 (cs/user-controlled-bypass at IndexedSearchService.PageAsync, the offset bounds check): the caller-supplied
/// page offset can only REJECT a request before anything is read; it can never route a request around the page's re-authorization.
/// An out-of-range offset fails with INVALID_COMMAND before the Authority store or the search reader is opened, so no snapshot,
/// session, SpiceDB decision, predicate approval (AuthorizePredicates) or record is touched and nothing is released. Every in-range
/// offset, including both bounds, proceeds into the store, where the complete §17.3 check sequence (actor binding, session, gate and
/// S1, snapshot ownership and query digest, revision, AuthorizePredicates, fresh SpiceDB discover/read per page record, I-2, seal,
/// S1 verify, locked re-check) runs before the single return. The stack behaviour of those checks is exercised by the Stage B
/// IndexedSearchQueryStackTests (release-only, C-131).
/// </summary>
public sealed class IndexedSearchPageOffsetTests
{
    private static readonly VerifiedActor Actor = new("https://issuer.example.test/", "subject-1", "business-api");

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(10_001)]
    [InlineData(int.MaxValue)]
    public async Task AnOutOfRangeOffsetIsRefusedBeforeAnySnapshotSessionOrAuthorizationStateIsRead(int offset)
    {
        var (service, store, reader) = Create(new IndexedSearchOptions { QueryEnabled = true });
        Assert.Equal(10_000, new IndexedSearchOptions().MaximumResults);

        var refused = await Assert.ThrowsAsync<AuthorityException>(() => service.PageAsync(Actor, Guid.CreateVersion7(), Request(offset), Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Equal(AuthorityErrorCodes.InvalidCommand, refused.ErrorCode);
        Assert.Equal(422, refused.HttpStatusCode);
        Assert.Equal(0, store.ScopesOpened);
        Assert.Equal(0, reader.SessionsOpened);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9_999)]
    [InlineData(10_000)]
    public async Task EveryInRangeOffsetEntersTheFullReauthorizationPathAndNeverShortCircuits(int offset)
    {
        var (service, store, reader) = Create(new IndexedSearchOptions { QueryEnabled = true });

        await Assert.ThrowsAsync<StoreReachedException>(() => service.PageAsync(Actor, Guid.CreateVersion7(), Request(offset), Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Equal(1, store.ScopesOpened);
        Assert.Equal(0, reader.SessionsOpened); // the reader is opened only after the actor, session, gate, snapshot and revision checks
    }

    [Fact]
    public async Task TheBoundFollowsTheConfiguredMaximumAndNotTheCaller()
    {
        var (service, store, _) = Create(new IndexedSearchOptions { QueryEnabled = true, MaximumResults = 50 });

        await Assert.ThrowsAsync<StoreReachedException>(() => service.PageAsync(Actor, Guid.CreateVersion7(), Request(50), Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => service.PageAsync(Actor, Guid.CreateVersion7(), Request(51), Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Equal(AuthorityErrorCodes.InvalidCommand, refused.ErrorCode);
        Assert.Equal(1, store.ScopesOpened);
    }

    [Fact]
    public async Task WhenIndexedSearchIsDisabledNoOffsetReachesTheStore()
    {
        var (service, store, _) = Create(new IndexedSearchOptions { QueryEnabled = false });

        foreach (var offset in new[] { -1, 0, 10_000, int.MaxValue })
        {
            await Assert.ThrowsAsync<AuthorityException>(() => service.PageAsync(Actor, Guid.CreateVersion7(), Request(offset), Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, store.ScopesOpened);
    }

    private static IndexedSearchPageRequest Request(int offset) => new(
        Guid.CreateVersion7(),
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        new IndexedSearchQuery(Guid.CreateVersion7(), [new IndexedSearchCondition(Guid.CreateVersion7(), "equals", "String", "value", null, null)], null, false, false, null, null, 25),
        offset);

    private static (IndexedSearchService Service, CountingStore Store, CountingReader Reader) Create(IndexedSearchOptions options)
    {
        var store = new CountingStore();
        var reader = new CountingReader();
        // The verifier, seal verifier, sweep and alert sink are never reached before the store is opened; null proves it.
        var service = new IndexedSearchService(store, reader, null!, null!, null!, new SearchIndexOptions { Mode = SearchIndexMode.Dark }, options, null!, TimeProvider.System);
        return (service, store, reader);
    }

    private sealed class StoreReachedException() : Exception("the Authority store was opened: the full page re-authorization begins here");

    private sealed class CountingStore : IAuthorityStore
    {
        public int ScopesOpened { get; private set; }

        public Task<IAuthorityScope> BeginScopeAsync(CancellationToken cancellationToken)
        {
            ScopesOpened++;
            throw new StoreReachedException();
        }

        public Task<IAuthorityScope> BeginReadOnlySnapshotScopeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> GetOrganizationsWithPendingWorkAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> GetAllOrganizationIdsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<OrganizationChangeMarker>> GetOrganizationChangeMarkersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CountingReader : ISearchQueryReader
    {
        public int SessionsOpened { get; private set; }

        public bool IsConfigured => true;

        public Task<ISearchQuerySession> BeginAsync(Guid organizationId, CancellationToken cancellationToken)
        {
            SessionsOpened++;
            throw new NotSupportedException();
        }
    }
}
