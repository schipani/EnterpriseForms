using System.Security.Claims;
using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.Dashboard;
using EnterpriseForms.Application.Features.Identity.EnsureProjection;
using EnterpriseForms.Application.Features.Identity.Presence;
using EnterpriseForms.Application.Features.Identity.Profile;
using EnterpriseForms.Application.Features.Organizations;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Identity;

/// <summary>BFF session surface (PG-001/002/003, B-01): session status, development login, logout and the authenticated bootstrap.</summary>
public static class SessionEndpoints
{
    public const string OwnerName = "Identity";

    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var anonymous = endpoints.MapGroup(ApiVersions.V1Prefix + "/session").WithTags(OwnerName).WithFeatureOwner(OwnerName);

        anonymous.MapGet("/", static async Task<Ok<SessionResponse>> (HttpContext http, DevelopmentIdentityProvider development) =>
            {
                var result = await http.AuthenticateAsync(BusinessAuthentication.SessionScheme).ConfigureAwait(false);
                if (BusinessAuthentication.IsSessionStateUnavailable(http))
                {
                    // G23: fail closed, but do not report a sign-out the user did not perform.
                    throw new BusinessException(BusinessAuthentication.SessionStateUnavailableCode, 503, "The session could not be verified. Try again shortly.");
                }

                if (!result.Succeeded || result.Principal is null)
                {
                    return TypedResults.Ok(new SessionResponse(false, null, null, null, null, [], string.Empty, null, development.IsAvailable ? "TestIdentityProvider" : "EntraId"));
                }

                var principal = result.Principal;
                return TypedResults.Ok(new SessionResponse(true, Guid.TryParse(principal.FindFirstValue("org"), out var org) ? org : null, principal.FindFirstValue("org_name"),
                    Guid.TryParse(principal.FindFirstValue("authority_user_id"), out var uid) ? uid : null, principal.FindFirstValue("name"), [], principal.FindFirstValue(BusinessAuthentication.CsrfClaim) ?? string.Empty,
                    result.Properties?.ExpiresUtc, "TestIdentityProvider"));
            })
            .WithName("GetSession")
            .WithSummary("Current browser session (no secrets): authenticated flag, organization, CSRF token for mutations.");

        anonymous.MapGet("/development-personas", static Results<Ok<IReadOnlyList<DevelopmentPersonaResponse>>, NotFound> (DevelopmentIdentityProvider development) =>
                development.IsAvailable ? TypedResults.Ok<IReadOnlyList<DevelopmentPersonaResponse>>(development.Personas().Select(DevelopmentIdentityProvider.ToResponse).ToList()) : TypedResults.NotFound())
            .WithName("ListDevelopmentPersonas")
            .WithSummary("Synthetic personas seeded through the authority operator plane. 404 outside Development/Testing.");

        anonymous.MapPost("/development-login", static async Task<Ok<SessionResponse>> (DevelopmentLoginRequest request, HttpContext http, DevelopmentIdentityProvider development, EnsureIdentityProjectionHandler projection, CancellationToken cancellationToken) =>
            {
                var (assertion, persona, expires) = development.Mint(request.PersonaKey);
                var claims = new Dictionary<string, string>
                {
                    ["iss"] = http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<UserAuthenticationOptions>>().Value.Issuer ?? string.Empty,
                    ["sub"] = persona.SubjectIdentifier,
                    ["org"] = persona.OrganizationId.ToString("D"),
                    ["org_name"] = persona.OrganizationDisplayName,
                    ["authority_user_id"] = persona.AuthorityUserId.ToString("D"),
                    ["name"] = persona.DisplayName,
                };
                (var csrf, expires) = await BusinessAuthentication.StartSessionAsync(http, assertion, claims, expires, cancellationToken).ConfigureAwait(false);
                var context = new RequestContext(persona.OrganizationId, persona.AuthorityUserId, claims["iss"], persona.SubjectIdentifier, persona.DisplayName, assertion, Guid.CreateVersion7());
                await projection.RecordSignInAsync(context, cancellationToken).ConfigureAwait(false);
                // Development only: project the other seeded personas of this organization so directory pickers (form users,
                // shares) can name people who have not signed in yet. Display data only; the authority still decides access.
                await projection.HandleManyAsync(persona.OrganizationId, development.Personas().Where(p => p.OrganizationId == persona.OrganizationId).Select(p => (p.AuthorityUserId, claims["iss"], p.SubjectIdentifier, p.DisplayName)).ToList(), cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(new SessionResponse(true, persona.OrganizationId, persona.OrganizationDisplayName, persona.AuthorityUserId, persona.DisplayName, [], csrf, expires, "TestIdentityProvider"));
            })
            .WithName("DevelopmentLogin")
            .WithSummary("Development/Testing only: starts a BFF session for a seeded persona. Refused elsewhere by construction.");

        anonymous.MapPost("/logout", static async Task<NoContent> (HttpContext http, PresenceHandlers presence, CancellationToken cancellationToken) =>
            {
                // The signed-in person (read before the session ends) for the presence clean-up below; never a client-supplied id.
                var current = await http.AuthenticateAsync(BusinessAuthentication.SessionScheme).ConfigureAwait(false);
                var signedOutOrganization = current.Succeeded && Guid.TryParse(current.Principal?.FindFirstValue("org"), out var organization) ? organization : (Guid?)null;
                var signedOutUser = current.Succeeded && Guid.TryParse(current.Principal?.FindFirstValue("authority_user_id"), out var user) ? user : (Guid?)null;

                // G23: the session is revoked at the Authority first; a copied cookie stops working everywhere at once.
                if (!await BusinessAuthentication.EndSessionAsync(http, cancellationToken).ConfigureAwait(false))
                {
                    throw new BusinessException(BusinessAuthentication.SessionStateUnavailableCode, 503, "Sign-out could not be confirmed. Try again.");
                }

                if (signedOutOrganization is { } org && signedOutUser is { } uid)
                {
                    try
                    {
                        await presence.ClearOnSessionEndAsync(org, uid, cancellationToken).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // presence is informational: its clean-up never blocks or fails a confirmed sign-out (the heartbeat expires on its own)
                    catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                    {
                        // heartbeat expiry covers this case
                    }
                }

                return TypedResults.NoContent();
            })
            .WithName("Logout")
            .WithSummary("Revokes the session at the Authority, then deletes the cookie. 503 SESSION_STATE_UNAVAILABLE if revocation cannot be confirmed (cookie kept for a retry).");

        var authenticated = endpoints.MapGroup(ApiVersions.V1Prefix + "/bootstrap").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        authenticated.MapGet("/", static async Task<Ok<BootstrapResponse>> (HttpContext http, EnsureIdentityProjectionHandler projection, BrandingHandlers branding, DashboardHandler dashboard, IAuthorizationAuthorityClient authority, UserProfileHandlers profiles, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                await projection.HandleAsync(context, cancellationToken).ConfigureAwait(false);
                var shownName = await profiles.EffectiveDisplayNameAsync(context, cancellationToken).ConfigureAwait(false);
                var capabilities = await dashboard.CapabilitiesAsync(context, cancellationToken).ConfigureAwait(false);
                var theme = await branding.GetAsync(context, cancellationToken).ConfigureAwait(false);
                var preferences = await branding.GetPreferenceAsync(context, cancellationToken).ConfigureAwait(false);
                var revision = (await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.ManageOrganization), cancellationToken).ConfigureAwait(false)).AuthorizationRevision;
                var session = new SessionResponse(true, context.OrganizationId, http.User.FindFirstValue("org_name") ?? theme.OrganizationDisplayName, context.LocalUserId, shownName, capabilities, http.User.FindFirstValue(BusinessAuthentication.CsrfClaim) ?? string.Empty, null, "TestIdentityProvider");
                return TypedResults.Ok(new BootstrapResponse(session, theme, preferences, revision));
            })
            .WithName("GetBootstrap")
            .WithSummary("Authenticated bootstrap: user, capabilities, safe branding, UI preferences and current authorization revision (no record counts).");

        return endpoints;
    }
}
