using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C6 quick record lookup on the real stack: an exact reference or record id finds a record the caller may discover
/// (owned, shared, or archived); a member without access gets exactly the same empty answer as for a record that does not
/// exist; input that is not an exact reference or id is refused. Matches are decided by the Authorization Authority.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class RecordLookupTests(StageBFixture fixture)
{
    private static string Reference(Guid id) => "REC-" + id.ToString("N")[^8..].ToUpperInvariant();

    private Task<HttpResult> LookupAsync(StageBScenario s, TestUser user, string input) =>
        fixture.Api.GetAsync("/api/v1/form-records/lookup?q=" + Uri.EscapeDataString(input), s.Token(user));

    private static List<Guid> MatchIds(HttpResult result) => result.Root.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("formRecordId").GetGuid()).ToList();

    /// <summary>The response with the echoed query removed: what an unauthorized caller learns must equal "does not exist".</summary>
    private static string Shape(HttpResult result) =>
        JsonSerializer.Serialize(new { matches = result.Root.GetProperty("matches").GetArrayLength(), count = result.Root.GetProperty("matchCount").GetInt32(), unavailable = result.Root.GetProperty("unavailableRecordIds").GetArrayLength() });

    [Fact]
    public async Task ExactLookupFindsOwnedSharedAndArchivedRecordsAndRevealsNothingWithoutAccess()
    {
        var s = new StageBScenario(fixture, "c6-lookup");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);
        await s.WaitProjectionAsync(s.Clerk, record);

        // Owner: by short reference (any case) and by full id (with and without hyphens).
        foreach (var input in new[] { Reference(record), Reference(record).ToLowerInvariant(), record.ToString("D"), record.ToString("N") })
        {
            var found = await LookupAsync(s, s.Clerk, input);
            Assert.True(found.IsSuccess, found.Body);
            Assert.Equal([record], MatchIds(found));
            Assert.Equal(Reference(record), found.Root.GetProperty("matches")[0].GetProperty("recordReference").GetString());
        }

        // A member with no grant: identical answer to a record that does not exist, for both kinds of lookup.
        var hiddenByReference = await LookupAsync(s, s.Reader, Reference(record));
        var hiddenById = await LookupAsync(s, s.Reader, record.ToString("D"));
        var missingReference = await LookupAsync(s, s.Reader, "REC-00000000");
        var missingId = await LookupAsync(s, s.Reader, Guid.CreateVersion7().ToString("D"));
        foreach (var result in new[] { hiddenByReference, hiddenById, missingReference, missingId })
        {
            Assert.Equal(HttpStatusCode.OK, result.Status);
        }

        Assert.Equal(Shape(missingReference), Shape(hiddenByReference));
        Assert.Equal(Shape(missingId), Shape(hiddenById));
        Assert.Equal(0, hiddenByReference.Root.GetProperty("matchCount").GetInt32());

        // After a share the same member finds it.
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
        Assert.True(share.IsSuccess, share.Body);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        Assert.Equal([record], MatchIds(await LookupAsync(s, s.Reader, Reference(record))));

        // Archived: hidden from the default list, still found by exact lookup.
        var view = (await s.WaitProjectionAsync(s.Clerk, record)).Root;
        var archived = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/archive", s.Token(s.Clerk), new { expectedContentRevision = view.GetProperty("record").GetProperty("contentRevision").GetInt64(), expectedStatus = "Draft", reason = (string?)null });
        Assert.True(archived.IsSuccess, archived.Body);
        var list = await fixture.Api.GetAsync("/api/v1/form-records?pageSize=100", s.Token(s.Clerk));
        Assert.DoesNotContain(list.Root.GetProperty("items").EnumerateArray(), i => i.GetProperty("formRecordId").GetGuid() == record);
        var afterArchive = await LookupAsync(s, s.Clerk, Reference(record));
        Assert.Equal([record], MatchIds(afterArchive));
        Assert.Equal("Archived", afterArchive.Root.GetProperty("matches")[0].GetProperty("recordStatus").GetString());
    }

    [Theory]
    [InlineData("Smith")]
    [InlineData("REC-12345")]
    [InlineData("given_name=Alex")]
    public async Task InputThatIsNotAnExactReferenceOrIdIsRefused(string input)
    {
        var s = new StageBScenario(fixture, "c6-lookup-invalid");
        await s.BootstrapAsync();
        var refused = await LookupAsync(s, s.Clerk, input);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Equal("LOOKUP_INVALID", refused.ErrorCode);
    }
}
