using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// FU-M2f3-03, closure test M2F3-21 (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §10.2): "Level-2 traffic and admitted level-1 traffic
/// produce no evidence", for requests the capability check admits and that then <b>complete successfully</b> through the real Authority
/// host pipeline (authentication, endpoint, the evidence filter, the real recorder and its real observers). Before this test only
/// admitted requests that failed at the next step were exercised, which let the success-path "record on admit" mutation MZ-09a survive.
/// <para>
/// Routes: the two builder-plane entry points whose success path can be completed against a scripted transactional store without faking
/// signed-content verification: deep-sweep verify (the stored entry holds another operation: the legitimate deep-check bookkeeping
/// <c>ENTRY_CHANGED</c> is written and committed in the request's own transaction, 200) and backfill (no covering approval: every record
/// <c>NotCovered</c>, nothing written, 200). Traffic: level 2 in a bound organization (deployment floor 1 and 2) and admitted level 1 in
/// an unbound organization (deployment floor 1, after the one binding existence query).
/// </para>
/// <para>
/// Per request: the floor admitted it (one <c>admitted</c> evaluation, no ledger read for level 2); 200 with the expected result; exactly
/// one scope, whose calls are exactly the success path (pinned; no evidence call, no second transaction); the legitimate write is the
/// expected one and nothing else is committed; no <c>EnvelopeCapabilityRefused</c> entry and no audit-revision change; no refusal-evidence
/// outcome on the observer (so no evidence counter), no 9250/9253/9254/9256 log, no recorder attempt, cache entry or scheduled retry.
/// Sensitivity control: the same request without a declaration in the bound organization is refused, recorded and logged.
/// </para>
/// </summary>
public sealed class CapabilityRefusalEvidenceAdmittedSuccessHostTests
{
    private const string Header = ClientEnvelopeCapability.HeaderName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- scripted transactional store: organization, gate, search index, evidence port; every call logged per scope ----

    public sealed class SuccessScript
    {
        private int sequence;

        public SuccessScript(Guid organization)
        {
            Organization = organization;
            State = new OrganizationAuthorizationState
            {
                OrganizationId = organization, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", AuditJournalRevision = 12,
            };
            StoredEntry = new SearchRecordEntry { SearchRecordEntryId = Guid.CreateVersion7(), OrganizationId = organization, IndexedOperationId = Guid.CreateVersion7() };
            var store = DispatchProxy.Create<IAuthorityStore, SuccessStore>();
            ((SuccessStore)(object)store).Script = this;
            Store = store;
        }

        public Guid Organization { get; }

        public OrganizationAuthorizationState State { get; set; }

        public bool Bound { get; set; }

        public SearchRecordEntry StoredEntry { get; }

        public List<SecurityJournalEntry> Journal { get; } = [];

        /// <summary>Committed deep-check bookkeeping: (entry, outcome code).</summary>
        public List<(Guid Entry, string Outcome)> CommittedDeepChecks { get; } = [];

        public ConcurrentQueue<string> Events { get; } = new();

        public IAuthorityStore Store { get; }

        public int Next() => Interlocked.Increment(ref sequence);

        public Dictionary<int, List<string>> Scopes() => Events.Select(e => e.Split('#')).GroupBy(p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)).ToDictionary(g => g.Key, g => g.Select(p => p[0]).ToList());

        public void Reset() => Events.Clear();

        /// <summary>A completed task with the empty value of a port method's result type (empty list, zero, null).</summary>
        public static object Empty(Type returnType)
        {
            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            var result = returnType.GetGenericArguments()[0];
            object? value = result.IsGenericType && result.GetGenericTypeDefinition() == typeof(List<>) ? Activator.CreateInstance(result)
                : result.IsValueType ? Activator.CreateInstance(result) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value])!;
        }
    }

    public class SuccessStore : DispatchProxy
    {
        public SuccessScript Script { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IAuthorityStore.BeginScopeAsync))
            {
                var scope = Create<IAuthorityScope, SuccessScope>();
                var proxy = (SuccessScope)(object)scope;
                proxy.Script = Script;
                proxy.Id = Script.Next();
                Script.Events.Enqueue("Begin#" + proxy.Id);
                return Task.FromResult(scope);
            }

            throw new NpgsqlException("scripted store: " + targetMethod?.Name + " is not available");
        }
    }

    public class SuccessScope : DispatchProxy
    {
        private readonly List<SecurityJournalEntry> appended = [];
        private readonly List<(Guid, string)> deepChecks = [];
        private OrganizationAuthorizationState? working;
        private ISearchIndexStore? index;

        public SuccessScript Script { get; set; } = null!;

        public int Id { get; set; }

        internal void StageDeepCheck(Guid entry, string outcome) => deepChecks.Add((entry, outcome));

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            Script.Events.Enqueue((name == nameof(IAsyncDisposable.DisposeAsync) ? "Dispose" : name) + "#" + Id);
            switch (name)
            {
                case nameof(IAsyncDisposable.DisposeAsync):
                    return ValueTask.CompletedTask;
                case nameof(IAuthorityScope.LockOrganizationAsync):
                    working = (Guid)args![0]! == Script.Organization
                        ? (OrganizationAuthorizationState)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Script.State, null)!
                        : null;
                    return Task.FromResult(working);
                case nameof(IAuthorityScope.OrganizationHasDataKeyBindingsAsync):
                    return Task.FromResult(Script.Bound);
                case nameof(IAuthorityScope.CountPendingArchiveAsync):
                    return Task.FromResult(0);
                case nameof(IAuthorityScope.FindLatestJournalEntryAsync):
                    // SEC-BL-01 E-6: this script has no authorization journal, so the gate has no head receipt to verify.
                    return Task.FromResult<AuthorizationAuthority.Domain.Features.Journal.SecurityJournalEntry?>(null);
                case "get_" + nameof(IAuthorityScope.SearchIndex):
                    if (index is null)
                    {
                        index = Create<ISearchIndexStore, SuccessIndex>();
                        ((SuccessIndex)(object)index).Scope = this;
                    }

                    return index;
                case nameof(IAuthorityScope.GetFieldSearchApprovalsAsync):
                    return SuccessScript.Empty(targetMethod.ReturnType);
                case nameof(IAuthorityScope.SetCapabilityEvidenceLockTimeoutAsync):
                    return Task.CompletedTask;
                case nameof(IAuthorityScope.StartCapabilityEvidenceLookupAsync):
                    return Task.FromResult(DateTimeOffset.UtcNow);
                case nameof(IAuthorityScope.IsCapabilityRefusalEvidenceIndexUsableAsync):
                    return Task.FromResult(true);
                case nameof(IAuthorityScope.GetNewestCapabilityRefusalEvidenceAsync):
                    lock (Script.Journal)
                    {
                        return Task.FromResult<IReadOnlyList<CapabilityRefusalEvidenceRow>>(Script.Journal.Where(e => e.JournalEventKind == CapabilityRefusalEvidence.EventKind)
                            .OrderByDescending(e => e.JournalStreamRevision).Take(8)
                            .Select(e => new CapabilityRefusalEvidenceRow(e.SecurityJournalEntryId, e.JournalStreamRevision, e.JournalEventBodyJson, e.JournalEventDigest)).ToList());
                    }

                case nameof(IAuthorityScope.AddJournalEntry):
                    appended.Add((SecurityJournalEntry)args![0]!);
                    return null;
                case nameof(IAuthorityScope.CommitAsync):
                    lock (Script.Journal)
                    {
                        Script.Journal.AddRange(appended);
                        Script.CommittedDeepChecks.AddRange(deepChecks);
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

        internal void Log(string call) => Script.Events.Enqueue(call + "#" + Id);
    }

    public class SuccessIndex : DispatchProxy
    {
        public SuccessScope Scope { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            Scope.Log("index." + name);
            var script = Scope.Script;
            switch (name)
            {
                case nameof(ISearchIndexStore.BindOrganizationAsync):
                    return Task.CompletedTask;
                case nameof(ISearchIndexStore.FindProjectionStateAsync):
                    return Task.FromResult<SearchProjectionState?>(new SearchProjectionState { SearchProjectionStateId = Guid.CreateVersion7(), OrganizationId = script.Organization });
                case nameof(ISearchIndexStore.GetStoredRecordIndexAsync):
                    return Task.FromResult<StoredRecordIndex?>(new StoredRecordIndex(script.StoredEntry, [], []));
                case nameof(ISearchIndexStore.RecordDeepCheckAsync):
                    Scope.StageDeepCheck((Guid)args![0]!, (string)args[3]!);
                    return Task.CompletedTask;
                case nameof(ISearchIndexStore.GetCoveragesAsync):
                case nameof(ISearchIndexStore.GetFormEntriesAsync):
                    return SuccessScript.Empty(targetMethod.ReturnType);
                default:
                    throw new NpgsqlException("scripted index: " + name + " is not available");
            }
        }
    }

    // ---- observers wrapped (the real observers still emit their counters and logs), and a host-scoped log capture ----

    private sealed class FloorEvaluations(EnvelopeCapabilityObserver inner) : IEnvelopeCapabilityObserver
    {
        public ConcurrentQueue<(Guid Organization, int Level, string Outcome)> All { get; } = new();

        public void Evaluated(Guid organizationId, int clientLevel, string outcome)
        {
            All.Enqueue((organizationId, clientLevel, outcome));
            inner.Evaluated(organizationId, clientLevel, outcome);
        }
    }

    private sealed class EvidenceOutcomes(CapabilityRefusalEvidenceObserver inner) : ICapabilityRefusalEvidenceObserver
    {
        public ConcurrentQueue<(Guid Organization, string EntryPoint, CapabilityRefusalEvidenceOutcome Outcome)> All { get; } = new();

        public void Observed(Guid organizationId, string entryPoint, CapabilityRefusalEvidenceOutcome outcome, CapabilityRefusalEvidenceReason reason)
        {
            All.Enqueue((organizationId, entryPoint, outcome));
            inner.Observed(organizationId, entryPoint, outcome, reason);
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<(int EventId, string Category, string Message)> All { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(CapturedLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.All.Enqueue((eventId.Id, category, formatter(state, exception)));
        }
    }

    private static readonly int[] RefusalEvents = [9250, 9253, 9254, 9256];

    private sealed record Host(WebApplicationFactory<AuthorizationAuthority.Api.Program> Factory, HttpClient Raw, SuccessScript Script, FloorEvaluations Floor,
        EvidenceOutcomes Evidence, CapturedLogs Logs, CapabilityRefusalEvidenceRecorder Recorder) : IDisposable
    {
        public void Dispose()
        {
            Raw.Dispose();
            Factory.Dispose();
        }
    }

    private static Host Start(Guid organization, string deploymentFloor)
    {
        var script = new SuccessScript(organization);
        var logs = new CapturedLogs();
        FloorEvaluations? floor = null;
        EvidenceOutcomes? evidence = null;
        var factory = EnvelopeCapabilityEnforcementHostTests.Authority(new EnvelopeCapabilityEnforcementHostTests.Script(), deploymentFloor).WithWebHostBuilder(b =>
        {
            b.ConfigureLogging(l => l.AddProvider(logs));
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthorityStore>();
                services.AddSingleton(script.Store);
                services.RemoveAll<IEnvelopeCapabilityObserver>();
                services.AddSingleton<EnvelopeCapabilityObserver>();
                services.AddSingleton<IEnvelopeCapabilityObserver>(p => floor = new FloorEvaluations(p.GetRequiredService<EnvelopeCapabilityObserver>()));
                services.RemoveAll<ICapabilityRefusalEvidenceObserver>();
                services.AddSingleton<CapabilityRefusalEvidenceObserver>();
                services.AddSingleton<ICapabilityRefusalEvidenceObserver>(p => evidence = new EvidenceOutcomes(p.GetRequiredService<CapabilityRefusalEvidenceObserver>()));
            });
        });
        using (factory.CreateClient())
        {
        }

        var recorder = factory.Services.GetRequiredService<CapabilityRefusalEvidenceRecorder>();
        _ = factory.Services.GetRequiredService<IEnvelopeCapabilityObserver>();
        return new Host(factory, new HttpClient(factory.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") }, script, floor!, evidence!, logs, recorder);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(Host host, EnvelopeCapabilityEnforcementHostTests.Captured sent, string[]? capability)
    {
        using var request = new HttpRequestMessage(sent.Method, sent.Uri);
        foreach (var (name, values) in sent.Headers.Where(h => h.Key != Header))
        {
            request.Headers.TryAddWithoutValidation(name, values);
        }

        if (capability is not null)
        {
            request.Headers.TryAddWithoutValidation(Header, capability);
        }

        request.Content = new ByteArrayContent(sent.Body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await host.Raw.SendAsync(request, Ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(Ct));
        return (response.StatusCode, document.RootElement.Clone());
    }

    /// <summary>The exact success path of each route (scope calls in order), with or without the one binding existence query.</summary>
    private static List<string> SuccessPath(string route, bool existenceQuery)
    {
        var path = new List<string> { "Begin", nameof(IAuthorityScope.LockOrganizationAsync) };
        if (existenceQuery)
        {
            path.Add(nameof(IAuthorityScope.OrganizationHasDataKeyBindingsAsync));
        }

        path.AddRange([nameof(IAuthorityScope.CountPendingArchiveAsync), nameof(IAuthorityScope.FindLatestJournalEntryAsync), "get_SearchIndex", "index.FindProjectionStateAsync", "index.BindOrganizationAsync"]);
        path.AddRange(route == "deep-verify"
            ? [nameof(IAuthorityScope.GetFieldSearchApprovalsAsync), "index.GetStoredRecordIndexAsync", "index.RecordDeepCheckAsync", nameof(IAuthorityScope.CommitAsync), "Dispose"]
            : ["index.GetCoveragesAsync", "index.GetFormEntriesAsync", "Dispose"]);
        return path;
    }

    private static async Task AssertAdmittedSuccessWithoutEvidenceAsync(Host host, EnvelopeCapabilityEnforcementHostTests.Captured sent, string[]? capability, int level, bool existenceQuery, string label)
    {
        var script = host.Script;
        script.Reset();
        var deepChecksBefore = script.CommittedDeepChecks.Count;
        var floorBefore = host.Floor.All.Count;
        var logsBefore = host.Logs.All.Count;

        var (status, body) = await SendAsync(host, sent, capability);

        // The capability check admitted the request, and the request completed successfully.
        Assert.True(status == HttpStatusCode.OK, $"{label}: status {(int)status} {body}");
        Assert.Equal([(script.Organization, level, EnvelopeCapabilityOutcomes.Admitted)], host.Floor.All.Skip(floorBefore).ToList());
        var result = Assert.Single(body.GetProperty("results").EnumerateArray());
        if (sent.Name == "deep-verify")
        {
            Assert.Equal(("Inconclusive", "ENTRY_CHANGED"), (result.GetProperty("unit_outcome").GetString(), result.GetProperty("outcome_code").GetString()));
        }
        else
        {
            Assert.Equal("NOT_COVERED", result.GetProperty("outcome").GetString());
        }

        // Exactly the legitimate transaction: one scope, the pinned success path, the expected write and nothing else.
        var scopes = script.Scopes();
        var only = Assert.Single(scopes);
        Assert.Equal(SuccessPath(sent.Name, existenceQuery), only.Value);
        Assert.Equal(sent.Name == "deep-verify" ? [(script.StoredEntry.SearchRecordEntryId, "ENTRY_CHANGED")] : [], script.CommittedDeepChecks.Skip(deepChecksBefore).ToList());

        // No evidence: no entry, no audit-revision change, no recorder outcome, no refusal log or evidence counter, no retry scheduled.
        Assert.Empty(script.Journal);
        Assert.Equal(12, script.State.AuditJournalRevision);
        Assert.True(host.Evidence.All.IsEmpty, label + ": the recorder reported " + string.Join(", ", host.Evidence.All.Select(e => e.Outcome)));
        Assert.DoesNotContain(host.Logs.All.Skip(logsBefore), l => RefusalEvents.Contains(l.EventId));
        Assert.Equal((0, 0, 0), (host.Recorder.AttemptsInWindow, host.Recorder.CachedCount, host.Recorder.PendingCount));
    }

    [Fact]
    public async Task SuccessfullyCompletedAdmittedRequestsProduceNoEvidenceTelemetryOrRetry()
    {
        // The real E3-M2f-1 builder-plane requests (deep-sweep verify and backfill), captured with their workload credentials.
        var capture = new EnvelopeCapabilityEnforcementHostTests.Script();
        var organization = capture.AddOrganization(bound: true);
        List<EnvelopeCapabilityEnforcementHostTests.Captured> requests;
        using (var capturing = EnvelopeCapabilityEnforcementHostTests.Authority(capture))
        {
            using var started = capturing.CreateClient();
            requests = (await EnvelopeCapabilityEnforcementHostTests.CaptureSevenAsync(capturing, capture, organization)).Where(r => r.Name is "deep-verify" or "backfill").ToList();
        }

        Assert.Equal(2, requests.Count);

        // Evaluation counter of the real floor observer (tagged with the organization, so other tests cannot interfere).
        var counted = new ConcurrentQueue<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == EnvelopeCapabilityObserver.MeterName && instrument.Name == "envelope_capability_evaluations")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var values = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if (values.TryGetValue("organization_id", out var org) && org is Guid id && id == organization)
            {
                counted.Enqueue((string)values["outcome"]!);
            }
        });
        listener.Start();

        // Deployment floor 1: level 2 in the bound organization (no ledger read); admitted level 1 in the unbound organization.
        using (var host = Start(organization, "1"))
        {
            foreach (var sent in requests)
            {
                host.Script.Bound = true;
                await AssertAdmittedSuccessWithoutEvidenceAsync(host, sent, ["2"], 2, existenceQuery: false, "D=1 level 2 bound " + sent.Name);
                host.Script.Bound = false;
                await AssertAdmittedSuccessWithoutEvidenceAsync(host, sent, null, 1, existenceQuery: true, "D=1 admitted level 1 unbound " + sent.Name);
                await AssertAdmittedSuccessWithoutEvidenceAsync(host, sent, ["2"], 2, existenceQuery: false, "D=1 level 2 unbound " + sent.Name);
            }

            // No retry appears later either: the retry service runs every second.
            await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
            Assert.Equal((0, 0, 0), (host.Recorder.AttemptsInWindow, host.Recorder.CachedCount, host.Recorder.PendingCount));
            Assert.DoesNotContain(host.Script.Events, e => e.StartsWith(nameof(IAuthorityScope.SetCapabilityEvidenceLockTimeoutAsync), StringComparison.Ordinal));

            // Sensitivity control on the same host: without a declaration in the bound organization the floor refuses, the refusal is
            // recorded once (T2), the observer and the refusal log see it. So the checks above can observe evidence when it happens.
            foreach (var sent in requests)
            {
                host.Script.Bound = true;
                host.Script.Reset();
                var (status, body) = await SendAsync(host, sent, null);
                Assert.Equal((HttpStatusCode.Conflict, "CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT"), (status, body.GetProperty("error_code").GetString()));
                Assert.Contains(host.Script.Events, e => e.StartsWith(nameof(IAuthorityScope.SetCapabilityEvidenceLockTimeoutAsync), StringComparison.Ordinal));
            }

            Assert.Equal(2, host.Evidence.All.Count(e => e.Outcome == CapabilityRefusalEvidenceOutcome.Committed));
            Assert.Equal(2, host.Script.Journal.Count(e => e.JournalEventKind == CapabilityRefusalEvidence.EventKind));
            Assert.Equal(2, host.Logs.All.Count(l => l.EventId == 9250));
        }

        // Deployment floor 2: level 2 in the bound organization is admitted without a ledger read.
        using (var host = Start(organization, "2"))
        {
            host.Script.Bound = true;
            foreach (var sent in requests)
            {
                await AssertAdmittedSuccessWithoutEvidenceAsync(host, sent, ["2"], 2, existenceQuery: false, "D=2 level 2 bound " + sent.Name);
            }

            Assert.True(host.Evidence.All.IsEmpty);
        }

        listener.RecordObservableInstruments();
        Assert.Equal(Enumerable.Repeat(EnvelopeCapabilityOutcomes.Admitted, 8).Concat(Enumerable.Repeat(EnvelopeCapabilityOutcomes.Refused, 2)).Order(StringComparer.Ordinal), counted.Order(StringComparer.Ordinal));
    }
}
