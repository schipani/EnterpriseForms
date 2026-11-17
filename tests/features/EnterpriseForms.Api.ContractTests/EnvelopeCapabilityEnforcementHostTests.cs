using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Catalogue;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Organizations;
using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Application.Features.Search.FieldSearch;
using EnterpriseForms.Application.Features.Search.IndexBuilder;
using EnterpriseForms.Application.Features.SecurityOperations.Incidents;
using EnterpriseForms.Infrastructure.Authority;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2f-2 (docs/security/E3_M2F_CONTRACT_CLOSURE.md §3, §6, §7, §9) on the real Authority host pipeline (authentication, endpoints,
/// header parsing, exception mapping) with a scripted store that answers only the organization lock, the Release B fence, the binding
/// existence query and the census snapshot lookup, and records every scope call. For each of the seven content endpoints:
/// <list type="bullet">
/// <item>without a valid level-2 declaration, in an organization with a binding, the response is the fixed <c>409
/// CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT</c> and the scope made no call after the existence query (nothing was resolved, pinned,
/// verified or written);</item>
/// <item>the real E3-M2f-1 clients (level 2) pass the check without reading the ledger and continue to the next step;</item>
/// <item>an unbound organization admits level 1 after one existence query (CAP-S5 compatibility), the deployment floor 2 refuses it
/// without reading the ledger, and an invalid deployment value refuses the start (M2F-29).</item>
/// </list>
/// The business side maps the refusal to a terminal service-unavailable condition (M2F-28).
/// </summary>
public sealed class EnvelopeCapabilityEnforcementHostTests
{
    private const string WorkloadKey = "d29ya2xvYWQtZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";
    private const string WorkloadIssuer = "enterpriseforms-business-api";
    private const string WorkloadAudience = "authorization-authority";
    private const string UserAssertionKey = "dGVzdC1pZHAtZGV2LW9ubHktc2lnbmluZy1rZXktMDAwMDAwMDAwMA==";
    private const string UserAssertionIssuer = "https://test-idp.enterpriseforms.invalid";
    private const string UserAssertionAudience = "enterpriseforms";
    private const string VerifierKey = "dmVyaWZpZXItdGVzdC1vbmx5LXNoYXJlZC1rZXktMDAwMDAwMDAw";
    private const string VerifierIssuer = "enterpriseforms-field-protection-verifier";
    private const string VerifierAudience = "authorization-authority-verifier";
    private const string BuilderIssuer = "enterpriseforms-search-index-builder";
    private const string BuilderAudience = "authorization-authority-search-index-builder";
    private const string Header = ClientEnvelopeCapability.HeaderName;
    private static readonly string BuilderKey = Convert.ToBase64String(Encoding.ASCII.GetBytes("search-index-builder-e3m2f2-test-only-key-0000"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- scripted store ----

    /// <summary>What the scripted store knows: organizations, bound organizations, the census snapshot, and every scope's calls.</summary>
    public sealed class Script
    {
        public ConcurrentDictionary<Guid, OrganizationAuthorizationState> Organizations { get; } = new();

        public ConcurrentDictionary<Guid, bool> Bound { get; } = new();

        public SearchCatalogueSnapshot? Snapshot { get; set; }

        public ConcurrentQueue<List<string>> Scopes { get; } = new();

        public IAuthorityStore Store { get; }

        public Script()
        {
            var store = DispatchProxy.Create<IAuthorityStore, ScriptedStore>();
            ((ScriptedStore)(object)store).Script = this;
            Store = store;
        }

        public Guid AddOrganization(bool bound)
        {
            var id = Guid.CreateVersion7();
            Organizations[id] = new OrganizationAuthorizationState { OrganizationId = id, AuthorizationRevision = 7, ProjectionAppliedRevision = 7 };
            if (bound)
            {
                Bound[id] = true;
            }

            return id;
        }

        /// <summary>The calls of every scope that locked <paramref name="organization"/> since the last <see cref="Reset"/>.</summary>
        public List<List<string>> ScopesOf(Guid organization) => Scopes.Where(s => { lock (s) { return s.Contains("LockOrganizationAsync:" + organization.ToString("N")); } }).ToList();

        public void Reset() => Scopes.Clear();
    }

    public class ScriptedStore : DispatchProxy
    {
        public Script Script { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IAuthorityStore.BeginScopeAsync))
            {
                var scope = Create<IAuthorityScope, ScriptedScope>();
                var calls = new List<string>();
                ((ScriptedScope)(object)scope).Script = Script;
                ((ScriptedScope)(object)scope).Calls = calls;
                Script.Scopes.Enqueue(calls);
                return Task.FromResult(scope);
            }

            throw new NpgsqlException("scripted store: " + targetMethod?.Name + " is not available");
        }
    }

    public class ScriptedScope : DispatchProxy
    {
        public Script Script { get; set; } = null!;

        public List<string> Calls { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (name == nameof(IAsyncDisposable.DisposeAsync))
            {
                return ValueTask.CompletedTask;
            }

            lock (Calls)
            {
                Calls.Add(name is nameof(IAuthorityScope.LockOrganizationAsync) or nameof(IAuthorityScope.OrganizationHasDataKeyBindingsAsync) ? name + ":" + ((Guid)args![0]!).ToString("N") : name);
            }

            return name switch
            {
                nameof(IAuthorityScope.LockOrganizationAsync) => Task.FromResult(Script.Organizations.GetValueOrDefault((Guid)args![0]!)),
                nameof(IAuthorityScope.RequireContentAcceptanceFloorAsync) => Task.CompletedTask,
                nameof(IAuthorityScope.OrganizationHasDataKeyBindingsAsync) => Task.FromResult(Script.Bound.ContainsKey((Guid)args![0]!)),
                nameof(IAuthorityScope.FindCatalogueSnapshotAsync) => Task.FromResult(Script.Snapshot),
                _ => throw new NpgsqlException("scripted scope: " + name + " is not available"),
            };
        }
    }

    private static WebApplicationFactory<AuthorizationAuthority.Api.Program> Authority(Script script, string? deploymentFloor = null) =>
        new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:AuthorityDb", "Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none;Timeout=2");
            b.UseSetting("Identity:Verifier:Issuer", VerifierIssuer);
            b.UseSetting("Identity:Verifier:Audience", VerifierAudience);
            b.UseSetting("Identity:Verifier:SharedKeyBase64", VerifierKey);
            b.UseSetting("Identity:SearchIndexBuilder:Issuer", BuilderIssuer);
            b.UseSetting("Identity:SearchIndexBuilder:Audience", BuilderAudience);
            b.UseSetting("Identity:SearchIndexBuilder:SharedKeyBase64", BuilderKey);
            if (deploymentFloor is not null)
            {
                b.UseSetting(EnvelopeCapabilityFloor.ConfigurationKey, deploymentFloor);
            }

            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthorityStore>();
                services.AddSingleton(script.Store);

                // The builder plane answers only in Dark mode (checked before its lock). Dark composition needs a pinned seal key, so the
                // test swaps in Dark options for the builder service alone; the scripted store refuses everything after the check anyway.
                services.RemoveAll<SearchIndexOptions>();
                services.AddSingleton(new SearchIndexOptions { Mode = SearchIndexMode.Dark });
            });
        });

    // ---- the seven content requests, as the real E3-M2f-1 clients send them ----

    private sealed record Captured(string Name, HttpMethod Method, Uri Uri, Dictionary<string, string[]> Headers, byte[] Body);

    private sealed class Capturing(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<Captured> Requests { get; } = [];

        public string Name { get; set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new Captured(Name, request.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()), body));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Header-stripping proxy in front of the Authority (what an intermediary that drops custom headers would do).</summary>
    private sealed class Stripping(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove(Header);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task<List<Captured>> CaptureSevenAsync(WebApplicationFactory<AuthorizationAuthority.Api.Program> authority, Script script, Guid organization)
    {
        var capturing = new Capturing(authority.Server.CreateHandler());
        var user = new DelegatedUserContext(UserAssertionIssuer, "e3-m2f2-user", organization, UserAssertion());
        var record = Guid.CreateVersion7();
        var operation = Guid.CreateVersion7();
        script.Snapshot = new SearchCatalogueSnapshot
        {
            SearchCatalogueSnapshotId = Guid.CreateVersion7(),
            OrganizationId = organization,
            SnapshotScopeKind = CatalogueScopeKinds.MigrationVerification,
            RequestingWorkloadName = "field-protection-verifier",
            ScopingSecurityCommandId = Guid.CreateVersion7(),
            FormDefinitionId = Guid.CreateVersion7(),
            SnapshotExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        };
        StagedContentItem[] observed = [new(ManifestItemKind.RecordHeader, record, Convert.ToBase64String(Encoding.ASCII.GetBytes("observed")), Convert.ToBase64String(new byte[32]))];
        var business = BusinessClient(capturing);
        var verifier = new ProtectionVerifierHttpClient(Factory(capturing), Options.Create(new ProtectionVerifierOptions { Issuer = VerifierIssuer, Audience = VerifierAudience, SharedKeyBase64 = VerifierKey }), Options.Create(new AuthorityClientOptions { BaseAddress = "http://localhost/" }), TimeProvider.System);
        var builder = new SearchIndexBuilderHttpClient(Factory(capturing), Options.Create(new SearchIndexBuilderOptions { Issuer = BuilderIssuer, Audience = BuilderAudience, SharedKeyBase64 = BuilderKey }), Options.Create(new AuthorityClientOptions { BaseAddress = "http://localhost/" }), TimeProvider.System);

        async Task Send(string name, Func<Task> call)
        {
            capturing.Name = name;
            try
            {
                await call();
            }
            catch (AuthorityClientException)
            {
            }
        }

        await Send("prepare", () => business.PrepareContentChangeAsync(user, new EnterpriseForms.Application.Common.Authority.PrepareContentChangeRequest(organization, record, operation, 7, 1, Convert.ToBase64String(new byte[32]),
            [new CandidateContentItem(ManifestItemKind.RecordHeader, record, Convert.ToBase64String(Encoding.ASCII.GetBytes("candidate")))], null, observed), "e3-m2f2-host-key-0001", Ct));
        await Send("commit", () => business.CommitContentChangeAsync(user, operation, new CommitContentChangeRequest(organization, record, observed), Ct));
        await Send("verify", () => business.VerifyRecordProjectionAsync(user, new VerifyRecordProjectionRequest(organization, record, ProtectedAction.Read, observed), Ct));
        await Send("batch-verify", () => business.VerifyRecordProjectionsAsync(user, new VerifyRecordProjectionsRequest(organization, [new BatchVerificationItem(record, operation, observed)]), Ct));
        await Send("census", () => verifier.TakeCensusAsync(script.Snapshot.SearchCatalogueSnapshotId, [new ProtectionCensusItem(record, operation, observed)], null, Ct));
        await Send("backfill", () => builder.BackfillAsync(organization, Guid.CreateVersion7(), [new SearchIndexBackfillItem(record, operation, observed)], Ct));
        await Send("deep-verify", () => builder.DeepVerifyAsync(organization, [new SearchIndexBackfillItem(record, operation, observed)], Ct));
        Assert.Equal(7, capturing.Requests.Count);
        Assert.All(capturing.Requests, r => Assert.Equal(["2"], r.Headers[Header])); // the E3-M2f-1 clients
        return capturing.Requests;
    }

    private static async Task<(HttpStatusCode Status, string? Code, bool? Retryable, string? Detail)> ReplayAsync(HttpClient raw, Captured sent, string[]? capability)
    {
        using var replay = new HttpRequestMessage(sent.Method, sent.Uri);
        foreach (var (name, values) in sent.Headers.Where(h => h.Key != Header))
        {
            replay.Headers.TryAddWithoutValidation(name, values);
        }

        if (capability is not null)
        {
            replay.Headers.TryAddWithoutValidation(Header, capability);
        }

        replay.Content = new ByteArrayContent(sent.Body);
        replay.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await raw.SendAsync(replay, Ct);
        var body = await response.Content.ReadAsByteArrayAsync(Ct);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return (response.StatusCode, root.TryGetProperty("error_code", out var c) ? c.GetString() : null, root.TryGetProperty("retryable", out var r) ? r.GetBoolean() : null, root.TryGetProperty("detail", out var d) ? d.GetString() : null);
        }
        catch (JsonException)
        {
            return (response.StatusCode, null, null, null);
        }
    }

    /// <summary>Header instances that are not a valid level-2 declaration (null: no header).</summary>
    private static readonly string[]?[] BelowLevelTwo = [null, ["1"], ["3"], ["02"], [""], ["two"], ["1", "2"], ["2", "2"], ["1,2"]];

    private static readonly string[] StepsBeforeTheCheck = ["FindCatalogueSnapshotAsync", "RequireContentAcceptanceFloorAsync"];

    [Fact]
    public async Task EveryContentEndpointRefusesAnUndeclaredOrUnknownCapabilityInABoundOrganizationBeforeAnyOtherStep()
    {
        var script = new Script();
        var organization = script.AddOrganization(bound: true);
        using var authority = Authority(script);
        using var started = authority.CreateClient();
        var requests = await CaptureSevenAsync(authority, script, organization);
        using var raw = new HttpClient(authority.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };

        foreach (var sent in requests)
        {
            foreach (var capability in BelowLevelTwo)
            {
                script.Reset();
                var outcome = await ReplayAsync(raw, sent, capability);
                Assert.Equal((HttpStatusCode.Conflict, "CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT", (bool?)false, EnvelopeCapabilityFloor.RefusalDetail), outcome);
                var scope = Assert.Single(script.ScopesOf(organization));
                var steps = scope.Select(c => c.Split(':')[0]).ToList();
                // Only the lock, the Release B fence (Prepare, Commit) or the snapshot lookup (census, before the lock), then the existence query: nothing after it.
                Assert.Equal("OrganizationHasDataKeyBindingsAsync", steps[^1]);
                Assert.Equal("LockOrganizationAsync", steps.Where(s => s != "FindCatalogueSnapshotAsync").First());
                Assert.All(steps, s => Assert.Contains(s, StepsBeforeTheCheck.Concat(["LockOrganizationAsync", "OrganizationHasDataKeyBindingsAsync"])));
                Assert.Equal(sent.Name is "prepare" or "commit", steps.Contains("RequireContentAcceptanceFloorAsync"));
            }

            // The real level-2 clients pass the check without reading the ledger and reach the next step (here an unavailable store).
            script.Reset();
            var admitted = await ReplayAsync(raw, sent, ["2"]);
            Assert.Equal((HttpStatusCode.ServiceUnavailable, "AUTHORITY_NOT_READY"), (admitted.Status, admitted.Code));
            var calls = Assert.Single(script.ScopesOf(organization));
            Assert.DoesNotContain(calls, c => c.StartsWith("OrganizationHasDataKeyBindingsAsync", StringComparison.Ordinal));
            Assert.True(calls.Count >= 2, sent.Name + ": the request continued after the lock");
        }
    }

    [Fact]
    public async Task AnUnboundOrganizationAdmitsLevelOneAfterOneExistenceQueryAndTheDeploymentFloorTwoRefusesItWithoutOne()
    {
        var script = new Script();
        var organization = script.AddOrganization(bound: false);
        using (var authority = Authority(script))
        {
            using var started = authority.CreateClient();
            var requests = await CaptureSevenAsync(authority, script, organization);
            using var raw = new HttpClient(authority.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };
            foreach (var sent in requests)
            {
                script.Reset();
                var outcome = await ReplayAsync(raw, sent, null);
                Assert.Equal((HttpStatusCode.ServiceUnavailable, "AUTHORITY_NOT_READY"), (outcome.Status, outcome.Code));
                var calls = Assert.Single(script.ScopesOf(organization));
                Assert.Single(calls, c => c.StartsWith("OrganizationHasDataKeyBindingsAsync", StringComparison.Ordinal));
                Assert.True(calls.IndexOf(calls.Single(c => c.StartsWith("OrganizationHasDataKeyBindingsAsync", StringComparison.Ordinal))) < calls.Count - 1, sent.Name + ": admitted, the request continued");
            }
        }

        using (var authority = Authority(script, deploymentFloor: "2"))
        {
            using var started = authority.CreateClient();
            var floor = authority.Services.GetRequiredService<EnvelopeCapabilityFloor>();
            Assert.Equal(2, floor.DeploymentFloor);
            Assert.Same(floor, authority.Services.GetRequiredService<ContentService>().CapabilityFloor); // one shared instance (CAP-O7)
            var requests = await CaptureSevenAsync(authority, script, organization);
            using var raw = new HttpClient(authority.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };
            foreach (var sent in requests)
            {
                script.Reset();
                Assert.Equal((HttpStatusCode.Conflict, "CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT", (bool?)false, EnvelopeCapabilityFloor.RefusalDetail), await ReplayAsync(raw, sent, ["1"]));
                Assert.DoesNotContain(Assert.Single(script.ScopesOf(organization)), c => c.StartsWith("OrganizationHasDataKeyBindingsAsync", StringComparison.Ordinal)); // C < D: no ledger read
                script.Reset();
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await ReplayAsync(raw, sent, ["2"])).Status);
            }
        }
    }

    [Theory]
    [InlineData("3")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(" 2")]
    [InlineData("02")]
    [InlineData("two")]
    public void M2F29AnInvalidDeploymentFloorRefusesTheAuthorityStart(string configured)
    {
        using var authority = Authority(new Script(), configured);
        var ex = Assert.ThrowsAny<Exception>(() => authority.CreateClient());
        Assert.Contains(EnvelopeCapabilityFloor.ConfigurationKey, Flatten(ex), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    public void M2F29TheComposedHostUsesTheConfiguredDeploymentFloorEverywhere(string? configured, int expected)
    {
        using var authority = Authority(new Script(), configured);
        using var started = authority.CreateClient();
        var floor = authority.Services.GetRequiredService<EnvelopeCapabilityFloor>();
        Assert.Equal(expected, floor.DeploymentFloor);
        Assert.Equal("envelope capability floor enforced; deployment floor " + expected, floor.Description);
        Assert.Same(floor, authority.Services.GetRequiredService<ContentService>().CapabilityFloor);
        Assert.NotNull(authority.Services.GetRequiredService<ProtectionReconciliationService>());
        Assert.NotNull(authority.Services.GetRequiredService<SearchIndexBuilderService>());
    }

    // ---- M2F-28: the business keeps the refusal terminal ----

    [Fact]
    public async Task M2F28TheBusinessSeesANonRetryableRefusalAndAnswersUsersWithAServiceUnavailableCondition()
    {
        var script = new Script();
        var organization = script.AddOrganization(bound: true);
        using var authority = Authority(script);
        using var started = authority.CreateClient();
        var business = BusinessClient(new Stripping(authority.Server.CreateHandler()));
        var user = new DelegatedUserContext(UserAssertionIssuer, "e3-m2f2-user", organization, UserAssertion());

        var refusal = await Assert.ThrowsAsync<AuthorityClientException>(() => business.VerifyRecordProjectionAsync(user, new VerifyRecordProjectionRequest(organization, Guid.CreateVersion7(), ProtectedAction.Read, []), Ct));
        Assert.Equal((EnvelopeCapabilityRefusal.ErrorCode, 409, false), (refusal.ErrorCode, refusal.HttpStatusCode, refusal.Retryable));
        Assert.True(EnvelopeCapabilityRefusal.Is(refusal));

        // Never a retry, an abandoned operation, an unusable key (fresh operation), a migration retry, a search retry or an incident.
        Assert.False(CreateFormRecordHandler.IsAbandonedOperation(refusal.ErrorCode));
        Assert.False(CreateFormRecordHandler.IsUnusableOperationKey(refusal.ErrorCode));
        Assert.False(FieldProtectionMigrationService.IsRetryableConflict(refusal.ErrorCode));
        Assert.False(FieldSearchRetryPolicy.IsRetryable(refusal));
        Assert.Null(SecurityIncidentClassifier.FromAuthorityError(refusal));

        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        Assert.True(await new BusinessProblemHandler().TryHandleAsync(http, refusal, Ct));
        http.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(http.Response.Body, cancellationToken: Ct);
        Assert.Equal(503, http.Response.StatusCode);
        Assert.Equal("SERVICE_UNAVAILABLE", problem.RootElement.GetProperty("title").GetString());
        Assert.False(problem.RootElement.GetProperty("retryable").GetBoolean());
        var text = problem.RootElement.GetRawText();
        Assert.DoesNotContain(EnvelopeCapabilityRefusal.ErrorCode, text, StringComparison.Ordinal);
        Assert.DoesNotContain("capability", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(organization.ToString("D"), text, StringComparison.Ordinal);
    }

    [Fact]
    public void M2F28TheBusinessCodeIsTheAuthorityCodeAndOtherCodesAreNotTheRefusal()
    {
        Assert.Equal(AuthorizationAuthority.Application.Common.AuthorityErrorCodes.ClientEnvelopeCapabilityInsufficient, EnvelopeCapabilityRefusal.ErrorCode);
        Assert.False(EnvelopeCapabilityRefusal.Is(new AuthorityClientException("OPERATION_RULES_OUTDATED", 409, false)));
        Assert.False(EnvelopeCapabilityRefusal.Is(new AuthorityClientException("client_envelope_capability_insufficient", 409, false)));
        Assert.True(EnvelopeCapabilityRefusal.Is(new AuthorityClientException(EnvelopeCapabilityRefusal.ErrorCode, 409, false)));
    }

    // ---- helpers ----

    private static string Flatten(Exception ex)
    {
        var text = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            text.Append(e.Message).Append(" | ");
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    text.Append(Flatten(inner));
                }
            }
        }

        return text.ToString();
    }

    private static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        foreach (var name in new[] { AuthorityHttpClient.HttpClientName, ProtectionVerifierHttpClient.HttpClientName, SearchIndexBuilderHttpClient.HttpClientName })
        {
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);
        }

        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private static AuthorityHttpClient BusinessClient(HttpMessageHandler handler) =>
        new(Factory(handler), Options.Create(new AuthorityClientOptions { BaseAddress = "http://localhost/", WorkloadIssuer = WorkloadIssuer, WorkloadAudience = WorkloadAudience, WorkloadSharedKeyBase64 = WorkloadKey }), TimeProvider.System);

    private static string UserAssertion() =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = UserAssertionIssuer,
            Audience = UserAssertionAudience,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddSeconds(-5),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "e3-m2f2-user" },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(UserAssertionKey)), SecurityAlgorithms.HmacSha256),
        });
}
