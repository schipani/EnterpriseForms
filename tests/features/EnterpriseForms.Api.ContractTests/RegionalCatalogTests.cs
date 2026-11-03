using EnterpriseForms.Application.Features.UserPreferences;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>C8 M3: the typed regional catalog is internally consistent and its defaults are members of it (Canada-first).</summary>
public sealed class RegionalCatalogTests
{
    [Fact]
    public void DefaultsAreCatalogMembersAndCanadaFirst()
    {
        Assert.True(RegionalCatalog.IsCountry(RegionalCatalog.DefaultCountryCode));
        Assert.True(RegionalCatalog.IsTimeZone(RegionalCatalog.DefaultTimeZoneId));
        Assert.True(RegionalCatalog.IsLanguage(RegionalCatalog.DefaultLanguageCode));
        Assert.True(RegionalCatalog.IsDatePreset(RegionalCatalog.DefaultDatePresetKey));
        Assert.True(RegionalCatalog.IsNumberPreset(RegionalCatalog.DefaultNumberPresetKey));
        Assert.Equal(["CA", "US", "MX"], RegionalCatalog.Countries.Take(3).Select(c => c.CountryCode));
    }

    [Fact]
    public void EuropeIsARegionNotACountryAndKeysAreUnique()
    {
        Assert.False(RegionalCatalog.IsCountry("EU"));
        Assert.All(RegionalCatalog.Countries, c => Assert.Matches("^[A-Z]{2}$", c.CountryCode));
        Assert.Equal(RegionalCatalog.Countries.Count, RegionalCatalog.Countries.Select(c => c.CountryCode).Distinct().Count());
        Assert.Equal(RegionalCatalog.DatePresets.Count, RegionalCatalog.DatePresets.Select(p => p.DateFormatPresetKey).Distinct().Count());
        Assert.Equal(RegionalCatalog.NumberPresets.Count, RegionalCatalog.NumberPresets.Select(p => p.NumberFormatPresetKey).Distinct().Count());
    }

    [Fact]
    public void TenLanguagesInPersistedOrder()
    {
        Assert.Equal(["en", "fr", "es", "it", "zh-Hans", "de", "pt", "ar", "hi", "ru"], RegionalCatalog.Languages.Select(l => l.LanguageCode));
    }

    [Theory]
    [InlineData("EST")]
    [InlineData("PST")]
    [InlineData("UTC-05:00")]
    [InlineData("america/toronto")]
    [InlineData("")]
    [InlineData(null)]
    public void OffsetsAliasesAndWrongCaseAreNotTimeZones(string? id) => Assert.False(RegionalCatalog.IsTimeZone(id));

    [Fact]
    public void ZonesAreDeduplicatedAndCanadianZonesLeadForACanadianOrganization()
    {
        var zones = RegionalCatalog.TimeZonesFor("CA");
        Assert.Equal(zones.Count, zones.Select(z => z.IanaTimeZoneId).Distinct().Count());
        Assert.Equal(["America/Toronto", "America/Winnipeg", "America/Edmonton", "America/Vancouver", "America/Halifax", "America/St_Johns"], zones.Take(6).Select(z => z.IanaTimeZoneId));
        var lastCanadian = zones.ToList().FindLastIndex(z => z.ContextPriority == 0);
        Assert.True(zones.Take(lastCanadian + 1).All(z => z.CountryCodes.Contains("CA")));
        Assert.Equal("Etc/UTC", zones[^1].IanaTimeZoneId);
    }

    [Fact]
    public void AMexicanOrganizationListsMexicanZonesFirstAndNeverHidesTheRest()
    {
        var zones = RegionalCatalog.TimeZonesFor("MX");
        Assert.Equal("America/Mexico_City", zones[0].IanaTimeZoneId);
        Assert.Contains(zones, z => z.IanaTimeZoneId == "America/Toronto");
        Assert.Equal(RegionalCatalog.TimeZonesFor(null).Count, zones.Count);
    }
}
