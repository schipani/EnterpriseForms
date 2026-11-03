using System.Net;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M3: organization regional defaults (PG-043) and personal preferences (PG-007) on the real stack: Authority-decided
/// writes, ETag/If-Match, catalog validation, organization isolation, user scoping, audit, and database check constraints.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class RegionalPreferencesTests(StageBFixture fixture)
{
    private static object Settings(string country = "CA", string zone = "America/Winnipeg", string language = "fr", string date = "dmy-24h", string number = "fr-ca", string reason = "Head office moved to Winnipeg") =>
        new { defaultCountryCode = country, defaultIanaTimeZoneId = zone, defaultLanguageCode = language, defaultDateFormatPresetKey = date, defaultNumberFormatPresetKey = number, changeReason = reason };

    private static object Prefs(string tz = "OrganizationDefault", string? zone = null, string date = "OrganizationDefault", string? datePreset = null, string number = "OrganizationDefault", string? numberPreset = null, string language = "OrganizationDefault", string? languageCode = null) =>
        new { timeZonePreferenceMode = tz, ianaTimeZoneId = zone, dateFormatPreferenceMode = date, dateFormatPresetKey = datePreset, numberFormatPreferenceMode = number, numberFormatPresetKey = numberPreset, languagePreferenceMode = language, languageCode };

    private Task<HttpResult> PutSettings(StageBScenario s, TestUser user, object body, string? ifMatch) =>
        fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/settings", s.Token(user), body, Guid.NewGuid().ToString("N"), ifMatch);

    private Task<HttpResult> PutPrefs(StageBScenario s, TestUser user, object body, string? ifMatch = null) =>
        fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/me/preferences", s.Token(user), body, Guid.NewGuid().ToString("N"), ifMatch);

    [Fact]
    public async Task OrganizationSettingsDefaultToCanadaAndOnlyTheOwnerMayChangeThemWithTheCurrentRevision()
    {
        var s = new StageBScenario(fixture, "regional-settings");
        await s.BootstrapAsync();

        var initial = await fixture.Api.GetAsync("/api/v1/organization/settings", s.Token(s.Clerk));
        Assert.True(initial.IsSuccess, initial.Body);
        Assert.Equal("CA", initial.Root.GetProperty("defaultCountryCode").GetString());
        Assert.Equal("America/Toronto", initial.Root.GetProperty("defaultIanaTimeZoneId").GetString());
        Assert.Equal(0, initial.Root.GetProperty("organizationSettingRevision").GetInt64());
        Assert.True(initial.Root.GetProperty("isSystemDefault").GetBoolean());

        // A member without manage_configuration is refused by the Authority (If-Match present so the refusal is the authorization one).
        var clerk = await PutSettings(s, s.Clerk, Settings(), "\"0\"");
        Assert.Equal(HttpStatusCode.Forbidden, clerk.Status);

        Assert.Equal((HttpStatusCode)428, (await PutSettings(s, s.Designer, Settings(), null)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutSettings(s, s.Designer, Settings(), "\"7\"")).Status);

        // Catalog validation: Europe is not a country, EST is not an IANA zone, presets are keys not patterns.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutSettings(s, s.Designer, Settings(country: "EU"), "\"0\"")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutSettings(s, s.Designer, Settings(zone: "EST"), "\"0\"")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutSettings(s, s.Designer, Settings(zone: "UTC-05:00"), "\"0\"")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutSettings(s, s.Designer, Settings(language: "xx"), "\"0\"")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutSettings(s, s.Designer, Settings(date: "yyyy-MM-dd"), "\"0\"")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutSettings(s, s.Designer, Settings(number: "1,234.56"), "\"0\"")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutSettings(s, s.Designer, Settings(reason: " "), "\"0\"")).Status);

        var saved = await PutSettings(s, s.Designer, Settings(), "\"0\"");
        Assert.True(saved.IsSuccess, saved.Body);
        Assert.Equal(1, saved.Root.GetProperty("organizationSettingRevision").GetInt64());
        Assert.Equal("America/Winnipeg", saved.Root.GetProperty("defaultIanaTimeZoneId").GetString());
        Assert.False(saved.Root.GetProperty("isSystemDefault").GetBoolean());

        // The same stale revision cannot overwrite again.
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutSettings(s, s.Designer, Settings(zone: "America/Edmonton"), "\"0\"")).Status);
        var second = await PutSettings(s, s.Designer, Settings(zone: "America/Edmonton"), "\"1\"");
        Assert.True(second.IsSuccess, second.Body);
        Assert.Equal(2, second.Root.GetProperty("organizationSettingRevision").GetInt64());

        // Every successful change is audited with the reason (not the stored data of anyone).
        var audit = fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'ORGANIZATION_SETTINGS_UPDATED' AND event_metadata_json::text LIKE '%Head office moved%';").Trim();
        Assert.Equal("1", audit);
        Assert.Equal("2", fixture.Stack.AttackerSql($"SELECT organization_setting_revision FROM organization_settings WHERE tenant_id = '{s.OrganizationId:D}';").Trim());
    }

    [Fact]
    public async Task PreferencesAreUserScopedIndependentPerSettingAndIsolatedFromOtherOrganizations()
    {
        var s = new StageBScenario(fixture, "regional-prefs");
        await s.BootstrapAsync();
        var other = new StageBScenario(fixture, "regional-prefs-other");
        await other.BootstrapAsync();

        var initial = await fixture.Api.GetAsync("/api/v1/me/preferences", s.Token(s.Clerk));
        Assert.True(initial.IsSuccess, initial.Body);
        Assert.Equal("OrganizationDefault", initial.Root.GetProperty("timeZonePreferenceMode").GetString());
        Assert.Equal(0, initial.Root.GetProperty("preferenceRevision").GetInt64());

        // Mode/value pairing and catalog membership are enforced for each of the four settings.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutPrefs(s, s.Clerk, Prefs(tz: "Explicit"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutPrefs(s, s.Clerk, Prefs(tz: "BrowserDefault", zone: "America/Toronto"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutPrefs(s, s.Clerk, Prefs(tz: "Explicit", zone: "EST"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutPrefs(s, s.Clerk, Prefs(date: "Explicit", datePreset: "dd/MM/yyyy"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutPrefs(s, s.Clerk, Prefs(number: "Explicit", numberPreset: "bogus"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutPrefs(s, s.Clerk, Prefs(language: "Explicit", languageCode: "tlh"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutPrefs(s, s.Clerk, Prefs(tz: "Sometimes"))).Status);

        var saved = await PutPrefs(s, s.Clerk, Prefs(tz: "Explicit", zone: "America/Winnipeg", date: "BrowserDefault", number: "OrganizationDefault", language: "Explicit", languageCode: "fr"), "\"0\"");
        Assert.True(saved.IsSuccess, saved.Body);
        Assert.Equal("Explicit", saved.Root.GetProperty("timeZonePreferenceMode").GetString());
        Assert.Equal("America/Winnipeg", saved.Root.GetProperty("ianaTimeZoneId").GetString());
        Assert.Equal("BrowserDefault", saved.Root.GetProperty("dateFormatPreferenceMode").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, saved.Root.GetProperty("dateFormatPresetKey").ValueKind);
        Assert.Equal("fr", saved.Root.GetProperty("languageCode").GetString());
        Assert.Equal(1, saved.Root.GetProperty("preferenceRevision").GetInt64());

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutPrefs(s, s.Clerk, Prefs(), "\"0\"")).Status);

        // Another member of the same organization and a member of another organization see their own defaults, never these preferences.
        var sibling = await fixture.Api.GetAsync("/api/v1/me/preferences", s.Token(s.Reader));
        Assert.Equal("OrganizationDefault", sibling.Root.GetProperty("timeZonePreferenceMode").GetString());
        Assert.Equal(0, sibling.Root.GetProperty("preferenceRevision").GetInt64());
        var foreign = await fixture.Api.GetAsync("/api/v1/me/preferences", other.Token(other.Clerk));
        Assert.Equal("OrganizationDefault", foreign.Root.GetProperty("timeZonePreferenceMode").GetString());

        // Another organization's owner settings never leak across: changing this organization leaves the other at the system default.
        Assert.True((await PutSettings(s, s.Designer, Settings(), "\"0\"")).IsSuccess);
        var otherSettings = await fixture.Api.GetAsync("/api/v1/organization/settings", other.Token(other.Clerk));
        Assert.Equal("America/Toronto", otherSettings.Root.GetProperty("defaultIanaTimeZoneId").GetString());
        Assert.True(otherSettings.Root.GetProperty("isSystemDefault").GetBoolean());
        var mine = await fixture.Api.GetAsync("/api/v1/me/preferences", s.Token(s.Clerk));
        Assert.Equal("America/Winnipeg", mine.Root.GetProperty("organizationDefaults").GetProperty("defaultIanaTimeZoneId").GetString());

        Assert.Equal("1", fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'USER_PREFERENCES_UPDATED';").Trim());

        // The database itself refuses an Explicit mode without a value (check constraint), and the row stays as saved.
        fixture.Stack.AttackerSql($"UPDATE user_preferences SET iana_time_zone_id = NULL WHERE tenant_id = '{s.OrganizationId:D}';", throwOnError: false);
        Assert.Equal("America/Winnipeg", fixture.Stack.AttackerSql($"SELECT iana_time_zone_id FROM user_preferences WHERE tenant_id = '{s.OrganizationId:D}';").Trim());
    }

    [Fact]
    public async Task TheCatalogIsAuthenticatedOnlyCanadianZonesLeadForACanadianOrganizationAndEuropeIsNotACountry()
    {
        var s = new StageBScenario(fixture, "regional-catalog");
        await s.BootstrapAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Api.GetAsync("/api/v1/catalog/time-zones", string.Empty)).Status);

        var zones = await fixture.Api.GetAsync("/api/v1/catalog/time-zones?organizationContext=true", s.Token(s.Clerk));
        Assert.True(zones.IsSuccess, zones.Body);
        var ids = zones.Root.GetProperty("timeZones").EnumerateArray().Select(z => z.GetProperty("ianaTimeZoneId").GetString()!).ToList();
        Assert.Equal("America/Toronto", ids[0]);
        Assert.Equal("America/Winnipeg", ids[1]);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var firstNonCanadian = ids.FindIndex(id => !zones.Root.GetProperty("timeZones").EnumerateArray().First(z => z.GetProperty("ianaTimeZoneId").GetString() == id).GetProperty("countryCodes").EnumerateArray().Any(c => c.GetString() == "CA"));
        Assert.True(firstNonCanadian > 5);
        Assert.All(ids.Take(firstNonCanadian), id => Assert.StartsWith("America/", id, StringComparison.Ordinal));

        var countries = await fixture.Api.GetAsync("/api/v1/catalog/countries", s.Token(s.Clerk));
        var codes = countries.Root.GetProperty("countries").EnumerateArray().Select(c => c.GetProperty("countryCode").GetString()).ToList();
        Assert.Equal(["CA", "US", "MX"], codes.Take(3));
        Assert.DoesNotContain("EU", codes);

        var languages = await fixture.Api.GetAsync("/api/v1/catalog/languages", s.Token(s.Clerk));
        Assert.Equal(10, languages.Root.GetProperty("languages").GetArrayLength());
        Assert.True((await fixture.Api.GetAsync("/api/v1/catalog/date-formats", s.Token(s.Clerk))).IsSuccess);
        Assert.True((await fixture.Api.GetAsync("/api/v1/catalog/number-formats", s.Token(s.Clerk))).IsSuccess);
    }
}
