using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Ports;
using EnterpriseForms.Application.Features.Search.FieldSearch;
using EnterpriseForms.Application.Features.Search.Verification;
using EnterpriseForms.Contracts.Features;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// IX-4 business <c>Indexed</c> engine (design §16 IX-4, R1 §E.1, §G.2, §6.6, §17.2), unit level with a fake Authority: results are
/// never presented as verified; the V1 fallback runs only for the index states of §17.2 and only within V1's limits; organization and
/// record quarantine, limits and authorization refusals never fall back; sessionless callers get V1; the cursor is bound to the
/// caller and session and carries no result. The real stack runs in IndexedFieldSearchEngineStackTests.
/// </summary>
public sealed class IndexedFieldSearchEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Field = Guid.CreateVersion7();
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Caller = Guid.CreateVersion7();
    private static readonly string Session = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());

    private static RequestContext Context(string? session = null, Guid? user = null, bool withSession = true) =>
        new(Organization, user ?? Caller, "https://issuer.test", "subject", "Caller", "assertion", Guid.CreateVersion7()) { SessionDigestBase64 = withSession ? session ?? Session : null };

    private static FieldSearchRequest Request(int pageSize = 10) => new(Form, [new FieldSearchCondition(Field, "equals", "String", "Sentinel-Value-Q7")], null, false, false, null, null, pageSize);

    /// <summary>A reversible stand-in for Data Protection (the real protector is authenticated encryption; the binding rules are what is tested).</summary>
    private sealed class ReversibleCursors : IOpaqueCursorProtector
    {
        public List<string> Payloads { get; } = [];

        public string Protect(string payload, TimeSpan lifetime)
        {
            Payloads.Add(payload);
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)).Replace('+', '-').Replace('/', '_');
        }

        public string? Unprotect(string token)
        {
            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/')));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    private sealed class Names : IRecordDisplayDirectory
    {
        public Task<RecordDisplayNames> GetNamesAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(new RecordDisplayNames(new Dictionary<Guid, string> { [Form] = "Synthetic form" }, new Dictionary<Guid, string> { [Caller] = "Caller" }));

        public string ReferenceFor(Guid formRecordId) => "REC-" + formRecordId.ToString("N")[..8].ToUpperInvariant();
    }

    private sealed class FakeIndexed : IIndexedSearchAuthority
    {
        public List<Guid> Ids { get; init; } = [];

        public Queue<AuthorityClientException> SearchErrors { get; } = new();

        public AuthorityClientException? PageError { get; set; }

        public List<(string Session, string Nonce, AuthorityIndexedSearchQuery Query, int? Offset)> Calls { get; } = [];

        public Guid Snapshot { get; } = Guid.CreateVersion7();

        public Task<AuthorityIndexedSearchResult> SearchAsync(DelegatedUserContext user, string sessionDigestBase64, string queryNonceBase64, AuthorityIndexedSearchQuery query, CancellationToken cancellationToken)
        {
            Calls.Add((sessionDigestBase64, queryNonceBase64, query, null));
            return SearchErrors.TryDequeue(out var error) ? throw error : Task.FromResult(Page(query, 0));
        }

        public Task<AuthorityIndexedSearchResult> PageAsync(DelegatedUserContext user, Guid searchResultSnapshotId, string sessionDigestBase64, string queryNonceBase64, AuthorityIndexedSearchQuery query, int offset, CancellationToken cancellationToken)
        {
            Calls.Add((sessionDigestBase64, queryNonceBase64, query, offset));
            return PageError is { } error ? throw error : Task.FromResult(Page(query, offset));
        }

        private AuthorityIndexedSearchResult Page(AuthorityIndexedSearchQuery query, int offset) =>
            new(Snapshot, query.FormDefinitionId, Ids.Count, offset, query.PageSize,
                Ids.Skip(offset).Take(query.PageSize).Select(i => new AuthorityIndexedSearchItem(i, "Draft", null, Caller, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), true)).ToList(),
                Ids.Count + 3, 1, 42, 7, 5, DateTimeOffset.UtcNow.AddMinutes(5), FieldSearchResultBases.AuthorityAcceptedContent, Guid.CreateVersion7());
    }

    private sealed class EngineObserver : IFieldSearchEngineObserver
    {
        public List<(string Reason, string Outcome)> Fallbacks { get; } = [];

        public List<(string Stage, string Code, int Retry, double DelayMs)> Retries { get; } = [];

        public void FallbackToVerifiedScan(string indexReason, string outcome) => Fallbacks.Add((indexReason, outcome));

        public void Retrying(Guid correlationId, string stage, string code, int retryNumber, double delayMilliseconds) => Retries.Add((stage, code, retryNumber, delayMilliseconds));
    }

    /// <summary>C-150 R56-A: a capability check that first fails with the queued Authority refusals, then answers as the approving policy.</summary>
    private sealed class FlakyPolicy(params AuthorityClientException[] failures) : IFieldSearchPolicyAuthority
    {
        private readonly Queue<AuthorityClientException> pending = new(failures);
        private readonly PhaseC7FieldSearchTests.ApprovingPolicy inner = new() { Request = Request() };

        public int Calls { get; private set; }

        public Task<AuthorityFieldSearchCapabilities> GetFieldSearchCapabilitiesAsync(DelegatedUserContext user, FieldSearchCapabilitiesQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            return pending.TryDequeue(out var failure) ? throw failure : inner.GetFieldSearchCapabilitiesAsync(user, query, cancellationToken);
        }

        public Task<AuthorityFieldSearchAdministration> GetFieldSearchAdministrationAsync(DelegatedUserContext user, FieldSearchAdministrationQuery query, CancellationToken cancellationToken) =>
            inner.GetFieldSearchAdministrationAsync(user, query, cancellationToken);

        public Task RecordSearchExecutionAsync(DelegatedUserContext user, Guid searchCatalogueSnapshotId, SearchExecutionEvidence evidence, CancellationToken cancellationToken) =>
            inner.RecordSearchExecutionAsync(user, searchCatalogueSnapshotId, evidence, cancellationToken);
    }

    /// <summary>The V1 side: a stub Authority whose catalogue answers with <paramref name="catalogueFailure"/> (V1 is then the verified-scan failure).</summary>
    private static (FieldSearchHandler Handler, FakeIndexed Indexed, ReversibleCursors Cursors, EngineObserver Observer, List<string> V1Calls) Make(string engine = FieldSearchLimits.EngineIndexed,
        AuthorityClientException? catalogueFailure = null, FakeIndexed? indexed = null, IFieldSearchPolicyAuthority? policy = null)
    {
        var limits = new FieldSearchLimits { ExecutionEnabled = true, Engine = engine, RetryInitialDelayMilliseconds = 10, RetryMaximumDelayMilliseconds = 20 };
        var v1Calls = new List<string>();
        var authority = DispatchProxy.Create<IAuthorizationAuthorityClient, PhaseC7ScopeVerificationTests.Stub<IAuthorizationAuthorityClient>>();
        ((PhaseC7ScopeVerificationTests.Stub<IAuthorizationAuthorityClient>)(object)authority).Handler = (method, _) =>
        {
            v1Calls.Add(method.Name);
            throw catalogueFailure ?? new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true);
        };
        var cursors = new ReversibleCursors();
        var fake = indexed ?? new FakeIndexed { Ids = Enumerable.Range(0, 23).Select(_ => Guid.CreateVersion7()).ToList() };
        var observer = new EngineObserver();
        var admission = new FieldSearchAdmission(limits);
        var retry = new FieldSearchRetryPolicy(limits, TimeProvider.System, observer);
        var engineInstance = new FieldSearchIndexedEngine(limits, fake, admission, cursors, new Names(), TimeProvider.System, retry);
        var handler = new FieldSearchHandler(limits, policy ?? new PhaseC7FieldSearchTests.ApprovingPolicy { Request = Request() }, new TrustedScopeVerifier(authority, new NoRecords(), limits, TimeProvider.System),
            admission, authority, cursors, new Names(), TimeProvider.System, null, engineInstance, observer, retry);
        return (handler, fake, cursors, observer, v1Calls);
    }

    private sealed class NoRecords : IAcceptedRecordItemsReader
    {
        public Task<IReadOnlyList<Application.Common.Authority.StagedContentItem>> ReadObservedItemsAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) => throw new InvalidOperationException("not reached");

        public Task<AcceptedRecordContent> ReadAcceptedContentAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) => throw new InvalidOperationException("not reached");
    }

    // ---- results ----

    [Fact]
    public async Task AnIndexedResultIsAuthorityAcceptedContentAndNeverPresentedAsVerified()
    {
        var (handler, indexed, cursors, observer, v1) = Make();
        var response = await handler.SearchAsync(Context(), Request(), Ct);
        Assert.Equal(FieldSearchResultBases.AuthorityAcceptedContent, response.ResultBasis);
        Assert.All(response.Items, i => Assert.False(i.ProjectionVerified)); // IX-37: no row claims a verified business copy
        Assert.Equal(23, response.TotalCount);
        Assert.Equal(10, response.Items.Count);
        Assert.Equal((1, "Complete"), (response.PageStart, response.Completeness));
        Assert.Contains(FieldSearchIndexedEngine.AcceptedContentStatement, response.ScopeStatement, StringComparison.Ordinal);
        Assert.StartsWith("Complete: all 26 records of this form that you can find were searched at one Authority snapshot; 1 pending creation(s)", response.ScopeStatement, StringComparison.Ordinal);
        Assert.DoesNotContain("were verified at one Authority snapshot", response.ScopeStatement, StringComparison.Ordinal);
        Assert.All(response.Items, i => Assert.Equal("Synthetic form", i.FormDefinitionName));
        Assert.All(response.Items, i => Assert.True(i.CreatedByCurrentUser && i.CanEdit));
        Assert.Empty(v1); // V1 never ran: one search never combines engines
        Assert.Empty(observer.Fallbacks);
        var call = Assert.Single(indexed.Calls);
        Assert.Equal(Session, call.Session);
        Assert.Equal(32, Convert.FromBase64String(call.Nonce).Length);
        // The cursor holds the snapshot, nonce and query (encrypted in production); never the result ids.
        Assert.StartsWith(IndexedFieldSearchCursor.TokenPrefix + "10.", response.ContinuationToken, StringComparison.Ordinal);
        Assert.Null(response.PreviousToken);
        Assert.DoesNotContain(cursors.Payloads, p => indexed.Ids.Any(id => p.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task PagesComeFromTheAuthorityWithTheSameSessionNonceAndQuery()
    {
        var (handler, indexed, _, _, _) = Make();
        var first = await handler.SearchAsync(Context(), Request(), Ct);
        var second = await handler.PageAsync(Context(), new FieldSearchPageRequest(first.ContinuationToken), Ct);
        Assert.Equal((11, 10, FieldSearchResultBases.AuthorityAcceptedContent), (second.PageStart, second.Items.Count, second.ResultBasis));
        var third = await handler.PageAsync(Context(), new FieldSearchPageRequest(second.ContinuationToken), Ct);
        Assert.Equal((21, 3), (third.PageStart, third.Items.Count));
        Assert.Null(third.ContinuationToken);
        var back = await handler.PageAsync(Context(), new FieldSearchPageRequest(third.PreviousToken), Ct);
        Assert.Equal(11, back.PageStart);
        Assert.Equal([null, 10, 20, 10], indexed.Calls.Select(c => c.Offset));
        Assert.Single(indexed.Calls.Select(c => c.Nonce).Distinct());
        Assert.All(indexed.Calls, c => Assert.Equal(Session, c.Session));
        Assert.All(indexed.Calls, c => Assert.Equal("Sentinel-Value-Q7", c.Query.Conditions.Single().Value));
    }

    [Fact]
    public async Task ACursorIsBoundToTheCallerAndTheSessionAndAnAlteredOneIsRefused()
    {
        var (handler, _, _, _, _) = Make();
        var token = (await handler.SearchAsync(Context(), Request(), Ct)).ContinuationToken!;
        var otherSession = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());
        foreach (var context in new[] { Context(session: otherSession), Context(user: Guid.CreateVersion7()), Context(withSession: false) })
        {
            Assert.Equal(SearchErrorCodes.SnapshotChanged, (await Assert.ThrowsAsync<SearchException>(() => handler.PageAsync(context, new FieldSearchPageRequest(token), Ct))).ErrorCode);
        }

        var tampered = token[..^4] + "AAAA";
        Assert.Contains((await Assert.ThrowsAsync<SearchException>(() => handler.PageAsync(Context(), new FieldSearchPageRequest(tampered), Ct))).ErrorCode, new[] { SearchErrorCodes.SnapshotChanged, SearchErrorCodes.RequestInvalid });
        Assert.Equal(SearchErrorCodes.RequestInvalid, (await Assert.ThrowsAsync<SearchException>(() => handler.PageAsync(Context(), new FieldSearchPageRequest("i.x." + token[4..]), Ct))).ErrorCode);
        Assert.Equal(SearchErrorCodes.RequestInvalid, (await Assert.ThrowsAsync<SearchException>(() => handler.PageAsync(Context(), new FieldSearchPageRequest("i.9999." + token[(token.IndexOf('.', 2) + 1)..]), Ct))).ErrorCode);
    }

    // ---- fallback (IX-D8) ----

    public static TheoryData<string, bool> IndexStates => new()
    {
        { "SEARCH_INDEX_NOT_READY", true },
        { "SEARCH_INDEX_INCONSISTENT", false },
        { "SEARCH_INDEX_VERIFICATION_OVERDUE", false },
    };

    [Theory]
    [MemberData(nameof(IndexStates))]
    public async Task WhenTheIndexCannotAnswerTheCompleteV1PathRunsAndItsOwnFailureIsReturned(string code, bool retryable)
    {
        var indexed = new FakeIndexed();
        indexed.SearchErrors.Enqueue(new AuthorityClientException(code, 503, retryable));
        var (handler, _, _, observer, v1) = Make(indexed: indexed, catalogueFailure: new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true));
        var failure = await Assert.ThrowsAsync<SearchException>(() => handler.SearchAsync(Context(), Request(), Ct));
        // V1 ran (the catalogue was requested) and answered with its own failure: never an indexed answer, never mixed.
        Assert.Contains(nameof(IAuthorizationAuthorityClient.IssueSearchCatalogueAsync), v1);
        Assert.Equal((SearchErrorCodes.VerificationUnavailable, "AUTHORITY_UNAVAILABLE"), (failure.ErrorCode, failure.UnavailableReason));
        Assert.Equal([(code, SearchErrorCodes.VerificationUnavailable)], observer.Fallbacks);
    }

    [Theory]
    [MemberData(nameof(IndexStates))]
    public async Task OutsideV1LimitsTheFallbackReturnsTheIndexReason(string code, bool retryable)
    {
        var indexed = new FakeIndexed();
        indexed.SearchErrors.Enqueue(new AuthorityClientException(code, 503, retryable));
        var (handler, _, _, observer, _) = Make(indexed: indexed, catalogueFailure: new AuthorityClientException("LIMIT_EXCEEDED", 422, false));
        var failure = await Assert.ThrowsAsync<SearchException>(() => handler.SearchAsync(Context(), Request(), Ct));
        Assert.Equal((SearchErrorCodes.VerificationUnavailable, code, retryable, 503), (failure.ErrorCode, failure.UnavailableReason, failure.Retryable, failure.HttpStatusCode));
        Assert.Equal([(code, "V1_LIMIT")], observer.Fallbacks);
    }

    public static TheoryData<string, int, string, string?> FinalRefusals => new()
    {
        // S1 and record quarantine: the same reason V1 gives (TrustedScopeVerifier), and V1 is never run (Q2).
        { "INTEGRITY_QUARANTINE", 423, SearchErrorCodes.VerificationUnavailable, "RECORD_QUARANTINED" },
        { "RECORD_QUARANTINED", 503, SearchErrorCodes.VerificationUnavailable, "RECORD_QUARANTINED" },
        { "PREDICATE_VALUE_ENCRYPTED", 422, SearchErrorCodes.VerificationUnavailable, "PREDICATE_VALUE_ENCRYPTED" },
        { "SEARCH_CAPACITY_EXCEEDED", 503, SearchErrorCodes.VerificationUnavailable, "SEARCH_CAPACITY_EXCEEDED" },
        { "SEARCH_FIELD_NOT_ALLOWED", 422, SearchErrorCodes.FieldNotAllowed, null },
        { "SEARCH_OPERATOR_NOT_SUPPORTED", 422, SearchErrorCodes.OperatorNotSupported, null },
        { "SEARCH_VERIFICATION_LIMIT", 422, SearchErrorCodes.VerificationLimit, null },
        { "SEARCH_SESSION_INVALID", 401, "UNAUTHORIZED", null },
        { "SOMETHING_UNKNOWN", 500, SearchErrorCodes.VerificationUnavailable, "AUTHORITY_UNAVAILABLE" },
    };

    [Theory]
    [MemberData(nameof(FinalRefusals))]
    public async Task QuarantineLimitAndAuthorizationRefusalsNeverFallBack(string code, int status, string expected, string? reason)
    {
        var indexed = new FakeIndexed();
        indexed.SearchErrors.Enqueue(new AuthorityClientException(code, status, false, code is "SEARCH_FIELD_NOT_ALLOWED" or "SEARCH_OPERATOR_NOT_SUPPORTED" ? "condition 1: the field cannot be searched" : code == "SEARCH_VERIFICATION_LIMIT" ? "results" : null));
        var (handler, _, _, observer, v1) = Make(indexed: indexed);
        var failure = await Assert.ThrowsAsync<SearchException>(() => handler.SearchAsync(Context(), Request(), Ct));
        Assert.Equal(expected, failure.ErrorCode);
        Assert.Equal(reason, failure.UnavailableReason);
        Assert.Empty(v1);
        Assert.Empty(observer.Fallbacks);
        if (code == "SEARCH_VERIFICATION_LIMIT")
        {
            Assert.Equal("results", failure.LimitKind);
        }
    }

    [Fact]
    public async Task AChangedSnapshotIsRetriedWithBoundedBackoffThenReported()
    {
        // C-150 R56-A: retried with a new nonce (a new Authority snapshot) each time, at most RetryMaximumAttempts (4) calls.
        var indexed = new FakeIndexed { Ids = [Guid.CreateVersion7()] };
        indexed.SearchErrors.Enqueue(new AuthorityClientException("SEARCH_SNAPSHOT_CHANGED", 409, true));
        indexed.SearchErrors.Enqueue(new AuthorityClientException("AUTHORITY_NOT_READY", 503, true));
        var (handler, _, _, _, _) = Make(indexed: indexed);
        Assert.Equal(1, (await handler.SearchAsync(Context(), Request(), Ct)).TotalCount);
        Assert.Equal(3, indexed.Calls.Count);
        Assert.Equal(3, indexed.Calls.Select(c => c.Nonce).Distinct().Count()); // never a reused snapshot

        var always = new FakeIndexed();
        for (var i = 0; i < 6; i++)
        {
            always.SearchErrors.Enqueue(new AuthorityClientException("SEARCH_SNAPSHOT_CHANGED", 409, true));
        }

        var (again, _, _, _, _) = Make(indexed: always);
        Assert.Equal(SearchErrorCodes.SnapshotChanged, (await Assert.ThrowsAsync<SearchException>(() => again.SearchAsync(Context(), Request(), Ct))).ErrorCode);
        Assert.Equal(4, always.Calls.Count);
    }

    [Fact]
    public async Task AClosedGateAtTheCapabilityCheckIsRetriedAndSharesTheBudgetWithTheSearch()
    {
        // C-150 R56-A: the capability check's 503 AUTHORITY_NOT_READY is retried with backoff; its retries count against the same
        // per-request budget as the search attempts (RetryMaximumAttempts = 4: at most 3 retries in all).
        var policy = new FlakyPolicy(new AuthorityClientException("AUTHORITY_NOT_READY", 503, true));
        var indexed = new FakeIndexed { Ids = [Guid.CreateVersion7()] };
        var (handler, _, _, observer, _) = Make(indexed: indexed, policy: policy);
        Assert.Equal(1, (await handler.SearchAsync(Context(), Request(), Ct)).TotalCount);
        Assert.Equal((2, 1), (policy.Calls, indexed.Calls.Count));
        Assert.Equal([("capabilities", "AUTHORITY_NOT_READY", 1)], observer.Retries.Select(r => (r.Stage, r.Code, r.Retry)).ToList());

        var both = new FlakyPolicy(new AuthorityClientException("AUTHORITY_NOT_READY", 503, true), new AuthorityClientException("AUTHORITY_NOT_READY", 503, true));
        var failing = new FakeIndexed();
        for (var i = 0; i < 6; i++)
        {
            failing.SearchErrors.Enqueue(new AuthorityClientException("SEARCH_SNAPSHOT_CHANGED", 409, true));
        }

        var (shared, _, _, sharedObserver, _) = Make(indexed: failing, policy: both);
        Assert.Equal(SearchErrorCodes.SnapshotChanged, (await Assert.ThrowsAsync<SearchException>(() => shared.SearchAsync(Context(), Request(), Ct))).ErrorCode);
        Assert.Equal((3, 2), (both.Calls, failing.Calls.Count)); // two capability retries, then one search retry: 3 retries in all
        Assert.Equal(["capabilities", "capabilities", "indexed-search"], sharedObserver.Retries.Select(r => r.Stage).ToList());
    }

    [Theory]
    [InlineData("AUTHORITY_NOT_READY", 503, false)]
    [InlineData("INTEGRITY_QUARANTINE", 423, false)]
    [InlineData("SEARCH_FIELD_NOT_ALLOWED", 422, false)]
    [InlineData("AUTHORITY_UNAVAILABLE", 503, true)]
    [InlineData("SEARCH_CAPACITY_EXCEEDED", 503, true)]
    public async Task OtherCapabilityRefusalsAreNeverRetried(string code, int status, bool retryable)
    {
        var policy = new FlakyPolicy(new AuthorityClientException(code, status, retryable));
        var (handler, indexed, _, observer, _) = Make(policy: policy);
        Assert.Equal(code, (await Assert.ThrowsAsync<AuthorityClientException>(() => handler.SearchAsync(Context(), Request(), Ct))).ErrorCode);
        Assert.Equal(1, policy.Calls);
        Assert.Empty(indexed.Calls);
        Assert.Empty(observer.Retries);
    }

    [Theory]
    [InlineData("INTEGRITY_QUARANTINE", 423, true)]
    [InlineData("RECORD_QUARANTINED", 423, false)]
    [InlineData("SEARCH_FIELD_NOT_ALLOWED", 422, false)]
    [InlineData("SEARCH_VERIFICATION_LIMIT", 422, false)]
    [InlineData("SEARCH_CAPACITY_EXCEEDED", 503, true)]
    [InlineData("SEARCH_SESSION_INVALID", 401, false)]
    [InlineData("SEARCH_SNAPSHOT_CHANGED", 409, false)]
    [InlineData("SEARCH_SNAPSHOT_CHANGED", 503, true)]
    [InlineData("AUTHORITY_NOT_READY", 409, true)]
    public async Task OnlyExplicitlyRetryableNotReadyAndSnapshotChangedAreRetried(string code, int status, bool retryable)
    {
        var indexed = new FakeIndexed { Ids = [Guid.CreateVersion7()] };
        indexed.SearchErrors.Enqueue(new AuthorityClientException(code, status, retryable));
        var (handler, _, _, observer, _) = Make(indexed: indexed);
        await Assert.ThrowsAsync<SearchException>(() => handler.SearchAsync(Context(), Request(), Ct));
        Assert.Single(indexed.Calls);
        Assert.Empty(observer.Retries);
    }

    [Fact]
    public async Task ARetryWaitIsCancelledWithTheRequest()
    {
        var indexed = new FakeIndexed();
        for (var i = 0; i < 6; i++)
        {
            indexed.SearchErrors.Enqueue(new AuthorityClientException("SEARCH_SNAPSHOT_CHANGED", 409, true));
        }

        var limits = new FieldSearchLimits { ExecutionEnabled = true, Engine = FieldSearchLimits.EngineIndexed, RetryInitialDelayMilliseconds = 4000, RetryMaximumDelayMilliseconds = 8000, RetryMaximumTotalMilliseconds = 20000 };
        var engine = new FieldSearchIndexedEngine(limits, indexed, new FieldSearchAdmission(limits), new ReversibleCursors(), new Names(), TimeProvider.System);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.SearchAsync(Context(), Request(), cancel.Token));
        // The jittered wait is at least 2000 ms (half the 4000 ms step); 1500 ms leaves headroom for a loaded test host (it was 450 ms
        // against a 500 ms minimum and failed once under the full parallel suite).
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1500), "the backoff wait must end with the request");
        Assert.Single(indexed.Calls);
    }

    [Theory]
    [InlineData("SEARCH_SNAPSHOT_CHANGED", SearchErrorCodes.SnapshotChanged, null)]
    [InlineData("SEARCH_SNAPSHOT_NOT_FOUND", SearchErrorCodes.SnapshotChanged, null)]
    [InlineData("INTEGRITY_QUARANTINE", SearchErrorCodes.VerificationUnavailable, "RECORD_QUARANTINED")]
    [InlineData("SEARCH_INDEX_INCONSISTENT", SearchErrorCodes.VerificationUnavailable, "SEARCH_INDEX_INCONSISTENT")]
    [InlineData("AUTHORITY_NOT_READY", SearchErrorCodes.VerificationUnavailable, "AUTHORITY_GATE_CLOSED")]
    public async Task APageNeverFallsBackAndNeverReturnsAPartialPage(string code, string expected, string? reason)
    {
        var (handler, indexed, _, observer, v1) = Make();
        var token = (await handler.SearchAsync(Context(), Request(), Ct)).ContinuationToken;
        indexed.PageError = new AuthorityClientException(code, code == "INTEGRITY_QUARANTINE" ? 423 : 503, false);
        var failure = await Assert.ThrowsAsync<SearchException>(() => handler.PageAsync(Context(), new FieldSearchPageRequest(token), Ct));
        Assert.Equal((expected, reason), (failure.ErrorCode, failure.UnavailableReason));
        Assert.Empty(v1);
        Assert.Empty(observer.Fallbacks);
    }

    // ---- engine choice ----

    [Fact]
    public async Task WithoutABrowserSessionOrUnderTheOtherEnginesV1AnswersAndTheIndexIsNotCalled()
    {
        var (sessionless, indexed, _, _, v1) = Make();
        await Assert.ThrowsAsync<SearchException>(() => sessionless.SearchAsync(Context(withSession: false), Request(), Ct));
        Assert.Empty(indexed.Calls);
        Assert.Contains(nameof(IAuthorizationAuthorityClient.IssueSearchCatalogueAsync), v1);

        foreach (var engine in new[] { FieldSearchLimits.EngineVerifiedScan, FieldSearchLimits.EngineShadow })
        {
            var (handler, other, _, _, calls) = Make(engine);
            await Assert.ThrowsAsync<SearchException>(() => handler.SearchAsync(Context(), Request(), Ct));
            Assert.Empty(other.Calls);
            Assert.NotEmpty(calls);
        }
    }

    // ---- PD-6 (C-134): sessionless V1 compatibility routing, its boundaries ----

    [Fact]
    public async Task ASessionlessCallerIsRefusedBeforeAnyEngineForAForbiddenFieldAndNeverRetriedThroughTheIndex()
    {
        var (handler, indexed, _, observer, v1) = Make();
        var forged = new FieldSearchRequest(Form, [new FieldSearchCondition(Guid.CreateVersion7(), "equals", "String", "Sentinel-Value-Q7")], null, false, false, null, null, 10);
        foreach (var context in new[] { Context(withSession: false), Context() })
        {
            var refusal = await Assert.ThrowsAsync<SearchException>(() => handler.SearchAsync(context, forged, Ct));
            Assert.Equal(SearchErrorCodes.FieldNotAllowed, refusal.ErrorCode); // the Authority's capability decision, whichever engine
        }

        Assert.Empty(indexed.Calls);
        Assert.Empty(v1);
        Assert.Empty(observer.Fallbacks);
    }

    [Fact]
    public async Task ASessionlessV1RefusalIsFinalAndNeverRetriedThroughTheIndex()
    {
        var (handler, indexed, _, observer, v1) = Make(catalogueFailure: new AuthorityClientException("INTEGRITY_QUARANTINE", 423, false));
        var refusal = await Assert.ThrowsAsync<SearchException>(() => handler.SearchAsync(Context(withSession: false), Request(), Ct));
        Assert.Equal((SearchErrorCodes.VerificationUnavailable, "RECORD_QUARANTINED"), (refusal.ErrorCode, refusal.UnavailableReason)); // V1's own mapping
        Assert.Contains(nameof(IAuthorizationAuthorityClient.IssueSearchCatalogueAsync), v1);
        Assert.Empty(indexed.Calls); // the missing session never routes a refused search to another engine
        Assert.Empty(observer.Fallbacks);
    }

    [Fact]
    public async Task AnIndexedPageIsNeverDowngradedToV1WithoutASessionOrAfterTheEngineIsSwitchedOff()
    {
        var (handler, indexed, cursors, _, v1) = Make();
        var token = (await handler.SearchAsync(Context(), Request(), Ct)).ContinuationToken!;
        var calls = indexed.Calls.Count;
        Assert.Equal(SearchErrorCodes.SnapshotChanged, (await Assert.ThrowsAsync<SearchException>(() => handler.PageAsync(Context(withSession: false), new FieldSearchPageRequest(token), Ct))).ErrorCode);

        // The same cursor presented to a deployment rolled back to VerifiedScan: refused, never paged by V1 or by the index.
        var limits = new FieldSearchLimits { ExecutionEnabled = true, Engine = FieldSearchLimits.EngineVerifiedScan };
        var stub = DispatchProxy.Create<IAuthorizationAuthorityClient, PhaseC7ScopeVerificationTests.Stub<IAuthorizationAuthorityClient>>();
        var rolledBack = new FieldSearchHandler(limits, new PhaseC7FieldSearchTests.ApprovingPolicy { Request = Request() }, new TrustedScopeVerifier(stub, new NoRecords(), limits, TimeProvider.System),
            new FieldSearchAdmission(limits), stub, cursors, new Names(), TimeProvider.System, null, new FieldSearchIndexedEngine(limits, indexed, new FieldSearchAdmission(limits), cursors, new Names(), TimeProvider.System), null);
        Assert.Equal(SearchErrorCodes.SnapshotChanged, (await Assert.ThrowsAsync<SearchException>(() => rolledBack.PageAsync(Context(), new FieldSearchPageRequest(token), Ct))).ErrorCode);
        Assert.Equal(calls, indexed.Calls.Count);
        Assert.Empty(v1);
    }

    [Fact]
    public async Task TheTestingShadowEndpointStillComparesUnderTheIndexedEngine()
    {
        var (handler, indexed, _, _, v1) = Make();
        var (_, failure, comparison) = await handler.ExecuteAsync(Context(), Request(), forceShadow: true, Ct);
        Assert.NotNull(failure); // V1 answered (with the stub's failure)
        Assert.NotEmpty(v1);
        Assert.Null(comparison); // the handler was built without a comparer; the engine path was not taken
        Assert.Empty(indexed.Calls);
    }

    [Fact]
    public void IndexedIsAValidEngineAndADevelopmentAndTestingCapabilityOnly()
    {
        new FieldSearchLimits { Engine = FieldSearchLimits.EngineIndexed }.Validate();
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { Engine = "Tiered" }.Validate());
        new FieldSearchLimits { Engine = FieldSearchLimits.EngineIndexed }.ValidateEnvironment("Development");
        new FieldSearchLimits { Engine = FieldSearchLimits.EngineIndexed }.ValidateEnvironment("Testing");
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { Engine = FieldSearchLimits.EngineIndexed }.ValidateEnvironment("Production"));
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { Engine = FieldSearchLimits.EngineIndexed }.ValidateEnvironment("Staging"));
    }

    [Fact]
    public void TheEngineReachesOnlyTheAuthorityIndexAndTheDisplayPort()
    {
        var parameters = typeof(FieldSearchIndexedEngine).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        Assert.Equal([typeof(FieldSearchLimits), typeof(IIndexedSearchAuthority), typeof(FieldSearchAdmission), typeof(IOpaqueCursorProtector), typeof(IRecordDisplayDirectory), typeof(TimeProvider),
            typeof(FieldSearchRetryPolicy)], parameters); // C-150 R56-A: the retry policy depends only on limits, the clock and the observer
    }
}
