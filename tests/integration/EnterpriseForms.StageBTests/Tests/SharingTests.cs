using System.Net;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// ADR-PB-002 Stage B item 5: authorized share, effective-access verification, revocation, one child-specific
/// visibility case, and denial of unshared child records and documents to another principal. Shares are listed from
/// the local projection only for access managers; every access decision below is the authority's.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SharingTests(StageBFixture fixture)
{
    [Fact]
    public async Task ShareListRevokeAndChildDocumentDenialFollowTheAuthority()
    {
        var s = new StageBScenario(fixture, "sharing");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var home = await s.CreateAddressAsync(s.Clerk, accused, "9 Shared Crescent");
        var document = await s.UploadAsync(s.Clerk, accused, "shared-note.txt", "text/plain", StageBScenario.SyntheticBytes(2048, 7));
        var attachmentId = document.GetProperty("fileAttachmentId").GetGuid();

        // Before any share: the reader cannot list shares (concealment-safe 404), read, or download.
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.GetAsync($"/api/v1/form-records/{accused:D}/shares", s.Token(s.Reader))).Status);
        Assert.True((await s.ProjectionAsync(s.Reader, accused)).Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);

        // Owner lists an empty share set, then shares two identity fields with the reader.
        var empty = await fixture.Api.GetAsync($"/api/v1/form-records/{accused:D}/shares", s.Token(s.Clerk));
        Assert.True(empty.IsSuccess, empty.Body);
        Assert.Empty(empty.Root.GetProperty("items").EnumerateArray());
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name", "family_name" } });
        Assert.True(share.IsSuccess, share.Body);
        var shareId = share.Root.GetProperty("recordShareId").GetGuid();

        var listed = await fixture.Api.GetAsync($"/api/v1/form-records/{accused:D}/shares", s.Token(s.Clerk));
        Assert.True(listed.IsSuccess, listed.Body);
        var row = Assert.Single(listed.Root.GetProperty("items").EnumerateArray());
        Assert.Equal(shareId, row.GetProperty("recordShareId").GetGuid());
        Assert.Equal(s.Reader.AuthorityUserId, row.GetProperty("recipientUserId").GetGuid());
        Assert.Equal("Reader", row.GetProperty("recipientRole").GetString());
        Assert.Equal("Active", row.GetProperty("shareStatus").GetString());
        Assert.Equal(["family_name", "given_name"], row.GetProperty("fieldKeys").EnumerateArray().Select(k => k.GetString()).ToArray());

        // Effective access: the reader now discovers the parent and sees exactly the shared fields.
        var forReader = await s.WaitProjectionAsync(s.Reader, accused, TimeSpan.FromSeconds(40));
        var permissions = forReader.Root.GetProperty("permissions");
        Assert.True(permissions.GetProperty("canRead").GetBoolean());
        Assert.False(permissions.GetProperty("canEdit").GetBoolean());
        Assert.False(permissions.GetProperty("canManageAccess").GetBoolean());
        Assert.False(permissions.GetProperty("isCompleteProjection").GetBoolean());
        var visibleKeys = forReader.Root.GetProperty("record").GetProperty("values").EnumerateArray().Select(v => v.GetProperty("fieldKey").GetString()).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(["family_name", "given_name"], visibleKeys);
        Assert.Empty(forReader.Root.GetProperty("documents").EnumerateArray());
        // Authorized projection (§4H): field metadata, layout and relationship metadata are reduced server-side.
        Assert.Equal(["family_name", "given_name"], forReader.Root.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("fieldKey").GetString()).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Empty(forReader.Root.GetProperty("relationships").EnumerateArray());
        var partialLayout = forReader.Root.GetProperty("layoutVersion").GetProperty("layoutJson").GetString()!;
        Assert.DoesNotContain("relatedFormList", partialLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("documentCollection", partialLayout, StringComparison.Ordinal);
        Assert.DoesNotContain(s.AccusedFields["date_of_birth"].ToString("D"), partialLayout, StringComparison.Ordinal);
        Assert.Contains(s.AccusedFields["given_name"].ToString("D"), partialLayout, StringComparison.Ordinal);
        var discovered = await fixture.Api.GetAsync("/api/v1/form-records?includeChildren=true&pageSize=50", s.Token(s.Reader));
        Assert.True(discovered.IsSuccess, discovered.Body);
        var discoveredIds = discovered.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToList();
        Assert.Contains(accused, discoveredIds);
        Assert.DoesNotContain(home, discoveredIds); // child-specific visibility: a parent share grants nothing on the child

        // The recipient cannot manage access, read the child, list children, or download the (unshared) document.
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.GetAsync($"/api/v1/form-records/{accused:D}/shares", s.Token(s.Reader))).Status);
        Assert.True((await s.ProjectionAsync(s.Reader, home)).Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        // The child list re-authorizes every child through the authority and omits unauthorized ones without a count
        // (B-04): the shared reader may open the list but sees no address; fail-closed is equally acceptable.
        var childrenForReader = await s.ChildrenAsync(s.Reader, accused);
        if (childrenForReader.IsSuccess)
        {
            Assert.Empty(childrenForReader.Root.GetProperty("items").EnumerateArray());
        }
        else
        {
            Assert.True(childrenForReader.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, childrenForReader.Body);
        }

        var download = await fixture.DownloadAsync($"/api/v1/form-records/{accused:D}/documents/{attachmentId:D}/download", s.Token(s.Reader));
        Assert.True(download.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, download.Status.ToString());
        var readerShare = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/shares", s.Token(s.Reader), new { recipientUserId = s.Designer.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
        Assert.True(readerShare.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity, readerShare.Body);

        // Revocation: the owner revokes; the reader loses discovery and reading without any projection catch-up window.
        var revoke = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/shares/{shareId:D}/revoke", s.Token(s.Clerk), new { });
        Assert.True(revoke.IsSuccess, revoke.Body);
        var afterRevoke = await fixture.Api.GetAsync($"/api/v1/form-records/{accused:D}/shares", s.Token(s.Clerk));
        Assert.Equal("Revoked", Assert.Single(afterRevoke.Root.GetProperty("items").EnumerateArray()).GetProperty("shareStatus").GetString());
        var denied = await s.ProjectionAsync(s.Reader, accused);
        Assert.True(denied.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, denied.Body);
        var gone = await fixture.Api.GetAsync("/api/v1/form-records?includeChildren=true&pageSize=50", s.Token(s.Reader));
        Assert.Empty(gone.Root.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task RecordListPagesWithOpaqueContinuationTokens()
    {
        var s = new StageBScenario(fixture, "paging");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var first = await s.CreateAccusedAsync(s.Clerk, StageBScenario.AccusedValues("Page", "One"));
        var second = await s.CreateAccusedAsync(s.Clerk, StageBScenario.AccusedValues("Page", "Two"));
        var third = await s.CreateAccusedAsync(s.Clerk, StageBScenario.AccusedValues("Page", "Three"));

        var seen = new List<Guid>();
        string? token = null;
        var pages = 0;
        do
        {
            var page = await fixture.Api.GetAsync("/api/v1/form-records?pageSize=1" + (token is null ? string.Empty : "&continuationToken=" + Uri.EscapeDataString(token)), s.Token(s.Clerk));
            Assert.True(page.IsSuccess, page.Body);
            seen.AddRange(page.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()));
            token = page.Root.TryGetProperty("continuationToken", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
            pages++;
        }
        while (token is not null && pages < 10);

        Assert.True(pages >= 3, $"expected at least three pages, got {pages}");
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(new HashSet<Guid> { first, second, third }, seen.ToHashSet());

        // A forged token is rejected rather than widened.
        var forged = await fixture.Api.GetAsync("/api/v1/form-records?pageSize=1&continuationToken=not-a-real-token", s.Token(s.Clerk));
        Assert.True(forged.Status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound or HttpStatusCode.Forbidden, forged.Body);
    }
}
