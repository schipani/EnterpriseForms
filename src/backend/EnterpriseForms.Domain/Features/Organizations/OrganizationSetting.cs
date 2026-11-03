namespace EnterpriseForms.Domain.Features.Organizations;

/// <summary>
/// Organization regional defaults (C8 M3, PG-043): country, IANA time zone, language, date and number presets. Presentation
/// configuration only: it never changes stored instants, authorization, encryption, integrity or search. One row per tenant;
/// optimistic concurrency by <see cref="OrganizationSettingRevision"/>. Keys are validated against the typed code catalog.
/// </summary>
public sealed class OrganizationSetting
{
    public Guid OrganizationSettingId { get; set; }

    public Guid TenantId { get; set; }

    public string DefaultCountryCode { get; set; } = "CA";

    public string DefaultIanaTimeZoneId { get; set; } = "America/Toronto";

    public string DefaultLanguageCode { get; set; } = "en";

    public string DefaultDateFormatPresetKey { get; set; } = "iso-24h";

    public string DefaultNumberFormatPresetKey { get; set; } = "en-ca";

    public long OrganizationSettingRevision { get; set; } = 1;

    public DateTimeOffset SettingCreatedOn { get; set; }

    public Guid SettingCreatedByUserId { get; set; }

    public DateTimeOffset SettingModifiedOn { get; set; }

    public Guid SettingModifiedByUserId { get; set; }
}
