using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C7 M1 — AC-5 proof tests (AC5-T01..T15 where M1 applies) on the real stack: Authority + AuthorityDb, SpiceDB,
/// Vault Transit, BusinessDb with the attacker-sql business administrator. The business half runs through the Testing-only
/// scope-verification route (catalogue → staged rows → batch verification → snapshot validation; no predicate, no field
/// values). Field-value matching itself is the M3 executor and is NOT tested here (pending, see PHASE_C7_STATUS.md).
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SearchCatalogueTests(StageBFixture fixture)
{
    private const string Verify = "/api/v1/testing/search-scope-verification";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private async Task<(StageBScenario S, List<Guid> Records)> WorkspaceAsync(string name, int records)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < records; i++)
        {
            ids.Add(await s.CreateAccusedAsync(s.Clerk));
        }

        foreach (var id in ids)
        {
            Assert.True((await s.WaitProjectionAsync(s.Clerk, id)).IsSuccess);
        }

        return (s, ids);
    }

    private Task<HttpResult> VerifyAsync(StageBScenario s, TestUser user) => fixture.Api.PostAsync(Verify, s.Token(user), new { formDefinitionId = s.AccusedFormId });

    private Task<HttpResult> CatalogueAsync(StageBScenario s, TestUser user, Guid? form = null, int maximum = 500) =>
        fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search-catalogue", TestIdentity.WorkloadToken(), new { organizationId = s.OrganizationId, formDefinitionId = form ?? s.AccusedFormId, maximumEntries = maximum }, s.Token(user));

    private Task<HttpResult> ValidateAsync(StageBScenario s, TestUser user, Guid snapshot) =>
        fixture.Authority.SendAsync(HttpMethod.Post, $"/internal/v1/search-catalogue/{snapshot:D}/validate", TestIdentity.WorkloadToken(), null, s.Token(user));

    private static List<Guid> Ids(HttpResult result, string property) => result.Root.GetProperty(property).EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetGuid() : e.GetProperty("form_record_id").GetGuid()).ToList();

    [Fact]
    public async Task TheVerifiedScopeIsExactlyTheCallersDiscoverableRecordsOfTheFormAndEveryOneVerifies()
    {
        var (s, records) = await WorkspaceAsync("c7-scope", 3);
        var clerk = await VerifyAsync(s, s.Clerk);
        Assert.True(clerk.IsSuccess, clerk.Body);
        Assert.Equal(3, clerk.Root.GetProperty("catalogueEntryCount").GetInt32());
        Assert.Equal(3, clerk.Root.GetProperty("verifiedRecordCount").GetInt32());
        Assert.Equal(3, clerk.Root.GetProperty("completeProjectionCount").GetInt32());
        Assert.Equal(records.Order(), Ids(clerk, "verifiedRecordIds").Order());

        // A member with no grant has an empty, complete scope (not an error, not someone else's records).
        var reader = await VerifyAsync(s, s.Reader);
        Assert.True(reader.IsSuccess, reader.Body);
        Assert.Equal(0, reader.Root.GetProperty("catalogueEntryCount").GetInt32());

        // A field-level share brings exactly that record into the reader's scope, as a partial projection.
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{records[0]:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
        Assert.True(share.IsSuccess, share.Body);
        await s.WaitGateReadyAsync();
        var shared = await VerifyAsync(s, s.Reader);
        Assert.True(shared.IsSuccess, shared.Body);
        Assert.Equal([records[0]], Ids(shared, "verifiedRecordIds"));
        Assert.Equal(0, shared.Root.GetProperty("completeProjectionCount").GetInt32());

        // Another form's scope is separate (the Address child form holds none of these records).
        var other = await CatalogueAsync(s, s.Clerk, form: s.AddressFormId);
        Assert.True(other.IsSuccess, other.Body);
        Assert.Equal(0, other.Root.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task DeletedValueRowsFailTheWholeScopeAndAreNeverReportedAsFewerRecords()
    {
        var (s, records) = await WorkspaceAsync("c7-delete", 3);
        // Text fields are Confidential (encrypted) by default (ADR-PB-004); the date of birth is a plaintext typed row.
        Assert.NotEqual("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_date_values WHERE form_record_id = '{records[1]:D}';").Trim());
        var deleted = fixture.Stack.AttackerSql($"DELETE FROM field_date_values WHERE form_record_id = '{records[1]:D}'; SELECT count(*) FROM field_date_values WHERE form_record_id = '{records[1]:D}';").Trim();
        Assert.EndsWith("0", deleted, StringComparison.Ordinal);
        var result = await VerifyAsync(s, s.Clerk);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
        Assert.Equal("SEARCH_VERIFICATION_UNAVAILABLE", result.ErrorCode);
        Assert.Equal("RECORD_VERIFICATION_FAILED", result.Root.GetProperty("unavailable_reason").GetString());
        Assert.False(result.Root.TryGetProperty("verifiedRecordIds", out _));
        // The tampered record is now quarantined by the Authority; the scope stays failed (not silently smaller).
        var again = await VerifyAsync(s, s.Clerk);
        Assert.Equal("RECORD_QUARANTINED", again.Root.GetProperty("unavailable_reason").GetString());
    }

    [Fact]
    public async Task AlteredAndRolledBackValuesFailTheScope()
    {
        var (s, records) = await WorkspaceAsync("c7-alter", 2);
        var altered = fixture.Stack.AttackerSql($"UPDATE field_date_values SET date_value = DATE '2001-01-01' WHERE form_record_id = '{records[0]:D}'; SELECT count(*) FROM field_date_values WHERE form_record_id = '{records[0]:D}' AND date_value = DATE '2001-01-01';").Trim();
        Assert.NotEqual("0", altered);
        Assert.Equal("RECORD_VERIFICATION_FAILED", (await VerifyAsync(s, s.Clerk)).Root.GetProperty("unavailable_reason").GetString());

        var (r, rolled) = await WorkspaceAsync("c7-rollback", 1);
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{rolled[0]:D}", r.Token(r.Clerk), new { expectedContentRevision = 1, values = new Dictionary<string, string> { ["date_of_birth"] = "1991-03-01" } });
        Assert.True(edit.IsSuccess, edit.Body);
        Assert.True((await r.WaitProjectionAsync(r.Clerk, rolled[0])).IsSuccess);
        Assert.True((await VerifyAsync(r, r.Clerk)).IsSuccess);
        // The business DBA restores the first revision's value into the current rows (a rollback of the content).
        var restored = fixture.Stack.AttackerSql($"UPDATE field_date_values v SET date_value = o.date_value FROM field_date_values o WHERE v.form_record_id = '{rolled[0]:D}' AND o.form_record_id = v.form_record_id AND o.field_definition_id = v.field_definition_id AND o.content_change_operation_id <> v.content_change_operation_id AND o.date_value <> v.date_value; SELECT count(*) FROM field_date_values WHERE form_record_id = '{rolled[0]:D}' AND date_value = DATE '1990-02-28';").Trim();
        Assert.NotEqual("0", restored);
        var result = await VerifyAsync(r, r.Clerk);
        Assert.Equal("SEARCH_VERIFICATION_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public async Task BusinessMetadataManipulationAndInjectedRowsCannotChangeTheScope()
    {
        var (s, records) = await WorkspaceAsync("c7-metadata", 2);
        // Moving a record to another form and hiding its status in business columns does not remove it from the scope.
        fixture.Stack.AttackerSql($"UPDATE form_records SET form_definition_id = '{s.AddressFormId:D}', record_status = 'Archived' WHERE form_record_id = '{records[0]:D}';");
        var moved = await VerifyAsync(s, s.Clerk);
        Assert.True(moved.IsSuccess, moved.Body);
        Assert.Contains(records[0], Ids(moved, "verifiedRecordIds"));

        // An injected business record (a copy of a real one under a new id) is not in the Authority registry: never in scope.
        var injectedId = Guid.CreateVersion7();
        fixture.Stack.AttackerSql($"INSERT INTO form_records SELECT (jsonb_populate_record(NULL::form_records, to_jsonb(r) || jsonb_build_object('form_record_id', '{injectedId:D}'))).* FROM form_records r WHERE r.form_record_id = '{records[1]:D}';", throwOnError: false);
        var injected = await VerifyAsync(s, s.Clerk);
        Assert.True(injected.IsSuccess, injected.Body);
        Assert.DoesNotContain(injectedId, Ids(injected, "verifiedRecordIds"));
        Assert.Equal(2, injected.Root.GetProperty("catalogueEntryCount").GetInt32());

        // An injected extra value row inside a real record (a false match candidate) breaks that record's leaf set.
        var extra = fixture.Stack.AttackerSql($"INSERT INTO field_date_values SELECT (jsonb_populate_record(NULL::field_date_values, to_jsonb(v) || jsonb_build_object('field_date_value_id', '{Guid.CreateVersion7():D}', 'field_definition_id', '{s.AccusedFields["given_name"]:D}'))).* FROM field_date_values v WHERE v.form_record_id = '{records[1]:D}' LIMIT 1; SELECT count(*) FROM field_date_values WHERE form_record_id = '{records[1]:D}' AND field_definition_id = '{s.AccusedFields["given_name"]:D}';").Trim();
        Assert.EndsWith("1", extra, StringComparison.Ordinal);
        var tampered = await VerifyAsync(s, s.Clerk);
        Assert.Equal("SEARCH_VERIFICATION_UNAVAILABLE", tampered.ErrorCode);
    }

    [Fact]
    public async Task SnapshotsDieOnRevocationNewRecordsAndInScopeEditsButSurviveEditsElsewhere()
    {
        var (s, records) = await WorkspaceAsync("c7-snapshot", 2);
        var first = await CatalogueAsync(s, s.Clerk);
        Assert.True(first.IsSuccess, first.Body);
        var snapshot = first.Root.GetProperty("search_catalogue_snapshot_id").GetGuid();
        Assert.True((await ValidateAsync(s, s.Clerk, snapshot)).Root.GetProperty("valid").GetBoolean());

        // An edit of a record of ANOTHER form (an Address child) moves the content revision but not this snapshot's entries.
        var address = await s.CreateAddressAsync(s.Clerk, records[0], "1 Main St");
        Assert.True((await s.WaitProjectionAsync(s.Clerk, address)).IsSuccess);
        var afterOther = await CatalogueAsync(s, s.Clerk);
        var snapshot2 = afterOther.Root.GetProperty("search_catalogue_snapshot_id").GetGuid();
        var addressEdit = await fixture.Api.PatchAsync($"/api/v1/form-records/{address:D}", s.Token(s.Clerk), new { expectedContentRevision = 1, values = new Dictionary<string, string> { ["street_address"] = "2 Main St" } });
        Assert.True(addressEdit.IsSuccess, addressEdit.Body);
        Assert.True((await s.WaitProjectionAsync(s.Clerk, address)).IsSuccess);
        Assert.True((await ValidateAsync(s, s.Clerk, snapshot2)).Root.GetProperty("valid").GetBoolean());

        // An edit of an in-scope record invalidates.
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{records[1]:D}", s.Token(s.Clerk), new { expectedContentRevision = 1, values = new Dictionary<string, string> { ["given_name"] = "Changed" } });
        Assert.True(edit.IsSuccess, edit.Body);
        Assert.True((await s.WaitProjectionAsync(s.Clerk, records[1])).IsSuccess);
        Assert.Equal("CONTENT_HEAD_CHANGED", (await ValidateAsync(s, s.Clerk, snapshot2)).Root.GetProperty("invalidation_reason").GetString());

        // A newly registered record (a security command) invalidates every earlier snapshot of the organization.
        var third = await CatalogueAsync(s, s.Clerk);
        var snapshot3 = third.Root.GetProperty("search_catalogue_snapshot_id").GetGuid();
        var added = await s.CreateAccusedAsync(s.Clerk);
        Assert.True((await s.WaitProjectionAsync(s.Clerk, added)).IsSuccess);
        Assert.Equal("AUTHORIZATION_REVISION_CHANGED", (await ValidateAsync(s, s.Clerk, snapshot3)).Root.GetProperty("invalidation_reason").GetString());

        // A grant invalidates the recipient's earlier snapshot (taken while the record was not discoverable to them) …
        var readerBefore = await CatalogueAsync(s, s.Reader);
        Assert.Empty(Ids(readerBefore, "entries"));
        var readerBeforeSnapshot = readerBefore.Root.GetProperty("search_catalogue_snapshot_id").GetGuid();

        // … and a revocation invalidates the recipient's snapshot; the record leaves the next catalogue.
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{records[0]:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
        Assert.True(share.IsSuccess, share.Body);
        await s.WaitGateReadyAsync();
        Assert.Equal("AUTHORIZATION_REVISION_CHANGED", (await ValidateAsync(s, s.Reader, readerBeforeSnapshot)).Root.GetProperty("invalidation_reason").GetString());
        var readerCatalogue = await CatalogueAsync(s, s.Reader);
        Assert.Equal([records[0]], Ids(readerCatalogue, "entries"));
        var readerSnapshot = readerCatalogue.Root.GetProperty("search_catalogue_snapshot_id").GetGuid();
        var revoke = await fixture.Api.PostAsync($"/api/v1/form-records/{records[0]:D}/shares/{share.Root.GetProperty("recordShareId").GetGuid():D}/revoke", s.Token(s.Clerk), new { });
        Assert.True(revoke.IsSuccess, revoke.Body);
        await s.WaitGateReadyAsync();
        Assert.Equal("AUTHORIZATION_REVISION_CHANGED", (await ValidateAsync(s, s.Reader, readerSnapshot)).Root.GetProperty("invalidation_reason").GetString());
        Assert.Empty(Ids(await CatalogueAsync(s, s.Reader), "entries"));
    }

    [Fact]
    public async Task LimitsRefuseWithoutTruncationAndSnapshotsAreBoundToTheirOwnerAndOrganization()
    {
        var (s, _) = await WorkspaceAsync("c7-limits", 3);
        var refused = await CatalogueAsync(s, s.Clerk, maximum: 2);
        Assert.Equal((HttpStatusCode)422, refused.Status);
        Assert.Equal("LIMIT_EXCEEDED", refused.ErrorCode);
        Assert.DoesNotContain("form_record_id", refused.Body, StringComparison.Ordinal);
        Assert.Equal(3, (await CatalogueAsync(s, s.Clerk, maximum: 3)).Root.GetProperty("entries").GetArrayLength());
        Assert.Equal((HttpStatusCode)422, (await CatalogueAsync(s, s.Clerk, maximum: 501)).Status);

        var snapshot = (await CatalogueAsync(s, s.Clerk)).Root.GetProperty("search_catalogue_snapshot_id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await ValidateAsync(s, s.Reader, snapshot)).Status); // another user
        Assert.Equal(HttpStatusCode.NotFound, (await ValidateAsync(s, s.Clerk, Guid.CreateVersion7())).Status); // unknown id

        var other = new StageBScenario(fixture, "c7-limits-other");
        await other.BootstrapAsync();
        Assert.NotEqual(HttpStatusCode.OK, (await ValidateAsync(other, other.Clerk, snapshot)).Status); // another organization
        // A user of one organization cannot enumerate another organization's form.
        Assert.NotEqual(HttpStatusCode.OK, (await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search-catalogue", TestIdentity.WorkloadToken(), new { organizationId = s.OrganizationId, formDefinitionId = s.AccusedFormId, maximumEntries = 500 }, other.Token(other.Clerk))).Status);
    }

    [Fact]
    public async Task OnlyTheVerifierWithAnEffectiveCommandOfTheFormGetsTheCompletePopulation()
    {
        var (s, records) = await WorkspaceAsync("c7-migration", 2);
        var body = new { organizationId = s.OrganizationId, formDefinitionId = s.AccusedFormId, securityCommandId = Guid.CreateVersion7() };
        const string route = "/internal/v1/verifier/migration-catalogue";
        // The business workload credential (with or without a user) never reaches the privileged catalogue.
        Assert.Contains((await fixture.Authority.SendAsync(HttpMethod.Post, route, TestIdentity.WorkloadToken(), body)).Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Contains((await fixture.Authority.SendAsync(HttpMethod.Post, route, TestIdentity.WorkloadToken(), body, s.Token(s.Clerk))).Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Contains((await fixture.Authority.SendAsync(HttpMethod.Post, route, TestIdentity.OperatorToken(), body)).Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        // The verifier without a scoping command is refused (and the denial is journaled on the audit stream).
        var denied = await fixture.Authority.SendAsync(HttpMethod.Post, route, TestIdentity.VerifierToken(), body);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);

        // A real strengthening command of this form scopes it: the complete population, including records the reader cannot discover.
        var field = s.AccusedFields["date_of_birth"]; // plaintext (Standard) date; text fields are already encrypted
        var strengthen = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/fields/{field:D}/protection", s.Token(s.Designer), new { targetClassification = "Confidential", targetProtectionMode = "ApplicationEncrypted", changeReason = "C7 migration catalogue proof", expectedProtectionRevision = 1 });
        Assert.True(strengthen.IsSuccess, strengthen.Body);
        await s.WaitGateReadyAsync();
        var command = Guid.Parse(fixture.Stack.AuthorityObserverSql($"SELECT security_command_id FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND command_kind_name = 'PublishFormSecurityRevision' AND command_state_name = 'Effective' ORDER BY command_created_on DESC LIMIT 1;").Trim());
        var issued = await fixture.Authority.SendAsync(HttpMethod.Post, route, TestIdentity.VerifierToken(), new { organizationId = s.OrganizationId, formDefinitionId = s.AccusedFormId, securityCommandId = command });
        Assert.True(issued.IsSuccess, issued.Body);
        Assert.Equal(records.Order(), Ids(issued, "entries").Order());
        Assert.Empty(Ids(await CatalogueAsync(s, s.Reader), "entries")); // the reader can discover none of them
        // Bound to the issuing verifier workload.
        var snapshot = issued.Root.GetProperty("search_catalogue_snapshot_id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Authority.SendAsync(HttpMethod.Get, $"{route}/{snapshot:D}/entries", TestIdentity.VerifierToken("another-verifier"), null)).Status);
        var audit = fixture.Stack.AuthorityObserverSql($"SELECT journal_event_kind FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' ORDER BY journal_stream_revision;");
        Assert.Contains("MigrationCatalogueDenied", audit, StringComparison.Ordinal);
        Assert.Contains("MigrationCatalogueIssued", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchVerificationIsBoundedAndAuthorizesEveryRecord()
    {
        var (s, records) = await WorkspaceAsync("c7-batch", 1);
        var head = AuthorityOracle.Head(fixture.Stack, records[0]);
        var tooMany = Enumerable.Range(0, 51).Select(_ => new { formRecordId = Guid.CreateVersion7(), expectedCommittedOperationId = Guid.CreateVersion7(), observedItems = Array.Empty<object>() }).ToArray();
        Assert.Equal((HttpStatusCode)422, (await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/integrity/verify-record-projections", TestIdentity.WorkloadToken(), new { organizationId = s.OrganizationId, records = tooMany }, s.Token(s.Clerk))).Status);
        // A record the caller cannot discover is a concealment-safe typed result, and nothing about it is returned.
        var hidden = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/integrity/verify-record-projections", TestIdentity.WorkloadToken(), new { organizationId = s.OrganizationId, records = new[] { new { formRecordId = records[0], expectedCommittedOperationId = head.OperationId!.Value, observedItems = Array.Empty<object>() } } }, s.Token(s.Reader));
        Assert.True(hidden.IsSuccess, hidden.Body);
        var result = hidden.Root.GetProperty("results")[0];
        Assert.Equal("RECORD_UNAVAILABLE", result.GetProperty("failure_reason_code").GetString());
        Assert.Equal(0, result.GetProperty("readable_items").GetArrayLength());
        // A wrong expected head is reported as changed, not verified and not quarantined.
        var stale = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/integrity/verify-record-projections", TestIdentity.WorkloadToken(), new { organizationId = s.OrganizationId, records = new[] { new { formRecordId = records[0], expectedCommittedOperationId = Guid.CreateVersion7(), observedItems = Array.Empty<object>() } } }, s.Token(s.Clerk));
        Assert.Equal("HEAD_CHANGED", stale.Root.GetProperty("results")[0].GetProperty("failure_reason_code").GetString());
        Assert.True((await VerifyAsync(s, s.Clerk)).IsSuccess); // still intact
    }

    [Fact]
    public async Task CrossOrganizationBusinessTamperingCannotMoveARecordIntoAnotherCatalogue()
    {
        var (s, records) = await WorkspaceAsync("c7-cross-org-a", 2);
        var (other, otherRecords) = await WorkspaceAsync("c7-cross-org-b", 1);

        // The hostile business DBA re-homes a record of organization A into organization B's tenant and form.
        fixture.Stack.AttackerSql($"UPDATE form_records SET tenant_id = '{other.OrganizationId:D}', form_definition_id = '{other.AccusedFormId:D}' WHERE form_record_id = '{records[0]:D}';");

        var otherScope = await VerifyAsync(other, other.Clerk);
        Assert.True(otherScope.IsSuccess, otherScope.Body);
        Assert.Equal(otherRecords, Ids(otherScope, "verifiedRecordIds"));
        var ownScope = await VerifyAsync(s, s.Clerk);
        Assert.True(ownScope.IsSuccess, ownScope.Body);
        Assert.Equal(records.Order(), Ids(ownScope, "verifiedRecordIds").Order());

        // A user of organization B naming A's record or A's form gets nothing from the Authority.
        var foreignCatalogue = await CatalogueAsync(other, other.Clerk, form: s.AccusedFormId);
        Assert.True(!foreignCatalogue.IsSuccess || Ids(foreignCatalogue, "entries").Count == 0, foreignCatalogue.Body);
    }

    [Fact]
    public async Task EveryIssuedOrRefusedCatalogueHasDurableAuditEvidenceThatReachesTheArchive()
    {
        var (s, _) = await WorkspaceAsync("c7-audit", 2);
        var issued = await CatalogueAsync(s, s.Clerk);
        Assert.True(issued.IsSuccess, issued.Body);
        var snapshot = issued.Root.GetProperty("search_catalogue_snapshot_id").GetGuid();
        var refused = await CatalogueAsync(s, s.Clerk, maximum: 1);
        Assert.Equal("LIMIT_EXCEEDED", refused.ErrorCode);
        Assert.True((await VerifyAsync(s, s.Clerk)).IsSuccess);

        // No snapshot of this organization exists without its issuance event (same AuthorityDb transaction), and the
        // refusal was journaled before it was returned.
        var orphans = fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM search_catalogue_snapshots c WHERE c.organization_id = '{s.OrganizationId:D}' AND NOT EXISTS (SELECT 1 FROM security_journal_entries j WHERE j.organization_id = c.organization_id AND j.journal_stream_name = 'audit' AND j.journal_event_body_json LIKE '%' || c.search_catalogue_snapshot_id::text || '%');").Trim();
        Assert.Equal("0", orphans);
        Assert.Equal("1", fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_body_json LIKE '%{snapshot:D}%';").Trim());
        Assert.Equal("1", fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = 'SearchCatalogueRefused';").Trim());

        // The audit stream is delivered to the independent archive by the existing processor (durable, retried, hash-chained).
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        string pending;
        while ((pending = fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND archive_delivery_state <> 'Anchored';").Trim()) != "0" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        Assert.Equal("0", pending);
    }

    /// <summary>
    /// C-66 (owner's final AC-5 review): a ~200-record operational scope with realistic values, measured sequentially and
    /// under 4 and 8 concurrent verifications while other records are being edited. p50/p95/p99 per load level, written to
    /// artifacts/stage-b/c7-m1-benchmark-200.json. It fails only if a verification fails, never on a latency number.
    /// </summary>
    [Fact]
    public async Task BenchmarkA200RecordScopeSequentiallyAndUnderConcurrency()
    {
        var s = new StageBScenario(fixture, "c7-bench-200");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var created = new List<Guid>();
        for (var index = 0; index < 200; index++)
        {
            await s.WaitGateReadyAsync();
            var values = StageBScenario.AccusedValues("Given" + index.ToString(CultureInfo.InvariantCulture), new string('F', 8 + (index % 40)));
            values["case_status"] = index % 3 == 0 ? "closed" : "open";
            created.Add(await CreateWithRetryAsync(s, $"c7-bench200-{s.OrganizationId:N}-{index}", TestContext.Current.CancellationToken, values));
        }

        foreach (var id in created)
        {
            Assert.True((await s.WaitProjectionAsync(s.Clerk, id)).IsSuccess);
        }

        // Background workload on another form: address records of a few parents, edited while the scopes are verified.
        var addresses = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            addresses.Add(await s.CreateAddressAsync(s.Clerk, created[i], $"{i} Bench St"));
        }

        await s.WaitGateReadyAsync();
        var levels = new List<object>();
        foreach (var (concurrency, rounds) in new[] { (1, 20), (4, 6), (8, 4) })
        {
            using var stop = new CancellationTokenSource();
            var background = Task.Run(async () =>
            {
                var revision = new Dictionary<Guid, long>();
                var n = 0;
                while (!stop.IsCancellationRequested)
                {
                    var address = addresses[n++ % addresses.Count];
                    try
                    {
                        var current = (await s.WaitProjectionAsync(s.Clerk, address)).Root.GetProperty("record").GetProperty("contentRevision").GetInt64();
                        await fixture.Api.PatchAsync($"/api/v1/form-records/{address:D}", s.Token(s.Clerk), new { expectedContentRevision = current, values = new Dictionary<string, string> { ["street_address"] = $"{n} Bench St" } });
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or HttpRequestException)
                    {
                        // background load only: a transient failure of an edit is not part of the measurement
                    }

                    await Task.Delay(250, TestContext.Current.CancellationToken);
                }
            }, TestContext.Current.CancellationToken);

            var server = new List<double>();
            var client = new List<double>();
            var failures = 0;
            var timeLimitRefusals = 0;
            for (var round = 0; round <= rounds; round++)
            {
                var results = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
                {
                    var watch = Stopwatch.StartNew();
                    var result = await VerifyAsync(s, s.Clerk);
                    watch.Stop();
                    return (result, watch.Elapsed.TotalMilliseconds);
                }));
                foreach (var (result, elapsed) in results)
                {
                    if (!result.IsSuccess)
                    {
                        // The D4 time budget (15 s) exceeded under load: a typed, non-partial refusal — counted and reported,
                        // never a truncated scope. Any other failure must be the typed, retryable snapshot/availability refusal.
                        if (result.ErrorCode == "SEARCH_VERIFICATION_LIMIT")
                        {
                            Assert.Equal("time", result.Root.GetProperty("limit_kind").GetString());
                            Assert.False(result.Root.TryGetProperty("verifiedRecordCount", out _));
                            timeLimitRefusals++;
                            continue;
                        }

                        Assert.True(result.ErrorCode is "SEARCH_SNAPSHOT_CHANGED" or "SEARCH_VERIFICATION_UNAVAILABLE", result.Body);
                        failures++;
                        continue;
                    }

                    Assert.Equal(200, result.Root.GetProperty("verifiedRecordCount").GetInt32());
                    if (round > 0)
                    {
                        server.Add(result.Root.GetProperty("elapsedMilliseconds").GetDouble());
                        client.Add(elapsed);
                    }
                }
            }

            await stop.CancelAsync();
            await background;
            if (concurrency == 1)
            {
                Assert.NotEmpty(server); // sequentially, a 200-record scope must verify within the budget
            }

            levels.Add(new { concurrency, rounds, attempts = (rounds + 1) * concurrency, verifications = server.Count, typedRetryableRefusals = failures, timeLimitRefusals, serverMilliseconds = server.Count > 0 ? Percentiles(server) : null, clientMilliseconds = client.Count > 0 ? Percentiles(client) : null });
        }

        var json = JsonSerializer.Serialize(new { measuredOn = DateTimeOffset.UtcNow, records = 200, valueProfile = "Accused form: 8 fields incl. encrypted names (name lengths 8..47), plaintext date/boolean/option/datetime/guid", backgroundWorkload = "address edits on another form every 250 ms", note = "engineering measurement; small samples; p99 of small n is near the maximum; not a capacity claim", levels }, Indented);
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        Console.WriteLine("C7-AC5-BENCHMARK-200 " + JsonSerializer.Serialize(levels));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "EnterpriseForms.All.slnx")))
        {
            root = root.Parent;
        }

        if (root is not null)
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "artifacts", "stage-b"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "artifacts", "stage-b", "c7-m1-benchmark-200.json"), json, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// PB-09 measurement of the M1 verification path (not a production capacity claim): scopes of 50, 150 and 480 records (near the 500 limit),
    /// repeated runs, server-reported elapsed time, Authority calls and verified bytes. Results are written to
    /// artifacts/stage-b/c7-m1-benchmark.json and the test log. It fails only if a run fails, never on a latency number.
    /// </summary>
    [Fact]
    public async Task BenchmarkTheVerifiedScopeAt50And150And480Records()
    {
        var report = new List<object>();
        foreach (var (size, runs) in new[] { (50, 15), (150, 12), (480, 8) })
        {
            var s = new StageBScenario(fixture, $"c7-bench-{size}");
            await s.BootstrapAsync();
            await s.BuildAccusedWorkspaceAsync();
            // Sequential creation, waiting for the gate before each one: a create retried after a partial failure cannot be
            // completed idempotently (pre-existing finding recorded in PHASE_C7_STATUS.md), so the benchmark avoids provoking it.
            var created = new List<Guid>();
            for (var index = 0; index < size; index++)
            {
                await s.WaitGateReadyAsync();
                created.Add(await CreateWithRetryAsync(s, $"c7-bench-{s.OrganizationId:N}-{index}", TestContext.Current.CancellationToken));
            }

            foreach (var id in created)
            {
                Assert.True((await s.WaitProjectionAsync(s.Clerk, id)).IsSuccess);
            }

            await s.WaitGateReadyAsync();

            var server = new List<double>();
            var client = new List<double>();
            JsonElement last = default;
            for (var run = 0; run <= runs; run++)
            {
                var watch = Stopwatch.StartNew();
                var result = await VerifyAsync(s, s.Clerk);
                watch.Stop();
                for (var retry = 0; retry < 5 && !result.IsSuccess && result.ErrorCode is "SEARCH_SNAPSHOT_CHANGED" or "SEARCH_VERIFICATION_UNAVAILABLE"; retry++)
                {
                    await Task.Delay(2000, TestContext.Current.CancellationToken); // archive/projection settling right after bulk creation
                    watch.Restart();
                    result = await VerifyAsync(s, s.Clerk);
                    watch.Stop();
                }

                Assert.True(result.IsSuccess, result.Body);
                Assert.True(result.Root.GetProperty("verifiedRecordCount").GetInt32() >= size, result.Body);
                if (run == 0)
                {
                    continue; // warm-up
                }

                server.Add(result.Root.GetProperty("elapsedMilliseconds").GetDouble());
                client.Add(watch.Elapsed.TotalMilliseconds);
                last = result.Root.Clone();
            }

            report.Add(new
            {
                records = size,
                runs,
                serverMilliseconds = Percentiles(server),
                clientMilliseconds = Percentiles(client),
                catalogueEntries = last.GetProperty("catalogueEntryCount").GetInt32(),
                recordsWithoutContent = last.GetProperty("recordsWithoutContent").GetInt32(),
                authorityCalls = last.GetProperty("authorityCalls").GetInt32(),
                verificationBatches = last.GetProperty("verificationBatches").GetInt32(),
                verifiedBytes = last.GetProperty("verifiedBytes").GetInt64(),
            });
            await WriteReportAsync(report);
        }

        await WriteReportAsync(report);

        // Leave the shared stack as found: let the archive processor deliver the benchmark's journal backlog before other tests run.
        var drained = Stopwatch.StartNew();
        while (drained.Elapsed < TimeSpan.FromMinutes(5) && fixture.Stack.AuthorityObserverSql("SELECT count(*) FROM security_journal_entries WHERE archive_delivery_state = 'Pending';").Trim() != "0")
        {
            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }
    }

    private static async Task WriteReportAsync(List<object> report)
    {
        var json = JsonSerializer.Serialize(new { measuredOn = DateTimeOffset.UtcNow, environment = "GitHub-hosted isolated Docker stack (compose.security-test)", note = "provisional engineering measurement; small sample sizes; p99 of n<=15 runs is the maximum", scenarios = report }, Indented);
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        Console.WriteLine("C7-M1-BENCHMARK " + JsonSerializer.Serialize(report));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "EnterpriseForms.All.slnx")))
        {
            root = root.Parent;
        }

        if (root is not null)
        {
            var directory = Path.Combine(root.FullName, "artifacts", "stage-b");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "c7-m1-benchmark.json"), json, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Record registration is optimistic on the organization's authorization revision; concurrent creations retry that conflict with the SAME idempotency key, so a create that committed but answered "not ready" is never registered twice.</summary>
    private async Task<Guid> CreateWithRetryAsync(StageBScenario s, string idempotencyKey, CancellationToken cancellationToken, Dictionary<string, string>? values = null)
    {
        var key = idempotencyKey;
        for (var attempt = 0; ; attempt++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/records", s.Token(s.Clerk), new { values = values ?? StageBScenario.AccusedValues(), ownerTeamId = (Guid?)null }, key);
            if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                return created.Root.GetProperty("formRecordId").GetGuid();
            }

            Assert.True(attempt < 150 && created.ErrorCode is "STALE_AUTHORIZATION_REVISION" or "AUTHORITY_NOT_READY" or "COMMAND_PENDING", "create failed: " + created.Body);
            if (created.ErrorCode == "STALE_AUTHORIZATION_REVISION")
            {
                // The registration was rejected (nothing registered): a fresh key starts a new attempt.
                key = $"{idempotencyKey}-{attempt}";
            }

            // Not ready / pending may have committed: the same key resolves to the same record.
            await Task.Delay(Random.Shared.Next(250, 1200), cancellationToken);
        }
    }

    private static object Percentiles(List<double> samples)
    {
        var sorted = samples.Order().ToList();
        double At(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
        return new { p50 = Math.Round(At(0.50), 1), p95 = Math.Round(At(0.95), 1), p99 = Math.Round(At(0.99), 1), max = Math.Round(sorted[^1], 1), samples = sorted.Count };
    }
}
