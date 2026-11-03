using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.PlatformConsole.Composition;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseForms.PlatformConsole.Infrastructure;

/// <summary>
/// The console's only path to the Authority: the platform plane (<c>/internal/v1/platform/*</c>), authenticated with the dedicated
/// Platform Console workload credential AND an operator assertion naming exactly one operator (the session's). Both are minted per
/// call with a two-minute lifetime and never leave this process. The Authority decides everything (operator status, permissions,
/// eligibility, evidence); this client only carries requests and returns the Authority's answer or its refusal code.
/// </summary>
public sealed class AuthorityPlatformClient(HttpClient http, IOptions<PlatformConsoleOptions> options, TimeProvider clock)
{
    public const string OperatorAssertionHeader = "X-Platform-Operator-Assertion";
    public const string CorrelationHeader = "X-Correlation-Id";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(2);
    private readonly JsonWebTokenHandler handler = new();

    public Task<PlatformOperatorProfile> GetProfileAsync(string subject, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<PlatformOperatorProfile>(HttpMethod.Get, "internal/v1/platform/operator/me", subject, null, correlationId, cancellationToken);

    public Task<PlatformMembershipRequestPage> SearchAsync(string subject, PlatformMembershipRequestSearchRequest request, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<PlatformMembershipRequestPage>(HttpMethod.Post, "internal/v1/platform/membership-requests/search", subject, request, correlationId, cancellationToken);

    public Task<PlatformMembershipRequestDetail> GetDetailAsync(string subject, Guid organizationId, Guid requestId, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<PlatformMembershipRequestDetail>(HttpMethod.Get, RequestPath(organizationId, requestId, string.Empty), subject, null, correlationId, cancellationToken);

    /// <summary><paramref name="decision"/> selects the Authority endpoint: approve → <c>/approvals</c>, withdraw → <c>/approvals/withdraw</c>, reject → <c>/reject</c>.</summary>
    public Task<PlatformMembershipDecisionResponse> DecideAsync(string subject, Guid organizationId, Guid requestId, string decision, PlatformMembershipDecisionRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        var action = decision switch
        {
            OperatorDecisions.Approve => "/approvals",
            OperatorDecisions.Withdraw => "/approvals/withdraw",
            OperatorDecisions.Reject => "/reject",
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };
        return SendAsync<PlatformMembershipDecisionResponse>(HttpMethod.Post, RequestPath(organizationId, requestId, action), subject, request, correlationId, cancellationToken);
    }

    // ---- C8 onboarding (C-104) ----

    public Task<OrganizationOnboardingCreateResponse> CreateOnboardingAsync(string subject, OrganizationOnboardingCreateRequest request, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<OrganizationOnboardingCreateResponse>(HttpMethod.Post, "internal/v1/platform/organization-onboarding", subject, request, correlationId, cancellationToken);

    public Task<OrganizationOnboardingPage> SearchOnboardingAsync(string subject, OrganizationOnboardingSearchRequest request, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<OrganizationOnboardingPage>(HttpMethod.Post, "internal/v1/platform/organization-onboarding/search", subject, request, correlationId, cancellationToken);

    public Task<OrganizationOnboardingDetail> GetOnboardingAsync(string subject, Guid requestId, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<OrganizationOnboardingDetail>(HttpMethod.Get, $"internal/v1/platform/organization-onboarding/{requestId:D}", subject, null, correlationId, cancellationToken);

    /// <summary><paramref name="decision"/> selects the endpoint: approve, withdraw, reject, cancel or initialize.</summary>
    public Task<OrganizationOnboardingDecisionResponse> DecideOnboardingAsync(string subject, Guid requestId, string decision, OrganizationOnboardingDecisionRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        var action = decision switch
        {
            OperatorDecisions.Approve => "/approvals",
            OperatorDecisions.Withdraw => "/approvals/withdraw",
            OperatorDecisions.Reject => "/reject",
            OperatorDecisions.Cancel => "/cancel",
            OperatorDecisions.Initialize => "/initialize",
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };
        return SendAsync<OrganizationOnboardingDecisionResponse>(HttpMethod.Post, $"internal/v1/platform/organization-onboarding/{requestId:D}{action}", subject, request, correlationId, cancellationToken);
    }

    public Task<OwnerRecoveryCreateResponse> CreateRecoveryAsync(string subject, OwnerRecoveryCreateRequest request, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<OwnerRecoveryCreateResponse>(HttpMethod.Post, "internal/v1/platform/owner-recovery", subject, request, correlationId, cancellationToken);

    public Task<OwnerRecoveryMemberPage> SearchRecoveryMembersAsync(string subject, Guid organizationId, OwnerRecoveryMemberSearchRequest request, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<OwnerRecoveryMemberPage>(HttpMethod.Post, $"internal/v1/platform/owner-recovery/organizations/{organizationId:D}/members/search", subject, request, correlationId, cancellationToken);

    public Task<OwnerRecoveryPage> SearchRecoveryAsync(string subject, OwnerRecoverySearchRequest request, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<OwnerRecoveryPage>(HttpMethod.Post, "internal/v1/platform/owner-recovery/search", subject, request, correlationId, cancellationToken);

    public Task<OwnerRecoveryCaseDetail> GetRecoveryAsync(string subject, Guid caseId, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<OwnerRecoveryCaseDetail>(HttpMethod.Get, $"internal/v1/platform/owner-recovery/{caseId:D}", subject, null, correlationId, cancellationToken);

    /// <summary>C-105: <paramref name="decision"/> selects the endpoint: approve, withdraw, cancel or execute.</summary>
    public Task<OwnerRecoveryDecisionResponse> DecideRecoveryAsync(string subject, Guid caseId, string decision, OwnerRecoveryDecisionRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        var action = decision switch
        {
            OperatorDecisions.Approve => "/approvals",
            OperatorDecisions.Withdraw => "/approvals/withdraw",
            OperatorDecisions.Cancel => "/cancel",
            OperatorDecisions.Execute => "/execute",
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };
        return SendAsync<OwnerRecoveryDecisionResponse>(HttpMethod.Post, $"internal/v1/platform/owner-recovery/{caseId:D}{action}", subject, request, correlationId, cancellationToken);
    }

    public Task<OrganizationDirectoryPage> SearchOrganizationsAsync(string subject, OrganizationDirectorySearchRequest request, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<OrganizationDirectoryPage>(HttpMethod.Post, "internal/v1/platform/organizations/search", subject, request, correlationId, cancellationToken);

    public Task<OrganizationDirectoryDetail> GetOrganizationAsync(string subject, Guid organizationId, Guid correlationId, CancellationToken cancellationToken) =>
        SendAsync<OrganizationDirectoryDetail>(HttpMethod.Get, $"internal/v1/platform/organizations/{organizationId:D}", subject, null, correlationId, cancellationToken);

    private static string RequestPath(Guid organizationId, Guid requestId, string action) =>
        $"internal/v1/platform/organizations/{organizationId:D}/membership-requests/{requestId:D}{action}";

    private async Task<T> SendAsync<T>(HttpMethod method, string path, string subject, object? body, Guid correlationId, CancellationToken cancellationToken)
    {
        var settings = options.Value.Authority;
        using var message = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Mint(settings.ConsoleCredential, settings.ConsoleCredential.Issuer!, settings.WorkloadName, new Dictionary<string, object>
        {
            ["workload_role"] = "platform-console",
            ["workload_name"] = settings.WorkloadName,
        }));
        message.Headers.Add(OperatorAssertionHeader, Mint(settings.OperatorAssertion, settings.OperatorAssertion.Issuer!, subject, []));
        message.Headers.Add(CorrelationHeader, correlationId.ToString("D"));
        if (body is not null)
        {
            message.Content = JsonContent.Create(body, body.GetType(), options: ConsoleJson.Authority);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new ConsoleException(ConsoleErrorCodes.AuthorityUnavailable, 503, "the Authorization Authority could not be reached", retryable: true, correlationId);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConsoleException(ConsoleErrorCodes.AuthorityUnavailable, 503, "the Authorization Authority did not answer in time", retryable: true, correlationId);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<T>(ConsoleJson.Authority, cancellationToken).ConfigureAwait(false)
                    ?? throw new ConsoleException(ConsoleErrorCodes.AuthorityUnavailable, 502, "the Authorization Authority returned an empty answer", correlationId: correlationId);
            }

            ErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<ErrorResponse>(ConsoleJson.Authority, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
            }

            var status = (int)response.StatusCode;
            if (status == StatusCodes.Status401Unauthorized)
            {
                // The Authority refused the console's own credential or assertion: a deployment fault, never "sign in again".
                throw new ConsoleException(ConsoleErrorCodes.AuthorityCredentialRejected, 502, "the Authorization Authority refused the Platform Console credential", correlationId: error?.CorrelationId ?? correlationId);
            }

            throw new ConsoleException(error?.ErrorCode ?? "AUTHORITY_HTTP_" + status, status, error?.Detail ?? "refused by the Authorization Authority", error?.Retryable ?? status >= 500, error?.CorrelationId ?? correlationId);
        }
    }

    private string Mint(CredentialOptions credential, string issuer, string subject, Dictionary<string, object> claims)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        claims["sub"] = subject;
        claims["jti"] = Guid.NewGuid().ToString("N");
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = credential.Audience,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.Add(TokenLifetime),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(credential.Key()), SecurityAlgorithms.HmacSha256),
        });
    }
}

/// <summary>Decision values of operator evidence and of the Authority endpoints (mirrors the Authority's <c>PlatformDecisions</c>; pinned by the architecture tests).</summary>
public static class OperatorDecisions
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Withdraw = "withdraw";

    /// <summary>C8 onboarding (C-104): close an approved request before initialization.</summary>
    public const string Cancel = "cancel";

    /// <summary>C8 onboarding (C-104): create the organization from an approved request whose key custody is complete.</summary>
    public const string Initialize = "initialize";

    /// <summary>C-105: execute an approved owner recovery case after its waiting period.</summary>
    public const string Execute = "execute";
}
