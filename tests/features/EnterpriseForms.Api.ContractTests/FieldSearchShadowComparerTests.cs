using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.Search.FieldSearch;
using EnterpriseForms.Contracts.Features;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// IX-3 business Shadow engine (R1 §G.3): V1 stays authoritative; the comparison reads every indexed page and classifies the
/// outcome. Unit level, with a fake Authority: classifications, the documented legitimate differences, skips, paging and the time
/// budget. The real comparison runs in IndexedSearchQueryStackTests.
/// </summary>
public sealed class FieldSearchShadowComparerTests
{
    private static readonly Guid Field = Guid.CreateVersion7();

    private sealed class Observer : IFieldSearchShadowObserver
    {
        public List<FieldSearchShadowComparison> Seen { get; } = [];

        public void Compared(FieldSearchShadowComparison comparison) => Seen.Add(comparison);
    }

    private sealed class FakeIndexed(Func<int, AuthorityIndexedSearchResult>? pages = null, string? error = null, TimeSpan? delay = null) : IIndexedSearchAuthority
    {
        public int Calls { get; private set; }

        public List<string> Nonces { get; } = [];

        public async Task<AuthorityIndexedSearchResult> SearchAsync(DelegatedUserContext user, string sessionDigestBase64, string queryNonceBase64, AuthorityIndexedSearchQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            Nonces.Add(queryNonceBase64);
            if (delay is { } d)
            {
                await Task.Delay(d, cancellationToken);
            }

            return error is null ? pages!(0) : throw new AuthorityClientException(error, 503, false);
        }

        public Task<AuthorityIndexedSearchResult> PageAsync(DelegatedUserContext user, Guid searchResultSnapshotId, string sessionDigestBase64, string queryNonceBase64, AuthorityIndexedSearchQuery query, int offset, CancellationToken cancellationToken)
        {
            Calls++;
            Nonces.Add(queryNonceBase64);
            return Task.FromResult(pages!(offset));
        }
    }

    private static RequestContext Context(bool withSession = true) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "https://issuer.test", "subject", "Caller", "assertion", Guid.CreateVersion7()) { SessionDigestBase64 = withSession ? Convert.ToBase64String(new byte[32]) : null };

    private static FieldSearchRequest Request() => new(Guid.CreateVersion7(), [new FieldSearchCondition(Field, "equals", "String", "x", null, null)], null, false, false, null, null, 25);

    private static Func<int, AuthorityIndexedSearchResult> Paged(List<Guid> ids) => offset =>
        new(Guid.CreateVersion7(), Guid.Empty, ids.Count, offset, 100, ids.Skip(offset).Take(100).Select(i => new AuthorityIndexedSearchItem(i, "Draft", null, Guid.Empty, default, default, false)).ToList(), ids.Count, 0, 1, 1, 1, default, "AuthorityAcceptedContent", Guid.Empty);

    private static (FieldSearchShadowComparer Comparer, Observer Observer) Make(IIndexedSearchAuthority indexed, int sample = 100, int seconds = 15)
    {
        var limits = new FieldSearchLimits { Engine = FieldSearchLimits.EngineShadow, ShadowSamplePercent = sample, ShadowMaximumSeconds = seconds };
        var observer = new Observer();
        return (new FieldSearchShadowComparer(limits, indexed, observer, TimeProvider.System), observer);
    }

    [Fact]
    public async Task IdenticalOrderedIdsAcrossSeveralIndexedPagesAreAMatch()
    {
        var ids = Enumerable.Range(0, 250).Select(_ => Guid.CreateVersion7()).ToList();
        var indexed = new FakeIndexed(Paged(ids));
        var (comparer, observer) = Make(indexed);
        var result = await comparer.CompareAsync(Context(), Request(), FieldSearchV1Outcome.Success(ids), false, CancellationToken.None);
        Assert.Equal("Match", result.Classification);
        Assert.Equal(3, indexed.Calls); // the first page and two further pages, every page with the same nonce
        Assert.Single(indexed.Nonces.Distinct());
        Assert.Single(observer.Seen);
    }

    [Fact]
    public async Task OrderCountOrMembershipDifferencesAreUnexplainedDifferences()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToList();
        var swapped = new List<Guid>(ids);
        (swapped[1], swapped[2]) = (swapped[2], swapped[1]);
        var (comparer, _) = Make(new FakeIndexed(Paged(swapped)));
        var order = await comparer.CompareAsync(Context(), Request(), FieldSearchV1Outcome.Success(ids), false, CancellationToken.None);
        Assert.Equal("Difference", order.Classification);
        Assert.Equal(1, order.FirstDifferingPosition);

        var (missing, _) = Make(new FakeIndexed(Paged(ids.Take(4).ToList())));
        Assert.Equal("Difference", (await missing.CompareAsync(Context(), Request(), FieldSearchV1Outcome.Success(ids), false, CancellationToken.None)).Classification);

        var (extra, _) = Make(new FakeIndexed(Paged([.. ids, Guid.CreateVersion7()])));
        Assert.Equal("Difference", (await extra.CompareAsync(Context(), Request(), FieldSearchV1Outcome.Success(ids), false, CancellationToken.None)).Classification);
    }

    [Fact]
    public async Task ErrorsCompareByCodeAndTheDocumentedLegitimateDifferencesAreLabelled()
    {
        var quarantined = FieldSearchV1Outcome.Failure(SearchException.VerificationUnavailable("RECORD_QUARANTINED", retryable: false));
        var (same, _) = Make(new FakeIndexed(error: "RECORD_QUARANTINED"));
        Assert.Equal("Match", (await same.CompareAsync(Context(), Request(), quarantined, false, CancellationToken.None)).Classification);

        var (differentError, _) = Make(new FakeIndexed(error: "SEARCH_INDEX_INCONSISTENT"));
        Assert.Equal("Difference", (await differentError.CompareAsync(Context(), Request(), quarantined, false, CancellationToken.None)).Classification);

        var ids = new List<Guid> { Guid.CreateVersion7() };
        var (scopeLimit, _) = Make(new FakeIndexed(Paged(ids)));
        Assert.Equal("LegitimateV1ScopeLimit", (await scopeLimit.CompareAsync(Context(), Request(), FieldSearchV1Outcome.Failure(SearchException.VerificationLimit("records")), false, CancellationToken.None)).Classification);

        var (damage, _) = Make(new FakeIndexed(Paged(ids)));
        Assert.Equal("LegitimateBusinessCopyNotVerified", (await damage.CompareAsync(Context(), Request(), FieldSearchV1Outcome.Failure(SearchException.VerificationUnavailable("RECORD_VERIFICATION_FAILED", retryable: false)), false, CancellationToken.None)).Classification);

        // A V1 failure of another kind, with an indexed answer, is not explained away.
        var (other, _) = Make(new FakeIndexed(Paged(ids)));
        Assert.Equal("Difference", (await other.CompareAsync(Context(), Request(), FieldSearchV1Outcome.Failure(SearchException.VerificationUnavailable("AUTHORITY_GATE_CLOSED", retryable: true)), false, CancellationToken.None)).Classification);

        var (notReady, _) = Make(new FakeIndexed(error: "SEARCH_INDEX_NOT_READY"));
        Assert.Equal("NotComparable", (await notReady.CompareAsync(Context(), Request(), FieldSearchV1Outcome.Success(ids), false, CancellationToken.None)).Classification);
    }

    [Fact]
    public async Task NoSessionNoSampleOrNoShadowEngineSkipWithoutCallingTheAuthority()
    {
        var indexed = new FakeIndexed(Paged([]));
        var (comparer, _) = Make(indexed);
        Assert.Equal("NO_BROWSER_SESSION", (await comparer.CompareAsync(Context(withSession: false), Request(), FieldSearchV1Outcome.Success([]), true, CancellationToken.None)).SkippedReason);
        var (unsampled, _) = Make(indexed, sample: 0);
        Assert.Equal("NOT_SAMPLED", (await unsampled.CompareAsync(Context(), Request(), FieldSearchV1Outcome.Success([]), false, CancellationToken.None)).SkippedReason);
        Assert.Equal("Match", (await unsampled.CompareAsync(Context(), Request(), FieldSearchV1Outcome.Success([]), true, CancellationToken.None)).Classification); // forced (Testing)
        var off = new FieldSearchShadowComparer(new FieldSearchLimits(), indexed, new Observer(), TimeProvider.System);
        Assert.False(off.Enabled);
        Assert.Equal(1, indexed.Calls);
    }

    [Fact]
    public async Task TheShadowTimeBudgetEndsTheComparisonAsNotComparable()
    {
        var (comparer, _) = Make(new FakeIndexed(Paged([]), delay: TimeSpan.FromSeconds(30)), seconds: 1);
        var started = DateTime.UtcNow;
        var result = await comparer.CompareAsync(Context(), Request(), FieldSearchV1Outcome.Success([]), true, CancellationToken.None);
        Assert.Equal("NotComparable", result.Classification);
        Assert.Equal("SHADOW_TIME_BUDGET", result.IndexedError);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void TheEngineSettingIsValidatedAndShadowIsADevelopmentAndTestingCapability()
    {
        new FieldSearchLimits { Engine = FieldSearchLimits.EngineShadow }.Validate();
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { Engine = "Indexed" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { ShadowSamplePercent = 101 }.Validate());
        new FieldSearchLimits { Engine = FieldSearchLimits.EngineShadow }.ValidateEnvironment("Testing");
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { Engine = FieldSearchLimits.EngineShadow }.ValidateEnvironment("Production"));
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { Engine = FieldSearchLimits.EngineShadow }.ValidateEnvironment("Staging"));
    }
}
