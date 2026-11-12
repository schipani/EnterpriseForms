using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// IX-1 (C-126) on the real security-test stack (business API, Authorization Authority, AuthorityDb, SpiceDB, Vault Transit):
/// the Authority-owned search index is derived inside the content-commit transaction, sealed ES256 by Vault with the pinned
/// synthetic test key, verified locally by the sweeps, protected by grants, forced RLS and the INV-R constraint trigger, and
/// rolled back with the head by a crash. Dark mode: no query path exists, and existing search is untouched. Synthetic data only.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed partial class SearchIndexFoundationTests(StageBFixture fixture)
{
    private sealed record Ws(StageBScenario S, Guid FormId, Guid Reference, Guid Count, Guid Status, Guid Open, Guid Closed, Guid Note, Guid Unapproved, Dictionary<Guid, Guid> Approvals);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Ws> WorkspaceAsync(string name, bool approve = true)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "IX-1 synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("IX-1 " + s.Name, s.Clerk);
        async Task<JsonElement> FieldAsync(string key, string label, string type, string classification = "Internal", string protection = "Standard", object? options = null)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = label, fieldDescription = string.Empty, fieldDataType = type, options, fieldClassification = classification, fieldProtectionMode = protection });
            Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
            return added.Root.Clone();
        }

        var reference = (await FieldAsync("case_reference", "Case reference", "String")).GetProperty("fieldDefinitionId").GetGuid();
        var count = (await FieldAsync("exhibit_count", "Exhibit count", "Integer")).GetProperty("fieldDefinitionId").GetGuid();
        var status = await FieldAsync("case_status", "Case status", "Option", options: new[] { new { optionKey = "open", optionDisplayText = "Open" }, new { optionKey = "closed", optionDisplayText = "Closed" } });
        var statusId = status.GetProperty("fieldDefinitionId").GetGuid();
        var options = status.GetProperty("options").EnumerateArray().ToDictionary(o => o.GetProperty("optionKey").GetString()!, o => o.GetProperty("fieldOptionId").GetGuid());
        var note = (await FieldAsync("case_note", "Case note", "String", "Confidential", "ApplicationEncrypted")).GetProperty("fieldDefinitionId").GetGuid();
        var unapproved = (await FieldAsync("free_text", "Free text", "String")).GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("IX-1", StageBScenario.Section("main", "Main",
            StageBScenario.Field(reference, "text", "Case reference", 2), StageBScenario.Field(count, "number", "Exhibit count", 2),
            $$"""{"kind":"field","fieldDefinitionId":"{{statusId:D}}","control":"select","label":"Case status","layout":{"columnSpan":2,"startOnNewRow":false},"optionsSnapshot":[{"fieldOptionId":"{{options["open"]:D}}","label":"Open","sortOrder":1},{"fieldOptionId":"{{options["closed"]:D}}","label":"Closed","sortOrder":2}]}""",
            StageBScenario.Field(note, "textarea", "Case note", 4), StageBScenario.Field(unapproved, "text", "Free text", 2))));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        var approvals = new Dictionary<Guid, Guid>();
        if (approve)
        {
            foreach (var (field, ops) in new[] { (reference, new[] { "equals", "startsWith" }), (count, new[] { "equals", "between" }), (statusId, new[] { "is" }) })
            {
                var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = ops, approvalReference = "IX-1", useDevelopmentStepUp = true });
                Assert.True(approved.IsSuccess, approved.Body);
            }

            foreach (var line in Sql($"SELECT field_definition_id || '|' || field_search_approval_id FROM field_search_approvals WHERE form_definition_id = '{formId:D}' AND approval_state_name = 'Active';").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                approvals[Guid.Parse(line.Split('|')[0])] = Guid.Parse(line.Split('|')[1]);
            }
        }

        return new Ws(s, formId, reference, count, statusId, options["open"], options["closed"], note, unapproved, approvals);
    }

    private static Dictionary<string, string> Values(string reference, int count, string status) => new(StringComparer.Ordinal)
    {
        ["case_reference"] = reference, ["exhibit_count"] = count.ToString(CultureInfo.InvariantCulture), ["case_status"] = status, ["case_note"] = "secret " + reference, ["free_text"] = "free " + reference,
    };

    private async Task<Guid> CreateAsync(Ws w, Dictionary<string, string> values, Guid? form = null, int attempts = 60)
    {
        var key = "ix1-" + Guid.CreateVersion7().ToString("N");
        for (var attempt = 0; ; attempt++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{form ?? w.FormId:D}/records", w.S.Token(w.S.Clerk), new { values }, key);
            if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                var id = created.Root.GetProperty("formRecordId").GetGuid();
                Assert.True((await w.S.WaitProjectionAsync(w.S.Clerk, id)).IsSuccess);
                return id;
            }

            Assert.True(attempt < attempts && created.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING", "create failed: " + created.Body);
            await Task.Delay(250 + Random.Shared.Next(500), Ct);
        }
    }

    private string Sql(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    private string Scalar(string sql) => Sql(sql).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    private string RuntimeRole() => Scalar("SELECT grantee FROM information_schema.role_table_grants WHERE table_schema = 'authority' AND table_name = 'security_journal_entries' AND privilege_type = 'INSERT' AND grantee NOT IN (SELECT rolname FROM pg_roles WHERE rolsuper) LIMIT 1;");

    private string Projection(Guid org) => Scalar($"SELECT projection_status_name || '|' || coalesce(projection_status_reason_code, '') || '|' || projection_content_integrity_revision || '|' || verification_suspended FROM authority_search.search_projection_states WHERE organization_id = '{org:D}';");

    private long ContentRevision(Guid org) => long.Parse(Scalar($"SELECT content_integrity_revision FROM organization_authorization_states WHERE organization_id = '{org:D}';"), CultureInfo.InvariantCulture);

    private static async Task<string> WaitAsync(Func<string> probe, Func<string, bool> done, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        string last;
        do
        {
            last = probe();
            if (done(last))
            {
                return last;
            }

            await Task.Delay(1000, Ct);
        }
        while (DateTime.UtcNow < deadline);
        return last;
    }

    /// <summary>IX-02/03/04/05, IX-14: derivation at the commit; approved top-level values only; encrypted and unapproved fields never indexed; seals verified by the sweep with the pinned key.</summary>
    [Fact]
    public async Task CommitsDeriveSealedEntriesThatTheSweepVerifiesWithThePinnedKey()
    {
        var w = await WorkspaceAsync("ix1-derive");
        var records = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            records.Add(await CreateAsync(w, Values($"  Ref-{i} Alpha ", 10 + i, i % 2 == 0 ? "open" : "closed")));
        }

        var org = w.S.OrganizationId;
        // IX-2: the running builder may already have attested the coverages Complete (every record was indexed inline); Complete
        // is reachable only with an attestation journal entry. Before IX-2 this asserted Building only.
        Assert.Equal(3, int.Parse(Scalar($"SELECT count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{org:D}' AND (coverage_state_name = 'Building' OR (coverage_state_name = 'Complete' AND coverage_attested_journal_entry_id IS NOT NULL));"), CultureInfo.InvariantCulture));
        Assert.Equal("3", Scalar($"SELECT count(*) FROM authority_search.search_record_entries e JOIN record_content_heads h ON h.organization_id = e.organization_id AND h.form_record_id = e.form_record_id AND h.committed_operation_id = e.indexed_operation_id AND h.committed_content_revision = e.indexed_content_revision AND h.committed_manifest_root = e.indexed_manifest_root WHERE e.organization_id = '{org:D}' AND octet_length(e.entry_seal_signature) = 64 AND e.seal_key_name = 'search-entry-seal-v1';"));
        Assert.Equal(["REF-0 ALPHA", "REF-1 ALPHA", "REF-2 ALPHA"], Sql($"SELECT normalized_string_value FROM authority_search.search_string_postings WHERE organization_id = '{org:D}' ORDER BY 1;").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("3", Scalar($"SELECT count(*) FROM authority_search.search_integer_postings WHERE organization_id = '{org:D}';"));
        Assert.Equal("3", Scalar($"SELECT count(*) FROM authority_search.search_option_postings WHERE organization_id = '{org:D}' AND field_option_id IN ('{w.Open:D}','{w.Closed:D}');"));
        foreach (var excluded in new[] { w.Note, w.Unapproved })
        {
            Assert.Equal("0", Scalar($"SELECT (SELECT count(*) FROM authority_search.search_string_postings WHERE field_definition_id = '{excluded:D}') + (SELECT count(*) FROM authority_search.search_record_field_anomalies WHERE field_definition_id = '{excluded:D}');"));
        }

        Assert.Equal(ContentRevision(org).ToString(CultureInfo.InvariantCulture), Projection(org).Split('|')[2]);
        Assert.StartsWith("Active||", Projection(org), StringComparison.Ordinal);

        // S-B of the running Authority: every Vault-produced seal verifies locally against the pinned public key; S-A passes.
        var verified = await WaitAsync(() => Scalar($"SELECT count(*) FILTER (WHERE last_seal_attempt_outcome_code = 'VALID') || '|' || count(*) FROM authority_search.search_record_entries WHERE organization_id = '{org:D}';"), v => v == "3|3");
        Assert.Equal("3|3", verified);
        Assert.Equal("t", await WaitAsync(() => Scalar($"SELECT (last_successful_cheap_pass_on IS NOT NULL)::text FROM authority_search.search_projection_states WHERE organization_id = '{org:D}';").Trim()[..1], v => v == "t"));
        Assert.EndsWith("|false", Projection(org), StringComparison.Ordinal);
    }

    /// <summary>IX-34 (seal side), OR-1: an Authority-domain rewrite of a posting fails the seal sweep and quarantines the index only; the organization and record reads are unaffected.</summary>
    [Fact]
    public async Task AnAuthorityDomainPostingRewriteQuarantinesOnlyTheIndex()
    {
        var w = await WorkspaceAsync("ix1-tamper");
        var record = await CreateAsync(w, Values("Tamper One", 5, "open"));
        var org = w.S.OrganizationId;
        fixture.Stack.AuthorityAdministratorSql($"UPDATE authority_search.search_string_postings SET normalized_string_value = 'FORGED MATCH' WHERE organization_id = '{org:D}';");

        var state = await WaitAsync(() => Projection(org), p => p.StartsWith("Quarantined", StringComparison.Ordinal));
        Assert.StartsWith("Quarantined|POSTING_VALUE_MISMATCH|", state, StringComparison.Ordinal);
        Assert.Equal("false", Scalar($"SELECT integrity_quarantine_enabled::text FROM organization_authorization_states WHERE organization_id = '{org:D}';"));
        Assert.True((await w.S.ProjectionAsync(w.S.Clerk, record)).IsSuccess); // record reads continue (S1 untouched)
        Assert.Equal("1", Scalar($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{org:D}' AND journal_event_kind = 'SearchIndexQuarantined';"));
    }

    /// <summary>IX-42, IX-01/02, R1 §B.6: the runtime role inserts and deletes derived rows but cannot rewrite them; the reader is read-only; RLS isolates organizations.</summary>
    [Fact]
    public async Task GrantsAndForcedRlsConfineBothRoles()
    {
        var a = await WorkspaceAsync("ix1-grants-a");
        var b = await WorkspaceAsync("ix1-grants-b");
        await CreateAsync(a, Values("Alpha Grant", 1, "open"));
        await CreateAsync(b, Values("Beta Grant", 2, "closed"));
        var role = RuntimeRole();
        Assert.False(string.IsNullOrEmpty(role));
        string As(string asRole, Guid? org, string sql) => fixture.Stack.AuthorityAdministratorSql(
            $"BEGIN; SET LOCAL ROLE {asRole};{(org is { } o ? $" SELECT set_config('authority_search.organization_id', '{o:D}', true);" : string.Empty)} {sql}; COMMIT;", throwOnError: false);

        Assert.Contains("permission denied", As(role, a.S.OrganizationId, "UPDATE authority_search.search_string_postings SET normalized_string_value = normalized_string_value WHERE false"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", As(role, a.S.OrganizationId, "UPDATE authority_search.search_record_entries SET entry_seal_signature = entry_seal_signature WHERE false"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permission denied", As(role, a.S.OrganizationId, "UPDATE authority_search.search_record_entries SET last_seal_attempt_outcome_code = last_seal_attempt_outcome_code WHERE false"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", As(role, a.S.OrganizationId, "DELETE FROM authority_search.search_projection_states WHERE false"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", As(role, a.S.OrganizationId, "DELETE FROM authority.security_journal_entries WHERE false"), StringComparison.OrdinalIgnoreCase);

        // Forced RLS: the bound organization's rows only; nothing without a binding; a write for another organization is refused.
        Assert.StartsWith("1", As(role, a.S.OrganizationId, $"SELECT count(*) FROM authority_search.search_string_postings").Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(l => l.Trim().Length > 0 && char.IsDigit(l.Trim()[0])).Trim(), StringComparison.Ordinal);
        Assert.Contains("\n0", "\n" + As(role, a.S.OrganizationId, $"SELECT count(*) FROM authority_search.search_string_postings WHERE organization_id = '{b.S.OrganizationId:D}'"), StringComparison.Ordinal);
        Assert.Contains("\n0", "\n" + As(role, null, "SELECT count(*) FROM authority_search.search_string_postings"), StringComparison.Ordinal);
        Assert.Contains("row-level security", As(role, a.S.OrganizationId, $"INSERT INTO authority_search.search_form_states (search_form_state_id, organization_id, form_definition_id, form_content_revision, form_state_modified_on) VALUES (gen_random_uuid(), '{a.S.OrganizationId:D}', gen_random_uuid(), 0, now()); INSERT INTO authority_search.search_record_field_anomalies (search_record_field_anomaly_id, organization_id, form_definition_id, form_record_id, field_definition_id, indexed_operation_id, anomaly_kind_name) VALUES (gen_random_uuid(), '{b.S.OrganizationId:D}', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'EncryptedValuePresent')"), StringComparison.OrdinalIgnoreCase);

        // The search reader: SELECT only, read-only by default.
        Assert.Contains("\n0", "\n" + As("authority_search_reader", a.S.OrganizationId, $"SELECT count(*) FROM authority_search.search_string_postings WHERE organization_id = '{b.S.OrganizationId:D}'"), StringComparison.Ordinal);
        var readerWrite = As("authority_search_reader", a.S.OrganizationId, "DELETE FROM authority_search.search_string_postings WHERE false");
        Assert.True(readerWrite.Contains("permission denied", StringComparison.OrdinalIgnoreCase) || readerWrite.Contains("read-only", StringComparison.OrdinalIgnoreCase), readerWrite);
        Assert.Equal("f", Scalar("SELECT rolcanlogin::text FROM pg_roles WHERE rolname = 'authority_search_reader';")[..1]);
        Assert.Equal("{default_transaction_read_only=on}", Scalar("SELECT coalesce(array_agg(c)::text, '') FROM pg_db_role_setting s JOIN pg_roles r ON r.oid = s.setrole, unnest(s.setconfig) c WHERE r.rolname = 'authority_search_reader' AND c LIKE 'default_transaction%';"));
        Assert.Contains("log_parameter_max_length=0", Sql($"SELECT array_to_string(setconfig, ',') FROM pg_db_role_setting s JOIN pg_roles r ON r.oid = s.setrole WHERE r.rolname = '{role}';"), StringComparison.Ordinal);
    }

    /// <summary>IX-74 (OR-2, INV-R): the database refuses, at commit, a content-revision change that leaves the projection behind; synchronized changes commit.</summary>
    [Fact]
    public async Task TheDatabaseRefusesAnUnsynchronizedContentRevisionAtCommit()
    {
        var w = await WorkspaceAsync("ix1-inv-r");
        await CreateAsync(w, Values("Invariant", 3, "open"));
        var org = w.S.OrganizationId;
        var role = RuntimeRole();
        var before = ContentRevision(org);

        var refused = fixture.Stack.AuthorityAdministratorSql($"BEGIN; SET LOCAL ROLE {role}; UPDATE authority.organization_authorization_states SET content_integrity_revision = content_integrity_revision + 1 WHERE organization_id = '{org:D}'; COMMIT;", throwOnError: false);
        Assert.Contains("INV-R", refused, StringComparison.Ordinal);
        Assert.Equal(before, ContentRevision(org));

        var accepted = fixture.Stack.AuthorityAdministratorSql($"BEGIN; SET LOCAL ROLE {role}; UPDATE authority.organization_authorization_states SET content_integrity_revision = content_integrity_revision + 1 WHERE organization_id = '{org:D}'; UPDATE authority_search.search_projection_states SET projection_content_integrity_revision = projection_content_integrity_revision + 1 WHERE organization_id = '{org:D}'; COMMIT;", throwOnError: false);
        Assert.DoesNotContain("ERROR", accepted, StringComparison.Ordinal);
        Assert.Equal(before + 1, ContentRevision(org));
        Assert.Equal((before + 1).ToString(CultureInfo.InvariantCulture), Projection(org).Split('|')[2]);
    }

    /// <summary>IX-64 / IX-66 (OR-3): a crash after derivation, before the commit, rolls the index back with the head; a later commit derives again.</summary>
    [Fact]
    public async Task ACrashAfterDerivationRollsTheIndexBackWithTheHead()
    {
        var w = await WorkspaceAsync("ix1-crash");
        var record = await CreateAsync(w, Values("Before Crash", 1, "open"));
        var org = w.S.OrganizationId;
        var headBefore = AuthorityOracle.Head(fixture.Stack, record);
        const string Marker = "FAULT-INJECTION: terminating process at 'CrashAfterSearchIndexDerivation'";
        var crashesBefore = Count(fixture.Stack.Logs("authority", 5000), Marker);
        var arm = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "CrashAfterSearchIndexDerivation" });
        Assert.True(arm.IsSuccess, arm.Body);
        try
        {
            var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = headBefore.ContentRevision, values = new Dictionary<string, string>(StringComparer.Ordinal) { ["case_reference"] = "After Crash" } });
            _ = edit; // the business outcome of the interrupted call is not asserted; the Authority-side invariant below is
        }
        finally
        {
            await WaitAsync(() => fixture.Stack.Logs("authority", 5000), l => Count(l, Marker) > crashesBefore, 60);
            await WaitAuthorityAsync(w.S);
            await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "None" });
        }

        Assert.Equal(crashesBefore + 1, Count(fixture.Stack.Logs("authority", 5000), Marker));
        await Task.Delay(TimeSpan.FromSeconds(10), Ct); // let any business-side retry of the interrupted commit settle

        // The invariant across the crash: the index is always bound to the head the Authority actually accepted. Either the
        // crashed transaction rolled back completely (head and index unchanged) or a later retry committed both together.
        string Bound() => Scalar($"SELECT h.committed_operation_id || '|' || e.indexed_operation_id || '|' || p.normalized_string_value FROM record_content_heads h JOIN authority_search.search_record_entries e ON e.organization_id = h.organization_id AND e.form_record_id = h.form_record_id JOIN authority_search.search_string_postings p ON p.organization_id = e.organization_id AND p.form_record_id = e.form_record_id AND p.indexed_operation_id = e.indexed_operation_id WHERE h.form_record_id = '{record:D}';");
        var bound = Bound().Split('|');
        Assert.Equal(bound[0], bound[1]);
        Assert.Equal(bound[0] == headBefore.OperationId!.Value.ToString("D") ? "BEFORE CRASH" : "AFTER CRASH", bound[2]);
        Assert.Equal("0", Scalar($"SELECT count(*) FROM content_change_operations o WHERE o.form_record_id = '{record:D}' AND o.operation_state_name = 'Committed' AND NOT EXISTS (SELECT 1 FROM record_manifest_commits c WHERE c.committed_operation_id = o.content_change_operation_id);"));
        Assert.Equal(ContentRevision(org).ToString(CultureInfo.InvariantCulture), Projection(org).Split('|')[2]);

        var head = AuthorityOracle.Head(fixture.Stack, record);
        var retry = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = head.ContentRevision, values = new Dictionary<string, string>(StringComparer.Ordinal) { ["case_reference"] = "After Recovery" } });
        for (var attempt = 0; !retry.IsSuccess && attempt < 30 && retry.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING"; attempt++)
        {
            await Task.Delay(1000, Ct);
            retry = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = head.ContentRevision, values = new Dictionary<string, string>(StringComparer.Ordinal) { ["case_reference"] = "After Recovery" } });
        }

        Assert.True(retry.IsSuccess, retry.Body);
        var after = Bound().Split('|');
        Assert.Equal(after[0], after[1]);
        Assert.Equal("AFTER RECOVERY", after[2]);
        Assert.Equal(AuthorityOracle.Head(fixture.Stack, record).OperationId!.Value.ToString("D"), after[0]);
    }

    /// <summary>OR-2 (W2), IX-63/IX-72: a verified read that quarantines a tampered record advances both revisions together; the index is neither quarantined nor out of step.</summary>
    [Fact]
    public async Task AVerifiedReadQuarantineKeepsTheProjectionSynchronized()
    {
        var w = await WorkspaceAsync("ix1-w2");
        var record = await CreateAsync(w, Values("Read Quarantine", 4, "open"));
        var org = w.S.OrganizationId;
        var before = ContentRevision(org);
        fixture.Stack.AttackerSql($"UPDATE field_string_values SET string_value = 'tampered by the business DBA' WHERE form_record_id = '{record:D}' AND field_definition_id = '{w.Reference:D}';");
        var read = await w.S.ProjectionAsync(w.S.Clerk, record);
        Assert.False(read.IsSuccess, read.Body);
        Assert.Equal("true", Scalar($"SELECT content_head_quarantined::text FROM record_content_heads WHERE form_record_id = '{record:D}';"));
        Assert.True(ContentRevision(org) > before);
        Assert.Equal(ContentRevision(org).ToString(CultureInfo.InvariantCulture), Projection(org).Split('|')[2]);
        Assert.StartsWith("Active|", Projection(org), StringComparison.Ordinal);
        await Task.Delay(TimeSpan.FromSeconds(15), Ct); // at least one more S-A pass
        Assert.StartsWith("Active|", Projection(org), StringComparison.Ordinal);
    }

    /// <summary>IX-06 (C.2): withdrawing an approval purges the whole form's index in the command transaction and returns sibling coverages to Building.</summary>
    [Fact]
    public async Task WithdrawalPurgesTheFormIndexInTheCommandTransaction()
    {
        var w = await WorkspaceAsync("ix1-withdraw");
        await CreateAsync(w, Values("Withdraw One", 1, "open"));
        await CreateAsync(w, Values("Withdraw Two", 2, "closed"));
        var org = w.S.OrganizationId;
        Assert.Equal("2", Scalar($"SELECT count(*) FROM authority_search.search_record_entries WHERE organization_id = '{org:D}';"));
        var before = Scalar("SELECT now()::text;");
        var withdrawn = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.Reference:D}/search-approval/withdraw", w.S.Token(w.S.Administrator), new { reason = "IX-1 withdrawal test" });
        Assert.True(withdrawn.IsSuccess, withdrawn.Body);
        // IX-2: the builder may re-derive the form for its remaining coverages within seconds, so the purge is proven by what can
        // survive it: no row of the withdrawn approval or field, no string posting at all (the withdrawn field is the only string
        // field approved), and only entries written after the withdrawal (re-derived from verified bytes). Before IX-2 this asserted
        // an empty index.
        Assert.Equal("0|0|0", Scalar($"SELECT (SELECT count(*) FROM authority_search.search_record_entries WHERE organization_id = '{org:D}' AND (entry_written_on <= '{before}'::timestamptz OR '{w.Approvals[w.Reference]:D}' = ANY (derived_field_search_approval_ids))) || '|' || (SELECT count(*) FROM authority_search.search_string_postings WHERE organization_id = '{org:D}') || '|' || (SELECT count(*) FROM authority_search.search_integer_postings WHERE organization_id = '{org:D}' AND field_search_approval_id = '{w.Approvals[w.Reference]:D}');"));
        Assert.Equal("Withdrawn", Scalar($"SELECT coverage_state_name FROM authority_search.search_field_coverages WHERE field_search_approval_id = '{w.Approvals[w.Reference]:D}';"));
        Assert.Equal("2", Scalar($"SELECT count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{org:D}' AND (coverage_state_name = 'Building' OR (coverage_state_name = 'Complete' AND coverage_attested_journal_entry_id IS NOT NULL AND coverage_completed_on > '{before}'::timestamptz));"));
    }

    /// <summary>
    /// Concurrency: twelve records of one organization are edited in parallel, so twelve content commits (each deriving and
    /// sealing its index) contend for the organization lock. Every commit is indexed at its new head, and the revisions stay
    /// synchronized. (Record registration is a security command and is created sequentially: parallel registrations race on
    /// the authorization revision by design, which is unrelated to the index.)
    /// </summary>
    [Fact]
    public async Task ConcurrentCommitsKeepTheIndexAndRevisionsSynchronized()
    {
        var w = await WorkspaceAsync("ix1-concurrent");
        var records = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            records.Add(await CreateAsync(w, Values($"Parallel {i}", i, "open")));
        }

        var heads = records.ToDictionary(r => r, r => AuthorityOracle.Head(fixture.Stack, r));
        var edits = await Task.WhenAll(records.Select(async (r, i) =>
        {
            for (var attempt = 0; ; attempt++)
            {
                var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{r:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = heads[r].ContentRevision, values = new Dictionary<string, string>(StringComparer.Ordinal) { ["case_reference"] = $"Edited {i}" } });
                if (edit.IsSuccess || attempt >= 30 || edit.ErrorCode is not ("AUTHORITY_NOT_READY" or "COMMAND_PENDING"))
                {
                    return edit;
                }

                await Task.Delay(500, Ct);
            }
        }));
        Assert.All(edits, e => Assert.True(e.IsSuccess, e.Body));
        var org = w.S.OrganizationId;
        Assert.Equal("12", Scalar($"SELECT count(*) FROM authority_search.search_record_entries e JOIN record_content_heads h ON h.form_record_id = e.form_record_id AND h.committed_operation_id = e.indexed_operation_id AND h.committed_content_revision = 2 WHERE e.organization_id = '{org:D}';"));
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"EDITED {i}").Order(StringComparer.Ordinal), Sql($"SELECT normalized_string_value FROM authority_search.search_string_postings WHERE organization_id = '{org:D}';").Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        Assert.Equal(ContentRevision(org).ToString(CultureInfo.InvariantCulture), Projection(org).Split('|')[2]);
        Assert.Equal("12|12", await WaitAsync(() => Scalar($"SELECT count(*) FILTER (WHERE last_seal_attempt_outcome_code = 'VALID' AND last_seal_verified_on > entry_written_on) || '|' || count(*) FROM authority_search.search_record_entries WHERE organization_id = '{org:D}';"), v => v == "12|12"));
    }

    /// <summary>
    /// IX-1 measurement (PB-09 commit p95 delta gate, IX-1 exit): the Authority-side content-commit duration (Testing-only log line
    /// IX1-COMMIT-TIMING, endpoint timing around ContentService.CommitAsync) for records of a form with search approvals
    /// (derivation + Vault ES256 seal + index writes + the extra flush inside the commit) and of an identical form without.
    /// Written to artifacts/ix-1/commit-latency.json. Local Docker and Vault Transit only; not Azure, not Managed HSM.
    /// </summary>
    [Fact]
    public async Task MeasureCommitLatencyWithAndWithoutTheIndex()
    {
        var indexed = await WorkspaceAsync("ix1-latency-indexed");
        var plain = await WorkspaceAsync("ix1-latency-plain", approve: false);
        async Task<List<Guid>> CreateManyAsync(Ws w)
        {
            var records = new List<Guid>();
            for (var i = 0; i < 40; i++)
            {
                records.Add(await CreateAsync(w, Values($"Latency {i}", i, "open")));
            }

            return records;
        }

        var indexedRecords = await CreateManyAsync(indexed);
        var plainRecords = await CreateManyAsync(plain);
        var logs = fixture.Stack.Logs("authority", 50000);
        (List<double> Timings, List<double> Commands) Timings(IEnumerable<Guid> records)
        {
            var wanted = records.Skip(5).Select(r => r.ToString("D")).ToHashSet(StringComparer.Ordinal);
            var values = new List<double>();
            var commands = new List<double>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(logs, @"IX1-COMMIT-TIMING record=([0-9a-f-]{36}) elapsed_ms=([0-9.,]+) db_commands=([0-9]+)"))
            {
                if (wanted.Contains(m.Groups[1].Value))
                {
                    values.Add(double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture));
                    commands.Add(double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
                }
            }

            return (values.Order().ToList(), commands.Order().ToList());
        }

        var (withIndex, indexedCommands) = Timings(indexedRecords);
        var (without, plainCommands) = Timings(plainRecords);
        Assert.True(withIndex.Count >= 30 && without.Count >= 30, $"timing lines found: {withIndex.Count} indexed, {without.Count} plain");
        static double Q(List<double> s, double p) => s[Math.Min(s.Count - 1, (int)(p * s.Count))];
        var wantedIndexed = indexedRecords.Skip(5).Select(r => r.ToString("D")).ToHashSet(StringComparer.Ordinal);
        var stages = new Dictionary<string, List<double>>(StringComparer.Ordinal) { ["reads"] = [], ["derive"] = [], ["sign"] = [], ["writes"] = [] };
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(logs, @"IX1-DERIVE-TIMING record=([0-9a-f-]{36}) reads_ms=([0-9.,]+) derive_ms=([0-9.,]+) sign_ms=([0-9.,]+) writes_ms=([0-9.,]+)"))
        {
            if (wantedIndexed.Contains(m.Groups[1].Value))
            {
                var i = 2;
                foreach (var name in new[] { "reads", "derive", "sign", "writes" })
                {
                    stages[name].Add(double.Parse(m.Groups[i++].Value.Replace(',', '.'), CultureInfo.InvariantCulture));
                }
            }
        }

        foreach (var list in stages.Values)
        {
            list.Sort();
        }

        Assert.True(stages["sign"].Count >= 30, $"stage timing lines found: {stages["sign"].Count}");
        var stageReport = stages.ToDictionary(kv => kv.Key, kv => new { p50_ms = Math.Round(Q(kv.Value, 0.50), 2), p95_ms = Math.Round(Q(kv.Value, 0.95), 2), p99_ms = Math.Round(Q(kv.Value, 0.99), 2) }, StringComparer.Ordinal);
        var report = new
        {
            environment = "local Docker security-test stack, Vault Transit dev ecdsa-p256; Authority-side ContentService.CommitAsync duration from the Testing-only IX1-COMMIT-TIMING log; sequential record creation, first 5 per form excluded",
            indexed_samples = withIndex.Count, plain_samples = without.Count,
            indexed_p50_ms = Math.Round(Q(withIndex, 0.50), 2), indexed_p95_ms = Math.Round(Q(withIndex, 0.95), 2),
            plain_p50_ms = Math.Round(Q(without, 0.50), 2), plain_p95_ms = Math.Round(Q(without, 0.95), 2),
            delta_p50_ms = Math.Round(Q(withIndex, 0.50) - Q(without, 0.50), 2), delta_p95_ms = Math.Round(Q(withIndex, 0.95) - Q(without, 0.95), 2),
            indexed_p99_ms = Math.Round(Q(withIndex, 0.99), 2), plain_p99_ms = Math.Round(Q(without, 0.99), 2),
            delta_p99_ms = Math.Round(Q(withIndex, 0.99) - Q(without, 0.99), 2),
            p99_note = "with 35 samples p99 is the maximum sample",
            indexed_db_commands = new { min = indexedCommands.Min(), median = Q(indexedCommands, 0.50), max = indexedCommands.Max() },
            plain_db_commands = new { min = plainCommands.Min(), median = Q(plainCommands, 0.50), max = plainCommands.Max() },
            gate_p95_delta_ms = 20,
            indexed_stage_breakdown = stageReport,
            stage_note = "stages are inside the index derivation only; the commit's extra flush (D-IX1-8), the projection-state read and the deferred INV-R trigger are outside them",
            measured_on = DateTimeOffset.UtcNow,
        };
        var directory = Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-1");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "commit-latency.json"), JsonSerializer.Serialize(report, Indented), Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(report));
    }

    /// <summary>After a crash fault the Authority restarts (restart: unless-stopped): wait until it answers and the gate is ready.</summary>
    private static async Task WaitAuthorityAsync(StageBScenario s)
    {
        var deadline = DateTime.UtcNow.AddSeconds(240);
        while (true)
        {
            try
            {
                await s.WaitGateReadyAsync(TimeSpan.FromSeconds(30));
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000, Ct);
            }
        }
    }

    private static int Count(string text, string marker)
    {
        var count = 0;
        for (var index = text.IndexOf(marker, StringComparison.Ordinal); index >= 0; index = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
