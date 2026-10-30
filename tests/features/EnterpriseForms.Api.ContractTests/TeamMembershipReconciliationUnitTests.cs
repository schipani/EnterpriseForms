using System.Net;
using System.Text.Json;
using EnterpriseForms.Api;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.Teams.MembershipReconciliation;
using EnterpriseForms.Domain.Features.Teams;
using EnterpriseForms.Infrastructure.Authority;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C8 business side, without a stack: package §1.3 validation of every AC-9 answer (RC-22), D8-16 bounds and the
/// Testing-only shortening (RC-28), the Worker-only reconciler port with its C-1/C-2 registration guards (RC-26, RC-28), and
/// the client's typed failure mapping (RC-25, RC-27). The live behaviour runs in the Stage B RC suite.
/// </summary>
public sealed class TeamMembershipReconciliationUnitTests
{
    private const string WorkloadKey = "d29ya2xvYWQtZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";
    private const string VerifierKey = "dmVyaWZpZXItdGVzdC1vbmx5LXNoYXJlZC1rZXktMDAwMDAwMDAw";
    private const string ReconcilerKey = "cmVjb25jaWxlci10ZXN0LW9ubHktc2hhcmVkLWtleS0wMDAwMDA=";

    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Team = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Command = Guid.CreateVersion7();
    private static readonly Guid Correlation = Guid.CreateVersion7();

    private static AuthorityCommandOperation Operation(string kind = TeamMembershipCommandKinds.AddTeamMember, Guid? command = null, long? revision = null) => new()
    {
        AuthorityCommandOperationId = Guid.CreateVersion7(),
        TenantId = Tenant,
        TeamId = Team,
        MemberUserId = Member,
        CommandKindName = kind,
        AuthorityIdempotencyKey = TeamMembershipCommandKinds.KeyPrefix(kind) + Guid.NewGuid().ToString("D"),
        AuthorityCommandId = command,
        CommittedAuthorizationRevision = revision,
    };

    private static SecurityCommandOutcomeAnswer Effective(long committed = 3, long applied = 4, long authorization = 5, long e = 3, bool member = true) =>
        new(true, Correlation, Tenant, Command, "AddTeamMember", "Effective", committed, null, Team, Member,
            new SecurityCommandOutcomeOrganizationAnswer(authorization, applied, "AwaitingProjection", false),
            new SecurityCommandOutcomeMembershipAnswer(applied, member, e, authorization > applied));

    [Fact]
    public void AGenuineEffectiveAnswerAndTheUniformNotFoundAreValid()
    {
        Assert.True(TeamMembershipOutcomeValidator.Validate(Operation(), Correlation, Effective()).IsValid);
        Assert.True(TeamMembershipOutcomeValidator.Validate(Operation(command: Command, revision: 3), Correlation, Effective()).IsValid);
        Assert.True(TeamMembershipOutcomeValidator.Validate(Operation(), Correlation, new SecurityCommandOutcomeAnswer(false, Correlation, Tenant, null, null, null, null, null, null, null, null, null)).IsValid);
        var rejected = new SecurityCommandOutcomeAnswer(true, Correlation, Tenant, Command, "AddTeamMember", "Rejected", null, "FORBIDDEN", Team, Member, new SecurityCommandOutcomeOrganizationAnswer(5, 5, "Ready", false), null);
        Assert.True(TeamMembershipOutcomeValidator.Validate(Operation(), Correlation, rejected).IsValid);
        var quarantined = Effective() with { OrganizationState = new SecurityCommandOutcomeOrganizationAnswer(5, 4, "Quarantined", true), Membership = null };
        Assert.True(TeamMembershipOutcomeValidator.Validate(Operation(), Correlation, quarantined).IsValid);
    }

    public static TheoryData<string, SecurityCommandOutcomeAnswer, OutcomeValidationVerdict> Forged()
    {
        var good = Effective();
        return new TheoryData<string, SecurityCommandOutcomeAnswer, OutcomeValidationVerdict>
        {
            { "correlation", good with { CorrelationId = Guid.CreateVersion7() }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "organization", good with { OrganizationId = Guid.CreateVersion7() }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "kind", good with { CommandKind = "RemoveTeamMember" }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "no command id", good with { SecurityCommandId = null }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "team", good with { TeamId = Guid.CreateVersion7() }, OutcomeValidationVerdict.OperationMismatch },
            { "member", good with { MemberUserId = Guid.CreateVersion7() }, OutcomeValidationVerdict.OperationMismatch },
            { "unknown state", good with { CommandState = "Pending" }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "no organization state", good with { OrganizationState = null }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "applied above authorization", good with { OrganizationState = new SecurityCommandOutcomeOrganizationAnswer(3, 4, "Ready", false) }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "committed zero", good with { CommittedAuthorizationRevision = 0 }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "committed above authorization", good with { CommittedAuthorizationRevision = 6 }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "effective above applied", Effective(committed: 5, applied: 4, authorization: 5, e: 4), OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "awaiting at or below applied", good with { CommandState = "CommittedAwaitingProjection" }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "membership missing", good with { Membership = null }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "membership while quarantined", good with { OrganizationState = new SecurityCommandOutcomeOrganizationAnswer(5, 4, "Quarantined", true) }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "evaluated at another revision", good with { Membership = new SecurityCommandOutcomeMembershipAnswer(3, true, 3, true) }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "membership revision above evaluated", good with { Membership = new SecurityCommandOutcomeMembershipAnswer(4, true, 5, true) }, OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "effective older than its membership revision", Effective(committed: 3, e: 2), OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "not found carrying data", new SecurityCommandOutcomeAnswer(false, Correlation, Tenant, Command, null, null, null, null, null, null, null, null), OutcomeValidationVerdict.InvalidAuthorityResponse },
            { "rejected with a revision", good with { CommandState = "Rejected", Membership = null }, OutcomeValidationVerdict.InvalidAuthorityResponse },
        };
    }

    [Theory]
    [MemberData(nameof(Forged))]
    public void RC22EveryPackageRuleViolationIsRefused(string rule, SecurityCommandOutcomeAnswer answer, OutcomeValidationVerdict expected)
    {
        var verdict = TeamMembershipOutcomeValidator.Validate(Operation(), Correlation, answer);
        Assert.False(verdict.IsValid, rule);
        Assert.Equal(expected, verdict.Verdict);
    }

    [Fact]
    public void RC22AnAnswerForAnotherCommandOrRevisionThanTheOneRecordedIsAMismatch()
    {
        Assert.Equal(OutcomeValidationVerdict.OperationMismatch, TeamMembershipOutcomeValidator.Validate(Operation(command: Guid.CreateVersion7()), Correlation, Effective()).Verdict);
        Assert.Equal(OutcomeValidationVerdict.OperationMismatch, TeamMembershipOutcomeValidator.Validate(Operation(command: Command, revision: 2), Correlation, Effective()).Verdict);
        var rejected = new SecurityCommandOutcomeAnswer(true, Correlation, Tenant, Command, "AddTeamMember", "Rejected", null, "FORBIDDEN", Team, Member, new SecurityCommandOutcomeOrganizationAnswer(5, 5, "Ready", false), null);
        Assert.Equal(OutcomeValidationVerdict.InvalidAuthorityResponse, TeamMembershipOutcomeValidator.Validate(Operation(command: Command, revision: 3), Correlation, rejected).Verdict);
    }

    [Fact]
    public void KeyPrefixesMatchTheirKindsOnly()
    {
        Assert.True(TeamMembershipCommandKinds.PrefixMatches(TeamMembershipCommandKinds.AddTeamMember, "team-add:abc12345"));
        Assert.False(TeamMembershipCommandKinds.PrefixMatches(TeamMembershipCommandKinds.AddTeamMember, "team-remove:abc12345"));
        Assert.False(TeamMembershipCommandKinds.PrefixMatches(TeamMembershipCommandKinds.RemoveTeamMember, "team-add:abc12345"));
        Assert.False(TeamMembershipCommandKinds.PrefixMatches("RegisterTeam", "team-add:abc12345"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void RC28TheDefaultsAreValidEverywhere(string environment) => new TeamMembershipReconciliationOptions().Validate(environment, 10);

    [Theory]
    [InlineData("InFlightWindowSeconds", 6)]
    [InlineData("BackoffSeconds", 1)]
    [InlineData("BackoffCapSeconds", 3)]
    [InlineData("NeedsAttentionSeconds", 20)]
    [InlineData("LateCommitSweepIntervalSeconds", 5)]
    public void RC28ShortenedWindowsAreAcceptedInTestingAndRefusedElsewhere(string setting, int value)
    {
        TeamMembershipReconciliationOptions Shortened()
        {
            var options = new TeamMembershipReconciliationOptions { BackoffSeconds = 1, BackoffCapSeconds = 300 };
            typeof(TeamMembershipReconciliationOptions).GetProperty(setting)!.SetValue(options, value);
            return options;
        }

        Shortened().Validate("Testing", 10);
        foreach (var environment in new[] { "Production", "Staging", "Development" })
        {
            var failure = Assert.Throws<InvalidOperationException>(() => Shortened().Validate(environment, 10));
            Assert.Contains("only in Testing", failure.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("InFlightWindowSeconds", 3601)]
    [InlineData("InFlightWindowSeconds", 29)] // < 3 × the 10 s Authority client timeout
    [InlineData("BackoffCapSeconds", 1801)]
    [InlineData("NeedsAttentionSeconds", 86401)]
    [InlineData("ConsecutiveErrorBudget", 4)]
    [InlineData("ConsecutiveErrorBudget", 101)]
    [InlineData("LateCommitSweepDays", 0)]
    [InlineData("LateCommitSweepDays", 31)]
    [InlineData("RetentionDays", 89)]
    [InlineData("ApiProjectionWaitSeconds", 16)]
    public void RC28OutOfBoundsValuesRefuseToStartInProduction(string setting, int value)
    {
        var options = new TeamMembershipReconciliationOptions();
        typeof(TeamMembershipReconciliationOptions).GetProperty(setting)!.SetValue(options, value);
        Assert.Throws<InvalidOperationException>(() => options.Validate("Production", 10));
    }

    [Fact]
    public void BackoffGrowsAndIsCapped()
    {
        var options = new TeamMembershipReconciliationOptions();
        Assert.Equal(TimeSpan.FromSeconds(5), options.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(10), options.Backoff(2));
        Assert.Equal(TimeSpan.FromSeconds(300), options.Backoff(50));
        Assert.Equal(TimeSpan.FromSeconds(300), options.Backoff(int.MaxValue));
    }

    [Fact]
    public void RC26TheBusinessApiNeverRegistersTheReconcilerEvenWithItsCredentialInConfiguration()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("TeamMembershipReconciler:Issuer", "enterpriseforms-team-membership-reconciler");
            b.UseSetting("TeamMembershipReconciler:Audience", "authorization-authority-reconciler");
            b.UseSetting("TeamMembershipReconciler:SharedKeyBase64", ReconcilerKey);
        });
        Assert.Null(api.Services.GetService<ITeamMembershipOutcomeAuthority>());
        Assert.Null(api.Services.GetService<TeamMembershipReconciliationService>());
    }

    [Theory]
    [InlineData("partial", "Development", "configured completely")]
    [InlineData("workload-key", "Development", "own key and issuer")]
    [InlineData("verifier-key", "Development", "own key and issuer")]
    [InlineData("workload-issuer", "Development", "own key and issuer")]
    [InlineData("verifier-issuer", "Development", "own key and issuer")]
    [InlineData("http", "Production", "client certificate")]
    [InlineData("no-certificate", "Production", "client certificate")]
    public void C1C2TheWorkerRegistrationRefusesAPartialReusedOrUnprotectedCredential(string variant, string environment, string message)
    {
        var settings = new Dictionary<string, string?>
        {
            ["TrustAuthority:BaseAddress"] = variant == "http" ? "http://authority.invalid:8080" : "https://authority.invalid:8443",
            ["TrustAuthority:WorkloadIssuer"] = "enterpriseforms-business-api",
            ["TrustAuthority:WorkloadSharedKeyBase64"] = WorkloadKey,
            ["ProtectionVerifier:Issuer"] = "enterpriseforms-field-protection-verifier",
            ["ProtectionVerifier:Audience"] = "authorization-authority-verifier",
            ["ProtectionVerifier:SharedKeyBase64"] = VerifierKey,
            ["TeamMembershipReconciler:Issuer"] = variant switch
            {
                "workload-issuer" => "enterpriseforms-business-api",
                "verifier-issuer" => "enterpriseforms-field-protection-verifier",
                _ => "enterpriseforms-team-membership-reconciler",
            },
        };
        if (variant != "partial")
        {
            settings["TeamMembershipReconciler:Audience"] = "authorization-authority-reconciler";
            settings["TeamMembershipReconciler:SharedKeyBase64"] = variant switch { "workload-key" => WorkloadKey, "verifier-key" => VerifierKey, _ => ReconcilerKey };
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var failure = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddTeamMembershipReconciler(configuration, environment));
        Assert.Contains(message, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnconfiguredReconcilerFailsClosedWithoutContactingTheAuthority()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var client = Client(handler, configured: false);
        Assert.False(client.IsConfigured);
        var failure = await Assert.ThrowsAsync<ReconcilerLookupException>(() => client.LookupAsync(Tenant, "team-add:abcdefgh", "AddTeamMember", Correlation, TestContext.Current.CancellationToken));
        Assert.Equal(ReconcilerLookupFailure.NotConfigured, failure.Failure);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ReconcilerLookupFailure.CredentialRefused)]
    [InlineData(HttpStatusCode.Forbidden, ReconcilerLookupFailure.CredentialRefused)]
    [InlineData(HttpStatusCode.TooManyRequests, ReconcilerLookupFailure.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ReconcilerLookupFailure.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, ReconcilerLookupFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, ReconcilerLookupFailure.Refused)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ReconcilerLookupFailure.Refused)]
    public async Task RC25RC27EveryNonSuccessIsATypedFailure(HttpStatusCode status, ReconcilerLookupFailure expected)
    {
        var handler = new StubHandler(status, "{}", retryAfterSeconds: status == HttpStatusCode.TooManyRequests ? 7 : null);
        var failure = await Assert.ThrowsAsync<ReconcilerLookupException>(() => Client(handler).LookupAsync(Tenant, "team-add:abcdefgh", "AddTeamMember", Correlation, TestContext.Current.CancellationToken));
        Assert.Equal(expected, failure.Failure);
        if (expected == ReconcilerLookupFailure.RateLimited)
        {
            Assert.Equal(TimeSpan.FromSeconds(7), failure.RetryAfter);
        }
    }

    [Fact]
    public async Task AnUnreadableSuccessBodyIsRefusedNotTrusted()
    {
        var failure = await Assert.ThrowsAsync<ReconcilerLookupException>(() => Client(new StubHandler(HttpStatusCode.OK, "not json")).LookupAsync(Tenant, "team-add:abcdefgh", "AddTeamMember", Correlation, TestContext.Current.CancellationToken));
        Assert.Equal(ReconcilerLookupFailure.Refused, failure.Failure);
    }

    [Fact]
    public async Task RC26TheLookupCarriesOnlyTheReconcilerTokenTheCorrelationAndTheBindingFields()
    {
        var answer = JsonSerializer.Serialize(new { found = false, correlation_id = Correlation, organization_id = Tenant });
        var handler = new StubHandler(HttpStatusCode.OK, answer);
        var outcome = await Client(handler).LookupAsync(Tenant, "team-add:abcdefgh", "AddTeamMember", Correlation, TestContext.Current.CancellationToken);
        Assert.False(outcome.Found);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/internal/v1/security-command-outcomes/lookup", request.Uri, StringComparison.Ordinal);
        Assert.False(request.Headers.ContainsKey("X-Delegated-User-Assertion"));
        Assert.False(request.Headers.ContainsKey("Idempotency-Key"));
        Assert.Equal(Correlation.ToString("D"), request.Headers["X-Correlation-Id"]);
        var token = new JsonWebToken(request.Headers["Authorization"]["Bearer ".Length..]);
        Assert.Equal("business-projection-reconciler", token.GetClaim("workload_role").Value);
        Assert.Equal("enterpriseforms-team-membership-reconciler", token.Issuer);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(["organization_id", "idempotency_key", "expected_command_kind"], body.RootElement.EnumerateObject().Select(p => p.Name));
    }

    private static TeamMembershipReconcilerHttpClient Client(StubHandler handler, bool configured = true)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(TeamMembershipReconcilerHttpClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        var reconciler = configured
            ? new TeamMembershipReconcilerOptions { Issuer = "enterpriseforms-team-membership-reconciler", Audience = "authorization-authority-reconciler", SharedKeyBase64 = ReconcilerKey }
            : new TeamMembershipReconcilerOptions();
        return new TeamMembershipReconcilerHttpClient(provider.GetRequiredService<IHttpClientFactory>(), Microsoft.Extensions.Options.Options.Create(reconciler), Microsoft.Extensions.Options.Options.Create(new AuthorityClientOptions { BaseAddress = "http://authority.invalid:8080" }), TimeProvider.System);
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string Body);

    private sealed class StubHandler(HttpStatusCode status, string body, int? retryAfterSeconds = null) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.ToString(), headers, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            if (retryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }
    }
}
