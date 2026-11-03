using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.UserPreferences;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Organizations;

namespace EnterpriseForms.Application.Features.Organizations;

/// <summary>
/// Organization regional defaults (C8 M3, PG-043). Reading is open to every member (the personal preferences page needs the
/// defaults); writing needs the Authority's manage_configuration decision on every call, a reason, and the current revision
/// (If-Match). The values are presentation configuration: they never touch stored instants, authorization, encryption or search.
/// </summary>
public sealed class RegionalSettingsHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, TimeProvider clock)
{
    public async Task<OrganizationSettingsResponse> GetAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        return ToResponse(await scope.FindOrganizationSettingAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<OrganizationSettingsResponse> UpdateAsync(RequestContext context, UpdateOrganizationSettingsRequest request, long expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await AdministrationAccess.RequireOwnerAsync(authority, context, cancellationToken).ConfigureAwait(false);
        Validate(request);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var setting = await scope.FindOrganizationSettingAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var currentRevision = setting?.OrganizationSettingRevision ?? 0;
        if (currentRevision != expectedRevision)
        {
            throw new BusinessException("SETTINGS_REVISION_CONFLICT", 412, "the regional settings were changed by someone else; reload");
        }

        var now = clock.GetUtcNow();
        var previous = setting is null ? null : new { setting.DefaultCountryCode, setting.DefaultIanaTimeZoneId, setting.DefaultLanguageCode, setting.DefaultDateFormatPresetKey, setting.DefaultNumberFormatPresetKey };
        if (setting is null)
        {
            setting = new OrganizationSetting { OrganizationSettingId = Guid.CreateVersion7(), TenantId = context.OrganizationId, OrganizationSettingRevision = 0, SettingCreatedOn = now, SettingCreatedByUserId = context.LocalUserId };
            scope.AddOrganizationSetting(setting);
        }

        setting.DefaultCountryCode = request.DefaultCountryCode;
        setting.DefaultIanaTimeZoneId = request.DefaultIanaTimeZoneId;
        setting.DefaultLanguageCode = request.DefaultLanguageCode;
        setting.DefaultDateFormatPresetKey = request.DefaultDateFormatPresetKey;
        setting.DefaultNumberFormatPresetKey = request.DefaultNumberFormatPresetKey;
        setting.OrganizationSettingRevision++;
        setting.SettingModifiedOn = now;
        setting.SettingModifiedByUserId = context.LocalUserId;
        AuditRecorder.Record(scope, context, clock, "Organization", AuditActions.OrganizationSettingsUpdated, "organization regional defaults updated", targetEntityType: "organization_settings", targetEntityId: setting.OrganizationSettingId,
            metadata: new { revision = setting.OrganizationSettingRevision, previous, current = new { setting.DefaultCountryCode, setting.DefaultIanaTimeZoneId, setting.DefaultLanguageCode, setting.DefaultDateFormatPresetKey, setting.DefaultNumberFormatPresetKey }, reason = request.ChangeReason.Trim() });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResponse(setting);
    }

    public static OrganizationSettingsResponse ToResponse(OrganizationSetting? setting) =>
        setting is null
            ? new OrganizationSettingsResponse(RegionalCatalog.DefaultCountryCode, RegionalCatalog.DefaultTimeZoneId, RegionalCatalog.DefaultLanguageCode, RegionalCatalog.DefaultDatePresetKey, RegionalCatalog.DefaultNumberPresetKey, 0, true, RegionalCatalog.Version)
            : new OrganizationSettingsResponse(setting.DefaultCountryCode, setting.DefaultIanaTimeZoneId, setting.DefaultLanguageCode, setting.DefaultDateFormatPresetKey, setting.DefaultNumberFormatPresetKey, setting.OrganizationSettingRevision, false, RegionalCatalog.Version);

    private static void Validate(UpdateOrganizationSettingsRequest request)
    {
        var reason = request.ChangeReason?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 500)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a reason of 3..500 characters is required");
        }

        if (!RegionalCatalog.IsCountry(request.DefaultCountryCode))
        {
            throw new BusinessException("INVALID_COUNTRY", 422, "the default country is not in the catalog (Europe is a region: choose a country)");
        }

        if (!RegionalCatalog.IsTimeZone(request.DefaultIanaTimeZoneId))
        {
            throw new BusinessException("INVALID_TIME_ZONE", 422, "the default time zone is not a supported IANA zone");
        }

        if (!RegionalCatalog.IsLanguage(request.DefaultLanguageCode))
        {
            throw new BusinessException("INVALID_LANGUAGE", 422, "the default language is not supported");
        }

        if (!RegionalCatalog.IsDatePreset(request.DefaultDateFormatPresetKey))
        {
            throw new BusinessException("INVALID_DATE_FORMAT", 422, "the default date format preset is not in the catalog");
        }

        if (!RegionalCatalog.IsNumberPreset(request.DefaultNumberFormatPresetKey))
        {
            throw new BusinessException("INVALID_NUMBER_FORMAT", 422, "the default number format preset is not in the catalog");
        }
    }
}
