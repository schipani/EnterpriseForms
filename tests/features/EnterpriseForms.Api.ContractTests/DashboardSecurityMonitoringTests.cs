using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Paging;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Dashboard;
using EnterpriseForms.Application.Features.FormDesigner;
using EnterpriseForms.Application.Features.SecurityOperations.Incidents;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.SecurityOperations;
using EnterpriseForms.Infrastructure.Features.SecurityOperations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Dashboard redesign and security monitoring: incident deduplication and classification rules, the recorder's
/// never-throw guarantee, Security Administrator-only incident access with record identifiers withheld from viewers who
/// cannot discover the record, opaque 15-row paging, and calendar buckets.
/// </summary>
public sealed class DashboardSecurityMonitoringTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Me = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Me, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);

    private static SecurityIncidentObservation Observation(string type, Guid? record = null, string code = "ROW_SIGNATURE_MISMATCH") =>
        new(Org, Me, Guid.CreateVersion7(), type, record, SecurityIncidentDetectionSources.RecordRead, code);

    private static SecurityIncident Incident(string status, string type = SecurityIncidentTypes.ConfirmedIntegrityMismatch) => new()
    {
        SecurityIncidentId = Guid.CreateVersion7(), TenantId = Org, IncidentTypeName = type, IncidentStatusName = status, FirstDetectedOn = Now.AddHours(-1), LastDetectedOn = Now.AddHours(-1),
    };

    // ---- detection rules ----

    [Fact]
    public void AFirstDetectionOpensAnIncident()
    {
        var decision = SecurityIncidentDeduplication.Decide(null, Observation(SecurityIncidentTypes.ConfirmedIntegrityMismatch, Guid.CreateVersion7()), null, Now);
        Assert.Equal(SecurityIncidentDecisionKind.CreateIncident, decision.Kind);
        Assert.True(decision.AppendHistoryRow);
    }

    [Theory]
    [InlineData(SecurityIncidentStatuses.Open)]
    [InlineData(SecurityIncidentStatuses.Investigating)]
    public void AnActiveIncidentAbsorbsRepeatedDetections(string status)
    {
        var decision = SecurityIncidentDeduplication.Decide(Incident(status), Observation(SecurityIncidentTypes.IntegrityQuarantine, Guid.CreateVersion7()), Now.AddSeconds(-5), Now);
        Assert.Equal(SecurityIncidentDecisionKind.CountOnExisting, decision.Kind);
    }

    [Fact]
    public void RepeatsWithinAMinuteAreCountedButShareOneHistoryRow()
    {
        var incident = Incident(SecurityIncidentStatuses.Open, SecurityIncidentTypes.IntegrityQuarantine);
        Assert.False(SecurityIncidentDeduplication.Decide(incident, Observation(SecurityIncidentTypes.IntegrityQuarantine), Now.AddSeconds(-10), Now).AppendHistoryRow);
        Assert.True(SecurityIncidentDeduplication.Decide(incident, Observation(SecurityIncidentTypes.IntegrityQuarantine), Now.AddMinutes(-2), Now).AppendHistoryRow);
    }

    [Theory]
    [InlineData(SecurityIncidentStatuses.Resolved)]
    [InlineData(SecurityIncidentStatuses.FalsePositive)]
    public void AQuarantineRefusalAfterClosureIsCountedOnTheClosedIncidentWithoutReopeningIt(string status)
    {
        var closed = Incident(status);
        var decision = SecurityIncidentDeduplication.Decide(closed, Observation(SecurityIncidentTypes.IntegrityQuarantine), Now.AddHours(-1), Now);
        Assert.Equal(SecurityIncidentDecisionKind.CountOnExisting, decision.Kind);
        Assert.Equal(status, closed.IncidentStatusName);
    }

    [Fact]
    public void AFreshConfirmedMismatchAfterClosureOpensANewIncident()
    {
        var decision = SecurityIncidentDeduplication.Decide(Incident(SecurityIncidentStatuses.Resolved), Observation(SecurityIncidentTypes.ConfirmedIntegrityMismatch), Now.AddHours(-1), Now);
        Assert.Equal(SecurityIncidentDecisionKind.CreateIncident, decision.Kind);
    }

    [Fact]
    public void IntegrityObservationsOfOneRecordShareAKeyAndOutagesShareOnePerCode()
    {
        var record = Guid.CreateVersion7();
        Assert.Equal(Observation(SecurityIncidentTypes.ConfirmedIntegrityMismatch, record).DeduplicationKey, Observation(SecurityIncidentTypes.IntegrityQuarantine, record).DeduplicationKey);
        Assert.NotEqual(Observation(SecurityIncidentTypes.ConfirmedIntegrityMismatch, record).DeduplicationKey, Observation(SecurityIncidentTypes.ConfirmedIntegrityMismatch, Guid.CreateVersion7()).DeduplicationKey);
        Assert.Equal("service:AUTHORITY_UNAVAILABLE", Observation(SecurityIncidentTypes.VerificationServiceUnavailable, record, "AUTHORITY_UNAVAILABLE").DeduplicationKey);
    }

    [Theory]
    [InlineData("ROW_SIGNATURE_MISMATCH", SecurityIncidentTypes.ConfirmedIntegrityMismatch)]
    [InlineData("MANIFEST_LEAF_MISSING", SecurityIncidentTypes.ConfirmedIntegrityMismatch)]
    [InlineData("NO_COMMITTED_CONTENT", null)]
    [InlineData("HEAD_CHANGED", null)]
    public void VerificationFailuresAreClassifiedWithoutGuessingIntent(string code, string? expected) =>
        Assert.Equal(expected, SecurityIncidentClassifier.FromVerificationFailure(code));

    [Theory]
    [InlineData("INTEGRITY_QUARANTINE", 423, SecurityIncidentTypes.IntegrityQuarantine)]
    [InlineData("AUTHORITY_UNAVAILABLE", 503, SecurityIncidentTypes.VerificationServiceUnavailable)]
    [InlineData("KEY_UNAVAILABLE", 503, SecurityIncidentTypes.VerificationServiceUnavailable)]
    [InlineData("AUTHORITY_NOT_READY", 503, null)]
    [InlineData("NOT_FOUND", 404, null)]
    [InlineData("FORBIDDEN", 403, null)]
    public void AuthorityErrorsAreClassifiedAndTransientOnesAreNotIncidents(string code, int status, string? expected) =>
        Assert.Equal(expected, SecurityIncidentClassifier.FromAuthorityError(new AuthorityClientException(code, status, status >= 500)));

    [Fact]
    public void ErrorCodesAreSanitizedBeforeStorage()
    {
        Assert.Equal("ROW_MISMATCHscript", SecurityIncidentClassifier.SanitizeCode("ROW_MISMATCH<script>"));
        Assert.Equal(80, SecurityIncidentClassifier.SanitizeCode(new string('A', 200)).Length);
    }

    [Fact]
    public async Task TheRecorderNeverThrowsEvenWhenTheStoreIsDown()
    {
        var store = Stubs.Create<IBusinessStore>((_, _) => Task.FromException<IBusinessScope>(new InvalidOperationException("database unavailable")));
        var recorder = new SecurityIncidentRecorder(store, TimeProvider.System, NullLogger<SecurityIncidentRecorder>.Instance);
        await recorder.RecordAsync(Observation(SecurityIncidentTypes.ConfirmedIntegrityMismatch, Guid.CreateVersion7()), CancellationToken.None);
    }

    [Fact]
    public async Task TheRecorderCreatesOneIncidentWithHistoryAndAnAuditEventForAFirstDetection()
    {
        var added = new List<object>();
        var committed = false;
        var scope = Stubs.Create<IBusinessScope>((method, args) => method.Name switch
        {
            nameof(IBusinessScope.LockLatestSecurityIncidentAsync) => Task.FromResult<SecurityIncident?>(null),
            nameof(IBusinessScope.AddSecurityIncident) or nameof(IBusinessScope.AddSecurityIncidentOccurrence) or nameof(IBusinessScope.AddAuditEvent) => Add(added, args[0]!),
            nameof(IBusinessScope.CommitAsync) => Commit(ref committed),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });
        var store = Stubs.Create<IBusinessStore>((_, _) => Task.FromResult(scope));
        var record = Guid.CreateVersion7();
        await new SecurityIncidentRecorder(store, TimeProvider.System, NullLogger<SecurityIncidentRecorder>.Instance).RecordAsync(Observation(SecurityIncidentTypes.ConfirmedIntegrityMismatch, record), CancellationToken.None);

        Assert.True(committed);
        var incident = Assert.Single(added.OfType<SecurityIncident>());
        Assert.Equal(SecurityIncidentSeverities.Critical, incident.IncidentSeverityName);
        Assert.Equal(SecurityIncidentStatuses.Open, incident.IncidentStatusName);
        Assert.Equal(record, incident.FormRecordId);
        Assert.Single(added.OfType<SecurityIncidentOccurrence>());
        var audit = Assert.Single(added.OfType<Domain.Features.Audit.AuditEvent>());
        Assert.Equal("SECURITY_INCIDENT_DETECTED", audit.EventAction);
        Assert.DoesNotContain("value", audit.EventMetadataJson, StringComparison.OrdinalIgnoreCase);
    }

    private static object? Add(List<object> added, object item)
    {
        added.Add(item);
        return null;
    }

    private static Task Commit(ref bool committed)
    {
        committed = true;
        return Task.CompletedTask;
    }

    // ---- incident access ----

    [Fact]
    public async Task OnlySecurityAdministratorsCanListIncidentsAndOthersGetNoCounts()
    {
        var authority = new FakeAuthority { AdministerSecurity = false };
        var handlers = new SecurityIncidentHandlers(new FakeIncidentStore([]).Proxy, authority.Proxy, Roster(), new TestProtector(), TimeProvider.System);
        var list = await Assert.ThrowsAsync<BusinessException>(() => handlers.ListAsync(Context, SecurityIncidentHandlers.Normalize(null, null, null, null, null, null, null), null, CancellationToken.None));
        Assert.Equal(403, list.HttpStatusCode);
        var summary = await Assert.ThrowsAsync<BusinessException>(() => handlers.SummaryAsync(Context, 30, "America/Toronto", CancellationToken.None));
        Assert.Equal(403, summary.HttpStatusCode);
        var detail = await Assert.ThrowsAsync<BusinessException>(() => handlers.GetAsync(Context, Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal(403, detail.HttpStatusCode);
        Assert.All(authority.Checks, c => Assert.Equal(ProtectedAction.AdministerSecurity, c));
    }

    [Fact]
    public async Task RecordIdentifiersAreWithheldUnlessTheViewerCanDiscoverTheRecord()
    {
        var visibleRecord = Guid.CreateVersion7();
        var hiddenRecord = Guid.CreateVersion7();
        var visible = Incident(SecurityIncidentStatuses.Open);
        visible.FormRecordId = visibleRecord;
        var hidden = Incident(SecurityIncidentStatuses.Open);
        hidden.FormRecordId = hiddenRecord;
        hidden.LastDetectedOn = Now;
        var authority = new FakeAuthority { Discover = id => id == visibleRecord };
        var handlers = new SecurityIncidentHandlers(new FakeIncidentStore([visible, hidden]).Proxy, authority.Proxy, Roster(), new TestProtector(), TimeProvider.System);

        var page = await handlers.ListAsync(Context, SecurityIncidentHandlers.Normalize(null, null, null, null, null, null, null), null, CancellationToken.None);
        var shown = page.Items.Single(i => i.SecurityIncidentId == visible.SecurityIncidentId);
        Assert.Equal(visibleRecord, shown.FormRecordId);
        Assert.Equal("REC-" + visibleRecord.ToString("N")[^8..].ToUpperInvariant(), shown.RecordReference);
        var withheld = page.Items.Single(i => i.SecurityIncidentId == hidden.SecurityIncidentId);
        Assert.True(withheld.RecordWithheld);
        Assert.Null(withheld.FormRecordId);
        Assert.Null(withheld.RecordReference);

        // Searching for the hidden record's id finds nothing: the search cannot be used to learn which records have incidents.
        var search = await handlers.ListAsync(Context, SecurityIncidentHandlers.Normalize(hiddenRecord.ToString("D"), null, null, null, null, null, null), null, CancellationToken.None);
        Assert.Equal(0, search.TotalCount);
        var found = await handlers.ListAsync(Context, SecurityIncidentHandlers.Normalize(visibleRecord.ToString("D"), null, null, null, null, null, null), null, CancellationToken.None);
        Assert.Equal(visible.SecurityIncidentId, Assert.Single(found.Items).SecurityIncidentId);
    }

    [Fact]
    public async Task IncidentPagesDefaultToFifteenRowsWithDeterministicCursors()
    {
        var all = Enumerable.Range(0, 32).Select(i =>
        {
            var incident = Incident(SecurityIncidentStatuses.Open);
            incident.LastDetectedOn = Now.AddMinutes(-i);
            return incident;
        }).ToList();
        var store = new FakeIncidentStore(all);
        var handlers = new SecurityIncidentHandlers(store.Proxy, new FakeAuthority().Proxy, Roster(), new TestProtector(), TimeProvider.System);
        var query = SecurityIncidentHandlers.Normalize(null, null, null, null, null, null, null);

        var first = await handlers.ListAsync(Context, query, null, CancellationToken.None);
        Assert.Equal(15, first.Items.Count);
        Assert.Equal(32, first.TotalCount);
        Assert.Equal(1, first.PageStart);
        Assert.Null(first.PreviousToken);
        var second = await handlers.ListAsync(Context, query, first.NextToken, CancellationToken.None);
        Assert.Equal(16, second.PageStart);
        var third = await handlers.ListAsync(Context, query, second.NextToken, CancellationToken.None);
        Assert.Equal(2, third.Items.Count);
        Assert.Null(third.NextToken);
        var seen = first.Items.Concat(second.Items).Concat(third.Items).Select(i => i.SecurityIncidentId).ToList();
        Assert.Equal(32, seen.Distinct().Count());

        // A cursor is bound to its filters and its caller.
        var filtered = SecurityIncidentHandlers.Normalize(null, null, SecurityIncidentSeverities.Critical, null, null, null, null);
        Assert.Equal("CURSOR_SCOPE_MISMATCH", (await Assert.ThrowsAsync<BusinessException>(() => handlers.ListAsync(Context, filtered, first.NextToken, CancellationToken.None))).ErrorCode);
        var someoneElse = Context with { LocalUserId = Guid.CreateVersion7() };
        Assert.Equal("CURSOR_INVALID", (await Assert.ThrowsAsync<BusinessException>(() => handlers.ListAsync(someoneElse, query, first.NextToken, CancellationToken.None))).ErrorCode);
    }

    [Theory]
    [InlineData("Unknown", null, null)]
    [InlineData(null, "Severe", null)]
    [InlineData(null, null, "Closed")]
    public void UnknownIncidentFiltersAreRejected(string? type, string? severity, string? status) =>
        Assert.Throws<BusinessException>(() => SecurityIncidentHandlers.Normalize(null, type, severity, status, null, null, null));

    // ---- dashboard sections ----

    [Fact]
    public async Task PermittedFormsArePagedOnTheServerWithFiltersAndAStableOrder()
    {
        var definitions = Enumerable.Range(0, 20).Select(i => new FormDefinition { FormDefinitionId = Guid.CreateVersion7(), TenantId = Org, FormDefinitionName = $"Form {i:00}", CurrentFormVersionId = i % 2 == 0 ? Guid.CreateVersion7() : null }).ToList();
        var versions = definitions.Where(d => d.CurrentFormVersionId is not null).Select(d => new FormVersion { FormVersionId = d.CurrentFormVersionId!.Value, FormDefinitionId = d.FormDefinitionId, VersionNumber = 1 }).ToList();
        var scope = Stubs.Create<IBusinessScope>((method, _) => method.Name switch
        {
            nameof(IBusinessScope.GetFormDefinitionsAsync) => Task.FromResult(definitions),
            nameof(IBusinessScope.GetFormVersionSummariesAsync) => Task.FromResult(versions),
            nameof(IBusinessScope.GetFormDefinitionIdsWithDraftAsync) => Task.FromResult(new HashSet<Guid>()),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });
        var store = Stubs.Create<IBusinessStore>((_, _) => Task.FromResult(scope));
        // Designer of nothing; may create records with the first 18 forms only (the last two are invisible).
        var authority = new FakeAuthority { FormAllowed = (id, action) => action == ProtectedAction.CreateRecord && definitions.FindIndex(d => d.FormDefinitionId == id) < 18 };
        var forms = new FormDesignerHandlers(store, authority.Proxy, null!, TimeProvider.System, null!);
        var handlers = new DashboardSectionHandlers(store, authority.Proxy, new TestProtector(), forms, TimeProvider.System);

        var first = await handlers.FormsAsync(Context, null, null, null, null, null, CancellationToken.None);
        Assert.Equal(18, first.TotalCount);
        Assert.Equal(15, first.Items.Count);
        Assert.Equal("Form 00", first.Items[0].FormDefinitionName);
        var second = await handlers.FormsAsync(Context, null, null, null, null, first.NextToken, CancellationToken.None);
        Assert.Equal(3, second.Items.Count);
        Assert.DoesNotContain(first.Items.Concat(second.Items), f => f.FormDefinitionName is "Form 18" or "Form 19");

        var published = await handlers.FormsAsync(Context, "form 1", null, "published", null, null, CancellationToken.None);
        Assert.All(published.Items, f => Assert.NotNull(f.CurrentVersionNumber));
        Assert.Equal(["Form 10", "Form 12", "Form 14", "Form 16"], published.Items.Select(f => f.FormDefinitionName));
        var design = await handlers.FormsAsync(Context, null, "design", null, null, null, CancellationToken.None);
        Assert.Equal(0, design.TotalCount);
        await Assert.ThrowsAsync<BusinessException>(() => handlers.FormsAsync(Context, null, "everything", null, null, null, CancellationToken.None));
    }

    [Fact]
    public void UploadStatesMapToFourGroupsAndUnknownGroupsAreRejected()
    {
        Assert.Equal("Completed", DashboardSectionHandlers.UploadGroupOf("Committed"));
        Assert.Equal("Failed", DashboardSectionHandlers.UploadGroupOf("Rejected"));
        Assert.Equal("Cancelled", DashboardSectionHandlers.UploadGroupOf("Expired"));
        Assert.Equal("InProgress", DashboardSectionHandlers.UploadGroupOf("Processing"));
        Assert.Equal(9, DashboardSectionHandlers.StatesOfGroup(null).Count);
        Assert.Throws<BusinessException>(() => DashboardSectionHandlers.StatesOfGroup("Everything"));
    }

    // ---- time buckets and page size ----

    [Fact]
    public void TimeBucketsCoverThePeriodEndingTodayInTheViewersZone()
    {
        var plan = TimeBucketPlan.Create(new DateTimeOffset(2026, 10, 30, 2, 0, 0, TimeSpan.Zero), 7, "America/Toronto");
        Assert.Equal("America/Toronto", plan.TimeZoneId);
        Assert.Equal(7, plan.BucketCount);
        // 02:00 UTC on Oct 30 is still Oct 29 in Toronto, so the last bucket is Oct 29.
        Assert.Equal("2026-10-29", plan.BucketStartDate(6));
        Assert.Equal(6, plan.IndexOf(new DateTimeOffset(2026, 10, 30, 1, 0, 0, TimeSpan.Zero)));
        Assert.Null(plan.IndexOf(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));

        var quarter = TimeBucketPlan.Create(Now, 90, null);
        Assert.Equal("Etc/UTC", quarter.TimeZoneId);
        Assert.Equal(13, quarter.BucketCount);
        Assert.Equal(7, quarter.BucketDayCount);
        Assert.Equal(12, quarter.IndexOf(Now));

        Assert.Equal(30, TimeBucketPlan.Create(Now, 45, "Not/AZone").PeriodDays);
        Assert.Equal("Etc/UTC", TimeBucketPlan.Create(Now, 45, "Not/AZone").TimeZoneId);
    }

    [Fact]
    public void PageSizeDefaultsToFifteenAndIsCapped()
    {
        Assert.Equal(15, OffsetPageCursor.ClampPageSize(null));
        Assert.Equal(50, OffsetPageCursor.ClampPageSize(5000));
        Assert.Equal(1, OffsetPageCursor.ClampPageSize(0));
    }

    // ---- stand-ins ----

    private static ISecurityAdministratorAuthority Roster() => Stubs.Create<ISecurityAdministratorAuthority>((_, _) => Task.FromResult(new AuthoritySecurityAdministratorRoster(true, true, string.Empty, [new AuthoritySecurityAdministrator(Me, "https://issuer.invalid", "subject", Now, Guid.CreateVersion7())], [], 9, Guid.Empty)));

    private sealed class TestProtector : IOpaqueCursorProtector
    {
        public string Protect(string payload, TimeSpan lifetime) => "ok." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

        public string? Unprotect(string token) => token.StartsWith("ok.", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token[3..])) : null;
    }

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

        public bool AdministerSecurity { get; init; } = true;

        public Func<Guid, bool> Discover { get; init; } = _ => true;

        public Func<Guid, ProtectedAction, bool> FormAllowed { get; init; } = (_, _) => true;

        public List<ProtectedAction> Checks { get; } = [];

        private object? Handle(MethodInfo method, object?[] args)
        {
            switch (method.Name)
            {
                case nameof(IAuthorizationAuthorityClient.CheckPermissionAsync):
                    var request = (PermissionCheckRequest)args[1]!;
                    Checks.Add(request.RequestedAction);
                    return Task.FromResult(new PermissionDecision(request.RequestedAction == ProtectedAction.AdministerSecurity && AdministerSecurity, null, 9, 9, null, Guid.Empty));
                case nameof(IAuthorizationAuthorityClient.CheckPermissionsBatchAsync):
                    var requests = (IReadOnlyList<PermissionCheckRequest>)args[1]!;
                    return Task.FromResult<IReadOnlyList<PermissionDecision>>(requests.Select(r => new PermissionDecision(
                        r.RequestedAction == ProtectedAction.Discover ? Discover(r.Resource.ResourceId) : FormAllowed(r.Resource.ResourceId, r.RequestedAction), null, 9, 9, null, Guid.Empty)).ToList());
                default:
                    throw new NotSupportedException(method.Name);
            }
        }
    }

    private sealed class FakeIncidentStore
    {
        private readonly List<SecurityIncident> incidents;

        public FakeIncidentStore(List<SecurityIncident> incidents)
        {
            this.incidents = incidents;
            Proxy = Stubs.Create<IBusinessStore>((method, _) => method.Name == nameof(IBusinessStore.BeginScopeAsync) ? Task.FromResult(NewScope()) : throw new NotSupportedException(method.Name));
        }

        public IBusinessStore Proxy { get; }

        private IBusinessScope NewScope() => Stubs.Create<IBusinessScope>((method, args) => method.Name switch
        {
            nameof(IBusinessScope.QuerySecurityIncidentsAsync) => Task.FromResult(Query((SecurityIncidentFilter)args[1]!, (int)args[2]!, (int)args[3]!)),
            nameof(IBusinessScope.GetSecurityIncidentStatisticsAsync) => Task.FromResult(incidents.Select(i => new SecurityIncidentStatistic(i.IncidentStatusName, i.IncidentSeverityName, i.IncidentTypeName, i.FirstDetectedOn, i.LastDetectedOn)).ToList()),
            nameof(IBusinessScope.GetUsersAsync) => Task.FromResult(new List<User> { new() { UserId = Me, TenantId = Org, DisplayName = "Tester" } }),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });

        private StorePage<SecurityIncident> Query(SecurityIncidentFilter filter, int offset, int limit)
        {
            var matching = incidents
                .Where(i => filter.IncidentSeverityName is null || i.IncidentSeverityName == filter.IncidentSeverityName)
                .Where(i => filter.FormRecordIds is null || (i.FormRecordId is { } r && filter.FormRecordIds.Contains(r)))
                .Where(i => filter.IncidentIdSuffix is null || i.SecurityIncidentId.ToString("D").EndsWith(filter.IncidentIdSuffix, StringComparison.Ordinal))
                .OrderByDescending(i => i.LastDetectedOn).ThenByDescending(i => i.SecurityIncidentId).ToList();
            return new StorePage<SecurityIncident>(matching.Skip(offset).Take(limit).ToList(), matching.Count);
        }
    }
}
