using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.Organizations;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Organizations;
using EnterpriseForms.Domain.Features.UserPreferences;

namespace EnterpriseForms.Application.Features.UserPreferences;

/// <summary>
/// Personal regional preferences (C8 M3, PG-007). Only the caller's own row in the caller's organization is ever read or
/// written: no user id is accepted from the client. A missing row means OrganizationDefault for all four. Presentation only.
/// </summary>
public sealed class PreferenceHandlers(IBusinessStore store, TimeProvider clock)
{
    private static readonly string[] Modes = ["OrganizationDefault", "BrowserDefault", "Explicit"];

    public async Task<UserPreferencesResponse> GetAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var preference = await scope.FindUserPreferenceAsync(context.OrganizationId, context.LocalUserId, cancellationToken).ConfigureAwait(false);
        var organization = await scope.FindOrganizationSettingAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        return ToResponse(preference, organization);
    }

    public async Task<UserPreferencesResponse> UpdateAsync(RequestContext context, UpdateUserPreferencesRequest request, long? expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await scope.EnsureTenantAndUserProjectionAsync(context.OrganizationId, context.LocalUserId, context.IssuerUri, context.SubjectIdentifier, context.DisplayName, cancellationToken).ConfigureAwait(false);
        var preference = await scope.FindUserPreferenceAsync(context.OrganizationId, context.LocalUserId, cancellationToken).ConfigureAwait(false);
        var currentRevision = preference?.PreferenceRevision ?? 0;
        if (expectedRevision is { } expected && expected != currentRevision)
        {
            throw new BusinessException("PREFERENCE_REVISION_CONFLICT", 412, "your preferences were changed elsewhere; reload");
        }

        var now = clock.GetUtcNow();
        if (preference is null)
        {
            preference = new UserPreference { UserPreferenceId = Guid.CreateVersion7(), TenantId = context.OrganizationId, UserId = context.LocalUserId, PreferenceRevision = 0, PreferenceCreatedOn = now };
            scope.AddUserPreference(preference);
        }

        preference.TimeZonePreferenceMode = request.TimeZonePreferenceMode;
        preference.IanaTimeZoneId = request.IanaTimeZoneId;
        preference.DateFormatPreferenceMode = request.DateFormatPreferenceMode;
        preference.DateFormatPresetKey = request.DateFormatPresetKey;
        preference.NumberFormatPreferenceMode = request.NumberFormatPreferenceMode;
        preference.NumberFormatPresetKey = request.NumberFormatPresetKey;
        preference.LanguagePreferenceMode = request.LanguagePreferenceMode;
        preference.LanguageCode = request.LanguageCode;
        preference.PreferenceRevision++;
        preference.PreferenceModifiedOn = now;
        // Metadata carries modes only (never browser-detected values, which are not stored).
        AuditRecorder.Record(scope, context, clock, "Preferences", AuditActions.UserPreferencesUpdated, "personal regional preferences updated", targetEntityType: "user_preferences", targetEntityId: preference.UserPreferenceId,
            metadata: new { revision = preference.PreferenceRevision, timeZone = request.TimeZonePreferenceMode, dateFormat = request.DateFormatPreferenceMode, numberFormat = request.NumberFormatPreferenceMode, language = request.LanguagePreferenceMode });
        var organization = await scope.FindOrganizationSettingAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResponse(preference, organization);
    }

    public static UserPreferencesResponse ToResponse(UserPreference? p, OrganizationSetting? organization) =>
        new(p?.TimeZonePreferenceMode ?? "OrganizationDefault", p?.IanaTimeZoneId, p?.DateFormatPreferenceMode ?? "OrganizationDefault", p?.DateFormatPresetKey,
            p?.NumberFormatPreferenceMode ?? "OrganizationDefault", p?.NumberFormatPresetKey, p?.LanguagePreferenceMode ?? "OrganizationDefault", p?.LanguageCode,
            p?.PreferenceRevision ?? 0, RegionalSettingsHandlers.ToResponse(organization));

    private static void Validate(UpdateUserPreferencesRequest r)
    {
        Check(r.TimeZonePreferenceMode, r.IanaTimeZoneId, RegionalCatalog.IsTimeZone, "INVALID_TIME_ZONE", "time zone");
        Check(r.DateFormatPreferenceMode, r.DateFormatPresetKey, RegionalCatalog.IsDatePreset, "INVALID_DATE_FORMAT", "date format");
        Check(r.NumberFormatPreferenceMode, r.NumberFormatPresetKey, RegionalCatalog.IsNumberPreset, "INVALID_NUMBER_FORMAT", "number format");
        Check(r.LanguagePreferenceMode, r.LanguageCode, RegionalCatalog.IsLanguage, "INVALID_LANGUAGE", "language");
    }

    private static void Check(string? mode, string? value, Func<string?, bool> isValid, string code, string label)
    {
        if (mode is null || !Modes.Contains(mode))
        {
            throw new BusinessException("INVALID_REQUEST", 422, $"the {label} mode must be OrganizationDefault, BrowserDefault or Explicit");
        }

        if (mode == "Explicit")
        {
            if (!isValid(value))
            {
                throw new BusinessException(code, 422, $"the explicit {label} is not in the catalog");
            }
        }
        else if (value is not null)
        {
            throw new BusinessException("INVALID_REQUEST", 422, $"a {label} value is only allowed in Explicit mode");
        }
    }
}

/// <summary>Read-only catalog projections (C8 M3). Authentication only: the catalog is public reference data, never tenant data.</summary>
public static class CatalogHandlers
{
    public static CountryCatalogResponse Countries() => new(RegionalCatalog.Version, RegionalCatalog.Countries);

    public static TimeZoneCatalogResponse TimeZones(string? organizationCountryCode)
    {
        var country = RegionalCatalog.IsCountry(organizationCountryCode) ? organizationCountryCode : null;
        return new TimeZoneCatalogResponse(RegionalCatalog.Version, country, RegionalCatalog.TimeZonesFor(country));
    }

    public static LanguageCatalogResponse Languages() => new(RegionalCatalog.Version, RegionalCatalog.Languages);

    public static DateFormatCatalogResponse DateFormats() => new(RegionalCatalog.Version, RegionalCatalog.DatePresets);

    public static NumberFormatCatalogResponse NumberFormats() => new(RegionalCatalog.Version, RegionalCatalog.NumberPresets);
}
