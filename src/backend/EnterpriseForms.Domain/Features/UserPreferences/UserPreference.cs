namespace EnterpriseForms.Domain.Features.UserPreferences;

/// <summary>
/// Personal regional preferences (C8 M3, PG-007): time zone, date format, number format and language, each independently
/// OrganizationDefault | BrowserDefault | Explicit. A missing row means OrganizationDefault for all four. Presentation only.
/// Kept separate from the text-size <see cref="UserUiPreference"/>.
/// </summary>
public sealed class UserPreference
{
    public Guid UserPreferenceId { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public string TimeZonePreferenceMode { get; set; } = "OrganizationDefault";

    public string? IanaTimeZoneId { get; set; }

    public string DateFormatPreferenceMode { get; set; } = "OrganizationDefault";

    public string? DateFormatPresetKey { get; set; }

    public string NumberFormatPreferenceMode { get; set; } = "OrganizationDefault";

    public string? NumberFormatPresetKey { get; set; }

    public string LanguagePreferenceMode { get; set; } = "OrganizationDefault";

    public string? LanguageCode { get; set; }

    public long PreferenceRevision { get; set; } = 1;

    public DateTimeOffset PreferenceCreatedOn { get; set; }

    public DateTimeOffset PreferenceModifiedOn { get; set; }
}
