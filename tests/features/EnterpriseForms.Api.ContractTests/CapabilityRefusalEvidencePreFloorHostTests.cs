using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// FU-M2f3-01, closure test M2F3-22 (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §10.2): "Requests rejected before the floor produce no
/// evidence". On the real Authority host pipeline (authentication, endpoints, the evidence filter, exception mapping), each of the seven
/// requests the real E3-M2f-1 clients send is replayed <b>without a capability declaration</b> (level 1) for an organization with a
/// binding, where the unmodified request is refused by the floor and recorded. Each variant changes exactly one thing so that the request
/// is rejected <b>before</b> the floor: no credential, a forged credential, another workload's credential, an unreadable body, an
/// unregistered organization, an unavailable store at the organization lock, the Release B fence (Prepare, Commit) and an unknown census
/// snapshot (protection census).
/// <para>
/// Premise (that the rejection really happened before the floor): the floor's observer saw no evaluation and no scope asked for the binding
/// existence. Assertion (no evidence): no evidence transaction began, the recorder's observer saw nothing, the recorder holds no attempt,
/// cache or pending state, and the journal is empty. Sensitivity control: afterwards the unmodified request is refused and recorded.
/// </para>
/// </summary>
public sealed class CapabilityRefusalEvidencePreFloorHostTests
{
    private const string Header = ClientEnvelopeCapability.HeaderName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- a scripted store with switchable pre-floor faults; logs every scope call ----

    public sealed class PreFloorScript
    {
        private int sequence;

        public PreFloorScript(Guid organization, SearchCatalogueSnapshot snapshot)
        {
            Organization = organization;
            Snapshot = snapshot;
            State = new OrganizationAuthorizationState { OrganizationId = organization, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, AuditJournalRevision = 12 };
            var store = DispatchProxy.Create<IAuthorityStore, PreFloorStore>();
            ((PreFloorStore)(object)store).Script = this;
            Store = store;
        }

        public Guid Organization { get; }

        public SearchCatalogueSnapshot Snapshot { get; }

        public OrganizationAuthorizationState State { get; set; }

        public bool OrganizationRegistered { get; set; } = true;

        public bool LockUnavailable { get; set; }

        public bool FenceRefuses { get; set; }

        public bool SnapshotUnknown { get; set; }

        public List<SecurityJournalEntry> Journal { get; } = [];

        public ConcurrentQueue<string> Events { get; } = new();

        public IAuthorityStore Store { get; }

        public int Next() => Interlocked.Increment(ref sequence);

        public void Reset()
        {
            Events.Clear();
            OrganizationRegistered = true;
            LockUnavailable = false;
            FenceRefuses = false;
            SnapshotUnknown = false;
        }

        public bool Called(string method) => Events.Any(e => e.StartsWith(method + "#", StringComparison.Ordinal));
    }

    public class PreFloorStore : DispatchProxy
    {
        public PreFloorScript Script { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IAuthorityStore.BeginScopeAsync))
            {
                var scope = Create<IAuthorityScope, PreFloorScope>();
                var proxy = (PreFloorScope)(object)scope;
                proxy.Script = Script;
                proxy.Id = Script.Next();
                Script.Events.Enqueue("Begin#" + proxy.Id);
                return Task.FromResult(scope);
            }

            throw new NpgsqlException("scripted store: " + targetMethod?.Name + " is not available");
        }
    }

    public class PreFloorScope : DispatchProxy
    {
        private readonly List<SecurityJournalEntry> appended = [];
        private OrganizationAuthorizationState? working;

        public PreFloorScript Script { get; set; } = null!;

        public int Id { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            Script.Events.Enqueue((name == nameof(IAsyncDisposable.DisposeAsync) ? "Dispose" : name) + "#" + Id);
            switch (name)
            {
                case nameof(IAsyncDisposable.DisposeAsync):
                    return ValueTask.CompletedTask;
                case nameof(IAuthorityScope.LockOrganizationAsync):
                    if (Script.LockUnavailable)
                    {
                        throw new NpgsqlException("organization lock unavailable (test)");
                    }

                    working = Script.OrganizationRegistered && (Guid)args![0]! == Script.Organization
                        ? (OrganizationAuthorizationState)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Script.State, null)!
                        : null;
                    return Task.FromResult(working);
                case nameof(IAuthorityScope.RequireContentAcceptanceFloorAsync):
                    return Script.FenceRefuses ? Task.FromException(ContentAcceptanceFloorFailures.Refusal()) : Task.CompletedTask;
                case nameof(IAuthorityScope.OrganizationHasDataKeyBindingsAsync):
                    return Task.FromResult(true);
                case nameof(IAuthorityScope.FindCatalogueSnapshotAsync):
                    return Task.FromResult(Script.SnapshotUnknown ? null : Script.Snapshot);
                case nameof(IAuthorityScope.SetCapabilityEvidenceLockTimeoutAsync):
                    return Task.CompletedTask;
                case nameof(IAuthorityScope.StartCapabilityEvidenceLookupAsync):
                    return Task.FromResult(DateTimeOffset.UtcNow);
                case nameof(IAuthorityScope.IsCapabilityRefusalEvidenceIndexUsableAsync):
                    return Task.FromResult(true);
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

    // ---- observers of the floor (premise) and of the recorder (assertion) ----

    private sealed class FloorEvaluations : IEnvelopeCapabilityObserver
    {
        public ConcurrentQueue<(Guid Organization, int Level, string Outcome)> All { get; } = new();

        public void Evaluated(Guid organizationId, int clientLevel, string outcome) => All.Enqueue((organizationId, clientLevel, outcome));
    }

    private sealed class EvidenceOutcomes : ICapabilityRefusalEvidenceObserver
    {
        public ConcurrentQueue<(Guid Organization, string EntryPoint, CapabilityRefusalEvidenceOutcome Outcome)> All { get; } = new();

        public void Observed(Guid organizationId, string entryPoint, CapabilityRefusalEvidenceOutcome outcome, CapabilityRefusalEvidenceReason reason) => All.Enqueue((organizationId, entryPoint, outcome));
    }

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

    /// <summary>How one variant changes the replayed request or the store, and the status the host must answer with.</summary>
    private sealed record Variant(string Name, Func<string, bool> AppliesTo, HttpStatusCode Expected, Action<PreFloorScript>? Store = null,
        Func<EnvelopeCapabilityEnforcementHostTests.Captured, IReadOnlyList<EnvelopeCapabilityEnforcementHostTests.Captured>, (Dictionary<string, string[]> Headers, byte[] Body)>? Request = null);

    private static bool IsAuthorization(string header) => string.Equals(header, "Authorization", StringComparison.OrdinalIgnoreCase);

    private static string WorkloadOf(string requestName) => CapabilityRefusalEvidence.WorkloadOf(EntryOfRequest[requestName]);

    private static readonly Variant[] Variants =
    [
        new("no credential", _ => true, HttpStatusCode.Unauthorized,
            Request: (sent, _) => (sent.Headers.Where(h => !IsAuthorization(h.Key)).ToDictionary(h => h.Key, h => h.Value), sent.Body)),
        new("forged credential", _ => true, HttpStatusCode.Unauthorized,
            Request: (sent, _) => (sent.Headers.ToDictionary(h => h.Key, h => IsAuthorization(h.Key) ? [Forge(h.Value[0])] : h.Value), sent.Body)),
        new("another workload's credential", _ => true, HttpStatusCode.Unauthorized,
            Request: (sent, all) =>
            {
                var foreign = all.First(r => WorkloadOf(r.Name) != WorkloadOf(sent.Name));
                return (sent.Headers.ToDictionary(h => h.Key, h => IsAuthorization(h.Key) ? foreign.Headers.First(f => IsAuthorization(f.Key)).Value : h.Value), sent.Body);
            }),
        new("unreadable body", _ => true, HttpStatusCode.UnprocessableEntity,
            Request: (sent, _) => (sent.Headers, "{\"organization_id\":"u8.ToArray())),
        new("unregistered organization", _ => true, HttpStatusCode.NotFound, Store: s => s.OrganizationRegistered = false),
        new("store unavailable at the organization lock", _ => true, HttpStatusCode.ServiceUnavailable, Store: s => s.LockUnavailable = true),
        new("Release B fence refuses", name => name is "prepare" or "commit", HttpStatusCode.ServiceUnavailable, Store: s => s.FenceRefuses = true),
        new("unknown census snapshot", name => name == "census", HttpStatusCode.NotFound, Store: s => s.SnapshotUnknown = true),
    ];

    /// <summary>Changes one character of the signature segment (the token still parses, its signature no longer verifies).</summary>
    private static string Forge(string authorization)
    {
        var last = authorization[^2];
        return authorization[..^2] + (last == 'A' ? 'B' : 'A') + authorization[^1];
    }

    private static async Task<(HttpStatusCode Status, string? Code)> SendAsync(HttpClient raw, EnvelopeCapabilityEnforcementHostTests.Captured sent, Dictionary<string, string[]> headers, byte[] body)
    {
        using var request = new HttpRequestMessage(sent.Method, sent.Uri);
        foreach (var (name, values) in headers.Where(h => h.Key != Header))
        {
            request.Headers.TryAddWithoutValidation(name, values);
        }

        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await raw.SendAsync(request, Ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return (response.StatusCode, document.RootElement.TryGetProperty("error_code", out var code) ? code.GetString() : null);
        }
        catch (JsonException)
        {
            return (response.StatusCode, null);
        }
    }

    [Fact]
    public async Task RequestsRejectedBeforeTheFloorProduceNoEvidenceOnAnyOfTheSevenRoutes()
    {
        // The seven requests exactly as the real E3-M2f-1 clients send them (the capability header is removed on replay: level 1).
        var capture = new EnvelopeCapabilityEnforcementHostTests.Script();
        var organization = capture.AddOrganization(bound: true);
        List<EnvelopeCapabilityEnforcementHostTests.Captured> requests;
        using (var capturing = EnvelopeCapabilityEnforcementHostTests.Authority(capture))
        {
            using var started = capturing.CreateClient();
            requests = await EnvelopeCapabilityEnforcementHostTests.CaptureSevenAsync(capturing, capture, organization);
        }

        var script = new PreFloorScript(organization, capture.Snapshot!);
        var floor = new FloorEvaluations();
        var evidence = new EvidenceOutcomes();
        using var authority = EnvelopeCapabilityEnforcementHostTests.Authority(new EnvelopeCapabilityEnforcementHostTests.Script()).WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAuthorityStore>();
            services.AddSingleton(script.Store);
            services.RemoveAll<IEnvelopeCapabilityObserver>();
            services.AddSingleton<IEnvelopeCapabilityObserver>(floor);
            services.RemoveAll<ICapabilityRefusalEvidenceObserver>();
            services.AddSingleton<ICapabilityRefusalEvidenceObserver>(evidence);
        }));
        using var started2 = authority.CreateClient();
        using var raw = new HttpClient(authority.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };
        var recorder = authority.Services.GetRequiredService<CapabilityRefusalEvidenceRecorder>();

        var executed = 0;
        var statuses = new List<string>();
        foreach (var sent in requests)
        {
            foreach (var variant in Variants.Where(v => v.AppliesTo(sent.Name)))
            {
                var label = sent.Name + " / " + variant.Name;
                script.Reset();
                variant.Store?.Invoke(script);
                var (headers, body) = variant.Request?.Invoke(sent, requests) ?? (sent.Headers, sent.Body);
                Assert.Contains(sent.Headers, h => IsAuthorization(h.Key)); // the captured request carries its workload credential

                var response = await SendAsync(raw, sent, headers, body);

                // The rejection is the variant's own, before the floor.
                if (variant.Expected != response.Status)
                {
                    statuses.Add($"{label}: status {(int)response.Status} {response.Code}, expected {(int)variant.Expected}");
                }

                Assert.NotEqual(AuthorityErrorCodes.ClientEnvelopeCapabilityInsufficient, response.Code);
                Assert.True(floor.All.IsEmpty, label + ": the capability floor was evaluated");
                Assert.False(script.Called(nameof(IAuthorityScope.OrganizationHasDataKeyBindingsAsync)), label + ": the binding existence was queried");

                // No evidence: no T2, nothing reported by the recorder, no in-process recorder state, nothing written.
                Assert.False(script.Called(nameof(IAuthorityScope.SetCapabilityEvidenceLockTimeoutAsync)), label + ": an evidence transaction began");
                Assert.False(script.Called(nameof(IAuthorityScope.AddJournalEntry)), label + ": a journal entry was staged");
                Assert.True(evidence.All.IsEmpty, label + ": the recorder reported " + string.Join(", ", evidence.All.Select(e => e.Outcome)));
                Assert.Equal((0, 0, 0), (recorder.AttemptsInWindow, recorder.CachedCount, recorder.PendingCount));
                Assert.Empty(script.Journal);
                executed++;
            }
        }

        Assert.True(statuses.Count == 0, string.Join(Environment.NewLine, statuses));
        Assert.Equal(7 * 6 + 2 + 1, executed);

        // Sensitivity control: the same seven requests, unmodified, are refused by the floor and recorded once each.
        foreach (var sent in requests)
        {
            script.Reset();
            var response = await SendAsync(raw, sent, sent.Headers, sent.Body);
            Assert.Equal((HttpStatusCode.Conflict, AuthorityErrorCodes.ClientEnvelopeCapabilityInsufficient), (response.Status, response.Code));
            Assert.True(script.Called(nameof(IAuthorityScope.SetCapabilityEvidenceLockTimeoutAsync)), sent.Name + ": the control request was not recorded");
        }

        Assert.Equal(7, floor.All.Count(f => f.Outcome == EnvelopeCapabilityOutcomes.Refused && f.Organization == organization && f.Level == 1));
        Assert.Equal(7, evidence.All.Count(e => e.Outcome == CapabilityRefusalEvidenceOutcome.Committed));
        Assert.Equal(requests.Select(r => EntryOfRequest[r.Name]).Order(StringComparer.Ordinal), evidence.All.Select(e => e.EntryPoint).Order(StringComparer.Ordinal));
        Assert.Equal(7, script.Journal.Count);
        Assert.All(script.Journal, e => Assert.Equal(CapabilityRefusalEvidence.EventKind, e.JournalEventKind));
    }
}
