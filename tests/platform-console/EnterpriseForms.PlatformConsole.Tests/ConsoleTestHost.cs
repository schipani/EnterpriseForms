using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using EnterpriseForms.PlatformConsole.Composition;
using EnterpriseForms.PlatformConsole.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EnterpriseForms.PlatformConsole.Tests;

/// <summary>A clock the tests move by hand (session idle and absolute expiry).</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

/// <summary>
/// Vault Transit at the HTTP boundary: one ECDSA P-256 key per name, a token whose "policy" allows <c>update</c> on the sign paths of
/// listed keys only, and JWS marshaling (base64url r‖s) exactly as Vault returns it. Records every sign request.
/// </summary>
public sealed class FakeVault : HttpMessageHandler
{
    public const string Token = "fake-console-vault-token";
    private readonly Dictionary<string, ECDsa> keys = new(StringComparer.Ordinal);

    public HashSet<string> TokenMaySign { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> SignedWith { get; } = new();

    public bool Unavailable { get; set; }

    public ECDsa Key(string name)
    {
        if (!keys.TryGetValue(name, out var key))
        {
            key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            keys[name] = key;
        }

        return key;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new HttpRequestException("vault down");
        }

        var path = request.RequestUri!.AbsolutePath;
        const string prefix = "/v1/transit/sign/";
        if (request.Method != HttpMethod.Post || !path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith("/sha2-256", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var name = path[prefix.Length..^"/sha2-256".Length];
        if (!request.Headers.TryGetValues("X-Vault-Token", out var tokens) || tokens.Single() != Token || !TokenMaySign.Contains(name))
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"errors\":[\"1 error occurred:\\n\\t* permission denied\\n\\n\"]}") };
        }

        var body = await request.Content!.ReadFromJsonAsync<JsonObject>(cancellationToken);
        if (body?["marshaling_algorithm"]?.GetValue<string>() != "jws")
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        SignedWith.Enqueue(name);
        var signature = Key(name).SignData(Convert.FromBase64String(body["input"]!.GetValue<string>()), HashAlgorithmName.SHA256);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new JsonObject { ["data"] = new JsonObject { ["signature"] = "vault:v1:" + Base64Url.EncodeToString(signature), ["key_version"] = 1 } }) };
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var key in keys.Values)
        {
            key.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// The Authority's platform plane at the HTTP boundary: validates the console credential and the operator assertion with their own
/// keys, resolves operators from a small registry, and verifies every piece of evidence with the Authority's real
/// <see cref="PlatformOperatorEvidenceVerifier"/> (single use by jti). Two-operator approval is modelled only as far as the console
/// needs (first pending, same operator refused, second distinct operator approves). Records every call.
/// </summary>
public sealed class FakeAuthority : HttpMessageHandler
{
    public const string ConsoleKey = "cGxhdGZvcm0tY29uc29sZS11bml0LXRlc3Qtb25seS1zaGFyZWQta2V5LTA=";
    public const string AssertionKey = "cGxhdGZvcm0tb3BlcmF0b3ItYXNzZXJ0aW9uLXVuaXQtdGVzdC1vbmx5LWs=";
    public const string ConsoleIssuer = "enterpriseforms-platform-console-test";
    public const string ConsoleAudience = "authorization-authority-platform";
    public const string AssertionIssuer = "https://platform-operators.unit.enterpriseforms.invalid/";
    public const string AssertionAudience = "authorization-authority-platform-operator";
    public static readonly Guid Organization = Guid.Parse("01a16640-87f6-7488-a1f1-000000000001");

    private readonly JsonWebTokenHandler handler = new();
    private readonly PlatformOperatorEvidenceVerifier verifier = new(PlatformOperatorPolicy.Create(new PlatformOperatorOptions { Mode = PlatformOperatorPolicy.SyntheticMode }, "Testing"));
    private readonly HashSet<string> usedEvidence = new(StringComparer.Ordinal);

    public Dictionary<string, PlatformOperator> Registry { get; } = new(StringComparer.Ordinal);

    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public Dictionary<Guid, FakeRequest> Requests { get; } = [];

    public ConcurrentQueue<(string Method, string Path, string? Subject)> Calls { get; } = new();

    public ConcurrentQueue<string> Evidence { get; } = new();

    /// <summary>When set, answers every platform call with this error (archive outage, rollback refusal, credential rejection).</summary>
    public (HttpStatusCode Status, string Code, bool Retryable)? Failure { get; set; }

    public sealed class FakeRequest(Guid organizationId, string statementSha256)
    {
        public Guid OrganizationId { get; } = organizationId;

        public string StatementSha256 { get; set; } = statementSha256;

        public string State { get; set; } = "Pending";

        public List<Guid> Approvals { get; } = [];
    }

    public PlatformOperator Register(string subject, string displayName, string keyName, ECDsa key)
    {
        var o = new PlatformOperator
        {
            PlatformOperatorId = Guid.CreateVersion7(),
            OperatorIssuerUri = AssertionIssuer,
            OperatorSubjectIdentifier = subject,
            OperatorDisplayName = displayName,
            OperatorStatusName = PlatformOperatorStates.Active,
            OperatorEvidenceKindName = PlatformOperatorEvidenceKinds.SyntheticDevelopmentJws,
            SyntheticApprovalKeyIdentifier = keyName,
            SyntheticApprovalPublicKey = key.ExportSubjectPublicKeyInfo(),
            OperatorProvisionedOn = DateTimeOffset.UtcNow,
            OperatorStatusChangedOn = DateTimeOffset.UtcNow,
        };
        Registry[subject] = o;
        return o;
    }

    public Guid NewRequest()
    {
        var id = Guid.CreateVersion7();
        Requests[id] = new FakeRequest(Organization, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        return id;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var bearer = request.Headers.Authorization?.Parameter;
        var assertion = request.Headers.TryGetValues(AuthorityPlatformClient.OperatorAssertionHeader, out var values) ? values.Single() : null;
        var consoleValid = bearer is not null && (await handler.ValidateTokenAsync(bearer, Parameters(ConsoleKey, ConsoleIssuer, ConsoleAudience, Clock))).Claims.TryGetValue("workload_role", out var role) && Equals(role, "platform-console");
        var asserted = assertion is null ? null : await handler.ValidateTokenAsync(assertion, Parameters(AssertionKey, AssertionIssuer, AssertionAudience, Clock));
        var subject = asserted is { IsValid: true } ? asserted.Claims["sub"]?.ToString() : null;
        Calls.Enqueue((request.Method.Method, path, subject));
        if (!path.StartsWith("/internal/v1/platform/", StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.NotFound, "NOT_FOUND");
        }

        if (!consoleValid || subject is null)
        {
            return Error(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        }

        if (Failure is { } failure)
        {
            return Error(failure.Status, failure.Code, failure.Retryable);
        }

        if (!Registry.TryGetValue(subject, out var me) || me.OperatorStatusName != PlatformOperatorStates.Active)
        {
            return Error(HttpStatusCode.Forbidden, "PLATFORM_OPERATOR_NOT_ACTIVE");
        }

        if (path == "/internal/v1/platform/operator/me")
        {
            return Json(new PlatformOperatorProfile(Summary(me), PlatformOperatorPolicy.SyntheticMode, PlatformOperatorPolicy.SyntheticAssurance, PlatformPermissions.All, Registry.Values.Count(o => o.OperatorStatusName == PlatformOperatorStates.Active), Journal(), Guid.CreateVersion7()));
        }

        if (path == "/internal/v1/platform/membership-requests/search")
        {
            var items = Requests.Select(r => Item(r.Key, r.Value, me)).ToList();
            return Json(new PlatformMembershipRequestPage(items, items.Count, null, 15, "needs_decision", Journal(), PlatformOperatorPolicy.SyntheticAssurance, Guid.CreateVersion7()));
        }

        var parts = path["/internal/v1/platform/organizations/".Length..].Split('/');
        if (parts.Length < 3 || !Guid.TryParse(parts[0], out var organizationId) || !Guid.TryParse(parts[2], out var requestId) || !Requests.TryGetValue(requestId, out var target) || target.OrganizationId != organizationId)
        {
            return Error(HttpStatusCode.NotFound, "NOT_FOUND");
        }

        var action = string.Join('/', parts.Skip(3));
        if (request.Method == HttpMethod.Get && action.Length == 0)
        {
            return Json(new PlatformMembershipRequestDetail(Item(requestId, target, me), "joins the intake team", null, "{\"statement_version\":2}", target.StatementSha256, ["Creates one membership"], [],
                new PlatformEligibility(!target.Approvals.Contains(me.PlatformOperatorId), true, target.Approvals.Contains(me.PlatformOperatorId), null, null), PlatformOperatorPolicy.SyntheticAssurance, Guid.CreateVersion7()));
        }

        var decision = action switch { "approvals" => "approve", "approvals/withdraw" => "withdraw", "reject" => "reject", _ => null };
        if (decision is null || request.Method != HttpMethod.Post)
        {
            return Error(HttpStatusCode.NotFound, "NOT_FOUND");
        }

        var input = (await request.Content!.ReadFromJsonAsync<PlatformMembershipDecisionRequest>(ConsoleJson.Authority, cancellationToken))!;
        if (input.DryRun)
        {
            return Json(Decision(true, requestId, target, decision, "Preview", null));
        }

        if (!string.Equals(input.ExpectedStatementSha256, target.StatementSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Error(HttpStatusCode.Conflict, "APPROVAL_STATEMENT_CHANGED");
        }

        VerifiedOperatorEvidence evidence;
        try
        {
            evidence = verifier.Verify(me, input.EvidenceKind, input.Evidence, new OperatorEvidenceBinding(decision, PlatformOperationKinds.MembershipApproval, requestId, organizationId, target.StatementSha256,
                PlatformOperatorEvidenceVerifier.JustificationSha256(input.Justification.Trim())), Clock.GetUtcNow());
        }
        catch (AuthorityException ex)
        {
            return Error((HttpStatusCode)ex.HttpStatusCode, ex.ErrorCode);
        }

        if (!usedEvidence.Add(evidence.EvidenceIdentifier))
        {
            return Error(HttpStatusCode.Conflict, "OPERATOR_EVIDENCE_REPLAYED");
        }

        Evidence.Enqueue(input.Evidence!);
        if (target.State != "Pending")
        {
            return Error(HttpStatusCode.Conflict, "MEMBERSHIP_REQUEST_NOT_PENDING");
        }

        switch (decision)
        {
            case "approve" when target.Approvals.Contains(me.PlatformOperatorId):
                return Error(HttpStatusCode.Conflict, "OPERATOR_ALREADY_APPROVED");
            case "approve":
                target.Approvals.Add(me.PlatformOperatorId);
                if (target.Approvals.Count < 2)
                {
                    return Json(Decision(false, requestId, target, decision, "AwaitingSecondApproval", Guid.CreateVersion7()));
                }

                target.State = "Approved";
                return Json(Decision(false, requestId, target, decision, "Approved", Guid.CreateVersion7()));
            case "withdraw" when !target.Approvals.Remove(me.PlatformOperatorId):
                return Error(HttpStatusCode.Conflict, "NO_PENDING_APPROVAL");
            case "withdraw":
                return Json(Decision(false, requestId, target, decision, "Withdrawn", null));
            default:
                target.State = "Rejected";
                target.Approvals.Clear();
                return Json(Decision(false, requestId, target, decision, "Rejected", null));
        }
    }

    private static PlatformMembershipDecisionResponse Decision(bool dryRun, Guid requestId, FakeRequest target, string decision, string outcome, Guid? approvalId) =>
        new(dryRun, true, null, null, requestId, target.OrganizationId, decision, "{\"statement_version\":2}", target.StatementSha256, outcome, target.Approvals.Count, 2, approvalId, outcome == "Approved" ? Guid.CreateVersion7() : null,
            outcome == "Approved" ? "Effective" : null, 41, 7, "Anchored", PlatformOperatorPolicy.SyntheticAssurance, Guid.CreateVersion7());

    private static PlatformMembershipRequestItem Item(Guid requestId, FakeRequest r, PlatformOperator me) =>
        new(requestId, r.OrganizationId, "Northwind Crown Office (synthetic)", "Nia Newcomer", new PlatformIdentity("https://test-idp.enterpriseforms.invalid", "po-person-1"), "Designer", Guid.CreateVersion7(),
            new PlatformIdentity("https://test-idp.enterpriseforms.invalid", "dev-owner"), DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(6), r.State,
            new PlatformApprovalStatus(r.Approvals.Count, 2, r.Approvals.Contains(me.PlatformOperatorId), r.Approvals.Count == 1 && !r.Approvals.Contains(me.PlatformOperatorId), null), ["review"]);

    private static PlatformOperatorSummary Summary(PlatformOperator o) =>
        new(o.PlatformOperatorId, o.OperatorIssuerUri, o.OperatorSubjectIdentifier, o.OperatorDisplayName, o.OperatorStatusName, o.OperatorEvidenceKindName, o.SyntheticApprovalKeyIdentifier, o.OperatorRowRevision, o.OperatorProvisionedOn, o.OperatorStatusChangedOn, o.OperatorStatusReasonText);

    private static PlatformJournalStatus Journal() => new(7, 0, false, null);

    private static TokenValidationParameters Parameters(string key, string issuer, string audience, TimeProvider clock) => new()
    {
        ValidIssuer = issuer,
        ValidAudience = audience,
        IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(key)),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateLifetime = true,
        RequireExpirationTime = true,
        LifetimeValidator = (notBefore, expires, _, _) => notBefore <= clock.GetUtcNow().UtcDateTime.AddSeconds(30) && expires > clock.GetUtcNow().UtcDateTime.AddSeconds(-30),
    };

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: ConsoleJson.Authority) };

    private static HttpResponseMessage Error(HttpStatusCode status, string code, bool retryable = false) =>
        new(status) { Content = JsonContent.Create(new ErrorResponse(code, Guid.CreateVersion7(), retryable, "refused (" + code + ")"), options: ConsoleJson.Authority) };
}

/// <summary>The console host with the fakes and the manual clock.</summary>
public sealed class ConsoleFactory : WebApplicationFactory<EnterpriseForms.PlatformConsole.Program>
{
    public const string Origin = "https://localhost";
    public static readonly (string Subject, string Name, string Key)[] Operators =
    [
        ("unit-operator-avery", "Avery Operator (synthetic)", "unit-operator-key-avery"),
        ("unit-operator-blair", "Blair Operator (synthetic)", "unit-operator-key-blair"),
        ("unit-operator-cameron", "Cameron Operator (synthetic)", "unit-operator-key-cameron"),
    ];

    public FakeAuthority Authority { get; } = new();

    public FakeVault Vault { get; } = new();

    public ManualClock Clock { get; } = new();

    public string Environment { get; init; } = "Testing";

    public Dictionary<string, string?> Settings { get; } = [];

    public ConsoleFactory()
    {
        Authority.Clock = Clock;
        foreach (var (subject, name, key) in Operators)
        {
            Vault.TokenMaySign.Add(key);
            Authority.Register(subject, name, key, Vault.Key(key));
        }
    }

    public static Dictionary<string, string?> BaseSettings()
    {
        var settings = new Dictionary<string, string?>
        {
            ["PlatformConsole:Mode"] = "SyntheticDevelopment",
            ["PlatformConsole:AllowedOrigins:0"] = Origin,
            ["PlatformConsole:Authority:BaseAddress"] = "http://authority.test.invalid/",
            ["PlatformConsole:Authority:ConsoleCredential:Issuer"] = FakeAuthority.ConsoleIssuer,
            ["PlatformConsole:Authority:ConsoleCredential:Audience"] = FakeAuthority.ConsoleAudience,
            ["PlatformConsole:Authority:ConsoleCredential:SharedKeyBase64"] = FakeAuthority.ConsoleKey,
            ["PlatformConsole:Authority:OperatorAssertion:Issuer"] = FakeAuthority.AssertionIssuer,
            ["PlatformConsole:Authority:OperatorAssertion:Audience"] = FakeAuthority.AssertionAudience,
            ["PlatformConsole:Authority:OperatorAssertion:SharedKeyBase64"] = FakeAuthority.AssertionKey,
            ["PlatformConsole:Vault:Address"] = "http://vault.test.invalid/",
            ["PlatformConsole:Vault:Token"] = FakeVault.Token,
        };
        for (var i = 0; i < Operators.Length; i++)
        {
            settings[$"PlatformConsole:Operators:{i}:SubjectIdentifier"] = Operators[i].Subject;
            settings[$"PlatformConsole:Operators:{i}:DisplayName"] = Operators[i].Name;
            settings[$"PlatformConsole:Operators:{i}:VaultKeyName"] = Operators[i].Key;
        }

        return settings;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        foreach (var (key, value) in BaseSettings().Concat(Settings).GroupBy(p => p.Key).Select(g => g.Last()))
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddHttpClient<AuthorityPlatformClient>().ConfigurePrimaryHttpMessageHandler(() => Authority).SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            services.AddHttpClient<IOperatorEvidenceSigner, VaultTransitEvidenceSigner>().ConfigurePrimaryHttpMessageHandler(() => Vault).SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        });
    }

    public ConsoleBrowser Browser() => new(CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(Origin), AllowAutoRedirect = false }));

    public Guid OperatorId(int index) => Authority.Registry[Operators[index].Subject].PlatformOperatorId;
}

/// <summary>One browser: its own cookie and CSRF token, the console's origin on every request (unless a test changes it).</summary>
public sealed class ConsoleBrowser(HttpClient client) : IDisposable
{
    public string? Cookie { get; set; }

    public string? Csrf { get; set; }

    public string? Origin { get; set; } = ConsoleFactory.Origin;

    public string? LastSetCookie { get; private set; }

    public async Task<(HttpStatusCode Status, JsonElement Body, string Text)> SendAsync(HttpMethod method, string path, object? body = null, string? csrf = null, Action<HttpRequestMessage>? tweak = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (Origin is not null)
        {
            request.Headers.Add("Origin", Origin);
        }

        if (Cookie is not null)
        {
            request.Headers.Add("Cookie", Cookie);
        }

        if ((csrf ?? Csrf) is { } token && method != HttpMethod.Get)
        {
            request.Headers.Add("X-CSRF-Token", token);
        }

        if (body is not null)
        {
            request.Content = body is string raw ? new StringContent(raw, Encoding.UTF8, "application/json") : JsonContent.Create(body);
        }

        tweak?.Invoke(request);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        LastSetCookie = response.Headers.TryGetValues("Set-Cookie", out var cookies) ? string.Join("\n", cookies) : null;
        var json = text.Length > 0 && text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (response.StatusCode, json, text);
    }

    public async Task<JsonElement> SignInAsync(string subject)
    {
        var (status, body, text) = await SendAsync(HttpMethod.Post, "/console-api/v1/session/development-login", new { subjectIdentifier = subject });
        if (status != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"sign-in failed: {(int)status} {text}");
        }

        Cookie = LastSetCookie!.Split('\n').Select(c => c.Split(';')[0]).Last(c => !c.EndsWith('=')); // the issued cookie, not a deletion
        Csrf = body.GetProperty("csrfToken").GetString();
        return body;
    }

    public void Dispose() => client.Dispose();
}

public static class Problems
{
    public static string Code(this JsonElement body) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty("errorCode", out var code) ? code.GetString() ?? string.Empty : string.Empty;
}
