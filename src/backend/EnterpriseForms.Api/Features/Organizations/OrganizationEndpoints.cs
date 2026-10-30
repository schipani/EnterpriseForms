using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Organizations;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Organizations;

/// <summary>Organization branding/logo (PG-042) and user text-size preference (PG-007 subset); users directory for sharing pickers.</summary>
public static class OrganizationEndpoints
{
    public const string OwnerName = "Organizations";

    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var branding = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization/branding").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        branding.MapGet("/", static async Task<Ok<BrandingResponse>> (HttpContext http, BrandingHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.GetAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false)))
            .WithName("GetOrganizationBranding");

        branding.MapPut("/", static async Task<Ok<BrandingResponse>> (UpdateBrandingRequest request, HttpContext http, BrandingHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.UpdateAsync(BusinessAuthentication.RequireContext(http), request, cancellationToken).ConfigureAwait(false)))
            .WithName("UpdateOrganizationBranding")
            .WithSummary("OrganizationOwner only; WCAG pairwise contrast validated server-side; revision CAS.");

        branding.MapGet("/logo", static async Task<Results<FileContentHttpResult, NotFound>> (HttpContext http, BrandingHandlers handlers, CancellationToken cancellationToken) =>
            {
                var png = await handlers.GetLogoAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false);
                return png is null ? TypedResults.NotFound() : TypedResults.File(png, "image/png");
            })
            .WithName("GetOrganizationLogo");

        branding.MapPost("/logo", static async Task<Ok<BrandingResponse>> (HttpContext http, BrandingHandlers handlers, CancellationToken cancellationToken) =>
            {
                using var buffer = new MemoryStream();
                var limited = new byte[81920];
                int read;
                while ((read = await http.Request.Body.ReadAsync(limited, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(limited, 0, read);
                    if (buffer.Length > 2 * 1024 * 1024)
                    {
                        throw new Application.Common.BusinessException("INVALID_REQUEST", 422, "logo must be at most 2 MiB");
                    }
                }

                return TypedResults.Ok(await handlers.UploadLogoAsync(BusinessAuthentication.RequireContext(http), buffer.ToArray(), cancellationToken).ConfigureAwait(false));
            })
            .Accepts<Stream>("image/png")
            .DisableAntiforgery()
            .WithName("UploadOrganizationLogo");

        var preferences = endpoints.MapGroup(ApiVersions.V1Prefix + "/me/ui-preferences").WithTags("UserPreferences").WithFeatureOwner("UserPreferences").RequireAuthorization(BusinessAuthentication.Policy);
        preferences.MapGet("/", static async Task<Ok<UiPreferenceResponse>> (HttpContext http, BrandingHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.GetPreferenceAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false)))
            .WithName("GetUiPreferences");
        preferences.MapPut("/", static async Task<Ok<UiPreferenceResponse>> (UpdateUiPreferenceRequest request, HttpContext http, BrandingHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.UpdatePreferenceAsync(BusinessAuthentication.RequireContext(http), request, cancellationToken).ConfigureAwait(false)))
            .WithName("UpdateUiPreferences");

        var policy = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization/security-policy").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        policy.MapGet("/", static async Task<Ok<OrganizationSecurityPolicyResponse>> (HttpContext http, SecurityPolicyHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.GetAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false)))
            .WithName("GetOrganizationSecurityPolicy")
            .WithSummary("Data-protection policy: minimum field classification and whether plaintext Internal fields are permitted (ADR-PB-004).");

        policy.MapPut("/", static async Task<Ok<OrganizationSecurityPolicyResponse>> (UpdateOrganizationSecurityPolicyRequest request, HttpContext http, SecurityPolicyHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.UpdateAsync(BusinessAuthentication.RequireContext(http), request, cancellationToken).ConfigureAwait(false)))
            .WithName("UpdateOrganizationSecurityPolicy")
            .WithSummary("OrganizationOwner only; reason required; audited; revision CAS.");

        var securityAdministrators = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization/security-administrators").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        securityAdministrators.MapGet("/", static async Task<Ok<SecurityAdministratorsResponse>> (HttpContext http, SecurityAdministratorsHandlers handlers, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handlers.GetAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetSecurityAdministrators")
            .WithSummary("The organization's Security Administrators (Authority relation system_administrator) — for its organization owners (read-only) and Security Administrators (with candidates); 403 for everyone else.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        securityAdministrators.MapPost("/", static async Task<NoContent> (AppointSecurityAdministratorRequest request, HttpContext http, SecurityAdministratorsHandlers handlers, CancellationToken cancellationToken) =>
            {
                await handlers.AppointAsync(BusinessAuthentication.RequireContext(http), request, cancellationToken).ConfigureAwait(false);
                return TypedResults.NoContent();
            })
            .WithName("AppointSecurityAdministrator")
            .WithSummary("Appoints an existing verified member as Security Administrator: a GrantSystemRole security command that only a Security Administrator may submit, with step-up (unavailable in staging and production until PB-08). Reason required; audited by the Authority. 204 once Effective.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        securityAdministrators.MapPost("/{userId:guid}/remove", static async Task<NoContent> (Guid userId, RemoveSecurityAdministratorRequest request, HttpContext http, SecurityAdministratorsHandlers handlers, CancellationToken cancellationToken) =>
            {
                await handlers.RemoveAsync(BusinessAuthentication.RequireContext(http), userId, request, cancellationToken).ConfigureAwait(false);
                return TypedResults.NoContent();
            })
            .WithName("RemoveSecurityAdministrator")
            .WithSummary("Removes a Security Administrator (RevokeSystemRole, step-up, reason, audited). The last Security Administrator cannot be removed (409 LAST_SECURITY_ADMINISTRATOR). 204 once Effective; self-removal allowed while another remains.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Phase C8 M1 (PG-041): aggregate of independently failing parts; owners and Security Administrators only (the Authority decides).
        var overview = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization/overview").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        overview.MapGet("/", static async Task<Ok<OrganizationOverviewResponse>> (HttpContext http, OrganizationOverviewHandler handler, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.GetAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetOrganizationOverview")
            .WithSummary("Organization overview (PG-041): organization identity, member and Security Administrator counts, teams, data-protection floor and authentication status as independently computed parts (an unavailable part never fails the page). Organization owners and Security Administrators only; 403 for everyone else, without counts.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var users = endpoints.MapGroup(ApiVersions.V1Prefix + "/users").WithTags("Identity").WithFeatureOwner("Identity").RequireAuthorization(BusinessAuthentication.Policy);
        users.MapGet("/", static async Task<Ok<IReadOnlyList<DirectoryUserResponse>>> (HttpContext http, IBusinessStore store, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
                var all = await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok<IReadOnlyList<DirectoryUserResponse>>(all.Select(u => new DirectoryUserResponse(u.UserId, u.DisplayName)).ToList());
            })
            .WithName("ListOrganizationUsers")
            .WithSummary("Display-name directory of users seen in this organization (projection; confers nothing).");

        return endpoints;
    }
}
