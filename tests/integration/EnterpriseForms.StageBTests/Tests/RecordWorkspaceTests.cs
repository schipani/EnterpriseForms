using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C4 on the real stack (business API, independent authority, SpiceDB with a 20-result lookup page in the security
/// test profile): the forms catalog beyond the authority's 250-checks-per-call limit (C-27), complete discovery across
/// SpiceDB cursor pages (C-28), My Records filtering/sorting/paging over the complete authorized set with bound cursors
/// (C-29), the record summary (C-30), structured field errors (C-31) and fail-closed behaviour when the authority is down.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class RecordWorkspaceTests(StageBFixture fixture)
{
    /// <summary>List calls are gated (503) for a moment after a security change until the projection catches up.</summary>
    private async Task<HttpResult> ListAsync(StageBScenario s, TestUser user, string query, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        HttpResult last;
        do
        {
            last = await fixture.Api.GetAsync("/api/v1/form-records" + query, s.Token(user));
            if (last.Status != HttpStatusCode.ServiceUnavailable)
            {
                return last;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);
        return last;
    }

    private static List<Guid> Ids(HttpResult page) => page.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToList();

    private static string? Next(HttpResult page) => page.Root.TryGetProperty("continuationToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    private static string ErrorCode(HttpResult result) => result.Root.GetProperty("title").GetString() ?? string.Empty;

    [Fact]
    public async Task FormsCatalogIsCompleteAndExactBeyondTheAuthorityBatchLimit()
    {
        var s = new StageBScenario(fixture, "catalog-260");
        await s.BootstrapAsync();
        // 130 forms → 260 permission checks for every caller (the authority accepts at most 250 per call).
        var usable = new HashSet<Guid>();
        var all = new HashSet<Guid>();
        for (var i = 0; i < 130; i++)
        {
            var formId = i % 3 == 0 ? await s.CreateFormAsync($"Catalog {i:D3} {s.Name}", s.Clerk) : await s.CreateFormAsync($"Catalog {i:D3} {s.Name}");
            all.Add(formId);
            if (i % 3 == 0)
            {
                usable.Add(formId);
            }
        }

        var designer = await fixture.Api.GetAsync("/api/v1/form-definitions", s.Token(s.Designer));
        Assert.True(designer.IsSuccess, designer.Body);
        Assert.Equal(all, designer.Root.EnumerateArray().Select(f => f.GetProperty("formDefinitionId").GetGuid()).ToHashSet());
        Assert.All(designer.Root.EnumerateArray(), f => Assert.True(f.GetProperty("canDesign").GetBoolean()));

        // The clerk sees exactly the 44 forms it may use — none it may not, none missing, none marked designable.
        var clerk = await fixture.Api.GetAsync("/api/v1/form-definitions", s.Token(s.Clerk));
        Assert.True(clerk.IsSuccess, clerk.Body);
        Assert.Equal(usable, clerk.Root.EnumerateArray().Select(f => f.GetProperty("formDefinitionId").GetGuid()).ToHashSet());
        Assert.All(clerk.Root.EnumerateArray(), f =>
        {
            Assert.True(f.GetProperty("canCreateRecord").GetBoolean());
            Assert.False(f.GetProperty("canDesign").GetBoolean());
        });

        var reader = await fixture.Api.GetAsync("/api/v1/form-definitions", s.Token(s.Reader));
        Assert.True(reader.IsSuccess, reader.Body);
        Assert.Empty(reader.Root.EnumerateArray());
    }

    [Fact]
    public async Task MyRecordsListsTheCompleteAuthorizedSetAcrossLookupPagesWithExactFiltersSortAndPaging()
    {
        var s = new StageBScenario(fixture, "my-records");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        // 45 records: more than two SpiceDB lookup pages (20 in this profile) and more than four list pages of 10.
        var created = new List<Guid>();
        for (var i = 0; i < 45; i++)
        {
            created.Add(await s.CreateAccusedAsync(s.Clerk, StageBScenario.AccusedValues("Listed", $"Person{i:D2}")));
        }

        var children = new[] { await s.CreateAddressAsync(s.Clerk, created[0], "1 Child Street"), await s.CreateAddressAsync(s.Clerk, created[0], "2 Child Street") };

        // Oldest first across the whole set, ten at a time, with exact totals and positions.
        var seen = new List<Guid>();
        string? token = null;
        var pageStarts = new List<int>();
        do
        {
            var page = await ListAsync(s, s.Clerk, "?pageSize=10&sortBy=createdOn&sortDirection=asc" + (token is null ? string.Empty : "&continuationToken=" + Uri.EscapeDataString(token)));
            Assert.True(page.IsSuccess, page.Body);
            Assert.Equal(45, page.Root.GetProperty("totalCount").GetInt32());
            pageStarts.Add(page.Root.GetProperty("pageStart").GetInt32());
            Assert.Equal(pageStarts.Count == 1, page.Root.GetProperty("previousToken").ValueKind == JsonValueKind.Null);
            foreach (var item in page.Root.GetProperty("items").EnumerateArray())
            {
                Assert.True(item.GetProperty("canEdit").GetBoolean());
                Assert.True(item.GetProperty("createdByCurrentUser").GetBoolean());
                Assert.Equal("REC-" + item.GetProperty("formRecordId").GetGuid().ToString("N")[^8..].ToUpperInvariant(), item.GetProperty("recordReference").GetString());
                Assert.Equal(s.AccusedFormId, item.GetProperty("formDefinitionId").GetGuid());
            }

            seen.AddRange(Ids(page));
            token = Next(page);
        }
        while (token is not null && pageStarts.Count < 10);

        Assert.Equal([1, 11, 21, 31, 41], pageStarts);
        Assert.Equal(created, seen);

        var newestFirst = await ListAsync(s, s.Clerk, "?pageSize=5&sortBy=createdOn&sortDirection=desc");
        Assert.Equal(Enumerable.Reverse(created).Take(5), Ids(newestFirst));

        // Child records only when asked for; form and status filters are exact.
        var withChildren = await ListAsync(s, s.Clerk, "?pageSize=100&includeChildren=true");
        Assert.Equal(47, withChildren.Root.GetProperty("totalCount").GetInt32());
        Assert.Equal(new HashSet<Guid> { s.AccusedFormId, s.AddressFormId }, withChildren.Root.GetProperty("availableForms").EnumerateArray().Select(f => f.GetProperty("formDefinitionId").GetGuid()).ToHashSet());
        var addresses = await ListAsync(s, s.Clerk, $"?pageSize=100&includeChildren=true&formDefinitionId={s.AddressFormId:D}");
        Assert.Equal(children.ToHashSet(), Ids(addresses).ToHashSet());
        Assert.All(addresses.Root.GetProperty("items").EnumerateArray(), i => Assert.Equal(created[0], i.GetProperty("parentFormRecordId").GetGuid()));
        var submitted = await ListAsync(s, s.Clerk, "?recordStatus=Submitted");
        Assert.Equal(0, submitted.Root.GetProperty("totalCount").GetInt32());
        Assert.Empty(Ids(submitted));

        // Across permission boundaries: no grant → nothing; a designer without data grants → nothing.
        Assert.Equal(0, (await ListAsync(s, s.Reader, "?includeChildren=true")).Root.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await ListAsync(s, s.Designer, "?includeChildren=true")).Root.GetProperty("totalCount").GetInt32());

        // Cursors are bound to the caller and the listing scope.
        var firstPage = await ListAsync(s, s.Clerk, "?pageSize=10&sortBy=createdOn&sortDirection=asc");
        var cursor = Uri.EscapeDataString(Next(firstPage)!);
        var stolen = await ListAsync(s, s.Reader, "?pageSize=10&sortBy=createdOn&sortDirection=asc&continuationToken=" + cursor);
        Assert.Equal((HttpStatusCode.BadRequest, "CURSOR_INVALID"), (stolen.Status, ErrorCode(stolen)));
        var rescoped = await ListAsync(s, s.Clerk, "?pageSize=10&sortBy=createdOn&sortDirection=desc&continuationToken=" + cursor);
        Assert.Equal((HttpStatusCode.BadRequest, "CURSOR_SCOPE_MISMATCH"), (rescoped.Status, ErrorCode(rescoped)));
        var forged = await ListAsync(s, s.Clerk, "?pageSize=10&sortBy=createdOn&sortDirection=asc&continuationToken=" + cursor[..^4] + "AAAA");
        Assert.Equal(HttpStatusCode.BadRequest, forged.Status);
        var badSort = await ListAsync(s, s.Clerk, "?sortBy=given_name");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badSort.Status);

        // Sharing two records changes the authorization revision: the old cursor is refused (no shifted page), and the
        // reader now discovers exactly those two — without edit rights.
        foreach (var shared in new[] { created[3], created[30] })
        {
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{shared:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
            Assert.True(share.IsSuccess, share.Body);
        }

        var changed = await ListAsync(s, s.Clerk, "?pageSize=10&sortBy=createdOn&sortDirection=asc&continuationToken=" + cursor);
        Assert.Equal((HttpStatusCode.Conflict, "LIST_CHANGED"), (changed.Status, ErrorCode(changed)));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        var reader = await ListAsync(s, s.Reader, "?pageSize=25");
        while (reader.IsSuccess && reader.Root.GetProperty("totalCount").GetInt32() < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
            reader = await ListAsync(s, s.Reader, "?pageSize=25");
        }

        Assert.True(reader.IsSuccess, reader.Body);
        Assert.Equal(new HashSet<Guid> { created[3], created[30] }, Ids(reader).ToHashSet());
        Assert.All(reader.Root.GetProperty("items").EnumerateArray(), i =>
        {
            Assert.False(i.GetProperty("canEdit").GetBoolean());
            Assert.False(i.GetProperty("createdByCurrentUser").GetBoolean());
        });
    }

    [Fact]
    public async Task TheRecordSummaryCarriesOnlyAllowlistedMetadata()
    {
        var s = new StageBScenario(fixture, "record-summary");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var own = await s.WaitProjectionAsync(s.Clerk, accused);
        var summary = own.Root.GetProperty("summary");
        Assert.Equal("REC-" + accused.ToString("N")[^8..].ToUpperInvariant(), summary.GetProperty("recordReference").GetString());
        Assert.True(summary.GetProperty("createdByCurrentUser").GetBoolean());
        Assert.Equal(JsonValueKind.String, summary.GetProperty("createdOn").ValueKind);

        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
        Assert.True(share.IsSuccess, share.Body);
        var shared = await s.WaitProjectionAsync(s.Reader, accused, TimeSpan.FromSeconds(40));
        var sharedSummary = shared.Root.GetProperty("summary");
        Assert.False(sharedSummary.GetProperty("createdByCurrentUser").GetBoolean());
        Assert.Equal(s.Clerk.DisplayName, sharedSummary.GetProperty("createdByDisplayName").GetString());
        // The summary adds no field content: the partial projection still releases exactly the shared field.
        Assert.Equal(["given_name"], shared.Root.GetProperty("record").GetProperty("values").EnumerateArray().Select(v => v.GetProperty("fieldKey").GetString()).ToArray());
        var serialized = sharedSummary.GetRawText();
        Assert.DoesNotContain("Synthetic", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("1990-02-28", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationFailuresNameTheFieldsWithoutDisclosingValues()
    {
        var s = new StageBScenario(fixture, "field-errors");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var values = StageBScenario.AccusedValues();
        values["given_name"] = string.Empty;
        values["date_of_birth"] = "1990-02-28";
        var missing = await fixture.Api.PatchAsync($"/api/v1/form-records/{accused:D}", s.Token(s.Clerk), new { expectedContentRevision = 1, values });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.Status);
        Assert.Equal(["given_name"], missing.Root.GetProperty("field_keys").EnumerateArray().Select(k => k.GetString()).ToArray());

        values = StageBScenario.AccusedValues();
        values["date_of_birth"] = "28/02/1990";
        var malformed = await fixture.Api.PatchAsync($"/api/v1/form-records/{accused:D}", s.Token(s.Clerk), new { expectedContentRevision = 1, values });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.Status);
        Assert.Equal(["date_of_birth"], malformed.Root.GetProperty("field_keys").EnumerateArray().Select(k => k.GetString()).ToArray());
        Assert.DoesNotContain("28/02/1990", malformed.Body, StringComparison.Ordinal);

        // Nothing was written by either refused save.
        var after = await s.WaitProjectionAsync(s.Clerk, accused);
        Assert.Equal(1, after.Root.GetProperty("record").GetProperty("contentRevision").GetInt64());
    }

    [Fact]
    public async Task ListsFailClosedWhileTheAuthorityIsUnavailable()
    {
        var s = new StageBScenario(fixture, "authority-down");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        await s.CreateAccusedAsync(s.Clerk);
        fixture.Stack.Pause("authority");
        try
        {
            var records = await fixture.Api.GetAsync("/api/v1/form-records", s.Token(s.Clerk));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, records.Status);
            Assert.False(records.Root.TryGetProperty("items", out _), "no partial list while the authority is unavailable");
            var forms = await fixture.Api.GetAsync("/api/v1/form-definitions", s.Token(s.Clerk));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, forms.Status);
        }
        finally
        {
            fixture.Stack.Unpause("authority");
        }

        await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(60));
        var recovered = await ListAsync(s, s.Clerk, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(recovered.IsSuccess, recovered.Body);
        Assert.Equal(1, recovered.Root.GetProperty("totalCount").GetInt32());
    }
}
