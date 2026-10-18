using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Documents;

namespace EnterpriseForms.Application.Features.Dashboard;

/// <summary>
/// Dashboard composition (PG-006): widget descriptors from a static catalog filtered by authority-derived capabilities;
/// counts are computed only over resources discoverable by the caller. No arbitrary endpoints, no cross-tenant counts.
/// </summary>
public sealed class DashboardHandler(IBusinessStore store, IAuthorizationAuthorityClient authority)
{
    public static readonly IReadOnlyList<string> CapabilityNames = ["records.discover", "forms.design", "organization.manage"];

    public async Task<IReadOnlyList<string>> CapabilitiesAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var decisions = await authority.CheckPermissionsBatchAsync(context.ToDelegatedUser(),
        [
            new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.DesignForm),
            new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.ManageOrganization),
        ], cancellationToken).ConfigureAwait(false);
        var capabilities = new List<string> { "records.discover" };
        if (decisions[0].Allowed)
        {
            capabilities.Add("forms.design");
        }

        if (decisions[1].Allowed)
        {
            capabilities.Add("organization.manage");
        }

        return capabilities;
    }

    public async Task<DashboardResponse> GetAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var capabilities = await CapabilitiesAsync(context, cancellationToken).ConfigureAwait(false);
        var widgets = new List<DashboardWidgetDefinitionResponse>
        {
            new("my-records", "My Records", "record-list", 2, "records.list"),
            new("upload-activity", "Upload Activity", "upload-status", 2, "uploads.mine"),
            new("recent-activity", "Recent Personal Activity", "activity", 2, "audit.mine"),
            new("permitted-forms", "Permitted Forms", "action-list", 2, "forms.list"),
        };
        if (capabilities.Contains("forms.design"))
        {
            widgets.Add(new("my-designs", "My Designs", "action-list", 2, "forms.list"));
        }

        if (capabilities.Contains("organization.manage"))
        {
            widgets.Add(new("organization-branding", "Organization Branding", "action-list", 2, "organization.branding"));
        }

        return new DashboardResponse(widgets, capabilities);
    }

    public async Task<DashboardCountResponse> CountAsync(RequestContext context, string widgetIdentifier, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);
        switch (widgetIdentifier)
        {
            case "my-records":
            {
                var page = await authority.DiscoverAuthorizedRecordsAsync(context.ToDelegatedUser(), context.OrganizationId, 100, null, cancellationToken).ConfigureAwait(false);
                await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
                var records = await scope.GetFormRecordsAsync(context.OrganizationId, page.FormRecordIds, cancellationToken).ConfigureAwait(false);
                return new DashboardCountResponse(widgetIdentifier, records.Count(r => r.ParentFormRecordId is null), clock.GetUtcNow(), "authority-discoverable top-level records (first 100)");
            }

            case "upload-activity":
            {
                await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
                var sessions = await scope.GetUploadSessionsForUserAsync(context.OrganizationId, context.LocalUserId, [UploadSessionStates.Open, UploadSessionStates.Completed, UploadSessionStates.Processing, UploadSessionStates.Processed], cancellationToken).ConfigureAwait(false);
                return new DashboardCountResponse(widgetIdentifier, sessions.Count, clock.GetUtcNow(), "own uploads in progress");
            }

            default:
                throw new BusinessException("NOT_FOUND", 404, "unknown widget");
        }
    }
}
