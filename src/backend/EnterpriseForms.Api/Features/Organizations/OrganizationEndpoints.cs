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
