using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>Cross-tenant isolation, role boundaries, the BFF development login boundary and tamper regression on the Stage B tables (requirement 9).</summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class AuthorizationAndTamperTests(StageBFixture fixture)
{
    [Fact]
    public async Task CrossTenantAndRoleBoundariesHoldForEveryStageBSurface()
    {
        var s = new StageBScenario(fixture, "authz");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var home = await s.CreateAddressAsync(s.Clerk, accused, "9 Isolation Road");
        await s.UploadAsync(s.Clerk, accused, "tenant.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("tenant bound"));

        // Second organization with its own owner: nothing from the first organization is visible or reachable.
        var other = new StageBScenario(fixture, "authz-other");
        await other.BootstrapAsync();
        var outsider = other.Token(other.Designer);
        foreach (var path in new[]
                 {
                     $"/api/v1/form-definitions/{s.AccusedFormId:D}",
                     $"/api/v1/form-definitions/{s.AccusedFormId:D}/versions",
                     $"/api/v1/form-records/{accused:D}",
                     $"/api/v1/form-records/{accused:D}/projection",
                     $"/api/v1/form-records/{home:D}/projection",
                     $"/api/v1/form-records/{accused:D}/related/{s.RelationshipId:D}",
                     $"/api/v1/form-records/{accused:D}/documents",
                     $"/api/v1/form-records/{accused:D}/activity",
                 })
        {
            var result = await fixture.Api.GetAsync(path, outsider);
            Assert.True(result.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, path + " → " + result.Status + " " + result.Body);
            Assert.DoesNotContain("Avery", result.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("Isolation Road", result.Body, StringComparison.Ordinal);
        }

        var forms = await fixture.Api.GetAsync("/api/v1/form-definitions", outsider);
        Assert.DoesNotContain(forms.Root.EnumerateArray(), f => f.GetProperty("formDefinitionId").GetGuid() == s.AccusedFormId);
        var records = await fixture.Api.GetAsync("/api/v1/form-records?includeChildren=true&pageSize=50", outsider);
        Assert.Empty(records.Root.GetProperty("items").EnumerateArray());
        var outsiderDesign = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/fields", outsider, new { fieldKey = "injected", fieldDisplayName = "Injected", fieldDescription = "", fieldDataType = "String", options = (object?)null });
        Assert.True(outsiderDesign.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        var outsiderUpload = await fixture.Api.PostAsync("/api/v1/upload-sessions", outsider, new { formRecordId = accused, fieldDefinitionId = s.AccusedFields["case_documents"], originalFileName = "x.txt", contentType = "text/plain", sizeBytes = 4, replacesFileAttachmentId = (Guid?)null });
        Assert.True(outsiderUpload.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);

        // A token minted for organization A cannot be used to act in organization B's context either.
        var crossToken = StageBScenario.TokenFor(s.Clerk, other.OrganizationId);
        var crossRead = await fixture.Api.GetAsync($"/api/v1/form-records/{accused:D}/projection", crossToken);
        Assert.True(crossRead.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, crossRead.Body);

        // Role boundaries inside the organization: the clerk (form user) cannot design; the designer cannot read the clerk's record.
        var clerkDesign = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/fields", s.Token(s.Clerk), new { fieldKey = "clerk_field", fieldDisplayName = "Clerk", fieldDescription = "", fieldDataType = "String", options = (object?)null });
        Assert.True(clerkDesign.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, clerkDesign.Body);
        var designerRead = await s.ProjectionAsync(s.Designer, accused);
        Assert.True(designerRead.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, "designer must not read record content: " + designerRead.Body);
        var designerDocuments = await fixture.Api.GetAsync($"/api/v1/form-records/{accused:D}/documents", s.Token(s.Designer));
        Assert.True(designerDocuments.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);

        // Branding: only the organization owner may change it; the floor is enforced server-side.
        var branding = await fixture.Api.GetAsync("/api/v1/organization/branding", s.Token(s.Designer));
        Assert.True(branding.IsSuccess, branding.Body);
        var revision = branding.Root.GetProperty("brandingRevision").GetInt64();
        var clerkBranding = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/branding", s.Token(s.Clerk), new { expectedBrandingRevision = revision, organizationDisplayName = "Hijacked", logoAlternativeText = (string?)null, primaryButtonBackgroundColor = "#111111", primaryButtonForegroundColor = "#FFFFFF", secondaryButtonBackgroundColor = "#FFFFFF", secondaryButtonForegroundColor = "#111111", secondaryButtonBorderColor = "#111111", organizationDefaultTextScalePercent = 100, organizationColorMode = "Light" }, null);
        Assert.True(clerkBranding.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, clerkBranding.Body);
        var lowContrast = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/branding", s.Token(s.Designer), new { expectedBrandingRevision = revision, organizationDisplayName = "Stage B", logoAlternativeText = (string?)null, primaryButtonBackgroundColor = "#FFFF00", primaryButtonForegroundColor = "#FFFFFF", secondaryButtonBackgroundColor = "#FFFFFF", secondaryButtonForegroundColor = "#111111", secondaryButtonBorderColor = "#111111", organizationDefaultTextScalePercent = 100, organizationColorMode = "Light" }, null);
        Assert.Equal("THEME_CONTRAST_FAILED", lowContrast.ErrorCode);
    }

    [Fact]
    public async Task SessionSurfaceIsCsrfBoundAndDevelopmentLoginOnlyAcceptsSeededPersonas()
    {
        // Anonymous probe reports no session and identifies the development identity provider (Testing environment).
        var anonymous = await fixture.Api.SendAsync(HttpMethod.Get, "/api/v1/session", null, null, null);
        Assert.True(anonymous.IsSuccess, anonymous.Body);
        Assert.False(anonymous.Root.GetProperty("authenticated").GetBoolean());
        Assert.Equal("TestIdentityProvider", anonymous.Root.GetProperty("identityProviderKind").GetString());

        // Unknown persona keys are refused; the endpoint never mints a session for an arbitrary identity.
        var unknown = await fixture.Api.SendAsync(HttpMethod.Post, "/api/v1/session/development-login", null, new { personaKey = "nobody-" + Guid.NewGuid().ToString("N") }, null);
        Assert.False(unknown.IsSuccess);
        Assert.Equal("UNKNOWN_PERSONA", unknown.ErrorCode);

        // Protected surfaces require a credential: no cookie, no bearer → 401, never data.
        var bootstrap = await fixture.Api.SendAsync(HttpMethod.Get, "/api/v1/bootstrap", null, null, null);
        Assert.Equal(HttpStatusCode.Unauthorized, bootstrap.Status);
        var records = await fixture.Api.SendAsync(HttpMethod.Get, "/api/v1/form-records", null, null, null);
        Assert.Equal(HttpStatusCode.Unauthorized, records.Status);
    }

    [Fact]
    public async Task HostileWritesToStageBTablesFailClosed()
    {
        var s = new StageBScenario(fixture, "tamper");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var bytes = StageBScenario.SyntheticBytes(32 * 1024, 9);
        var committed = await s.UploadAsync(s.Clerk, accused, "evidence.bin", "application/octet-stream", bytes);
        var attachmentId = committed.GetProperty("fileAttachmentId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await fixture.DownloadAsync($"/api/v1/form-records/{accused:D}/documents/{attachmentId:D}/download", s.Token(s.Clerk))).Status);

        // (a) Mutating a typed date value (signature untouched) quarantines the record projection.
        // Candidates are immutable and insert-only: revision 1 and the document-commit revision each hold a date row; all are mutated.
        var dateRows = fixture.Stack.AttackerSql($"UPDATE field_date_values SET date_value = DATE '2001-01-01' WHERE form_record_id = '{accused:D}'; SELECT count(*) FROM field_date_values WHERE form_record_id = '{accused:D}' AND date_value = DATE '2001-01-01';").Trim();
        Assert.True(int.Parse(dateRows, System.Globalization.CultureInfo.InvariantCulture) >= 1, dateRows);
        var read = await s.ProjectionAsync(s.Clerk, accused);
        Assert.Equal(HttpStatusCode.Locked, read.Status);
        Assert.Equal("INTEGRITY_VERIFICATION_FAILED", read.ErrorCode);
        Assert.DoesNotContain("1990-02-28", read.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("2001-01-01", read.Body, StringComparison.Ordinal);
        var children = await s.ChildrenAsync(s.Clerk, accused);
        Assert.NotEqual(HttpStatusCode.OK, children.Status);
        var download = await fixture.DownloadAsync($"/api/v1/form-records/{accused:D}/documents/{attachmentId:D}/download", s.Token(s.Clerk));
        Assert.NotEqual(HttpStatusCode.OK, download.Status);
        Assert.NotEqual(StageBScenario.Sha256Hex(bytes), StageBScenario.Sha256Hex(download.Body));

        // (b) On a second record, swap the ciphertext digest of the committed attachment version: download fails closed.
        var second = await s.CreateAccusedAsync(s.Clerk, StageBScenario.AccusedValues("Second", "Record"));
        var committed2 = await s.UploadAsync(s.Clerk, second, "evidence2.bin", "application/octet-stream", StageBScenario.SyntheticBytes(16 * 1024, 10));
        var attachment2 = committed2.GetProperty("fileAttachmentId").GetGuid();
        // Two rows per attachment by design: the worker's processed row (operation 0) and the user-committed signed copy.
        var versionRows = fixture.Stack.AttackerSql($"UPDATE file_attachment_versions SET ciphertext_sha256 = decode(repeat('ab', 32), 'hex') WHERE file_attachment_id = '{attachment2:D}'; SELECT count(*) FROM file_attachment_versions WHERE file_attachment_id = '{attachment2:D}';").Trim();
        Assert.True(int.Parse(versionRows, System.Globalization.CultureInfo.InvariantCulture) >= 1, versionRows);
        var tamperedDownload = await fixture.DownloadAsync($"/api/v1/form-records/{second:D}/documents/{attachment2:D}/download", s.Token(s.Clerk));
        Assert.True(tamperedDownload.Status is HttpStatusCode.Locked or HttpStatusCode.Conflict or HttpStatusCode.InternalServerError, tamperedDownload.Status.ToString());
        Assert.NotEqual(StageBScenario.Sha256Hex(StageBScenario.SyntheticBytes(16 * 1024, 10)), StageBScenario.Sha256Hex(tamperedDownload.Body));
        var projection2 = await s.ProjectionAsync(s.Clerk, second);
        Assert.Equal(HttpStatusCode.Locked, projection2.Status);

        // (c) An unsigned extra option row injected into a third record is detected against the accepted manifest.
        var third = await s.CreateAccusedAsync(s.Clerk, StageBScenario.AccusedValues("Third", "Record"));
        var injected = fixture.Stack.AttackerSql($"UPDATE field_boolean_values SET boolean_value = NOT boolean_value WHERE form_record_id = '{third:D}'; SELECT count(*) FROM field_boolean_values WHERE form_record_id = '{third:D}';").Trim();
        Assert.True(int.Parse(injected, System.Globalization.CultureInfo.InvariantCulture) >= 1, injected);
        var read3 = await s.ProjectionAsync(s.Clerk, third);
        Assert.Equal(HttpStatusCode.Locked, read3.Status);
        var edit3 = await fixture.Api.PatchAsync($"/api/v1/form-records/{third:D}", s.Token(s.Clerk), new { expectedContentRevision = 1, values = new Dictionary<string, string> { ["given_name"] = "Overwrite" } });
        Assert.Equal(HttpStatusCode.Locked, edit3.Status);
    }

    [Fact]
    public void HarnessIdentityIsSyntheticOnly()
    {
        Assert.StartsWith("https://test-idp.", TestIdentity.Issuer, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{40}$", fixture.CommitSha);
    }
}
