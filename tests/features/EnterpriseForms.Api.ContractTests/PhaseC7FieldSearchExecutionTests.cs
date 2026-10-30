using System.Collections.Concurrent;
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
/// Phase C7 M3 — the verified executor with the Authority stubbed: predicate semantics on verified values, readability,
/// verified metadata filters and sort, paging from the cursor with Authority re-validation, at most one automatic retry,
/// admission, mandatory execution evidence and no partial results. The adversarial proof against real infrastructure is
/// the Stage B integration suite (FieldSearchExecutionTests).
/// </summary>
public sealed class PhaseC7FieldSearchExecutionTests
{
    private static readonly RequestContext Context = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Name = Guid.CreateVersion7();
    private static readonly Guid Count = Guid.CreateVersion7();
    private static readonly Guid Day = Guid.CreateVersion7();
    private static readonly Guid Status = Guid.CreateVersion7();
    private static readonly Guid Flag = Guid.CreateVersion7();
    private static readonly Guid Open = Guid.CreateVersion7();
    private static readonly Guid Closed = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rec
    {
        public Guid Id { get; } = Guid.CreateVersion7();
        public string RecordStatus { get; set; } = "Submitted";
        public Guid? Parent { get; set; }
        public Guid Form { get; set; } = PhaseC7FieldSearchExecutionTests.Form;
        public long Revision { get; set; } = 1;
        public bool Complete { get; set; } = true;
        public HashSet<Guid> Unreadable { get; } = [];
        public List<AcceptedFieldValue> Values { get; } = [];
        public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow.AddDays(-1);
        public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;

        public Rec Text(Guid field, string value, Guid? group = null) => Add(new(field, Guid.CreateVersion7(), group, "String", Text: value));

        public Rec Int(Guid field, long value) => Add(new(field, Guid.CreateVersion7(), null, "Integer", IntegerValue: value));

        public Rec Date(Guid field, DateOnly value) => Add(new(field, Guid.CreateVersion7(), null, "Date", Date: value));

        public Rec Option(Guid field, Guid option) => Add(new(field, Guid.CreateVersion7(), null, "Option", Identifier: option));

        public Rec Bool(Guid field, bool value) => Add(new(field, Guid.CreateVersion7(), null, "Boolean", Boolean: value));

        private Rec Add(AcceptedFieldValue value)
        {
            Values.Add(value);
            return this;
        }
    }

    private sealed class World
    {
        public List<Rec> Records { get; } = [];
        public Guid Snapshot { get; private set; } = Guid.CreateVersion7();
        public long Revision { get; set; } = 7;
        public Queue<Exception> CatalogueFailures { get; } = new();
        public HashSet<Guid> FailVerification { get; } = [];
        public bool SnapshotValid { get; set; } = true;
        public int Catalogues { get; private set; }
        public List<SearchExecutionEvidence> Evidence { get; } = [];
        public bool EvidenceFails { get; set; }
        public FieldSearchLimits Limits { get; } = new() { ExecutionEnabled = true };

        public List<AuthoritySearchableField> Searchable { get; } =
        [
            new(Name, "Surname", "String", ["equals", "startsWith", "contains"], [], 1),
            new(Count, "Exhibits", "Integer", ["equals", "greaterThan", "between"], [], 1),
            new(Day, "Hearing", "Date", ["equals", "before", "after", "between"], [], 1),
            new(Status, "Case status", "Option", ["is", "includes"], [new(Open, "Open"), new(Closed, "Closed")], 1),
            new(Flag, "Urgent", "Boolean", ["isTrue", "isFalse"], [], 1),
        ];

        public FieldSearchHandler Handler(FieldSearchAdmission? admission = null)
        {
            var authority = DispatchProxy.Create<IAuthorizationAuthorityClient, PhaseC7ScopeVerificationTests.Stub<IAuthorizationAuthorityClient>>();
            ((PhaseC7ScopeVerificationTests.Stub<IAuthorizationAuthorityClient>)(object)authority).Handler = (method, args) => method.Name switch
            {
                nameof(IAuthorizationAuthorityClient.IssueSearchCatalogueAsync) => Catalogue(),
                nameof(IAuthorizationAuthorityClient.VerifyRecordProjectionsAsync) => Verify((VerifyRecordProjectionsRequest)args[1]!),
                nameof(IAuthorizationAuthorityClient.ValidateSearchCatalogueAsync) => Task.FromResult(new CatalogueSnapshotValidation((Guid)args[1]!, SnapshotValid, SnapshotValid ? null : "AUTHORIZATION_CHANGED", Revision, 1, Guid.Empty)),
                nameof(IAuthorizationAuthorityClient.CheckPermissionsBatchAsync) => Task.FromResult<IReadOnlyList<PermissionDecision>>(((IReadOnlyList<PermissionCheckRequest>)args[1]!).Select(_ => new PermissionDecision(true, null, Revision, Revision, null, Guid.Empty)).ToList()),
                _ => throw new NotSupportedException(method.Name),
            };
            return new FieldSearchHandler(Limits, new Policy(this), new TrustedScopeVerifier(authority, new RecordsReader(this), Limits, TimeProvider.System), admission ?? new FieldSearchAdmission(Limits),
                authority, new Cursors(), new Directory(), TimeProvider.System);
        }

        private Task<SearchCatalogue> Catalogue()
        {
            Catalogues++;
            if (CatalogueFailures.TryDequeue(out var failure))
            {
                return Task.FromException<SearchCatalogue>(failure);
            }

            Snapshot = Guid.CreateVersion7();
            var entries = Records.Select(r => new CatalogueEntry(r.Id, r.Revision, r.Revision > 0 ? r.Id : null, false, false, r.Created, r.Revision > 0 ? r.Modified : null)).ToList();
            return Task.FromResult(new SearchCatalogue(Snapshot, Context.OrganizationId, Form, Revision, Revision, 1, entries, DateTimeOffset.UtcNow.AddMinutes(5), Guid.Empty));
        }

        private Task<VerifyRecordProjectionsResponse> Verify(VerifyRecordProjectionsRequest request) => Task.FromResult(new VerifyRecordProjectionsResponse(request.Records.Select(item =>
        {
            var rec = Records.Single(r => r.Id == item.FormRecordId);
            if (FailVerification.Contains(rec.Id))
            {
                return new BatchVerificationResult(rec.Id, false, "ROOT_MISMATCH", rec.Revision, rec.Id, false, []);
            }

            var readable = new List<VerifiedProjectionItem> { new(ManifestItemKind.RecordHeader, rec.Id) };
            readable.AddRange(rec.Values.Where(v => !rec.Unreadable.Contains(v.FieldDefinitionId)).Select(v => new VerifiedProjectionItem(ManifestItemKind.FieldValue, v.ItemId)));
            return new BatchVerificationResult(rec.Id, true, null, rec.Revision, rec.Id, rec.Complete, readable);
        }).ToList(), Revision, Guid.Empty));

        private sealed class RecordsReader(World world) : IAcceptedRecordItemsReader
        {
            public Task<IReadOnlyList<StagedContentItem>> ReadObservedItemsAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<AcceptedRecordContent> ReadAcceptedContentAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken)
            {
                var rec = world.Records.Single(r => r.Id == formRecordId);
                return Task.FromResult(new AcceptedRecordContent([new StagedContentItem(ManifestItemKind.RecordHeader, rec.Id, Convert.ToBase64String(new byte[64]), Convert.ToBase64String(new byte[32]))],
                    new AcceptedRecordHeaderFacts(rec.Form, rec.RecordStatus, Guid.Empty, rec.Parent), rec.Values));
            }
        }

        private sealed class Policy(World world) : IFieldSearchPolicyAuthority
        {
            public Task<AuthorityFieldSearchCapabilities> GetFieldSearchCapabilitiesAsync(DelegatedUserContext user, FieldSearchCapabilitiesQuery query, CancellationToken cancellationToken) =>
                Task.FromResult(new AuthorityFieldSearchCapabilities(query.FormDefinitionId, true, 1, world.Revision, world.Searchable, null, Guid.Empty));

            public Task<AuthorityFieldSearchAdministration> GetFieldSearchAdministrationAsync(DelegatedUserContext user, FieldSearchAdministrationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task RecordSearchExecutionAsync(DelegatedUserContext user, Guid searchCatalogueSnapshotId, SearchExecutionEvidence evidence, CancellationToken cancellationToken)
            {
                if (world.EvidenceFails)
                {
                    throw new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true);
                }

                world.Evidence.Add(evidence);
                return Task.CompletedTask;
            }
        }
    }

    /// <summary>A transparent in-memory protector: tokens are opaque handles; tampered or unknown handles do not unprotect.</summary>
    private sealed class Cursors : IOpaqueCursorProtector
    {
        private readonly ConcurrentDictionary<string, string> issued = new();

        public string Protect(string payload, TimeSpan lifetime)
        {
            var handle = Guid.NewGuid().ToString("N");
            issued[handle] = payload;
            return handle;
        }

        public string? Unprotect(string token) => issued.TryGetValue(token, out var payload) ? payload : null;
    }

    private sealed class Directory : IRecordDisplayDirectory
    {
        public Task<RecordDisplayNames> GetNamesAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(new RecordDisplayNames(new Dictionary<Guid, string> { [Form] = "Case" }, new Dictionary<Guid, string>()));

        public string ReferenceFor(Guid formRecordId) => "REC-" + formRecordId.ToString("N")[^8..];
    }

    private static FieldSearchRequest Request(params FieldSearchCondition[] conditions) => new(Form, conditions, PageSize: 10);

    private static async Task<HashSet<Guid>> MatchesAsync(World world, params FieldSearchCondition[] conditions) =>
        (await world.Handler().SearchAsync(Context, Request(conditions), Ct)).Items.Select(i => i.FormRecordId).ToHashSet();

    [Fact]
    public async Task TextNumberDateOptionAndBooleanPredicatesMatchOnlyVerifiedReadableTopLevelValues()
    {
        var world = new World();
        var a = new Rec().Text(Name, "Jordan Smith").Int(Count, 4).Date(Day, new DateOnly(2026, 3, 1)).Option(Status, Open).Bool(Flag, true);
        var b = new Rec().Text(Name, "  jordan smith ").Int(Count, 9).Date(Day, new DateOnly(2026, 9, 1)).Option(Status, Closed).Bool(Flag, false);
        var c = new Rec().Text(Name, "Taylor", group: Guid.CreateVersion7()); // a repeating-group value is never searched
        var d = new Rec(); // no values at all: matches nothing
        world.Records.AddRange([a, b, c, d]);

        Assert.Equal([a.Id, b.Id], await MatchesAsync(world, new FieldSearchCondition(Name, "equals", "String", "JORDAN SMITH")));
        Assert.Equal([a.Id, b.Id], await MatchesAsync(world, new FieldSearchCondition(Name, "startsWith", "String", "jor")));
        Assert.Empty(await MatchesAsync(world, new FieldSearchCondition(Name, "contains", "String", "Taylor")));
        Assert.Equal([b.Id], await MatchesAsync(world, new FieldSearchCondition(Count, "greaterThan", "Integer", "4")));
        Assert.Equal([a.Id, b.Id], await MatchesAsync(world, new FieldSearchCondition(Count, "between", "Integer", null, "4", "9")));
        Assert.Equal([a.Id], await MatchesAsync(world, new FieldSearchCondition(Day, "before", "Date", "2026-03-02")));
        Assert.Equal([b.Id], await MatchesAsync(world, new FieldSearchCondition(Day, "equals", "Date", "2026-09-01")));
        Assert.Equal([a.Id], await MatchesAsync(world, new FieldSearchCondition(Status, "is", "Option", Open.ToString("D"))));
        Assert.Equal([b.Id], await MatchesAsync(world, new FieldSearchCondition(Flag, "isFalse", "Boolean")));
        // AND across conditions.
        Assert.Equal([b.Id], await MatchesAsync(world, new FieldSearchCondition(Name, "contains", "String", "smith"), new FieldSearchCondition(Count, "greaterThan", "Integer", "5"), new FieldSearchCondition(Flag, "isFalse", "Boolean")));
    }

    [Fact]
    public async Task ARecordWhosePredicateValueTheCallerCannotReadIsNotEvaluatedAndNothingAboutItLeaks()
    {
        var world = new World();
        var visible = new Rec().Text(Name, "Jordan");
        var hidden = new Rec { Complete = false }.Text(Name, "Jordan");
        hidden.Unreadable.Add(Name);
        world.Records.AddRange([visible, hidden]);
        var result = await world.Handler().SearchAsync(Context, Request(new FieldSearchCondition(Name, "equals", "String", "Jordan")), Ct);
        Assert.Equal([visible.Id], result.Items.Select(i => i.FormRecordId));
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, world.Evidence.Single().EvaluatedRecordCount);
        Assert.DoesNotContain("Jordan", result.ScopeStatement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusArchivedAndChildFiltersAndTheSortUseVerifiedFacts()
    {
        var world = new World();
        var older = new Rec { Modified = DateTimeOffset.UtcNow.AddHours(-2) }.Text(Name, "Lee");
        var newer = new Rec { Modified = DateTimeOffset.UtcNow.AddHours(-1) }.Text(Name, "Lee");
        var archived = new Rec { RecordStatus = "Archived" }.Text(Name, "Lee");
        var draft = new Rec { RecordStatus = "Draft" }.Text(Name, "Lee");
        var child = new Rec { Parent = Guid.CreateVersion7() }.Text(Name, "Lee");
        world.Records.AddRange([older, newer, archived, draft, child]);
        var condition = new FieldSearchCondition(Name, "equals", "String", "Lee");
        var handler = world.Handler();

        var defaults = await handler.SearchAsync(Context, Request(condition), Ct);
        Assert.Equal([draft.Id, newer.Id, older.Id], defaults.Items.Select(i => i.FormRecordId).ToList()); // modifiedOn desc
        Assert.DoesNotContain(defaults.Items, i => i.FormRecordId == archived.Id || i.FormRecordId == child.Id);
        Assert.All(defaults.Items, i => Assert.True(i.ProjectionVerified));

        Assert.Equal([draft.Id], (await handler.SearchAsync(Context, new FieldSearchRequest(Form, [condition], RecordStatus: "Draft"), Ct)).Items.Select(i => i.FormRecordId));
        Assert.Contains((await handler.SearchAsync(Context, new FieldSearchRequest(Form, [condition], IncludeArchived: true), Ct)).Items, i => i.FormRecordId == archived.Id);
        Assert.Contains((await handler.SearchAsync(Context, new FieldSearchRequest(Form, [condition], IncludeChildren: true), Ct)).Items, i => i.FormRecordId == child.Id);
        var ascending = await handler.SearchAsync(Context, new FieldSearchRequest(Form, [condition], SortBy: "modifiedOn", SortDirection: "asc"), Ct);
        Assert.Equal(older.Id, ascending.Items[0].FormRecordId);
    }

    [Fact]
    public async Task PagesComeFromTheVerifiedResultAndDieWithTheSnapshot()
    {
        var world = new World();
        for (var i = 0; i < 23; i++)
        {
            world.Records.Add(new Rec { Modified = DateTimeOffset.UtcNow.AddMinutes(-i) }.Int(Count, i));
        }

        var handler = world.Handler();
        var first = await handler.SearchAsync(Context, Request(new FieldSearchCondition(Count, "greaterThan", "Integer", "-1")), Ct);
        Assert.Equal(23, first.TotalCount);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal("Complete", first.Completeness);
        Assert.Null(first.PreviousToken);
        var second = await handler.PageAsync(Context, new FieldSearchPageRequest(first.ContinuationToken), Ct);
        Assert.Equal(11, second.PageStart);
        Assert.Empty(first.Items.Select(i => i.FormRecordId).Intersect(second.Items.Select(i => i.FormRecordId)));
        Assert.Equal(1, world.Catalogues); // paging never re-runs the search

        // Another user cannot use the cursor; a revocation (snapshot invalid) ends paging with the typed refusal.
        var other = Context with { LocalUserId = Guid.CreateVersion7() };
        Assert.Equal(SearchErrorCodes.SnapshotChanged, (await Assert.ThrowsAsync<SearchException>(() => handler.PageAsync(other, new FieldSearchPageRequest(second.ContinuationToken), Ct))).ErrorCode);
        Assert.Equal(SearchErrorCodes.SnapshotChanged, (await Assert.ThrowsAsync<SearchException>(() => handler.PageAsync(Context, new FieldSearchPageRequest("0.forged"), Ct))).ErrorCode);
        world.SnapshotValid = false;
        Assert.Equal(SearchErrorCodes.SnapshotChanged, (await Assert.ThrowsAsync<SearchException>(() => handler.PageAsync(Context, new FieldSearchPageRequest(second.ContinuationToken), Ct))).ErrorCode);
    }

    [Fact]
    public async Task OneFailedVerificationFailsTheWholeSearchWithNoPartialResultAndEvidence()
    {
        var world = new World();
        var good = new Rec().Text(Name, "Jordan");
        var bad = new Rec().Text(Name, "Jordan");
        world.Records.AddRange([good, bad]);
        world.FailVerification.Add(bad.Id);
        var refusal = await Assert.ThrowsAsync<SearchException>(() => world.Handler().SearchAsync(Context, Request(new FieldSearchCondition(Name, "equals", "String", "Jordan")), Ct));
        Assert.Equal(SearchErrorCodes.VerificationUnavailable, refusal.ErrorCode);
        Assert.False(refusal.Retryable);
        Assert.Equal(SearchErrorCodes.VerificationUnavailable, world.Evidence.Single().Outcome);
        Assert.Null(world.Evidence.Single().MatchedRecordCount);
    }

    [Fact]
    public async Task AChangedSnapshotIsRetriedAtMostOnceAndAPendingCreationNeverBlocks()
    {
        var world = new World();
        world.Records.Add(new Rec().Text(Name, "Jordan"));
        world.Records.Add(new Rec { Revision = 0 }); // registered, never accepted
        world.CatalogueFailures.Enqueue(new AuthorityClientException("AUTHORITY_NOT_READY", 503, true));
        var result = await world.Handler().SearchAsync(Context, Request(new FieldSearchCondition(Name, "equals", "String", "Jordan")), Ct);
        Assert.Equal(1, result.TotalCount);
        Assert.Contains("1 pending creation", result.ScopeStatement, StringComparison.Ordinal);
        Assert.Equal(2, world.Catalogues);

        world.CatalogueFailures.Enqueue(new AuthorityClientException("AUTHORITY_NOT_READY", 503, true));
        world.CatalogueFailures.Enqueue(new AuthorityClientException("AUTHORITY_NOT_READY", 503, true));
        var refusal = await Assert.ThrowsAsync<SearchException>(() => world.Handler().SearchAsync(Context, Request(new FieldSearchCondition(Name, "equals", "String", "Jordan")), Ct));
        Assert.Equal(SearchErrorCodes.SnapshotChanged, refusal.ErrorCode);
        Assert.True(refusal.Retryable);
        Assert.Equal(4, world.Catalogues);
    }

    [Fact]
    public async Task VerificationLimitsAndNonTransientFailuresAreNeverRetried()
    {
        var world = new World();
        world.Records.Add(new Rec().Text(Name, "Jordan"));
        world.CatalogueFailures.Enqueue(new AuthorityClientException("LIMIT_EXCEEDED", 422, false));
        var refusal = await Assert.ThrowsAsync<SearchException>(() => world.Handler().SearchAsync(Context, Request(new FieldSearchCondition(Name, "equals", "String", "Jordan")), Ct));
        Assert.Equal(SearchErrorCodes.VerificationLimit, refusal.ErrorCode);
        Assert.Equal("records", refusal.LimitKind);
        Assert.Equal(1, world.Catalogues);
    }

    [Fact]
    public async Task WithoutExecutionEvidenceNoResultLeavesTheServer()
    {
        var world = new World { EvidenceFails = true };
        world.Records.Add(new Rec().Text(Name, "Jordan"));
        var refusal = await Assert.ThrowsAsync<SearchException>(() => world.Handler().SearchAsync(Context, Request(new FieldSearchCondition(Name, "equals", "String", "Jordan")), Ct));
        Assert.Equal("EXECUTION_EVIDENCE_UNAVAILABLE", refusal.UnavailableReason);
        Assert.True(refusal.Retryable);
    }

    [Fact]
    public async Task EvidenceCarriesFieldsOperatorsAndCountsButNeverValues()
    {
        var world = new World();
        world.Records.Add(new Rec().Text(Name, "Zq7Sentinel"));
        await world.Handler().SearchAsync(Context, Request(new FieldSearchCondition(Name, "equals", "String", "Zq7Sentinel")), Ct);
        var evidence = world.Evidence.Single();
        Assert.Equal(("COMPLETED", 1, 1), (evidence.Outcome, evidence.EvaluatedRecordCount, evidence.MatchedRecordCount));
        Assert.Equal([Name], evidence.FieldDefinitionIds);
        Assert.Equal(["equals"], evidence.Operators);
        Assert.DoesNotContain("Zq7Sentinel", System.Text.Json.JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdmissionRefusesBeyondTheConcurrencyBoundsAsBusyAndRetryable()
    {
        var world = new World();
        world.Limits.MaximumConcurrentSearches = 1;
        world.Limits.AdmissionWaitMilliseconds = 0;
        var admission = new FieldSearchAdmission(world.Limits);
        using (await admission.AdmitAsync(Guid.CreateVersion7(), Ct))
        {
            var busy = await Assert.ThrowsAsync<SearchException>(() => world.Handler(admission).SearchAsync(Context, Request(new FieldSearchCondition(Name, "equals", "String", "x1z")), Ct));
            Assert.Equal((SearchErrorCodes.VerificationUnavailable, FieldSearchAdmission.CapacityExceeded, true), (busy.ErrorCode, busy.UnavailableReason, busy.Retryable));
            Assert.Equal(0, world.Catalogues);
        }

        var perUser = new FieldSearchAdmission(new FieldSearchLimits { MaximumConcurrentSearches = 4, MaximumConcurrentSearchesPerUser = 1, AdmissionWaitMilliseconds = 0 });
        using (await perUser.AdmitAsync(Context.LocalUserId, Ct))
        {
            Assert.Equal(FieldSearchAdmission.CapacityExceeded, (await Assert.ThrowsAsync<SearchException>(() => perUser.AdmitAsync(Context.LocalUserId, Ct))).UnavailableReason);
        }

        using (await perUser.AdmitAsync(Context.LocalUserId, Ct))
        {
            // released again after use
        }
    }

    [Fact]
    public async Task AVerifiedRecordWhoseSignedHeaderNamesAnotherFormFailsTheSearch()
    {
        var world = new World();
        world.Records.Add(new Rec { Form = Guid.CreateVersion7() }.Text(Name, "Jordan"));
        var refusal = await Assert.ThrowsAsync<SearchException>(() => world.Handler().SearchAsync(Context, Request(new FieldSearchCondition(Name, "equals", "String", "Jordan")), Ct));
        Assert.Equal("RECORD_VERIFICATION_FAILED", refusal.UnavailableReason);
    }

    [Fact]
    public void TheExecutionGateIsADevelopmentCapabilityAndTheApprovedCeilingsCannotBeRaised()
    {
        foreach (var environment in new[] { "Production", "Staging" })
        {
            Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { ExecutionEnabled = true }.ValidateEnvironment(environment));
        }

        new FieldSearchLimits { ExecutionEnabled = true }.ValidateEnvironment("Testing");
        new FieldSearchLimits().ValidateEnvironment("Production");
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { MaximumExecutionSeconds = 16 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { MaximumEligibleRecords = 501 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { MaximumVerifiedBytes = FieldSearchLimits.ApprovedMaximumVerifiedBytes + 1 }.Validate());
    }
}
