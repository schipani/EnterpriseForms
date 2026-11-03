namespace EnterpriseForms.Contracts.Features;

/// <summary>Organization regional defaults (C8 M3, PG-043). Presentation configuration only.</summary>
public sealed record OrganizationSettingsResponse(
    string DefaultCountryCode,
    string DefaultIanaTimeZoneId,
    string DefaultLanguageCode,
    string DefaultDateFormatPresetKey,
    string DefaultNumberFormatPresetKey,
    long OrganizationSettingRevision,
    bool IsSystemDefault,
    string CatalogVersion);

public sealed record UpdateOrganizationSettingsRequest(
    string DefaultCountryCode,
    string DefaultIanaTimeZoneId,
    string DefaultLanguageCode,
    string DefaultDateFormatPresetKey,
    string DefaultNumberFormatPresetKey,
    string ChangeReason);

/// <summary>Personal preferences (C8 M3, PG-007). Each mode is OrganizationDefault | BrowserDefault | Explicit; the value is present exactly when Explicit.</summary>
public sealed record UserPreferencesResponse(
    string TimeZonePreferenceMode,
    string? IanaTimeZoneId,
    string DateFormatPreferenceMode,
    string? DateFormatPresetKey,
    string NumberFormatPreferenceMode,
    string? NumberFormatPresetKey,
    string LanguagePreferenceMode,
    string? LanguageCode,
    long PreferenceRevision,
    OrganizationSettingsResponse OrganizationDefaults);

public sealed record UpdateUserPreferencesRequest(
    string TimeZonePreferenceMode,
    string? IanaTimeZoneId,
    string DateFormatPreferenceMode,
    string? DateFormatPresetKey,
    string NumberFormatPreferenceMode,
    string? NumberFormatPresetKey,
    string LanguagePreferenceMode,
    string? LanguageCode);

public sealed record CountryCatalogEntry(string CountryCode, string CountryDisplayName, string GeographicRegionCode, int DisplaySortOrder);

public sealed record CountryCatalogResponse(string CatalogVersion, IReadOnlyList<CountryCatalogEntry> Countries);

/// <summary>An IANA zone; ContextPriority is 0 for the organization's country, else 1. Order is (priority, persisted order, id).</summary>
public sealed record TimeZoneCatalogEntry(string IanaTimeZoneId, string TimeZoneDisplayName, IReadOnlyList<string> CountryCodes, int TimeZoneSortOrder, int ContextPriority);

public sealed record TimeZoneCatalogResponse(string CatalogVersion, string? OrganizationCountryCode, IReadOnlyList<TimeZoneCatalogEntry> TimeZones);

public sealed record LanguageCatalogEntry(string LanguageCode, string LanguageDisplayName, string NativeLanguageName, int DisplaySortOrder);

public sealed record LanguageCatalogResponse(string CatalogVersion, IReadOnlyList<LanguageCatalogEntry> Languages);

/// <summary>A date/time preset: IntlLocaleTag plus structured options so every client formats identically (never a free-form pattern).</summary>
public sealed record DateFormatPresetEntry(string DateFormatPresetKey, string PresetDisplayName, string IntlLocaleTag, string MonthStyle, string HourCycle, int DisplaySortOrder);

public sealed record DateFormatCatalogResponse(string CatalogVersion, IReadOnlyList<DateFormatPresetEntry> Presets);

public sealed record NumberFormatPresetEntry(string NumberFormatPresetKey, string PresetDisplayName, string IntlLocaleTag, string ExampleText, int DisplaySortOrder);

public sealed record NumberFormatCatalogResponse(string CatalogVersion, IReadOnlyList<NumberFormatPresetEntry> Presets);
