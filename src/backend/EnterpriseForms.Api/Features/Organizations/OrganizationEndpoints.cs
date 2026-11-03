using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Organizations;
using EnterpriseForms.Application.Features.UserPreferences;
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

        // Phase C8 M3: regional defaults (PG-043), personal regional preferences (PG-007) and the typed regional catalog.
        var settings = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization/settings").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        settings.MapGet("/", static async Task<Ok<OrganizationSettingsResponse>> (HttpContext http, RegionalSettingsHandlers handlers, CancellationToken cancellationToken) =>
            {
                var response = await handlers.GetAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false);
                http.Response.Headers.ETag = RevisionEtag(response.OrganizationSettingRevision);
                return TypedResults.Ok(response);
            })
            .WithName("GetOrganizationSettings")
            .WithSummary("Organization regional defaults (country, IANA time zone, language, date and number presets). Any member may read; ETag carries the revision (0 = system default, never saved).");
        settings.MapPut("/", static async Task<Ok<OrganizationSettingsResponse>> (UpdateOrganizationSettingsRequest request, HttpContext http, RegionalSettingsHandlers handlers, CancellationToken cancellationToken) =>
            {
                var response = await handlers.UpdateAsync(BusinessAuthentication.RequireContext(http), request, RequireIfMatch(http), cancellationToken).ConfigureAwait(false);
                http.Response.Headers.ETag = RevisionEtag(response.OrganizationSettingRevision);
                return TypedResults.Ok(response);
            })
            .WithName("UpdateOrganizationSettings")
            .WithSummary("Organization owner only (Authority manage_configuration); reason required; If-Match required (428 missing, 412 stale); values validated against the typed catalog; audited as ORGANIZATION_SETTINGS_UPDATED.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        var regional = endpoints.MapGroup(ApiVersions.V1Prefix + "/me/preferences").WithTags("UserPreferences").WithFeatureOwner("UserPreferences").RequireAuthorization(BusinessAuthentication.Policy);
        regional.MapGet("/", static async Task<Ok<UserPreferencesResponse>> (HttpContext http, PreferenceHandlers handlers, CancellationToken cancellationToken) =>
            {
                var response = await handlers.GetAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false);
                http.Response.Headers.ETag = RevisionEtag(response.PreferenceRevision);
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(response);
            })
            .WithName("GetRegionalPreferences")
            .WithSummary("The caller's own regional preferences plus the organization defaults needed to resolve them. A missing row means OrganizationDefault for all four.");
        regional.MapPut("/", static async Task<Ok<UserPreferencesResponse>> (UpdateUserPreferencesRequest request, HttpContext http, PreferenceHandlers handlers, CancellationToken cancellationToken) =>
            {
                var response = await handlers.UpdateAsync(BusinessAuthentication.RequireContext(http), request, OptionalIfMatch(http), cancellationToken).ConfigureAwait(false);
                http.Response.Headers.ETag = RevisionEtag(response.PreferenceRevision);
                return TypedResults.Ok(response);
            })
            .WithName("UpdateRegionalPreferences")
            .WithSummary("Updates only the caller's own preferences (no user id is accepted). Each mode is OrganizationDefault, BrowserDefault or Explicit; the value is allowed exactly when Explicit and must be in the catalog. If-Match is honored when sent (412 stale).")
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        var catalog = endpoints.MapGroup(ApiVersions.V1Prefix + "/catalog").WithTags("UserPreferences").WithFeatureOwner("UserPreferences").RequireAuthorization(BusinessAuthentication.Policy);
        catalog.MapGet("/countries", static Ok<CountryCatalogResponse> () => TypedResults.Ok(CatalogHandlers.Countries())).WithName("GetCountryCatalog");
        catalog.MapGet("/time-zones", static async Task<Ok<TimeZoneCatalogResponse>> (HttpContext http, bool? organizationContext, RegionalSettingsHandlers settings, CancellationToken cancellationToken) =>
            {
                string? country = null;
                if (organizationContext == true)
                {
                    country = (await settings.GetAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false)).DefaultCountryCode;
                }

                return TypedResults.Ok(CatalogHandlers.TimeZones(country));
            })
            .WithName("GetTimeZoneCatalog")
            .WithSummary("IANA zones, each once, ordered (organization-country zones first when organizationContext=true, then the persisted order).");
        catalog.MapGet("/languages", static Ok<LanguageCatalogResponse> () => TypedResults.Ok(CatalogHandlers.Languages())).WithName("GetLanguageCatalog");
        catalog.MapGet("/date-formats", static Ok<DateFormatCatalogResponse> () => TypedResults.Ok(CatalogHandlers.DateFormats())).WithName("GetDateFormatCatalog");
        catalog.MapGet("/number-formats", static Ok<NumberFormatCatalogResponse> () => TypedResults.Ok(CatalogHandlers.NumberFormats())).WithName("GetNumberFormatCatalog");

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

        // C-105: the organization's notice of an open owner recovery case and its objection (owners and Security Administrators; the Authority decides).
        var recovery = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization/owner-recovery").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        recovery.MapGet("/", static async Task<Ok<OwnerRecoveryNoticeResponse>> (HttpContext http, OwnerRecoveryNoticeHandlers handlers, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handlers.GetAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetOwnerRecoveryNotice")
            .WithSummary("The organization's open owner recovery case (reference, justification, proposed and declared-unavailable owners, waiting period, approval progress) for enabled owners and Security Administrators; no case for anyone else.");

        recovery.MapPost("/{caseId:guid}/objections", static async Task<Ok<OwnerRecoveryObjectionResponse>> (Guid caseId, OwnerRecoveryObjectionRequest request, HttpContext http, OwnerRecoveryNoticeHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.ObjectAsync(BusinessAuthentication.RequireContext(http), caseId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("ObjectToOwnerRecovery")
            .WithSummary("Objects to the open owner recovery case (explanation, fresh step-up). Returns only after the Authority recorded it; the case then cannot be executed. 404 for anyone who is not an enabled owner or Security Administrator.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

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

    private static string RevisionEtag(long revision) => "\"" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";

    private static long? OptionalIfMatch(HttpContext http)
    {
        var header = http.Request.Headers.IfMatch.ToString().Trim().Trim('"');
        if (header.Length == 0)
        {
            return null;
        }

        return long.TryParse(header, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new Application.Common.BusinessException("PRECONDITION_FAILED", 412, "If-Match must carry the current revision ETag");
    }

    private static long RequireIfMatch(HttpContext http) =>
        OptionalIfMatch(http) ?? throw new Application.Common.BusinessException("PRECONDITION_REQUIRED", 428, "If-Match with the current revision ETag is required");
}
