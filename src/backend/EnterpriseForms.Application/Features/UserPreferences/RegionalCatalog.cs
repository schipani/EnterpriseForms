using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.UserPreferences;

/// <summary>
/// The typed, versioned, server-validated regional catalog (C8 M3; CLAUDE.md §4E allows a code catalog before the relational
/// §4F/§4G catalogs). Keys are stable identifiers; the browser never supplies a free-form format. Europe is a region, not a
/// country: only real ISO 3166-1 countries are selectable. Ordering is (context priority, persisted sort order, key).
/// </summary>
public static class RegionalCatalog
{
    /// <summary>Bump on any change to the lists below; clients cache by it.</summary>
    public const string Version = "2026.11.1";

    public const string DefaultCountryCode = "CA";
    public const string DefaultTimeZoneId = "America/Toronto";
    public const string DefaultLanguageCode = "en";
    public const string DefaultDatePresetKey = "iso-24h";
    public const string DefaultNumberPresetKey = "en-ca";

    private static readonly CountryCatalogEntry[] CountryList =
    [
        new("CA", "Canada", "NorthAmerica", 1),
        new("US", "United States", "NorthAmerica", 2),
        new("MX", "Mexico", "NorthAmerica", 3),
        new("GB", "United Kingdom", "Europe", 4),
        new("IE", "Ireland", "Europe", 5),
        new("FR", "France", "Europe", 6),
        new("DE", "Germany", "Europe", 7),
        new("IT", "Italy", "Europe", 8),
        new("ES", "Spain", "Europe", 9),
        new("PT", "Portugal", "Europe", 10),
        new("NL", "Netherlands", "Europe", 11),
        new("BE", "Belgium", "Europe", 12),
        new("CH", "Switzerland", "Europe", 13),
        new("AT", "Austria", "Europe", 14),
        new("SE", "Sweden", "Europe", 15),
        new("NO", "Norway", "Europe", 16),
        new("DK", "Denmark", "Europe", 17),
        new("FI", "Finland", "Europe", 18),
        new("PL", "Poland", "Europe", 19),
    ];

    private sealed record Zone(string Id, string Name, string[] Countries, int Order);

    private static readonly Zone[] ZoneList =
    [
        new("America/Toronto", "Eastern Time (Toronto)", ["CA"], 1),
        new("America/Winnipeg", "Central Time (Winnipeg)", ["CA"], 2),
        new("America/Edmonton", "Mountain Time (Edmonton)", ["CA"], 3),
        new("America/Vancouver", "Pacific Time (Vancouver)", ["CA"], 4),
        new("America/Halifax", "Atlantic Time (Halifax)", ["CA"], 5),
        new("America/St_Johns", "Newfoundland Time (St. John's)", ["CA"], 6),
        new("America/Regina", "Central Standard Time (Regina)", ["CA"], 7),
        new("America/Whitehorse", "Yukon Time (Whitehorse)", ["CA"], 8),
        new("America/Iqaluit", "Eastern Time (Iqaluit)", ["CA"], 9),
        new("America/New_York", "Eastern Time (New York)", ["US"], 10),
        new("America/Chicago", "Central Time (Chicago)", ["US"], 11),
        new("America/Denver", "Mountain Time (Denver)", ["US"], 12),
        new("America/Phoenix", "Mountain Standard Time (Phoenix)", ["US"], 13),
        new("America/Los_Angeles", "Pacific Time (Los Angeles)", ["US"], 14),
        new("America/Anchorage", "Alaska Time (Anchorage)", ["US"], 15),
        new("Pacific/Honolulu", "Hawaii Time (Honolulu)", ["US"], 16),
        new("America/Mexico_City", "Central Time (Mexico City)", ["MX"], 17),
        new("America/Monterrey", "Central Time (Monterrey)", ["MX"], 18),
        new("America/Cancun", "Eastern Standard Time (Cancún)", ["MX"], 19),
        new("America/Chihuahua", "Mountain Time (Chihuahua)", ["MX"], 20),
        new("America/Tijuana", "Pacific Time (Tijuana)", ["MX"], 21),
        new("Europe/London", "UK Time (London)", ["GB"], 22),
        new("Europe/Dublin", "Irish Time (Dublin)", ["IE"], 23),
        new("Europe/Lisbon", "Western European Time (Lisbon)", ["PT"], 24),
        new("Europe/Paris", "Central European Time (Paris)", ["FR"], 25),
        new("Europe/Berlin", "Central European Time (Berlin)", ["DE"], 26),
        new("Europe/Rome", "Central European Time (Rome)", ["IT"], 27),
        new("Europe/Madrid", "Central European Time (Madrid)", ["ES"], 28),
        new("Europe/Amsterdam", "Central European Time (Amsterdam)", ["NL"], 29),
        new("Europe/Brussels", "Central European Time (Brussels)", ["BE"], 30),
        new("Europe/Zurich", "Central European Time (Zurich)", ["CH"], 31),
        new("Europe/Vienna", "Central European Time (Vienna)", ["AT"], 32),
        new("Europe/Stockholm", "Central European Time (Stockholm)", ["SE"], 33),
        new("Europe/Oslo", "Central European Time (Oslo)", ["NO"], 34),
        new("Europe/Copenhagen", "Central European Time (Copenhagen)", ["DK"], 35),
        new("Europe/Warsaw", "Central European Time (Warsaw)", ["PL"], 36),
        new("Europe/Helsinki", "Eastern European Time (Helsinki)", ["FI"], 37),
        new("Etc/UTC", "Coordinated Universal Time (UTC)", [], 99),
    ];

    private static readonly LanguageCatalogEntry[] LanguageList =
    [
        new("en", "English", "English", 1),
        new("fr", "French", "Français", 2),
        new("es", "Spanish", "Español", 3),
        new("it", "Italian", "Italiano", 4),
        new("zh-Hans", "Simplified Chinese", "简体中文", 5),
        new("de", "German", "Deutsch", 6),
        new("pt", "Portuguese", "Português", 7),
        new("ar", "Arabic", "العربية", 8),
        new("hi", "Hindi", "हिन्दी", 9),
        new("ru", "Russian", "Русский", 10),
    ];

    private static readonly DateFormatPresetEntry[] DateList =
    [
        new("iso-24h", "2026-11-03, 24-hour clock", "en-CA", "2-digit", "h23", 1),
        new("iso-12h", "2026-11-03, 12-hour clock", "en-CA", "2-digit", "h12", 2),
        new("dmy-24h", "03/11/2026, 24-hour clock", "en-GB", "2-digit", "h23", 3),
        new("mdy-12h", "11/03/2026, 12-hour clock", "en-US", "2-digit", "h12", 4),
        new("dmy-text-24h", "03 Nov 2026, 24-hour clock", "en-GB", "short", "h23", 5),
        new("mdy-text-12h", "Nov 3, 2026, 12-hour clock", "en-US", "short", "h12", 6),
    ];

    private static readonly NumberFormatPresetEntry[] NumberList =
    [
        new("en-ca", "1,234.56 (Canada, English)", "en-CA", "1,234.56", 1),
        new("fr-ca", "1 234,56 (Canada, French)", "fr-CA", "1 234,56", 2),
        new("en-us", "1,234.56 (United States)", "en-US", "1,234.56", 3),
        new("es-mx", "1,234.56 (Mexico)", "es-MX", "1,234.56", 4),
        new("de-de", "1.234,56 (Germany)", "de-DE", "1.234,56", 5),
        new("fr-fr", "1 234,56 (France)", "fr-FR", "1 234,56", 6),
        new("en-in", "1,23,456.78 (India grouping)", "en-IN", "1,23,456.78", 7),
    ];

    public static IReadOnlyList<CountryCatalogEntry> Countries => CountryList;

    public static IReadOnlyList<LanguageCatalogEntry> Languages => LanguageList;

    public static IReadOnlyList<DateFormatPresetEntry> DatePresets => DateList;

    public static IReadOnlyList<NumberFormatPresetEntry> NumberPresets => NumberList;

    public static bool IsCountry(string? code) => code is not null && CountryList.Any(c => c.CountryCode == code);

    public static bool IsTimeZone(string? id) => id is not null && ZoneList.Any(z => z.Id == id);

    public static bool IsLanguage(string? code) => code is not null && LanguageList.Any(l => l.LanguageCode == code);

    public static bool IsDatePreset(string? key) => key is not null && DateList.Any(p => p.DateFormatPresetKey == key);

    public static bool IsNumberPreset(string? key) => key is not null && NumberList.Any(p => p.NumberFormatPresetKey == key);

    /// <summary>Zones ordered (organization-country first, persisted order, id); each zone appears once.</summary>
    public static IReadOnlyList<TimeZoneCatalogEntry> TimeZonesFor(string? organizationCountryCode) =>
        ZoneList
            .Select(z => new TimeZoneCatalogEntry(z.Id, z.Name, z.Countries, z.Order, organizationCountryCode is not null && z.Countries.Contains(organizationCountryCode) ? 0 : 1))
            .OrderBy(z => z.ContextPriority).ThenBy(z => z.TimeZoneSortOrder).ThenBy(z => z.IanaTimeZoneId, StringComparer.Ordinal)
            .ToList();
}
