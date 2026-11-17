using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2f-3b (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §6, §8.1, §10.2, §10.5) on the real Authority host pipeline (authentication,
/// endpoints, the evidence filter, exception mapping). The seven requests are the ones the real E3-M2f-1 clients send (captured by
/// <see cref="EnvelopeCapabilityEnforcementHostTests"/>), replayed without a capability declaration against a scripted store that
/// refuses them in T1 (bound organization) and implements the evidence port transactionally for T2, logging every scope call.
/// </summary>
public sealed class CapabilityRefusalEvidenceHostTests
{
    private const string Header = ClientEnvelopeCapability.HeaderName;
    private static readonly Guid Correlation = Guid.Parse("0196a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] CapabilityRoutes =
    [
        "/internal/v1/content-changes/prepare", "/internal/v1/content-changes/{operationId:guid}/commit", "/internal/v1/integrity/verify-record-projection",
        "/internal/v1/integrity/verify-record-projections", "/internal/v1/verifier/migration-catalogue/{snapshotId:guid}/census",
        "/internal/v1/search-index-builder/backfill", "/internal/v1/search-index-builder/deep-sweep/verify",
    ];

    private static readonly Dictionary<string, string> EntryOfRequest = new(StringComparer.Ordinal)
    {
        ["prepare"] = CapabilityRefusalEvidence.EntryPoints.Prepare,
        ["commit"] = CapabilityRefusalEvidence.EntryPoints.Commit,
        ["verify"] = CapabilityRefusalEvidence.EntryPoints.Verify,
        ["batch-verify"] = CapabilityRefusalEvidence.EntryPoints.VerifyBatch,
        ["census"] = CapabilityRefusalEvidence.EntryPoints.ProtectionCensus,
        ["backfill"] = CapabilityRefusalEvidence.EntryPoints.IndexBackfill,
        ["deep-verify"] = CapabilityRefusalEvidence.EntryPoints.IndexDeepVerify,
    };

    // ---- the evidence-capable scripted store ----

    public sealed class EvidenceScript
    {
        private int sequence;

        public EvidenceScript(Guid organization, SearchCatalogueSnapshot snapshot)
        {
            Organization = organization;
            Snapshot = snapshot;
            State = new OrganizationAuthorizationState { OrganizationId = organization, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, AuditJournalRevision = 12 };
            var store = DispatchProxy.Create<IAuthorityStore, EvidenceHostStore>();
            ((EvidenceHostStore)(object)store).Script = this;
            Store = store;
        }

        public Guid Organization { get; }

        public SearchCatalogueSnapshot Snapshot { get; }

        public OrganizationAuthorizationState State { get; set; }

        public bool IndexUsable { get; set; } = true;

        public bool EvidencePortFails { get; set; }

        public List<SecurityJournalEntry> Journal { get; } = [];

        public ConcurrentQueue<string> Events { get; } = new();

        public IAuthorityStore Store { get; }

        public int Next() => Interlocked.Increment(ref sequence);

        /// <summary>The scopes (by id) and their calls, in event order.</summary>
        public Dictionary<int, List<string>> Scopes() => Events.Select(e => e.Split('#')).GroupBy(p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)).ToDictionary(g => g.Key, g => g.Select(p => p[0]).ToList());

        public void Reset()
        {
            Events.Clear();
        }
    }

    public class EvidenceHostStore : DispatchProxy
    {
        public EvidenceScript Script { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IAuthorityStore.BeginScopeAsync))
            {
                var scope = Create<IAuthorityScope, EvidenceHostScope>();
                var proxy = (EvidenceHostScope)(object)scope;
                proxy.Script = Script;
                proxy.Id = Script.Next();
                Script.Events.Enqueue("Begin#" + proxy.Id);
                return Task.FromResult(scope);
            }

            throw new NpgsqlException("scripted store: " + targetMethod?.Name + " is not available");
        }
    }

    public class EvidenceHostScope : DispatchProxy
    {
        private readonly List<SecurityJournalEntry> appended = [];
        private OrganizationAuthorizationState? working;

        public EvidenceScript Script { get; set; } = null!;

        public int Id { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            Script.Events.Enqueue((name == nameof(IAsyncDisposable.DisposeAsync) ? "Dispose" : name) + "#" + Id);
            if (Script.EvidencePortFails && name.Contains("CapabilityEvidence", StringComparison.Ordinal))
            {
                throw new NpgsqlException("evidence port unavailable (test)");
            }

            switch (name)
            {
                case nameof(IAsyncDisposable.DisposeAsync):
                    return ValueTask.CompletedTask;
                case nameof(IAuthorityScope.LockOrganizationAsync):
                    working = (Guid)args![0]! == Script.Organization ? (OrganizationAuthorizationState)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Script.State, null)! : null;
                    return Task.FromResult(working);
                case nameof(IAuthorityScope.RequireContentAcceptanceFloorAsync):
                    return Task.CompletedTask;
                case nameof(IAuthorityScope.OrganizationHasDataKeyBindingsAsync):
                    return Task.FromResult(true);
                case nameof(IAuthorityScope.FindCatalogueSnapshotAsync):
                    return Task.FromResult<SearchCatalogueSnapshot?>(Script.Snapshot);
                case nameof(IAuthorityScope.SetCapabilityEvidenceLockTimeoutAsync):
                    return Task.CompletedTask;
                case nameof(IAuthorityScope.StartCapabilityEvidenceLookupAsync):
                    return Task.FromResult(DateTimeOffset.UtcNow);
                case nameof(IAuthorityScope.IsCapabilityRefusalEvidenceIndexUsableAsync):
                    return Task.FromResult(Script.IndexUsable);
                case nameof(IAuthorityScope.GetNewestCapabilityRefusalEvidenceAsync):
                    lock (Script.Journal)
                    {
                        return Task.FromResult<IReadOnlyList<CapabilityRefusalEvidenceRow>>(Script.Journal.OrderByDescending(e => e.JournalStreamRevision).Take(8)
                            .Select(e => new CapabilityRefusalEvidenceRow(e.SecurityJournalEntryId, e.JournalStreamRevision, e.JournalEventBodyJson, e.JournalEventDigest)).ToList());
                    }

                case nameof(IAuthorityScope.AddJournalEntry):
                    appended.Add((SecurityJournalEntry)args![0]!);
                    return null;
                case nameof(IAuthorityScope.CommitAsync):
                    lock (Script.Journal)
                    {
                        Script.Journal.AddRange(appended);
                        if (working is not null)
                        {
                            Script.State = working;
                        }
                    }

                    return Task.CompletedTask;
                default:
                    throw new NpgsqlException("scripted scope: " + name + " is not available");
            }
        }
    }

    private static WebApplicationFactory<AuthorizationAuthority.Api.Program> Host(EvidenceScript script)
    {
        // The same host settings as the E3-M2f-2 host tests, with this store.
        var template = new EnvelopeCapabilityEnforcementHostTests.Script();
        return EnvelopeCapabilityEnforcementHostTests.Authority(template).WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAuthorityStore>();
            services.AddSingleton(script.Store);
        }));
    }

    private static async Task<(List<EnvelopeCapabilityEnforcementHostTests.Captured> Requests, Guid Organization, SearchCatalogueSnapshot Snapshot)> CaptureAsync()
    {
        var capture = new EnvelopeCapabilityEnforcementHostTests.Script();
        var organization = capture.AddOrganization(bound: true);
        using var authority = EnvelopeCapabilityEnforcementHostTests.Authority(capture);
        using var started = authority.CreateClient();
        var requests = await EnvelopeCapabilityEnforcementHostTests.CaptureSevenAsync(authority, capture, organization);
        return (requests, organization, capture.Snapshot!);
    }

    private static async Task<(HttpStatusCode Status, byte[] Body)> ReplayAsync(HttpClient raw, EnvelopeCapabilityEnforcementHostTests.Captured sent, string[]? capability = null)
    {
        using var replay = new HttpRequestMessage(sent.Method, sent.Uri);
        foreach (var (name, values) in sent.Headers.Where(h => h.Key != Header && h.Key != InternalAuthentication.CorrelationHeader))
        {
            replay.Headers.TryAddWithoutValidation(name, values);
        }

        replay.Headers.TryAddWithoutValidation(InternalAuthentication.CorrelationHeader, Correlation.ToString("D"));
        if (capability is not null)
        {
            replay.Headers.TryAddWithoutValidation(Header, capability);
        }

        replay.Content = new ByteArrayContent(sent.Body);
        replay.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await raw.SendAsync(replay, Ct);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    private static void AssertRefusal((HttpStatusCode Status, byte[] Body) response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        Assert.Equal(("CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT", false, EnvelopeCapabilityFloor.RefusalDetail),
            (root.GetProperty("error_code").GetString(), root.GetProperty("retryable").GetBoolean(), root.GetProperty("detail").GetString()));
    }

    // ---- M2F3-08..14, M2F3-17: each route records once, after T1 is disposed ----

    [Fact]
    public async Task EveryCapabilityRouteRecordsOneEntryOnlyAfterTheRefusedTransactionWasDisposed()
    {
        var (requests, organization, snapshot) = await CaptureAsync();
        var script = new EvidenceScript(organization, snapshot);
        using var authority = Host(script);
        using var started = authority.CreateClient();
        using var raw = new HttpClient(authority.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };

        foreach (var sent in requests)
        {
            script.Reset();
            AssertRefusal(await ReplayAsync(raw, sent));
            var scopes = script.Scopes();
            var t1 = Assert.Single(scopes, s => s.Value.Contains(nameof(IAuthorityScope.OrganizationHasDataKeyBindingsAsync))).Key;
            var t2 = Assert.Single(scopes, s => s.Value.Contains(nameof(IAuthorityScope.SetCapabilityEvidenceLockTimeoutAsync))).Key;
            var events = script.Events.ToList();
            Assert.True(events.IndexOf("Dispose#" + t1) >= 0 && events.IndexOf("Dispose#" + t1) < events.IndexOf("Begin#" + t2), sent.Name + ": T2 began only after T1 was disposed");
            Assert.DoesNotContain(nameof(IAuthorityScope.AddJournalEntry), scopes[t1]);
            Assert.DoesNotContain(nameof(IAuthorityScope.CommitAsync), scopes[t1]);
            Assert.Contains(nameof(IAuthorityScope.CommitAsync), scopes[t2]);
            Assert.DoesNotContain(nameof(IAuthorityScope.OrganizationHasDataKeyBindingsAsync), scopes[t2]);
            Assert.DoesNotContain(nameof(IAuthorityScope.RequireContentAcceptanceFloorAsync), scopes[t2]);

            // A repeat: the same 409, T1 again, no T2 (pre-filter).
            script.Reset();
            AssertRefusal(await ReplayAsync(raw, sent));
            Assert.DoesNotContain(script.Scopes(), s => s.Value.Contains(nameof(IAuthorityScope.SetCapabilityEvidenceLockTimeoutAsync)));
        }

        var entries = script.Journal.Select(e => CapabilityRefusalEvidenceLookup.Verify(new CapabilityRefusalEvidenceRow(e.SecurityJournalEntryId, e.JournalStreamRevision, e.JournalEventBodyJson, e.JournalEventDigest), organization)).ToList();
        Assert.Equal(7, entries.Count);
        Assert.All(entries, Assert.NotNull);
        Assert.Equal(requests.Select(r => EntryOfRequest[r.Name]).Order(StringComparer.Ordinal), entries.Select(e => e!.Key.EntryPoint).Order(StringComparer.Ordinal));
        Assert.All(entries, e => Assert.Equal(CapabilityRefusalEvidence.WorkloadOf(e!.Key.EntryPoint), e.Key.WorkloadName));
        Assert.All(script.Journal, e => Assert.Equal((JournalStreamNames.Audit, CapabilityRefusalEvidence.EventKind), (e.JournalStreamName, e.JournalEventKind)));
        Assert.Equal(12 + 7, script.State.AuditJournalRevision);
        Assert.False(script.State.IntegrityQuarantineEnabled);
    }

    // ---- M2F3-07, F-1: the 409 is byte-identical whatever happens to the evidence ----

    [Fact]
    public async Task TheRefusalIsByteIdenticalWhetherEvidenceIsRecordedSuppressedDeferredOrFailing()
    {
        var (requests, organization, snapshot) = await CaptureAsync();
        var recorded = new EvidenceScript(organization, snapshot);
        var unusable = new EvidenceScript(organization, snapshot) { IndexUsable = false };
        var failing = new EvidenceScript(organization, snapshot) { EvidencePortFails = true };
        using var a = Host(recorded);
        using var b = Host(unusable);
        using var c = Host(failing);
        using var startedA = a.CreateClient();
        using var startedB = b.CreateClient();
        using var startedC = c.CreateClient();
        using var rawA = new HttpClient(a.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };
        using var rawB = new HttpClient(b.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };
        using var rawC = new HttpClient(c.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };

        foreach (var sent in requests)
        {
            var first = await ReplayAsync(rawA, sent);       // recorded
            var repeat = await ReplayAsync(rawA, sent);      // suppressed by the pre-filter
            var deferred = await ReplayAsync(rawB, sent);    // index unusable: deferred
            var broken = await ReplayAsync(rawC, sent);      // evidence port throws
            AssertRefusal(first);
            foreach (var other in new[] { repeat, deferred, broken })
            {
                Assert.Equal(first.Status, other.Status);
                Assert.Equal(first.Body, other.Body);
            }
        }

        Assert.Equal(7, recorded.Journal.Count);
        Assert.Empty(unusable.Journal);
        Assert.Empty(failing.Journal);
    }

    // ---- M2F3-21, M2F3-46: everything else passes through unrecorded ----

    [Fact]
    public async Task AdmittedRequestsAndOtherErrorsAreNeverRecorded()
    {
        var (requests, organization, snapshot) = await CaptureAsync();
        var script = new EvidenceScript(organization, snapshot);
        using var authority = Host(script);
        using var started = authority.CreateClient();
        using var raw = new HttpClient(authority.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };

        foreach (var sent in requests)
        {
            script.Reset();
            var admitted = await ReplayAsync(raw, sent, ["2"]); // passes the check; the scripted store then fails the next step
            Assert.NotEqual(HttpStatusCode.Conflict, admitted.Status);
            Assert.DoesNotContain(script.Scopes(), s => s.Value.Contains(nameof(IAuthorityScope.SetCapabilityEvidenceLockTimeoutAsync)));
        }

        Assert.Empty(script.Journal);
    }

    // ---- M2F3-45: exactly the seven routes carry the filter ----

    [Fact]
    public void ExactlyTheSevenCapabilityRoutesCarryTheEvidenceFilter()
    {
        using var authority = EnvelopeCapabilityEnforcementHostTests.Authority(new EnvelopeCapabilityEnforcementHostTests.Script());
        using var started = authority.CreateClient();
        var endpoints = authority.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
        var marked = endpoints.Select(e => (Pattern: e.RoutePattern.RawText, Marker: e.Metadata.GetMetadata<CapabilityRefusalEvidenceEndpoint>())).Where(e => e.Marker is not null).ToList();
        Assert.Equal(7, marked.Count);
        Assert.Equal(CapabilityRefusalEvidence.EntryPoints.All.Order(StringComparer.Ordinal), marked.Select(m => m.Marker!.EntryPoint).Order(StringComparer.Ordinal));
        Assert.Equal(CapabilityRoutes.Order(StringComparer.Ordinal), marked.Select(m => m.Pattern!).Order(StringComparer.Ordinal));
    }
}
